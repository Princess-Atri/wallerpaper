using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Wallerpaper.Diagnostics;
using Wallerpaper.Shell.Models;
using Wallerpaper.Shell.Services;

namespace Wallerpaper.Shell.Views;

public sealed partial class SettingsView : UserControl
{
    private ShellConfig? _config;
    private WallpaperCatalog? _catalog;
    private ThumbnailService? _thumbnails;
    private readonly Dictionary<string, ComboBox> _monitorCombos = new();

    public SettingsView()
    {
        InitializeComponent();
    }

    public event EventHandler<ShellConfig>? Saved;
    public event EventHandler? LogViewerRequested;

    public void Configure(ShellConfig config, WallpaperCatalog catalog, ThumbnailService thumbnails)
    {
        _config = config;
        _catalog = catalog;
        _thumbnails = thumbnails;
        LoadValues();
    }

    /// <summary>Kept for callers that used to feed the settings preview card.</summary>
    public void SetPreview(WallpaperItem? item)
    {
    }

    private void LoadValues()
    {
        if (_config is null) return;

        PolicyCombo.SelectedIndex = _config.Performance.Policy switch
        {
            "continue" => 0,
            "stop" => 2,
            _ => 1
        };
        MaximizedToggle.IsOn = _config.Performance.TriggerOnMaximized;
        AcFpsBox.Value = _config.Performance.AcFps;
        BatteryFpsBox.Value = _config.Performance.BatteryFps;
        TextureMemoryBox.Value = _config.Performance.MaxTextureMemoryMb;
        ParticlesBox.Value = _config.Performance.MaxParticles;
        RenderScaleSlider.Value = _config.Performance.RenderScale;
        RenderScaleValue.Text = $"{_config.Performance.RenderScale:F2}×";

        var registryAutostart = false;
        try { registryAutostart = AutostartService.IsEnabled(); } catch { }
        AutostartToggle.IsOn = _config.General.StartWithWindows || registryAutostart;
        MinimizeToggle.IsOn = _config.General.MinimizeToTray;
        foreach (ComboBoxItem item in DeleteBehaviorCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), _config.Playback.DeleteCurrentBehavior,
                    StringComparison.OrdinalIgnoreCase))
            {
                DeleteBehaviorCombo.SelectedItem = item;
                break;
            }
        }

        foreach (ComboBoxItem item in LockScreenCombo.Items.OfType<ComboBoxItem>())
        {
            if (string.Equals(item.Tag?.ToString(), _config.General.LockScreenMode,
                    StringComparison.OrdinalIgnoreCase))
            {
                LockScreenCombo.SelectedItem = item;
                break;
            }
        }
        if (LockScreenCombo.SelectedItem is null)
            LockScreenCombo.SelectedIndex = 0;

        CacheDirectoryBox.Text = _config.Cache.Directory;
        CacheSizeBox.Value = _config.Cache.MaxSizeMb;
        ThumbWidthBox.Value = _config.Cache.ThumbnailWidth;
        ThumbHeightBox.Value = _config.Cache.ThumbnailHeight;
        AutoCleanDaysBox.Value = _config.Cache.AutoCleanDays;

        RebuildMonitorAssignments();
        var selected = _catalog?.Items.FirstOrDefault();
        SetPreview(selected);
    }

    private void RebuildMonitorAssignments()
    {
        MonitorAssignmentsPanel.Children.Clear();
        _monitorCombos.Clear();
        if (_config is null || _catalog is null) return;

        var monitors = MonitorService.Enumerate();
        foreach (var monitor in monitors)
        {
            var row = new Grid { ColumnSpacing = 12 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.4, GridUnitType.Star) });

            row.Children.Add(new TextBlock
            {
                Text = monitor.Label,
                VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap
            });

            var combo = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            combo.Items.Add("（跟随当前壁纸）");
            foreach (var item in _catalog.Items)
                combo.Items.Add($"{item.Title}  [{item.Kind}]");
            combo.SelectedIndex = 0;

            if (_config.Monitors.TryGetValue(monitor.Device, out var assignment))
            {
                var index = _catalog.Items.ToList().FindIndex(x => x.Id == assignment.WallpaperId);
                if (index >= 0) combo.SelectedIndex = index + 1;
            }

            Grid.SetColumn(combo, 1);
            row.Children.Add(combo);
            MonitorAssignmentsPanel.Children.Add(row);
            _monitorCombos[monitor.Device] = combo;
        }

        if (monitors.Count == 0)
            MonitorAssignmentsPanel.Children.Add(new TextBlock { Text = "未检测到显示器。" });
    }

    public async Task ShowLongVideoSuperResolutionAsync()
    {
        if (_config is null || _catalog is null || App.MainWindow is null)
            return;
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".webm");
            picker.FileTypeFilter.Add(".mkv");
            picker.FileTypeFilter.Add(".mov");
            InitializeWithWindow.Initialize(picker,
                WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null)
                return;

            var factorBox = new ComboBox { SelectedIndex = 0, Header = "超分倍数" };
            factorBox.Items.Add("2×");
            factorBox.Items.Add("4×");
            var factorResult = await new ContentDialog
            {
                Title = "长视频超分辨率",
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        (UIElement)new TextBlock
                        {
                            Text = file.Name,
                            TextWrapping = TextWrapping.Wrap
                        },
                        (UIElement)factorBox,
                        (UIElement)new TextBlock
                        {
                            Text = "长视频会逐帧提取并超分，可能运行数小时。",
                            FontSize = 12,
                            TextWrapping = TextWrapping.Wrap
                        }
                    }
                },
                PrimaryButtonText = "开始",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = XamlRoot
            }.ShowAsync();
            if (factorResult != ContentDialogResult.Primary)
                return;

            var factor = factorBox.SelectedIndex == 1 ? 4 : 2;
            var status = new TextBlock { Text = "准备…", TextWrapping = TextWrapping.Wrap };
            var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
            var progressDialog = new ContentDialog
            {
                Title = $"超分辨率 · {factor}×",
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children = { (UIElement)status, (UIElement)bar }
                },
                CloseButtonText = "取消",
                XamlRoot = XamlRoot
            };
            var cts = new CancellationTokenSource();
            progressDialog.CloseButtonClick += (_, _) => cts.Cancel();
            _ = progressDialog.ShowAsync();
            try
            {
                var result = await new SuperResolutionService().UpscaleAsync(
                    file.Path, 30, factor, "realesr-animevideov3",
                    (percent, message) => DispatcherQueue.TryEnqueue(() =>
                    {
                        bar.Value = Math.Clamp(percent, 0, 100);
                        status.Text = message;
                    }), cts.Token);
                progressDialog.Hide();
                _catalog.AddPath(result.OutputPath, WallpaperKind.Video);
                ConfigStore.Save(_config);
                await new ContentDialog
                {
                    Title = "超分辨率完成",
                    Content = result.OutputPath,
                    CloseButtonText = "关闭",
                    XamlRoot = XamlRoot
                }.ShowAsync();
                Saved?.Invoke(this, _config);
            }
            catch (OperationCanceledException)
            {
                progressDialog.Hide();
            }
            catch (Exception ex)
            {
                progressDialog.Hide();
                AppLog.Error("long video super resolution failed", ex);
                await new ContentDialog
                {
                    Title = "超分辨率失败",
                    Content = ex.Message,
                    CloseButtonText = "关闭",
                    XamlRoot = XamlRoot
                }.ShowAsync();
            }
        }
        catch (Exception ex)
        {
            await new ContentDialog
            {
                Title = "超分辨率失败",
                Content = ex.Message,
                CloseButtonText = "关闭",
                XamlRoot = XamlRoot
            }.ShowAsync();
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null) return;

        _config.Performance.Policy = (PolicyCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "pause";
        _config.Performance.TriggerOnMaximized = MaximizedToggle.IsOn;
        _config.Performance.AcFps = Int(AcFpsBox.Value, 60);
        _config.Performance.BatteryFps = Int(BatteryFpsBox.Value, 30);
        _config.Performance.MaxTextureMemoryMb = Int(TextureMemoryBox.Value, 512);
        _config.Performance.MaxParticles = Int(ParticlesBox.Value, 1024);
        _config.Performance.RenderScale = Math.Clamp(RenderScaleSlider.Value, 0.5, 2);

        _config.General.StartWithWindows = AutostartToggle.IsOn;
        _config.General.MinimizeToTray = MinimizeToggle.IsOn;
        _config.Playback.DeleteCurrentBehavior =
            (DeleteBehaviorCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? "previous";
        _config.General.LockScreenMode =
            (LockScreenCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString()
            ?? "off";

        _config.Cache.Directory = CacheDirectoryBox.Text.Trim();
        _config.Cache.MaxSizeMb = Int(CacheSizeBox.Value, 2048);
        _config.Cache.ThumbnailWidth = Int(ThumbWidthBox.Value, 480);
        _config.Cache.ThumbnailHeight = Int(ThumbHeightBox.Value, 270);
        _config.Cache.AutoCleanDays = Int(AutoCleanDaysBox.Value, 14);

        try
        {
            AutostartService.SetEnabled(AutostartToggle.IsOn);
        }
        catch (Exception ex)
        {
            CacheDirectoryBox.Header = $"自启动写入失败：{ex.Message}";
        }

        if (_catalog is not null)
        {
            var items = _catalog.Items.ToList();
            foreach (var (device, combo) in _monitorCombos)
            {
                var index = combo.SelectedIndex - 1;
                if (index >= 0 && index < items.Count)
                {
                    _config.Monitors[device] = new MonitorAssignment
                    {
                        WallpaperId = items[index].Id,
                        Fps = _config.Performance.AcFps,
                        Enabled = true
                    };
                }
                else
                {
                    _config.Monitors.Remove(device);
                }
            }
        }

        Saved?.Invoke(this, _config);
        StatusText.Text = DescribeEffect(_config);
    }

    /// <summary>Tells the user which settings took effect now and which need
    /// a renderer restart, so nothing silently "does nothing".</summary>
    private static string DescribeEffect(ShellConfig config)
    {
        var immediate = new List<string>();
        var restart = new List<string>();

        immediate.Add("自启动、最小化到托盘、播放规则");
        immediate.Add("自动清理天数、缩略图尺寸");
        restart.Add("性能策略、交流电/电池帧率");
        restart.Add("渲染缩放、最大粒子数、贴图显存上限");

        var text = "已保存。立即生效：" + string.Join("、", immediate)
            + "。重启调度器后生效：" + string.Join("、", restart) + "。";
        if (string.Equals(config.General.LockScreenMode, "match",
                StringComparison.OrdinalIgnoreCase))
            text += " 锁屏壁纸会在下次应用壁纸时同步。";
        return text;
    }

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null) return;
        PolicyCombo.SelectedIndex = 1;
        MaximizedToggle.IsOn = false;
        AcFpsBox.Value = 60;
        BatteryFpsBox.Value = 30;
        TextureMemoryBox.Value = 512;
        ParticlesBox.Value = 1024;
        RenderScaleSlider.Value = 1;
        AutostartToggle.IsOn = false;
        MinimizeToggle.IsOn = true;
        CacheSizeBox.Value = 2048;
        ThumbWidthBox.Value = 480;
        ThumbHeightBox.Value = 270;
        AutoCleanDaysBox.Value = 14;
    }

    private void OpenLogViewer_Click(object sender, RoutedEventArgs e)
    {
        LogViewerRequested?.Invoke(this, EventArgs.Empty);
    }

    private async void CleanupNow_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null) return;
        var days = Int(AutoCleanDaysBox.Value, 14);
        StatusText.Text = "正在清理…";
        try
        {
            var result = await Task.Run(() =>
                CacheCleanupService.Cleanup(_config, Math.Max(1, days)));
            RefreshCacheUsage();
            StatusText.Text =
                $"清理完成：删除 {result.DeletedFiles} 个文件，" +
                $"释放 {result.FreedBytes / 1048576.0:F0} MB，" +
                $"保留 {result.KeptInUse} 个在用文件。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "清理失败：" + ex.Message;
        }
    }

    private void RefreshCacheUsage()
    {
        try
        {
            var (files, bytes) = CacheCleanupService.Measure();
            var (aFiles, aBytes) = CacheCleanupService.MeasureArtifacts();
            var (dFiles, dBytes) = CacheCleanupService.MeasureCrashDumps();
            var text = $"当前缓存：{files} 个文件，{bytes / 1048576.0:F0} MB";
            if (aFiles > 0)
                text += $"\r\n开发产物 artifacts：{aFiles} 个文件，{aBytes / 1048576.0:F0} MB";
            if (dFiles > 0)
                text += $"\r\n崩溃转储：{dFiles} 个文件，{dBytes / 1048576.0:F0} MB（保留最新 1 个）";
            CacheUsageText.Text = text;
        }
        catch
        {
            CacheUsageText.Text = "";
        }
    }

    private void OpenArtifacts_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = AppStorage.ArtifactsRoot;
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
                { UseShellExecute = true });
        }
        catch { }
    }

    private void RenderScale_Changed(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (RenderScaleValue is not null)
            RenderScaleValue.Text = $"{e.NewValue:F2}×";
    }

    private void OpenCache_Click(object sender, RoutedEventArgs e)
    {
        var dir = CacheDirectoryBox.Text.Trim();
        if (!Directory.Exists(dir))
            Directory.CreateDirectory(dir);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
    }

    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        _thumbnails?.Clear();
        StatusText.Text = "缩略图缓存已清空，返回主界面刷新即可重新生成。";
    }

    private static int Int(double value, int fallback) =>
        double.IsNaN(value) ? fallback : (int)Math.Round(value);
}
