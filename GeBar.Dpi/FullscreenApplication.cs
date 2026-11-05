using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace GeBar.Dpi;

internal sealed class FullscreenApplication
{
    private uint _processId;
    private string? _processName;

    internal static bool IsEligible(string name) => !Excluded.Contains(name);

    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "ShellExperienceHost", "StartMenuExperienceHost", "SearchHost", "LockApp",
        "chrome", "msedge", "firefox", "brave", "opera", "opera_gx", "vivaldi", "browser", "zen", "waterfox", "floorp",
        "vlc", "mpv", "mpvnet", "mpc-hc", "mpc-hc64", "mpc-be", "mpc-be64", "PotPlayerMini", "PotPlayerMini64",
        "Video.UI", "Microsoft.Media.Player", "wmplayer", "ApplicationFrameHost", "Netflix", "Plex", "Kodi", "Stremio"
    };

    public bool IsActive()
    {
        nint window = GetForegroundWindow();
        if (window == 0 || IsIconic(window) || !IsWindowVisible(window)) return false;
        if ((GetWindowLong(window, -16) & 0x00C00000) != 0) return false;
        GetWindowThreadProcessId(window, out uint processId);
        if (processId == 0) return false;
        if (_processId != processId)
        {
            _processId = 0;
            try
            {
                using Process process = Process.GetProcessById((int)processId);
                _processName = process.ProcessName;
                _processId = processId;
            }
            catch (Exception error) when (error is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
        }
        if (_processName is null || !IsEligible(_processName)) return false;
        nint awareness = SetThreadDpiAwarenessContext(-4);
        try
        {
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfo(MonitorFromWindow(window, 2), ref info)) return false;
            if (DwmGetWindowAttribute(window, 9, out Bounds bounds, Marshal.SizeOf<Bounds>()) != 0 && !GetWindowRect(window, out bounds)) return false;
            return CoversMonitor(bounds, info.Monitor);
        }
        finally { if (awareness != 0) SetThreadDpiAwarenessContext(awareness); }
    }

    internal static bool CoversMonitor(Bounds window, Bounds monitor) =>
        Math.Abs((long)window.Left - monitor.Left) <= 2 && Math.Abs((long)window.Top - monitor.Top) <= 2
        && Math.Abs((long)window.Right - monitor.Right) <= 2 && Math.Abs((long)window.Bottom - monitor.Bottom) <= 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Bounds { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo { public int Size; public Bounds Monitor, Work; public uint Flags; }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern nint SetThreadDpiAwarenessContext(nint awareness);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint window);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(nint window, int index);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
    [DllImport("user32.dll")] private static extern nint MonitorFromWindow(nint window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint window, out Bounds bounds);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint window, int attribute, out Bounds value, int size);
}
