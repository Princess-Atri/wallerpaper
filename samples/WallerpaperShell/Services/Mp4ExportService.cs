using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Exports a normal video, or the two clips of a sequence, to a standalone
/// H.264 MP4. No runtime-only behavior (such as looping the second clip) is
/// baked into the file.
/// </summary>
public sealed class Mp4ExportService
{
    public async Task<string> ExportAsync(
        IReadOnlyList<string> paths,
        string? outputPath = null,
        Action<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var inputs = paths.Where(File.Exists).ToList();
        if (inputs.Count == 0)
            throw new InvalidOperationException("没有可转换的视频。");
        var frames = AiInterpolationService.FindFfmpegFrames()
            ?? throw new InvalidOperationException("未找到视频滤镜 FFmpeg。");
        var encode = AiInterpolationService.FindFfmpegEncode()
            ?? throw new InvalidOperationException("未找到编码 FFmpeg。");

        string output;
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            output = outputPath;
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        }
        else
        {
            var outDir = AppStorage.CachePath("mp4-export");
            var stem = Path.GetFileNameWithoutExtension(inputs[0]);
            output = Path.Combine(outDir,
                $"{stem}_export_{DateTime.Now:yyyyMMdd-HHmmss}.mp4");
        }

        if (inputs.Count == 1)
        {
            progress?.Invoke("正在转换 MP4…");
            await Run(encode, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", inputs[0],
                "-c:v", "libx264", "-preset", "slow", "-crf", "16",
                "-profile:v", "high", "-level", "4.1", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k",
                "-movflags", "+faststart", output
            }, cancellationToken);
            return output;
        }

        progress?.Invoke("正在拼接组合视频…");
        var (width, height) = await ProbeSize(frames, inputs[0], cancellationToken);
        width = Math.Max(2, width & ~1);
        height = Math.Max(2, height & ~1);
        var work = Path.Combine(Path.GetTempPath(), "WallerpaperMp4",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var intermediate = Path.Combine(work, "concat.mkv");
            var filters = new List<string>();
            for (var i = 0; i < inputs.Count; i++)
            {
                filters.Add(
                    $"[{i}:v]setpts=PTS-STARTPTS," +
                    $"scale={width}:{height}:force_original_aspect_ratio=decrease," +
                    $"pad={width}:{height}:(ow-iw)/2:(oh-ih)/2,setsar=1[v{i}]");
            }
            var refs = string.Concat(Enumerable.Range(0, inputs.Count).Select(i => $"[v{i}]"));
            filters.Add($"{refs}concat=n={inputs.Count}:v=1:a=0[vout]");
            var args = new List<string>
            {
                "-y", "-hide_banner", "-loglevel", "error"
            };
            foreach (var input in inputs) { args.Add("-i"); args.Add(input); }
            args.Add("-filter_complex");
            args.Add(string.Join(";", filters));
            args.Add("-map"); args.Add("[vout]");
            args.Add("-c:v"); args.Add("libvpx-vp9");
            args.Add("-lossless"); args.Add("1");
            args.Add("-row-mt"); args.Add("1"); args.Add("-cpu-used"); args.Add("4");
            args.Add("-an"); args.Add(intermediate);
            await Run(frames, args, cancellationToken);

            progress?.Invoke("正在编码 MP4…");
            await Run(encode, new[]
            {
                "-y", "-hide_banner", "-loglevel", "error",
                "-i", intermediate, "-i", inputs[0],
                "-map", "0:v:0", "-map", "1:a?",
                "-c:v", "libx264", "-preset", "slow", "-crf", "16",
                "-profile:v", "high", "-level", "4.1", "-pix_fmt", "yuv420p",
                "-c:a", "aac", "-b:a", "192k",
                "-movflags", "+faststart", output
            }, cancellationToken);
            return output;
        }
        finally
        {
            try { Directory.Delete(work, true); } catch { }
        }
    }

    private static async Task<(int Width, int Height)> ProbeSize(
        string exe, string path, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        psi.ArgumentList.Add("-hide_banner");
        psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add(path);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        var m = Regex.Match(err, @"Video:.*?(?<w>\d{2,5})x(?<h>\d{2,5})");
        if (!m.Success) throw new InvalidOperationException("无法读取视频尺寸。");
        return (int.Parse(m.Groups["w"].Value, CultureInfo.InvariantCulture),
            int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture));
    }

    private static async Task Run(string exe, IEnumerable<string> args,
        CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct);
        if (p.ExitCode != 0)
            throw new InvalidOperationException(
                $"MP4 转换失败（{Path.GetFileName(exe)}）：{err}");
    }
}
