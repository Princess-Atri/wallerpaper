using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Wallerpaper.Setup;

internal static class Program
{
    private const string ProductName = "Wallerpaper";
    private const string Version = "0.7.0";
    private const string RunValue = "WallerpaperShell";

    private static string InstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", ProductName);

    private static string DataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        ProductName);

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            var uninstall = args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase));
            var fromTemp = args.Any(a => a.Equals("--from-temp", StringComparison.OrdinalIgnoreCase));
            var noLaunch = args.Any(a => a.Equals("--no-launch", StringComparison.OrdinalIgnoreCase));
            var custom = args.FirstOrDefault(a => a.StartsWith("--install-dir=", StringComparison.OrdinalIgnoreCase));
            var installDir = custom is null ? InstallDir : custom["--install-dir=".Length..];

            return uninstall
                ? Uninstall(installDir, fromTemp)
                : Install(installDir, noLaunch);
        }
        catch (Exception ex)
        {
            Trace("FATAL: " + ex);
            MessageBox($"Wallerpaper 安装程序失败：\n{ex.Message}", "Wallerpaper", 0x10);
            return 1;
        }
    }

    private static int Install(string installDir, bool noLaunch)
    {
        using var payload = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("payload.zip")
            ?? throw new InvalidOperationException("payload.zip missing; rebuild with tools/build-release.ps1.");

        StopProcesses();
        var temp = CreateStagingDirectory(installDir);
        try
        {
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
                zip.ExtractToDirectory(temp, overwriteFiles: true);

            if (Directory.Exists(installDir))
                TryDelete(installDir);
            Directory.CreateDirectory(installDir);
            CopyDirectory(temp, installDir);

            var self = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot resolve setup executable.");
            File.Copy(self, Path.Combine(installDir, "WallerpaperSetup.exe"), overwrite: true);

            try
            {
                CreateShortcuts(installDir);
                Trace("shortcuts created");
            }
            catch (Exception ex)
            {
                // A locked corporate desktop must not block the install; the
                // Start Menu/desktop shortcut is optional.
                Trace("shortcut failed: " + ex);
            }

            try
            {
                WriteUninstallRegistry(installDir);
                Trace("uninstall registry written");
            }
            catch (Exception ex)
            {
                // Restricted/Low-integrity launches can read but not write the
                // Uninstall key. The app is still usable; report it but do not
                // fail the entire installation.
                Trace("uninstall registry failed: " + ex);
            }

            try
            {
                WriteRunKey(installDir);
                Trace("run key written");
            }
            catch (Exception ex)
            {
                Trace("run key failed: " + ex);
            }

            if (!noLaunch)
            {
                var exe = Path.Combine(installDir, "WallerpaperShell.exe");
                if (File.Exists(exe))
                    Process.Start(new ProcessStartInfo(exe) { WorkingDirectory = installDir });
            }
            return 0;
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static int Uninstall(string installDir, bool fromTemp)
    {
        StopProcesses();
        RemoveRunKey();
        RemoveUninstallRegistry();
        RemoveShortcuts();

        var current = Environment.ProcessPath ?? "";
        if (!fromTemp && current.StartsWith(installDir, StringComparison.OrdinalIgnoreCase))
        {
            // The uninstaller cannot delete its own install directory while running.
            var temp = Path.Combine(Path.GetTempPath(),
                "WallerpaperUninstall-" + Guid.NewGuid().ToString("N") + ".exe");
            File.Copy(current, temp, overwrite: true);
            Process.Start(new ProcessStartInfo(temp,
                $"--uninstall --from-temp --install-dir=\"{installDir}\"")
            {
                UseShellExecute = false
            });
            return 0;
        }

        TryDelete(installDir);
        TryDelete(DataDir);

        if (Directory.Exists(installDir))
        {
            // A renderer/Host may still be shutting down; schedule a final
            // out-of-process cleanup so the install directory cannot remain.
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping 127.0.0.1 -n 3 >nul & rmdir /s /q \"{installDir}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }

        if (fromTemp && File.Exists(current))
        {
            Process.Start(new ProcessStartInfo("cmd.exe",
                $"/c ping 127.0.0.1 -n 3 >nul & del /f /q \"{current}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
        }
        return 0;
    }

    private static void WriteRunKey(string installDir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key.SetValue(RunValue,
            $"\"{Path.Combine(installDir, "WallerpaperShell.exe")}\" --minimized");
    }

    private static void RemoveRunKey()
    {
        using var key = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
        key?.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    private static void WriteUninstallRegistry(string installDir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProductName}", writable: true);
        key.SetValue("DisplayName", "Wallerpaper");
        key.SetValue("DisplayVersion", Version);
        key.SetValue("Publisher", "Wallerpaper");
        key.SetValue("InstallLocation", installDir);
        key.SetValue("DisplayIcon", Path.Combine(installDir, "WallerpaperShell.exe"));
        key.SetValue("UninstallString",
            $"\"{Path.Combine(installDir, "WallerpaperSetup.exe")}\" --uninstall --install-dir=\"{installDir}\"");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    private static void RemoveUninstallRegistry()
    {
        Registry.CurrentUser.DeleteSubKeyTree(
            $@"Software\Microsoft\Windows\CurrentVersion\Uninstall\{ProductName}",
            throwOnMissingSubKey: false);
    }

    private static void CreateShortcuts(string installDir)
    {
        var target = Path.Combine(installDir, "WallerpaperShell.exe");
        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Wallerpaper.lnk"), target, installDir);
        CreateShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "Wallerpaper.lnk"), target, installDir);
    }

    private static void CreateShortcut(string path, string target, string workingDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var type = Type.GetTypeFromProgID("WScript.Shell")
            ?? throw new InvalidOperationException("WScript.Shell unavailable.");
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic shortcut = shell.CreateShortcut(path);
        shortcut.TargetPath = target;
        shortcut.WorkingDirectory = workingDirectory;
        shortcut.IconLocation = target + ",0";
        shortcut.Description = "Wallerpaper 动态壁纸";
        shortcut.Save();
    }

    private static void RemoveShortcuts()
    {
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            "Wallerpaper.lnk"));
        TryDelete(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.StartMenu),
            "Programs", "Wallerpaper.lnk"));
    }

    private static void StopProcesses()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var any = false;
            foreach (var name in new[]
                     {
                         "WallerpaperShell", "WallerpaperHost", "SceneWallpaperHost",
                         "VideoWallpaperHost", "WebWallpaperHost"
                     })
            {
                foreach (var p in Process.GetProcessesByName(name))
                {
                    any = true;
                    try { p.Kill(entireProcessTree: true); p.WaitForExit(3000); }
                    catch { }
                }
            }
            if (!any) break;
            Thread.Sleep(700);
        }
    }

    private static string CreateStagingDirectory(string installDir)
    {
        var candidates = new[]
        {
            Path.Combine(Path.GetTempPath(),
                "WallerpaperSetup-" + Guid.NewGuid().ToString("N")),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Wallerpaper", "setup-" + Guid.NewGuid().ToString("N")),
            installDir.TrimEnd('\\') + ".setup-" + Guid.NewGuid().ToString("N")
        };

        var errors = new List<string>();
        foreach (var candidate in candidates)
        {
            try
            {
                Directory.CreateDirectory(candidate);
                Trace("staging=" + candidate);
                return candidate;
            }
            catch (Exception ex)
            {
                errors.Add($"{candidate}: {ex.Message}");
            }
        }
        throw new IOException(
            "无法创建安装临时目录。\n\n" +
            "如果安装器是从 DSH、Agent、沙箱或文件预览卡片启动的，请先把安装包复制到桌面或下载目录，" +
            "再用文件资源管理器双击运行；不要从受限终端启动。\n\n" +
            string.Join("\n", errors));
    }

    private static void CopyDirectory(string source, string target)
    {
        foreach (var dir in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, dir)));
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var dest = Path.Combine(target, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, overwrite: true);
        }
    }

    private static void TryDelete(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
                else if (File.Exists(path))
                    File.Delete(path);
                return;
            }
            catch
            {
                Thread.Sleep(500);
            }
        }
    }

    private static void Trace(string message)
    {
        try
        {
            var baseDir = Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
            File.AppendAllText(Path.Combine(baseDir, "WallerpaperSetup.log"),
                $"{DateTime.Now:O} {message}\n");
        }
        catch { }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBoxW(IntPtr hwnd, string text, string caption, uint type);

    private static void MessageBox(string text, string caption, uint type) =>
        MessageBoxW(IntPtr.Zero, text, caption, type);
}
