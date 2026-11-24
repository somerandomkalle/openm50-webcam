// OpenM50VCam - Windows 11 virtual camera ("OpenM50 Webcam") for OpenM50Webcam.exe
//
// This DLL is a Media Foundation media source that the Windows Frame Server loads when an
// application opens the virtual camera. It pulls the MJPEG stream served by OpenM50Webcam.exe on
// http://127.0.0.1:8090/stream.mjpg, decodes it with WIC and delivers NV12 frames.
//
// The DLL also exports two helpers that OpenM50Webcam.exe calls to create/remove the virtual
// camera (MFCreateVirtualCamera, session lifetime: the camera exists while the exe runs).
//
// Must be registered machine-wide (regsvr32 as admin) and live in a folder the LocalService
// account can read (e.g. C:\Program Files\OpenM50 Webcam), because Frame Server loads it.

#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <mfapi.h>
#include <mfidl.h>
#include <mferror.h>
#include <mfvirtualcamera.h>
#include <wincodec.h>
#include <winhttp.h>
#include <wrl/client.h>
#include <atomic>
#include <mutex>
#include <thread>
#include <vector>
#include <string>
#include <algorithm>
#include <cstdarg>
#include <cwchar>

#pragma comment(lib, "mfplat.lib")
#pragma comment(lib, "mfuuid.lib")
#pragma comment(lib, "mfsensorgroup.lib")
#pragma comment(lib, "winhttp.lib")
#pragma comment(lib, "windowscodecs.lib")
#pragma comment(lib, "ole32.lib")
#pragma comment(lib, "advapi32.lib")
#pragma comment(lib, "gdi32.lib")
#pragma comment(lib, "user32.lib")

using Microsoft::WRL::ComPtr;

// {E2B7A5C1-3F4D-4C8E-9A61-0D5B7F2C9E34}
static const CLSID CLSID_OpenM50VCam = { 0xe2b7a5c1, 0x3f4d, 0x4c8e, { 0x9a, 0x61, 0x0d, 0x5b, 0x7f, 0x2c, 0x9e, 0x34 } };
// PINNAME_VIDEO_CAPTURE {FB6C4281-0353-11d1-905F-0000C0CC16BA}
static const GUID PINNAME_VIDEO_CAPTURE_ = { 0xfb6c4281, 0x0353, 0x11d1, { 0x90, 0x5f, 0x00, 0x00, 0xc0, 0xcc, 0x16, 0xba } };

static const wchar_t* kFriendlyName = L"OpenM50 Webcam";
static const INTERNET_PORT kPort = 8090;
static const LONGLONG kFrameDuration = 333333; // 30 fps in 100 ns units

static HMODULE g_module = nullptr;

static void Trace(const wchar_t* fmt, ...)
{
    wchar_t buf[512];
    va_list args;
    va_start(args, fmt);
    _vsnwprintf_s(buf, _TRUNCATE, fmt, args);
    va_end(args);
    OutputDebugStringW(L"[OpenM50VCam] ");
    OutputDebugStringW(buf);
    OutputDebugStringW(L"\n");
}

// IKsControl, declared here to avoid pulling in the DirectShow/KS headers.
struct __declspec(uuid("28F54685-06FD-11D2-B27A-00A0C9223196")) IKsControlX : public IUnknown
{
    virtual HRESULT STDMETHODCALLTYPE KsProperty(void* property, ULONG propertyLength, void* data, ULONG dataLength, ULONG* bytesReturned) = 0;
    virtual HRESULT STDMETHODCALLTYPE KsMethod(void* method, ULONG methodLength, void* data, ULONG dataLength, ULONG* bytesReturned) = 0;
    virtual HRESULT STDMETHODCALLTYPE KsEvent(void* event, ULONG eventLength, void* data, ULONG dataLength, ULONG* bytesReturned) = 0;
};

// =====================================================================================
// FrameProvider: pulls MJPEG from OpenM50Webcam.exe and keeps the latest frame as NV12
// =====================================================================================

class FrameProvider
{
public:
    static FrameProvider& Get()
    {
        static FrameProvider instance;
        return instance;
    }

    void AddClient()
    {
        if (_clients.fetch_add(1) == 0)
        {
            std::lock_guard<std::mutex> lock(_mutex);
            if (!_threadStarted)
            {
                _threadStarted = true;
                std::thread([this] { Run(); }).detach();
            }
        }
    }

    void RemoveClient() { _clients.fetch_sub(1); }

    void SetTargetSize(UINT w, UINT h)
    {
        std::lock_guard<std::mutex> lock(_mutex);
        _targetW = w;
        _targetH = h;
    }

    // Writes one NV12 frame (w*h*3/2 bytes). When no fresh frame arrives, shows the last frame
    // darkened with a status message on top instead of freezing.
    void Fill(BYTE* dst, UINT w, UINT h)
    {
        size_t size = (size_t)w * h * 3 / 2;
        std::lock_guard<std::mutex> lock(_mutex);
        bool haveFrame = _nv12W == w && _nv12H == h && _nv12.size() == size;
        if (haveFrame && GetTickCount64() - _lastFrameTick < kStaleMs)
        {
            memcpy(dst, _nv12.data(), size);
            return;
        }

        Status status = _status;
        if (status == Status::Streaming) status = Status::Connecting; // server streams, but nothing reaches us
        if (_statusFrame.size() != size || _statusFrameW != w || _statusFrameH != h ||
            _statusFrameStatus != status || _statusFrameSourceTick != _lastFrameTick)
        {
            BuildStatusFrame(w, h, status, haveFrame ? _nv12.data() : nullptr);
            _statusFrameW = w;
            _statusFrameH = h;
            _statusFrameStatus = status;
            _statusFrameSourceTick = _lastFrameTick;
        }
        memcpy(dst, _statusFrame.data(), size);
    }

private:
    enum class Status { NotRunning, NoCamera, Connecting, Error, Streaming };
    static constexpr ULONGLONG kStaleMs = 1500;

    FrameProvider() = default;

    static void Messages(Status s, const wchar_t*& title, const wchar_t*& detail)
    {
        switch (s)
        {
        case Status::NotRunning:
            title = L"OpenM50 Webcam is not running";
            detail = L"Start the OpenM50 Webcam app to see the camera image.";
            break;
        case Status::NoCamera:
            title = L"No camera connected";
            detail = L"Connect the Canon EOS M50 via USB and switch it on.\nWi-Fi and Bluetooth on the camera must be off.";
            break;
        case Status::Error:
            title = L"Camera is not responding";
            detail = L"Reconnecting automatically.\nIf this persists, switch the camera off and on again.";
            break;
        default:
            title = L"Connecting to the camera…";
            detail = L"One moment please.";
            break;
        }
    }

    // Renders icon + text with GDI into an 8-bit coverage mask (0 = background, 255 = white).
    static std::vector<BYTE> RenderMask(UINT w, UINT h, Status status)
    {
        std::vector<BYTE> mask((size_t)w * h, 0);
        HDC dc = CreateCompatibleDC(nullptr);
        if (!dc) return mask;
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(bi.bmiHeader);
        bi.bmiHeader.biWidth = (LONG)w;
        bi.bmiHeader.biHeight = -(LONG)h; // top-down
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        void* bits = nullptr;
        HBITMAP bmp = CreateDIBSection(dc, &bi, DIB_RGB_COLORS, &bits, nullptr, 0);
        if (!bmp || !bits)
        {
            DeleteDC(dc);
            return mask;
        }
        HGDIOBJ oldBmp = SelectObject(dc, bmp);
        memset(bits, 0, (size_t)w * h * 4);
        SetBkMode(dc, TRANSPARENT);

        // Camera icon with a "no signal" slash.
        int cx = (int)w / 2, cy = (int)(h * 0.30), s = (int)h / 11;
        int penWidth = (std::max)(2, (int)h / 160);
        HPEN pen = CreatePen(PS_SOLID, penWidth, RGB(235, 235, 235));
        HGDIOBJ oldPen = SelectObject(dc, pen);
        HGDIOBJ oldBrush = SelectObject(dc, GetStockObject(NULL_BRUSH));
        RoundRect(dc, cx - s * 3 / 2, cy - s * 3 / 4, cx + s * 3 / 2, cy + s, s / 2, s / 2); // body
        RoundRect(dc, cx - s / 2, cy - s * 6 / 5, cx + s / 2, cy - s * 3 / 4 + penWidth, s / 4, s / 4); // top
        Ellipse(dc, cx - s / 2, cy - s / 3, cx + s / 2, cy + s * 2 / 3); // lens
        if (status != Status::Connecting)
        {
            MoveToEx(dc, cx - s * 2, cy - s * 3 / 2, nullptr);
            LineTo(dc, cx + s * 2, cy + s * 3 / 2);
        }
        SelectObject(dc, oldBrush);
        SelectObject(dc, oldPen);
        DeleteObject(pen);

        const wchar_t* title;
        const wchar_t* detail;
        Messages(status, title, detail);

        auto drawText = [&](const wchar_t* text, int height, int weight, COLORREF color, RECT rc) {
            HFONT font = CreateFontW(-height, 0, 0, 0, weight, FALSE, FALSE, FALSE, DEFAULT_CHARSET, OUT_DEFAULT_PRECIS,
                                     CLIP_DEFAULT_PRECIS, ANTIALIASED_QUALITY, DEFAULT_PITCH, L"Segoe UI");
            HGDIOBJ oldFont = SelectObject(dc, font);
            SetTextColor(dc, color);
            DrawTextW(dc, text, -1, &rc, DT_CENTER | DT_WORDBREAK | DT_NOPREFIX);
            SelectObject(dc, oldFont);
            DeleteObject(font);
        };
        drawText(title, (int)h / 15, FW_SEMIBOLD, RGB(255, 255, 255),
                 RECT{ (LONG)w / 12, (LONG)(h * 0.47), (LONG)(w * 11 / 12), (LONG)(h * 0.60) });
        drawText(detail, (int)h / 32, FW_NORMAL, RGB(200, 200, 200),
                 RECT{ (LONG)w / 8, (LONG)(h * 0.60), (LONG)(w * 7 / 8), (LONG)(h * 0.80) });
        drawText(L"OpenM50 Webcam", (int)h / 42, FW_NORMAL, RGB(120, 120, 120),
                 RECT{ 0, (LONG)(h * 0.91), (LONG)w, (LONG)h });

        GdiFlush();
        const BYTE* px = static_cast<const BYTE*>(bits);
        for (size_t i = 0; i < mask.size(); i++)
            mask[i] = px[i * 4 + 1]; // green channel = coverage for grey/white text
        SelectObject(dc, oldBmp);
        DeleteObject(bmp);
        DeleteDC(dc);
        return mask;
    }

    void BuildStatusFrame(UINT w, UINT h, Status status, const BYTE* lastFrame)
    {
        size_t lumaSize = (size_t)w * h;
        _statusFrame.resize(lumaSize * 3 / 2);
        BYTE* y = _statusFrame.data();
        BYTE* uv = y + lumaSize;
        if (lastFrame)
        {
            // Darken and desaturate the last frame as background.
            for (size_t i = 0; i < lumaSize; i++) y[i] = (BYTE)(16 + (lastFrame[i] - 16) * 25 / 100);
            for (size_t i = 0; i < lumaSize / 2; i++) uv[i] = (BYTE)(128 + (lastFrame[lumaSize + i] - 128) * 25 / 100);
        }
        else
        {
            memset(y, 24, lumaSize);
            memset(uv, 128, lumaSize / 2);
        }
        std::vector<BYTE> mask = RenderMask(w, h, status);
        for (size_t i = 0; i < lumaSize; i++)
            if (mask[i]) y[i] = (BYTE)(y[i] + ((235 - y[i]) * mask[i] + 127) / 255);
    }

    // Asks OpenM50Webcam.exe for its state (GET /status) to show the right message.
    void PollStatus(HINTERNET session)
    {
        Status status = Status::NotRunning;
        HINTERNET connect = session ? WinHttpConnect(session, L"127.0.0.1", kPort, 0) : nullptr;
        HINTERNET request = connect ? WinHttpOpenRequest(connect, L"GET", L"/status", nullptr, WINHTTP_NO_REFERER,
                                                         WINHTTP_DEFAULT_ACCEPT_TYPES, 0) : nullptr;
        if (request)
        {
            WinHttpSetTimeouts(request, 1000, 1000, 1000, 1000);
            if (WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) &&
                WinHttpReceiveResponse(request, nullptr))
            {
                std::string body;
                char chunk[1024];
                DWORD read = 0;
                while (WinHttpReadData(request, chunk, sizeof(chunk), &read) && read > 0)
                    body.append(chunk, read);
                size_t p = body.find("\"state\":\"");
                std::string state = p == std::string::npos ? "" : body.substr(p + 9, body.find('"', p + 9) - (p + 9));
                status = state == "streaming"   ? Status::Streaming
                       : state == "no-camera"   ? Status::NoCamera
                       : state == "error"       ? Status::Error
                                                : Status::Connecting;
            }
        }
        if (request) WinHttpCloseHandle(request);
        if (connect) WinHttpCloseHandle(connect);
        std::lock_guard<std::mutex> lock(_mutex);
        _status = status;
    }

    void Run()
    {
        CoInitializeEx(nullptr, COINIT_MULTITHREADED);
        ComPtr<IWICImagingFactory> wic;
        HRESULT hr = CoCreateInstance(CLSID_WICImagingFactory, nullptr, CLSCTX_INPROC_SERVER, IID_PPV_ARGS(&wic));
        if (FAILED(hr))
        {
            Trace(L"WIC factory failed 0x%08X", hr);
            return;
        }

        HINTERNET session = WinHttpOpen(L"OpenM50VCam/1.0", WINHTTP_ACCESS_TYPE_NO_PROXY,
                                        WINHTTP_NO_PROXY_NAME, WINHTTP_NO_PROXY_BYPASS, 0);
        for (;;)
        {
            if (_clients.load() <= 0)
            {
                Sleep(200);
                continue;
            }
            StreamOnce(session, wic.Get()); // returns when the stream stalls or the server is gone
            PollStatus(session);
            Sleep(300);
        }
    }

    void StreamOnce(HINTERNET session, IWICImagingFactory* wic)
    {
        if (!session) return;
        HINTERNET connect = WinHttpConnect(session, L"127.0.0.1", kPort, 0);
        if (!connect) return;
        HINTERNET request = WinHttpOpenRequest(connect, L"GET", L"/stream.mjpg?client=vcam", nullptr,
                                               WINHTTP_NO_REFERER, WINHTTP_DEFAULT_ACCEPT_TYPES, 0);
        if (request)
        {
            // Short receive timeout: if no frame arrives for 1.5 s we drop out and ask for the status.
            WinHttpSetTimeouts(request, 1000, 1000, 1000, 1500);
            if (WinHttpSendRequest(request, WINHTTP_NO_ADDITIONAL_HEADERS, 0, WINHTTP_NO_REQUEST_DATA, 0, 0, 0) &&
                WinHttpReceiveResponse(request, nullptr))
            {
                std::vector<BYTE> buffer;
                buffer.reserve(1 << 20);
                std::vector<BYTE> chunk(256 * 1024);
                while (_clients.load() > 0)
                {
                    DWORD read = 0;
                    if (!WinHttpReadData(request, chunk.data(), (DWORD)chunk.size(), &read) || read == 0)
                        break;
                    buffer.insert(buffer.end(), chunk.begin(), chunk.begin() + read);
                    ParseParts(buffer, wic);
                }
            }
            WinHttpCloseHandle(request);
        }
        WinHttpCloseHandle(connect);
    }

    // Multipart parts look like: "--boundary\r\nContent-Type: image/jpeg\r\nContent-Length: N\r\n\r\n<N bytes>\r\n"
    void ParseParts(std::vector<BYTE>& buf, IWICImagingFactory* wic)
    {
        static const BYTE sep[] = { '\r', '\n', '\r', '\n' };
        size_t consumed = 0;           // bytes fully processed
        size_t jpegOffset = 0, jpegLength = 0; // newest complete JPEG
        for (;;)
        {
            auto from = buf.begin() + consumed;
            auto it = std::search(from, buf.end(), std::begin(sep), std::end(sep));
            if (it == buf.end())
            {
                if (buf.size() - consumed > 64 * 1024) consumed = buf.size(); // garbage, resync
                break;
            }
            size_t headerEnd = (size_t)(it - buf.begin()) + 4;
            std::string header(from, it);
            std::transform(header.begin(), header.end(), header.begin(), [](char c) { return (char)tolower((unsigned char)c); });
            size_t pos = header.find("content-length:");
            size_t length = pos == std::string::npos ? 0 : strtoul(header.c_str() + pos + 15, nullptr, 10);
            if (length == 0 || length > 32 * 1024 * 1024)
            {
                consumed = headerEnd; // not a frame header, skip it
                continue;
            }
            if (buf.size() < headerEnd + length) break; // frame incomplete, wait for more data
            jpegOffset = headerEnd;
            jpegLength = length;
            consumed = headerEnd + length;
        }

        // Only decode the newest complete frame; older queued frames are dropped.
        if (jpegLength > 0)
            Decode(wic, buf.data() + jpegOffset, jpegLength);
        if (consumed > 0)
            buf.erase(buf.begin(), buf.begin() + consumed);
    }

    void Decode(IWICImagingFactory* wic, const BYTE* data, size_t length)
    {
        UINT tw, th;
        {
            std::lock_guard<std::mutex> lock(_mutex);
            tw = _targetW;
            th = _targetH;
        }
        if (tw == 0 || th == 0) return;

        ComPtr<IWICStream> stream;
        ComPtr<IWICBitmapDecoder> decoder;
        ComPtr<IWICBitmapFrameDecode> frame;
        ComPtr<IWICFormatConverter> converter;
        if (FAILED(wic->CreateStream(&stream))) return;
        if (FAILED(stream->InitializeFromMemory(const_cast<BYTE*>(data), (DWORD)length))) return;
        if (FAILED(wic->CreateDecoderFromStream(stream.Get(), nullptr, WICDecodeMetadataCacheOnDemand, &decoder))) return;
        if (FAILED(decoder->GetFrame(0, &frame))) return;
        UINT sw = 0, sh = 0;
        frame->GetSize(&sw, &sh);
        if (sw == 0 || sh == 0) return;
        if (FAILED(wic->CreateFormatConverter(&converter))) return;
        if (FAILED(converter->Initialize(frame.Get(), GUID_WICPixelFormat32bppBGRA, WICBitmapDitherTypeNone,
                                         nullptr, 0.0, WICBitmapPaletteTypeCustom))) return;

        // Fit into the target size keeping the aspect ratio (letterbox if needed).
        UINT fw = tw;
        UINT fh = (UINT)((UINT64)sh * tw / sw);
        if (fh > th)
        {
            fh = th;
            fw = (UINT)((UINT64)sw * th / sh);
        }
        fw &= ~1u;
        fh &= ~1u;
        if (fw == 0 || fh == 0) return;

        IWICBitmapSource* source = converter.Get();
        ComPtr<IWICBitmapScaler> scaler;
        if (fw != sw || fh != sh)
        {
            if (FAILED(wic->CreateBitmapScaler(&scaler))) return;
            if (FAILED(scaler->Initialize(converter.Get(), fw, fh, WICBitmapInterpolationModeHighQualityCubic))) return;
            source = scaler.Get();
        }

        _bgra.resize((size_t)fw * fh * 4);
        if (FAILED(source->CopyPixels(nullptr, fw * 4, (UINT)_bgra.size(), _bgra.data()))) return;

        std::vector<BYTE> nv12((size_t)tw * th * 3 / 2);
        memset(nv12.data(), 16, (size_t)tw * th);                 // black luma
        memset(nv12.data() + (size_t)tw * th, 128, (size_t)tw * th / 2); // neutral chroma
        UINT ox = ((tw - fw) / 2) & ~1u;
        UINT oy = ((th - fh) / 2) & ~1u;
        BgraToNv12(_bgra.data(), fw, fh, nv12.data(), tw, th, ox, oy);

        std::lock_guard<std::mutex> lock(_mutex);
        if (tw != _targetW || th != _targetH) return; // size changed meanwhile
        _nv12.swap(nv12);
        _nv12W = tw;
        _nv12H = th;
        _lastFrameTick = GetTickCount64();
        _status = Status::Streaming;
    }

    static inline BYTE Clamp(int v) { return (BYTE)(v < 0 ? 0 : (v > 255 ? 255 : v)); }

    // BT.709, limited range.
    static void BgraToNv12(const BYTE* bgra, UINT fw, UINT fh, BYTE* nv12, UINT tw, UINT th, UINT ox, UINT oy)
    {
        BYTE* yPlane = nv12;
        BYTE* uvPlane = nv12 + (size_t)tw * th;
        for (UINT y = 0; y < fh; y++)
        {
            const BYTE* src = bgra + (size_t)y * fw * 4;
            BYTE* dstY = yPlane + (size_t)(oy + y) * tw + ox;
            for (UINT x = 0; x < fw; x++)
            {
                int b = src[x * 4 + 0], g = src[x * 4 + 1], r = src[x * 4 + 2];
                dstY[x] = Clamp(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);
            }
        }
        for (UINT y = 0; y + 1 < fh; y += 2)
        {
            const BYTE* row0 = bgra + (size_t)y * fw * 4;
            const BYTE* row1 = row0 + (size_t)fw * 4;
            BYTE* dstUV = uvPlane + (size_t)((oy + y) / 2) * tw + ox;
            for (UINT x = 0; x + 1 < fw; x += 2)
            {
                int b = row0[x * 4] + row0[x * 4 + 4] + row1[x * 4] + row1[x * 4 + 4];
                int g = row0[x * 4 + 1] + row0[x * 4 + 5] + row1[x * 4 + 1] + row1[x * 4 + 5];
                int r = row0[x * 4 + 2] + row0[x * 4 + 6] + row1[x * 4 + 2] + row1[x * 4 + 6];
                b >>= 2; g >>= 2; r >>= 2;
                dstUV[x] = Clamp(((-26 * r - 87 * g + 112 * b + 128) >> 8) + 128);
                dstUV[x + 1] = Clamp(((112 * r - 102 * g - 10 * b + 128) >> 8) + 128);
            }
        }
    }

    std::mutex _mutex;
    std::atomic<int> _clients{ 0 };
    bool _threadStarted = false;
    UINT _targetW = 1920, _targetH = 1080;
    std::vector<BYTE> _bgra;    // decode thread only
    std::vector<BYTE> _nv12;    // guarded by _mutex
    UINT _nv12W = 0, _nv12H = 0;
    ULONGLONG _lastFrameTick = 0;
    Status _status = Status::Connecting;
    // cached "no signal" frame
    std::vector<BYTE> _statusFrame;
    UINT _statusFrameW = 0, _statusFrameH = 0;
    Status _statusFrameStatus = Status::Streaming;
    ULONGLONG _statusFrameSourceTick = ~0ull;
};

// =====================================================================================
// MediaStream
// =====================================================================================

class MediaSource;

class MediaStream : public IMFMediaStream2
{
public:
    HRESULT Initialize(MediaSource* source);

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFMediaEventGenerator) ||
            riid == __uuidof(IMFMediaStream) || riid == __uuidof(IMFMediaStream2))
        {
            *ppv = static_cast<IMFMediaStream2*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++_ref; }
    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG r = --_ref;
        if (r == 0) delete this;
        return r;
    }

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** ev) override
    {
        ComPtr<IMFMediaEventQueue> q;
        {
            std::lock_guard<std::recursive_mutex> lock(_mutex);
            if (_shutdown) return MF_E_SHUTDOWN;
            q = _queue;
        }
        return q->GetEvent(flags, ev);
    }
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state) override
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        return _queue->BeginGetEvent(cb, state);
    }
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev) override
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        return _queue->EndGetEvent(result, ev);
    }
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value) override
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        return _queue->QueueEventParamVar(type, ext, status, value);
    }

    // IMFMediaStream
    STDMETHODIMP GetMediaSource(IMFMediaSource** source) override;
    STDMETHODIMP GetStreamDescriptor(IMFStreamDescriptor** sd) override
    {
        if (!sd) return E_POINTER;
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        return _descriptor.CopyTo(sd);
    }
    STDMETHODIMP RequestSample(IUnknown* token) override
    {
        // Pace delivery to ~30 fps (pull model: the pipeline asks again right after each sample).
        LONGLONG now = MFGetSystemTime();
        LONGLONG due = _nextDue.load();
        if (due > now)
        {
            LONGLONG waitMs = (due - now) / 10000;
            if (waitMs > 0 && waitMs < 100) Sleep((DWORD)waitMs);
            now = MFGetSystemTime();
        }
        // Advance from the scheduled time (not the wake-up time) so Sleep() overshoot doesn't lower the rate;
        // resync if we fell more than one frame behind.
        _nextDue = (due < now - kFrameDuration ? now : due) + kFrameDuration;

        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        if (_state != MF_STREAM_STATE_RUNNING) return MF_E_INVALIDREQUEST;

        DWORD size = _width * _height * 3 / 2;
        ComPtr<IMFSample> sample;
        ComPtr<IMFMediaBuffer> buffer;
        HRESULT hr = MFCreateSample(&sample);
        if (SUCCEEDED(hr)) hr = MFCreateMemoryBuffer(size, &buffer);
        if (SUCCEEDED(hr))
        {
            BYTE* p = nullptr;
            hr = buffer->Lock(&p, nullptr, nullptr);
            if (SUCCEEDED(hr))
            {
                FrameProvider::Get().Fill(p, _width, _height);
                buffer->Unlock();
                buffer->SetCurrentLength(size);
            }
        }
        if (SUCCEEDED(hr)) hr = sample->AddBuffer(buffer.Get());
        if (SUCCEEDED(hr)) hr = sample->SetSampleTime(now);
        if (SUCCEEDED(hr)) hr = sample->SetSampleDuration(kFrameDuration);
        if (SUCCEEDED(hr) && token) hr = sample->SetUnknown(MFSampleExtension_Token, token);
        if (SUCCEEDED(hr)) hr = _queue->QueueEventParamUnk(MEMediaSample, GUID_NULL, S_OK, sample.Get());
        return hr;
    }

    // IMFMediaStream2
    STDMETHODIMP SetStreamState(MF_STREAM_STATE state) override
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        if (state == MF_STREAM_STATE_STOPPED) ReleaseClient();
        _state = state;
        return S_OK;
    }
    STDMETHODIMP GetStreamState(MF_STREAM_STATE* state) override
    {
        if (!state) return E_POINTER;
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        *state = _state;
        return S_OK;
    }

    // Internal
    HRESULT Start(IMFMediaType* type, const PROPVARIANT* time)
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        if (type)
        {
            UINT32 w = 0, h = 0;
            if (SUCCEEDED(MFGetAttributeSize(type, MF_MT_FRAME_SIZE, &w, &h)) && w && h)
            {
                _width = w;
                _height = h;
            }
        }
        FrameProvider::Get().SetTargetSize(_width, _height);
        if (!_hasClient)
        {
            _hasClient = true;
            FrameProvider::Get().AddClient();
        }
        _state = MF_STREAM_STATE_RUNNING;
        Trace(L"stream started %ux%u", _width, _height);
        return _queue->QueueEventParamVar(MEStreamStarted, GUID_NULL, S_OK, time);
    }

    HRESULT Stop()
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        ReleaseClient();
        _state = MF_STREAM_STATE_STOPPED;
        return _queue->QueueEventParamVar(MEStreamStopped, GUID_NULL, S_OK, nullptr);
    }

    void Shutdown()
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return;
        ReleaseClient();
        _state = MF_STREAM_STATE_STOPPED;
        _shutdown = true;
        if (_queue) _queue->Shutdown();
        _source = nullptr;
    }

    IMFStreamDescriptor* Descriptor() { return _descriptor.Get(); }

private:
    ~MediaStream() = default;

    void ReleaseClient()
    {
        if (_hasClient)
        {
            _hasClient = false;
            FrameProvider::Get().RemoveClient();
        }
    }

    static HRESULT CreateType(UINT w, UINT h, IMFMediaType** out)
    {
        ComPtr<IMFMediaType> t;
        HRESULT hr = MFCreateMediaType(&t);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Video);
        if (SUCCEEDED(hr)) hr = t->SetGUID(MF_MT_SUBTYPE, MFVideoFormat_NV12);
        if (SUCCEEDED(hr)) hr = MFSetAttributeSize(t.Get(), MF_MT_FRAME_SIZE, w, h);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t.Get(), MF_MT_FRAME_RATE, 30, 1);
        if (SUCCEEDED(hr)) hr = MFSetAttributeRatio(t.Get(), MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_DEFAULT_STRIDE, w);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_SAMPLE_SIZE, w * h * 3 / 2);
        if (SUCCEEDED(hr)) hr = t->SetUINT32(MF_MT_AVG_BITRATE, w * h * 12 * 30);
        if (SUCCEEDED(hr)) *out = t.Detach();
        return hr;
    }

    std::atomic<ULONG> _ref{ 1 };
    std::recursive_mutex _mutex;
    ComPtr<IMFMediaEventQueue> _queue;
    ComPtr<IMFStreamDescriptor> _descriptor;
    MediaSource* _source = nullptr; // weak, the source owns the stream
    MF_STREAM_STATE _state = MF_STREAM_STATE_STOPPED;
    UINT _width = 1920, _height = 1080;
    bool _shutdown = false;
    bool _hasClient = false;
    std::atomic<LONGLONG> _nextDue{ 0 };

    friend class MediaSource;
};

// =====================================================================================
// MediaSource
// =====================================================================================

class MediaSource : public IMFMediaSourceEx, public IMFGetService, public IKsControlX
{
public:
    HRESULT Initialize(IMFAttributes* activatorAttributes)
    {
        HRESULT hr = MFCreateAttributes(&_attributes, 10);
        if (SUCCEEDED(hr) && activatorAttributes) hr = activatorAttributes->CopyAllItems(_attributes.Get());
        if (SUCCEEDED(hr)) hr = MFCreateEventQueue(&_queue);
        if (SUCCEEDED(hr))
        {
            _stream.Attach(new (std::nothrow) MediaStream());
            hr = _stream ? _stream->Initialize(this) : E_OUTOFMEMORY;
        }
        if (SUCCEEDED(hr))
        {
            IMFStreamDescriptor* sd = _stream->Descriptor();
            hr = MFCreatePresentationDescriptor(1, &sd, &_descriptor);
        }
        if (SUCCEEDED(hr)) hr = _descriptor->SelectStream(0);
        return hr;
    }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFMediaEventGenerator) ||
            riid == __uuidof(IMFMediaSource) || riid == __uuidof(IMFMediaSourceEx))
            *ppv = static_cast<IMFMediaSourceEx*>(this);
        else if (riid == __uuidof(IMFGetService))
            *ppv = static_cast<IMFGetService*>(this);
        else if (riid == __uuidof(IKsControlX))
            *ppv = static_cast<IKsControlX*>(this);
        else
        {
            *ppv = nullptr;
            return E_NOINTERFACE;
        }
        AddRef();
        return S_OK;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++_ref; }
    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG r = --_ref;
        if (r == 0) delete this;
        return r;
    }

    // IMFMediaEventGenerator
    STDMETHODIMP GetEvent(DWORD flags, IMFMediaEvent** ev) override
    {
        ComPtr<IMFMediaEventQueue> q;
        {
            std::lock_guard<std::recursive_mutex> lock(_mutex);
            if (_shutdown) return MF_E_SHUTDOWN;
            q = _queue;
        }
        return q->GetEvent(flags, ev);
    }
    STDMETHODIMP BeginGetEvent(IMFAsyncCallback* cb, IUnknown* state) override
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        return _queue->BeginGetEvent(cb, state);
    }
    STDMETHODIMP EndGetEvent(IMFAsyncResult* result, IMFMediaEvent** ev) override
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        return _queue->EndGetEvent(result, ev);
    }
    STDMETHODIMP QueueEvent(MediaEventType type, REFGUID ext, HRESULT status, const PROPVARIANT* value) override
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        return _queue->QueueEventParamVar(type, ext, status, value);
    }

    // IMFMediaSource
    STDMETHODIMP GetCharacteristics(DWORD* characteristics) override
    {
        if (!characteristics) return E_POINTER;
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        *characteristics = MFMEDIASOURCE_IS_LIVE;
        return S_OK;
    }
    STDMETHODIMP CreatePresentationDescriptor(IMFPresentationDescriptor** pd) override
    {
        if (!pd) return E_POINTER;
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        return _descriptor->Clone(pd);
    }
    STDMETHODIMP Start(IMFPresentationDescriptor* pd, const GUID* timeFormat, const PROPVARIANT* startPosition) override
    {
        if (!pd) return E_POINTER;
        if (timeFormat && *timeFormat != GUID_NULL) return MF_E_UNSUPPORTED_TIME_FORMAT;
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;

        PROPVARIANT time;
        PropVariantInit(&time);
        time.vt = VT_I8;
        time.hVal.QuadPart = MFGetSystemTime();

        DWORD count = 0;
        HRESULT hr = pd->GetStreamDescriptorCount(&count);
        for (DWORD i = 0; SUCCEEDED(hr) && i < count; i++)
        {
            BOOL selected = FALSE;
            ComPtr<IMFStreamDescriptor> sd;
            hr = pd->GetStreamDescriptorByIndex(i, &selected, &sd);
            if (FAILED(hr)) break;
            DWORD id = 0;
            sd->GetStreamIdentifier(&id);
            if (id != 0) continue;

            if (selected)
            {
                ComPtr<IMFMediaTypeHandler> handler;
                ComPtr<IMFMediaType> type;
                if (SUCCEEDED(sd->GetMediaTypeHandler(&handler)))
                    handler->GetCurrentMediaType(&type);

                hr = _queue->QueueEventParamUnk(_streamAnnounced ? MEUpdatedStream : MENewStream, GUID_NULL, S_OK,
                                                static_cast<IMFMediaStream2*>(_stream.Get()));
                _streamAnnounced = true;
                if (SUCCEEDED(hr)) hr = _stream->Start(type.Get(), &time);
                _descriptor->SelectStream(0);
            }
            else
            {
                _stream->Stop();
                _descriptor->DeselectStream(0);
            }
        }
        if (SUCCEEDED(hr)) hr = _queue->QueueEventParamVar(MESourceStarted, GUID_NULL, S_OK, &time);
        return hr;
    }
    STDMETHODIMP Stop() override
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        _stream->Stop();
        return _queue->QueueEventParamVar(MESourceStopped, GUID_NULL, S_OK, nullptr);
    }
    STDMETHODIMP Pause() override { return MF_E_INVALID_STATE_TRANSITION; }
    STDMETHODIMP Shutdown() override
    {
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        _shutdown = true;
        if (_stream) _stream->Shutdown();
        if (_queue) _queue->Shutdown();
        Trace(L"source shutdown");
        return S_OK;
    }

    // IMFMediaSourceEx
    STDMETHODIMP GetSourceAttributes(IMFAttributes** attributes) override
    {
        if (!attributes) return E_POINTER;
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        return _attributes.CopyTo(attributes);
    }
    STDMETHODIMP GetStreamAttributes(DWORD streamId, IMFAttributes** attributes) override
    {
        if (!attributes) return E_POINTER;
        std::lock_guard<std::recursive_mutex> lock(_mutex);
        if (_shutdown) return MF_E_SHUTDOWN;
        if (streamId != 0) return MF_E_INVALIDSTREAMNUMBER;
        return _stream->Descriptor()->QueryInterface(IID_PPV_ARGS(attributes));
    }
    STDMETHODIMP SetD3DManager(IUnknown*) override { return S_OK; } // system-memory samples only

    // IMFGetService
    STDMETHODIMP GetService(REFGUID, REFIID, LPVOID*) override { return MF_E_UNSUPPORTED_SERVICE; }

    // IKsControl
    STDMETHODIMP KsProperty(void*, ULONG, void*, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsMethod(void*, ULONG, void*, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }
    STDMETHODIMP KsEvent(void*, ULONG, void*, ULONG, ULONG*) override { return HRESULT_FROM_WIN32(ERROR_SET_NOT_FOUND); }

private:
    ~MediaSource() = default;

    std::atomic<ULONG> _ref{ 1 };
    std::recursive_mutex _mutex;
    ComPtr<IMFAttributes> _attributes;
    ComPtr<IMFMediaEventQueue> _queue;
    ComPtr<IMFPresentationDescriptor> _descriptor;
    ComPtr<MediaStream> _stream;
    bool _shutdown = false;
    bool _streamAnnounced = false;
};

HRESULT MediaStream::Initialize(MediaSource* source)
{
    _source = source;
    HRESULT hr = MFCreateEventQueue(&_queue);

    static const UINT sizes[][2] = { { 1920, 1080 }, { 1280, 720 }, { 1024, 576 }, { 640, 360 } };
    const DWORD typeCount = ARRAYSIZE(sizes);
    IMFMediaType* types[typeCount] = {};
    for (DWORD i = 0; SUCCEEDED(hr) && i < typeCount; i++)
        hr = CreateType(sizes[i][0], sizes[i][1], &types[i]);

    if (SUCCEEDED(hr)) hr = MFCreateStreamDescriptor(0, typeCount, types, &_descriptor);
    if (SUCCEEDED(hr))
    {
        ComPtr<IMFMediaTypeHandler> handler;
        hr = _descriptor->GetMediaTypeHandler(&handler);
        if (SUCCEEDED(hr)) hr = handler->SetCurrentMediaType(types[0]);
    }
    if (SUCCEEDED(hr)) hr = _descriptor->SetGUID(MF_DEVICESTREAM_STREAM_CATEGORY, PINNAME_VIDEO_CAPTURE_);
    if (SUCCEEDED(hr)) hr = _descriptor->SetUINT32(MF_DEVICESTREAM_STREAM_ID, 0);
    if (SUCCEEDED(hr)) hr = _descriptor->SetUINT32(MF_DEVICESTREAM_FRAMESERVER_SHARED, 1);
    if (SUCCEEDED(hr)) hr = _descriptor->SetUINT32(MF_DEVICESTREAM_ATTRIBUTE_FRAMESOURCE_TYPES, MFFrameSourceTypes_Color);

    for (auto t : types)
        if (t) t->Release();
    return hr;
}

STDMETHODIMP MediaStream::GetMediaSource(IMFMediaSource** source)
{
    if (!source) return E_POINTER;
    std::lock_guard<std::recursive_mutex> lock(_mutex);
    if (_shutdown || !_source) return MF_E_SHUTDOWN;
    return _source->QueryInterface(IID_PPV_ARGS(source));
}

// =====================================================================================
// Activator (what Frame Server CoCreates)
// =====================================================================================

class Activator : public IMFActivate
{
public:
    HRESULT Initialize() { return MFCreateAttributes(&_attributes, 4); }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IMFAttributes) || riid == __uuidof(IMFActivate))
        {
            *ppv = static_cast<IMFActivate*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return ++_ref; }
    STDMETHODIMP_(ULONG) Release() override
    {
        ULONG r = --_ref;
        if (r == 0) delete this;
        return r;
    }

    // IMFActivate
    STDMETHODIMP ActivateObject(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        std::lock_guard<std::mutex> lock(_mutex);
        if (!_source)
        {
            ComPtr<MediaSource> source;
            source.Attach(new (std::nothrow) MediaSource());
            if (!source) return E_OUTOFMEMORY;
            HRESULT hr = source->Initialize(_attributes.Get());
            if (FAILED(hr))
            {
                Trace(L"source init failed 0x%08X", hr);
                return hr;
            }
            _source = source;
            Trace(L"source activated");
        }
        return _source->QueryInterface(riid, ppv);
    }
    STDMETHODIMP ShutdownObject() override
    {
        std::lock_guard<std::mutex> lock(_mutex);
        if (_source)
        {
            _source->Shutdown();
            _source.Reset();
        }
        return S_OK;
    }
    STDMETHODIMP DetachObject() override
    {
        std::lock_guard<std::mutex> lock(_mutex);
        _source.Reset();
        return S_OK;
    }

    // IMFAttributes (forwarded)
    STDMETHODIMP GetItem(REFGUID k, PROPVARIANT* v) override { return _attributes->GetItem(k, v); }
    STDMETHODIMP GetItemType(REFGUID k, MF_ATTRIBUTE_TYPE* t) override { return _attributes->GetItemType(k, t); }
    STDMETHODIMP CompareItem(REFGUID k, REFPROPVARIANT v, BOOL* r) override { return _attributes->CompareItem(k, v, r); }
    STDMETHODIMP Compare(IMFAttributes* a, MF_ATTRIBUTES_MATCH_TYPE m, BOOL* r) override { return _attributes->Compare(a, m, r); }
    STDMETHODIMP GetUINT32(REFGUID k, UINT32* v) override { return _attributes->GetUINT32(k, v); }
    STDMETHODIMP GetUINT64(REFGUID k, UINT64* v) override { return _attributes->GetUINT64(k, v); }
    STDMETHODIMP GetDouble(REFGUID k, double* v) override { return _attributes->GetDouble(k, v); }
    STDMETHODIMP GetGUID(REFGUID k, GUID* v) override { return _attributes->GetGUID(k, v); }
    STDMETHODIMP GetStringLength(REFGUID k, UINT32* l) override { return _attributes->GetStringLength(k, l); }
    STDMETHODIMP GetString(REFGUID k, LPWSTR v, UINT32 s, UINT32* l) override { return _attributes->GetString(k, v, s, l); }
    STDMETHODIMP GetAllocatedString(REFGUID k, LPWSTR* v, UINT32* l) override { return _attributes->GetAllocatedString(k, v, l); }
    STDMETHODIMP GetBlobSize(REFGUID k, UINT32* s) override { return _attributes->GetBlobSize(k, s); }
    STDMETHODIMP GetBlob(REFGUID k, UINT8* b, UINT32 s, UINT32* l) override { return _attributes->GetBlob(k, b, s, l); }
    STDMETHODIMP GetAllocatedBlob(REFGUID k, UINT8** b, UINT32* s) override { return _attributes->GetAllocatedBlob(k, b, s); }
    STDMETHODIMP GetUnknown(REFGUID k, REFIID r, LPVOID* p) override { return _attributes->GetUnknown(k, r, p); }
    STDMETHODIMP SetItem(REFGUID k, REFPROPVARIANT v) override { return _attributes->SetItem(k, v); }
    STDMETHODIMP DeleteItem(REFGUID k) override { return _attributes->DeleteItem(k); }
    STDMETHODIMP DeleteAllItems() override { return _attributes->DeleteAllItems(); }
    STDMETHODIMP SetUINT32(REFGUID k, UINT32 v) override { return _attributes->SetUINT32(k, v); }
    STDMETHODIMP SetUINT64(REFGUID k, UINT64 v) override { return _attributes->SetUINT64(k, v); }
    STDMETHODIMP SetDouble(REFGUID k, double v) override { return _attributes->SetDouble(k, v); }
    STDMETHODIMP SetGUID(REFGUID k, REFGUID v) override { return _attributes->SetGUID(k, v); }
    STDMETHODIMP SetString(REFGUID k, LPCWSTR v) override { return _attributes->SetString(k, v); }
    STDMETHODIMP SetBlob(REFGUID k, const UINT8* b, UINT32 s) override { return _attributes->SetBlob(k, b, s); }
    STDMETHODIMP SetUnknown(REFGUID k, IUnknown* u) override { return _attributes->SetUnknown(k, u); }
    STDMETHODIMP LockStore() override { return _attributes->LockStore(); }
    STDMETHODIMP UnlockStore() override { return _attributes->UnlockStore(); }
    STDMETHODIMP GetCount(UINT32* c) override { return _attributes->GetCount(c); }
    STDMETHODIMP GetItemByIndex(UINT32 i, GUID* k, PROPVARIANT* v) override { return _attributes->GetItemByIndex(i, k, v); }
    STDMETHODIMP CopyAllItems(IMFAttributes* d) override { return _attributes->CopyAllItems(d); }

private:
    ~Activator() = default;
    std::atomic<ULONG> _ref{ 1 };
    std::mutex _mutex;
    ComPtr<IMFAttributes> _attributes;
    ComPtr<MediaSource> _source;
};

// =====================================================================================
// Class factory & DLL exports
// =====================================================================================

class ClassFactory : public IClassFactory
{
public:
    STDMETHODIMP QueryInterface(REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        if (riid == __uuidof(IUnknown) || riid == __uuidof(IClassFactory))
        {
            *ppv = static_cast<IClassFactory*>(this);
            AddRef();
            return S_OK;
        }
        *ppv = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return 2; }  // static instance
    STDMETHODIMP_(ULONG) Release() override { return 1; }
    STDMETHODIMP CreateInstance(IUnknown* outer, REFIID riid, void** ppv) override
    {
        if (!ppv) return E_POINTER;
        *ppv = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        ComPtr<Activator> activator;
        activator.Attach(new (std::nothrow) Activator());
        if (!activator) return E_OUTOFMEMORY;
        HRESULT hr = activator->Initialize();
        if (FAILED(hr)) return hr;
        return activator->QueryInterface(riid, ppv);
    }
    STDMETHODIMP LockServer(BOOL) override { return S_OK; }
};

static ClassFactory g_factory;

static std::wstring ClsidString()
{
    wchar_t buf[64];
    StringFromGUID2(CLSID_OpenM50VCam, buf, ARRAYSIZE(buf));
    return buf;
}

BOOL APIENTRY DllMain(HMODULE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        g_module = module;
        DisableThreadLibraryCalls(module);
    }
    return TRUE;
}

STDAPI DllGetClassObject(REFCLSID rclsid, REFIID riid, LPVOID* ppv)
{
    if (rclsid != CLSID_OpenM50VCam) return CLASS_E_CLASSNOTAVAILABLE;
    return g_factory.QueryInterface(riid, ppv);
}

STDAPI DllCanUnloadNow()
{
    return S_FALSE; // the frame grabber thread lives for the lifetime of the process
}

STDAPI DllRegisterServer()
{
    wchar_t path[MAX_PATH];
    if (!GetModuleFileNameW(g_module, path, MAX_PATH)) return HRESULT_FROM_WIN32(GetLastError());

    std::wstring key = L"Software\\Classes\\CLSID\\" + ClsidString();
    HKEY hkey = nullptr;
    LSTATUS st = RegCreateKeyExW(HKEY_LOCAL_MACHINE, key.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &hkey, nullptr);
    if (st != ERROR_SUCCESS) return HRESULT_FROM_WIN32(st);
    RegSetValueExW(hkey, nullptr, 0, REG_SZ, (const BYTE*)kFriendlyName, (DWORD)((wcslen(kFriendlyName) + 1) * sizeof(wchar_t)));
    RegCloseKey(hkey);

    key += L"\\InprocServer32";
    st = RegCreateKeyExW(HKEY_LOCAL_MACHINE, key.c_str(), 0, nullptr, 0, KEY_WRITE, nullptr, &hkey, nullptr);
    if (st != ERROR_SUCCESS) return HRESULT_FROM_WIN32(st);
    RegSetValueExW(hkey, nullptr, 0, REG_SZ, (const BYTE*)path, (DWORD)((wcslen(path) + 1) * sizeof(wchar_t)));
    const wchar_t* model = L"Both";
    RegSetValueExW(hkey, L"ThreadingModel", 0, REG_SZ, (const BYTE*)model, (DWORD)((wcslen(model) + 1) * sizeof(wchar_t)));
    RegCloseKey(hkey);
    return S_OK;
}

STDAPI DllUnregisterServer()
{
    std::wstring key = L"Software\\Classes\\CLSID\\" + ClsidString();
    LSTATUS st = RegDeleteTreeW(HKEY_LOCAL_MACHINE, key.c_str());
    return (st == ERROR_SUCCESS || st == ERROR_FILE_NOT_FOUND) ? S_OK : HRESULT_FROM_WIN32(st);
}

// ---- helpers for OpenM50Webcam.exe -----------------------------------------------------

extern "C" HRESULT __stdcall OpenM50_CreateVirtualCamera(void** handle)
{
    if (!handle) return E_POINTER;
    *handle = nullptr;
    CoInitializeEx(nullptr, COINIT_MULTITHREADED); // ok if already initialized
    HRESULT hr = MFStartup(MF_VERSION);
    if (FAILED(hr)) return hr;

    std::wstring clsid = ClsidString();
    IMFVirtualCamera* camera = nullptr;
    hr = MFCreateVirtualCamera(MFVirtualCameraType_SoftwareCameraSource, MFVirtualCameraLifetime_Session,
                               MFVirtualCameraAccess_CurrentUser, kFriendlyName, clsid.c_str(), nullptr, 0, &camera);
    if (SUCCEEDED(hr))
    {
        // Note: Windows always appends "(Windows Virtual Camera)" (localized) to the name of
        // virtual cameras so users can tell them apart from hardware; this cannot be overridden.
        hr = camera->Start(nullptr);
        if (FAILED(hr))
        {
            camera->Shutdown();
            camera->Release();
            camera = nullptr;
        }
    }
    if (FAILED(hr))
    {
        MFShutdown();
        return hr;
    }
    *handle = camera;
    return S_OK;
}

extern "C" HRESULT __stdcall OpenM50_RemoveVirtualCamera(void* handle)
{
    if (!handle) return S_OK;
    auto camera = static_cast<IMFVirtualCamera*>(handle);
    camera->Stop();
    HRESULT hr = camera->Remove();
    camera->Shutdown();
    camera->Release();
    MFShutdown();
    return hr;
}
