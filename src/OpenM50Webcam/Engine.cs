using System.Net;
using OpenM50Webcam.Canon;
using OpenM50Webcam.Server;
using OpenM50Webcam.Wpd;

namespace OpenM50Webcam;

internal sealed class EngineOptions
{
    public int Port = 8090;
    public double MaxFps;          // 0 = as fast as the camera delivers
    public bool KeepCameraScreen;
    public bool Lan;
    public bool NoVirtualCamera;
    public string? DeviceFilter;
}

internal enum CameraStatus { Searching, Connecting, Streaming, Error }

internal enum VirtualCameraStatus { Starting, Active, Disabled, NotSupported, NotInstalled, Failed }

/// <summary>
/// Everything that runs while the app is open: camera thread, MJPEG server and virtual camera.
/// Used by both the window (GUI) and the --headless console mode.
/// </summary>
internal sealed class Engine : IDisposable
{
    private readonly EngineOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _logLock = new();
    private readonly List<string> _log = new();
    private Thread? _cameraThread;
    private Task? _serverTask;
    private VirtualCamera? _virtualCamera;
    private bool _stopped;

    public Engine(EngineOptions options)
    {
        _options = options;
        Hub = new FrameHub();
        var address = options.Lan ? IPAddress.Any : IPAddress.Loopback;
        Server = new MjpegServer(Hub, address, options.Port, () => CameraStateText);
    }

    public FrameHub Hub { get; }
    public MjpegServer Server { get; }
    public int Port => _options.Port;

    private volatile CameraStatus _camera = CameraStatus.Searching;
    public CameraStatus Camera => _camera;
    public string? CameraName { get; private set; }
    public string? LastError { get; private set; }
    public VirtualCameraStatus VirtualCamera { get; private set; } = VirtualCameraStatus.Starting;
    public string? VirtualCameraError { get; private set; }

    public event Action<string>? LogMessage;

    public IReadOnlyList<string> RecentLog
    {
        get { lock (_logLock) return _log.ToArray(); }
    }

    /// <summary>Text used by the /status endpoint (machine-readable state is derived from it).</summary>
    private string CameraStateText => _camera switch
    {
        CameraStatus.Searching => "waiting for camera",
        CameraStatus.Connecting => "connecting",
        CameraStatus.Error => "error, retrying",
        _ => CameraName ?? "camera",
    };

    /// <summary>Starts server, virtual camera and camera thread. Throws if the HTTP port is in use.</summary>
    public void Start()
    {
        _serverTask = Server.RunAsync(_cts.Token);
        if (_serverTask.IsFaulted) _serverTask.GetAwaiter().GetResult(); // port in use etc.

        if (_options.NoVirtualCamera)
        {
            VirtualCamera = VirtualCameraStatus.Disabled;
        }
        else
        {
            // Media Foundation virtual camera APIs want an MTA thread.
            RunOnMtaThread(() =>
            {
                _virtualCamera = OpenM50Webcam.VirtualCamera.TryCreate(Log, out var status, out var error);
                VirtualCamera = status;
                VirtualCameraError = error;
            });
        }

        _cameraThread = new Thread(CameraLoop) { IsBackground = true, Name = "Camera" };
        _cameraThread.SetApartmentState(ApartmentState.MTA);
        _cameraThread.Start();
    }

    /// <summary>Stops the camera cleanly (turns the camera screen back on) and removes the virtual camera.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        Log("Stopping ...");
        _cts.Cancel();
        _cameraThread?.Join(TimeSpan.FromSeconds(5));
        if (_virtualCamera is not null)
        {
            var vc = _virtualCamera;
            _virtualCamera = null;
            RunOnMtaThread(vc.Dispose);
        }
        try { _serverTask?.Wait(TimeSpan.FromSeconds(2)); } catch { /* cancelled */ }
    }

    public void Dispose() => Stop();

    private void CameraLoop()
    {
        var ct = _cts.Token;
        // Creating the virtual camera makes Windows re-enumerate cameras; other camera software
        // (e.g. Canon EOS Webcam Utility) may briefly grab the M50. Give that a moment to settle.
        if (_virtualCamera is not null) ct.WaitHandle.WaitOne(1500);

        bool reportedMissing = false;
        while (!ct.IsCancellationRequested)
        {
            WpdDeviceInfo? info = null;
            try { info = CameraFinder.Find(_options.DeviceFilter); }
            catch (Exception ex) { Log("Device enumeration failed: " + ex.Message); }

            if (info is null)
            {
                _camera = CameraStatus.Searching;
                CameraName = null;
                if (!reportedMissing)
                {
                    Log("No Canon camera found. Connect the M50 via USB and switch it on (Wi-Fi off).");
                    reportedMissing = true;
                }
                Hub.MarkIdle();
                ct.WaitHandle.WaitOne(1500);
                continue;
            }

            reportedMissing = false;
            try
            {
                _camera = CameraStatus.Connecting;
                CameraName = info.Name;
                Log($"Connecting to {info.Name} ...");
                using var device = MtpDevice.Open(info);
                using var liveView = new CanonLiveView(device);
                liveView.Start(_options.KeepCameraScreen, Log);
                liveView.Run(jpeg =>
                {
                    Hub.Publish(jpeg);
                    if (_camera != CameraStatus.Streaming)
                    {
                        _camera = CameraStatus.Streaming;
                        LastError = null;
                    }
                }, _options.MaxFps, Log, ct);
            }
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                _camera = CameraStatus.Error;
                LastError = ex.Message;
                Log($"Camera error: {ex.Message}");
                Hub.MarkIdle();
                ct.WaitHandle.WaitOne(2000);
            }
        }
    }

    public void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        lock (_logLock)
        {
            _log.Add(line);
            if (_log.Count > 200) _log.RemoveAt(0);
        }
        LogMessage?.Invoke(line);
    }

    internal static void RunOnMtaThread(Action action)
    {
        Exception? error = null;
        var t = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { error = ex; }
        });
        t.SetApartmentState(ApartmentState.MTA);
        t.Start();
        t.Join();
        if (error is not null) throw new InvalidOperationException(error.Message, error);
    }
}

internal static class CameraFinder
{
    public static WpdDeviceInfo? Find(string? filter)
    {
        var devices = MtpDevice.Enumerate();
        if (!string.IsNullOrWhiteSpace(filter))
            return devices.FirstOrDefault(d =>
                d.Name.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
                d.Id.Contains(filter, StringComparison.OrdinalIgnoreCase));

        return devices.FirstOrDefault(d => d.Manufacturer.Contains("Canon", StringComparison.OrdinalIgnoreCase))
            ?? devices.FirstOrDefault(d =>
                d.Name.Contains("Canon", StringComparison.OrdinalIgnoreCase) ||
                d.Name.Contains("EOS", StringComparison.OrdinalIgnoreCase) ||
                d.Id.Contains("vid_04a9", StringComparison.OrdinalIgnoreCase)); // Canon USB vendor id
    }
}
