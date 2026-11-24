using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace OpenM50Webcam.Gui;

public partial class MainWindow : Window
{
    private static readonly Brush Green = Freeze(new SolidColorBrush(Color.FromRgb(0x2E, 0xC4, 0xB6)));
    private static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xF4, 0xA2, 0x59)));
    private static readonly Brush Red = Freeze(new SolidColorBrush(Color.FromRgb(0xEF, 0x6F, 0x6C)));
    private static readonly Brush Grey = Freeze(new SolidColorBrush(Color.FromRgb(0x5A, 0x62, 0x70)));

    private readonly Engine _engine;
    private readonly DispatcherTimer _previewTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly bool _eosUtilityInstalled;
    private long _lastPreviewSequence;
    private bool _quitting;

    internal MainWindow(Engine engine)
    {
        InitializeComponent();
        _engine = engine;
        _eosUtilityInstalled = IsEosWebcamUtilityInstalled();

        string version = typeof(MainWindow).Assembly.GetName().Version?.ToString(3) ?? "";
        FooterText.Text = $"v{version}  ·  Not affiliated with Canon";

        _previewTimer = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(66) };
        _previewTimer.Tick += (_, _) => UpdatePreview();
        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _statusTimer.Tick += (_, _) => UpdateStatus();
        Loaded += (_, _) =>
        {
            UpdateStatus();
            _previewTimer.Start();
            _statusTimer.Start();
        };
    }

    // ------------------------------------------------------------------ preview

    private void UpdatePreview()
    {
        bool streaming = _engine.Camera == CameraStatus.Streaming && _engine.Hub.MillisecondsSinceLastFrame < 1500;
        var frame = _engine.Hub.Latest;

        if (frame is not null && frame.Sequence != _lastPreviewSequence && WindowState != WindowState.Minimized)
        {
            _lastPreviewSequence = frame.Sequence;
            try
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 720; // ~2x the preview width for sharp HiDPI rendering
                bmp.StreamSource = new MemoryStream(frame.Jpeg);
                bmp.EndInit();
                bmp.Freeze();
                PreviewImage.Background = new ImageBrush(bmp) { Stretch = Stretch.Uniform };
            }
            catch { /* corrupt frame, skip */ }
        }

        PreviewImage.Opacity = streaming ? 1.0 : 0.18;
        PreviewOverlay.Visibility = streaming ? Visibility.Collapsed : Visibility.Visible;
        FpsBadge.Visibility = streaming ? Visibility.Visible : Visibility.Collapsed;
        if (streaming && frame is not null)
            FpsText.Text = string.Create(CultureInfo.InvariantCulture, $"{frame.Width}×{frame.Height} · {_engine.Hub.Fps:0} fps");
    }

    // ------------------------------------------------------------------ status rows

    private void UpdateStatus()
    {
        var frame = _engine.Hub.Latest;
        string cameraName = _engine.CameraName ?? "Camera";

        // Camera
        switch (_engine.Camera)
        {
            case CameraStatus.Streaming:
                CameraDot.Fill = Green;
                CameraTitle.Text = $"{cameraName} connected";
                CameraDetail.Text = frame is null ? "Live view running"
                    : string.Create(CultureInfo.InvariantCulture, $"Live view · {frame.Width}×{frame.Height} · {_engine.Hub.Fps:0} fps");
                PreviewText.Text = "Waiting for image …";
                break;
            case CameraStatus.Connecting:
                CameraDot.Fill = Amber;
                CameraTitle.Text = $"Connecting to {cameraName} …";
                CameraDetail.Text = "Starting live view";
                PreviewText.Text = "Connecting …";
                break;
            case CameraStatus.Error:
                CameraDot.Fill = Red;
                CameraTitle.Text = "Camera is not responding";
                CameraDetail.Text = "Reconnecting automatically. If this persists, switch the camera off and on again.";
                PreviewText.Text = "Camera is not responding";
                break;
            default:
                CameraDot.Fill = Grey;
                CameraTitle.Text = "No camera connected";
                CameraDetail.Text = "Connect the EOS M50 via USB and switch it on. Wi-Fi must be off.";
                PreviewText.Text = "No camera connected";
                break;
        }

        // Clients of the MJPEG server
        var clients = _engine.Server.StreamClients;
        bool webcamInUse = clients.Any(c => c.IsVirtualCamera);
        var streamApps = clients.Where(c => !c.IsVirtualCamera)
            .Select(c => ClientApps.ProcessNameForConnection(c.RemotePort, _engine.Port) ?? "Unknown app")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        // Virtual webcam
        switch (_engine.VirtualCamera)
        {
            case VirtualCameraStatus.Active when webcamInUse:
                var apps = SafeAppsUsingCamera();
                WebcamDot.Fill = Green;
                WebcamTitle.Text = apps.Count > 0 ? $"Webcam in use by {JoinNames(apps)}" : "Webcam in use";
                WebcamDetail.Text = "Device “OpenM50 Webcam”";
                break;
            case VirtualCameraStatus.Active:
                WebcamDot.Fill = Grey;
                WebcamTitle.Text = "Webcam ready";
                WebcamDetail.Text = "Select “OpenM50 Webcam” in Zoom, Teams, Discord, your browser …";
                break;
            case VirtualCameraStatus.Starting:
                WebcamDot.Fill = Amber;
                WebcamTitle.Text = "Starting webcam …";
                WebcamDetail.Text = "";
                break;
            case VirtualCameraStatus.NotSupported:
                WebcamDot.Fill = Grey;
                WebcamTitle.Text = "Webcam device needs Windows 11";
                WebcamDetail.Text = "Use the OBS stream below instead.";
                break;
            case VirtualCameraStatus.NotInstalled:
                WebcamDot.Fill = Amber;
                WebcamTitle.Text = "Webcam device not installed";
                WebcamDetail.Text = "Run the OpenM50 Webcam installer to add it.";
                break;
            case VirtualCameraStatus.Disabled:
                WebcamDot.Fill = Grey;
                WebcamTitle.Text = "Webcam device disabled";
                WebcamDetail.Text = "Started with --no-vcam.";
                break;
            default:
                WebcamDot.Fill = Red;
                WebcamTitle.Text = "Webcam device failed";
                WebcamDetail.Text = _engine.VirtualCameraError ?? "";
                break;
        }

        // OBS / MJPEG stream
        string url = $"http://localhost:{_engine.Port}/";
        if (streamApps.Count > 0)
        {
            StreamDot.Fill = Green;
            StreamTitle.Text = $"Stream in use by {JoinNames(streamApps)}";
        }
        else
        {
            StreamDot.Fill = Grey;
            StreamTitle.Text = "OBS stream ready";
        }
        StreamDetail.Text = url;

        // Warning banner
        string? warning = null;
        if (_eosUtilityInstalled)
            warning = "Canon EOS Webcam Utility is installed. It can grab the M50 and block the live view. " +
                      "Uninstalling it is recommended.";
        WarningText.Text = warning ?? "";
        WarningBanner.Visibility = warning is null ? Visibility.Collapsed : Visibility.Visible;
    }

    private static List<string> SafeAppsUsingCamera()
    {
        try { return ClientApps.AppsUsingCameraNow(); }
        catch { return new List<string>(); }
    }

    private static string JoinNames(IReadOnlyList<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        _ => $"{names[0]}, {names[1]} and {names.Count - 2} more",
    };

    private static bool IsEosWebcamUtilityInstalled()
    {
        using var key = Registry.ClassesRoot.OpenSubKey(@"CLSID\{9812588D-5CE9-4E4C-ABC1-049138D10DCE}");
        return key is not null;
    }

    // ------------------------------------------------------------------ window chrome & quit

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void CopyUrl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText($"http://localhost:{_engine.Port}/");
            CopyButton.Content = "Copied";
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            t.Tick += (_, _) => { CopyButton.Content = "Copy URL"; t.Stop(); };
            t.Start();
        }
        catch { /* clipboard busy */ }
    }

    private async void Quit_Click(object sender, RoutedEventArgs e) => await QuitAsync();

    private async Task QuitAsync()
    {
        if (_quitting) return;
        _quitting = true;
        QuitButton.IsEnabled = false;
        QuitButton.Content = "Stopping …";
        _previewTimer.Stop();
        _statusTimer.Stop();
        await Task.Run(_engine.Stop); // turns the camera screen back on, removes the webcam device
        Application.Current.Shutdown();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        // Alt+F4 etc.: quit cleanly instead of just closing the window.
        if (!_quitting)
        {
            e.Cancel = true;
            _ = QuitAsync();
        }
        base.OnClosing(e);
    }

    /// <summary>Brings the window to the front (called when the app is started a second time).</summary>
    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private static Brush Freeze(Brush b)
    {
        b.Freeze();
        return b;
    }
}
