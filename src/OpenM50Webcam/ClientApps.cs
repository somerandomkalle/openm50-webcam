using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace OpenM50Webcam;

/// <summary>Finds out which applications are using our outputs.</summary>
internal static partial class ClientApps
{
    // ------------------------------------------------------------------ MJPEG stream clients

    /// <summary>Returns the display name of the process that owns the local TCP connection from <paramref name="clientPort"/> to <paramref name="serverPort"/>.</summary>
    public static string? ProcessNameForConnection(int clientPort, int serverPort)
    {
        int? pid = FindOwningPid(clientPort, serverPort);
        if (pid is null) return null;
        return DisplayNameForPid(pid.Value);
    }

    private const int AF_INET = 2;
    private const int TCP_TABLE_OWNER_PID_ALL = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr table, ref int size, bool sort, int ipVersion, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State, LocalAddr, LocalPort, RemoteAddr, RemotePort, OwningPid;
    }

    private static int NetPort(uint p) => (int)(((p & 0xFF) << 8) | ((p >> 8) & 0xFF));

    private static int? FindOwningPid(int clientPort, int serverPort)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0);
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buffer, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_ALL, 0) != 0) return null;
            int count = Marshal.ReadInt32(buffer);
            int rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            for (int i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(buffer + 4 + i * rowSize);
                if (NetPort(row.LocalPort) == clientPort && NetPort(row.RemotePort) == serverPort)
                    return (int)row.OwningPid;
            }
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr process, int flags, StringBuilder name, ref int size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    private static string? DisplayNameForPid(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, pid);
        if (h == IntPtr.Zero)
        {
            try { return Process.GetProcessById(pid).ProcessName; } catch { return null; }
        }
        try
        {
            var sb = new StringBuilder(1024);
            int size = sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? DisplayNameForExe(sb.ToString()) : null;
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>Friendly name for an executable: its file description, with a few well-known fixes.</summary>
    public static string DisplayNameForExe(string path)
    {
        string file = Path.GetFileNameWithoutExtension(path);
        if (file.StartsWith("obs", StringComparison.OrdinalIgnoreCase)) return "OBS Studio";
        try
        {
            var vi = FileVersionInfo.GetVersionInfo(path);
            string? name = !string.IsNullOrWhiteSpace(vi.FileDescription) ? vi.FileDescription : vi.ProductName;
            if (!string.IsNullOrWhiteSpace(name)) return name.Trim();
        }
        catch { /* file not accessible */ }
        return file;
    }

    // ------------------------------------------------------------------ apps using a camera

    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\webcam";

    /// <summary>
    /// Apps that Windows currently records as using a camera (same data as the privacy indicator):
    /// entries whose LastUsedTimeStop is 0. Windows does not say *which* camera, so callers should
    /// only show this while the virtual camera is actually being read.
    /// </summary>
    public static List<string> AppsUsingCameraNow()
    {
        var result = new List<string>();
        using var root = Registry.CurrentUser.OpenSubKey(ConsentStore);
        if (root is null) return result;

        foreach (string name in root.GetSubKeyNames())
        {
            if (name == "NonPackaged")
            {
                using var np = root.OpenSubKey(name);
                if (np is null) continue;
                foreach (string exeKey in np.GetSubKeyNames())
                {
                    using var k = np.OpenSubKey(exeKey);
                    if (IsActive(k)) result.Add(DisplayNameForExe(exeKey.Replace('#', '\\')));
                }
            }
            else
            {
                using var k = root.OpenSubKey(name);
                if (IsActive(k)) result.Add(DisplayNameForPackage(name));
            }
        }
        return result.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static bool IsActive(RegistryKey? key)
    {
        if (key is null) return false;
        long start = Convert.ToInt64(key.GetValue("LastUsedTimeStart") ?? 0L);
        long stop = Convert.ToInt64(key.GetValue("LastUsedTimeStop") ?? 0L);
        return start != 0 && stop == 0;
    }

    private static readonly Dictionary<string, string> KnownPackages = new(StringComparer.OrdinalIgnoreCase)
    {
        ["MSTeams"] = "Microsoft Teams",
        ["Microsoft.WindowsCamera"] = "Windows Camera",
        ["Microsoft.SkypeApp"] = "Skype",
        ["5319275A.WhatsAppDesktop"] = "WhatsApp",
        ["Microsoft.Windows.Photos"] = "Photos",
        ["Clipchamp.Clipchamp"] = "Clipchamp",
    };

    private static string DisplayNameForPackage(string packageFamily)
    {
        string id = packageFamily.Split('_')[0];
        if (KnownPackages.TryGetValue(id, out var known)) return known;
        string last = id.Contains('.') ? id[(id.LastIndexOf('.') + 1)..] : id;
        return CamelCase().Replace(last, "$1 $2"); // "WindowsCamera" -> "Windows Camera"
    }

    [GeneratedRegex("([a-z])([A-Z])")]
    private static partial Regex CamelCase();
}
