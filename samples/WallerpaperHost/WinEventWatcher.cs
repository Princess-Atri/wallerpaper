using System.Runtime.InteropServices;

namespace Wallerpaper.Host;

/// <summary>
/// Out-of-context WinEvent hook for foreground/location/minimize changes.
/// It only sets a dirty flag; the scheduler still applies the 500ms debounce.
/// </summary>
internal sealed class WinEventWatcher : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint EventSystemMinimizeStart = 0x0016;
    private const uint EventSystemMinimizeEnd = 0x0017;
    private const uint EventObjectLocationChange = 0x800B;
    private const uint WineventOutOfContext = 0x0000;
    private const uint WineventSkipOwnProcess = 0x0002;
    private const uint PmRemove = 0x0001;

    private readonly WinEventDelegate _callback;
    private IntPtr _foregroundHook;
    private IntPtr _locationHook;
    private IntPtr _minimizeHook;
    private volatile bool _dirty;

    public WinEventWatcher()
    {
        _callback = OnWinEvent;
    }

    public void Start()
    {
        _foregroundHook = SetWinEventHook(
            EventSystemForeground, EventSystemForeground, IntPtr.Zero,
            _callback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);

        _minimizeHook = SetWinEventHook(
            EventSystemMinimizeStart, EventSystemMinimizeEnd, IntPtr.Zero,
            _callback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);

        _locationHook = SetWinEventHook(
            EventObjectLocationChange, EventObjectLocationChange, IntPtr.Zero,
            _callback, 0, 0, WineventOutOfContext | WineventSkipOwnProcess);
    }

    public bool Pump()
    {
        while (PeekMessage(out var msg, IntPtr.Zero, 0, 0, PmRemove))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
        return _dirty;
    }

    public bool ConsumeDirty()
    {
        if (!_dirty) return false;
        _dirty = false;
        return true;
    }

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time)
    {
        _dirty = true;
    }

    public void Dispose()
    {
        if (_foregroundHook != IntPtr.Zero)
            UnhookWinEvent(_foregroundHook);
        if (_minimizeHook != IntPtr.Zero)
            UnhookWinEvent(_minimizeHook);
        if (_locationHook != IntPtr.Zero)
            UnhookWinEvent(_locationHook);
        _foregroundHook = IntPtr.Zero;
        _minimizeHook = IntPtr.Zero;
        _locationHook = IntPtr.Zero;
    }

    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int x;
        public int y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module,
        WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);
}
