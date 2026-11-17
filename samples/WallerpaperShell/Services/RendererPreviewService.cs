using System.Diagnostics;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Services;

/// <summary>
/// Runs a WebView2 or D3D11 renderer on the real desktop for a short preview.
/// The renderer draws onto WorkerW exactly like the live wallpaper, so the
/// preview shows the true output instead of a static thumbnail.
/// </summary>
public static class RendererPreviewService
{
    private static Process? _process;

    public static bool IsSupported(WallpaperItem item) =>
        item.Kind is WallpaperKind.Web or WallpaperKind.Scene;

    /// <summary>Starts the renderer for the item's monitor. Returns null when
    /// the renderer executable is missing.</summary>
    public static Process? Start(ShellConfig config, WallpaperItem item, MonitorDisplay monitor,
        Action<string> log)
    {
        Stop();
        var exe = item.Kind == WallpaperKind.Web
            ? config.Host.WebChildExe
            : config.Host.ChildExe;
        if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
        {
            log($"preview renderer missing: {exe}");
            return null;
        }

        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        psi.ArgumentList.Add(item.Kind == WallpaperKind.Web ? "--html" : "--scene");
        psi.ArgumentList.Add(item.Path);
        psi.ArgumentList.Add("--bounds");
        psi.ArgumentList.Add(monitor.X.ToString());
        psi.ArgumentList.Add(monitor.Y.ToString());
        psi.ArgumentList.Add(monitor.Width.ToString());
        psi.ArgumentList.Add(monitor.Height.ToString());
        psi.ArgumentList.Add("--zoom");
        psi.ArgumentList.Add(Math.Clamp(item.Zoom, 0.5, 2.0)
            .ToString("0.###", System.Globalization.CultureInfo.InvariantCulture));
        psi.Environment.Remove("DOTNET_ROOT");
        psi.Environment.Remove("DOTNET_ROOT_X64");

        try
        {
            var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) log("preview: " + e.Data);
            };
            process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) log("preview ERR: " + e.Data);
            };
            if (!process.Start())
                return null;
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            try { process.PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }
            _process = process;
            log($"preview renderer started pid={process.Id} kind={item.Kind}");
            return process;
        }
        catch (Exception ex)
        {
            log($"preview renderer start failed: {ex.Message}");
            return null;
        }
    }

    public static void Stop()
    {
        var process = _process;
        _process = null;
        if (process is null) return;
        try
        {
            if (!process.HasExited)
            {
                process.StandardInput.WriteLine("stop");
                process.StandardInput.Flush();
                if (!process.WaitForExit(2500))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch { }
        finally
        {
            process.Dispose();
        }
    }
}
