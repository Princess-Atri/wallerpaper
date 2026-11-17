using System.Diagnostics;
using Wallerpaper.Diagnostics;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Keeps the real Windows desktop wallpaper showing the first frame of the
/// wallpaper that is currently playing. The video renderer lives on WorkerW
/// above the desktop, so whenever the renderer is rebuilt (apply, host
/// restart, monitor resume) the desktop is visible for a few hundred
/// milliseconds; without this the user sees the old wallpaper or black.
///
/// Exactly one generated frame is kept: as soon as a new frame has been
/// applied, the previous frame file is deleted, so the desktop image library
/// never accumulates one PNG per wallpaper switch.
/// </summary>
public static class DesktopFirstFrameService
{
    /// <summary>Where generated desktop frames live (one file at a time).</summary>
    public static string FrameFolder => Path.Combine(AppStorage.CacheRoot, "desktop-frame");

    /// <summary>
    /// Extracts the first frame of <paramref name="item"/>, applies it as the
    /// desktop wallpaper and removes the previously generated frame.
    /// <paramref name="width"/>/<paramref name="height"/> describe the target
    /// monitor: the frame is rendered with the same fit/crop the renderer uses
    /// so switching from the static desktop image to the live video does not
    /// jump.
    /// </summary>
    public static async Task<bool> ApplyAsync(WallpaperItem item, int width = 0,
        int height = 0, int delayBeforeSetMs = 0)
    {
        if (item is null) return false;
        try
        {
            if (item.Kind == WallpaperKind.Static)
            {
                // The static apply path already put the real image on the
                // desktop; only the obsolete generated frame has to go.
                ClearFrames();
                return true;
            }
            if (item.Kind is WallpaperKind.Web or WallpaperKind.Scene)
            {
                // Web/Scene render on the desktop surface itself and there is
                // no cheap way to grab their first rendered frame, so the
                // desktop image is left untouched instead of showing a
                // placeholder gradient.
                return false;
            }

            var frame = await Task.Run(() => ExtractFirstFrame(item, width, height));
            if (string.IsNullOrWhiteSpace(frame) || !File.Exists(frame))
                return false;
            if (delayBeforeSetMs > 0)
                await Task.Delay(delayBeforeSetMs);
            if (!DesktopWallpaperService.SetImage(frame))
            {
                TryDeleteFile(frame);
                return false;
            }
            DesktopWallpaperService.MarkFrameApplied();
            PruneFrames(new[] { Path.GetFullPath(frame) });
            AppLog.Info($"desktop first frame applied: {Path.GetFileName(frame)}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("desktop first frame failed", ex);
            return false;
        }
    }

    /// <summary>Deletes every generated frame (desktop was restored).</summary>
    public static void ClearFrames()
    {
        PruneFrames(Array.Empty<string>());
        DesktopWallpaperService.ClearFrameMark();
    }

    /// <summary>Housekeeping: keep only the newest frame file.</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(FrameFolder) || !Directory.EnumerateFiles(FrameFolder).Any())
                return;
            var newest = new DirectoryInfo(FrameFolder)
                .EnumerateFiles("*", SearchOption.TopDirectoryOnly)
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (newest is not null) keep.Add(newest.FullName);
            // Never delete the file the desktop is actually displaying.
            var current = DesktopWallpaperService.CurrentPath;
            if (IsFrameFile(current)) keep.Add(Path.GetFullPath(current!));
            PruneFrames(keep);
        }
        catch { }
    }

    public static (int Files, long Bytes) Measure()
    {
        var files = 0;
        long bytes = 0;
        try
        {
            if (!Directory.Exists(FrameFolder)) return (0, 0);
            foreach (var info in new DirectoryInfo(FrameFolder)
                         .EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                files++;
                bytes += info.Length;
            }
        }
        catch { }
        return (files, bytes);
    }

    /// <summary>
    /// True when the current desktop wallpaper already is a generated frame
    /// (used to decide whether a frame has to be rebuilt on startup).
    /// </summary>
    public static bool IsFrameFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        try
        {
            var full = Path.GetFullPath(path);
            return full.StartsWith(Path.GetFullPath(FrameFolder) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) && File.Exists(full);
        }
        catch
        {
            return false;
        }
    }

    private static string? ExtractFirstFrame(WallpaperItem item, int width, int height)
    {
        var ffmpeg = AiInterpolationService.FindFfmpegFrames();
        if (string.IsNullOrWhiteSpace(ffmpeg) || !File.Exists(ffmpeg))
            return null;

        var sources = new List<string>();
        if (!string.IsNullOrWhiteSpace(item.Path)) sources.Add(item.Path);
        if (!string.IsNullOrWhiteSpace(item.SecondPath) &&
            !sources.Contains(item.SecondPath, StringComparer.OrdinalIgnoreCase))
        {
            sources.Add(item.SecondPath);
        }

        Directory.CreateDirectory(FrameFolder);
        foreach (var source in sources)
        {
            if (!File.Exists(source)) continue;
            var target = Path.Combine(FrameFolder,
                $"frame-{DateTime.Now:yyyyMMdd-HHmmss-fff}.jpg");
            // The very first frame is what playback shows first, so the desktop
            // image and the video's first presented frame are the same picture.
            // The fit-specific filter lays the frame out exactly like the
            // renderer will (cover crop / letterbox / blurred background), and
            // the frame is rendered at monitor resolution, so any Windows
            // wallpaper position setting displays it 1:1.
            foreach (var filter in FrameFilters(item.Fit, width, height))
            {
                if (!RunFrameCommand(ffmpeg, source, target, filter)) continue;
                if (File.Exists(target) && new FileInfo(target).Length > 0)
                    return target;
            }
            TryDeleteFile(target);
        }
        return null;
    }

    /// <summary>Filter candidates, from exact layout to last-resort native frame.</summary>
    private static IEnumerable<string> FrameFilters(string? fit, int width, int height)
    {
        if (width <= 0 || height <= 0)
        {
            yield return "";
            yield break;
        }

        var w = width;
        var h = height;
        var cover = $"scale={w}:{h}:force_original_aspect_ratio=increase,crop={w}:{h}";
        switch ((fit ?? "cover").ToLowerInvariant())
        {
            case "contain":
                yield return $"scale={w}:{h}:force_original_aspect_ratio=decrease," +
                             $"pad={w}:{h}:(ow-iw)/2:(oh-ih)/2:black";
                break;
            case "blur":
                yield return $"split=2[bg][fg];" +
                             $"[bg]scale={w}:{h}:force_original_aspect_ratio=increase," +
                             $"crop={w}:{h},gblur=sigma=24[b];" +
                             $"[fg]scale={w}:{h}:force_original_aspect_ratio=decrease[f];" +
                             $"[b][f]overlay=(W-w)/2:(H-h)/2";
                break;
            default:
                yield return cover;
                break;
        }
        yield return cover;
        yield return "";
    }

    private static bool RunFrameCommand(string ffmpeg, string source, string target,
        string filter)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ffmpeg,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-y");
            psi.ArgumentList.Add("-hide_banner");
            psi.ArgumentList.Add("-loglevel");
            psi.ArgumentList.Add("error");
            psi.ArgumentList.Add("-ss");
            psi.ArgumentList.Add("00:00:00");
            psi.ArgumentList.Add("-i");
            psi.ArgumentList.Add(source);
            psi.ArgumentList.Add("-frames:v");
            psi.ArgumentList.Add("1");
            psi.ArgumentList.Add("-an");
            if (!string.IsNullOrWhiteSpace(filter))
            {
                psi.ArgumentList.Add("-vf");
                psi.ArgumentList.Add(filter);
            }
            // JPEG keeps the wait short: a 4K PNG encode alone costs ~1.5s,
            // and the frame is only on screen for the length of a switch.
            psi.ArgumentList.Add("-q:v");
            psi.ArgumentList.Add("2");
            psi.ArgumentList.Add(target);
            using var process = Process.Start(psi);
            if (process is null) return false;
            if (!process.WaitForExit(15000))
            {
                try { process.Kill(true); } catch { }
                return false;
            }
            return process.ExitCode == 0 && File.Exists(target) &&
                   new FileInfo(target).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void PruneFrames(IEnumerable<string> keep)
    {
        try
        {
            if (!Directory.Exists(FrameFolder)) return;
            var keepFull = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in keep)
            {
                try { keepFull.Add(Path.GetFullPath(path)); } catch { }
            }
            foreach (var info in new DirectoryInfo(FrameFolder)
                         .EnumerateFiles("*", SearchOption.TopDirectoryOnly))
            {
                if (keepFull.Contains(info.FullName)) continue;
                try { info.Delete(); } catch { }
            }
        }
        catch { }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch { }
    }
}
