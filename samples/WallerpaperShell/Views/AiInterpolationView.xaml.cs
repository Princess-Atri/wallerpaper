using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Wallerpaper.Diagnostics;
using Wallerpaper.Shell.Models;
using Wallerpaper.Shell.Services;

namespace Wallerpaper.Shell.Views;

public sealed partial class AiInterpolationView : UserControl
{
    private ShellConfig? _config;
    private WallpaperCatalog? _catalog;
    private string? _selectedPath;
    private CancellationTokenSource? _cts;

    public event EventHandler? LibraryChanged;

    public AiInterpolationView()
    {
        InitializeComponent();
    }

    public void Configure(ShellConfig config, WallpaperCatalog catalog)
    {
        _config = config;
        _catalog = catalog;
        RefreshOutputUi();
    }

    private void RefreshOutputUi()
    {
        var dir = _config?.Cache.OutputDirectory ?? "";
        OutputDirText.Text = dir;
        OutputHintText.Text = string.IsNullOrWhiteSpace(dir)
            ? "默认：先渲染到缓存，完成后复制到源视频所在目录。"
            : "结果会直接写入该目录（不存在会自动创建）。";
    }

    private async void PickOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null) return;
        try
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            if (App.MainWindow is null) return;
            InitializeWithWindow.Initialize(picker,
                WindowNative.GetWindowHandle(App.MainWindow));
            var folder = await picker.PickSingleFolderAsync();
            if (folder is null || string.IsNullOrWhiteSpace(folder.Path)) return;
            _config.Cache.OutputDirectory = folder.Path;
            ConfigStore.Save(_config);
            RefreshOutputUi();
            StatusText.Text = "输出目录已设为：" + folder.Path;
        }
        catch (Exception ex)
        {
            StatusText.Text = "选择输出目录失败：" + ex.Message;
        }
    }

    private void ResetOutput_Click(object sender, RoutedEventArgs e)
    {
        if (_config is null) return;
        _config.Cache.OutputDirectory = "";
        ConfigStore.Save(_config);
        RefreshOutputUi();
        StatusText.Text = "输出目录已恢复默认（源视频同目录）。";
    }

    private async void Choose_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add(".mp4");
            picker.FileTypeFilter.Add(".webm");
            picker.FileTypeFilter.Add(".mkv");
            picker.FileTypeFilter.Add(".mov");
            if (App.MainWindow is null) return;
            InitializeWithWindow.Initialize(picker,
                WindowNative.GetWindowHandle(App.MainWindow));
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            _selectedPath = file.Path;
            SelectedFileText.Text = file.Name;
            StartButton.IsEnabled = true;
            StatusText.Text = "已选择视频，可以开始补帧。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "选择视频失败：" + ex.Message;
        }
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPath is null || !File.Exists(_selectedPath) ||
            _config is null || _catalog is null)
        {
            StatusText.Text = "请先选择一个视频。";
            return;
        }
        if (!StartButton.IsEnabled) return;

        var factor = int.TryParse(
            ((ComboBoxItem)FactorCombo.SelectedItem).Tag?.ToString(), out var f) ? f : 2;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        ChooseButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ProgressBar.Value = 0;
        StatusText.Text = "准备补帧…";

        try
        {
            var service = new AiInterpolationService();
            var targetDir = string.IsNullOrWhiteSpace(_config.Cache.OutputDirectory)
                ? null
                : _config.Cache.OutputDirectory;
            var result = await service.InterpolateAsync(
                _selectedPath, 30, factor,
                (percent, message) => DispatcherQueue.TryEnqueue(() =>
                {
                    ProgressBar.Value = Math.Clamp(percent, 0, 100);
                    StatusText.Text = message;
                }), _cts.Token, targetDir);

            LibraryChanged?.Invoke(this, EventArgs.Empty);
            if (targetDir is not null)
            {
                StatusText.Text = "完成，已输出到：" + result.OutputPath;
            }
            else
            {
                var exported = AppStorage.ExportBesideSource(result.OutputPath,
                    _selectedPath, $"_AI{factor}x");
                StatusText.Text = "完成，已输出到原视频目录：" + exported;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            AppLog.Error("AI interpolation page failed", ex);
            StatusText.Text = "失败：" + ex.Message;
        }
        finally
        {
            StartButton.IsEnabled = _selectedPath is not null;
            ChooseButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "正在取消…";
    }
}
