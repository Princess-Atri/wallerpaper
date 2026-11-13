using System.Runtime.InteropServices;
using System.Text;

namespace Wallerpaper.Host;

internal readonly record struct MonitorInfo(
    IntPtr Handle,
    string Device,
    int X,
    int Y,
    int Width,
    int Height,
    bool Primary)
{
    public string Key => Device;
    public override string ToString() =>
        $"{Device} {X},{Y} {Width}x{Height}{(Primary ? " primary" : "")}";
}

internal sealed class MonitorManager
{
    private readonly Action<string> _log;
    private List<MonitorInfo> _monitors = new();

    public MonitorManager(Action<string> log)
    {
        _log = log;
    }

    public IReadOnlyList<MonitorInfo> Monitors => _monitors;

    public bool Refresh()
    {
        var found = new List<MonitorInfo>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data) =>
        {
            var mi = new MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<MONITORINFOEX>() };
            if (GetMonitorInfo(hMonitor, ref mi))
            {
                found.Add(new MonitorInfo(
                    hMonitor,
                    mi.szDevice.TrimEnd('\0'),
                    mi.rcMonitor.Left,
                    mi.rcMonitor.Top,
                    mi.rcMonitor.Right - mi.rcMonitor.Left,
                    mi.rcMonitor.Bottom - mi.rcMonitor.Top,
                    (mi.dwFlags & 1) != 0));
            }
            return true;
        }, IntPtr.Zero);

        var changed = !SameMonitors(_monitors, found);
        _monitors = found
            .OrderByDescending(m => m.Primary)
            .ThenBy(m => m.X)
            .ThenBy(m => m.Y)
            .ToList();
        if (changed)
            _log("monitors: " + string.Join(" | ", _monitors.Select(m => m.ToString())));
        return changed;
    }

    public MonitorInfo? FindByDevice(string device) =>
        _monitors.FirstOrDefault(m => string.Equals(m.Device, device, StringComparison.OrdinalIgnoreCase));

    public MonitorInfo? FindByHandle(IntPtr handle) =>
        _monitors.FirstOrDefault(m => m.Handle == handle);

    private static bool SameMonitors(List<MonitorInfo> a, List<MonitorInfo> b)
    {
        if (a.Count != b.Count)
            return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i].Device != b[i].Device || a[i].X != b[i].X || a[i].Y != b[i].Y ||
                a[i].Width != b[i].Width || a[i].Height != b[i].Height)
                return false;
        }
        return true;
    }

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFOEX info);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public uint cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }
}
