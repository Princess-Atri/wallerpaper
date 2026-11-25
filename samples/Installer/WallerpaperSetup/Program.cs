using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace BaiAcha.Setup;

internal static class Program
{
    private const string ProductName = "BaiAcha";
    private const string DisplayName = "BaiAcha 动态壁纸管理器";
    private const string Version = "1.0.0";
    private const string RunValue = "BaiAcha";
    private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\BaiAcha";
    private const string LegacyProduct = "Wallerpaper";

    private static readonly string[] AppProcesses =
    {
        "BaiAcha", "BaiAchaHost", "BaiAchaSceneHost", "BaiAchaVideoHost", "BaiAchaWebHost",
        "WallerpaperShell", "WallerpaperHost", "SceneWallpaperHost", "VideoWallpaperHost", "WebWallpaperHost"
    };

    private static string DefaultInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", ProductName);

    private static string LegacyInstallDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", LegacyProduct);

    [STAThread]
    private static int Main(string[] args)
    {
        var silent = Has(args, "--silent") || Has(args, "-s");
        _silent = silent;
        var uninstall = Has(args, "--uninstall");
        var noLaunch = Has(args, "--no-launch");
        var noShortcut = Has(args, "--no-shortcut");
        var autostart = !Has(args, "--no-autostart");
        var dirArg = args.FirstOrDefault(a => a.StartsWith("--install-dir=", StringComparison.OrdinalIgnoreCase));
        var installDir = dirArg is null ? DefaultInstallDir : dirArg["--install-dir=".Length..].Trim('"');

        if (uninstall)
            return Uninstall(installDir);

        if (silent)
        {
            var code = Install(installDir, new InstallOptions(autostart, !noShortcut,
                !noLaunch, Directory.Exists(LegacyInstallDir)));
            if (code != 0)
                Console.Error.WriteLine(ReadLog());
            return code;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new SetupForm(installDir, silent: false));
        return 0;
    }

    private static bool Has(string[] args, string name)
        => args.Any(a => a.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BaiAcha", "setup.log");
    private static bool _silent;

    internal static void Log(string message)
    {
        foreach (var path in new[]
                 {
                     LogPath,
                     Path.Combine(AppContext.BaseDirectory, "BaiAchaSetup.log")
                 })
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.AppendAllText(path,
                    $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
                return;
            }
            catch { }
        }
    }

    internal static string ReadLog()
    {
        foreach (var path in new[]
                 {
                     LogPath,
                     Path.Combine(AppContext.BaseDirectory, "BaiAchaSetup.log")
                 })
        {
            try { if (File.Exists(path)) return File.ReadAllText(path); } catch { }
        }
        return "(no log)";
    }

    internal static void Fail(string title, Exception ex)
    {
        Log(title + ": " + ex);
        if (_silent)
        {
            try { Console.Error.WriteLine(title + ": " + ex); } catch { }
            try
            {
                File.AppendAllText(
                    Path.Combine(AppContext.BaseDirectory, "BaiAchaSetup.error.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {title}: {ex}{Environment.NewLine}");
            }
            catch { }
            return;
        }
        MessageBox.Show($"{title}: {ex.Message}", ProductName, MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }

    internal sealed record InstallOptions(bool Autostart, bool Shortcuts, bool Launch, bool RemoveLegacy);

    internal static int Install(string installDir, InstallOptions options)
    {
        try
        {
            StopProcesses();
            // Stage inside the install directory itself: creating folders at a
            // drive root or in %TEMP% can be denied by security software, while
            // the install target is by definition writable.
            Directory.CreateDirectory(installDir);
            var staging = Path.Combine(installDir,
                ".staging-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(staging);
            try
            {
                using (var payload = Assembly.GetExecutingAssembly()
                           .GetManifestResourceStream("payload.zip")
                       ?? throw new InvalidOperationException(
                           "安装包缺少 payload.zip；请用 tools/build-release.ps1 重新打包。"))
                using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
                    zip.ExtractToDirectory(staging, overwriteFiles: true);

                Directory.CreateDirectory(installDir);
                foreach (var file in Directory.EnumerateFiles(staging, "*", SearchOption.AllDirectories))
                {
                    var rel = Path.GetRelativePath(staging, file);
                    var target = Path.Combine(installDir, rel);
                    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                    File.Copy(file, target, overwrite: true);
                }
            }
            finally
            {
                try { Directory.Delete(staging, recursive: true); } catch { }
            }

            var eula = ReadEula();
            File.WriteAllText(Path.Combine(installDir, "用户协议.txt"), eula);

            WriteUninstallInfo(installDir);
            if (options.Shortcuts)
            {
                CreateShortcut(Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.Programs), "BaiAcha.lnk"),
                    Path.Combine(installDir, "BaiAcha.exe"), installDir);
                CreateShortcut(Path.Combine(Environment.GetFolderPath(
                        Environment.SpecialFolder.DesktopDirectory), "BaiAcha.lnk"),
                    Path.Combine(installDir, "BaiAcha.exe"), installDir);
            }

            var exe = Path.Combine(installDir, "BaiAcha.exe");
            if (options.Autostart && File.Exists(exe))
            {
                using var run = Registry.CurrentUser.CreateSubKey(
                    @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
                run.SetValue(RunValue, $"\"{exe}\" --minimized");
                run.DeleteValue("WallerpaperShell", throwOnMissingValue: false);
            }

            if (options.RemoveLegacy && Directory.Exists(LegacyInstallDir))
                TryDeleteTree(LegacyInstallDir);

            if (options.Launch && File.Exists(exe))
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = exe,
                    WorkingDirectory = installDir,
                    UseShellExecute = true
                });
            }

            return 0;
        }
        catch (Exception ex)
        {
            Fail("安装失败", ex);
            return 1;
        }
    }

    internal static int Uninstall(string installDir)
    {
        try
        {
            StopProcesses();
            using (var run = Registry.CurrentUser.OpenSubKey(
                       @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true))
            {
                run?.DeleteValue(RunValue, throwOnMissingValue: false);
                run?.DeleteValue("WallerpaperShell", throwOnMissingValue: false);
            }
            Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, throwOnMissingSubKey: false);
            TryDelete(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.Programs), "BaiAcha.lnk"));
            TryDelete(Path.Combine(Environment.GetFolderPath(
                Environment.SpecialFolder.DesktopDirectory), "BaiAcha.lnk"));
            TryDeleteTree(installDir);
            return 0;
        }
        catch (Exception ex)
        {
            Fail("卸载失败", ex);
            return 1;
        }
    }

    internal static string ReadEula()
    {
        try
        {
            using var stream = Assembly.GetExecutingAssembly()
                .GetManifestResourceStream("EULA.txt");
            if (stream is null) return "（安装包内未包含用户协议）";
            using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
            return reader.ReadToEnd();
        }
        catch
        {
            return "（读取用户协议失败）";
        }
    }

    internal static string InstalledVersion => Version;

    private static void WriteUninstallInfo(string installDir)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKey, writable: true);
        if (key is null) return;
        key.SetValue("DisplayName", DisplayName);
        key.SetValue("DisplayVersion", Version);
        key.SetValue("InstallLocation", installDir);
        key.SetValue("Publisher", ProductName);
        key.SetValue("UninstallString", $"\"{Path.Combine(installDir, "BaiAchaSetup.exe")}\" --uninstall");
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    internal static void StopProcesses()
    {
        foreach (var name in AppProcesses)
        {
            foreach (var p in Process.GetProcessesByName(name))
            {
                try
                {
                    p.CloseMainWindow();
                    if (!p.WaitForExit(1500))
                        p.Kill(entireProcessTree: true);
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
    }

    private static void CreateShortcut(string linkPath, string target, string workDir)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(linkPath)!);
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return;
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(linkPath);
            shortcut.TargetPath = target;
            shortcut.WorkingDirectory = workDir;
            shortcut.IconLocation = target + ",0";
            shortcut.Description = DisplayName;
            shortcut.Save();
            Marshal.ReleaseComObject(shortcut);
            Marshal.ReleaseComObject(shell);
        }
        catch
        {
            // Shortcuts are a convenience; never fail the install over them.
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    internal static void TryDeleteTree(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}

internal sealed class SetupForm : Form
{
    private readonly TextBox _license = new();
    private readonly CheckBox _agree = new();
    private readonly TextBox _path = new();
    private readonly CheckBox _autostart = new();
    private readonly CheckBox _shortcut = new();
    private readonly CheckBox _launch = new();
    private readonly CheckBox _removeLegacy = new();
    private readonly Button _install = new();
    private readonly Button _cancel = new();
    private readonly ProgressBar _progress = new();
    private readonly Label _status = new();

    public SetupForm(string installDir, bool silent)
    {
        Text = $"BaiAcha {Program.InstalledVersion} 安装程序";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(640, 620);
        Font = new Font("Segoe UI", 9f);
        _ = silent;

        var title = new Label
        {
            Text = "BaiAcha 动态壁纸管理器",
            Font = new Font("Segoe UI", 16f, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 16)
        };
        var subtitle = new Label
        {
            Text = $"版本 {Program.InstalledVersion} · 安装前请阅读并同意用户协议",
            AutoSize = true,
            ForeColor = Color.DimGray,
            Location = new Point(22, 52)
        };

        _license.Multiline = true;
        _license.ReadOnly = true;
        _license.ScrollBars = ScrollBars.Vertical;
        _license.Location = new Point(22, 80);
        _license.Size = new Size(596, 300);
        _license.Text = Program.ReadEula();

        _agree.Text = "我已阅读并同意《BaiAcha 用户协议》";
        _agree.AutoSize = true;
        _agree.Location = new Point(22, 392);
        _agree.CheckedChanged += (_, _) => UpdateEnabled();

        var pathLabel = new Label
        {
            Text = "安装位置",
            AutoSize = true,
            Location = new Point(22, 424)
        };
        _path.Location = new Point(22, 446);
        _path.Size = new Size(500, 26);
        _path.Text = installDir;
        var browse = new Button
        {
            Text = "浏览…",
            Location = new Point(530, 445),
            Size = new Size(88, 28)
        };
        browse.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog
            {
                Description = "选择 BaiAcha 的安装位置",
                SelectedPath = Directory.Exists(_path.Text) ? _path.Text : installDir
            };
            if (dialog.ShowDialog(this) == DialogResult.OK)
                _path.Text = dialog.SelectedPath;
        };

        _autostart.Text = "开机自动启动（--minimized 静默进托盘）";
        _autostart.AutoSize = true;
        _autostart.Checked = true;
        _autostart.Location = new Point(22, 484);

        _shortcut.Text = "创建桌面与开始菜单快捷方式";
        _shortcut.AutoSize = true;
        _shortcut.Checked = true;
        _shortcut.Location = new Point(22, 508);

        _launch.Text = "安装完成后运行 BaiAcha";
        _launch.AutoSize = true;
        _launch.Checked = true;
        _launch.Location = new Point(22, 532);

        _removeLegacy.Text = "删除旧版 Wallerpaper 安装目录与自启动项";
        _removeLegacy.AutoSize = true;
        _removeLegacy.Checked = Directory.Exists(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "Wallerpaper"));
        _removeLegacy.Location = new Point(22, 556);

        _progress.Location = new Point(22, 586);
        _progress.Size = new Size(440, 12);

        _status.Text = "等待安装";
        _status.AutoSize = true;
        _status.ForeColor = Color.DimGray;
        _status.Location = new Point(22, 600);

        _install.Text = "安装";
        _install.Location = new Point(470, 580);
        _install.Size = new Size(72, 30);
        _install.Click += (_, _) => RunInstall();

        _cancel.Text = "取消";
        _cancel.Location = new Point(548, 580);
        _cancel.Size = new Size(72, 30);
        _cancel.Click += (_, _) => Close();

        Controls.AddRange(new Control[]
        {
            title, subtitle, _license, _agree, pathLabel, _path, browse,
            _autostart, _shortcut, _launch, _removeLegacy, _progress, _status,
            _install, _cancel
        });
        UpdateEnabled();
    }

    private void UpdateEnabled() => _install.Enabled = _agree.Checked && !string.IsNullOrWhiteSpace(_path.Text);

    private async void RunInstall()
    {
        var dir = _path.Text.Trim();
        if (string.IsNullOrWhiteSpace(dir)) return;
        if (Directory.Exists(Path.Combine(dir, "BaiAcha.exe")))
        {
            var overwrite = MessageBox.Show(this,
                "该目录已存在 BaiAcha 安装，是否覆盖安装？", "BaiAcha",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (overwrite != DialogResult.Yes) return;
        }

        _install.Enabled = false;
        _cancel.Enabled = false;
        _progress.Style = ProgressBarStyle.Marquee;
        _status.Text = "正在安装…";
        var options = new Program.InstallOptions(_autostart.Checked, _shortcut.Checked,
            _launch.Checked, _removeLegacy.Checked);
        var code = await Task.Run(() => Program.Install(dir, options));
        _progress.Style = ProgressBarStyle.Continuous;
        _progress.Value = 100;
        if (code == 0)
        {
            _status.Text = "安装完成";
            MessageBox.Show(this, $"BaiAcha {Program.InstalledVersion} 已安装到：\n{dir}",
                "BaiAcha", MessageBoxButtons.OK, MessageBoxIcon.Information);
            Close();
        }
        else
        {
            _status.Text = "安装失败，详见错误提示";
            _cancel.Enabled = true;
        }
    }
}
