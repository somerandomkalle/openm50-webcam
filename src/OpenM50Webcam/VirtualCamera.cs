using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace OpenM50Webcam;

/// <summary>
/// Registers the "OpenM50 Webcam" Windows 11 virtual camera for as long as this process runs.
/// The camera itself is implemented by OpenM50VCam.dll (registered machine-wide by the installer),
/// which Windows Frame Server loads and which pulls frames from our MJPEG server.
/// </summary>
internal sealed class VirtualCamera : IDisposable
{
    private const string Clsid = "{E2B7A5C1-3F4D-4C8E-9A61-0D5B7F2C9E34}";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateFn(out IntPtr handle);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int RemoveFn(IntPtr handle);

    private readonly RemoveFn _remove;
    private IntPtr _handle;

    private VirtualCamera(RemoveFn remove, IntPtr handle)
    {
        _remove = remove;
        _handle = handle;
    }

    /// <summary>Path of the registered DLL, or null if the virtual camera is not installed.</summary>
    public static string? InstalledDllPath()
    {
        using var key = Registry.LocalMachine.OpenSubKey($@"Software\Classes\CLSID\{Clsid}\InprocServer32");
        var path = key?.GetValue(null) as string;
        return path is not null && File.Exists(path) ? path : null;
    }

    public static VirtualCamera? TryCreate(Action<string> log, out VirtualCameraStatus status, out string? error)
    {
        error = null;
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
        {
            status = VirtualCameraStatus.NotSupported;
            log("Virtual camera needs Windows 11 - skipped (the OBS stream still works).");
            return null;
        }

        string? dll = InstalledDllPath();
        if (dll is null)
        {
            status = VirtualCameraStatus.NotInstalled;
            log("Virtual camera is not installed. Run the OpenM50 Webcam installer to get the webcam device.");
            return null;
        }

        try
        {
            IntPtr lib = NativeLibrary.Load(dll);
            var create = Marshal.GetDelegateForFunctionPointer<CreateFn>(NativeLibrary.GetExport(lib, "OpenM50_CreateVirtualCamera"));
            var remove = Marshal.GetDelegateForFunctionPointer<RemoveFn>(NativeLibrary.GetExport(lib, "OpenM50_RemoveVirtualCamera"));
            int hr = create(out IntPtr handle);
            if (hr < 0)
            {
                status = VirtualCameraStatus.Failed;
                error = $"Could not create the virtual camera (0x{hr:X8}).";
                log(error);
                return null;
            }
            status = VirtualCameraStatus.Active;
            log("Virtual camera \"OpenM50 Webcam\" is available to all apps.");
            // The library stays loaded until process exit.
            return new VirtualCamera(remove, handle);
        }
        catch (Exception ex)
        {
            status = VirtualCameraStatus.Failed;
            error = "Virtual camera failed: " + ex.Message;
            log(error);
            return null;
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        try { _remove(_handle); } catch { /* ignore */ }
        _handle = IntPtr.Zero;
    }
}
