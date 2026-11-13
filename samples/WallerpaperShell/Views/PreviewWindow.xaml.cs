using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices;
using Wallerpaper.Shell.Models;
using Windows.Graphics;
using Windows.Media.Core;

namespace Wallerpaper.Shell.Views;

public sealed partial class PreviewWindow : Window
{
    private readonly WallpaperItem _item;

    public PreviewWindow(WallpaperItem item)
    {
        InitializeComponent();
        _item = item;
        Title = $"预览 · {item.Title}";
        TitleText.Text = item.Title;
        AppWindow.Resize(new SizeInt32(760, 480));

        if (item.Kind == WallpaperKind.Video && File.Exists(item.Path))
        {
            Player.Visibility = Visibility.Visible;
            PreviewImage.Visibility = Visibility.Collapsed;
            Player.Source = MediaSource.CreateFromUri(new Uri(item.Path));
            HintText.Text = "视频预览（独立窗口，不影响桌面）";
        }
        else
        {
            Player.Visibility = Visibility.Collapsed;
            PreviewImage.Visibility = Visibility.Visible;
            if (!string.IsNullOrWhiteSpace(item.ThumbnailPath) && File.Exists(item.ThumbnailPath))
                PreviewImage.Source = new BitmapImage(new Uri(item.ThumbnailPath));
            HintText.Text = item.Kind == WallpaperKind.Scene
                ? "场景静态预览（缩略图）"
                : "Web 静态预览（缩略图）";
        }

        AppWindow.Closing += (_, _) => Player.Source = null;
    }

    public void BringToFront()
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,
            0x0001 | 0x0002 | 0x0040); // NOSIZE | NOMOVE | SHOWWINDOW
        Activate();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Player.Source = null;
        Close();
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
        int x, int y, int width, int height, uint flags);
}
