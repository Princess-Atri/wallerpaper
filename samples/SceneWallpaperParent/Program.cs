using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using WebWallpaperParent;

namespace SceneWallpaperParent;

internal static class Program
{
    private static int Main(string[] args)
    {
        var options = Options.Parse(args);
        if (options.ShowHelp)
        {
            Options.PrintHelp();
            return 0;
        }

        using var controller = new SceneController(options);
        return controller.Run();
    }
}

internal sealed class SceneController : IDisposable
{
    private readonly Options _options;
    private readonly string _childPath;
    private readonly string _scenePath;
    private Process? _child;
    private AudioSpectrum? _audio;
    private Timer? _audioTimer;
    private bool _audioLogged;
    private int? _exitCode;

    public SceneController(Options options)
    {
        _options = options;
        _childPath = Path.GetFullPath(options.ChildPath);
        _scenePath = Path.GetFullPath(options.ScenePath);
        if (!File.Exists(_childPath))
            throw new FileNotFoundException("child exe not found", _childPath);
        if (!File.Exists(_scenePath))
            throw new FileNotFoundException("scene json not found", _scenePath);
    }

    public int Run()
    {
        try
        {
            StartChild();
            var exit = WaitForExit();
            return _exitCode ?? exit;
        }
        finally
        {
            StopAudio();
            KillChild();
        }
    }

    private void StartChild()
    {
        var psi = new ProcessStartInfo
        {
            FileName = _childPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(_childPath)!
        };
        psi.ArgumentList.Add("--scene");
        psi.ArgumentList.Add(_scenePath);
        psi.ArgumentList.Add("--fps");
        psi.ArgumentList.Add(_options.Fps.ToString(CultureInfo.InvariantCulture));
        if (_options.Seconds > 0)
        {
            psi.ArgumentList.Add("--seconds");
            psi.ArgumentList.Add(_options.Seconds.ToString(CultureInfo.InvariantCulture));
        }
        if (_options.Vsync)
            psi.ArgumentList.Add("--vsync");
        if (_options.HalfRes)
            psi.ArgumentList.Add("--half-res");
        if (_options.Supersample > 1)
        {
            psi.ArgumentList.Add("--supersample");
            psi.ArgumentList.Add(_options.Supersample.ToString(CultureInfo.InvariantCulture));
        }
        if (_options.Stats)
            psi.ArgumentList.Add("--stats");

        _child = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _child.OutputDataReceived += (_, e) => OnEvent(e.Data);
        _child.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                Console.WriteLine("[child] " + e.Data);
        };
        _child.Exited += (_, _) =>
        {
            _exitCode = _child?.ExitCode;
            Console.WriteLine($"[parent] child exited with code {_exitCode}");
        };

        if (!_child.Start())
            throw new InvalidOperationException("failed to start child");

        _child.BeginOutputReadLine();
        _child.BeginErrorReadLine();
        Console.WriteLine($"starting child: {_childPath} --scene {_scenePath}");

        // Give the child time to create WorkerW/D3D11 and report ready.
        var ready = WaitForReady(TimeSpan.FromSeconds(15));
        if (!ready)
        {
            Console.Error.WriteLine("[parent] child did not report ready");
            KillChild();
            _exitCode = 4;
            return;
        }

        if (_options.PauseAfterSeconds > 0)
            _ = PauseAfterAsync(_options.PauseAfterSeconds);

        StartAudio();
    }

    private bool _ready;

    private bool WaitForReady(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout && !_ready && _child?.HasExited == false)
            Thread.Sleep(50);
        return _ready;
    }

    private void OnEvent(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        try
        {
            using var doc = JsonDocument.Parse(line);
            var type = doc.RootElement.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "ready")
            {
                _ready = true;
                Console.WriteLine("[parent] child ready");
            }
            else if (type == "stats")
            {
                Console.WriteLine("[stats] " + line);
            }
            else if (type is "paused" or "resumed" or "stopped")
            {
                Console.WriteLine("[event] " + line);
            }
        }
        catch
        {
            Console.WriteLine("[child-out] " + line);
        }
    }

    private int WaitForExit()
    {
        if (_child is null)
            return 4;

        if (_options.Seconds > 0)
        {
            var deadline = DateTime.UtcNow.AddSeconds(_options.Seconds + 5);
            while (!_child.HasExited && DateTime.UtcNow < deadline)
                Thread.Sleep(100);

            if (!_child.HasExited)
            {
                Send(new { cmd = "stop" });
                _child.WaitForExit(8000);
            }
        }
        else
        {
            while (!_child.HasExited)
                Thread.Sleep(100);
        }

        return _child.HasExited ? _child.ExitCode : 0;
    }

    private async Task PauseAfterAsync(double seconds)
    {
        await Task.Delay(TimeSpan.FromSeconds(seconds));
        Send(new { cmd = "pause" });
        await Task.Delay(2000);
        Send(new { cmd = "resume" });
    }

    private void StartAudio()
    {
        if (_options.NoAudio)
            return;

        try
        {
            _audio = new AudioSpectrum();
            _audioTimer = new Timer(_ => SendAudio(), null, 100, 33);
            Console.WriteLine("[parent] WASAPI loopback started");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[parent] audio unavailable: " + ex.Message);
        }
    }

    private void SendAudio()
    {
        if (_audio is null || _child is null || _child.HasExited)
            return;
        try
        {
            if (!_audio.TryGetSpectrum(out var bins, out var level))
                return;
            Send(new
            {
                cmd = "audio",
                bins,
                level,
                sentAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            });
            if (!_audioLogged)
            {
                _audioLogged = true;
                Console.WriteLine($"[parent] first audio spectrum level={level:F3} bins={bins.Length}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[parent] audio send failed: " + ex.Message);
        }
    }

    private void StopAudio()
    {
        _audioTimer?.Dispose();
        _audioTimer = null;
        _audio?.Dispose();
        _audio = null;
    }

    private void Send(object obj)
    {
        if (_child is null || _child.HasExited)
            return;
        try
        {
            _child.StandardInput.WriteLine(JsonSerializer.Serialize(obj));
            _child.StandardInput.Flush();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("[parent] send failed: " + ex.Message);
        }
    }

    private void KillChild()
    {
        if (_child is null)
            return;
        try
        {
            if (!_child.HasExited)
            {
                Send(new { cmd = "stop" });
                if (!_child.WaitForExit(5000))
                    _child.Kill(entireProcessTree: true);
            }
        }
        catch { }
        finally
        {
            _child.Dispose();
            _child = null;
        }
    }

    public void Dispose() => KillChild();
}

internal sealed class Options
{
    public string ChildPath { get; private set; } =
        Path.Combine(AppContext.BaseDirectory, "SceneWallpaperHost.exe");
    public string ScenePath { get; private set; } =
        Path.Combine(AppContext.BaseDirectory, "scenes", "audio_gradient", "scene.json");
    public int Fps { get; private set; } = 60;
    public double Seconds { get; private set; }
    public double PauseAfterSeconds { get; private set; }
    public bool NoAudio { get; private set; }
    public bool Vsync { get; private set; }
    public bool HalfRes { get; private set; }
    public bool Stats { get; private set; }
    public int Supersample { get; private set; } = 1;
    public bool ShowHelp { get; private set; }

    public static Options Parse(string[] args)
    {
        var o = new Options();
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--child": o.ChildPath = args[++i]; break;
                case "--scene": o.ScenePath = args[++i]; break;
                case "--fps": o.Fps = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--seconds": o.Seconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--pause-after": o.PauseAfterSeconds = double.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--no-audio": o.NoAudio = true; break;
                case "--vsync": o.Vsync = true; break;
                case "--half-res": o.HalfRes = true; break;
                case "--supersample": o.Supersample = int.Parse(args[++i], CultureInfo.InvariantCulture); break;
                case "--stats": o.Stats = true; break;
                case "--help":
                case "-h": o.ShowHelp = true; break;
            }
        }
        return o;
    }

    public static void PrintHelp()
    {
        Console.WriteLine("SceneWallpaperParent --child <SceneWallpaperHost.exe> --scene <scene.json>");
        Console.WriteLine("  [--seconds N] [--pause-after N] [--no-audio] [--fps 30|60] [--vsync]");
        Console.WriteLine("  [--half-res] [--supersample 2|4] [--stats]");
    }
}
