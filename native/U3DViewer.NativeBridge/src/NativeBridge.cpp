#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstdint>
#include <mutex>
#include <string>

using Microsoft::WRL::ComPtr;

#if defined(_WIN32)
#define UNITY_INTERFACE_API __stdcall
#define U3DVIEWER_EXPORT extern "C" __declspec(dllexport)
#else
#define UNITY_INTERFACE_API
#define U3DVIEWER_EXPORT extern "C"
#endif

namespace
{
    constexpr int kNativeBridgeAbiVersion = 3;
    constexpr int kCopySceneTextureEvent = 1;

    std::mutex g_mutex;

    // Writer state: used inside the target Unity process.
    ComPtr<ID3D11Texture2D> g_sourceTexture;
    ComPtr<ID3D11Texture2D> g_sharedTexture;
    ComPtr<ID3D11Device> g_device;
    ComPtr<ID3D11DeviceContext> g_context;
    ComPtr<IDXGIKeyedMutex> g_sharedMutex;
    HANDLE g_sharedHandle = nullptr;
    std::wstring g_sharedName;
    LUID g_sourceAdapterLuid{};
    std::wstring g_sourceAdapterName;
    bool g_writerReady = false;

    HRESULT g_lastError = S_OK;

    std::uint64_t PackLuid(const LUID& luid)
    {
        return (static_cast<std::uint64_t>(static_cast<std::uint32_t>(luid.HighPart)) << 32) |
               static_cast<std::uint64_t>(luid.LowPart);
    }

    DXGI_FORMAT ResolveSharedTextureFormat(DXGI_FORMAT sourceFormat)
    {
        // Unity RenderTexture can expose a typeless D3D11 resource so Unity may attach
        // either linear or sRGB views. Cross-device sharing is more portable when the
        // transport resource itself uses one of the typed formats guaranteed shareable
        // by D3D11.1. CopyResource permits copies within the same typeless format group.
        switch (sourceFormat)
        {
            case DXGI_FORMAT_R8G8B8A8_TYPELESS:
            case DXGI_FORMAT_R8G8B8A8_UNORM:
            case DXGI_FORMAT_R8G8B8A8_UNORM_SRGB:
                return DXGI_FORMAT_R8G8B8A8_UNORM;

            case DXGI_FORMAT_B8G8R8A8_TYPELESS:
            case DXGI_FORMAT_B8G8R8A8_UNORM:
            case DXGI_FORMAT_B8G8R8A8_UNORM_SRGB:
                return DXGI_FORMAT_B8G8R8A8_UNORM;

            case DXGI_FORMAT_B8G8R8X8_TYPELESS:
            case DXGI_FORMAT_B8G8R8X8_UNORM:
            case DXGI_FORMAT_B8G8R8X8_UNORM_SRGB:
                return DXGI_FORMAT_B8G8R8X8_UNORM;

            case DXGI_FORMAT_R10G10B10A2_TYPELESS:
            case DXGI_FORMAT_R10G10B10A2_UNORM:
                return DXGI_FORMAT_R10G10B10A2_UNORM;

            case DXGI_FORMAT_R16G16B16A16_TYPELESS:
            case DXGI_FORMAT_R16G16B16A16_FLOAT:
                return DXGI_FORMAT_R16G16B16A16_FLOAT;

            default:
                return sourceFormat;
        }
    }

    HRESULT GetAdapterInfo(ID3D11Device* device, LUID& luid, std::wstring& name)
    {
        if (device == nullptr)
        {
            return E_POINTER;
        }

        ComPtr<IDXGIDevice> dxgiDevice;
        HRESULT hr = device->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
        if (FAILED(hr))
        {
            return hr;
        }

        ComPtr<IDXGIAdapter> adapter;
        hr = dxgiDevice->GetAdapter(&adapter);
        if (FAILED(hr))
        {
            return hr;
        }

        DXGI_ADAPTER_DESC desc{};
        hr = adapter->GetDesc(&desc);
        if (FAILED(hr))
        {
            return hr;
        }

        luid = desc.AdapterLuid;
        name = desc.Description;
        return S_OK;
    }

    HRESULT RefreshSourceAdapterInfoLocked()
    {
        g_sourceAdapterLuid = {};
        g_sourceAdapterName.clear();

        if (!g_sourceTexture)
        {
            return E_POINTER;
        }

        ComPtr<ID3D11Device> sourceDevice;
        g_sourceTexture->GetDevice(&sourceDevice);
        if (!sourceDevice)
        {
            return E_FAIL;
        }

        return GetAdapterInfo(sourceDevice.Get(), g_sourceAdapterLuid, g_sourceAdapterName);
    }

    void ResetWriterResourceLocked(bool clearAdapterInfo = false)
    {
        g_writerReady = false;
        g_sharedMutex.Reset();
        if (g_sharedHandle != nullptr)
        {
            CloseHandle(g_sharedHandle);
            g_sharedHandle = nullptr;
        }
        g_sharedTexture.Reset();
        g_context.Reset();
        g_device.Reset();
        if (clearAdapterInfo)
        {
            g_sourceAdapterLuid = {};
            g_sourceAdapterName.clear();
        }
    }

    HRESULT EnsureSharedResourceLocked()
    {
        if (!g_sourceTexture)
        {
            return E_POINTER;
        }

        if (g_sharedTexture && g_sharedHandle != nullptr)
        {
            return S_OK;
        }

        if (g_sharedName.empty())
        {
            return E_INVALIDARG;
        }

        D3D11_TEXTURE2D_DESC sourceDesc{};
        g_sourceTexture->GetDesc(&sourceDesc);

        g_sourceTexture->GetDevice(&g_device);
        if (!g_device)
        {
            return E_FAIL;
        }

        HRESULT hr = GetAdapterInfo(g_device.Get(), g_sourceAdapterLuid, g_sourceAdapterName);
        if (FAILED(hr))
        {
            ResetWriterResourceLocked();
            return hr;
        }

        g_device->GetImmediateContext(&g_context);
        if (!g_context)
        {
            return E_FAIL;
        }

        D3D11_TEXTURE2D_DESC sharedDesc = sourceDesc;
        sharedDesc.Format = ResolveSharedTextureFormat(sourceDesc.Format);
        sharedDesc.Usage = D3D11_USAGE_DEFAULT;
        sharedDesc.CPUAccessFlags = 0;
        sharedDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_RENDER_TARGET;
        sharedDesc.MiscFlags = D3D11_RESOURCE_MISC_SHARED_NTHANDLE | D3D11_RESOURCE_MISC_SHARED_KEYEDMUTEX;
        sharedDesc.MipLevels = 1;
        sharedDesc.ArraySize = 1;
        sharedDesc.SampleDesc.Count = 1;
        sharedDesc.SampleDesc.Quality = 0;

        hr = g_device->CreateTexture2D(&sharedDesc, nullptr, &g_sharedTexture);
        if (FAILED(hr))
        {
            ResetWriterResourceLocked();
            return hr;
        }

        ComPtr<IDXGIResource1> dxgiResource;
        hr = g_sharedTexture.As(&dxgiResource);
        if (FAILED(hr))
        {
            ResetWriterResourceLocked();
            return hr;
        }

        hr = dxgiResource->CreateSharedHandle(
            nullptr,
            DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
            g_sharedName.c_str(),
            &g_sharedHandle);
        if (FAILED(hr))
        {
            ResetWriterResourceLocked();
            return hr;
        }

        if (g_sharedHandle == nullptr)
        {
            ResetWriterResourceLocked();
            return E_HANDLE;
        }

        hr = g_sharedTexture.As(&g_sharedMutex);
        if (FAILED(hr))
        {
            ResetWriterResourceLocked();
            return hr;
        }

        return S_OK;
    }

    void CopySceneTexture()
    {
        std::lock_guard<std::mutex> lock(g_mutex);

        HRESULT hr = EnsureSharedResourceLocked();
        if (FAILED(hr))
        {
            g_lastError = hr;
            return;
        }

        if (!g_context || !g_sourceTexture || !g_sharedTexture || !g_sharedMutex)
        {
            g_lastError = E_POINTER;
            return;
        }

        hr = g_sharedMutex->AcquireSync(0, 0);
        if (hr == WAIT_TIMEOUT)
        {
            return;
        }
        if (FAILED(hr))
        {
            g_lastError = hr;
            return;
        }

        g_context->CopyResource(g_sharedTexture.Get(), g_sourceTexture.Get());
        hr = g_sharedMutex->ReleaseSync(1);
        if (FAILED(hr))
        {
            g_lastError = hr;
            return;
        }

        g_writerReady = true;
        g_lastError = S_OK;
    }

    void UNITY_INTERFACE_API OnRenderEvent(int eventId)
    {
        if (eventId == kCopySceneTextureEvent)
        {
            CopySceneTexture();
        }
    }
}

U3DVIEWER_EXPORT int U3DViewer_GetAbiVersion()
{
    return kNativeBridgeAbiVersion;
}

U3DVIEWER_EXPORT int U3DViewer_SetSourceTexture(void* nativeTexture, const wchar_t* sharedName)
{
    if (nativeTexture == nullptr || sharedName == nullptr || sharedName[0] == L'\0')
    {
        return 0;
    }

    ComPtr<ID3D11Texture2D> source;
    auto* unknown = static_cast<IUnknown*>(nativeTexture);
    HRESULT hr = unknown->QueryInterface(IID_PPV_ARGS(&source));
    if (FAILED(hr))
    {
        std::lock_guard<std::mutex> lock(g_mutex);
        g_lastError = hr;
        return 0;
    }

    std::lock_guard<std::mutex> lock(g_mutex);
    g_sourceTexture = source;
    g_sharedName = sharedName;
    ResetWriterResourceLocked(true);

    hr = RefreshSourceAdapterInfoLocked();
    if (FAILED(hr))
    {
        // Adapter metadata is diagnostic. Keep the source texture usable even if it cannot be queried.
        g_sourceAdapterLuid = {};
        g_sourceAdapterName.clear();
    }

    g_lastError = S_OK;
    return 1;
}

U3DVIEWER_EXPORT void* U3DViewer_GetRenderEventFunc()
{
    return reinterpret_cast<void*>(&OnRenderEvent);
}

U3DVIEWER_EXPORT int U3DViewer_GetCopyEventId()
{
    return kCopySceneTextureEvent;
}

U3DVIEWER_EXPORT int U3DViewer_IsSceneWriterReady(const wchar_t* sharedName)
{
    if (sharedName == nullptr || sharedName[0] == L'\0')
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(g_mutex);
    return g_writerReady &&
           g_sharedTexture &&
           g_sharedHandle != nullptr &&
           g_sharedMutex &&
           g_sharedName == sharedName
        ? 1
        : 0;
}

U3DVIEWER_EXPORT int U3DViewer_GetSourceDxgiFormat()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_sourceTexture)
    {
        return static_cast<int>(DXGI_FORMAT_UNKNOWN);
    }

    D3D11_TEXTURE2D_DESC desc{};
    g_sourceTexture->GetDesc(&desc);
    return static_cast<int>(desc.Format);
}

U3DVIEWER_EXPORT unsigned long long U3DViewer_GetSourceAdapterLuid()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    if (PackLuid(g_sourceAdapterLuid) == 0 && g_sourceTexture)
    {
        RefreshSourceAdapterInfoLocked();
    }
    return static_cast<unsigned long long>(PackLuid(g_sourceAdapterLuid));
}

U3DVIEWER_EXPORT int U3DViewer_GetLastError()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return static_cast<int>(g_lastError);
}

U3DVIEWER_EXPORT void U3DViewer_Reset()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    g_sourceTexture.Reset();
    g_sharedName.clear();
    ResetWriterResourceLocked(true);
    g_lastError = S_OK;
}
