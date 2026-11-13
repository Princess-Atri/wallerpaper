// ============================================================================
//  WorkerW 桌面挂靠最小可运行 Demo
//  ---------------------------------------------------------------------------
//  目标框架 : .NET 8  (net8.0-windows, x64)
//  依赖     : 无 UI 框架，仅 Win32 P/Invoke
//
//  运行:
//    dotnet run --project samples/WorkerW -- --seconds 8
//    dotnet run --project samples/WorkerW -- --dry-run
//
//  官方文档 / 参考:
//    SetParent:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent
//    MapWindowPoints:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-mapwindowpoints
//    SetWindowPos:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowpos
//    FindWindowEx:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-findwindowexw
//    EnumWindows:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-enumwindows
//    SendMessageTimeout:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw
//    SetWindowLongPtr:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setwindowlongptrw
//    Extended Window Styles:
//      https://learn.microsoft.com/en-us/windows/win32/winmsg/extended-window-styles
//    RegisterWindowMessage:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registerwindowmessagew
//    SetProcessDpiAwarenessContext:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setprocessdpiawarenesscontext
//    GetMonitorInfo:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getmonitorinfow
//    SetLayeredWindowAttributes:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setlayeredwindowattributes
//    WM_NCHITTEST / HTTRANSPARENT:
//      https://learn.microsoft.com/en-us/windows/win32/inputdev/wm-nchittest
//    ClientToScreen:
//      https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-clienttoscreen
//
//  Lively 参考 (GPL-3.0, 本项目同样 GPL-3.0):
//    WinDesktopCore.SetupDesktopLayer() / TryAttachToDesktop():
//      https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Core/WinDesktopCore.cs
//    WndProcMsgWindow (TaskbarCreated):
//      https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively/Views/WindowMsg/WndProcMsgWindow.xaml.cs
//    WindowUtil.TrySetParent / RemoveWindowFromTaskbar:
//      https://github.com/lively-community/lively/blob/25a0a5f4ed1988ff42117d3a17feb321062237fe/src/Lively/Lively.Common/Helpers/WindowUtil.cs
//
//  重要说明:
//    0x052C 不是 MSDN 文档化的 Win32 API，而是 Explorer/Progman 的私有消息。
//    它在 Lively 源码中可以验证；不同 Windows build 行为可能变化，必须准备
//    "找不到 WorkerW 时使用 Progman 作为父窗口" 的兜底路径。
// ============================================================================

using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace WorkerWDemo;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            Options.PrintUsage();
            return 0;
        }

        try
        {
            // 必须在创建任何窗口之前设置 DPI 感知。
            // 这样 GetMonitorInfo().rcMonitor 和 SetWindowPos 都使用物理像素。
            DpiAwareness.EnablePerMonitorV2();

            var app = new WorkerWApp(options.AutoExitSeconds, options.DryRun);
            return app.Run();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FATAL: {ex}");
            return 1;
        }
    }
}

internal sealed class Options
{
    public int AutoExitSeconds { get; private set; }
    public bool DryRun { get; private set; }
    public bool ShowHelp { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--seconds":
                case "-s":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var seconds) && seconds > 0)
                        o.AutoExitSeconds = seconds;
                    break;
                case "--dry-run":
                    o.DryRun = true;
                    break;
                case "--help":
                case "-h":
                    o.ShowHelp = true;
                    break;
            }
        }

        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("WorkerW desktop attach demo (.NET 8)");
        Console.WriteLine();
        Console.WriteLine("  dotnet run --project samples/WorkerW -- [--seconds N] [--dry-run]");
        Console.WriteLine();
        Console.WriteLine("  --seconds N   在 N 秒后自动退出（用于验证清理逻辑）");
        Console.WriteLine("  --dry-run     只发现 Progman/WorkerW/DefView，不创建/挂靠窗口");
        Console.WriteLine("  --help        显示帮助");
    }
}

internal static class DpiAwareness
{
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4
    private static readonly IntPtr PerMonitorV2 = new(-4);

    public static void EnablePerMonitorV2()
    {
        // Win10 1703+: 首选 PerMonitorV2。
        try
        {
            if (NativeDpi.SetProcessDpiAwarenessContext(PerMonitorV2))
            {
                NativeDpi.SetThreadDpiAwarenessContext(PerMonitorV2);
                Console.WriteLine("[DPI] SetProcessDpiAwarenessContext(PerMonitorV2) succeeded.");
                return;
            }

            Console.WriteLine($"[DPI] SetProcessDpiAwarenessContext failed: {Marshal.GetLastWin32Error()}");
        }
        catch (EntryPointNotFoundException)
        {
            Console.WriteLine("[DPI] SetProcessDpiAwarenessContext not available.");
        }

        // Win8.1+ fallback: shcore!SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE = 2)
        try
        {
            var hr = NativeDpi.SetProcessDpiAwareness(2);
            if (hr == 0)
            {
                Console.WriteLine("[DPI] SetProcessDpiAwareness(PerMonitor) succeeded.");
                return;
            }

            Console.WriteLine($"[DPI] SetProcessDpiAwareness failed: hr=0x{hr:X8}");
        }
        catch (DllNotFoundException)
        {
            Console.WriteLine("[DPI] shcore.dll not found.");
        }
        catch (EntryPointNotFoundException)
        {
            Console.WriteLine("[DPI] SetProcessDpiAwareness not available.");
        }

        // Win7+ fallback: system DPI aware (not per-monitor).
        if (NativeDpi.SetProcessDPIAware())
            Console.WriteLine("[DPI] SetProcessDPIAware succeeded (system DPI aware fallback).");
        else
            Console.WriteLine($"[DPI] SetProcessDPIAware failed: {Marshal.GetLastWin32Error()}");
    }

    private static class NativeDpi
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDPIAware();
    }
}

internal sealed class WorkerWApp
{
    private const string MessageClassName = "Wallerpaper.WorkerW.Message";
    private const string WallpaperClassName = "Wallerpaper.WorkerW.Wallpaper";

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;

    private const long WS_POPUP = 0x80000000L;
    private const long WS_CHILD = 0x40000000L;

    private const long WS_EX_LAYERED = 0x00080000L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;
    private const long WS_EX_TOOLWINDOW = 0x00000080L;
    private const long WS_EX_NOACTIVATE = 0x08000000L;
    private const long WS_EX_NOREDIRECTIONBITMAP = 0x00200000L;

    private const uint LWA_ALPHA = 0x00000002;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private static readonly IntPtr HwndBottom = new(1);
    private static readonly IntPtr HwndTop = IntPtr.Zero;

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
    private const uint WM_NCHITTEST = 0x0084;
    private const uint WM_NCDESTROY = 0x0082;
    private const uint WM_ENDSESSION = 0x0016;

    private const int TimerWatchdog = 1;
    private const int TimerReattach = 2;

    private const int IDC_ARROW = 32512;
    private const int HTTRANSPARENT = -1;
    private const int ErrorClassAlreadyExists = 1410;

    private const uint SMTO_NORMAL = 0;

    private readonly int _autoExitSeconds;
    private readonly bool _dryRun;
    private readonly IntPtr _hInstance;

    private readonly WndProc _messageWndProc;
    private readonly WndProc _wallpaperWndProc;
    private readonly EnumWindowsProc _enumWindowsProc;

    private IntPtr _messageHwnd;
    private IntPtr _wallpaperHwnd;
    private IntPtr _brush;

    private bool _messageClassRegistered;
    private bool _wallpaperClassRegistered;

    private IntPtr _progman;
    private IntPtr _workerW;
    private IntPtr _defView;
    private IntPtr _parent;
    private IntPtr _insertAfter;
    private bool _isRaisedDesktop;

    private RECT _primaryRect;
    private uint _taskbarCreatedMessage;
    private uint _explorerPid;

    private int _reattachAttempts;
    private int _reattachScheduled;
    private bool _isShuttingDown;
    private bool _detaching;
    private bool _fatalAttachError;
    private uint _lastDpi;
    private Timer? _autoExitTimer;

    public WorkerWApp(int autoExitSeconds, bool dryRun)
    {
        _autoExitSeconds = autoExitSeconds;
        _dryRun = dryRun;
        _hInstance = NativeMethods.GetModuleHandle(null);
        _messageWndProc = MessageWndProc;
        _wallpaperWndProc = WallpaperWndProc;
        _enumWindowsProc = EnumWindowsCallback;
    }

    public int Run()
    {
        try
        {
            CreateMessageWindow();
            _explorerPid = GetExplorerPid();

            var found = FindDesktop();
            PrintDesktopState(found);

            if (_dryRun)
                return found ? 0 : 1;

            if (!found)
            {
                Log("WorkerW/Progman not ready; will retry from the message loop.");
                ScheduleReattach();
            }
            else if (!Attach())
            {
                if (_fatalAttachError)
                {
                    Log("Initial attach failed with a fatal error (see above); auto-retry disabled.");
                }
                else
                {
                    Log("Initial attach failed; will retry from the message loop.");
                    ScheduleReattach();
                }
            }

            Console.CancelKeyPress += OnCancelKeyPress;
            SetupAutoExit();

            Console.WriteLine();
            Console.WriteLine(">>> Demo is running. The desktop should be filled with a dark blue window.");
            Console.WriteLine(">>> Click/double-click desktop icons to verify they still work.");
            Console.WriteLine(">>> Press Ctrl+C to exit, or wait for --seconds timeout.");
            Console.WriteLine();

            RunMessageLoop();
            return 0;
        }
        finally
        {
            Cleanup();
        }
    }

    // ---------------------------------------------------------------------
    // Window / message loop
    // ---------------------------------------------------------------------

    private void CreateMessageWindow()
    {
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = _messageWndProc,
            hInstance = _hInstance,
            lpszClassName = MessageClassName
        };

        var atom = NativeMethods.RegisterClassEx(ref wc);
        if (atom == 0 && Marshal.GetLastWin32Error() != ErrorClassAlreadyExists)
            throw new InvalidOperationException($"RegisterClassEx(message) failed: {Marshal.GetLastWin32Error()}");

        _messageClassRegistered = atom != 0;

        _messageHwnd = NativeMethods.CreateWindowEx(
            (uint)WS_EX_TOOLWINDOW,
            MessageClassName,
            "Wallerpaper WorkerW Message Window",
            (uint)WS_POPUP,
            0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (_messageHwnd == IntPtr.Zero)
            throw new InvalidOperationException($"CreateWindowEx(message) failed: {Marshal.GetLastWin32Error()}");

        _taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
        NativeMethods.SetTimer(_messageHwnd, (IntPtr)TimerWatchdog, 1000, IntPtr.Zero);

        Log($"Message window created: hwnd=0x{_messageHwnd:X}, TaskbarCreated=0x{_taskbarCreatedMessage:X4}");
        Log($"Message window DPI: {SafeGetDpi(_messageHwnd)}");
    }

    private void RunMessageLoop()
    {
        while (true)
        {
            var result = NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0);
            if (result == -1)
            {
                Log($"GetMessage failed: {Marshal.GetLastWin32Error()}");
                break;
            }

            if (result == 0)
                break; // WM_QUIT

            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    private IntPtr MessageWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // TaskbarCreated 是运行时注册的消息，不是常量。
        if (msg == _taskbarCreatedMessage)
        {
            OnTaskbarCreated();
            return IntPtr.Zero;
        }

        switch (msg)
        {
            case WM_TIMER:
                OnTimer(wParam);
                return IntPtr.Zero;

            case WM_DISPLAYCHANGE:
                Log("[WM_DISPLAYCHANGE] display changed; reposition/re-attach.");
                RepositionOrReattach("WM_DISPLAYCHANGE");
                return IntPtr.Zero;

            case WM_DPICHANGED:
                Log("[WM_DPICHANGED] DPI changed; reposition.");
                RepositionOrReattach("WM_DPICHANGED");
                return IntPtr.Zero;

            case WM_ENDSESSION:
                if (wParam != IntPtr.Zero)
                    Shutdown("WM_ENDSESSION");
                return IntPtr.Zero;

            case WM_CLOSE:
                Shutdown("WM_CLOSE");
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
                // 背景由 WM_PAINT 填充，避免闪烁。
                return new IntPtr(1);

            case WM_PAINT:
                Paint(hWnd);
                return IntPtr.Zero;

            case WM_NCHITTEST:
                // 额外的点击穿透保险；主要穿透来自 WS_EX_TRANSPARENT 与
                // z-order 在 SHELLDLL_DefView 之下。
                return new IntPtr(HTTRANSPARENT);

            case WM_NCDESTROY:
                Log($"[WM_NCDESTROY] wallpaper hwnd=0x{hWnd:X} destroyed (detaching={_detaching}).");
                if (_wallpaperHwnd == hWnd)
                    _wallpaperHwnd = IntPtr.Zero;
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
            if (NativeMethods.GetClientRect(hWnd, out var rc))
                NativeMethods.FillRect(hdc, ref rc, _brush);
        }
        finally
        {
            NativeMethods.ReleaseDC(hWnd, hdc);
            NativeMethods.ValidateRect(hWnd, IntPtr.Zero);
        }
    }

    // ---------------------------------------------------------------------
    // WorkerW discovery / attach
    // ---------------------------------------------------------------------

    private bool FindDesktop()
    {
        _progman = NativeMethods.FindWindow("Progman", null);
        if (_progman == IntPtr.Zero)
        {
            Log("FindWindow(\"Progman\") returned NULL.");
            return false;
        }

        _primaryRect = GetPrimaryMonitorRect();

        // 0x052C 是未文档化的 Progman 消息；Lively 使用 wParam=0xD, lParam=0x1。
        // 官方文档只能验证 SendMessageTimeout 本身：
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagetimeoutw
        _workerW = IntPtr.Zero;
        _defView = IntPtr.Zero;

        Log("Sending undocumented Progman message 0x052C (0xD, 0x1)...");
        var sent = NativeMethods.SendMessageTimeout(
            _progman, 0x052C, new IntPtr(0xD), new IntPtr(0x1),
            SMTO_NORMAL, 1000, out var result);
        Log($"0x052C returned=0x{sent:X}, result=0x{result:X}");

        TryFindWorkerW();

        if (_workerW == IntPtr.Zero)
        {
            // 某些系统/版本对 0xD/0x1 参数不响应，用经典 0,0 再试一次。
            Log("WorkerW not found; retry 0x052C with (0, 0)...");
            NativeMethods.SendMessageTimeout(
                _progman, 0x052C, IntPtr.Zero, IntPtr.Zero,
                SMTO_NORMAL, 1000, out _);
            TryFindWorkerW();
        }

        if (_workerW == IntPtr.Zero)
        {
            // 某些 Win11 build 没有独立 WorkerW；尝试 Progman 下的 WorkerW。
            var candidate = NativeMethods.FindWindowEx(_progman, IntPtr.Zero, "WorkerW", null);
            if (candidate != IntPtr.Zero && !HasDefView(candidate))
            {
                Log($"Using WorkerW under Progman: 0x{candidate:X}");
                _workerW = candidate;
            }
        }

        if (_defView == IntPtr.Zero)
            _defView = NativeMethods.FindWindowEx(_progman, IntPtr.Zero, "SHELLDLL_DefView", null);

        _isRaisedDesktop = HasExStyle(_progman, WS_EX_NOREDIRECTIONBITMAP);
        Log($"Progman ex-style raised={_isRaisedDesktop}; DefView=0x{_defView:X}");

        if (_isRaisedDesktop && _defView != IntPtr.Zero && NativeMethods.GetParent(_defView) == _progman)
        {
            // Win11 raised desktop：壁纸作为 Progman 的 child，z-order 在 DefView 下方。
            _parent = _progman;
            _insertAfter = _defView;
        }
        else if (_workerW != IntPtr.Zero)
        {
            // 经典路径：DefView 所在 WorkerW 后面的那个 WorkerW。
            _parent = _workerW;
            _insertAfter = HwndBottom;
        }
        else if (_defView != IntPtr.Zero)
        {
            // 没有 WorkerW，但 DefView 在 Progman 下：用 Progman 兜底。
            _parent = _progman;
            _insertAfter = _defView;
            _isRaisedDesktop = true;
        }
        else
        {
            Log("No usable WorkerW/DefView parent found.");
            _parent = IntPtr.Zero;
            _insertAfter = IntPtr.Zero;
            return false;
        }

        Log($"Desktop target: parent=0x{_parent:X}, insertAfter=0x{_insertAfter:X}, " +
            $"raised={_isRaisedDesktop}, primary={RectToString(_primaryRect)}");
        return true;
    }

    private void TryFindWorkerW()
    {
        NativeMethods.EnumWindows(_enumWindowsProc, IntPtr.Zero);
    }

    private bool EnumWindowsCallback(IntPtr top, IntPtr lParam)
    {
        var defView = NativeMethods.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
        if (defView == IntPtr.Zero)
            return true;

        _defView = defView;

        // FindWindowEx(NULL, top, "WorkerW") 返回 top 之后的顶层 WorkerW；
        // 这个通常是 DefView 后面的壁纸承载窗口。
        var after = top;
        while (true)
        {
            var candidate = NativeMethods.FindWindowEx(IntPtr.Zero, after, "WorkerW", null);
            if (candidate == IntPtr.Zero)
                break;

            if (IsValidWorkerW(candidate))
            {
                _workerW = candidate;
                Log($"Found WorkerW: 0x{candidate:X}, rect={GetWindowRectString(candidate)}");
                return false; // stop enumeration
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

        // 排除“图标 WorkerW”（它自己包含 SHELLDLL_DefView）。
        if (HasDefView(hwnd))
            return false;

        if (!NativeMethods.GetWindowRect(hwnd, out var rect))
            return false;

        if (rect.Right <= rect.Left || rect.Bottom <= rect.Top)
            return false;

        return Intersects(rect, _primaryRect);
    }

    private bool HasDefView(IntPtr hwnd) =>
        NativeMethods.FindWindowEx(hwnd, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero;

    private bool Attach()
    {
        if (_dryRun)
            return true;

        if (_parent == IntPtr.Zero && !FindDesktop())
            return false;

        if (_wallpaperHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_wallpaperHwnd))
        {
            _wallpaperHwnd = CreateWallpaperWindow();
            if (_wallpaperHwnd == IntPtr.Zero)
                return false;
        }

        _primaryRect = GetPrimaryMonitorRect();

        var insertAfter = _insertAfter;
        if (_isRaisedDesktop && insertAfter != IntPtr.Zero && !NativeMethods.IsWindow(insertAfter))
        {
            Log("DefView handle is invalid; fallback to HWND_BOTTOM.");
            insertAfter = HwndBottom;
        }

        ApplyAttachmentStyle(_wallpaperHwnd);

        // 屏幕坐标 -> 父窗口客户区坐标。
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-mapwindowpoints
        var pt = MapScreenToParent(_parent, _primaryRect.Left, _primaryRect.Top);

        // SetParent 前先清 WS_POPUP、设 WS_CHILD（MSDN SetParent 的 Remarks 明确要求）。
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setparent
        NativeMethods.SetLastError(0);
        var oldParent = NativeMethods.SetParent(_wallpaperHwnd, _parent);
        var setParentError = Marshal.GetLastWin32Error();

        if (NativeMethods.GetParent(_wallpaperHwnd) != _parent)
        {
            Log($"SetParent failed: oldParent=0x{oldParent:X}, error={setParentError}");
            if (setParentError == 5)
            {
                _fatalAttachError = true;
                Log("SetParent returned ACCESS_DENIED (5). Run this demo as a normal user at the same " +
                    "integrity level as Explorer; elevated/low-integrity sandboxes cannot reparent into Explorer.");
            }

            DestroyWallpaperWindow();
            return false;
        }

        var width = _primaryRect.Right - _primaryRect.Left;
        var height = _primaryRect.Bottom - _primaryRect.Top;

        var ok = NativeMethods.SetWindowPos(
            _wallpaperHwnd, insertAfter,
            pt.X, pt.Y, width, height,
            SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

        if (!ok)
        {
            Log($"SetWindowPos failed: {Marshal.GetLastWin32Error()}");
            DestroyWallpaperWindow();
            return false;
        }

        NativeMethods.ShowWindow(_wallpaperHwnd, 4 /* SW_SHOWNOACTIVATE */);
        NativeMethods.UpdateWindow(_wallpaperHwnd);

        Log($"Attached wallpaper hwnd=0x{_wallpaperHwnd:X} to parent=0x{_parent:X}, " +
            $"rect=({pt.X},{pt.Y},{width},{height}), raised={_isRaisedDesktop}");

        VerifyAttachment();
        _lastDpi = SafeGetDpi(_wallpaperHwnd);
        _explorerPid = GetExplorerPid();
        return true;
    }

    private IntPtr CreateWallpaperWindow()
    {
        if (_brush == IntPtr.Zero)
        {
            // COLORREF = 0x00BBGGRR; RGB(24, 32, 48) => 0x00302018
            _brush = NativeMethods.CreateSolidBrush(0x00302018);
            if (_brush == IntPtr.Zero)
                throw new InvalidOperationException($"CreateSolidBrush failed: {Marshal.GetLastWin32Error()}");
        }

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
                throw new InvalidOperationException($"RegisterClassEx(wallpaper) failed: {Marshal.GetLastWin32Error()}");

            _wallpaperClassRegistered = true;
        }

        var exStyle = (uint)(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE);

        var hwnd = NativeMethods.CreateWindowEx(
            exStyle,
            WallpaperClassName,
            "Wallerpaper WorkerW Demo",
            (uint)WS_POPUP,
            0, 0, 100, 100,
            IntPtr.Zero, IntPtr.Zero, _hInstance, IntPtr.Zero);

        if (hwnd == IntPtr.Zero)
        {
            Log($"CreateWindowEx(wallpaper) failed: {Marshal.GetLastWin32Error()}");
            return IntPtr.Zero;
        }

        // WS_EX_LAYERED 必须配合 SetLayeredWindowAttributes 或 UpdateLayeredWindow。
        // https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setlayeredwindowattributes
        if (!NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA))
            Log($"SetLayeredWindowAttributes failed: {Marshal.GetLastWin32Error()}");

        Log($"Wallpaper window created: hwnd=0x{hwnd:X}, exStyle=0x{exStyle:X8}");
        return hwnd;
    }

    private void ApplyAttachmentStyle(IntPtr hwnd)
    {
        var style = NativeMethods.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        var newStyle = (style & ~WS_POPUP) | WS_CHILD;
        NativeMethods.SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(newStyle));

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        exStyle |= WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE;
        NativeMethods.SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(exStyle));

        NativeMethods.SetLayeredWindowAttributes(hwnd, 0, 255, LWA_ALPHA);

        var actualStyle = NativeMethods.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
        var actualExStyle = NativeMethods.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        Log($"Style: 0x{actualStyle:X8} (child={((actualStyle & WS_CHILD) != 0)}), " +
            $"exStyle=0x{actualExStyle:X8}");
    }

    private void VerifyAttachment()
    {
        if (_wallpaperHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_wallpaperHwnd))
            return;

        var parent = NativeMethods.GetParent(_wallpaperHwnd);
        NativeMethods.GetWindowRect(_wallpaperHwnd, out var rect);
        NativeMethods.GetClientRect(_wallpaperHwnd, out var client);

        Log($"Verify: parent=0x{parent:X} (expected 0x{_parent:X}), " +
            $"windowRect={RectToString(rect)}, client={client.Right - client.Left}x{client.Bottom - client.Top}, " +
            $"primary={RectToString(_primaryRect)}");

        if (parent != _parent)
            Log("WARNING: parent mismatch!");

        if (Math.Abs(rect.Left - _primaryRect.Left) > 1 ||
            Math.Abs(rect.Top - _primaryRect.Top) > 1 ||
            Math.Abs(rect.Right - _primaryRect.Right) > 1 ||
            Math.Abs(rect.Bottom - _primaryRect.Bottom) > 1)
        {
            Log("WARNING: window rect does not match primary monitor rect (DPI virtualization?).");
        }

        VerifyClickThrough();
        Log($"Wallpaper DPI: {SafeGetDpi(_wallpaperHwnd)}");
    }

    private void VerifyClickThrough()
    {
        var points = new[]
        {
            new POINT { X = _primaryRect.Left + (_primaryRect.Right - _primaryRect.Left) / 2,
                        Y = _primaryRect.Top + (_primaryRect.Bottom - _primaryRect.Top) / 2 },
            new POINT { X = _primaryRect.Left + 10, Y = _primaryRect.Top + 10 },
            new POINT { X = _primaryRect.Right - 10, Y = _primaryRect.Top + 10 }
        };

        foreach (var pt in points)
        {
            var hit = NativeMethods.WindowFromPoint(pt);
            var cls = GetClassName(hit);
            Log($"WindowFromPoint({pt.X},{pt.Y}) -> 0x{hit:X} [{cls}]");
            if (hit == _wallpaperHwnd)
                Log("WARNING: WindowFromPoint hit the wallpaper window; click-through/z-order may be wrong.");
        }
    }

    private void RepositionOrReattach(string reason)
    {
        if (_isShuttingDown || _dryRun)
            return;

        if (_wallpaperHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_wallpaperHwnd) ||
            _parent == IntPtr.Zero || !NativeMethods.IsWindow(_parent) ||
            NativeMethods.GetParent(_wallpaperHwnd) != _parent)
        {
            ScheduleReattach();
            return;
        }

        _primaryRect = GetPrimaryMonitorRect();

        var insertAfter = _isRaisedDesktop && NativeMethods.IsWindow(_insertAfter)
            ? _insertAfter
            : HwndBottom;

        var pt = MapScreenToParent(_parent, _primaryRect.Left, _primaryRect.Top);

        var ok = NativeMethods.SetWindowPos(
            _wallpaperHwnd, insertAfter,
            pt.X, pt.Y,
            _primaryRect.Right - _primaryRect.Left,
            _primaryRect.Bottom - _primaryRect.Top,
            SWP_NOACTIVATE | SWP_FRAMECHANGED | SWP_SHOWWINDOW);

        Log($"[{reason}] Reposition ok={ok}, rect={RectToString(_primaryRect)}");
        if (!ok)
            ScheduleReattach();
        else
            VerifyAttachment();
    }

    // ---------------------------------------------------------------------
    // Explorer restart / watchdog
    // ---------------------------------------------------------------------

    private void OnTaskbarCreated()
    {
        var pid = GetExplorerPid();
        Log($"[TaskbarCreated] explorer pid={pid}, previous={_explorerPid}");

        // TaskbarCreated 也会在 DPI/显示设置变化时广播；PID 没变时不做完整重挂。
        if (_explorerPid != 0 && pid == _explorerPid)
        {
            Log("[TaskbarCreated] same explorer PID; verify/reposition only.");
            RepositionOrReattach("TaskbarCreated(same pid)");
            return;
        }

        _explorerPid = pid;
        _reattachAttempts = 0;
        ScheduleReattach();
    }

    private void OnTimer(IntPtr timerId)
    {
        var id = timerId.ToInt32();

        if (id == TimerWatchdog)
        {
            Watchdog();
            return;
        }

        if (id == TimerReattach)
        {
            NativeMethods.KillTimer(_messageHwnd, (IntPtr)TimerReattach);
            Interlocked.Exchange(ref _reattachScheduled, 0);
            Reattach("timer");
        }
    }

    private void Watchdog()
    {
        if (_isShuttingDown || _dryRun || _fatalAttachError)
            return;

        var parentOk = _parent != IntPtr.Zero && NativeMethods.IsWindow(_parent);
        var hwndOk = _wallpaperHwnd != IntPtr.Zero && NativeMethods.IsWindow(_wallpaperHwnd);
        var parentMatch = hwndOk && NativeMethods.GetParent(_wallpaperHwnd) == _parent;

        if (!parentOk || !hwndOk || !parentMatch)
        {
            Log($"Watchdog: parentOk={parentOk}, hwndOk={hwndOk}, parentMatch={parentMatch}; scheduling re-attach.");
            ScheduleReattach();
            return;
        }

        // 不依赖 WM_DPICHANGED 的兜底：每秒比较一次显示器物理矩形和窗口 DPI。
        var currentRect = GetPrimaryMonitorRect();
        if (!RectEquals(currentRect, _primaryRect))
        {
            Log($"Watchdog: primary rect changed {RectToString(_primaryRect)} -> {RectToString(currentRect)}.");
            _primaryRect = currentRect;
            RepositionOrReattach("watchdog-rect");
            return;
        }

        var dpi = SafeGetDpi(_wallpaperHwnd);
        if (dpi != 0 && _lastDpi != 0 && dpi != _lastDpi)
        {
            Log($"Watchdog: DPI changed {_lastDpi} -> {dpi}.");
            _lastDpi = dpi;
            RepositionOrReattach("watchdog-dpi");
        }
    }

    private void ScheduleReattach()
    {
        if (_isShuttingDown || _dryRun || _fatalAttachError || _messageHwnd == IntPtr.Zero)
            return;

        if (_reattachAttempts > 20)
        {
            Log("Too many re-attach attempts; watchdog will keep trying but without scheduling new timers.");
            return;
        }

        if (Interlocked.Exchange(ref _reattachScheduled, 1) == 1)
            return;

        _reattachAttempts++;
        Log($"Re-attach scheduled (attempt {_reattachAttempts}) in 500ms.");
        NativeMethods.SetTimer(_messageHwnd, (IntPtr)TimerReattach, 500, IntPtr.Zero);
    }

    private void Reattach(string reason)
    {
        if (_isShuttingDown || _dryRun)
            return;

        Log($"[{reason}] re-attach: destroying old window and re-discovering desktop...");
        DestroyWallpaperWindow();

        _parent = IntPtr.Zero;
        _workerW = IntPtr.Zero;
        _defView = IntPtr.Zero;
        _insertAfter = IntPtr.Zero;
        _isRaisedDesktop = false;

        if (!FindDesktop() || !Attach())
        {
            Log($"[{reason}] re-attach failed; retrying.");
            ScheduleReattach();
            return;
        }

        _reattachAttempts = 0;
        Log($"[{reason}] re-attach succeeded.");
    }

    private void OnCancelKeyPress(object? sender, ConsoleCancelEventArgs e)
    {
        e.Cancel = true;
        if (_messageHwnd != IntPtr.Zero)
            NativeMethods.PostMessage(_messageHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
    }

    private void SetupAutoExit()
    {
        if (_autoExitSeconds <= 0)
            return;

        _autoExitTimer = new Timer(_ =>
        {
            if (!_isShuttingDown && _messageHwnd != IntPtr.Zero)
                NativeMethods.PostMessage(_messageHwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }, null, _autoExitSeconds * 1000, Timeout.Infinite);
    }

    // ---------------------------------------------------------------------
    // Shutdown / cleanup
    // ---------------------------------------------------------------------

    private void Shutdown(string reason)
    {
        if (_isShuttingDown)
            return;

        _isShuttingDown = true;
        Log($"[{reason}] shutting down...");

        _autoExitTimer?.Dispose();
        _autoExitTimer = null;

        if (_messageHwnd != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_messageHwnd, (IntPtr)TimerWatchdog);
            NativeMethods.KillTimer(_messageHwnd, (IntPtr)TimerReattach);
        }

        DestroyWallpaperWindow();

        if (_messageHwnd != IntPtr.Zero)
        {
            var hwnd = _messageHwnd;
            _messageHwnd = IntPtr.Zero;
            NativeMethods.DestroyWindow(hwnd);
        }

        NativeMethods.PostQuitMessage(0);
    }

    private void Cleanup()
    {
        _isShuttingDown = true;
        _autoExitTimer?.Dispose();
        _autoExitTimer = null;
        Console.CancelKeyPress -= OnCancelKeyPress;

        DestroyWallpaperWindow();

        if (_messageHwnd != IntPtr.Zero)
        {
            var hwnd = _messageHwnd;
            _messageHwnd = IntPtr.Zero;
            NativeMethods.DestroyWindow(hwnd);
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

        Log("Cleanup complete.");
    }

    private void DestroyWallpaperWindow()
    {
        if (_wallpaperHwnd == IntPtr.Zero || !NativeMethods.IsWindow(_wallpaperHwnd))
        {
            _wallpaperHwnd = IntPtr.Zero;
            return;
        }

        _detaching = true;
        try
        {
            var hwnd = _wallpaperHwnd;
            Log($"Detaching wallpaper hwnd=0x{hwnd:X} from parent=0x{_parent:X}...");

            NativeMethods.ShowWindow(hwnd, 0 /* SW_HIDE */);

            NativeMethods.SetLastError(0);
            NativeMethods.SetParent(hwnd, IntPtr.Zero);

            var style = NativeMethods.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            var newStyle = (style & ~WS_CHILD) | WS_POPUP;
            NativeMethods.SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(newStyle));

            NativeMethods.SetWindowPos(
                hwnd, HwndBottom, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

            var parentAfter = NativeMethods.GetParent(hwnd);
            Log($"Detached: parentAfter=0x{parentAfter:X}, " +
                $"styleChild={((NativeMethods.GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64() & WS_CHILD) != 0)}");

            NativeMethods.DestroyWindow(hwnd);
            Log($"Wallpaper window destroyed: 0x{hwnd:X}");
        }
        finally
        {
            _wallpaperHwnd = IntPtr.Zero;
            _detaching = false;
        }
    }

    // ---------------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------------

    private RECT GetPrimaryMonitorRect()
    {
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

    private POINT MapScreenToParent(IntPtr parent, int x, int y)
    {
        var pt = new POINT { X = x, Y = y };
        var mapped = NativeMethods.MapWindowPoints(IntPtr.Zero, parent, ref pt, 1);
        if (mapped != 0)
            return pt;

        // 某些跨进程/受限环境下 MapWindowPoints 返回 0；用父窗口客户区原点兜底。
        var origin = new POINT { X = 0, Y = 0 };
        if (NativeMethods.ClientToScreen(parent, ref origin))
        {
            var fallback = new POINT { X = x - origin.X, Y = y - origin.Y };
            Log($"MapWindowPoints returned 0 (err={Marshal.GetLastWin32Error()}); " +
                $"fallback ClientToScreen -> ({fallback.X},{fallback.Y})");
            return fallback;
        }

        if (NativeMethods.GetWindowRect(parent, out var parentRect))
        {
            var fallback = new POINT { X = x - parentRect.Left, Y = y - parentRect.Top };
            Log($"MapWindowPoints/ClientToScreen failed; fallback parent rect -> ({fallback.X},{fallback.Y})");
            return fallback;
        }

        Log("MapWindowPoints/ClientToScreen/GetWindowRect all failed; using (0,0).");
        return new POINT { X = 0, Y = 0 };
    }

    private static bool RectEquals(RECT a, RECT b) =>
        a.Left == b.Left && a.Top == b.Top && a.Right == b.Right && a.Bottom == b.Bottom;

    private static bool HasExStyle(IntPtr hwnd, long style) =>
        (NativeMethods.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64() & style) != 0;

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

    private static string GetWindowRectString(IntPtr hwnd) =>
        NativeMethods.GetWindowRect(hwnd, out var rc) ? RectToString(rc) : "<invalid>";

    private static string RectToString(RECT r) =>
        $"({r.Left},{r.Top})-({r.Right},{r.Bottom}) [{r.Right - r.Left}x{r.Bottom - r.Top}]";

    private static uint SafeGetDpi(IntPtr hwnd)
    {
        try
        {
            return NativeMethods.GetDpiForWindow(hwnd);
        }
        catch (EntryPointNotFoundException)
        {
            return 0;
        }
    }

    private static uint GetExplorerPid()
    {
        var tray = NativeMethods.FindWindow("Shell_TrayWnd", null);
        if (tray == IntPtr.Zero)
            return 0;

        NativeMethods.GetWindowThreadProcessId(tray, out var pid);
        return pid;
    }

    private void PrintDesktopState(bool found)
    {
        Log($"Progman=0x{_progman:X}, WorkerW=0x{_workerW:X}, DefView=0x{_defView:X}, " +
            $"raised={_isRaisedDesktop}, parent=0x{_parent:X}, insertAfter=0x{_insertAfter:X}, " +
            $"primary={RectToString(_primaryRect)}, found={found}");
    }

    private void Log(string message) =>
        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");

    private delegate IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    // =====================================================================
    // P/Invoke
    // =====================================================================

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

        [DllImport("shcore.dll")]
        public static extern int SetProcessDpiAwareness(int value);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool SetProcessDPIAware();

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

        [DllImport("user32.dll")]
        public static extern IntPtr WindowFromPoint(POINT point);

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

        [DllImport("user32.dll")]
        public static extern bool TranslateMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern IntPtr DispatchMessage(ref MSG lpMsg);

        [DllImport("user32.dll")]
        public static extern void PostQuitMessage(int nExitCode);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern uint RegisterWindowMessage(string lpString);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr SetTimer(
            IntPtr hWnd, IntPtr nIDEvent, uint uElapse, IntPtr lpTimerFunc);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool KillTimer(IntPtr hWnd, IntPtr uIDEvent);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr GetModuleHandle(string? lpModuleName);

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
        public static extern bool SetLayeredWindowAttributes(
            IntPtr hWnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int nIndex);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern void SetLastError(uint dwErrCode);
    }

    // =====================================================================
    // Native structs
    // =====================================================================

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
