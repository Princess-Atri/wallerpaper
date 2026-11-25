using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage.Pickers;
using WinRT.Interop;
using Wallerpaper.Diagnostics;
using Wallerpaper.Shell.Models;
using Wallerpaper.Shell.Services;

namespace Wallerpaper.Shell.Views;

public sealed partial class SuperResolutionView : UserControl
{
    private ShellConfig? _config;
    private WallpaperCatalog? _catalog;
    private string? _selectedPath;
    private CancellationTokenSource? _cts;

    public event EventHandler? LibraryChanged;

    public SuperResolutionView()
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
            StatusText.Text = "已选择视频，可以开始超分。";
        }
        catch (Exception ex)
        {
            StatusText.Text = "选择视频失败：" + ex.Message;
        }
    }

    private void FastMode_Changed(object sender, RoutedEventArgs e)
    {
        if (ModelCombo is null) return;
        var fast = FastModeCheck.IsChecked == true;
        ModelCombo.IsEnabled = !fast;
        FastModeHint.Text = fast
            ? "将使用 GPU 硬件编码（h264_mf / NVENC），几秒出片；不会像 AI 那样猜出细节。"
            : "勾选后使用 降噪 → lanczos 放大 → CAS 锐化 + 硬件编码，几秒出片；不会像 AI 那样「猜」出新细节。";
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPath is null || !File.Exists(_selectedPath) ||
            _config is null || _catalog is null)
        {
            StatusText.Text = "请先选择一个视频。";
            return;
        }
        if (StartButton.IsEnabled == false) return;

        var factor = (int)(((ComboBoxItem)FactorCombo.SelectedItem).Tag);
        var model = ((ComboBoxItem)ModelCombo.SelectedItem).Tag?.ToString()
                    ?? "realesrgan-x4plus";
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        StartButton.IsEnabled = false;
        ChooseButton.IsEnabled = false;
        CancelButton.IsEnabled = true;
        ProgressBar.IsIndeterminate = false;
        ProgressBar.Value = 0;
        StatusText.Text = "准备超分…";

        try
        {
            var targetDir = string.IsNullOrWhiteSpace(_config.Cache.OutputDirectory)
                ? null
                : _config.Cache.OutputDirectory;

            string outputPath;
            if (FastModeCheck.IsChecked == true)
            {
                var fastService = new FastUpscaleService();
                var fastResult = await fastService.UpscaleAsync(
                    _selectedPath, factor, targetDir,
                    (percent, message) => DispatcherQueue.TryEnqueue(() =>
                    {
                        ProgressBar.Value = Math.Clamp(percent, 0, 100);
                        StatusText.Text = message;
                    }), _cts.Token);
                outputPath = fastResult.OutputPath;
            }
            else
            {
                var service = new SuperResolutionService();
                var eta = new ProgressEta();
                var result = await service.UpscaleAsync(
                    _selectedPath, 30, factor, model,
                    (percent, message) => DispatcherQueue.TryEnqueue(() =>
                    {
                        ProgressBar.Value = Math.Clamp(percent, 0, 100);
                        StatusText.Text = eta.Format(percent, message);
                    }), _cts.Token, targetDir);
                outputPath = result.OutputPath;
            }

            LibraryChanged?.Invoke(this, EventArgs.Empty);
            if (targetDir is not null)
            {
                StatusText.Text = "完成，已输出到：" + outputPath;
            }
            else
            {
                var exported = AppStorage.ExportBesideSource(outputPath,
                    _selectedPath, $"_SR{factor}x");
                StatusText.Text = "完成，已输出到原视频目录：" + exported;
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = "已取消。";
        }
        catch (Exception ex)
        {
            AppLog.Error("super resolution page failed", ex);
            StatusText.Text = "失败：" + ex.Message;
        }
        finally
        {
            StartButton.IsEnabled = _selectedPath is not null;
            ChooseButton.IsEnabled = true;
            CancelButton.IsEnabled = false;
            ProgressBar.IsIndeterminate = false;
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        StatusText.Text = "正在取消…";
    }
}
