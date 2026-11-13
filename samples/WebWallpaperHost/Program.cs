using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Wallerpaper.Diagnostics;
using Wallerpaper.RenderHost.WorkerW;

namespace WebWallpaperHost;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppLog.Initialize("WebWallpaperHost");
        CrashReporter.Install("WebWallpaperHost");
        AppLog.Info("WebWallpaperHost starting");
        Console.OutputEncoding = new UTF8Encoding(false);

        try
        {
            Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        }
        catch
        {
            // The process may already have a DPI awareness context.
        }

        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            Options.PrintUsage();
            return 0;
        }

        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            return 2;
        }

        var app = new WebHostApp(options);
        return app.Run();
    }
}

internal sealed class Options
{
    public string? HtmlPath { get; private set; }
    public string? Url { get; private set; }
    public string? AllowHost { get; private set; }
    public string? UserDataFolder { get; private set; }
    public string InstanceId { get; private set; } = Guid.NewGuid().ToString("N");
    public int IdleSeconds { get; private set; } = 15;
    public int AutoExitSeconds { get; private set; }
    public double Zoom { get; private set; } = 1.0;
    public bool NoGpu { get; private set; }
    public (int X, int Y, int Width, int Height)? Bounds { get; private set; }
    public bool ShowHelp { get; private set; }
    public string? Error { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--html":
                    if (i + 1 < args.Length) o.HtmlPath = args[++i];
                    break;
                case "--url":
                    if (i + 1 < args.Length) o.Url = args[++i];
                    break;
                case "--allow-host":
                    if (i + 1 < args.Length) o.AllowHost = args[++i];
                    break;
                case "--user-data-folder":
                    if (i + 1 < args.Length) o.UserDataFolder = args[++i];
                    break;
                case "--id":
                    if (i + 1 < args.Length) o.InstanceId = args[++i];
                    break;
                case "--idle-seconds":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var idle))
                        o.IdleSeconds = Math.Max(1, idle);
                    break;
                case "--seconds":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var sec))
                        o.AutoExitSeconds = Math.Max(0, sec);
                    break;
                case "--zoom":
                    if (i + 1 < args.Length &&
                        double.TryParse(args[++i], System.Globalization.NumberStyles.Float,
                            System.Globalization.CultureInfo.InvariantCulture, out var zoom))
                        o.Zoom = Math.Clamp(zoom, 0.5, 2.0);
                    break;
                case "--no-gpu":
                    o.NoGpu = true;
                    break;
                case "--bounds":
                    if (i + 4 < args.Length &&
                        int.TryParse(args[++i], out var bx) &&
                        int.TryParse(args[++i], out var by) &&
                        int.TryParse(args[++i], out var bw) &&
                        int.TryParse(args[++i], out var bh))
                        o.Bounds = (bx, by, bw, bh);
                    break;
                case "--help":
                case "-h":
                    o.ShowHelp = true;
                    break;
            }
        }

        if (o.ShowHelp)
            return o;

        if (string.IsNullOrWhiteSpace(o.HtmlPath) && string.IsNullOrWhiteSpace(o.Url))
        {
            var demo = Path.Combine(AppContext.BaseDirectory, "wwwroot", "index.html");
            if (File.Exists(demo))
                o.HtmlPath = demo;
        }

        if (!string.IsNullOrWhiteSpace(o.HtmlPath))
        {
            var full = Path.GetFullPath(o.HtmlPath);
            if (!File.Exists(full))
            {
                o.Error = $"HTML not found: {full}";
                return o;
            }

            var ext = Path.GetExtension(full);
            if (!ext.Equals(".html", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".htm", StringComparison.OrdinalIgnoreCase))
            {
                o.Error = "Local Web wallpaper must be .html/.htm.";
                return o;
            }

            o.HtmlPath = full;
            o.Url = null;
        }
        else
        {
            if (!Uri.TryCreate(o.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            {
                o.Error = "--url must be an absolute https:// URL.";
                return o;
            }

            o.AllowHost ??= uri.Host;
            o.Url = uri.ToString();
        }

        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("WebWallpaperHost (WebView2 + WorkerW)");
        Console.WriteLine();
        Console.WriteLine("  WebWallpaperHost.exe --html <index.html> [--idle-seconds 15] [--no-gpu]");
        Console.WriteLine("  WebWallpaperHost.exe --url <https://...> [--allow-host example.com]");
        Console.WriteLine("  [--bounds x y w h] for per-monitor WallpaperHost");
        Console.WriteLine();
        Console.WriteLine("  stdin commands: pause, resume, stop, navigate, audio, status");
    }
}

internal sealed class WebHostApp
{
    private readonly Options _options;
    private readonly object _stdoutLock = new();
    private readonly ManualResetEventSlim _parentStdinClosed = new(false);

    private WorkerWWindow? _worker;
    private WebViewHost? _web;
    private System.Windows.Forms.Timer? _autoExitTimer;
    private ApplicationContext? _appContext;
    private SynchronizationContext? _uiContext;
    private int _exitCode;
    private bool _shuttingDown;

    public WebHostApp(Options options)
    {
        _options = options;
    }

    public int Run()
    {
        try
        {
            _worker = new WorkerWWindow(Log);
            if (_options.Bounds is { } bounds)
                _worker.SetTargetRect(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            _worker.WindowLost += () =>
            {
                _exitCode = 3;
                Log("WorkerW window lost; exiting so parent can restart child.");
                RequestStop();
            };

            if (!_worker.CreateAndAttach())
            {
                SendError("E_WORKERW",
                    "Failed to find/attach WorkerW. Check explorer.exe and run at Medium integrity.");
                _exitCode = 3;
                return _exitCode;
            }

            _appContext = new ApplicationContext();
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
            _uiContext = SynchronizationContext.Current!;

            StartIpcReader();
            _ = InitializeAsync();

            Application.Run(_appContext);
            return _exitCode;
        }
        catch (Exception ex)
        {
            Log($"fatal: {ex}");
            CrashReporter.WriteDump(ex, "web-run");
            SendError("E_INTERNAL", ex.Message);
            _exitCode = 1;
            return _exitCode;
        }
        finally
        {
            Cleanup();
        }
    }

    private async Task InitializeAsync()
    {
        try
        {
            Log($"web init: html={_options.HtmlPath ?? "<none>"}, url={_options.Url ?? "<none>"}, " +
                $"gpu={(!_options.NoGpu ? "on" : "off")}, idle={_options.IdleSeconds}s");

            _web = new WebViewHost(_options, Log);
            _web.Loaded += () => SendEvent("loaded", new Dictionary<string, object?>
            {
                ["url"] = _options.Url ?? "wallerpaper.local"
            });
            _web.ProcessFailed += (message) =>
            {
                SendError("E_WEBVIEW_CRASH", message);
                _exitCode = 5;
                RequestStop();
            };

            await _web.InitializeAsync(_worker!.WindowHandle, _options);

            SendEvent("ready", new Dictionary<string, object?>
            {
                ["pid"] = Environment.ProcessId,
                ["hwnd"] = _worker.WindowHandle.ToInt64(),
                ["mode"] = _options.HtmlPath is null ? "url" : "local",
                ["gpu"] = !_options.NoGpu,
                ["webViewVersion"] = _web.BrowserVersion
            });

            Log("web ready");

            if (_options.AutoExitSeconds > 0)
            {
                _autoExitTimer = new System.Windows.Forms.Timer
                {
                    Interval = _options.AutoExitSeconds * 1000
                };
                _autoExitTimer.Tick += (_, _) =>
                {
                    _autoExitTimer?.Stop();
                    _exitCode = 0;
                    RequestStop();
                };
                _autoExitTimer.Start();
            }
        }
        catch (Exception ex)
        {
            Log($"web init failed: {ex}");
            SendError("E_WEBVIEW_INIT", ex.Message);
            _exitCode = 4;
            RequestStop();
        }
    }

    private void StartIpcReader()
    {
        _ = Task.Run(() =>
        {
            try
            {
                using var reader = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
                while (true)
                {
                    var line = reader.ReadLine();
                    if (line is null)
                    {
                        Log("stdin closed by parent; stopping.");
                        _parentStdinClosed.Set();
                        RequestStop();
                        return;
                    }

                    if (string.IsNullOrWhiteSpace(line))
                        continue;

                    HandleCommand(line);
                }
            }
            catch (Exception ex)
            {
                Log($"stdin reader failed: {ex.Message}");
                SendError("E_IPC", ex.Message);
                RequestStop();
            }
        });
    }

    private void HandleCommand(string line)
    {
        try
        {
            using var doc = JsonDocument.Parse(line);
            var root = doc.RootElement;
            if (!root.TryGetProperty("cmd", out var cmdProp))
            {
                SendError("E_CMD", "missing cmd");
                return;
            }

            var cmd = cmdProp.GetString()?.ToLowerInvariant();
            switch (cmd)
            {
                case "stop":
                    _exitCode = 0;
                    RequestStop();
                    break;

                case "pause":
                    PostToUi(() => _ = _web!.PauseAsync());
                    SendEvent("paused", null);
                    break;

                case "resume":
                    PostToUi(() => _web!.Resume());
                    SendEvent("resumed", null);
                    break;

                case "volume":
                    if (root.TryGetProperty("value", out var volumeProp) &&
                        volumeProp.TryGetDouble(out var volume))
                        PostToUi(() => _web?.SetMediaVolume(volume));
                    break;

                case "speed":
                    if (root.TryGetProperty("value", out var speedProp) &&
                        speedProp.TryGetDouble(out var speed))
                        PostToUi(() => _web?.SetMediaSpeed(speed));
                    break;

                case "audio":
                    {
                        var bins = new List<double>();
                        if (root.TryGetProperty("bins", out var binsProp) && binsProp.ValueKind == JsonValueKind.Array)
                        {
                            foreach (var b in binsProp.EnumerateArray())
                                bins.Add(Math.Clamp(b.GetDouble(), 0, 1));
                        }

                        var level = root.TryGetProperty("level", out var levelProp) && levelProp.ValueKind == JsonValueKind.Number
                            ? Math.Clamp(levelProp.GetDouble(), 0, 1) : 0;
                        var copy = bins.ToArray();
                        PostToUi(() => _web?.InjectAudio(copy, level));
                        break;
                    }

                case "testmouse":
                    if (root.TryGetProperty("x", out var tx) &&
                        root.TryGetProperty("y", out var ty))
                    {
                        var x = tx.GetInt32();
                        var y = ty.GetInt32();
                        PostToUi(() => _web?.InjectMouseForTest(x, y));
                    }
                    break;

                case "navigate":
                    if (root.TryGetProperty("url", out var urlProp) &&
                        urlProp.GetString() is { } url)
                    {
                        PostToUi(() => _web?.Navigate(url));
                    }
                    break;

                case "status":
                    SendEvent("stats", _web?.GetStats() ?? new Dictionary<string, object?>
                    {
                        ["error"] = "not-ready"
                    });
                    break;

                default:
                    SendError("E_CMD", $"unknown cmd '{cmd}'");
                    break;
            }
        }
        catch (JsonException ex)
        {
            SendError("E_CMD", $"invalid json: {ex.Message}");
        }
        catch (Exception ex)
        {
            SendError("E_CMD", ex.Message);
        }
    }

    private void PostToUi(Action action)
    {
        var ctx = _uiContext;
        if (ctx is null)
            action();
        else
            ctx.Post(_ => action(), null);
    }

    private void RequestStop()
    {
        PostToUi(() =>
        {
            if (_shuttingDown)
                return;
            _shuttingDown = true;

            _autoExitTimer?.Stop();
            _autoExitTimer?.Dispose();
            _autoExitTimer = null;

            try
            {
                _web?.Dispose();
                _web = null;
            }
            catch (Exception ex)
            {
                Log($"web dispose: {ex.Message}");
            }

            _appContext?.ExitThread();
        });
    }

    private void Cleanup()
    {
        try
        {
            _web?.Dispose();
            _web = null;
        }
        catch (Exception ex)
        {
            Log($"cleanup web: {ex.Message}");
        }

        try
        {
            _worker?.Dispose();
            _worker = null;
        }
        catch (Exception ex)
        {
            Log($"cleanup window: {ex.Message}");
        }

        SendEvent("stopped", new Dictionary<string, object?>
        {
            ["exitCode"] = _exitCode,
            ["parentStdinClosed"] = _parentStdinClosed.IsSet
        });
    }

    private void SendError(string code, string message)
    {
        Log($"ERROR {code}: {message}");
        SendEvent("error", new Dictionary<string, object?>
        {
            ["code"] = code,
            ["message"] = message
        });
    }

    private void SendEvent(string ev, Dictionary<string, object?>? data)
    {
        var payload = new Dictionary<string, object?>(data ?? new Dictionary<string, object?>())
        {
            ["ev"] = ev,
            ["ts"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
        };

        try
        {
            lock (_stdoutLock)
            {
                Console.Out.WriteLine(JsonSerializer.Serialize(payload));
                Console.Out.Flush();
            }
        }
        catch (IOException)
        {
            // parent pipe closed
        }
    }

    private static void Log(string message)
    {
        AppLog.Info(message);
        Console.Error.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {message}");
    }
}
