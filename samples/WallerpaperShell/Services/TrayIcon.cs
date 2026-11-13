using System.Runtime.InteropServices;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Win32 Shell_NotifyIcon tray icon. It uses a message-only window so it does
/// not depend on the WinUI window lifetime. Call from the UI thread.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAY = WM_APP + 1;
    private const int WM_COMMAND = 0x0111;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_CONTEXTMENU = 0x007B;
    private const int WM_NULL = 0x0000;
    private const int NIF_MESSAGE = 0x01;
    private const int NIF_ICON = 0x02;
    private const int NIF_TIP = 0x04;
    private const int NIM_ADD = 0x00;
    private const int NIM_MODIFY = 0x01;
    private const int NIM_DELETE = 0x02;
    private const uint MF_STRING = 0x0000;
    private const uint MF_SEPARATOR = 0x0800;
    private const uint MF_GRAYED = 0x0001;
    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_NONOTIFY = 0x0080;
    private const int IDI_APPLICATION = 32512;
    private const int ID_SHOW = 1;
    private const int ID_TOGGLE = 2;
    private const int ID_SETTINGS = 3;
    private const int ID_EXIT = 4;
    private const int ID_WALLPAPER_BASE = 1000;

    private readonly Func<IReadOnlyList<WallpaperItem>> _wallpapers;
    private readonly Func<bool> _isPaused;
    private readonly Action _show;
    private readonly Action _toggle;
    private readonly Action _settings;
    private readonly Action _exit;
    private readonly Action<WallpaperItem> _selectWallpaper;
    private readonly WndProcDelegate _wndProc;
    private readonly IntPtr _hwnd;
    private IntPtr _oldWndProc;
    private bool _paused;
    private bool _disposed;

    public TrayIcon(
        Func<IReadOnlyList<WallpaperItem>> wallpapers,
        Func<bool> isPaused,
        Action show,
        Action toggle,
        Action settings,
        Action exit,
        Action<WallpaperItem> selectWallpaper)
    {
        _wallpapers = wallpapers;
        _isPaused = isPaused;
        _show = show;
        _toggle = toggle;
        _settings = settings;
        _exit = exit;
        _selectWallpaper = selectWallpaper;
        _wndProc = WndProc;

        // A hidden top-level window (not HWND_MESSAGE) can own a popup menu and
        // participate in SetForegroundWindow, which is required for reliable
        // dismissal when the user clicks elsewhere.
        _hwnd = CreateWindowEx(0x00000080 /* WS_EX_TOOLWINDOW */, "STATIC",
            "WallerpaperTray", unchecked((int)0x80000000) /* WS_POPUP */,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandle(null), IntPtr.Zero);
        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException("Cannot create tray message window.");

        _oldWndProc = SetWindowLongPtr(_hwnd, -4, Marshal.GetFunctionPointerForDelegate(_wndProc));
        AddIcon();
    }

    public void SetPaused(bool paused)
    {
        _paused = paused;
        UpdateTip();
    }

    private void AddIcon()
    {
        var data = CreateData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
        data.uCallbackMessage = WM_TRAY;
        var exe = Environment.ProcessPath;
        data.hIcon = exe is not null &&
                     ExtractIconEx(exe, 0, out var largeIcon, out _, 1) > 0 &&
                     largeIcon != IntPtr.Zero
            ? largeIcon
            : LoadIcon(IntPtr.Zero, new IntPtr(IDI_APPLICATION));
        data.szTip = TipText();
        Shell_NotifyIcon(NIM_ADD, ref data);
    }

    private void UpdateTip()
    {
        if (_disposed) return;
        var data = CreateData();
        data.uFlags = NIF_TIP;
        data.szTip = TipText();
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    private string TipText()
    {
        var state = _isPaused() ? "已暂停" : "运行中";
        return $"Wallerpaper · {state}";
    }

    private NOTIFYICONDATA CreateData() => new()
    {
        cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
        hWnd = _hwnd,
        uID = 1,
        szTip = "",
        szInfo = "",
        szInfoTitle = ""
    };

    private IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAY:
                var mouse = lParam.ToInt32();
                if (mouse is WM_RBUTTONUP or WM_CONTEXTMENU)
                    ShowMenu();
                else if (mouse == WM_LBUTTONDBLCLK)
                    _show();
                return IntPtr.Zero;
            case WM_COMMAND:
                HandleCommand(wParam.ToInt32() & 0xFFFF);
                return IntPtr.Zero;
        }
        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void HandleCommand(int id)
    {
        if (id == ID_SHOW) { _show(); return; }
        if (id == ID_TOGGLE) { _toggle(); return; }
        if (id == ID_SETTINGS) { _settings(); return; }
        if (id == ID_EXIT) { _exit(); return; }
        if (id >= ID_WALLPAPER_BASE)
        {
            var list = _wallpapers();
            var index = id - ID_WALLPAPER_BASE;
            if (index >= 0 && index < list.Count)
                _selectWallpaper(list[index]);
        }
    }

    private void ShowMenu()
    {
        var menu = CreatePopupMenu();
        try
        {
            AppendMenu(menu, MF_STRING, ID_SHOW, "打开 Wallerpaper");
            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, ID_TOGGLE, _isPaused() ? "播放壁纸" : "暂停壁纸");

            var quick = CreatePopupMenu();
            var list = _wallpapers().Take(20).ToList();
            if (list.Count == 0)
            {
                AppendMenu(quick, MF_STRING | MF_GRAYED, 0, "（暂无壁纸）");
            }
            else
            {
                for (var i = 0; i < list.Count; i++)
                {
                    var label = $"{list[i].Title}  [{list[i].Kind}]";
                    AppendMenu(quick, MF_STRING, ID_WALLPAPER_BASE + i, label);
                }
            }
            AppendMenu(menu, 0x00000010 /* MF_POPUP */, quick.ToInt32(), "快速切换壁纸");

            AppendMenu(menu, MF_SEPARATOR, 0, null);
            AppendMenu(menu, MF_STRING, ID_SETTINGS, "设置");
            AppendMenu(menu, MF_STRING, ID_EXIT, "退出");

            GetCursorPos(out var pt);
            SetForegroundWindow(_hwnd);
            var cmd = TrackPopupMenuEx(menu,
                TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);
            if (cmd > 0)
                HandleCommand(cmd);
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var data = CreateData();
        Shell_NotifyIcon(NIM_DELETE, ref data);
        if (_hwnd != IntPtr.Zero)
        {
            SetWindowLongPtr(_hwnd, -4, _oldWndProc);
            DestroyWindow(_hwnd);
        }
    }

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(int message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateWindowEx(int exStyle, string className,
        string windowName, int style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr instance, IntPtr iconName);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint ExtractIconEx(string file, int index, out IntPtr large,
        out IntPtr small, uint count);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, int id, string? item);

    [DllImport("user32.dll")]
    private static extern bool RemoveMenu(IntPtr menu, uint item, uint flags);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenuEx(IntPtr menu, uint flags, int x, int y,
        IntPtr hwnd, IntPtr tpm);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
