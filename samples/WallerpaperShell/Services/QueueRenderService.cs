using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

public sealed record QueueRenderResult(string OutputPath, int Entries, TimeSpan Elapsed);

/// <summary>
/// Renders a trimmed, hard-cut multi-video queue to one high-quality MP4.
/// The generated file is looped by VideoWallpaperHost, which gives the queue
/// whole-queue looping without a second decoder. Crossfade is a later pass.
/// </summary>
public sealed class QueueRenderService
{
    public async Task<QueueRenderResult> RenderAsync(
        IReadOnlyList<PlaylistEntry> entries,
        Action<int, string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var usable = entries.Where(x => File.Exists(x.Path)).ToList();
        if (usable.Count == 0)
            throw new InvalidOperationException("播放队列里没有可用的视频。");
        var ffmpeg = AiInterpolationService.FindFfmpegEncode()
            ?? throw new InvalidOperationException("未找到编码用 FFmpeg。");
        var started = Stopwatch.StartNew();

        progress?.Invoke(4, "读取视频信息…");
        var info = new List<(double Duration, int Width, int Height, bool Audio)>();
        foreach (var entry in usable)
            info.Add(await ProbeAsync(ffmpeg, entry.Path, cancellationToken));

        var width = info[0].Width > 0 ? info[0].Width : 1920;
        var height = info[0].Height > 0 ? info[0].Height : 1080;
        var allAudio = info.All(x => x.Audio);
        var durations = new List<double>();
        for (var i = 0; i < usable.Count; i++)
        {
            var total = info[i].Duration > 0 ? info[i].Duration : 0;
            var start = Math.Clamp(usable[i].StartSeconds, 0, Math.Max(0, total - 0.1));
            var end = usable[i].EndSeconds > start
                ? Math.Min(usable[i].EndSeconds, total > 0 ? total : usable[i].EndSeconds)
                : total;
            if (end <= start)
                end = total > start ? total : start + 1;
            durations.Add(Math.Max(0.2, end - start));
        }

        var args = new List<string> { "-y", "-hide_banner", "-loglevel", "error" };
        for (var i = 0; i < usable.Count; i++)
        {
            var start = Math.Clamp(usable[i].StartSeconds, 0, Math.Max(0, durations[i] + usable[i].StartSeconds));
            args.Add("-ss");
            args.Add(start.ToString("0.###", CultureInfo.InvariantCulture));
            args.Add("-t");
            args.Add(durations[i].ToString("0.###", CultureInfo.InvariantCulture));
            args.Add("-i");
            args.Add(usable[i].Path);
        }

        var filters = new List<string>();
        var concatInputs = new List<string>();
        for (var i = 0; i < usable.Count; i++)
        {
            filters.Add($"[{i}:v]setpts=PTS-STARTPTS," +
                        $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
                        $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1[v{i}]");
            concatInputs.Add($"[v{i}]");
            if (allAudio)
            {
                filters.Add($"[{i}:a]asetpts=PTS-STARTPTS[a{i}]");
            }
        }
        if (usable.Count == 1)
        {
            filters.Add("[v0]null[vout]");
            if (allAudio)
                filters.Add("[a0]anull[aout]");
        }
        else
        {
            filters.Add($"{string.Concat(concatInputs)}concat=n={usable.Count}:v=1:a=0[vout]");
            if (allAudio)
                filters.Add($"{string.Concat(Enumerable.Range(0, usable.Count).Select(i => $"[a{i}]"))}concat=n={usable.Count}:v=0:a=1[aout]");
        }

        args.Add("-filter_complex");
        args.Add(string.Join(";", filters));
        args.Add("-map");
        args.Add("[vout]");
        if (allAudio)
        {
            args.Add("-map");
            args.Add("[aout]");
        }
        args.AddRange(new[]
        {
            "-c:v", "libx264", "-preset", "medium", "-crf", "14", "-pix_fmt", "yuv420p"
        });
        if (allAudio)
            args.AddRange(new[] { "-c:a", "aac", "-b:a", "192k" });
        args.AddRange(new[] { "-movflags", "+faststart" });

        var outDir = AppStorage.CachePath("queue");
        var output = Path.Combine(outDir, $"queue_{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        args.Add(output);

        progress?.Invoke(25, "拼接队列并编码…");
        if (!await RunAsync(ffmpeg, args, cancellationToken) ||
            !File.Exists(output) || new FileInfo(output).Length < 1024)
            throw new InvalidOperationException("队列视频生成失败。");

        progress?.Invoke(100, "队列视频生成完成");
        return new QueueRenderResult(output, usable.Count, started.Elapsed);
    }

    private static async Task<(double Duration, int Width, int Height, bool Audio)> ProbeAsync(
        string ffmpeg, string path, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ffmpeg,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);
        using var process = new Process { StartInfo = psi };
        process.Start();
        var text = await process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken);
        var duration = 0.0;
        var match = Regex.Match(text, @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
        if (match.Success)
            duration = int.Parse(match.Groups[1].Value) * 3600 +
                       int.Parse(match.Groups[2].Value) * 60 +
                       double.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);
        var size = Regex.Match(text, @"Video:.*?(\d{2,5})x(\d{2,5})");
        var width = size.Success ? int.Parse(size.Groups[1].Value) : 0;
        var height = size.Success ? int.Parse(size.Groups[2].Value) : 0;
        return (duration, width, height, text.Contains("Audio:", StringComparison.OrdinalIgnoreCase));
    }

    private static async Task<bool> RunAsync(string exe, IEnumerable<string> args,
        CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            psi.ArgumentList.Add(arg);
        using var process = new Process { StartInfo = psi };
        process.Start();
        var stderr = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrWhiteSpace(e.Data))
                stderr.AppendLine(e.Data);
        };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromHours(2));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return process.ExitCode == 0;
    }
}
