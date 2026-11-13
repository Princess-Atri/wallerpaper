using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;


namespace Wallerpaper.RenderHost.WorkerW;

/// <summary>
/// Minimal WorkerW window host for one render child (video or WebView2).
/// It intentionally does NOT re-attach in-process: if Explorer/WorkerW destroys the
/// window, it raises WindowLost and the parent process restarts this child.
/// </summary>
public sealed class WorkerWWindow : IDisposable
{
    private const string MessageClassName = "Wallerpaper.RenderHost.Message";
    private const string WallpaperClassName = "Wallerpaper.RenderHost.Wallpaper";

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_POPUP = 0x80000000L;
    private const long WS_CHILD = 0x40000000L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private static readonly IntPtr HwndBottom = new(1);
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    private const uint WM_PAINT = 0x000F;
    private const uint WM_ERASEBKGND = 0x0014;
    private const uint WM_CLOSE = 0x0010;
    private const uint WM_DESTROY = 0x0002;
    private const uint WM_TIMER = 0x0113;
    private const uint WM_DISPLAYCHANGE = 0x007E;
    private const uint WM_DPICHANGED = 0x02E0;
    private const uint WM_NCDESTROY = 0x0082;
    private const uint WM_QUIT = 0x0012;
    private const uint PM_REMOVE = 0x0001;
    private const int TimerWatchdog = 1;
    private const int IDC_ARROW = 32512;
    private const int ErrorClassAlreadyExists = 1410;
    private const uint SMTO_NORMAL = 0;

    private readonly Action<string> _log;
    private readonly IntPtr _hInstance;
    private readonly WndProc _messageWndProc;
    private readonly WndProc _wallpaperWndProc;
    private readonly EnumWindowsProc _enumWindowsProc;

    private IntPtr _messageHwnd;
    private IntPtr _windowHwnd;
    private IntPtr _brush;
    private IntPtr _progman;
    private IntPtr _workerW;
    private IntPtr _defView;
    private IntPtr _parent;
    private IntPtr _insertAfter;
    private bool _isRaised;
    private RECT _primaryRect;
    private RECT? _targetRect;
    private bool _messageClassRegistered;
    private bool _wallpaperClassRegistered;
    private bool _windowLost;
    private bool _detaching;
    private bool _disposed;

    public WorkerWWindow(Action<string> log)
    {
        _log = log;
        _hInstance = NativeMethods.GetModuleHandle(null);
        _messageWndProc = MessageWndProc;
        _wallpaperWndProc = WallpaperWndProc;
        _enumWindowsProc = EnumWindowsCallback;
    }

    /// <summary>
    /// Optional per-monitor screen rectangle. Must be called before
    /// CreateAndAttach(); the host uses one WorkerW child per monitor.
    /// </summary>
    public void SetTargetRect(int x, int y, int width, int height)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width));
        _targetRect = new RECT { Left = x, Top = y, Right = x + width, Bottom = y + height };
    }

    public IntPtr WindowHandle => _windowHwnd;
    public IntPtr MessageHandle => _messageHwnd;
    public bool IsWindowLost => _windowLost;

    public event Action? WindowLost;

    public bool CreateAndAttach()
    {
        if (!CreateMessageWindow())
            return false;

        if (!FindDesktop())
        {
            _log("WorkerW/Progman not found.");
            return false;
        }

        if (!CreateWallpaperWindow())
            return false;

        if (!Attach())
            return false;

        NativeMethods.SetTimer(_messageHwnd, (IntPtr)TimerWatchdog, 1000, IntPtr.Zero);
        return true;
    }

    public void RunMessageLoop()
    {
        while (true)
        {
            var result = NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (result == -1)
            {
                _log($"GetMessage failed: {Marshal.GetLastWin32Error()}");
                break;
            }

            if (result == 0)
                break;

            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    /// <summary>
    /// Pumps pending messages while libmpv is loading (the mpv window needs its
    /// parent's message loop to respond to WM_SIZE/WM_PAINT during first frame).
    /// </summary>
    public void PumpMessages()
    {
        while (NativeMethods.PeekMessage(out var msg, IntPtr.Zero, 0, 0, PM_REMOVE))
        {
            if (msg.message == WM_QUIT)
            {
                NativeMethods.PostQuitMessage((int)msg.wParam);
                return;
            }

            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    public void RequestClose()
    {
        if (_messageHwnd != IntPtr.Zero)
            NativeMethods.PostMessage(_messageHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private bool CreateMessageWindow()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _messageWndProc,
            hInstance = _hInstance,
            lpszClassName = MessageClassName
        };

        var atom = NativeMethods.RegisterClassEx(ref wc);
        if (atom == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
        {
            _log($"RegisterClassEx(message) failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        _messageClassRegistered = atom != 0;

        _messageHwnd = NativeMethods.CreateWindowEx(
            (uint)WS_EX_TOOLWINDOW,
            MessageClassName,
            "Wallerpaper VideoHost Message Window",
            (uint)WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_messageHwnd == IntPtr.Zero)
        {
            _log($"CreateWindowEx(message) failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        _log($"Message window: 0x{_messageHwnd:X}");
        return true;
    }

    private bool CreateWallpaperWindow()
    {
        if (_brush == IntPtr.Zero)
            _brush = NativeMethods.CreateSolidBrush(0x00000000); // black

        if (!_wallpaperClassRegistered)
        {
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = 0x0001 | 0x0002, // CS_VREDRAW | CS_HREDRAW
                lpfnWndProc = _wallpaperWndProc,
                hInstance = _hInstance,
                hCursor = NativeMethods.LoadCursor(IntPtr.Zero, new IntPtr(IDC_ARROW)),
                hbrBackground = _brush,
                lpszClassName = WallpaperClassName
            };

            var atom = NativeMethods.RegisterClassEx(ref wc);
            if (atom == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            {
                _log($"RegisterClassEx(wallpaper) failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            _wallpaperClassRegistered = true;
        }

        var exStyle = (uint)(WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        _windowHwnd = NativeMethods.CreateWindowEx(
            exStyle,
            WallpaperClassName,
            "Wallerpaper VideoWallpaper",
            (uint)WS_POPUP,
            0, 0, 100, 100,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_windowHwnd == IntPtr.Zero)
        {
            _log($"CreateWindowEx(wallpaper) failed: {Marshal.GetLastWin32Error()}");
            return false;
        }

        _log($"Wallpaper window: 0x{_windowHwnd:X}");
        return true;
    }

    private bool FindDesktop()
    {
        _progman = NativeMethods.FindWindow("Progman", null);
        if (_progman == IntPtr.Zero)
            return false;

        _primaryRect = GetPrimaryMonitorRect();
        _workerW = IntPtr.Zero;
        _defView = IntPtr.Zero;

        NativeMethods.SendMessageTimeout(
            _progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1),
            SMTO_NORMAL, 1000, out _);
        NativeMethods.EnumWindows(_enumWindowsProc, IntPtr.Zero);

        if (_workerW == IntPtr.Zero)
        {
            NativeMethods.SendMessageTimeout(
                _progman, 0x052C, IntPtr.Zero, IntPtr.Zero,
                SMTO_NORMAL, 1000, out _);
            NativeMethods.EnumWindows(_enumWindowsProc, IntPtr.Zero);
        }

        if (_workerW == IntPtr.Zero)
        {
            var candidate = NativeMethods.FindWindowEx(_progman, IntPtr.Zero, "WorkerW", null);
            if (candidate != IntPtr.Zero && !HasDefView(candidate))
                _workerW = candidate;
        }

        if (_defView == IntPtr.Zero)
            _defView = NativeMethods.FindWindowEx(_progman, IntPtr.Zero, "SHELLDLL_DefView", null);

        _isRaised = HasExStyle(_progman, WS_EX_NOREDIRECTIONBITMAP);

        if (_isRaised && _defView != IntPtr.Zero && NativeMethods.GetParent(_defView) == _progman)
        {
            _parent = _progman;
            _insertAfter = _defView;
        }
        else if (_workerW != IntPtr.Zero)
        {
            _parent = _workerW;
            _insertAfter = HwndBottom;
        }
        else if (_defView != IntPtr.Zero)
        {
            _parent = _progman;
            _insertAfter = _defView;
            _isRaised = true;
        }
        else
        {
            return false;
        }

        _log($"Desktop: progman=0x{_progman:X}, workerW=0x{_workerW:X}, " +
             $"defView=0x{_defView:X}, raised={_isRaised}, parent=0x{_parent:X}, " +
             $"insertAfter=0x{_insertAfter:X}, primary={RectToString(_primaryRect)}");
        return true;
    }

    private bool EnumWindowsCallback(IntPtr top, IntPtr lParam)
    {
        var defView = NativeMethods.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
            return true;

        _defView = defView;

        var after = top;
        while (true)
        {
            var candidate = NativeMethods.FindWindowEx(IntPtr.Zero, after, "WorkerW", null);
            if (candidate == IntPtr.Zero)
                break;

            if (IsValidWorkerW(candidate))
            {
                _workerW = candidate;
                return false;
            }

            after = candidate;
        }

        return true;
    }

    private bool IsValidWorkerW(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !NativeMethods.IsWindow(hwnd))
            return false;

        if (!string.Equals(GetClassName(hwnd), "WorkerW", StringComparison.OrdinalIgnoreCase))
            return false;

        if (HasDefView(hwnd))
            return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;

        return rect.Right > rect.Left && rect.Bottom > rect.Top && Intersects(rect, _primaryRect);
    }

    private bool Attach()
    {
        if (_parent == IntPtr.Zero || _windowHwnd == IntPtr.Zero)
            return false;

        // Keep the parent selection from FindDesktop; if Progman refuses the
        // reparent on a given build, fall back to the Wallpaper WorkerW.
        var targetParent = _parent;
        var targetInsertAfter = _insertAfter;

        for (var attempt = 0; attempt < 4; attempt++)
        {
            if (attempt > 0 &&
                (_parent == IntPtr.Zero || !NativeMethods.IsWindow(_parent)))
            {
                // Explorer restarted between attempts; re-discover handles.
                if (!FindDesktop())
                {
                    Thread.Sleep(200);
                    continue;
                }

                targetParent = _parent;
                targetInsertAfter = _insertAfter;
            }

            ApplyChildStyle();

            // Flush the style change before cross-process SetParent.
            NativeMethods.SetWindowPos(
                _windowHwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            var pt = MapScreenToParent(targetParent, _primaryRect.Left, _primaryRect.Top);

            NativeMethods.SetLastError(0);
            var oldParent = NativeMethods.SetParent(_windowHwnd, targetParent);
            var error = Marshal.GetLastWin32Error();
            var parentAfter = NativeMethods.GetParent(_windowHwnd);
            var styleAfter = NativeMethods.GetWindowLongPtr(_windowHwnd, GWL_STYLE).ToInt64();

            _log($"Attach attempt {attempt}: SetParent(old=0x{oldParent:X}, err={error}) " +
                 $"-> parentAfter=0x{parentAfter:X}, expected=0x{targetParent:X}, " +
                 $"style=0x{styleAfter:X8}, child={((styleAfter & WS_CHILD) != 0)}");

            if (parentAfter == targetParent)
            {
                _parent = targetParent;
                _insertAfter = targetInsertAfter;

                var ok = NativeMethods.SetWindowPos(
                    _windowHwnd, _insertAfter,
                    pt.X, pt.Y,
                    _primaryRect.Right - _primaryRect.Left,
                    _primaryRect.Bottom - _primaryRect.Top,
                    SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

                if (!ok)
                {
                    _log($"SetWindowPos failed: {Marshal.GetLastWin32Error()}");
                    return false;
                }

                NativeMethods.ShowWindow(_windowHwnd, 4 /* SW_SHOWNOACTIVATE */);
                NativeMethods.UpdateWindow(_windowHwnd);

                _log($"Attached: hwnd=0x{_windowHwnd:X}, parent=0x{_parent:X}, " +
                     $"rect=({pt.X},{pt.Y},{_primaryRect.Right - _primaryRect.Left}x{_primaryRect.Bottom - _primaryRect.Top})");
                return true;
            }

            // Fallback: classic WorkerW parent (if we started with Progman).
            if (attempt == 1 && targetParent == _progman && _workerW != IntPtr.Zero)
            {
                _log($"Progman reparent failed; retrying with WorkerW=0x{_workerW:X}");
                targetParent = _workerW;
                targetInsertAfter = HwndBottom;
            }
            else if (attempt == 2 && targetParent != _progman && _defView != IntPtr.Zero)
            {
                _log($"WorkerW reparent failed; retrying with Progman=0x{_progman:X}");
                targetParent = _progman;
                targetInsertAfter = _defView;
            }

            Thread.Sleep(120);
        }

        _log("SetParent failed after retries (see Attach attempt logs). " +
             "If err=5/ACCESS_DENIED: the child and Explorer are at different integrity levels. " +
             "Start the parent from a non-elevated terminal (do not use 'Run as administrator'), " +
             "and make sure explorer.exe is running in this session.");
        return false;
    }

    private void ApplyChildStyle()
    {
        var styleBefore = NativeMethods.GetWindowLongPtr(_windowHwnd, GWL_STYLE).ToInt64();
        var newStyle = (styleBefore & ~WS_POPUP) | WS_CHILD;
        NativeMethods.SetWindowLongPtr(_windowHwnd, GWL_STYLE, new IntPtr(newStyle));

        var exStyle = NativeMethods.GetWindowLongPtr(_windowHwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(_windowHwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        var styleAfter = NativeMethods.GetWindowLongPtr(_windowHwnd, GWL_STYLE).ToInt64();
        _log($"Child style: before=0x{styleBefore:X8}, new=0x{newStyle:X8}, " +
             $"after=0x{styleAfter:X8}, child={((styleAfter & WS_CHILD) != 0)}, " +
             $"privileged={Environment.IsPrivilegedProcess}");
    }

    private void Reposition(string reason)
    {
        if (_windowLost || _windowHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_windowHwnd))
            return;

        _primaryRect = GetPrimaryMonitorRect();
        var pt = MapScreenToParent(_parent, _primaryRect.Left, _primaryRect.Top);
        var ok = NativeMethods.SetWindowPos(
            _windowHwnd, _insertAfter,
            pt.X, pt.Y,
            _primaryRect.Right - _primaryRect.Left,
            _primaryRect.Bottom - _primaryRect.Top,
            SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

        _log($"[{reason}] reposition ok={ok}, primary={RectToString(_primaryRect)}");
        if (!ok)
            MarkLost("reposition failed");
    }

    private IntPtr MessageWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TIMER:
                if (wParam.ToInt32() == TimerWatchdog)
                    Watchdog();
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
                Reposition("WM_DISPLAYCHANGE");
                return IntPtr.Zero;

            case WM_DPICHANGED:
                Reposition("WM_DPICHANGED");
                return IntPtr.Zero;

            case WM_CLOSE:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;

            case WM_DESTROY:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private IntPtr WallpaperWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_ERASEBKGND:
                return new IntPtr(1);

            case WM_PAINT:
                Paint(hWnd);
                return IntPtr.Zero;

            case WM_NCDESTROY:
                if (!_detaching && _windowHwnd == hWnd)
                    MarkLost("WM_NCDESTROY");
                break;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Paint(IntPtr hWnd)
    {
        var hdc = NativeMethods.GetDC(hWnd);
        if (hdc == IntPtr.Zero)
            return;

        try
        {
            if (NativeMethods.GetClientRect(hWnd, out var rc) && _brush != IntPtr.Zero)
                NativeMethods.FillRect(hdc, ref rc, _brush);
        }
        finally
        {
            NativeMethods.ReleaseDC(hWnd, hdc);
            NativeMethods.ValidateRect(hWnd, IntPtr.Zero);
        }
    }

    private void Watchdog()
    {
        if (_windowLost || _disposed)
            return;

        var windowOk = _windowHwnd != IntPtr.Zero && NativeMethods.IsWindow(_windowHwnd);
        var parentOk = _parent != IntPtr.Zero && NativeMethods.IsWindow(_parent);
        var parentMatch = windowOk && NativeMethods.GetParent(_windowHwnd) == _parent;

        if (!windowOk || !parentOk || !parentMatch)
        {
            _log($"Watchdog: windowOk={windowOk}, parentOk={parentOk}, parentMatch={parentMatch}");
            MarkLost("watchdog");
        }
    }

    private void MarkLost(string reason)
    {
        if (_windowLost)
            return;

        _windowLost = true;
        _log($"WINDOW_LOST: {reason}");
        WindowLost?.Invoke();
        RequestClose();
    }

    private POINT MapScreenToParent(IntPtr parent, int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        var mapped = NativeMethods.MapWindowPoints(IntPtr.Zero, parent, ref pt, 1);
        if (mapped != 0)
            return pt;

        var origin = new POINT { X = 0, Y = 0 };
        if (NativeMethods.ClientToScreen(parent, ref origin))
            return new POINT { X = x - origin.X, Y = y - origin.Y };

        if (NativeMethods.GetWindowRect(parent, out var parentRect))
            return new POINT { X = x - parentRect.Left, Y = y - parentRect.Top };

        return new POINT { X = 0, Y = 0 };
    }

    private RECT GetPrimaryMonitorRect()
    {
        if (_targetRect.HasValue)
            return _targetRect.Value;

        var hmon = NativeMethods.MonitorFromPoint(
            new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        var mi = new MONITORINFO { cbSize = (uint)Marshal.SizeOf<MONITORINFO>() };
        if (NativeMethods.GetMonitorInfo(hmon, ref mi))
            return mi.rcMonitor;

        return new RECT
        {
            Left = 0,
            Top = 0,
            Right = NativeMethods.GetSystemMetrics(SM_CXSCREEN),
            Bottom = NativeMethods.GetSystemMetrics(SM_CYSCREEN)
        };
    }

    private static bool HasExStyle(IntPtr hwnd, long style) =>
        (NativeMethods.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & style) != 0;

    private static bool HasDefView(IntPtr hwnd) =>
        NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;

    private static bool Intersects(RECT a, RECT b) =>
        a.Left < b.Right && a.Right > b.Left && a.Top < b.Bottom && a.Bottom > b.Top;

    private static string GetClassName(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
            return "<null>";
        var sb = new StringBuilder(256);
        var n = NativeMethods.GetClassName(hwnd, sb, sb.Capacity);
        return n > 0 ? sb.ToString() : "<unknown>";
    }

    private static string RectToString(RECT r) =>
        $"({r.Left},{r.Top})-({r.Right},{r.Bottom}) [{r.Right - r.Left}x{r.Bottom - r.Top}]";

    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_messageHwnd != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_messageHwnd, (IntPtr)TimerWatchdog);
            NativeMethods.DestroyWindow(_messageHwnd);
            _messageHwnd = IntPtr.Zero;
        }

        if (_windowHwnd != IntPtr.Zero)
        {
            _detaching = true;
            var hwnd = _windowHwnd;
            _windowHwnd = IntPtr.Zero;

            if (NativeMethods.IsWindow(hwnd))
            {
                NativeMethods.ShowWindow(hwnd, 0);
                NativeMethods.SetParent(hwnd, IntPtr.Zero);

                var style = NativeMethods.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
                NativeMethods.SetWindowLongPtr(hwnd, GWL_STYLE,
                    new IntPtr((style & ~WS_CHILD) | WS_POPUP));

                NativeMethods.SetWindowPos(hwnd, HwndBottom, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

                NativeMethods.DestroyWindow(hwnd);
            }

            _detaching = false;
        }

        if (_wallpaperClassRegistered)
            NativeMethods.UnregisterClass(WallpaperClassName, _hInstance);
        if (_messageClassRegistered)
            NativeMethods.UnregisterClass(MessageClassName, _hInstance);

        if (_brush != IntPtr.Zero)
        {
            NativeMethods.DeleteObject(_brush);
            _brush = IntPtr.Zero;
        }
    }

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr FindWindowEx(
            IntPtr hWndParent, IntPtr hWndChildAfter, string? lpszClass, string? lpszWindow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr SendMessageTimeout(
            IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint fuFlags, uint uTimeout, out IntPtr lpdwResult);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetParent(IntPtr hWndChild, IntPtr hWndNewParent);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetParent(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int MapWindowPoints(
            IntPtr hWndFrom, IntPtr hWndTo, ref POINT lpPoints, uint cPoints);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetWindowPos(
            IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool UpdateWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyWindow(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateWindowEx(
            uint dwExStyle, string lpClassName, string lpWindowName, uint dwStyle,
            int x, int y, int nWidth, int nHeight,
            IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr DefWindowProc(
            IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int GetMessage(
            out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PeekMessage(
            out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetTimer(
            IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void SetLastError(uint dwErrCode);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern IntPtr CreateSolidBrush(uint crColor);

        [DllImport("gdi32.dll", SetLastError = true)]
        public static extern bool DeleteObject(IntPtr hObject);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int FillRect(IntPtr hDC, ref RECT lprc, IntPtr hbr);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr GetDC(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool ValidateRect(IntPtr hWnd, IntPtr lpRect);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr LoadCursor(IntPtr hInstance, IntPtr lpCursorName);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
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
