#include <windows.h>
#include <d3d11.h>
#include <d3d11_1.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstdint>
#include <cstring>
#include <cwchar>
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
    constexpr int kCopySceneTextureEvent = 1;

    std::mutex g_mutex;

    // Writer state: used inside the target Unity process.
    ComPtr<ID3D11Texture2D> g_sourceTexture;
    ComPtr<ID3D11Texture2D> g_sharedTexture;
    ComPtr<ID3D11Device> g_device;
    ComPtr<ID3D11DeviceContext> g_context;
    ComPtr<IDXGIKeyedMutex> g_sharedMutex;
    std::wstring g_sharedName;
    LUID g_sourceAdapterLuid{};
    std::wstring g_sourceAdapterName;

    // Reader state: used when this same DLL is loaded by U3DViewer.exe.
    // It is a different process, therefore a different set of globals.
    ComPtr<ID3D11Device> g_readerDevice;
    ComPtr<ID3D11DeviceContext> g_readerContext;
    ComPtr<ID3D11Texture2D> g_readerTexture;
    ComPtr<ID3D11Texture2D> g_readerStaging;
    ComPtr<IDXGIKeyedMutex> g_readerMutex;
    UINT g_readerWidth = 0;
    UINT g_readerHeight = 0;
    DXGI_FORMAT g_readerFormat = DXGI_FORMAT_UNKNOWN;
    LUID g_readerAdapterLuid{};
    std::wstring g_readerAdapterName;

    HRESULT g_lastError = S_OK;

    std::uint64_t PackLuid(const LUID& luid)
    {
        return (static_cast<std::uint64_t>(static_cast<std::uint32_t>(luid.HighPart)) << 32) |
               static_cast<std::uint64_t>(luid.LowPart);
    }

    bool SameLuid(const LUID& left, const LUID& right)
    {
        return left.LowPart == right.LowPart && left.HighPart == right.HighPart;
    }

    LUID UnpackLuid(std::uint64_t value)
    {
        LUID luid{};
        luid.LowPart = static_cast<DWORD>(value & 0xFFFFFFFFull);
        luid.HighPart = static_cast<LONG>(static_cast<std::uint32_t>(value >> 32));
        return luid;
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
        g_sharedMutex.Reset();
        g_sharedTexture.Reset();
        g_context.Reset();
        g_device.Reset();
        if (clearAdapterInfo)
        {
            g_sourceAdapterLuid = {};
            g_sourceAdapterName.clear();
        }
    }

    void ResetReaderResourceLocked(bool clearAdapterInfo = true)
    {
        g_readerMutex.Reset();
        g_readerStaging.Reset();
        g_readerTexture.Reset();
        g_readerContext.Reset();
        g_readerDevice.Reset();
        g_readerWidth = 0;
        g_readerHeight = 0;
        g_readerFormat = DXGI_FORMAT_UNKNOWN;
        if (clearAdapterInfo)
        {
            g_readerAdapterLuid = {};
            g_readerAdapterName.clear();
        }
    }

    HRESULT CreateReaderDeviceLocked(std::uint64_t requestedAdapterLuid)
    {
        ComPtr<IDXGIAdapter1> selectedAdapter;

        if (requestedAdapterLuid != 0)
        {
            const LUID requested = UnpackLuid(requestedAdapterLuid);
            ComPtr<IDXGIFactory1> factory;
            HRESULT hr = CreateDXGIFactory1(IID_PPV_ARGS(&factory));
            if (FAILED(hr))
            {
                return hr;
            }

            for (UINT index = 0;; ++index)
            {
                ComPtr<IDXGIAdapter1> candidate;
                hr = factory->EnumAdapters1(index, &candidate);
                if (hr == DXGI_ERROR_NOT_FOUND)
                {
                    break;
                }
                if (FAILED(hr))
                {
                    return hr;
                }

                DXGI_ADAPTER_DESC1 desc{};
                hr = candidate->GetDesc1(&desc);
                if (FAILED(hr))
                {
                    continue;
                }

                if (SameLuid(desc.AdapterLuid, requested))
                {
                    selectedAdapter = candidate;
                    break;
                }
            }

            if (!selectedAdapter)
            {
                return DXGI_ERROR_NOT_FOUND;
            }
        }

        D3D_FEATURE_LEVEL featureLevel{};
        const D3D_DRIVER_TYPE driverType = selectedAdapter
            ? D3D_DRIVER_TYPE_UNKNOWN
            : D3D_DRIVER_TYPE_HARDWARE;

        HRESULT hr = D3D11CreateDevice(
            selectedAdapter.Get(),
            driverType,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &g_readerDevice,
            &featureLevel,
            &g_readerContext);
        if (FAILED(hr))
        {
            return hr;
        }

        hr = GetAdapterInfo(g_readerDevice.Get(), g_readerAdapterLuid, g_readerAdapterName);
        if (FAILED(hr))
        {
            return hr;
        }

        return S_OK;
    }

    HRESULT EnsureSharedResourceLocked()
    {
        if (!g_sourceTexture)
        {
            return E_POINTER;
        }

        if (g_sharedTexture)
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
        sharedDesc.Usage = D3D11_USAGE_DEFAULT;
        sharedDesc.CPUAccessFlags = 0;
        sharedDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
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

        HANDLE sharedHandle = nullptr;
        hr = dxgiResource->CreateSharedHandle(
            nullptr,
            DXGI_SHARED_RESOURCE_READ | DXGI_SHARED_RESOURCE_WRITE,
            g_sharedName.c_str(),
            &sharedHandle);
        if (FAILED(hr))
        {
            ResetWriterResourceLocked();
            return hr;
        }

        if (sharedHandle != nullptr)
        {
            CloseHandle(sharedHandle);
        }

        hr = g_sharedTexture.As(&g_sharedMutex);
        if (FAILED(hr))
        {
            ResetWriterResourceLocked();
            return hr;
        }

        return S_OK;
    }

    int OpenSharedTextureLocked(const wchar_t* sharedName, std::uint64_t adapterLuid)
    {
        if (sharedName == nullptr || sharedName[0] == L'\0')
        {
            g_lastError = E_INVALIDARG;
            return 0;
        }

        ResetReaderResourceLocked();

        HRESULT hr = CreateReaderDeviceLocked(adapterLuid);
        if (FAILED(hr))
        {
            g_lastError = hr;
            ResetReaderResourceLocked(false);
            return 0;
        }

        ComPtr<ID3D11Device1> device1;
        hr = g_readerDevice.As(&device1);
        if (FAILED(hr))
        {
            g_lastError = hr;
            ResetReaderResourceLocked(false);
            return 0;
        }

        hr = device1->OpenSharedResourceByName(
            sharedName,
            DXGI_SHARED_RESOURCE_READ,
            IID_PPV_ARGS(&g_readerTexture));
        if (FAILED(hr))
        {
            g_lastError = hr;
            ResetReaderResourceLocked(false);
            return 0;
        }

        D3D11_TEXTURE2D_DESC desc{};
        g_readerTexture->GetDesc(&desc);
        g_readerWidth = desc.Width;
        g_readerHeight = desc.Height;
        g_readerFormat = desc.Format;

        D3D11_TEXTURE2D_DESC stagingDesc = desc;
        stagingDesc.Usage = D3D11_USAGE_STAGING;
        stagingDesc.BindFlags = 0;
        stagingDesc.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        stagingDesc.MiscFlags = 0;
        stagingDesc.MipLevels = 1;
        stagingDesc.ArraySize = 1;
        stagingDesc.SampleDesc.Count = 1;
        stagingDesc.SampleDesc.Quality = 0;

        hr = g_readerDevice->CreateTexture2D(&stagingDesc, nullptr, &g_readerStaging);
        if (FAILED(hr))
        {
            g_lastError = hr;
            ResetReaderResourceLocked(false);
            return 0;
        }

        hr = g_readerTexture.As(&g_readerMutex);
        if (FAILED(hr))
        {
            g_lastError = hr;
            ResetReaderResourceLocked(false);
            return 0;
        }

        g_lastError = S_OK;
        return 1;
    }

    int CopyAdapterName(const std::wstring& name, wchar_t* buffer, int capacity)
    {
        if (buffer == nullptr || capacity <= 0)
        {
            return 0;
        }

        if (name.empty())
        {
            buffer[0] = L'\0';
            return 0;
        }

        wcsncpy_s(buffer, static_cast<std::size_t>(capacity), name.c_str(), _TRUNCATE);
        return 1;
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
        g_sharedMutex->ReleaseSync(1);
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

U3DVIEWER_EXPORT int U3DViewer_OpenSharedTexture(const wchar_t* sharedName)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return OpenSharedTextureLocked(sharedName, 0);
}

U3DVIEWER_EXPORT int U3DViewer_OpenSharedTextureOnAdapter(
    const wchar_t* sharedName,
    unsigned long long adapterLuid)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return OpenSharedTextureLocked(sharedName, static_cast<std::uint64_t>(adapterLuid));
}

U3DVIEWER_EXPORT unsigned long long U3DViewer_GetReaderAdapterLuid()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return static_cast<unsigned long long>(PackLuid(g_readerAdapterLuid));
}

U3DVIEWER_EXPORT int U3DViewer_GetReaderAdapterName(wchar_t* buffer, int capacity)
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return CopyAdapterName(g_readerAdapterName, buffer, capacity);
}

U3DVIEWER_EXPORT int U3DViewer_ReadSharedTexture(
    void* destination,
    int destinationStride,
    int destinationHeight,
    int* width,
    int* height,
    int* dxgiFormat)
{
    if (destination == nullptr || destinationStride <= 0 || destinationHeight <= 0)
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(g_mutex);
    if (!g_readerTexture || !g_readerStaging || !g_readerContext || !g_readerMutex)
    {
        g_lastError = E_POINTER;
        return 0;
    }

    const UINT bytesPerPixel = 4;
    const UINT rowBytes = g_readerWidth * bytesPerPixel;
    if (destinationStride < static_cast<int>(rowBytes) || destinationHeight < static_cast<int>(g_readerHeight))
    {
        g_lastError = E_INVALIDARG;
        return 0;
    }

    HRESULT hr = g_readerMutex->AcquireSync(1, 0);
    if (hr == WAIT_TIMEOUT)
    {
        return 0;
    }
    if (FAILED(hr))
    {
        g_lastError = hr;
        return 0;
    }

    g_readerContext->CopyResource(g_readerStaging.Get(), g_readerTexture.Get());

    D3D11_MAPPED_SUBRESOURCE mapped{};
    hr = g_readerContext->Map(g_readerStaging.Get(), 0, D3D11_MAP_READ, 0, &mapped);
    if (FAILED(hr))
    {
        g_readerMutex->ReleaseSync(0);
        g_lastError = hr;
        return 0;
    }

    auto* dst = static_cast<unsigned char*>(destination);
    auto* src = static_cast<const unsigned char*>(mapped.pData);
    for (UINT y = 0; y < g_readerHeight; ++y)
    {
        std::memcpy(
            dst + static_cast<size_t>(y) * destinationStride,
            src + static_cast<size_t>(y) * mapped.RowPitch,
            rowBytes);
    }

    g_readerContext->Unmap(g_readerStaging.Get(), 0);
    g_readerMutex->ReleaseSync(0);

    if (width) *width = static_cast<int>(g_readerWidth);
    if (height) *height = static_cast<int>(g_readerHeight);
    if (dxgiFormat) *dxgiFormat = static_cast<int>(g_readerFormat);

    g_lastError = S_OK;
    return 1;
}

U3DVIEWER_EXPORT int U3DViewer_GetLastError()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return static_cast<int>(g_lastError);
}

U3DVIEWER_EXPORT void U3DViewer_ResetReader()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    ResetReaderResourceLocked();
    g_lastError = S_OK;
}

U3DVIEWER_EXPORT void U3DViewer_Reset()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    g_sourceTexture.Reset();
    g_sharedName.clear();
    ResetWriterResourceLocked(true);
    g_lastError = S_OK;
}
