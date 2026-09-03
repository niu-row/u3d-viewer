#include <windows.h>
#include <d3d11.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

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
    ComPtr<ID3D11Texture2D> g_sourceTexture;
    ComPtr<ID3D11Texture2D> g_sharedTexture;
    ComPtr<ID3D11Device> g_device;
    ComPtr<ID3D11DeviceContext> g_context;
    ComPtr<IDXGIKeyedMutex> g_sharedMutex;
    std::wstring g_sharedName;
    HRESULT g_lastError = S_OK;

    void ResetSharedResourceLocked()
    {
        g_sharedMutex.Reset();
        g_sharedTexture.Reset();
        g_context.Reset();
        g_device.Reset();
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
            ResetSharedResourceLocked();
            return hr;
        }

        ComPtr<IDXGIResource1> dxgiResource;
        hr = g_sharedTexture.As(&dxgiResource);
        if (FAILED(hr))
        {
            ResetSharedResourceLocked();
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
            ResetSharedResourceLocked();
            return hr;
        }

        if (sharedHandle != nullptr)
        {
            CloseHandle(sharedHandle);
        }

        hr = g_sharedTexture.As(&g_sharedMutex);
        if (FAILED(hr))
        {
            ResetSharedResourceLocked();
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
    ResetSharedResourceLocked();
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

U3DVIEWER_EXPORT long U3DViewer_GetLastError()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    return static_cast<long>(g_lastError);
}

U3DVIEWER_EXPORT void U3DViewer_Reset()
{
    std::lock_guard<std::mutex> lock(g_mutex);
    g_sourceTexture.Reset();
    g_sharedName.clear();
    ResetSharedResourceLocked();
    g_lastError = S_OK;
}
