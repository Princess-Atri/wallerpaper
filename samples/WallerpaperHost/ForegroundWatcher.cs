using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Wallerpaper.Host;

internal sealed record ForegroundInfo(
    IntPtr Hwnd,
    IntPtr MonitorHandle,
    string ProcessName,
    string Title,
    string ClassName,
    bool IsFullscreen,
    bool IsMaximized,
    bool IsExcluded,
    string ExclusionReason,
    bool IsExclusiveD3D)
{
    public static readonly ForegroundInfo None = new(
        IntPtr.Zero, IntPtr.Zero, "", "", "", false, false, true, "none", false);
}

internal sealed class ForegroundWatcher
{
    private readonly HashSet<string> _excludeProcesses;
    private readonly HashSet<string> _scanProcesses;
    private readonly HashSet<string> _excludeTitles;
    private readonly HashSet<string> _excludeClasses;
    private readonly Dictionary<uint, string> _processNames = new();
    private readonly Action<string> _log;

    public ForegroundWatcher(IEnumerable<string> processes, IEnumerable<string> scanProcesses,
        IEnumerable<string> titles, IEnumerable<string> classes, Action<string> log)
    {
        _excludeProcesses = new(processes, StringComparer.OrdinalIgnoreCase);
        _scanProcesses = new(scanProcesses, StringComparer.OrdinalIgnoreCase);
        _excludeTitles = new(titles, StringComparer.OrdinalIgnoreCase);
        _excludeClasses = new(classes, StringComparer.OrdinalIgnoreCase);
        _log = log;
    }

    public ForegroundInfo Poll()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero)
            return ForegroundInfo.None;

        var title = GetText(hwnd);
        var className = GetClass(hwnd);
        GetWindowThreadProcessId(hwnd, out var pid);
        var process = GetProcessName(pid);

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var excluded = false;
        var reason = "";

        if (!IsWindowVisible(hwnd) || IsIconic(hwnd))
        {
            excluded = true;
            reason = "invisible/minimized";
        }
        else if (IsCloaked(hwnd))
        {
            excluded = true;
            reason = "cloaked";
        }
        else if (className is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "Shell_TrayWnd")
        {
            excluded = true;
            reason = "desktop/shell";
        }
        else if (_excludeProcesses.Contains(process))
        {
            excluded = true;
            reason = "process exclusion";
        }
        else if (_excludeTitles.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase)))
        {
            excluded = true;
            reason = "title exclusion";
        }
        else if (_excludeClasses.Contains(className))
        {
            excluded = true;
            reason = "class exclusion";
        }

        var fullscreen = false;
        var maximized = false;
        if (!excluded)
        {
            maximized = IsZoomed(hwnd) ||
                (GetWindowLongPtr(hwnd, GwlStyle).ToInt64() & WsMaximize) != 0;

            var frame = GetFrameBounds(hwnd);
            if (frame is not null && monitor != IntPtr.Zero &&
                GetMonitorRect(monitor) is { } mon)
            {
                // A maximized normal window is not treated as fullscreen.
                // Borderless games often have no WS_MAXIMIZE and hit this path.
                if (!maximized && Covers(frame.Value, mon, 2))
                    fullscreen = true;
            }
        }

        var exclusive = !excluded && IsExclusiveD3DFullscreen();
        return new ForegroundInfo(hwnd, monitor, process, title, className,
            fullscreen, maximized, excluded, reason, exclusive);
    }

    /// <summary>
    /// Returns monitor handles that currently have a visible borderless
    /// fullscreen window, even if it is not the foreground window. This closes
    /// the "video keeps playing behind the terminal" gap.
    /// </summary>
    public IReadOnlyCollection<IntPtr> FindFullscreenWindows()
    {
        var result = new HashSet<IntPtr>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd) || IsIconic(hwnd) || IsCloaked(hwnd))
                return true;

            var className = GetClass(hwnd);
            if (className is "Progman" or "WorkerW" or "SHELLDLL_DefView" or
                "Shell_TrayWnd" or "Wallerpaper.RenderHost.Wallpaper")
                return true;

            GetWindowThreadProcessId(hwnd, out var pid);
            var process = GetProcessName(pid);
            var title = GetText(hwnd);
            if (_excludeProcesses.Contains(process) ||
                (!_scanProcesses.Contains(process)) ||
                _excludeTitles.Any(t => title.Contains(t, StringComparison.OrdinalIgnoreCase)) ||
                _excludeClasses.Contains(className))
                return true;

            // A maximized window that still carries WS_MAXIMIZE is treated as a
            // desktop window, not as an exclusive/borderless fullscreen app.
            // This avoids pausing the wallpaper for a maximized terminal/IDE.
            if (IsZoomed(hwnd))
                return true;

            // The scan is already scoped to known media processes. For those,
            // a non-maximized window that covers the monitor is a fullscreen
            // player even if the player keeps caption/style bits around.
            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            var frame = GetFrameBounds(hwnd);
            if (frame is not null && GetMonitorRect(monitor) is { } mon &&
                Covers(frame.Value, mon, 2))
                result.Add(monitor);

            return true;
        }, IntPtr.Zero);
        return result;
    }

    private static bool IsExclusiveD3DFullscreen()
    {
        try
        {
            return SHQueryUserNotificationState(out var state) == 0 &&
                   state == QueryUserNotificationState.QunsRunningD3DFullScreen;
        }
        catch
        {
            return false;
        }
    }

    private string GetProcessName(uint pid)
    {
        if (_processNames.TryGetValue(pid, out var cached))
            return cached;
        try
        {
            var p = Process.GetProcessById((int)pid);
            var name = p.ProcessName + ".exe";
            _processNames[pid] = name;
            return name;
        }
        catch
        {
            return "";
        }
    }

    private static string GetText(IntPtr hwnd)
    {
        var sb = new StringBuilder(512);
        GetWindowText(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static string GetClass(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    private static RECT? GetFrameBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var frame, Marshal.SizeOf<RECT>()) == 0)
            return frame;
        return GetWindowRect(hwnd, out var rc) ? rc : null;
    }

    private static bool IsCloaked(IntPtr hwnd)
    {
        return DwmGetWindowIntAttribute(hwnd, DwmwaCloaked, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static bool Covers(RECT window, RECT monitor, int tolerance)
    {
        return window.Left <= monitor.Left + tolerance &&
               window.Top <= monitor.Top + tolerance &&
               window.Right >= monitor.Right - tolerance &&
               window.Bottom >= monitor.Bottom - tolerance;
    }

    private static RECT? GetMonitorRect(IntPtr hmon)
    {
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        return GetMonitorInfo(hmon, ref mi) ? mi.rcMonitor : null;
    }

    private const int GwlStyle = -16;
    private const long WsMaximize = 0x01000000L;
    private const long WsCaption = 0x00C00000L;
    private const long WsThickFrame = 0x00040000L;
    private const uint MonitorDefaultToNearest = 2;
    private const int DwmwaExtendedFrameBounds = 9;
    private const int DwmwaCloaked = 14;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    private enum QueryUserNotificationState
    {
        QunsNotPresent = 1,
        QunsBusy = 2,
        QunsRunningD3DFullScreen = 3,
        QunsPresentationMode = 4,
        QunsAcceptsNotifications = 5,
        QunsQuietTime = 6,
        QunsApp = 7
    }

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out QueryUserNotificationState state);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hmon, ref MONITORINFO info);
    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT value, int size);
    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowIntAttribute(IntPtr hwnd, int attr, out int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }
}
