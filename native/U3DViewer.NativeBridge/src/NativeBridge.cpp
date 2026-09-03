#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <cstring>
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

    HRESULT g_lastError = S_OK;

    void ResetWriterResourceLocked()
    {
        g_sharedMutex.Reset();
        g_sharedTexture.Reset();
        g_context.Reset();
        g_device.Reset();
    }

    void ResetReaderResourceLocked()
    {
        g_readerMutex.Reset();
        g_readerStaging.Reset();
        g_readerTexture.Reset();
        g_readerContext.Reset();
        g_readerDevice.Reset();
        g_readerWidth = 0;
        g_readerHeight = 0;
        g_readerFormat = DXGI_FORMAT_UNKNOWN;
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

        HRESULT hr = g_device->CreateTexture2D(&sharedDesc, nullptr, &g_sharedTexture);
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
    ResetWriterResourceLocked();
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

U3DVIEWER_EXPORT int U3DViewer_OpenSharedTexture(const wchar_t* sharedName)
{
    if (sharedName == nullptr || sharedName[0] == L'\0')
    {
        return 0;
    }

    std::lock_guard<std::mutex> lock(g_mutex);
    ResetReaderResourceLocked();

    D3D_FEATURE_LEVEL featureLevel{};
    HRESULT hr = D3D11CreateDevice(
        nullptr,
        D3D_DRIVER_TYPE_HARDWARE,
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
        g_lastError = hr;
        ResetReaderResourceLocked();
        return 0;
    }

    ComPtr<ID3D11Device1> device1;
    hr = g_readerDevice.As(&device1);
    if (FAILED(hr))
    {
        g_lastError = hr;
        ResetReaderResourceLocked();
        return 0;
    }

    hr = device1->OpenSharedResourceByName(
        sharedName,
        DXGI_SHARED_RESOURCE_READ,
        IID_PPV_ARGS(&g_readerTexture));
    if (FAILED(hr))
    {
        g_lastError = hr;
        ResetReaderResourceLocked();
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
        ResetReaderResourceLocked();
        return 0;
    }

    hr = g_readerTexture.As(&g_readerMutex);
    if (FAILED(hr))
    {
        g_lastError = hr;
        ResetReaderResourceLocked();
        return 0;
    }

    g_lastError = S_OK;
    return 1;
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

    bool releaseMutex = true;
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
    if (releaseMutex)
    {
        g_readerMutex->ReleaseSync(0);
    }

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
    ResetWriterResourceLocked();
    g_lastError = S_OK;
}
