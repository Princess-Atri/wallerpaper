using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace VideoWallpaperParent;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = new UTF8Encoding(false);
        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            Options.PrintUsage();
            return 0;
        }

        var controller = new ParentController(options);
        return await controller.RunAsync();
    }
}

internal sealed class Options
{
    public string ChildPath { get; private set; } = "";
    public string VideoPath { get; private set; } = "";
    public int Fps { get; private set; } = 60;
    public int Volume { get; private set; }
    public int AutoStopSeconds { get; private set; }
    public int DurationSeconds { get; private set; }
    public int SampleSeconds { get; private set; } = 15;
    public string Fit { get; private set; } = "cover";
    public string Hwdec { get; private set; } = "";
    public bool AllowSoftware { get; private set; }
    public bool AutoTest { get; private set; }
    public bool ShowHelp { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--child":
                    if (i + 1 < args.Length) o.ChildPath = args[++i];
                    break;
                case "--video":
                case "-v":
                    if (i + 1 < args.Length) o.VideoPath = args[++i];
                    break;
                case "--fps":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var fps)) o.Fps = fps;
                    break;
                case "--volume":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var vol)) o.Volume = vol;
                    break;
                case "--seconds":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var sec)) o.AutoStopSeconds = sec;
                    break;
                case "--duration":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var duration)) o.DurationSeconds = duration;
                    break;
                case "--sample":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var sample)) o.SampleSeconds = sample;
                    break;
                case "--fit":
                    if (i + 1 < args.Length) o.Fit = args[++i];
                    break;
                case "--hwdec":
                    if (i + 1 < args.Length) o.Hwdec = args[++i];
                    break;
                case "--allow-software":
                    o.AllowSoftware = true;
                    break;
                case "--auto-test":
                    o.AutoTest = true;
                    break;
                case "--help":
                case "-h":
                    o.ShowHelp = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(o.ChildPath))
            o.ChildPath = Path.Combine(AppContext.BaseDirectory, "VideoWallpaperHost.exe");

        if (o.Fps is not (30 or 60)) o.Fps = 60;
        if (o.Fit is not ("cover" or "contain")) o.Fit = "cover";
        o.Volume = Math.Clamp(o.Volume, 0, 100);
        o.SampleSeconds = Math.Max(1, o.SampleSeconds);
        o.DurationSeconds = Math.Max(0, o.DurationSeconds);
        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("VideoWallpaperParent — launches/controls VideoWallpaperHost over stdio");
        Console.WriteLine();
        Console.WriteLine("  VideoWallpaperParent.exe --video <file> [--child <host.exe|host.dll>]");
        Console.WriteLine("      [--fps 30|60] [--volume 0-100] [--fit cover|contain]");
        Console.WriteLine("      [--hwdec d3d11va|dxva2] [--allow-software] [--seconds N] [--auto-test]");
        Console.WriteLine("      [--duration 600] [--sample 15]   # long-run memory sampling");
        Console.WriteLine();
        Console.WriteLine("  Interactive: p=play, s=pause(release decoder), r=resume,");
        Console.WriteLine("               +/- volume, 1=1.0x 2=2.0x 0=0.5x, f=toggle 30/60, i=stats, q=stop");
    }
}

internal sealed class ParentController
{
    private readonly Options _options;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _inputLock = new();

    private Process? _child;
    private int _volume;
    private int _fps;
    private int? _exitCode;

    public ParentController(Options options)
    {
        _options = options;
        _volume = options.Volume;
        _fps = options.Fps;
    }

    public async Task<int> RunAsync()
    {
        if (!File.Exists(_options.ChildPath))
        {
            Console.Error.WriteLine($"child not found: {_options.ChildPath}");
            return 2;
        }

        if (!File.Exists(_options.VideoPath))
        {
            Console.Error.WriteLine($"video not found: {_options.VideoPath}");
            return 2;
        }

        try
        {
            StartChild();

            var ready = await Task.WhenAny(_ready.Task, Task.Delay(TimeSpan.FromSeconds(20)));
            if (ready != _ready.Task)
            {
                Console.Error.WriteLine("child did not become ready within 20s.");
                await StopAsync();
                return 4;
            }

            if (!_ready.Task.Result)
            {
                Console.Error.WriteLine(
                    $"child exited/failed before ready (exitCode={_exitCode?.ToString() ?? "?"}); " +
                    "see the child error above. Interactive commands are only available after READY.");
                await StopAsync();
                return _exitCode ?? 4;
            }

            if (_options.DurationSeconds > 0)
            {
                Send(new { cmd = "play" });
                await Task.Delay(500);

                var deadline = DateTime.UtcNow.AddSeconds(_options.DurationSeconds);
                var sampleIndex = 0;

                while (DateTime.UtcNow < deadline)
                {
                    if (_exitCode.HasValue)
                        break;

                    Send(new { cmd = "status" });
                    sampleIndex++;

                    var remaining = deadline - DateTime.UtcNow;
                    if (remaining <= TimeSpan.Zero)
                        break;

                    var delay = TimeSpan.FromSeconds(
                        Math.Min(_options.SampleSeconds, Math.Max(1, remaining.TotalSeconds)));
                    await Task.Delay(delay);
                }

                Console.WriteLine($"[parent] stability run finished, samples={sampleIndex}");
                await StopAsync();
                return _exitCode ?? 0;
            }

            if (_options.AutoTest)
            {
                await Task.Delay(800);
                Send(new { cmd = "speed", value = 1.25 });
                Send(new { cmd = "volume", value = 0 });
                Send(new { cmd = "fps", value = 60 });
                await Task.Delay(500);
                Send(new { cmd = "status" });
                await Task.Delay(800);
                Send(new { cmd = "pause", release = true });
                await Task.Delay(1200);
                Send(new { cmd = "status" });
                await Task.Delay(800);
                Send(new { cmd = "play" });
                await Task.Delay(1200);
                Send(new { cmd = "status" });
                await Task.Delay(500);
                await StopAsync();
                return _exitCode ?? 0;
            }

            if (_options.AutoStopSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.AutoStopSeconds));
                await StopAsync();
                return _exitCode ?? 0;
            }

            PrintInteractiveHelp();
            while (true)
            {
                var line = Console.ReadLine();
                if (line is null)
                    break;

                switch (line.Trim().ToLowerInvariant())
                {
                    case "p":
                        Send(new { cmd = "play" });
                        break;
                    case "s":
                        Send(new { cmd = "pause", release = true });
                        break;
                    case "r":
                        Send(new { cmd = "play" });
                        break;
                    case "+":
                        _volume = Math.Min(100, _volume + 5);
                        Send(new { cmd = "volume", value = _volume });
                        break;
                    case "-":
                        _volume = Math.Max(0, _volume - 5);
                        Send(new { cmd = "volume", value = _volume });
                        break;
                    case "1":
                        Send(new { cmd = "speed", value = 1.0 });
                        break;
                    case "2":
                        Send(new { cmd = "speed", value = 2.0 });
                        break;
                    case "0":
                        Send(new { cmd = "speed", value = 0.5 });
                        break;
                    case "f":
                        _fps = _fps == 60 ? 30 : 60;
                        Send(new { cmd = "fps", value = _fps });
                        break;
                    case "i":
                        Send(new { cmd = "status" });
                        break;
                    case "q":
                        await StopAsync();
                        return _exitCode ?? 0;
                }
            }

            await StopAsync();
            return _exitCode ?? 0;
        }
        finally
        {
            KillChildTree();
        }
    }

    private void StartChild()
    {
        var childDir = Path.GetDirectoryName(Path.GetFullPath(_options.ChildPath)) ?? AppContext.BaseDirectory;
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            StandardInputEncoding = new UTF8Encoding(false),
            CreateNoWindow = true,
            WorkingDirectory = childDir
        };

        if (_options.ChildPath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
        {
            psi.FileName = "dotnet";
            psi.ArgumentList.Add(Path.GetFullPath(_options.ChildPath));
        }
        else
        {
            psi.FileName = Path.GetFullPath(_options.ChildPath);
        }

        psi.ArgumentList.Add("--video");
        psi.ArgumentList.Add(Path.GetFullPath(_options.VideoPath));
        psi.ArgumentList.Add("--fps");
        psi.ArgumentList.Add(_fps.ToString());
        psi.ArgumentList.Add("--volume");
        psi.ArgumentList.Add(_volume.ToString());
        psi.ArgumentList.Add("--fit");
        psi.ArgumentList.Add(_options.Fit);
        if (!string.IsNullOrWhiteSpace(_options.Hwdec))
        {
            psi.ArgumentList.Add("--hwdec");
            psi.ArgumentList.Add(_options.Hwdec);
        }

        if (_options.AllowSoftware)
            psi.ArgumentList.Add("--allow-software");

        _child = new Process
        {
            StartInfo = psi,
            EnableRaisingEvents = true
        };

        _child.OutputDataReceived += OnChildOutput;
        _child.ErrorDataReceived += OnChildError;
        _child.Exited += OnChildExited;

        Console.WriteLine($"starting child: {psi.FileName} {string.Join(' ', psi.ArgumentList)}");
        _child.Start();
        _child.BeginOutputReadLine();
        _child.BeginErrorReadLine();
    }

    private void OnChildOutput(object? sender, DataReceivedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(e.Data))
            return;

        try
        {
            using var doc = JsonDocument.Parse(e.Data);
            var root = doc.RootElement;
            var ev = root.TryGetProperty("ev", out var evProp) ? evProp.GetString() : "?";

            switch (ev)
            {
                case "ready":
                    Console.WriteLine($"[child] READY hwdec={GetString(root, "hwdec")} " +
                                      $"size={GetInt(root, "width")}x{GetInt(root, "height")} " +
                                      $"duration={GetDouble(root, "duration"):F2}s " +
                                      $"fpsCap={GetBool(root, "fpsCap")}");
                    _ready.TrySetResult(true);
                    break;

                case "error":
                    Console.Error.WriteLine($"[child] ERROR {GetString(root, "code")}: {GetString(root, "message")}");
                    break;

                case "stopped":
                    Console.WriteLine($"[child] STOPPED exit={GetInt(root, "exitCode")}");
                    break;

                case "stats":
                    Console.WriteLine($"[child] stats ws={GetDouble(root, "workingSetMB"):F1}MB " +
                                      $"private={GetDouble(root, "privateMB"):F1}MB " +
                                      $"cpu={GetDouble(root, "cpuSeconds"):F2}s " +
                                      $"threads={GetInt(root, "threads")} handles={GetInt(root, "handles")} " +
                                      $"cache={GetDouble(root, "cache"):F2}s hwdec={GetString(root, "hwdec")} " +
                                      $"fps={GetDouble(root, "fps"):F1} time={GetDouble(root, "time"):F2} " +
                                      $"paused={GetBool(root, "paused")} released={GetBool(root, "released")}");
                    break;

                case "paused":
                    Console.WriteLine($"[child] paused released={GetBool(root, "released")}");
                    break;

                case "playing":
                    Console.WriteLine("[child] playing");
                    break;

                case "volume":
                    Console.WriteLine($"[child] volume={GetInt(root, "value")}");
                    break;

                case "speed":
                    Console.WriteLine($"[child] speed={GetDouble(root, "value"):F2}");
                    break;

                case "fps":
                    Console.WriteLine($"[child] fps cap={GetInt(root, "value")}");
                    break;

                default:
                    Console.WriteLine($"[child] {e.Data}");
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[child/raw] {e.Data} ({ex.Message})");
        }
    }

    private void OnChildError(object? sender, DataReceivedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(e.Data))
            Console.Error.WriteLine($"[child] {e.Data}");
    }

    private void OnChildExited(object? sender, EventArgs e)
    {
        try
        {
            _exitCode = _child?.ExitCode;
            Console.WriteLine($"[parent] child exited with code {_exitCode}");
            _ready.TrySetResult(false);
        }
        catch
        {
            _ready.TrySetResult(false);
        }
    }

    private void Send(object payload)
    {
        var child = _child;
        if (child is null || child.HasExited)
            return;

        var json = JsonSerializer.Serialize(payload);
        lock (_inputLock)
        {
            try
            {
                child.StandardInput.WriteLine(json);
                child.StandardInput.Flush();
                Console.WriteLine($"[parent] -> {json}");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[parent] send failed: {ex.Message}");
            }
        }
    }

    private async Task StopAsync()
    {
        var child = _child;
        if (child is null || child.HasExited)
            return;

        Send(new { cmd = "stop" });

        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(5));
            await child.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[parent] child did not exit in 5s; killing tree.");
            KillChildTree();
        }
    }

    private void KillChildTree()
    {
        try
        {
            if (_child is { HasExited: false })
                _child.Kill(entireProcessTree: true);
        }
        catch
        {
            // ignore
        }
    }

    private static void PrintInteractiveHelp()
    {
        Console.WriteLine();
        Console.WriteLine("Interactive commands:");
        Console.WriteLine("  p=play  s=pause(release decoder)  r=resume  q=stop");
        Console.WriteLine("  +/- volume   1=1.0x 2=2.0x 0=0.5x   f=30/60   i=stats");
        Console.WriteLine();
    }

    private static string? GetString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;

    private static int GetInt(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;

    private static double GetDouble(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetDouble() : 0;

    private static bool GetBool(JsonElement root, string name) =>
        root.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;
}
