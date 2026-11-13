using System.Runtime.InteropServices;

namespace Wallerpaper.Host;

/// <summary>
/// Per-monitor static wallpaper using IDesktopWallpaper (Windows 8+).
/// Used when a renderer repeatedly crashes, so the desktop is not left blank.
/// </summary>
internal sealed class WallpaperFallback : IDisposable
{
    private readonly Action<string> _log;
    private readonly IDesktopWallpaper? _desktop;
    private readonly Dictionary<string, MonitorEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public WallpaperFallback(Action<string> log)
    {
        _log = log;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("C2CF3110-460E-4fc1-B9D0-8A1C0C9CC4BD"));
            if (type is not null)
                _desktop = (IDesktopWallpaper?)Activator.CreateInstance(type);
        }
        catch (Exception ex)
        {
            _log($"IDesktopWallpaper unavailable: {ex.Message}");
        }

        if (_desktop is null)
            return;

        try
        {
            _desktop.GetMonitorDevicePathCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                _desktop.GetMonitorDevicePathAt(i, out var id);
                _desktop.GetMonitorRECT(id, out var rect);
                _entries[id] = new MonitorEntry(id, rect);
            }
        }
        catch (Exception ex)
        {
            _log($"IDesktopWallpaper enumerate failed: {ex.Message}");
        }
    }

    public bool Available => _desktop is not null;

    public void Apply(MonitorInfo monitor, string fallbackImage)
    {
        if (_desktop is null || _disposed || !File.Exists(fallbackImage))
            return;

        try
        {
            var id = Find(monitor);
            if (id is null)
            {
                _log($"[{monitor.Device}] fallback: monitor path not found");
                return;
            }

            if (!_entries[id].OriginalSaved)
            {
                _desktop.GetWallpaper(id, out var original);
                _entries[id].OriginalPath = original;
                _entries[id].OriginalSaved = true;
            }

            _desktop.SetWallpaper(id, fallbackImage);
            _entries[id].UsingFallback = true;
            _log($"[{monitor.Device}] static fallback applied: {fallbackImage}");
        }
        catch (Exception ex)
        {
            _log($"[{monitor.Device}] fallback failed: {ex.Message}");
        }
    }

    public void Restore(MonitorInfo monitor)
    {
        if (_desktop is null || _disposed)
            return;
        var id = Find(monitor);
        if (id is null || !_entries[id].UsingFallback)
            return;
        Restore(id);
    }

    public void RestoreAll()
    {
        if (_desktop is null || _disposed)
            return;
        foreach (var id in _entries.Keys.ToList())
        {
            if (_entries[id].UsingFallback)
                Restore(id);
        }
    }

    private void Restore(string id)
    {
        try
        {
            var original = _entries[id].OriginalPath;
            if (!string.IsNullOrWhiteSpace(original) && File.Exists(original))
                _desktop!.SetWallpaper(id, original);
            _entries[id].UsingFallback = false;
            _log($"fallback restored for {id}");
        }
        catch (Exception ex)
        {
            _log($"fallback restore failed for {id}: {ex.Message}");
        }
    }

    private string? Find(MonitorInfo monitor)
    {
        foreach (var entry in _entries.Values)
        {
            if (entry.Rect.Left == monitor.X && entry.Rect.Top == monitor.Y &&
                entry.Rect.Right == monitor.X + monitor.Width &&
                entry.Rect.Bottom == monitor.Y + monitor.Height)
                return entry.Id;
        }
        return null;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        RestoreAll();
        if (_desktop is not null && Marshal.IsComObject(_desktop))
            Marshal.ReleaseComObject(_desktop);
    }

    private sealed class MonitorEntry
    {
        public MonitorEntry(string id, RECT rect)
        {
            Id = id;
            Rect = rect;
        }

        public string Id { get; }
        public RECT Rect { get; }
        public string? OriginalPath { get; set; }
        public bool OriginalSaved { get; set; }
        public bool UsingFallback { get; set; }
    }

    [ComImport]
    [Guid("B92B56A9-8B55-4E14-9A89-0199BBB6F93B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDesktopWallpaper
    {
        void SetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID,
            [MarshalAs(UnmanagedType.LPWStr)] string wallpaper);

        void GetWallpaper([MarshalAs(UnmanagedType.LPWStr)] string? monitorID,
            [MarshalAs(UnmanagedType.LPWStr)] out string wallpaper);

        void GetMonitorDevicePathAt(uint monitorIndex,
            [MarshalAs(UnmanagedType.LPWStr)] out string monitorID);

        void GetMonitorDevicePathCount(out uint count);

        void GetMonitorRECT([MarshalAs(UnmanagedType.LPWStr)] string monitorID, out RECT displayRect);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }
}
