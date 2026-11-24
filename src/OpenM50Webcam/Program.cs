using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using OpenM50Webcam.Canon;
using OpenM50Webcam.Gui;
using OpenM50Webcam.Server;
using OpenM50Webcam.Wpd;

namespace OpenM50Webcam;

internal static class Program
{
    private sealed class Options
    {
        public EngineOptions Engine = new();
        public bool List;
        public bool Probe;
        public bool Headless;
        public bool Help;
        public bool ConsoleMode => List || Probe || Headless || Help;
    }

    private const string MutexName = @"Local\OpenM50Webcam.Camera";
    private const string ActivateEventName = @"Local\OpenM50Webcam.Activate";

    [DllImport("kernel32.dll")]
    private static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll")]
    private static extern bool AllocConsole();

    [STAThread]
    private static int Main(string[] args)
    {
        Options opt;
        try
        {
            opt = Parse(args);
        }
        catch (Exception ex)
        {
            OpenConsole();
            Console.Error.WriteLine(ex.Message);
            PrintHelp();
            return 2;
        }

        if (opt.ConsoleMode)
        {
            OpenConsole();
            if (opt.Help) { PrintHelp(); return 0; }
            // WPD and Media Foundation calls want an MTA thread.
            int code = 0;
            Engine.RunOnMtaThread(() => code = RunConsole(opt));
            return code;
        }

        return RunGui(opt);
    }

    // ------------------------------------------------------------------ GUI

    private static int RunGui(Options opt)
    {
        using var mutex = new Mutex(true, MutexName, out bool isOnlyInstance);
        using var activate = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        if (!isOnlyInstance)
        {
            activate.Set(); // bring the running instance to the front
            return 0;
        }

        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var engine = new Engine(opt.Engine);
        try
        {
            engine.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"OpenM50 Webcam could not start:\n\n{ex.Message}\n\nIs port {opt.Engine.Port} used by another program?",
                "OpenM50 Webcam", MessageBoxButton.OK, MessageBoxImage.Error);
            engine.Stop();
            return 1;
        }

        var window = new MainWindow(engine);
        app.SessionEnding += (_, _) => engine.Stop();
        app.DispatcherUnhandledException += (_, e) =>
        {
            engine.Log("UI error: " + e.Exception.Message);
            e.Handled = true;
        };

        // Second start of the app -> show this window.
        var listener = new Thread(() =>
        {
            while (activate.WaitOne())
                app.Dispatcher.BeginInvoke(window.BringToFront);
        }) { IsBackground = true, Name = "Activate" };
        listener.Start();

        window.Show();
        app.Run();
        engine.Stop();
        return 0;
    }

    // ------------------------------------------------------------------ console modes

    private static void OpenConsole()
    {
        if (!AttachConsole(-1)) AllocConsole();
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine();
    }

    private static int RunConsole(Options opt)
    {
        if (opt.List) return ListDevices();

        // Two instances talking to the camera at once make both slow and flaky.
        using var mutex = new Mutex(true, MutexName, out bool isOnlyInstance);
        if (!isOnlyInstance)
        {
            Console.Error.WriteLine("OpenM50 Webcam is already running. Quit the other instance first.");
            return 3;
        }

        return opt.Probe ? Probe(opt) : RunHeadless(opt);
    }

    private static int RunHeadless(Options opt)
    {
        Console.WriteLine("OpenM50 Webcam " + typeof(Program).Assembly.GetName().Version?.ToString(3) + " (headless)");
        Console.WriteLine("Not affiliated with Canon. Press Ctrl+C to stop.\n");

        using var engine = new Engine(opt.Engine);
        engine.LogMessage += line => Console.WriteLine(line);
        try { engine.Start(); }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Could not start: {ex.Message}");
            return 1;
        }

        Console.WriteLine($"OBS Browser source: http://localhost:{opt.Engine.Port}/  (1920x1080)");
        Console.WriteLine($"OBS Media source:   http://localhost:{opt.Engine.Port}/stream.mjpg\n");

        using var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        done.Wait();
        engine.Stop();
        return 0;
    }

    private static int ListDevices()
    {
        var devices = MtpDevice.Enumerate();
        if (devices.Count == 0)
        {
            Console.WriteLine("No portable (WPD) devices found.");
            return 1;
        }
        foreach (var d in devices)
        {
            Console.WriteLine($"{d.Name}  |  {d.Manufacturer}  |  {d.Description}");
            Console.WriteLine($"    {d.Id}");
        }
        return 0;
    }

    private static int Probe(Options opt)
    {
        void Log(string m) => Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {m}");

        var info = CameraFinder.Find(opt.Engine.DeviceFilter);
        if (info is null)
        {
            Console.WriteLine("No Canon camera found. Use --list to see all devices.");
            return 1;
        }
        Console.WriteLine($"Camera: {info.Name} ({info.Manufacturer})");
        using var device = MtpDevice.Open(info);
        var ops = device.GetSupportedVendorOpcodes();
        Console.WriteLine($"Vendor opcodes reported: {ops.Count}");

        using var liveView = new CanonLiveView(device);
        liveView.Start(opt.Engine.KeepCameraScreen, Log);

        var hub = new FrameHub();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        int frames = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            liveView.Run(jpeg =>
            {
                frames++;
                hub.Publish(jpeg);
            }, 0, Log, cts.Token);
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
        }
        liveView.Stop();

        var f = hub.Latest;
        if (f is null)
        {
            Console.WriteLine("No frame received.");
            return 1;
        }
        string file = Path.GetFullPath("probe.jpg");
        File.WriteAllBytes(file, f.Jpeg);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Received {frames} frames in {sw.Elapsed.TotalSeconds:0.0}s ({frames / sw.Elapsed.TotalSeconds:0.0} fps), size {f.Width}x{f.Height}."));
        Console.WriteLine($"Last frame saved to {file}");
        return 0;
    }

    // ------------------------------------------------------------------ arguments

    private static Options Parse(string[] args)
    {
        var o = new Options();
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {a}");
            switch (a.ToLowerInvariant())
            {
                case "--port": o.Engine.Port = int.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--fps": o.Engine.MaxFps = double.Parse(Next(), CultureInfo.InvariantCulture); break;
                case "--keep-screen": o.Engine.KeepCameraScreen = true; break;
                case "--lan": o.Engine.Lan = true; break;
                case "--no-vcam": o.Engine.NoVirtualCamera = true; break;
                case "--device": o.Engine.DeviceFilter = Next(); break;
                case "--headless": o.Headless = true; break;
                case "--list": o.List = true; break;
                case "--probe": o.Probe = true; break;
                case "-h": case "--help": case "/?": o.Help = true; break;
                default: throw new ArgumentException($"Unknown option: {a}");
            }
        }
        return o;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            OpenM50 Webcam - Canon EOS M50 as a USB webcam

            Usage: OpenM50Webcam [options]        (no options: open the app window)

              --headless        run without window, log to the console (Ctrl+C to stop)
              --port <n>        HTTP port (default 8090)
              --fps <n>         limit frame rate (default: as fast as the camera delivers)
              --keep-screen     keep the camera's own screen on while streaming
              --lan             listen on all network interfaces (default: localhost only)
              --no-vcam         do not create the "OpenM50 Webcam" virtual camera device
              --device <text>   pick a device whose name/id contains <text>
              --list            list all portable (WPD) devices and exit
              --probe           grab frames for a few seconds, report fps, save probe.jpg
              -h, --help        show this help
            """);
    }
}
