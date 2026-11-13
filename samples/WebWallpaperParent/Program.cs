using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace WebWallpaperParent;

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

        if (options.Error is not null)
        {
            Console.Error.WriteLine(options.Error);
            return 2;
        }

        var controller = new ParentController(options);
        return await controller.RunAsync();
    }
}

internal sealed class Options
{
    public string ChildPath { get; private set; } = "";
    public string? HtmlPath { get; private set; }
    public string? Url { get; private set; }
    public int AutoStopSeconds { get; private set; }
    public int IdleSeconds { get; private set; } = 15;
    public bool NoAudio { get; private set; }
    public bool NoGpu { get; private set; }
    public bool TestMouseSweep { get; private set; }
    public bool ShowHelp { get; private set; }
    public string? Error { get; private set; }

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
                case "--html":
                    if (i + 1 < args.Length) o.HtmlPath = args[++i];
                    break;
                case "--url":
                    if (i + 1 < args.Length) o.Url = args[++i];
                    break;
                case "--seconds":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var sec)) o.AutoStopSeconds = sec;
                    break;
                case "--idle-seconds":
                    if (i + 1 < args.Length && int.TryParse(args[++i], out var idle)) o.IdleSeconds = Math.Max(1, idle);
                    break;
                case "--no-audio":
                    o.NoAudio = true;
                    break;
                case "--no-gpu":
                    o.NoGpu = true;
                    break;
                case "--test-mouse-sweep":
                    o.TestMouseSweep = true;
                    break;
                case "--help":
                case "-h":
                    o.ShowHelp = true;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(o.ChildPath))
            o.ChildPath = Path.Combine(AppContext.BaseDirectory, "WebWallpaperHost.exe");

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
            o.HtmlPath = full;
            o.Url = null;
        }
        else if (string.IsNullOrWhiteSpace(o.Url))
        {
            o.Error = "Specify --html or --url.";
        }

        return o;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("WebWallpaperParent — WebView2 wallpaper over stdio + WASAPI loopback");
        Console.WriteLine();
        Console.WriteLine("  WebWallpaperParent.exe --html <index.html> [--child <host.exe>]");
        Console.WriteLine("      [--url https://...] [--seconds N] [--idle-seconds 15]");
        Console.WriteLine("      [--no-audio] [--no-gpu] [--test-mouse-sweep]");
        Console.WriteLine();
        Console.WriteLine("  Commands: p=pause, r=resume, s=stats, q=stop");
    }
}

internal sealed class ParentController
{
    private readonly Options _options;
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly object _inputLock = new();
    private JobObject? _job;

    private readonly string _instanceId = Guid.NewGuid().ToString("N");
    private readonly string _profilePath;

    private Process? _child;
    private AudioSpectrum? _audio;
    private Timer? _audioTimer;
    private bool _audioSendLogged;
    private int? _exitCode;

    public ParentController(Options options)
    {
        _options = options;
        _profilePath = Path.Combine(Path.GetTempPath(), "Wallerpaper", "WebView2", _instanceId);
        try
        {
            _job = new JobObject();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[parent] job object unavailable: {ex.Message}");
        }
    }

    public async Task<int> RunAsync()
    {
        if (!File.Exists(_options.ChildPath))
        {
            Console.Error.WriteLine($"child not found: {_options.ChildPath}");
            return 2;
        }

        try
        {
            StartChild();

            var ready = await Task.WhenAny(_ready.Task, Task.Delay(TimeSpan.FromSeconds(25)));
            if (ready != _ready.Task)
            {
                Console.Error.WriteLine("child did not become ready within 25s.");
                await StopAsync();
                return 4;
            }

            if (!_ready.Task.Result)
            {
                Console.Error.WriteLine($"child exited/failed before ready (exitCode={_exitCode?.ToString() ?? "?"}).");
                await StopAsync();
                return _exitCode ?? 4;
            }

            StartAudio();

            if (_options.TestMouseSweep)
            {
                await Task.Delay(2500);
                foreach (var point in new[] { (300, 300), (600, 400), (900, 500), (1200, 600) })
                {
                    Send(new { cmd = "testmouse", x = point.Item1, y = point.Item2 });
                    await Task.Delay(700);
                }
            }

            if (_options.AutoStopSeconds > 0)
            {
                await Task.Delay(TimeSpan.FromSeconds(_options.AutoStopSeconds));
                await StopAsync();
                return _exitCode ?? 0;
            }

            PrintHelp();
            while (true)
            {
                var line = Console.ReadLine();
                if (line is null)
                    break;

                switch (line.Trim().ToLowerInvariant())
                {
                    case "p":
                        Send(new { cmd = "pause" });
                        break;
                    case "r":
                        Send(new { cmd = "resume" });
                        break;
                    case "s":
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
            StopAudio();
            KillChildTree();
            _job?.Dispose();
            _job = null;
            DeleteProfile();
        }
    }

    private void StartChild()
    {
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
            WorkingDirectory = Path.GetDirectoryName(Path.GetFullPath(_options.ChildPath)) ?? AppContext.BaseDirectory
        };

        psi.FileName = Path.GetFullPath(_options.ChildPath);
        psi.ArgumentList.Add("--id");
        psi.ArgumentList.Add(_instanceId);
        psi.ArgumentList.Add("--user-data-folder");
        psi.ArgumentList.Add(_profilePath);
        psi.ArgumentList.Add("--idle-seconds");
        psi.ArgumentList.Add(_options.IdleSeconds.ToString());

        if (_options.HtmlPath is not null)
        {
            psi.ArgumentList.Add("--html");
            psi.ArgumentList.Add(_options.HtmlPath);
        }
        else
        {
            psi.ArgumentList.Add("--url");
            psi.ArgumentList.Add(_options.Url!);
        }

        if (_options.NoGpu)
            psi.ArgumentList.Add("--no-gpu");

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

        try
        {
            _job?.Assign(_child);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[parent] job assign failed: {ex.Message}");
        }

        _child.BeginOutputReadLine();
        _child.BeginErrorReadLine();
    }

    private void StartAudio()
    {
        if (_options.NoAudio)
            return;

        try
        {
            _audio = new AudioSpectrum();
            if (!_audio.Available)
            {
                Console.Error.WriteLine($"[parent] audio capture unavailable: {_audio.Error}");
                _audio.Dispose();
                _audio = null;
                return;
            }

            Console.WriteLine($"[parent] WASAPI loopback started: {_audio.GetType().Name}");

            _audioTimer = new Timer(_ =>
            {
                if (_audio is null)
                    return;
                if (_audio.TryGetSpectrum(out var bins, out var level))
                {
                    if (!_audioSendLogged)
                    {
                        _audioSendLogged = true;
                        Console.WriteLine($"[parent] first audio spectrum level={level:F3} bins={bins.Length}");
                    }

                    Send(new
                    {
                        cmd = "audio",
                        bins,
                        level
                    });
                }
            }, null, 50, 33);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[parent] audio capture failed: {ex.Message}");
            _audio?.Dispose();
            _audio = null;
        }
    }

    private void StopAudio()
    {
        _audioTimer?.Dispose();
        _audioTimer = null;
        _audio?.Dispose();
        _audio = null;
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
                    Console.WriteLine($"[child] READY version={GetString(root, "webViewVersion")} " +
                                      $"gpu={GetBool(root, "gpu")}");
                    _ready.TrySetResult(true);
                    break;
                case "loaded":
                    Console.WriteLine($"[child] LOADED {GetString(root, "url")}");
                    break;
                case "error":
                    Console.Error.WriteLine($"[child] ERROR {GetString(root, "code")}: {GetString(root, "message")}");
                    break;
                case "stopped":
                    Console.WriteLine($"[child] STOPPED exit={GetInt(root, "exitCode")}");
                    break;
                case "paused":
                    Console.WriteLine("[child] paused");
                    break;
                case "resumed":
                    Console.WriteLine("[child] resumed");
                    break;
                case "stats":
                    Console.WriteLine($"[child] stats ws={GetDouble(root, "workingSetMB"):F1}MB " +
                                      $"private={GetDouble(root, "privateMB"):F1}MB " +
                                      $"cpu={GetDouble(root, "cpuSeconds"):F2}s " +
                                      $"threads={GetInt(root, "threads")} handles={GetInt(root, "handles")} " +
                                      $"idle={GetBool(root, "idle")} paused={GetBool(root, "paused")} " +
                                      $"audioBins={GetInt(root, "audioBins")} level={GetDouble(root, "audioLevel"):F2}");
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
        _exitCode = _child?.ExitCode;
        Console.WriteLine($"[parent] child exited with code {_exitCode}");
        _ready.TrySetResult(false);
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
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[parent] send failed: {ex.Message}");
            }
        }
    }

    private async Task StopAsync()
    {
        StopAudio();

        var child = _child;
        if (child is null || child.HasExited)
            return;

        Send(new { cmd = "stop" });

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            await child.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("[parent] child did not exit in 8s; killing tree.");
            KillChildTree();
        }

        // Closing the Job kills any WebView2 browser/GPU descendants that outlived
        // the host. Only then remove the profile directory this instance owns.
        _job?.Dispose();
        _job = null;
        Thread.Sleep(200);
        DeleteProfile();
    }

    private void DeleteProfile()
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            try
            {
                if (Directory.Exists(_profilePath))
                    Directory.Delete(_profilePath, recursive: true);
                return;
            }
            catch (IOException)
            {
                Thread.Sleep(200);
            }
            catch (UnauthorizedAccessException)
            {
                Thread.Sleep(200);
            }
        }

        Console.Error.WriteLine($"[parent] could not delete WebView2 profile: {_profilePath}");
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

    private static void PrintHelp()
    {
        Console.WriteLine();
        Console.WriteLine("Commands: p=pause, r=resume, s=stats, q=stop");
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

/// <summary>
/// Kills the child and its WebView2 browser/renderer descendants when the parent
/// terminates, even if the child is killed without running its cleanup code.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private const int JobObjectExtendedLimitInformation = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;

    private readonly IntPtr _handle;

    public JobObject()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException($"CreateJobObject failed: {Marshal.GetLastWin32Error()}");

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JobObjectLimitKillOnJobClose
            }
        };

        if (!SetInformationJobObject(
                _handle,
                JobObjectExtendedLimitInformation,
                ref info,
                Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
        {
            var error = Marshal.GetLastWin32Error();
            CloseHandle(_handle);
            throw new InvalidOperationException($"SetInformationJobObject failed: {error}");
        }
    }

    public void Assign(Process process)
    {
        if (!AssignProcessToJobObject(_handle, process.Handle))
            throw new InvalidOperationException($"AssignProcessToJobObject failed: {Marshal.GetLastWin32Error()}");
    }

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
            CloseHandle(_handle);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, int jobObjectInformationClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION lpJobObjectInformation,
        int cbJobObjectInformationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
