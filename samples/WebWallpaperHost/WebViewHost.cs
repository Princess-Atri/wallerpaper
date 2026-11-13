using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;

namespace WebWallpaperHost;

/// <summary>
/// Raw CoreWebView2Controller host. It deliberately does NOT use the WinForms
/// WebView2 control: the controller is attached to the WorkerW child HWND and all
/// input is injected through the official WebMessage JS bridge.
/// </summary>
internal sealed class WebViewHost : IDisposable
{
    private readonly Options _options;
    private readonly Action<string> _log;
    private readonly string _userDataFolder;
    private readonly bool _ownsUserDataFolder;
    private readonly string _allowedPrefix;
    private readonly string _allowedHost;
    private readonly string _navigateUrl;

    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private CoreWebView2? _core;
    private IntPtr _hostHwnd;
    private System.Windows.Forms.Timer? _mouseTimer;
    private System.Windows.Forms.Timer? _idleTimer;
    private System.Windows.Forms.Timer? _styleTimer;
    private DateTime _lastActivityUtc = DateTime.UtcNow;
    private DateTime _lastMouseSentUtc = DateTime.MinValue;
    private int _lastMouseX = int.MinValue;
    private int _lastMouseY = int.MinValue;
    private bool _lastInside;
    private bool _idle;
    private bool _paused;
    private bool _disposed;
    private double _rasterizationScale = 1.0;
    private double _lastLevel;
    private int _audioBinCount;
    private bool _audioLogged;
    private bool _mouseLogged;
    private bool _deliveryLogged;
    private long _messageSeq;

    public WebViewHost(Options options, Action<string> log)
    {
        _options = options;
        _log = log;

        _ownsUserDataFolder = string.IsNullOrWhiteSpace(options.UserDataFolder);
        _userDataFolder = _ownsUserDataFolder
            ? Path.Combine(Path.GetTempPath(), "Wallerpaper", "WebView2", options.InstanceId)
            : Path.GetFullPath(options.UserDataFolder!);

        if (options.HtmlPath is not null)
        {
            var root = Path.GetDirectoryName(options.HtmlPath)!;
            _allowedPrefix = "https://wallerpaper.local/";
            _navigateUrl = _allowedPrefix + Uri.EscapeDataString(Path.GetFileName(options.HtmlPath));
            _allowedHost = string.Empty;
            MapFolder = root;
        }
        else
        {
            _allowedPrefix = string.Empty;
            _allowedHost = (options.AllowHost ?? string.Empty).Trim().ToLowerInvariant();
            _navigateUrl = options.Url!;
            MapFolder = null;
        }
    }

    public string? MapFolder { get; }
    public string BrowserVersion { get; private set; } = string.Empty;

    public event Action? Loaded;
    public event Action<string>? ProcessFailed;
    public event Action<string>? LoadFailed;

    public async Task InitializeAsync(IntPtr hostHwnd, Options options)
    {
        _hostHwnd = hostHwnd;
        Directory.CreateDirectory(_userDataFolder);

        BrowserVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
        _log($"WebView2 runtime: {BrowserVersion}");

        var browserArgs = new List<string>
        {
            "--disable-background-networking",
            "--disable-component-update",
            "--disable-sync",
            "--disable-breakpad",
            "--disable-crash-reporter",
            "--disable-default-apps",
            "--disable-extensions",
            "--disable-client-side-phishing-detection",
            "--disable-domain-reliability",
            "--metrics-recording-only",
            "--no-first-run",
            "--no-default-browser-check",
            "--autoplay-policy=no-user-gesture-required",
            "--disable-features=Translate,MediaRouter,OptimizationHints,AutofillServerCommunication,msWebOOUI,msPdfOOUI"
        };

        if (options.NoGpu)
            browserArgs.Add("--disable-gpu");

        var envOptions = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = string.Join(' ', browserArgs),
            Language = "zh-CN"
        };

        _environment = await CoreWebView2Environment.CreateAsync(
            browserExecutableFolder: null,
            userDataFolder: _userDataFolder,
            options: envOptions);

        _controller = await _environment.CreateCoreWebView2ControllerAsync(hostHwnd);
        _controller.DefaultBackgroundColor = Color.Black;
        _controller.ShouldDetectMonitorScaleChanges = false;
        _controller.AllowExternalDrop = false;
        _controller.RasterizationScale = GetScale(hostHwnd);
        _controller.ZoomFactor = Math.Clamp(options.Zoom, 0.5, 2.0);
        _controller.Bounds = GetClientBounds(hostHwnd);

        _core = _controller.CoreWebView2;
        _core.Settings.AreDefaultContextMenusEnabled = false;
        _core.Settings.AreDevToolsEnabled = false;
        _core.Settings.AreHostObjectsAllowed = false;
        _core.Settings.IsWebMessageEnabled = true;
        _core.Settings.IsStatusBarEnabled = false;
        _core.Settings.AreBrowserAcceleratorKeysEnabled = false;
        _core.Settings.IsZoomControlEnabled = false;
        _core.Settings.IsSwipeNavigationEnabled = false;
        _core.Settings.IsPinchZoomEnabled = false;
        _core.Settings.IsGeneralAutofillEnabled = false;
        _core.Settings.IsPasswordAutosaveEnabled = false;
        _core.Settings.AreDefaultScriptDialogsEnabled = false;
        _core.Settings.IsBuiltInErrorPageEnabled = false;

        _core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            _log($"blocked new window: {e.Uri}");
        };
        _core.NavigationStarting += (_, e) =>
        {
            if (!IsAllowedNavigation(e.Uri))
            {
                e.Cancel = true;
                _log($"blocked navigation: {e.Uri}");
            }
        };
        _core.PermissionRequested += (_, e) =>
        {
            e.State = CoreWebView2PermissionState.Deny;
            e.Handled = true;
            _log($"denied permission: {e.PermissionKind}");
        };
        _core.DownloadStarting += (_, e) =>
        {
            e.Cancel = true;
            e.Handled = true;
            _log($"blocked download: {e.DownloadOperation.Uri}");
        };
        _core.ProcessFailed += (_, e) =>
        {
            var msg = $"WebView2 process failed: {e.ProcessFailedKind}";
            _log(msg);
            ProcessFailed?.Invoke(msg);
        };
        _core.NavigationCompleted += (_, e) =>
        {
            if (e.IsSuccess)
            {
                _log($"navigation completed: {_core.Source}");
                Loaded?.Invoke();
                _ = ProbeBridgeAsync();
                _ = ProbeBridgeDelayedAsync();
            }
            else
            {
                var msg = $"navigation failed: {e.WebErrorStatus}";
                _log(msg);
                LoadFailed?.Invoke(msg);
            }
        };
        _core.WebMessageReceived += (_, e) =>
        {
            // Page -> host is intentionally diagnostic-only. It cannot execute host commands.
            _log($"page message: {Truncate(e.WebMessageAsJson, 300)}");
        };

        await _core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);

        if (MapFolder is not null)
        {
            _core.SetVirtualHostNameToFolderMapping(
                "wallerpaper.local",
                MapFolder,
                CoreWebView2HostResourceAccessKind.DenyCors);
        }

        _controller.IsVisible = true;
        _core.Navigate(_navigateUrl);

        _mouseTimer = new System.Windows.Forms.Timer { Interval = 16 };
        _mouseTimer.Tick += (_, _) => PollMouse();
        _mouseTimer.Start();

        _idleTimer = new System.Windows.Forms.Timer { Interval = 1000 };
        _idleTimer.Tick += (_, _) => UpdateIdle();
        _idleTimer.Start();

        _styleTimer = new System.Windows.Forms.Timer { Interval = 1500 };
        _styleTimer.Tick += (_, _) => ApplyClickThroughStyles();
        _styleTimer.Start();

        ApplyClickThroughStyles();
        SetIdle(false);
    }

    private void DeliverToPage(Dictionary<string, object?> payload)
    {
        if (_disposed || _core is null)
            return;

        payload["seq"] = Interlocked.Increment(ref _messageSeq);
        var json = JsonSerializer.Serialize(payload);

        if (!_deliveryLogged)
        {
            _deliveryLogged = true;
            _log($"first page delivery: type={payload.GetValueOrDefault("type")}, seq={payload["seq"]}");
        }

        try
        {
            // Official WebView2 JS bridge. The shim is installed before navigation;
            // a successful probe validates window.__wallerpaperDeliver exists.
            _core.PostWebMessageAsJson(json);
        }
        catch (Exception ex)
        {
            _log($"web message failed, trying script fallback: {ex.Message}");
            try
            {
                _ = _core.ExecuteScriptAsync(
                    $"window.__wallerpaperDeliver && window.__wallerpaperDeliver({json});");
            }
            catch (Exception fallbackEx)
            {
                _log($"script inject failed: {fallbackEx.Message}");
            }
        }
    }

    /// <summary>Test-only hook: drive the page bridge without OS cursor input.</summary>
    public void InjectMouseForTest(int x, int y)
    {
        if (_disposed || _core is null)
            return;
        DeliverToPage(new Dictionary<string, object?>
        {
            ["type"] = "wallerpaper.mouse",
            ["x"] = x,
            ["y"] = y,
            ["inside"] = true,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    public void InjectAudio(double[] bins, double level)
    {
        if (_disposed || _core is null)
            return;

        _lastLevel = Math.Clamp(level, 0, 1);
        _audioBinCount = bins.Length;
        if (!_audioLogged)
        {
            _audioLogged = true;
            _log($"audio first inject: bins={bins.Length}, level={_lastLevel:F3}");
        }

        if (_lastLevel > 0.02)
            MarkActivity();

        DeliverToPage(new Dictionary<string, object?>
        {
            ["type"] = "wallerpaper.audio",
            ["bins"] = bins,
            ["level"] = _lastLevel,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    public void SetMediaVolume(double volume)
    {
        if (_disposed || _core is null) return;
        var normalized = Math.Clamp(volume / 100.0, 0, 1);
        var script = FormattableString.Invariant(
            $"document.querySelectorAll('video,audio').forEach(e=>{{e.volume={normalized:0.###};}}); true;");
        _ = _core.ExecuteScriptAsync(script);
    }

    public void SetMediaSpeed(double speed)
    {
        if (_disposed || _core is null) return;
        var normalized = Math.Clamp(speed, 0.1, 4.0);
        var script = FormattableString.Invariant(
            $"document.querySelectorAll('video,audio').forEach(e=>{{e.playbackRate={normalized:0.###};}}); true;");
        _ = _core.ExecuteScriptAsync(script);
    }

    public async Task PauseAsync()
    {
        if (_disposed || _controller is null)
            return;

        _paused = true;
        try
        {
            await _core!.TrySuspendAsync();
            _controller.IsVisible = false;
            _log("WebView2 suspended");
        }
        catch (Exception ex)
        {
            _log($"suspend failed: {ex.Message}");
        }
    }

    public void Resume()
    {
        if (_disposed || _controller is null)
            return;

        _paused = false;
        _controller.IsVisible = true;
        MarkActivity();
        _log("WebView2 resumed");
    }

    private async Task ProbeBridgeAsync()
    {
        if (_core is null)
            return;

        try
        {
            var result = await _core.ExecuteScriptAsync(
                "JSON.stringify({" +
                "installed: !!window.__wallerpaperBridgeInstalled," +
                "chrome: typeof window.chrome," +
                "webview: typeof (window.chrome && window.chrome.webview)," +
                "listener: typeof (window.chrome && window.chrome.webview && window.chrome.webview.addEventListener)," +
                "msgCount: window.__wallerpaperMessageCount || 0," +
                "deliver: typeof window.__wallerpaperDeliver," +
                "api: typeof window.wallerpaper," +
                "audioLevel: window.wallerpaper ? window.wallerpaper.audio.level : null," +
                "audioBins: window.wallerpaper ? window.wallerpaper.audio.bins.length : null," +
                "mouse: window.wallerpaper ? window.wallerpaper.mouse : null" +
                "})");
            _log($"bridge probe: {result}");
        }
        catch (Exception ex)
        {
            _log($"bridge probe failed: {ex.Message}");
        }
    }

    private async Task ProbeBridgeDelayedAsync()
    {
        await Task.Delay(1500);
        await ProbeBridgeAsync();
    }

    public void Navigate(string url)
    {
        if (_disposed || _core is null)
            return;

        if (!IsAllowedNavigation(url))
        {
            _log($"blocked command navigation: {url}");
            return;
        }

        _core.Navigate(url);
    }

    public Dictionary<string, object?> GetStats()
    {
        using var p = Process.GetCurrentProcess();
        return new Dictionary<string, object?>
        {
            ["workingSetMB"] = Math.Round(p.WorkingSet64 / 1048576.0, 1),
            ["privateMB"] = Math.Round(p.PrivateMemorySize64 / 1048576.0, 1),
            ["cpuSeconds"] = Math.Round(p.TotalProcessorTime.TotalSeconds, 2),
            ["threads"] = p.Threads.Count,
            ["handles"] = p.HandleCount,
            ["idle"] = _idle,
            ["paused"] = _paused,
            ["audioBins"] = _audioBinCount,
            ["audioLevel"] = _lastLevel,
            ["url"] = _core?.Source ?? _navigateUrl,
            ["webViewVersion"] = BrowserVersion
        };
    }

    private void PollMouse()
    {
        if (_disposed || _core is null || _paused || _controller is null)
            return;

        if (!NativeMethods.GetCursorPos(out var screen))
            return;

        var client = screen;
        NativeMethods.ScreenToClient(_hostHwnd, ref client);

        var bounds = GetClientBounds(_hostHwnd);
        var inside = client.X >= 0 && client.Y >= 0 && client.X < bounds.Width && client.Y < bounds.Height;

        var x = _rasterizationScale > 0 ? (int)Math.Round(client.X / _rasterizationScale) : client.X;
        var y = _rasterizationScale > 0 ? (int)Math.Round(client.Y / _rasterizationScale) : client.Y;

        if (_lastInside == inside && _lastMouseX == x && _lastMouseY == y &&
            (DateTime.UtcNow - _lastMouseSentUtc).TotalMilliseconds < 250)
        {
            return;
        }

        var moved = inside && (_lastMouseX != x || _lastMouseY != y || _lastInside != inside);
        if (moved)
            MarkActivity();

        _lastInside = inside;
        _lastMouseX = x;
        _lastMouseY = y;
        _lastMouseSentUtc = DateTime.UtcNow;

        if (!_mouseLogged)
        {
            _mouseLogged = true;
            _log($"mouse first inject: x={x}, y={y}, inside={inside}, scale={_rasterizationScale:F2}");
        }

        DeliverToPage(new Dictionary<string, object?>
        {
            ["type"] = "wallerpaper.mouse",
            ["x"] = x,
            ["y"] = y,
            ["inside"] = inside,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        });
    }

    private void UpdateIdle()
    {
        if (_disposed || _core is null)
            return;

        // Refresh geometry if DPI/desktop changed.
        _rasterizationScale = GetScale(_hostHwnd);
        _controller!.RasterizationScale = _rasterizationScale;
        _controller.Bounds = GetClientBounds(_hostHwnd);

        var idleNow = !_paused && (DateTime.UtcNow - _lastActivityUtc).TotalSeconds >= _options.IdleSeconds;
        if (idleNow != _idle)
            SetIdle(idleNow);
    }

    private void MarkActivity()
    {
        _lastActivityUtc = DateTime.UtcNow;
        if (_idle)
            SetIdle(false);
    }

    private void SetIdle(bool idle)
    {
        _idle = idle;
        if (_mouseTimer is not null)
            _mouseTimer.Interval = idle ? 100 : 16;

        if (_core is null)
            return;

        DeliverToPage(new Dictionary<string, object?>
        {
            ["type"] = "wallerpaper.idle",
            ["idle"] = idle
        });
        _log(idle ? "idle: JS rAF throttled" : "active: JS rAF restored");
    }

    private void ApplyClickThroughStyles()
    {
        if (_disposed || _hostHwnd == IntPtr.Zero)
            return;

        NativeMethods.EnumChildWindows(_hostHwnd, (hwnd, _) =>
        {
            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE).ToInt64();
            ex |= NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_NOACTIVATE;
            NativeMethods.SetWindowLongPtr(hwnd, NativeMethods.GWL_EXSTYLE, new IntPtr(ex));
            return true;
        }, IntPtr.Zero);
    }

    private bool IsAllowedNavigation(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri))
            return false;

        if (MapFolder is not null)
            return uri.StartsWith(_allowedPrefix, StringComparison.OrdinalIgnoreCase);

        if (!Uri.TryCreate(uri, UriKind.Absolute, out var u))
            return false;

        return u.Scheme == Uri.UriSchemeHttps &&
               string.Equals(u.Host, _allowedHost, StringComparison.OrdinalIgnoreCase);
    }

    private static Rectangle GetClientBounds(IntPtr hwnd)
    {
        if (!NativeMethods.GetClientRect(hwnd, out var rc))
            return new Rectangle(0, 0, 1920, 1080);
        return new Rectangle(0, 0, Math.Max(1, rc.Right - rc.Left), Math.Max(1, rc.Bottom - rc.Top));
    }

    private static double GetScale(IntPtr hwnd)
    {
        var dpi = NativeMethods.GetDpiForWindow(hwnd);
        return dpi > 0 ? dpi / 96.0 : 1.0;
    }

    private static string Truncate(string s, int max) =>
        s.Length <= max ? s : s[..max] + "...";

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        try
        {
            _mouseTimer?.Stop();
            _mouseTimer?.Dispose();
            _mouseTimer = null;

            _idleTimer?.Stop();
            _idleTimer?.Dispose();
            _idleTimer = null;

            _styleTimer?.Stop();
            _styleTimer?.Dispose();
            _styleTimer = null;
        }
        catch
        {
            // ignore timer shutdown errors
        }

        try
        {
            _core?.Stop();
            _controller?.Close();
            (_controller as IDisposable)?.Dispose();
            _controller = null;
            _core = null;
        }
        catch (Exception ex)
        {
            _log($"WebView2 close failed: {ex.Message}");
        }

        // Give the browser process a short window to release the profile, then
        // remove only the profile this child owns. Never delete a user-supplied UDF.
        for (var attempt = 0; attempt < 8; attempt++)
        {
            if (!_ownsUserDataFolder)
                break;

            try
            {
                if (Directory.Exists(_userDataFolder))
                    Directory.Delete(_userDataFolder, recursive: true);
                break;
            }
            catch (IOException)
            {
                Thread.Sleep(150);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(150);
            }
        }

        _log("WebView2 host disposed");
    }

    private static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const long WS_EX_TRANSPARENT = 0x00000020L;
        public const long WS_EX_NOACTIVATE = 0x08000000L;

        [DllImport("user32.dll")]
        public static extern bool GetCursorPos(out POINT pt);

        [DllImport("user32.dll")]
        public static extern bool ScreenToClient(IntPtr hWnd, ref POINT pt);

        [DllImport("user32.dll")]
        public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern uint GetDpiForWindow(IntPtr hWnd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }
    }

    private const string BridgeScript = """
(function () {
  if (window.__wallerpaperBridgeInstalled) return;
  window.__wallerpaperBridgeInstalled = true;

  const mouseEventName = 'wallerpaper:mousemove';
  const audioEventName = 'wallerpaper:audio';
  const idleEventName = 'wallerpaper:idle';

  const api = {
    version: 1,
    mouse: { x: 0, y: 0, inside: false },
    audio: { bins: [], level: 0 },
    idle: false,
    getAudioBins: function () { return api.audio.bins.slice(); },
    onMouse: null,
    onAudio: null,
    onIdle: null
  };
  window.wallerpaper = api;

  function emit(name, detail) {
    try {
      window.dispatchEvent(new CustomEvent(name, { detail }));
      document.dispatchEvent(new CustomEvent(name, { detail }));
    } catch (e) { }
  }

  function installStyle() {
    const style = document.createElement('style');
    style.id = '__wallerpaper_style';
    style.textContent = `
      html, body {
        margin: 0 !important;
        padding: 0 !important;
        width: 100% !important;
        height: 100% !important;
        overflow: hidden !important;
        background: #000;
        cursor: none !important;
        user-select: none !important;
        -webkit-user-select: none !important;
        -ms-user-select: none !important;
      }
      * {
        -webkit-user-drag: none !important;
        box-sizing: border-box;
      }
      ::-webkit-scrollbar { display: none !important; width: 0 !important; height: 0 !important; }
    `;
    (document.head || document.documentElement).appendChild(style);
  }
  try {
    installStyle();
  } catch (e) {
    // AddScriptToExecuteOnDocumentCreated can run before documentElement
    // exists; install the style as soon as the DOM is ready.
    document.addEventListener('DOMContentLoaded', installStyle, { once: true });
  }

  document.addEventListener('contextmenu', e => e.preventDefault(), true);
  document.addEventListener('dragstart', e => e.preventDefault(), true);
  document.addEventListener('selectstart', e => e.preventDefault(), true);

  // Idle governor: pages using requestAnimationFrame are throttled to ~15fps.
  const nativeRAF = window.requestAnimationFrame.bind(window);
  let rafIdle = false;
  window.requestAnimationFrame = function (callback) {
    if (!rafIdle) return nativeRAF(callback);
    return window.setTimeout(function () {
      nativeRAF(callback);
    }, 66);
  };

  function dispatchMouse(x, y, inside) {
    api.mouse.x = x;
    api.mouse.y = y;
    api.mouse.inside = inside;
    if (typeof api.onMouse === 'function') api.onMouse(api.mouse);

    try {
      const target = document.elementFromPoint(x, y) || document.documentElement;
      const ev = new MouseEvent('mousemove', {
        bubbles: true,
        cancelable: true,
        view: window,
        clientX: x,
        clientY: y
      });
      target.dispatchEvent(ev);
      window.dispatchEvent(ev);
    } catch (e) { }

    emit(mouseEventName, api.mouse);
  }

  let lastSeq = 0;
  function handleHostMessage(data) {
    window.__wallerpaperMessageCount = (window.__wallerpaperMessageCount || 0) + 1;
    if (!data || !data.type) return;
    if (typeof data.seq === 'number') {
      if (data.seq <= lastSeq) return;
      lastSeq = data.seq;
    }

    if (data.type === 'wallerpaper.mouse') {
      dispatchMouse(data.x || 0, data.y || 0, !!data.inside);
      return;
    }

    if (data.type === 'wallerpaper.audio') {
      api.audio.bins = Array.isArray(data.bins) ? data.bins.slice() : [];
      api.audio.level = data.level || 0;
      if (typeof api.onAudio === 'function') api.onAudio(api.audio);
      emit(audioEventName, api.audio);
      return;
    }

    if (data.type === 'wallerpaper.idle') {
      api.idle = !!data.idle;
      rafIdle = api.idle;
      if (typeof api.onIdle === 'function') api.onIdle(api.idle);
      emit(idleEventName, { idle: api.idle });
    }
  }

  // ExecuteScriptAsync fallback (used when PostWebMessageAsJson is unavailable).
  window.__wallerpaperDeliver = handleHostMessage;

  window.chrome.webview.addEventListener('message', function (event) {
    let data = event.data;
    if (typeof data === 'string') {
      try { data = JSON.parse(data); } catch (e) { return; }
    }
    handleHostMessage(data);
  });

  try {
    window.chrome.webview.postMessage({ type: 'wallerpaper.bridge-ready' });
  } catch (e) { }
})();
""";
}
