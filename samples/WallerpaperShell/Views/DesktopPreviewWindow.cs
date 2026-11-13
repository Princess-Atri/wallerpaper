using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;
using Wallerpaper.Shell.Models;

namespace Wallerpaper.Shell.Views;

/// <summary>
/// Full-screen desktop-context preview. A screenshot of the real desktop is
/// the backdrop, and the selected video plays over it. Closing returns to the
/// main window without changing the live wallpaper.
/// </summary>
public sealed class DesktopPreviewWindow : Window
{
    private readonly WallpaperItem _item;
    private readonly Action _onClosed;
    private readonly MediaPlayer? _player;
    private readonly MediaPlayerElement? _playerElement;
    private bool _showingSecond;

    public DesktopPreviewWindow(WallpaperItem item, string desktopScreenshot,
        Action onClosed)
    {
        _item = item;
        _onClosed = onClosed;
        Title = "桌面全屏预览 · " + item.Title;

        var root = new Grid { Background = new SolidColorBrush(Colors.Black) };
        root.Children.Add(new Image
        {
            Source = new BitmapImage(new Uri(desktopScreenshot)),
            Stretch = Stretch.UniformToFill
        });

        if (item.Kind is WallpaperKind.Video or WallpaperKind.Sequence &&
            File.Exists(item.Path))
        {
            _player = new MediaPlayer
            {
                IsLoopingEnabled = item.Kind != WallpaperKind.Sequence,
                IsMuted = true,
                AutoPlay = true
            };
            _playerElement = new MediaPlayerElement
            {
                Stretch = item.Fit == "contain" ? Stretch.Uniform : Stretch.UniformToFill,
                AreTransportControlsEnabled = false,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Stretch
            };
            _playerElement.SetMediaPlayer(_player);
            _player.Source = MediaSource.CreateFromUri(new Uri(item.Path));
            _player.MediaEnded += OnMediaEnded;
            root.Children.Add(_playerElement);
        }
        else
        {
            var image = new Image
            {
                Stretch = Stretch.UniformToFill
            };
            if (!string.IsNullOrWhiteSpace(item.ThumbnailPath) &&
                File.Exists(item.ThumbnailPath))
                image.Source = new BitmapImage(new Uri(item.ThumbnailPath));
            else if (item.Thumbnail is not null)
                image.Source = item.Thumbnail;
            root.Children.Add(image);
        }

        var header = new Grid
        {
            Margin = new Thickness(22, 18, 22, 0),
            VerticalAlignment = VerticalAlignment.Top,
            ColumnDefinitions =
            {
                new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                new ColumnDefinition { Width = GridLength.Auto }
            }
        };
        var title = new Border
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xB8, 0, 0, 0)),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(16, 9, 16, 9),
            HorizontalAlignment = HorizontalAlignment.Left,
            Child = new TextBlock
            {
                Text = "桌面全屏预览 · " + item.Title,
                Foreground = new SolidColorBrush(Colors.White),
                FontSize = 15
            }
        };
        var close = new Button
        {
            Content = "退出全屏预览",
            Padding = new Thickness(16, 7, 16, 7),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0xD0, 20, 20, 20)),
            Foreground = new SolidColorBrush(Colors.White)
        };
        close.Click += (_, _) => Close();
        Grid.SetColumn(close, 1);
        header.Children.Add(title);
        header.Children.Add(close);
        root.Children.Add(header);

        Content = root;
        AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
        Closed += (_, _) =>
        {
            var player = _player;
            try
            {
                if (player is not null)
                {
                    player.MediaEnded -= OnMediaEnded;
                    player.Pause();
                    player.Source = null;
                }
            }
            catch { }

            // Do not dispose a 4K MediaPlayer synchronously on the UI thread
            // inside Closed; that can block teardown for seconds. Restore the
            // main window first, then release the player on the thread pool.
            _onClosed();
            if (player is not null)
            {
                _ = Task.Run(async () =>
                {
                    await Task.Delay(250);
                    try { player.Dispose(); } catch { }
                });
            }
        };
        root.KeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                Close();
                e.Handled = true;
            }
        };
        root.IsTabStop = true;
        root.Loaded += (_, _) => root.Focus(FocusState.Programmatic);
    }

    private void OnMediaEnded(MediaPlayer sender, object args)
    {
        if (_item.Kind == WallpaperKind.Sequence && !_showingSecond &&
            File.Exists(_item.SecondPath))
        {
            _showingSecond = true;
            DispatcherQueue.TryEnqueue(() =>
            {
                _player.Source = MediaSource.CreateFromUri(new Uri(_item.SecondPath));
                _player.Play();
            });
            return;
        }
        DispatcherQueue.TryEnqueue(() =>
        {
            _player.Position = TimeSpan.Zero;
            _player.Play();
        });
    }
}
