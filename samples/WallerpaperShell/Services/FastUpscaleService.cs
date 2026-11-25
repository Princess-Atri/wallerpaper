using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wallerpaper.Shell.Services;

public sealed record FastUpscaleResult(
    string OutputPath,
    int Width,
    int Height,
    TimeSpan Elapsed);

/// <summary>
/// "Fast" upscale: classic filter chain (denoise -> lanczos -> contrast adaptive
/// sharpen) encoded by the GPU, no Real-ESRGAN. Seconds instead of minutes, at
/// the cost of not inventing detail. Used by the 超分 page's "快速放大" mode.
/// </summary>
public sealed class FastUpscaleService
{
    private const int MaxWidth = 3840;
    private const int MaxHeight = 2160;

    /// <summary>Finds an ffmpeg that has the filters we need plus a fast
    /// encoder (hardware first). Falls back to null when nothing suitable is
    /// installed; the page then keeps offering the AI path only.</summary>
    public static string? FindFfmpeg()
    {
        var candidates = new List<string>();
        var beside = Path.Combine(AppContext.BaseDirectory, "ai-tools");
        candidates.Add(Path.Combine(beside, "ffmpeg-full.exe"));
        candidates.Add(@"D:\Desktop\wallerpaper c\tools\ai\ffmpeg-full.exe");
        // A full build that happens to be installed for other tools.
        candidates.Add(@"D:\Desktop\Applio-main\ffmpeg.exe");
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            try { candidates.Add(Path.Combine(dir, "ffmpeg.exe")); } catch { }
        }

        foreach (var candidate in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(candidate))
                continue;
            if (HasFastPipeline(candidate))
                return candidate;
        }
        return null;
    }

    private static bool HasFastPipeline(string ffmpeg)
    {
        try
        {
            var filters = RunCapture(ffmpeg, "-hide_banner -filters");
            var encoders = RunCapture(ffmpeg, "-hide_banner -encoders");
            var hasSharpen = filters.Contains("cas ") || filters.Contains("unsharp");
            var hasEncoder = encoders.Contains("h264_mf") || encoders.Contains("hevc_mf")
                || encoders.Contains("h264_nvenc") || encoders.Contains("libx264");
            return hasSharpen && hasEncoder;
        }
        catch
        {
            return false;
        }
    }

    private static string RunCapture(string exe, string args)
    {
        var psi = new ProcessStartInfo(exe, args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit(5000);
        return text;
    }

    public async Task<FastUpscaleResult> UpscaleAsync(
        string inputPath,
        int factor,
        string? outputDirectory = null,
        Action<int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(inputPath))
            throw new FileNotFoundException("视频不存在", inputPath);
        if (factor is not (2 or 3 or 4))
            throw new ArgumentOutOfRangeException(nameof(factor), "factor must be 2, 3 or 4");

        var ffmpeg = FindFfmpeg() ?? throw new InvalidOperationException(
            "未找到可用的完整版 ffmpeg（需要 cas/unsharp 滤镜和 h264_mf/h264_nvenc/libx264 编码器）。" +
            "把 ffmpeg-full.exe 放到 ai-tools 目录即可启用快速放大。");

        var (width, height, duration) = Probe(ffmpeg, inputPath);
        if (width <= 0 || height <= 0)
            throw new InvalidOperationException("无法读取源视频分辨率。");

        var targetWidth = Math.Min(width * factor, MaxWidth);
        var targetHeight = (int)Math.Round(targetWidth * (double)height / width);
        if (targetHeight > MaxHeight)
        {
            targetHeight = MaxHeight;
            targetWidth = (int)Math.Round(targetHeight * (double)width / height);
        }
        // Keep both dimensions even for yuv420p.
        targetWidth &= ~1;
        targetHeight &= ~1;

        var outputDir = !string.IsNullOrWhiteSpace(outputDirectory)
            ? outputDirectory!
            : AppStorage.CachePath("superres");
        Directory.CreateDirectory(outputDir);
        var stem = Path.GetFileNameWithoutExtension(inputPath);
        var outputPath = Path.Combine(outputDir,
            $"{stem}_FAST{targetWidth}x{targetHeight}_{DateTime.Now:yyyyMMdd-HHmmss}.mp4");

        var started = Stopwatch.StartNew();
        progress?.Invoke(5, $"快速放大：{width}×{height} → {targetWidth}×{targetHeight}（不走 AI）");

        var filters = "atadenoise," +
                      $"scale={targetWidth}:{targetHeight}:flags=lanczos," +
                      "setsar=1,cas=0.4";

        var attempts = new List<string[]>
        {
            new[] { "-c:v", "h264_mf", "-quality", "90", "-b:v", "40M" },
            new[] { "-c:v", "hevc_mf", "-quality", "90", "-b:v", "30M" },
            new[] { "-c:v", "h264_nvenc", "-preset", "medium", "-cq", "19" },
            new[] { "-c:v", "libx264", "-preset", "slow", "-crf", "16" }
        };

        string lastError = "";
        foreach (var encoder in attempts)
        {
            try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
            var args = new List<string>
            {
                "-y", "-hide_banner", "-loglevel", "error", "-i", inputPath,
                "-vf", filters
            };
            args.AddRange(encoder);
            args.AddRange(new[]
            {
                "-pix_fmt", "yuv420p",
                "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709",
                "-c:a", "copy", "-movflags", "+faststart", outputPath
            });
            progress?.Invoke(30, $"编码中（{encoder[1]}）…");
            var (ok, error) = await RunAsync(ffmpeg, args, cancellationToken);
            if (ok && File.Exists(outputPath) && new FileInfo(outputPath).Length > 1024)
            {
                progress?.Invoke(100, "快速放大完成");
                return new FastUpscaleResult(outputPath, targetWidth, targetHeight,
                    started.Elapsed);
            }
            lastError = error;
        }

        throw new InvalidOperationException(
            "快速放大失败（所有编码器都不可用）。" +
            (string.IsNullOrWhiteSpace(lastError) ? "" : "\n" + lastError));
    }

    private static (int Width, int Height, TimeSpan Duration) Probe(string ffmpeg, string input)
    {
        var text = RunCapture(ffmpeg, $"-hide_banner -i \"{input}\"");
        var size = Regex.Match(text, @"Video:.*?(\d{2,5})x(\d{2,5})");
        var duration = Regex.Match(text, @"Duration: (\d{2}):(\d{2}):(\d{2})\.(\d{2})");
        var width = size.Success ? int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture) : 0;
        var height = size.Success ? int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
        var span = TimeSpan.Zero;
        if (duration.Success)
            span = new TimeSpan(0, int.Parse(duration.Groups[1].Value),
                int.Parse(duration.Groups[2].Value), int.Parse(duration.Groups[3].Value));
        return (width, height, span);
    }

    private static async Task<(bool Ok, string Error)> RunAsync(string exe,
        IEnumerable<string> args, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                stderr.AppendLine(e.Data);
        };
        process.OutputDataReceived += (_, _) => { };
        process.Start();
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(1));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        var text = stderr.ToString().Trim();
        var error = text.Length > 600 ? text[^600..] : text;
        return (process.ExitCode == 0, error);
    }
}
