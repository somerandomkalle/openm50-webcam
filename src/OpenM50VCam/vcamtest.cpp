// vcamtest: opens the "OpenM50 Webcam" camera like any app would (Media Foundation source reader),
// reads frames for a few seconds, reports fps and saves the last frame as vcamtest.bmp.
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mfreadwrite.h>
#include <wrl/client.h>
#include <cstdio>
#include <vector>
#include <string>
#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mf.lib")
#pragma comment(lib, "mfreadwrite.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "ole32.lib")
using Microsoft::WRL::ComPtr;

static void SaveBmp(const wchar_t* path, const BYTE* nv12, UINT w, UINT h)
{
    std::vector<BYTE> rgb((size_t)w * h * 3);
    const BYTE* yp = nv12;
    const BYTE* uv = nv12 + (size_t)w * h;
    for (UINT y = 0; y < h; y++)
        for (UINT x = 0; x < w; x++)
        {
            int Y = yp[(size_t)y * w + x] - 16, U = uv[(size_t)(y / 2) * w + (x & ~1u)] - 128, V = uv[(size_t)(y / 2) * w + (x & ~1u) + 1] - 128;
            auto c = [](int v) { return (BYTE)(v < 0 ? 0 : v > 255 ? 255 : v); };
            BYTE* p = &rgb[((size_t)(h - 1 - y) * w + x) * 3];
            p[2] = c((298 * Y + 459 * V + 128) >> 8);
            p[1] = c((298 * Y - 55 * U - 136 * V + 128) >> 8);
            p[0] = c((298 * Y + 541 * U + 128) >> 8);
        }
    BITMAPFILEHEADER fh{}; BITMAPINFOHEADER ih{};
    ih.biSize = sizeof ih; ih.biWidth = w; ih.biHeight = h; ih.biPlanes = 1; ih.biBitCount = 24; ih.biSizeImage = (DWORD)rgb.size();
    fh.bfType = 0x4D42; fh.bfOffBits = sizeof fh + sizeof ih; fh.bfSize = fh.bfOffBits + ih.biSizeImage;
    FILE* f = nullptr;
    if (_wfopen_s(&f, path, L"wb") == 0 && f)
    {
        fwrite(&fh, sizeof fh, 1, f); fwrite(&ih, sizeof ih, 1, f); fwrite(rgb.data(), 1, rgb.size(), f); fclose(f);
    }
}

int wmain(int argc, wchar_t** argv)
{
    CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    MFStartup(MF_VERSION);

    // vcamtest --names <OpenM50VCam.dll>: create the camera via that DLL's helper, list names, remove.
    if (argc > 2 && wcscmp(argv[1], L"--names") == 0)
    {
        HMODULE lib = LoadLibraryW(argv[2]);
        if (!lib) { wprintf(L"cannot load %s\n", argv[2]); return 1; }
        using CreateFn = HRESULT(__stdcall*)(void**);
        using RemoveFn = HRESULT(__stdcall*)(void*);
        auto create = (CreateFn)GetProcAddress(lib, "OpenM50_CreateVirtualCamera");
        auto remove = (RemoveFn)GetProcAddress(lib, "OpenM50_RemoveVirtualCamera");
        void* cam = nullptr;
        HRESULT hr = create(&cam);
        wprintf(L"create: 0x%08X\n", hr);
        Sleep(1500);
        ComPtr<IMFAttributes> a;
        MFCreateAttributes(&a, 1);
        a->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
        IMFActivate** devs = nullptr; UINT32 n = 0;
        MFEnumDeviceSources(a.Get(), &devs, &n);
        for (UINT32 i = 0; i < n; i++)
        {
            wchar_t* name = nullptr; UINT32 len = 0;
            devs[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &len);
            wprintf(L"camera: %s\n", name ? name : L"?");
            CoTaskMemFree(name);
        }
        remove(cam);
        return 0;
    }

    const wchar_t* out = argc > 1 ? argv[1] : L"vcamtest.bmp";

    ComPtr<IMFAttributes> attrs;
    MFCreateAttributes(&attrs, 1);
    attrs->SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID);
    IMFActivate** devices = nullptr;
    UINT32 count = 0;
    MFEnumDeviceSources(attrs.Get(), &devices, &count);
    IMFActivate* chosen = nullptr;
    for (UINT32 i = 0; i < count; i++)
    {
        wchar_t* name = nullptr; UINT32 len = 0;
        devices[i]->GetAllocatedString(MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME, &name, &len);
        wprintf(L"camera: %s\n", name ? name : L"?");
        if (name && wcsstr(name, L"OpenM50")) chosen = devices[i];
        CoTaskMemFree(name);
    }
    if (!chosen) { wprintf(L"OpenM50 Webcam not found\n"); return 1; }

    ComPtr<IMFMediaSource> source;
    HRESULT hr = chosen->ActivateObject(IID_PPV_ARGS(&source));
    if (FAILED(hr)) { wprintf(L"activate failed 0x%08X\n", hr); return 1; }
    ComPtr<IMFSourceReader> reader;
    hr = MFCreateSourceReaderFromMediaSource(source.Get(), nullptr, &reader);
    if (FAILED(hr)) { wprintf(L"reader failed 0x%08X\n", hr); return 1; }

    for (DWORD i = 0;; i++)
    {
        ComPtr<IMFMediaType> t;
        if (FAILED(reader->GetNativeMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, i, &t))) break;
        UINT32 w, h; MFGetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, &w, &h);
        wprintf(L"  native type %u: %ux%u\n", i, w, h);
    }
    ComPtr<IMFMediaType> type;
    reader->GetCurrentMediaType(MF_SOURCE_READER_FIRST_VIDEO_STREAM, &type);
    UINT32 w = 0, h = 0;
    MFGetAttributeSize(type.Get(), MF_MT_FRAME_SIZE, &w, &h);

    DWORD start = GetTickCount();
    int frames = 0;
    std::vector<BYTE> last;
    while (GetTickCount() - start < 5000)
    {
        DWORD stream, flags; LONGLONG ts; ComPtr<IMFSample> s;
        hr = reader->ReadSample(MF_SOURCE_READER_FIRST_VIDEO_STREAM, 0, &stream, &flags, &ts, &s);
        if (FAILED(hr)) { wprintf(L"ReadSample failed 0x%08X\n", hr); break; }
        if (!s) continue;
        frames++;
        ComPtr<IMFMediaBuffer> b; s->ConvertToContiguousBuffer(&b);
        BYTE* p; DWORD len; b->Lock(&p, nullptr, &len); last.assign(p, p + len); b->Unlock();
    }
    double secs = (GetTickCount() - start) / 1000.0;
    wprintf(L"got %d frames in %.1fs (%.1f fps), %ux%u, %zu bytes/frame\n", frames, secs, frames / secs, w, h, last.size());
    if (last.size() >= (size_t)w * h * 3 / 2) { SaveBmp(out, last.data(), w, h); wprintf(L"saved %s\n", out); }
    source->Shutdown();
    MFShutdown();
    return frames > 0 ? 0 : 1;
}
