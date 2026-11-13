using System.Runtime.InteropServices;

namespace Wallerpaper.Shell.Services;

public sealed record MonitorDisplay(
    string Device,
    int X,
    int Y,
    int Width,
    int Height,
    bool Primary)
{
    public string Label => $"{(Primary ? "主屏 " : "")}{Device}  {Width}×{Height}";
}

public static class MonitorService
{
    private const int MonitorInfoPrimary = 1;

    public static List<MonitorDisplay> Enumerate()
    {
        var result = new List<MonitorDisplay>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
        {
            var info = new MONITORINFOEX
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>()
            };
            if (GetMonitorInfo(monitor, ref info))
            {
                var rect = info.rcMonitor;
                result.Add(new MonitorDisplay(
                    info.szDevice,
                    rect.Left,
                    rect.Top,
                    rect.Right - rect.Left,
                    rect.Bottom - rect.Top,
                    (info.dwFlags & MonitorInfoPrimary) != 0));
            }
            return true;
        }, IntPtr.Zero);
        return result
            .OrderByDescending(x => x.Primary)
            .ThenBy(x => x.X)
            .ToList();
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public int dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip,
        MonitorEnumProc callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFOEX info);
}
