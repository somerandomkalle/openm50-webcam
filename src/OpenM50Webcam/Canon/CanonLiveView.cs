using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using OpenM50Webcam.Wpd;

namespace OpenM50Webcam.Canon;

/// <summary>
/// Canon EOS PTP live view ("EVF") over USB.
/// Opcodes and property codes as documented by the libgphoto2 project (camlibs/ptp2/ptp.h).
/// </summary>
internal sealed class CanonLiveView : IDisposable
{
    // Canon EOS vendor operations
    private const ushort OpSetDevicePropValueEx = 0x9110;
    private const ushort OpSetRemoteMode = 0x9114;
    private const ushort OpSetEventMode = 0x9115;
    private const ushort OpGetEvent = 0x9116;
    private const ushort OpKeepDeviceOn = 0x911D;
    private const ushort OpGetViewFinderData = 0x9153;

    // Canon EOS device properties
    private const uint PropEvfOutputDevice = 0xD1B0;
    private const uint PropEvfMode = 0xD1B3;

    // EVF output device flags
    public const uint EvfOutputCameraScreen = 1;
    public const uint EvfOutputPc = 2;

    // Response codes
    private const ushort RcOk = 0x2001;
    private const ushort RcDeviceBusy = 0x2019;
    private const ushort RcCanonNotReady = 0xA102;

    private readonly MtpDevice _dev;
    private readonly HashSet<ushort> _vendorOps;
    private bool _started;

    public CanonLiveView(MtpDevice device)
    {
        _dev = device;
        try { _vendorOps = device.GetSupportedVendorOpcodes(); }
        catch (COMException) { _vendorOps = new HashSet<ushort>(); }

        if (_vendorOps.Count > 0 && !_vendorOps.Contains(OpGetViewFinderData))
            throw new NotSupportedException(
                $"'{device.Info.Name}' does not report Canon EOS live view support (opcode 0x9153 missing). " +
                "Is this really a Canon EOS camera?");
    }

    public void Start(bool keepCameraScreen, Action<string> log)
    {
        Check(_dev.Execute(OpSetRemoteMode, 1), "SetRemoteMode");
        Check(_dev.Execute(OpSetEventMode, 1), "SetEventMode");
        DrainEvents();

        // Switch the output to the camera screen first: if a previous session was killed while
        // streaming, the camera is still set to "PC" and setting the same value again does nothing.
        SetProperty(PropEvfOutputDevice, EvfOutputCameraScreen);
        uint output = EvfOutputPc | (keepCameraScreen ? EvfOutputCameraScreen : 0);
        Check(SetProperty(PropEvfOutputDevice, output), "EVFOutputDevice");
        _started = true;
        log($"Live view started (output device = {output}).");
    }

    /// <summary>
    /// Runs the frame loop until cancelled. Calls <paramref name="onFrame"/> with each JPEG frame.
    /// </summary>
    public void Run(Action<byte[]> onFrame, double maxFps, Action<string> log, CancellationToken ct)
    {
        var lastEvents = Stopwatch.StartNew();
        var lastKeepAlive = Stopwatch.StartNew();
        var notReadySince = Stopwatch.StartNew();
        bool triedEvfMode = false;
        double minFrameMs = maxFps > 0 ? 1000.0 / maxFps : 0;
        var frameTimer = Stopwatch.StartNew();
        int consecutiveErrors = 0;

        while (!ct.IsCancellationRequested)
        {
            if (minFrameMs > 0)
            {
                double wait = minFrameMs - frameTimer.Elapsed.TotalMilliseconds;
                if (wait > 1) Thread.Sleep((int)wait);
            }
            frameTimer.Restart();

            MtpResponse resp;
            byte[] data;
            try
            {
                resp = _dev.ExecuteRead(OpGetViewFinderData, out data, 0x00200000, 0, 0);
                consecutiveErrors = 0;
            }
            catch (COMException ex)
            {
                // The driver may surface "not ready" as an HRESULT failure. Retry a few times.
                if (++consecutiveErrors > 50) throw;
                if (consecutiveErrors == 1) log($"GetViewFinderData failed (0x{ex.HResult:X8}), retrying ...");
                Thread.Sleep(30);
                continue;
            }

            if (resp.Code == RcOk && data.Length > 0)
            {
                var jpeg = ExtractJpeg(data);
                if (jpeg is not null)
                {
                    onFrame(jpeg);
                    notReadySince.Restart();
                }
            }
            else if (resp.Code is RcCanonNotReady or RcDeviceBusy or RcOk)
            {
                // Watchdog: no image for several seconds -> let the caller reconnect.
                if (notReadySince.Elapsed > TimeSpan.FromSeconds(6))
                    throw new NoImageException();
                Thread.Sleep(15);
                // Some bodies only start streaming once EVFMode is enabled explicitly.
                if (!triedEvfMode && notReadySince.Elapsed > TimeSpan.FromSeconds(3))
                {
                    triedEvfMode = true;
                    log("No frames yet, enabling EVF mode explicitly ...");
                    SetProperty(PropEvfMode, 1);
                }
            }
            else
            {
                throw new IOException($"GetViewFinderData returned response {resp}.");
            }

            if (lastEvents.ElapsedMilliseconds > 500)
            {
                DrainEvents();
                lastEvents.Restart();
            }

            if (lastKeepAlive.Elapsed > TimeSpan.FromSeconds(20))
            {
                lastKeepAlive.Restart();
                if (_vendorOps.Count == 0 || _vendorOps.Contains(OpKeepDeviceOn))
                {
                    try { _dev.Execute(OpKeepDeviceOn); } catch { /* optional */ }
                }
            }
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        try { SetProperty(PropEvfOutputDevice, EvfOutputCameraScreen); } catch { }
        try { DrainEvents(); } catch { }
        try { _dev.Execute(OpSetEventMode, 0); } catch { }
        try { _dev.Execute(OpSetRemoteMode, 0); } catch { }
    }

    private MtpResponse SetProperty(uint prop, uint value)
    {
        var payload = new byte[12];
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(0), 12);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(4), prop);
        BinaryPrimitives.WriteUInt32LittleEndian(payload.AsSpan(8), value);
        var resp = _dev.ExecuteWrite(OpSetDevicePropValueEx, payload);
        DrainEvents();
        return resp;
    }

    private void DrainEvents()
    {
        // The camera queues property-change events; they must be fetched regularly.
        try { _dev.ExecuteRead(OpGetEvent, out _); } catch (COMException) { }
    }

    private static void Check(MtpResponse resp, string what)
    {
        if (resp.Code != RcOk)
            throw new IOException($"{what} failed with response {resp}.");
    }

    /// <summary>
    /// The viewfinder data is a list of records: [u32 length][u32 type][payload]. Type 1 is the JPEG.
    /// Falls back to scanning for JPEG SOI/EOI markers.
    /// </summary>
    internal static byte[]? ExtractJpeg(byte[] data)
    {
        int off = 0;
        while (off + 8 <= data.Length)
        {
            uint len = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off));
            uint type = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off + 4));
            if (len < 8 || off + (long)len > data.Length) break;
            if (type == 1 && len > 10 && data[off + 8] == 0xFF && data[off + 9] == 0xD8)
                return data.AsSpan(off + 8, (int)len - 8).ToArray();
            off += (int)len;
        }

        int start = data.AsSpan().IndexOf(new byte[] { 0xFF, 0xD8, 0xFF });
        if (start < 0) return null;
        int end = data.AsSpan().LastIndexOf(new byte[] { 0xFF, 0xD9 });
        if (end <= start) return null;
        return data.AsSpan(start, end + 2 - start).ToArray();
    }

    public void Dispose() => Stop();
}

/// <summary>The camera is connected but sends no live view image (often: another program uses it).</summary>
internal sealed class NoImageException : IOException
{
    public NoImageException()
        : base("The camera sends no image. Another program (e.g. Canon EOS Webcam Utility) may be using it.") { }
}
