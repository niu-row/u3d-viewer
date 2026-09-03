#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <algorithm>
#include <cstdint>
#include <cstring>
#include <iterator>
#include <mutex>
#include <new>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

#define U3DVIEWER_EXPORT extern "C" __declspec(dllexport)

namespace
{
    constexpr wchar_t kSceneHostClass[] = L"U3DViewer.SceneHost";
    constexpr UINT kSceneHostStyle = WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS | WS_CLIPCHILDREN;

    enum class PresenterInitStage : int
    {
        None = 0,
        FindAdapter = 1,
        CreateDevice = 2,
        QueryDevice1 = 3,
        OpenSharedResource = 4,
        QueryKeyedMutex = 5,
        CreateShaderResourceView = 6,
        CreateShaders = 7,
        QueryDxgiDevice = 8,
        GetAdapter = 9,
        GetFactory = 10,
        CreateSwapChain = 11,
        CreateRenderTarget = 12,
        Ready = 13
    };

    struct SceneInputState
    {
        int RightMouse;
        int Forward;
        int Right;
        int Up;
        int Shift;
        int FocusPressed;
        int MouseDeltaX;
        int MouseDeltaY;
        int WheelDelta;
    };

    struct Presenter
    {
        HWND Window = nullptr;
        ComPtr<ID3D11Device> Device;
        ComPtr<ID3D11DeviceContext> Context;
        ComPtr<IDXGISwapChain1> SwapChain;
        ComPtr<ID3D11RenderTargetView> RenderTargetView;
        ComPtr<ID3D11Texture2D> SharedTexture;
        ComPtr<IDXGIKeyedMutex> SharedMutex;
        ComPtr<ID3D11ShaderResourceView> SharedView;
        ComPtr<ID3D11VertexShader> VertexShader;
        ComPtr<ID3D11PixelShader> PixelShader;
        ComPtr<ID3D11SamplerState> Sampler;
        UINT Width = 0;
        UINT Height = 0;
        UINT SourceWidth = 0;
        UINT SourceHeight = 0;
        LUID AdapterLuid{};
        std::wstring AdapterName;
        PresenterInitStage InitStage = PresenterInitStage::None;
        bool LegacySwapChain = false;
    };

    struct HostState
    {
        std::mutex Mutex;
        Presenter* ScenePresenter = nullptr;
        HRESULT LastError = S_OK;
        PresenterInitStage LastInitStage = PresenterInitStage::None;
        LUID PresenterAdapterLuid{};
        std::wstring PresenterAdapterName;

        bool RightMouse = false;
        bool Keys[256]{};
        bool FocusPressed = false;
        LONG MouseDeltaX = 0;
        LONG MouseDeltaY = 0;
        int WheelDelta = 0;
        POINT SavedCursorPosition{};
        bool CursorHidden = false;
    };

    std::uint64_t PackLuid(const LUID& luid)
    {
        return (static_cast<std::uint64_t>(static_cast<std::uint32_t>(luid.HighPart)) << 32) |
               static_cast<std::uint64_t>(luid.LowPart);
    }

    LUID UnpackLuid(std::uint64_t value)
    {
        LUID luid{};
        luid.LowPart = static_cast<DWORD>(value & 0xFFFFFFFFull);
        luid.HighPart = static_cast<LONG>(static_cast<std::uint32_t>(value >> 32));
        return luid;
    }

    bool SameLuid(const LUID& left, const LUID& right)
    {
        return left.LowPart == right.LowPart && left.HighPart == right.HighPart;
    }

    DXGI_FORMAT ResolveShaderResourceFormat(DXGI_FORMAT format)
    {
        switch (format)
        {
            case DXGI_FORMAT_R8G8B8A8_TYPELESS:
                return DXGI_FORMAT_R8G8B8A8_UNORM;
            case DXGI_FORMAT_B8G8R8A8_TYPELESS:
                return DXGI_FORMAT_B8G8R8A8_UNORM;
            case DXGI_FORMAT_B8G8R8X8_TYPELESS:
                return DXGI_FORMAT_B8G8R8X8_UNORM;
            case DXGI_FORMAT_R10G10B10A2_TYPELESS:
                return DXGI_FORMAT_R10G10B10A2_UNORM;
            case DXGI_FORMAT_R16G16B16A16_TYPELESS:
                return DXGI_FORMAT_R16G16B16A16_FLOAT;
            default:
                return format;
        }
    }

    HostState* GetHostState(HWND window)
    {
        return reinterpret_cast<HostState*>(GetWindowLongPtrW(window, GWLP_USERDATA));
    }

    void RestoreCursor(HostState& state)
    {
        if (state.CursorHidden)
        {
            SetCursorPos(state.SavedCursorPosition.x, state.SavedCursorPosition.y);
            ShowCursor(TRUE);
            state.CursorHidden = false;
        }
    }

    void StopMouseLook(HWND window, HostState& state)
    {
        state.RightMouse = false;
        state.Keys['W'] = false;
        state.Keys['A'] = false;
        state.Keys['S'] = false;
        state.Keys['D'] = false;
        state.Keys['Q'] = false;
        state.Keys['E'] = false;
        state.Keys[VK_SHIFT] = false;
        state.Keys[VK_LSHIFT] = false;
        state.Keys[VK_RSHIFT] = false;
        if (GetCapture() == window)
        {
            ReleaseCapture();
        }
        RestoreCursor(state);
    }

    LRESULT CALLBACK SceneHostWndProc(HWND window, UINT message, WPARAM wParam, LPARAM lParam)
    {
        HostState* state = GetHostState(window);

        if (message == WM_NCCREATE)
        {
            const auto* create = reinterpret_cast<CREATESTRUCTW*>(lParam);
            state = static_cast<HostState*>(create->lpCreateParams);
            SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(state));
        }

        switch (message)
        {
            case WM_ERASEBKGND:
                return 1;

            case WM_RBUTTONDOWN:
                if (state != nullptr && !state->RightMouse)
                {
                    SetFocus(window);
                    SetCapture(window);
                    state->RightMouse = true;
                    GetCursorPos(&state->SavedCursorPosition);
                    ShowCursor(FALSE);
                    state->CursorHidden = true;
                }
                return 0;

            case WM_RBUTTONUP:
                if (state != nullptr)
                {
                    StopMouseLook(window, *state);
                }
                return 0;

            case WM_CAPTURECHANGED:
                if (state != nullptr && state->RightMouse)
                {
                    StopMouseLook(window, *state);
                }
                return 0;

            case WM_KILLFOCUS:
                if (state != nullptr)
                {
                    std::fill(std::begin(state->Keys), std::end(state->Keys), false);
                    if (state->RightMouse)
                    {
                        StopMouseLook(window, *state);
                    }
                }
                return 0;

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
                if (state != nullptr && wParam < 256)
                {
                    state->Keys[wParam] = true;
                    if (wParam == 'F' && (lParam & (1LL << 30)) == 0)
                    {
                        state->FocusPressed = true;
                    }
                }
                return 0;

            case WM_KEYUP:
            case WM_SYSKEYUP:
                if (state != nullptr && wParam < 256)
                {
                    state->Keys[wParam] = false;
                }
                return 0;

            case WM_MOUSEWHEEL:
                if (state != nullptr)
                {
                    state->WheelDelta += GET_WHEEL_DELTA_WPARAM(wParam) / WHEEL_DELTA;
                }
                return 0;

            case WM_INPUT:
                if (state != nullptr && state->RightMouse)
                {
                    UINT size = 0;
                    if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT, nullptr, &size, sizeof(RAWINPUTHEADER)) == 0 && size > 0)
                    {
                        std::vector<BYTE> bytes(size);
                        if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lParam), RID_INPUT, bytes.data(), &size, sizeof(RAWINPUTHEADER)) == size)
                        {
                            const auto* raw = reinterpret_cast<const RAWINPUT*>(bytes.data());
                            if (raw->header.dwType == RIM_TYPEMOUSE)
                            {
                                state->MouseDeltaX += raw->data.mouse.lLastX;
                                state->MouseDeltaY += raw->data.mouse.lLastY;
                            }
                        }
                    }
                }
                return 0;

            case WM_NCDESTROY:
                if (state != nullptr)
                {
                    if (state->RightMouse)
                    {
                        StopMouseLook(window, *state);
                    }
                    delete state->ScenePresenter;
                    state->ScenePresenter = nullptr;
                    delete state;
                    SetWindowLongPtrW(window, GWLP_USERDATA, 0);
                }
                break;
        }

        return DefWindowProcW(window, message, wParam, lParam);
    }

    bool EnsureSceneHostClass()
    {
        static const bool registered = []
        {
            WNDCLASSEXW cls{};
            cls.cbSize = sizeof(cls);
            cls.lpfnWndProc = SceneHostWndProc;
            cls.hInstance = GetModuleHandleW(nullptr);
            cls.hCursor = LoadCursorW(nullptr, IDC_ARROW);
            cls.lpszClassName = kSceneHostClass;

            const ATOM atom = RegisterClassExW(&cls);
            return atom != 0 || GetLastError() == ERROR_CLASS_ALREADY_EXISTS;
        }();
        return registered;
    }

    HRESULT FindAdapter(std::uint64_t requestedAdapterLuid, ComPtr<IDXGIAdapter1>& adapter)
    {
        ComPtr<IDXGIFactory1> factory;
        HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
        if (FAILED(hr))
        {
            return hr;
        }

        if (requestedAdapterLuid == 0)
        {
            return factory->EnumAdapters1(0, &adapter);
        }

        const LUID requested = UnpackLuid(requestedAdapterLuid);
        for (UINT index = 0;; ++index)
        {
            ComPtr<IDXGIAdapter1> candidate;
            hr = factory->EnumAdapters1(index, &candidate);
            if (hr == DXGI_ERROR_NOT_FOUND)
            {
                return DXGI_ERROR_NOT_FOUND;
            }
            if (FAILED(hr))
            {
                return hr;
            }

            DXGI_ADAPTER_DESC1 desc{};
            if (SUCCEEDED(candidate->GetDesc1(&desc)) && SameLuid(desc.AdapterLuid, requested))
            {
                adapter = candidate;
                return S_OK;
            }
        }
    }

    HRESULT CompileShader(const char* source, const char* entry, const char* target, ComPtr<ID3DBlob>& bytecode)
    {
        ComPtr<ID3DBlob> errors;
        return D3DCompile(
            source,
            std::strlen(source),
            "U3DViewer.ScenePresenter",
            nullptr,
            nullptr,
            entry,
            target,
            D3DCOMPILE_ENABLE_STRICTNESS | D3DCOMPILE_OPTIMIZATION_LEVEL3,
            0,
            &bytecode,
            &errors);
    }

    HRESULT CreateShaders(Presenter& presenter)
    {
        static constexpr char vertexSource[] = R"(
struct VSOut
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
};

VSOut main(uint vertexId : SV_VertexID)
{
    float2 position;
    if (vertexId == 0) position = float2(-1.0, -1.0);
    else if (vertexId == 1) position = float2(-1.0, 3.0);
    else position = float2(3.0, -1.0);

    VSOut output;
    output.Position = float4(position, 0.0, 1.0);
    output.UV = float2((position.x + 1.0) * 0.5, (1.0 - position.y) * 0.5);
    return output;
}
)";

        static constexpr char pixelSource[] = R"(
Texture2D SceneTexture : register(t0);
SamplerState SceneSampler : register(s0);

struct PSIn
{
    float4 Position : SV_POSITION;
    float2 UV : TEXCOORD0;
};

float4 main(PSIn input) : SV_TARGET
{
    return SceneTexture.Sample(SceneSampler, float2(input.UV.x, 1.0 - input.UV.y));
}
)";

        ComPtr<ID3DBlob> vertexBytecode;
        HRESULT hr = CompileShader(vertexSource, "main", "vs_5_0", vertexBytecode);
        if (FAILED(hr))
        {
            return hr;
        }

        hr = presenter.Device->CreateVertexShader(
            vertexBytecode->GetBufferPointer(),
            vertexBytecode->GetBufferSize(),
            nullptr,
            &presenter.VertexShader);
        if (FAILED(hr))
        {
            return hr;
        }

        ComPtr<ID3DBlob> pixelBytecode;
        hr = CompileShader(pixelSource, "main", "ps_5_0", pixelBytecode);
        if (FAILED(hr))
        {
            return hr;
        }

        hr = presenter.Device->CreatePixelShader(
            pixelBytecode->GetBufferPointer(),
            pixelBytecode->GetBufferSize(),
            nullptr,
            &presenter.PixelShader);
        if (FAILED(hr))
        {
            return hr;
        }

        D3D11_SAMPLER_DESC sampler{};
        sampler.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        sampler.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sampler.MaxLOD = D3D11_FLOAT32_MAX;
        return presenter.Device->CreateSamplerState(&sampler, &presenter.Sampler);
    }

    HRESULT CreateRenderTarget(Presenter& presenter, UINT width, UINT height)
    {
        width = std::max<UINT>(1, width);
        height = std::max<UINT>(1, height);

        presenter.Context->OMSetRenderTargets(0, nullptr, nullptr);
        presenter.RenderTargetView.Reset();

        if (presenter.Width != 0 && presenter.Height != 0 &&
            (presenter.Width != width || presenter.Height != height))
        {
            HRESULT hr = presenter.SwapChain->ResizeBuffers(0, width, height, DXGI_FORMAT_UNKNOWN, 0);
            if (FAILED(hr))
            {
                return hr;
            }
        }

        ComPtr<ID3D11Texture2D> backBuffer;
        HRESULT hr = presenter.SwapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer));
        if (FAILED(hr))
        {
            return hr;
        }

        hr = presenter.Device->CreateRenderTargetView(backBuffer.Get(), nullptr, &presenter.RenderTargetView);
        if (FAILED(hr))
        {
            return hr;
        }

        presenter.Width = width;
        presenter.Height = height;
        return S_OK;
    }

    D3D11_VIEWPORT BuildSceneViewport(const Presenter& presenter, UINT width, UINT height)
    {
        D3D11_VIEWPORT viewport{};
        viewport.MinDepth = 0.0f;
        viewport.MaxDepth = 1.0f;

        if (presenter.SourceWidth == 0 || presenter.SourceHeight == 0)
        {
            viewport.Width = static_cast<float>(width);
            viewport.Height = static_cast<float>(height);
            return viewport;
        }

        const float sourceAspect = static_cast<float>(presenter.SourceWidth) / static_cast<float>(presenter.SourceHeight);
        const float hostAspect = static_cast<float>(width) / static_cast<float>(height);
        if (hostAspect > sourceAspect)
        {
            viewport.Height = static_cast<float>(height);
            viewport.Width = viewport.Height * sourceAspect;
            viewport.TopLeftX = (static_cast<float>(width) - viewport.Width) * 0.5f;
        }
        else
        {
            viewport.Width = static_cast<float>(width);
            viewport.Height = viewport.Width / sourceAspect;
            viewport.TopLeftY = (static_cast<float>(height) - viewport.Height) * 0.5f;
        }

        return viewport;
    }

    HRESULT CreatePresenterSwapChain(Presenter& presenter, IDXGIFactory2* factory, HWND window, UINT width, UINT height)
    {
        DXGI_SWAP_CHAIN_DESC1 swapDesc{};
        swapDesc.Width = width;
        swapDesc.Height = height;
        swapDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        swapDesc.SampleDesc.Count = 1;
        swapDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swapDesc.BufferCount = 2;
        swapDesc.Scaling = DXGI_SCALING_STRETCH;
        swapDesc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        swapDesc.AlphaMode = DXGI_ALPHA_MODE_IGNORE;

        HRESULT hr = factory->CreateSwapChainForHwnd(
            presenter.Device.Get(),
            window,
            &swapDesc,
            nullptr,
            nullptr,
            &presenter.SwapChain);
        if (SUCCEEDED(hr))
        {
            presenter.LegacySwapChain = false;
            return S_OK;
        }

        if (hr != E_INVALIDARG)
        {
            return hr;
        }

        DXGI_SWAP_CHAIN_DESC legacyDesc{};
        legacyDesc.BufferDesc.Width = width;
        legacyDesc.BufferDesc.Height = height;
        legacyDesc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        legacyDesc.SampleDesc.Count = 1;
        legacyDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        legacyDesc.BufferCount = 1;
        legacyDesc.OutputWindow = window;
        legacyDesc.Windowed = TRUE;
        legacyDesc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        ComPtr<IDXGISwapChain> legacySwapChain;
        hr = factory->CreateSwapChain(presenter.Device.Get(), &legacyDesc, &legacySwapChain);
        if (FAILED(hr))
        {
            return hr;
        }

        hr = legacySwapChain.As(&presenter.SwapChain);
        if (FAILED(hr))
        {
            return hr;
        }

        presenter.LegacySwapChain = true;
        return S_OK;
    }

    HRESULT InitializePresenter(Presenter& presenter, HWND window, const wchar_t* sharedName, std::uint64_t adapterLuid)
    {
        presenter.Window = window;

        presenter.InitStage = PresenterInitStage::FindAdapter;
        ComPtr<IDXGIAdapter1> adapter;
        HRESULT hr = FindAdapter(adapterLuid, adapter);
        if (FAILED(hr))
        {
            return hr;
        }

        DXGI_ADAPTER_DESC1 adapterDesc{};
        if (SUCCEEDED(adapter->GetDesc1(&adapterDesc)))
        {
            presenter.AdapterLuid = adapterDesc.AdapterLuid;
            presenter.AdapterName = adapterDesc.Description;
        }

        presenter.InitStage = PresenterInitStage::CreateDevice;
        D3D_FEATURE_LEVEL featureLevel{};
        hr = D3D11CreateDevice(
            adapter.Get(),
            D3D_DRIVER_TYPE_UNKNOWN,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &presenter.Device,
            &featureLevel,
            &presenter.Context);
        if (FAILED(hr))
        {
            return hr;
        }

        presenter.InitStage = PresenterInitStage::QueryDevice1;
        ComPtr<ID3D11Device1> device1;
        hr = presenter.Device.As(&device1);
        if (FAILED(hr))
        {
            return hr;
        }

        presenter.InitStage = PresenterInitStage::OpenSharedResource;
        hr = device1->OpenSharedResourceByName(
            sharedName,
            DXGI_SHARED_RESOURCE_READ,
            IID_PPV_ARGS(&presenter.SharedTexture));
        if (FAILED(hr))
        {
            return hr;
        }

        D3D11_TEXTURE2D_DESC sourceDesc{};
        presenter.SharedTexture->GetDesc(&sourceDesc);
        presenter.SourceWidth = sourceDesc.Width;
        presenter.SourceHeight = sourceDesc.Height;

        presenter.InitStage = PresenterInitStage::QueryKeyedMutex;
        hr = presenter.SharedTexture.As(&presenter.SharedMutex);
        if (FAILED(hr))
        {
            return hr;
        }

        presenter.InitStage = PresenterInitStage::CreateShaderResourceView;
        D3D11_SHADER_RESOURCE_VIEW_DESC sharedViewDesc{};
        sharedViewDesc.Format = ResolveShaderResourceFormat(sourceDesc.Format);
        sharedViewDesc.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        sharedViewDesc.Texture2D.MostDetailedMip = 0;
        sharedViewDesc.Texture2D.MipLevels = 1;
        hr = presenter.Device->CreateShaderResourceView(
            presenter.SharedTexture.Get(),
            &sharedViewDesc,
            &presenter.SharedView);
        if (FAILED(hr))
        {
            return hr;
        }

        presenter.InitStage = PresenterInitStage::CreateShaders;
        hr = CreateShaders(presenter);
        if (FAILED(hr))
        {
            return hr;
        }

        presenter.InitStage = PresenterInitStage::QueryDxgiDevice;
        ComPtr<IDXGIDevice> dxgiDevice;
        hr = presenter.Device.As(&dxgiDevice);
        if (FAILED(hr))
        {
            return hr;
        }

        presenter.InitStage = PresenterInitStage::GetAdapter;
        ComPtr<IDXGIAdapter> dxgiAdapter;
        hr = dxgiDevice->GetAdapter(&dxgiAdapter);
        if (FAILED(hr))
        {
            return hr;
        }

        presenter.InitStage = PresenterInitStage::GetFactory;
        ComPtr<IDXGIFactory2> factory;
        hr = dxgiAdapter->GetParent(IID_PPV_ARGS(&factory));
        if (FAILED(hr))
        {
            return hr;
        }

        RECT client{};
        GetClientRect(window, &client);
        const UINT width = static_cast<UINT>(std::max<LONG>(1, client.right - client.left));
        const UINT height = static_cast<UINT>(std::max<LONG>(1, client.bottom - client.top));

        presenter.InitStage = PresenterInitStage::CreateSwapChain;
        hr = CreatePresenterSwapChain(presenter, factory.Get(), window, width, height);
        if (FAILED(hr))
        {
            return hr;
        }

        presenter.InitStage = PresenterInitStage::CreateRenderTarget;
        hr = CreateRenderTarget(presenter, width, height);
        if (FAILED(hr))
        {
            return hr;
        }

        presenter.InitStage = PresenterInitStage::Ready;
        return S_OK;
    }

    HRESULT PresentScene(Presenter& presenter)
    {
        RECT client{};
        if (!GetClientRect(presenter.Window, &client))
        {
            return HRESULT_FROM_WIN32(GetLastError());
        }

        const LONG rawWidth = client.right - client.left;
        const LONG rawHeight = client.bottom - client.top;
        if (rawWidth <= 0 || rawHeight <= 0)
        {
            return S_FALSE;
        }

        const UINT width = static_cast<UINT>(rawWidth);
        const UINT height = static_cast<UINT>(rawHeight);
        if (presenter.Width != width || presenter.Height != height || !presenter.RenderTargetView)
        {
            HRESULT hr = CreateRenderTarget(presenter, width, height);
            if (FAILED(hr))
            {
                return hr;
            }
        }

        HRESULT hr = presenter.SharedMutex->AcquireSync(1, 0);
        if (hr == WAIT_TIMEOUT)
        {
            return S_FALSE;
        }
        if (FAILED(hr))
        {
            return hr;
        }

        const float black[4] = { 0.f, 0.f, 0.f, 1.f };
        presenter.Context->ClearRenderTargetView(presenter.RenderTargetView.Get(), black);
        const D3D11_VIEWPORT viewport = BuildSceneViewport(presenter, width, height);

        ID3D11RenderTargetView* renderTarget = presenter.RenderTargetView.Get();
        presenter.Context->OMSetRenderTargets(1, &renderTarget, nullptr);
        presenter.Context->RSSetViewports(1, &viewport);
        presenter.Context->IASetInputLayout(nullptr);
        presenter.Context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        presenter.Context->VSSetShader(presenter.VertexShader.Get(), nullptr, 0);
        presenter.Context->PSSetShader(presenter.PixelShader.Get(), nullptr, 0);

        ID3D11ShaderResourceView* view = presenter.SharedView.Get();
        presenter.Context->PSSetShaderResources(0, 1, &view);
        ID3D11SamplerState* sampler = presenter.Sampler.Get();
        presenter.Context->PSSetSamplers(0, 1, &sampler);
        presenter.Context->Draw(3, 0);

        ID3D11ShaderResourceView* nullView = nullptr;
        presenter.Context->PSSetShaderResources(0, 1, &nullView);

        const HRESULT releaseHr = presenter.SharedMutex->ReleaseSync(0);
        if (FAILED(releaseHr))
        {
            return releaseHr;
        }

        const UINT presentFlags = presenter.LegacySwapChain ? 0u : DXGI_PRESENT_DO_NOT_WAIT;
        hr = presenter.SwapChain->Present(0, presentFlags);
        if (hr == DXGI_ERROR_WAS_STILL_DRAWING)
        {
            return S_FALSE;
        }
        return hr;
    }

    int CopyAdapterName(const std::wstring& name, wchar_t* buffer, int capacity)
    {
        if (buffer == nullptr || capacity <= 0 || name.empty())
        {
            if (buffer != nullptr && capacity > 0)
            {
                buffer[0] = L'\0';
            }
            return 0;
        }

        wcsncpy_s(buffer, static_cast<std::size_t>(capacity), name.c_str(), _TRUNCATE);
        return 1;
    }
}

U3DVIEWER_EXPORT HWND U3DViewer_CreateSceneHostWindow(HWND parent)
{
    if (parent == nullptr || !EnsureSceneHostClass())
    {
        return nullptr;
    }

    auto* state = new (std::nothrow) HostState();
    if (state == nullptr)
    {
        return nullptr;
    }

    HWND window = CreateWindowExW(
        0,
        kSceneHostClass,
        L"U3DViewer Scene View",
        kSceneHostStyle,
        0,
        0,
        1,
        1,
        parent,
        nullptr,
        GetModuleHandleW(nullptr),
        state);

    if (window == nullptr)
    {
        delete state;
        return nullptr;
    }

    RAWINPUTDEVICE rawMouse{};
    rawMouse.usUsagePage = 0x01;
    rawMouse.usUsage = 0x02;
    rawMouse.dwFlags = RIDEV_INPUTSINK;
    rawMouse.hwndTarget = window;
    RegisterRawInputDevices(&rawMouse, 1, sizeof(rawMouse));

    return window;
}

U3DVIEWER_EXPORT void U3DViewer_DestroySceneHostWindow(HWND window)
{
    if (window != nullptr && IsWindow(window))
    {
        DestroyWindow(window);
    }
}

U3DVIEWER_EXPORT int U3DViewer_OpenScenePresenter(HWND window, const wchar_t* sharedName, unsigned long long adapterLuid)
{
    if (window == nullptr || sharedName == nullptr || sharedName[0] == L'\0')
    {
        return 0;
    }

    HostState* state = GetHostState(window);
    if (state == nullptr)
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(state->Mutex);
    delete state->ScenePresenter;
    state->ScenePresenter = nullptr;
    state->PresenterAdapterLuid = {};
    state->PresenterAdapterName.clear();
    state->LastInitStage = PresenterInitStage::None;

    auto* presenter = new (std::nothrow) Presenter();
    if (presenter == nullptr)
    {
        state->LastError = E_OUTOFMEMORY;
        return 0;
    }

    const HRESULT hr = InitializePresenter(*presenter, window, sharedName, static_cast<std::uint64_t>(adapterLuid));
    state->PresenterAdapterLuid = presenter->AdapterLuid;
    state->PresenterAdapterName = presenter->AdapterName;
    state->LastInitStage = presenter->InitStage;
    state->LastError = hr;
    if (FAILED(hr))
    {
        delete presenter;
        return 0;
    }

    state->ScenePresenter = presenter;
    state->LastError = S_OK;
    return 1;
}

U3DVIEWER_EXPORT void U3DViewer_CloseScenePresenter(HWND window)
{
    HostState* state = window == nullptr ? nullptr : GetHostState(window);
    if (state == nullptr)
    {
        return;
    }

    std::lock_guard<std::mutex> lock(state->Mutex);
    delete state->ScenePresenter;
    state->ScenePresenter = nullptr;
    state->LastError = S_OK;
}

U3DVIEWER_EXPORT int U3DViewer_PresentScene(HWND window)
{
    HostState* state = window == nullptr ? nullptr : GetHostState(window);
    if (state == nullptr)
    {
        return -1;
    }

    std::lock_guard<std::mutex> lock(state->Mutex);
    if (state->ScenePresenter == nullptr)
    {
        state->LastError = E_POINTER;
        return -1;
    }

    const HRESULT hr = PresentScene(*state->ScenePresenter);
    state->LastError = hr;
    if (hr == S_FALSE || hr == DXGI_ERROR_WAS_STILL_DRAWING)
    {
        return 0;
    }
    return SUCCEEDED(hr) ? 1 : -1;
}

U3DVIEWER_EXPORT int U3DViewer_PollSceneInput(HWND window, SceneInputState* output)
{
    HostState* state = window == nullptr ? nullptr : GetHostState(window);
    if (state == nullptr || output == nullptr)
    {
        return 0;
    }

    output->RightMouse = state->RightMouse ? 1 : 0;
    output->Forward = (state->Keys['W'] ? 1 : 0) - (state->Keys['S'] ? 1 : 0);
    output->Right = (state->Keys['D'] ? 1 : 0) - (state->Keys['A'] ? 1 : 0);
    output->Up = (state->Keys['E'] ? 1 : 0) - (state->Keys['Q'] ? 1 : 0);
    output->Shift = (state->Keys[VK_SHIFT] || state->Keys[VK_LSHIFT] || state->Keys[VK_RSHIFT]) ? 1 : 0;
    output->FocusPressed = state->FocusPressed ? 1 : 0;
    output->MouseDeltaX = static_cast<int>(state->MouseDeltaX);
    output->MouseDeltaY = static_cast<int>(state->MouseDeltaY);
    output->WheelDelta = state->WheelDelta;

    state->FocusPressed = false;
    state->MouseDeltaX = 0;
    state->MouseDeltaY = 0;
    state->WheelDelta = 0;
    return 1;
}

U3DVIEWER_EXPORT int U3DViewer_GetScenePresenterLastError(HWND window)
{
    HostState* state = window == nullptr ? nullptr : GetHostState(window);
    return state == nullptr ? static_cast<int>(E_POINTER) : static_cast<int>(state->LastError);
}

U3DVIEWER_EXPORT int U3DViewer_GetScenePresenterInitStage(HWND window)
{
    HostState* state = window == nullptr ? nullptr : GetHostState(window);
    return state == nullptr ? static_cast<int>(PresenterInitStage::None) : static_cast<int>(state->LastInitStage);
}

U3DVIEWER_EXPORT unsigned long long U3DViewer_GetScenePresenterAdapterLuid(HWND window)
{
    HostState* state = window == nullptr ? nullptr : GetHostState(window);
    return state == nullptr ? 0ull : static_cast<unsigned long long>(PackLuid(state->PresenterAdapterLuid));
}

U3DVIEWER_EXPORT int U3DViewer_GetScenePresenterAdapterName(HWND window, wchar_t* buffer, int capacity)
{
    HostState* state = window == nullptr ? nullptr : GetHostState(window);
    return state == nullptr ? 0 : CopyAdapterName(state->PresenterAdapterName, buffer, capacity);
}
