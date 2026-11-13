using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Wallerpaper.Diagnostics;
using Wallerpaper.Shell.Models;
using Wallerpaper.Shell.Services;
using Wallerpaper.Shell.Views;
using WinRT;
using WinRT.Interop;
using WinRT.WallerpaperShellVtableClasses;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.UI;

namespace Wallerpaper.Shell;

public sealed partial class MainWindow : Window
{
	private readonly ShellConfig _config;

	private readonly WallpaperCatalog _catalog;

	private readonly HostBridge _host;

	private readonly LocalVideoSearch _localVideos;

	private ThumbnailService _thumbnails;

	private TrayIcon? _tray;

	private WallpaperKind _kind;

	private WallpaperItem? _selected;
	private DateTime _cropEditorClosedAt;
	private bool _favoritesOnly;
	private readonly Dictionary<NavigationViewItem, MenuFlyout> _navMenus = new();
	private MenuFlyout? _navOpenMenu;
	private Popup? _navFilterPopup;
	private NavigationViewItem? _navPinnedItem;
	private NavigationViewItem? _navHoverItem;
	private bool _navPointerInPopup;
	private Microsoft.UI.Dispatching.DispatcherQueueTimer? _navHideTimer;
	private string _navFilterTag = "Video";

	private readonly Dictionary<string, CancellationTokenSource> _fastSwitchDebounce = new(StringComparer.OrdinalIgnoreCase);

	private List<WallpaperItem> _visible = new List<WallpaperItem>();

	private List<MonitorDisplay> _monitors = new List<MonitorDisplay>();

	private bool _paused;

	private bool _exitRequested;

	private int _refreshToken;

	private DataTemplate? _defaultGridTemplate;

	private ItemsPanelTemplate? _defaultGridPanel;

	private MediaPlayer? _detailPlayer;

	private bool _fpsUi;

	private bool _fitUi;

	private bool _webZoomUi;

	private bool _sceneQualityUi;

	private CancellationTokenSource? _fpsProbeCts;

	public MainWindow(bool startMinimized = false)
	{
		InitializeComponent();
		_defaultGridTemplate = WallpaperGrid.ItemTemplate;
		_defaultGridPanel = (ItemsPanelTemplate)Root.Resources["DefaultGridPanel"];
		WallpaperGrid.ItemsPanel = _defaultGridPanel;
		// Enter on a toolbar button whose picker just closed was being
		// delivered again to the same button. These buttons remain fully
		// mouse-accessible; Enter is reserved for search fields.
		foreach (var toolbarButton in new[] { AddFileButton, AddFolderButton, AddSequenceButton })
		{
			toolbarButton.KeyDown += (_, e) =>
			{
				if (e.Key == Windows.System.VirtualKey.Enter)
					e.Handled = true;
			};
		}
		WallpaperGrid.PreviewKeyDown += (_, e) =>
		{
			if (e.Key is not (Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down) ||
			    _visible.Count == 0 || !IsSingleVisibleRow())
				return;
			var target = e.Key == Windows.System.VirtualKey.Up
				? _visible[0]
				: _visible[^1];
			WallpaperGrid.SelectedItem = target;
			WallpaperGrid.ScrollIntoView(target, ScrollIntoViewAlignment.Default);
			WallpaperGrid.Focus(FocusState.Programmatic);
			e.Handled = true;
		};
		WallpaperGrid.ContainerContentChanging += WallpaperGrid_ContainerContentChanging;
		SetupNavHoverEffects();
		_config = ConfigStore.Load();
		DesktopWallpaperService.Capture();
		_catalog = new WallpaperCatalog(_config);
		_localVideos = new LocalVideoSearch(_config);
		_thumbnails = new ThumbnailService(_config);
		_host = new HostBridge(_config, _catalog);
		_host.Log += OnHostLog;
		_host.FpsReported += OnFpsReported;

		// Start the renderer before laying out the full UI. Startup used to
		// return early for --minimized, which left the desktop without a
		// wallpaper until the user opened the window. This path is cheap and
		// makes the wallpaper appear as soon as the logon shell is ready.
		if (_config.Host.AutoStartHost && _catalog.Items.Count > 0)
			_host.Start();

		AppWindow.Resize(new SizeInt32(1360, 860));
		string text = Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
		if (File.Exists(text))
		{
			AppWindow.SetIcon(text);
		}
		SettingsViewControl.Configure(_config, _catalog, _thumbnails);
		SettingsViewControl.Saved += Settings_Saved;
		SuperResolutionViewControl.LibraryChanged += async (_, _) =>
		{
			await RefreshCatalogAsync();
			ApplyFilter();
		};
		_monitors = MonitorService.Enumerate();
		TargetMonitorCombo.Items.Clear();
		foreach (MonitorDisplay monitor in _monitors)
		{
			TargetMonitorCombo.Items.Add(monitor.Label);
		}
		if (_monitors.Count > 0)
		{
			TargetMonitorCombo.SelectedIndex = 0;
		}
		_tray = TryCreateTray();
		AppWindow.Closing += OnClosing;
		Closed += OnClosed;
		Nav.SelectedItem = Nav.MenuItems[0];
		RefreshCatalogAsync();
		if (startMinimized || _config.General.StartMinimized)
		{
			DispatcherQueue.TryEnqueue(HideToTray);
			return;
		}
		DispatcherQueue.TryEnqueue(() =>
		{
			if (_config.Host.AutoStartHost && _catalog.Items.Count > 0)
			{
				_host.Start();
			}
		});
	}

	private TrayIcon? TryCreateTray()
	{
		try
		{
			TrayIcon trayIcon = new TrayIcon(() => _catalog.Items.OrderBy((WallpaperItem x) => x.Title).ToList(), () => _paused, ShowFromTray, TogglePause, ShowSettings, ExitApp, SelectTrayWallpaper);
			trayIcon.SetPaused(_paused);
			return trayIcon;
		}
		catch (Exception ex)
		{
			SetStatus("托盘初始化失败：" + ex.Message);
			return null;
		}
	}

	private async Task RefreshCatalogAsync()
	{
		int token = ++_refreshToken;
		var scrollBefore = FindScrollViewer(WallpaperGrid);
		var scrollOffset = scrollBefore?.VerticalOffset ?? 0;
		int count = _catalog.Refresh();
		foreach (var assignment in _config.Monitors.Values)
		{
			var current = _catalog.Find(assignment.WallpaperId);
			if (current is not null) PlaybackHistoryService.Record(current);
		}
		try
		{
			_config.Wallpapers = _catalog.Items.ToList();
			ConfigStore.Save(_config);
		}
		catch (Exception ex)
		{
			AppLog.Error("catalog save failed", ex);
			SetStatus("保存壁纸目录失败：" + ex.Message);
		}
		ApplyFilter();
		if (scrollOffset > 0)
		{
			await Task.Delay(30);
			if (token == _refreshToken)
			{
				var scrollAfter = FindScrollViewer(WallpaperGrid);
				scrollAfter?.ChangeView(null,
					Math.Min(scrollOffset, scrollAfter.ScrollableHeight), null, true);
			}
		}
		foreach (WallpaperItem item in _visible.ToList())
		{
			string text = await _thumbnails.GetAsync(item);
			if (token == _refreshToken && text != null)
			{
				try
				{
					item.Thumbnail = new BitmapImage(new Uri(text));
				}
				catch
				{
				}
			}
		}
		SettingsViewControl.Configure(_config, _catalog, _thumbnails);
		SetStatus($"已载入 {count} 个壁纸");
		if (_config.Host.AutoStartHost && _catalog.Items.Count > 0 && !_host.IsRunning)
		{
			_host.Start();
		}
	}

	private void WallpaperGrid_ContainerContentChanging(
		ListViewBase sender, ContainerContentChangingEventArgs args)
	{
		if (args.InRecycleQueue || args.Item is not WallpaperItem item)
			return;
		if (args.ItemContainer.ContentTemplateRoot is not Grid root)
		{
			DispatcherQueue.TryEnqueue(() =>
			{
				if (args.ItemContainer.ContentTemplateRoot is Grid later)
					AttachFavoriteButton(later, item);
			});
			return;
		}
		AttachFavoriteButton(root, item);
	}

	private void AttachFavoriteButton(Grid root, WallpaperItem item)
	{
		var button = root.Children.OfType<Button>()
			.FirstOrDefault(x => x.Name == "FavoriteStarButton");
		if (button is null)
		{
			button = new Button
			{
				Name = "FavoriteStarButton",
				Width = 34,
				Height = 34,
				Padding = new Thickness(0),
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0, 6, 6, 0),
				Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
				BorderThickness = new Thickness(0),
				Content = new FontIcon { FontSize = 16 }
			};
			ToolTipService.SetToolTip(button, "收藏");
			button.Click += (_, _) => ToggleFavorite(item);
			root.Children.Add(button);
		}
		button.Tag = item;
		UpdateFavoriteButton(button, item);
	}

	private void UpdateFavoriteButton(Button button, WallpaperItem item)
	{
		if (button.Content is FontIcon icon)
		{
			icon.Glyph = item.IsFavorite ? "\uE735" : "\uE734";
			icon.Foreground = new SolidColorBrush(item.IsFavorite
				? Color.FromArgb(0xFF, 0xFF, 0xC8, 0x28)
				: Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
		}
	}

	private void ToggleFavorite(WallpaperItem item)
	{
		item.IsFavorite = !item.IsFavorite;
		ConfigStore.Save(_config);
		foreach (var container in _visible)
		{
			if (WallpaperGrid.ContainerFromItem(container) is GridViewItem itemContainer &&
			    itemContainer.ContentTemplateRoot is Grid root &&
			    root.Children.OfType<Button>()
				    .FirstOrDefault(x => x.Name == "FavoriteStarButton") is { } button)
				UpdateFavoriteButton(button, container);
		}
		if (_favoritesOnly)
			ApplyFilter();
	}

	private bool IsSingleVisibleRow()
	{
		if (_visible.Count < 2) return true;
		var panel = WallpaperGrid.ItemsPanelRoot;
		if (panel is null) return false;
		var first = WallpaperGrid.ContainerFromIndex(0) as FrameworkElement;
		var last = WallpaperGrid.ContainerFromIndex(_visible.Count - 1) as FrameworkElement;
		if (first is null || last is null) return false;
		try
		{
			var firstY = first.TransformToVisual(panel)
				.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
			var lastY = last.TransformToVisual(panel)
				.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
			return Math.Abs(firstY - lastY) < 2;
		}
		catch
		{
			return false;
		}
	}

	private void RevealWallpaper(string path)
	{
		if (string.IsNullOrWhiteSpace(path)) return;
		var item = _catalog.Items.FirstOrDefault(x =>
			string.Equals(x.Path, path, StringComparison.OrdinalIgnoreCase));
		if (item is null) return;
		if (!_visible.Contains(item))
		{
			_kind = item.Kind;
			SelectCategory(item.Kind.ToString());
			ApplyFilter();
		}
		WallpaperGrid.SelectedItem = item;
		WallpaperGrid.UpdateLayout();
		WallpaperGrid.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
		DispatcherQueue.TryEnqueue(() =>
		{
			if (!_visible.Contains(item)) return;
			WallpaperGrid.UpdateLayout();
			WallpaperGrid.ScrollIntoView(item, ScrollIntoViewAlignment.Leading);
		});
	}

	private void SetupNavHoverEffects()
	{
		var categories = Nav.MenuItems.OfType<NavigationViewItem>()
			.Where(x => x.Tag is "Video" or "Static" or "Web" or "Scene" or "Sequence")
			.ToList();
		NavigationViewItem? HitTest(Windows.Foundation.Point point)
		{
			bool Contains(FrameworkElement element, Windows.Foundation.Point point)
			{
				try
				{
					var p = element.TransformToVisual(Nav)
						.TransformPoint(new Windows.Foundation.Point(0, 0));
					return point.X >= p.X && point.X <= p.X + element.ActualWidth &&
					       point.Y >= p.Y && point.Y <= p.Y + element.ActualHeight;
				}
				catch { return false; }
			}
			foreach (var item in categories)
			{
				if (Contains(item, point)) return item;
			}
			return null;
		}
		foreach (var item in categories)
			item.IsExpanded = false;
		void Apply(NavigationViewItem? active)
		{
			foreach (var item in categories)
			{
				var isActive = ReferenceEquals(item, active);
				// Use opacity for the hover cue. A local Background brush could
				// stay on the previously hovered item after the pointer moved,
				// which made Static look blue while hovering Scene.
				item.Opacity = isActive ? 0.72 : 1.0;
			}
		}
		Nav.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
			Apply(HitTest(e.GetCurrentPoint(Nav).Position))), true);
		Nav.AddHandler(UIElement.PointerExitedEvent,
			new PointerEventHandler((_, _) => Apply(null)), true);
		foreach (var child in categories.SelectMany(x => x.MenuItems.OfType<NavigationViewItem>()))
		{
			child.PointerEntered += (_, _) => child.Opacity = 0.72;
			child.PointerExited += (_, _) => child.Opacity = 1.0;
		}
	}

	private void SetupNavFilterPopup()
	{
		if (_navMenus.Count > 0) return;
		NavigationViewItem? HitTestNavItem(Windows.Foundation.Point point)
		{
			foreach (var navItem in Nav.MenuItems.OfType<NavigationViewItem>())
			{
				var tag = navItem.Tag as string;
				if (tag is not ("Video" or "Web" or "Scene" or "Sequence")) continue;
				try
				{
					var p = navItem.TransformToVisual(Nav)
						.TransformPoint(new Windows.Foundation.Point(0, 0));
					if (point.X >= p.X && point.X <= p.X + navItem.ActualWidth &&
					    point.Y >= p.Y && point.Y <= p.Y + navItem.ActualHeight)
						return navItem;
				}
				catch { }
			}
			return null;
		}
		foreach (var navItem in Nav.MenuItems.OfType<NavigationViewItem>())
		{
			var tag = navItem.Tag as string;
			if (tag is not ("Video" or "Web" or "Scene" or "Sequence")) continue;
			var menu = new MenuFlyout();
			menu.Items.Add(new MenuFlyoutItem
			{
				Text = navItem.Content?.ToString() ?? tag,
				IsEnabled = false
			});
			menu.Items.Add(new MenuFlyoutSeparator());
			var all = new MenuFlyoutItem { Text = "全部壁纸" };
			var favorite = new MenuFlyoutItem { Text = "收藏" };
			void Choose(bool favorites)
			{
				_favoritesOnly = favorites;
				SelectCategory(tag);
				ApplyFilter();
				menu.Hide();
				_navPinnedItem = null;
			}
			all.Click += (_, _) => Choose(false);
			favorite.Click += (_, _) => Choose(true);
			menu.Items.Add(all);
			menu.Items.Add(favorite);
			menu.Closed += (_, _) =>
			{
				if (ReferenceEquals(_navOpenMenu, menu)) _navOpenMenu = null;
				if (ReferenceEquals(_navPinnedItem, navItem)) _navPinnedItem = null;
			};
			_navMenus[navItem] = menu;
		}
		Nav.AddHandler(UIElement.PointerMovedEvent, new PointerEventHandler((_, e) =>
		{
			if (_navPinnedItem is not null) return;
			var item = HitTestNavItem(e.GetCurrentPoint(Nav).Position);
			if (item is null)
			{
				_navHoverItem = null;
				_navOpenMenu?.Hide();
				return;
			}
			if (!_navMenus.TryGetValue(item, out var menu) ||
			    ReferenceEquals(_navOpenMenu, menu))
				return;
			_navHoverItem = item;
			_navOpenMenu?.Hide();
			_navOpenMenu = menu;
			menu.ShowAt(item);
		}), true);
		Nav.AddHandler(UIElement.TappedEvent, new TappedEventHandler((_, e) =>
		{
			var item = HitTestNavItem(e.GetPosition(Nav));
			if (item is null || !_navMenus.TryGetValue(item, out var menu)) return;
			_navPinnedItem = item;
			menu.ShowAt(item);
		}), true);
	}

	private void FavoriteFilterCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_catalog is null) return;
		_favoritesOnly = FavoriteFilterCombo.SelectedIndex == 1;
		ApplyFilter();
	}

	private void ApplyFilter()
	{
		string query = SearchBox.Text?.Trim() ?? "";
		bool flag = !string.IsNullOrWhiteSpace(query);
		_visible = (flag ? (from x in (from item in _catalog.Items
				select new
				{
					item = item,
					score = SearchScore(query, item.Title)
				} into x
				where x.score > 0.0
				orderby x.score descending
				select x).ThenBy(x => x.item.Title, StringComparer.OrdinalIgnoreCase)
			select x.item).ToList() : _catalog.ByKind(_kind).ToList());
		if (_favoritesOnly)
			_visible = _visible.Where(x => x.IsFavorite).ToList();
		FavoriteFilterCombo.Visibility = Visibility.Collapsed;
		WallpaperGrid.ItemsSource = _visible;
		EmptyState.Visibility = ((_visible.Count != 0) ? Visibility.Collapsed : Visibility.Visible);
		VideoToWebButton.Visibility = ((_kind != WallpaperKind.Web) ? Visibility.Collapsed : Visibility.Visible);
		CreateWebButton.Visibility = _kind == WallpaperKind.Web ? Visibility.Visible : Visibility.Collapsed;
		CreateSceneButton.Visibility = _kind == WallpaperKind.Scene ? Visibility.Visible : Visibility.Collapsed;
		ConvertToSequenceButton.Visibility = ((_kind != WallpaperKind.Video) ? Visibility.Collapsed : Visibility.Visible);
		ConvertToMp4Button.Visibility = ((_kind != WallpaperKind.Video && _kind != WallpaperKind.Sequence)
			? Visibility.Collapsed : Visibility.Visible);
		BatchDeleteButton.IsEnabled = _visible.Count > 0;
		if (!flag && _kind == WallpaperKind.Scene)
		{
			WallpaperGrid.ItemTemplate = (DataTemplate)Root.Resources["SceneCardTemplate"];
			WallpaperGrid.ItemsPanel = (ItemsPanelTemplate)Root.Resources["SceneGridPanel"];
		}
		else
		{
			if ((object)_defaultGridTemplate != null)
			{
				WallpaperGrid.ItemTemplate = _defaultGridTemplate;
			}
			if ((object)_defaultGridPanel != null)
			{
				WallpaperGrid.ItemsPanel = _defaultGridPanel;
			}
		}
		TextBlock categoryTitle = CategoryTitle;
		string text = ((!flag) ? (_kind switch
		{
			WallpaperKind.Video => "动态壁纸", 
			WallpaperKind.Static => "静态壁纸",
			WallpaperKind.Web => "Web 壁纸", 
			WallpaperKind.Sequence => "组合壁纸", 
			_ => "场景壁纸", 
		}) : "搜索结果");
		categoryTitle.Text = text;
		TextBlock categorySubtitle = CategorySubtitle;
		string text2;
		if (flag)
		{
			text2 = $"{_visible.Count} 个 · 支持中文、拼音和首字母搜索";
		}
		else
		{
			text2 = ((_kind == WallpaperKind.Sequence) ? $"{_visible.Count} 个 · 第一个播完一遍后循环第二个" : $"{_visible.Count} 个 · 文件或文件夹都可以直接加入");
		}
		categorySubtitle.Text = text2;
		if (_selected != null && !_visible.Contains(_selected))
		{
			ClearSelection();
		}
	}

	private static double SearchScore(string query, string title)
	{
		if (string.IsNullOrWhiteSpace(query) || string.IsNullOrWhiteSpace(title))
		{
			return 0.0;
		}
		query = query.Trim();
		title = title.Trim();
		int num = Math.Max(0, title.Length - query.Length);
		if (string.Equals(title, query, StringComparison.OrdinalIgnoreCase))
		{
			return 10000.0;
		}
		if (title.StartsWith(query, StringComparison.OrdinalIgnoreCase))
		{
			return 9000 - num;
		}
		int num2 = title.IndexOf(query, StringComparison.OrdinalIgnoreCase);
		if (num2 >= 0)
		{
			return 8000 - num2 * 10 - num;
		}
		var pinyinQuery = query.Replace(" ", "").Replace("'", "")
			.ToLowerInvariant();
		if (pinyinQuery.Length > 0 &&
		    pinyinQuery.All(ch => (ch >= 'a' && ch <= 'z') || (ch >= '0' && ch <= '9')))
		{
			var fullPinyin = PinyinHelper.ToPinyin(title);
			var initials = PinyinHelper.ToInitials(title);
			if (string.Equals(fullPinyin, pinyinQuery, StringComparison.Ordinal))
				return 9800;
			if (fullPinyin.StartsWith(pinyinQuery, StringComparison.Ordinal))
				return 9300 - Math.Max(0, fullPinyin.Length - pinyinQuery.Length);
			if (fullPinyin.Contains(pinyinQuery, StringComparison.Ordinal))
				return 8600;
			if (string.Equals(initials, pinyinQuery, StringComparison.Ordinal))
				return 8400;
			if (initials.StartsWith(pinyinQuery, StringComparison.Ordinal))
				return 7900 - Math.Max(0, initials.Length - pinyinQuery.Length);
			if (initials.Contains(pinyinQuery, StringComparison.Ordinal))
				return 6800;
			// Latin queries are meant as pinyin/initials, not a fuzzy
			// "contains any of these letters" match against filenames.
			return 0;
		}
		int num3 = 0;
		string text = title;
		foreach (char c in text)
		{
			if (num3 < query.Length && char.ToUpperInvariant(c) == char.ToUpperInvariant(query[num3]))
			{
				num3++;
			}
		}
		if (num3 == query.Length)
		{
			return 4000 - num + query.Length * 20;
		}
		int num4 = query.Distinct().Count((char ch) => title.Contains(ch, StringComparison.OrdinalIgnoreCase));
		return (num4 > 0) ? (1000 + num4 * 50 - num) : 0;
	}

	private async void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
	{
		NavigationViewItemBase selectedItemContainer = args.SelectedItemContainer;
		if (!(selectedItemContainer is NavigationViewItem))
		{
			return;
		}
		string rawTag = selectedItemContainer.Tag as string ?? "";
		var tagParts = rawTag.Split('|');
		string text = tagParts[0];
		_favoritesOnly = tagParts.Length > 1 &&
			string.Equals(tagParts[1], "Fav", StringComparison.OrdinalIgnoreCase);
		switch (text)
		{
		case "Pause":
			TogglePause();
			return;
		case "Recycle":
			await ShowRecycleBinAsync();
			return;
		case "SuperResolution":
			StopDetailPreview();
			DetailPage.Visibility = Visibility.Collapsed;
			LibraryContent.Visibility = Visibility.Collapsed;
			SettingsViewControl.Visibility = Visibility.Collapsed;
			SuperResolutionViewControl.Visibility = Visibility.Visible;
			AiInterpolationViewControl.Visibility = Visibility.Collapsed;
			SuperResolutionViewControl.Configure(_config, _catalog);
			return;
		case "AiInterpolation":
			StopDetailPreview();
			DetailPage.Visibility = Visibility.Collapsed;
			LibraryContent.Visibility = Visibility.Collapsed;
			SettingsViewControl.Visibility = Visibility.Collapsed;
			SuperResolutionViewControl.Visibility = Visibility.Collapsed;
			AiInterpolationViewControl.Visibility = Visibility.Visible;
			AiInterpolationViewControl.Configure(_config, _catalog);
			return;
		case "Settings":
			StopDetailPreview();
			DetailPage.Visibility = Visibility.Collapsed;
			LibraryContent.Visibility = Visibility.Collapsed;
			SuperResolutionViewControl.Visibility = Visibility.Collapsed;
			AiInterpolationViewControl.Visibility = Visibility.Collapsed;
			SettingsViewControl.Visibility = Visibility.Visible;
			SettingsViewControl.Configure(_config, _catalog, _thumbnails);
			return;
		}
		StopDetailPreview();
		DetailPage.Visibility = Visibility.Collapsed;
		LibraryContent.Visibility = Visibility.Visible;
		SettingsViewControl.Visibility = Visibility.Collapsed;
		SuperResolutionViewControl.Visibility = Visibility.Collapsed;
		AiInterpolationViewControl.Visibility = Visibility.Collapsed;
		_kind = text switch
		{
			"Static" => WallpaperKind.Static,
			"Web" => WallpaperKind.Web, 
			"Scene" => WallpaperKind.Scene, 
			"Sequence" => WallpaperKind.Sequence, 
			_ => WallpaperKind.Video, 
		};
		if (!string.IsNullOrWhiteSpace(SearchBox.Text))
		{
			SearchBox.Text = string.Empty;
		}
		ApplyFilter();
		LoadVisibleThumbnailsAsync();
	}

	private async Task LoadVisibleThumbnailsAsync()
	{
		int token = _refreshToken;
		foreach (WallpaperItem item in _visible.ToList())
		{
			string text = await _thumbnails.GetAsync(item);
			if (token == _refreshToken && text != null)
			{
				try
				{
					item.Thumbnail = new BitmapImage(new Uri(text));
				}
				catch
				{
				}
			}
		}
	}

	private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
	{
		if (args.Reason == AutoSuggestionBoxTextChangeReason.UserInput)
		{
			ApplyFilter();
		}
	}

	private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
	{
		ApplyFilter();
	}

	private async Task ShowLocalVideoSearchAsync(string query)
	{
		SetStatus("正在搜索本机视频：" + query + " …");
		IReadOnlyList<LocalVideoResult> results;
		try
		{
			results = await _localVideos.SearchAsync(query);
		}
		catch (Exception ex)
		{
			AppLog.Error("local video search failed", ex);
			SetStatus("搜索失败：" + ex.Message);
			return;
		}
		if (results.Count == 0)
		{
			await new ContentDialog
			{
				Title = "没有找到视频",
				Content = "本机没有找到文件名包含“" + query + "”的视频。\n可以尝试添加文件夹，或把视频放到“视频\\Wallerpaper”目录。",
				CloseButtonText = "知道了",
				XamlRoot = Content.XamlRoot
			}.ShowAsync();
			SetStatus("未找到匹配视频");
			return;
		}
		ListView list = new ListView
		{
			ItemsSource = results,
			DisplayMemberPath = "Display",
			SelectionMode = ListViewSelectionMode.Single,
			Height = 360.0,
			Margin = new Thickness(0.0, 8.0, 0.0, 0.0)
		};
		ContentDialog dialog = new ContentDialog
		{
			Title = $"找到 {results.Count} 个视频",
			Content = new StackPanel
			{
				Children = 
				{
					(UIElement)new TextBlock
					{
						Text = "双击结果，或选中后点“添加壁纸”。",
						TextWrapping = TextWrapping.Wrap,
						Opacity = 0.75
					},
					(UIElement)list
				}
			},
			PrimaryButtonText = "添加壁纸",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = Content.XamlRoot
		};
		list.DoubleTapped += async (object _, DoubleTappedRoutedEventArgs _) =>
		{
			await AddSelectedAsync();
			dialog.Hide();
		};
		dialog.PrimaryButtonClick += async (ContentDialog _, ContentDialogButtonClickEventArgs args) =>
		{
			if (!(list.SelectedItem is LocalVideoResult))
			{
				args.Cancel = true;
				SetStatus("请先选择一个视频");
			}
			else
			{
				args.Cancel = true;
				await AddSelectedAsync();
				dialog.Hide();
			}
		};
		await dialog.ShowAsync();
		SetStatus($"搜索“{query}”完成，共 {results.Count} 个结果");
		async Task AddSelectedAsync()
		{
			if (list.SelectedItem is LocalVideoResult localVideoResult)
			{
				await AddVideoFileAsync(localVideoResult.Path);
			}
		}
	}

	private async void AddFolder_Click(object sender, RoutedEventArgs e)
	{
		_ = 3;
		try
		{
			FolderPicker folderPicker = new FolderPicker
			{
				FileTypeFilter = { "*" }
			};
			InitializeWithWindow.Initialize(folderPicker, WindowNative.GetWindowHandle(this));
			StorageFolder storageFolder = await folderPicker.PickSingleFolderAsync();
			if ((object)storageFolder != null)
			{
				if (_kind == WallpaperKind.Sequence)
				{
					await ShowWrongPageDialogAsync();
					return;
				}
				if (_kind == WallpaperKind.Video)
				{
					await PickVideosFromFolderAsync(storageFolder.Path);
					return;
				}
				WallpaperKind folderKind = _kind;
				AddLibraryRoot(storageFolder.Path, folderKind, recursive: true);
				ConfigStore.Save(_config);
				await RefreshCatalogAsync();
				ApplyFilter();
				SetStatus($"已添加文件夹，载入 {_catalog.ByKind(folderKind).Count} 个壁纸");
			}
		}
		catch (Exception ex)
		{
			AppLog.Error("add folder failed", ex);
			SetStatus("添加文件夹失败：" + ex.Message);
		}
	}

	private async Task PickVideosFromFolderAsync(string folder)
	{
		List<string> videos = (from path in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
			where new string[10] { ".mp4", ".webm", ".mkv", ".mov", ".avi", ".wmv", ".m4v", ".flv", ".ts", ".m2ts" }.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)
			select path).OrderBy((string path) => path, StringComparer.OrdinalIgnoreCase).ToList();
		if (videos.Count == 0)
		{
			await new ContentDialog
			{
				Title = "文件夹里没有视频",
				Content = folder,
				CloseButtonText = "知道了",
				XamlRoot = Content.XamlRoot
			}.ShowAsync();
			return;
		}
		List<WallpaperItem> items = videos.Select((string path) => new WallpaperItem
		{
			Id = StableId(path),
			Title = Path.GetFileName(path),
			Kind = WallpaperKind.Video,
			Path = path
		}).ToList();
		foreach (WallpaperItem item in items)
		{
			string text = await _thumbnails.GetAsync(item);
			if (!string.IsNullOrWhiteSpace(text) && File.Exists(text))
			{
				item.Thumbnail = new BitmapImage(new Uri(text));
			}
		}
		GridView grid = new GridView
		{
			ItemsSource = items,
			SelectionMode = ListViewSelectionMode.Multiple,
			ItemTemplate = (DataTemplate)Root.Resources["DeleteCardTemplate"],
			ItemsPanel = (ItemsPanelTemplate)Root.Resources["DeleteGridPanel"],
			Width = 536.0,
			Height = 360.0,
			IsItemClickEnabled = false
		};
		Button button = new Button
		{
			Content = "全部添加",
			Background = new SolidColorBrush(Color.FromArgb(179, 217, 217, 217)),
			Foreground = new SolidColorBrush(Color.FromArgb(230, 16, 21, 31)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(85, 0, 0, 0)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(8.0),
			HorizontalAlignment = HorizontalAlignment.Left,
			Padding = new Thickness(22.0, 8.0, 22.0, 8.0)
		};
		Button addSelected = new Button
		{
			Content = "添加选中的视频",
			Background = new SolidColorBrush(Color.FromArgb(204, 0, 120, 212)),
			Foreground = new SolidColorBrush(Color.FromArgb(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
			BorderBrush = new SolidColorBrush(Color.FromArgb(136, 0, 120, 212)),
			BorderThickness = new Thickness(1.0),
			CornerRadius = new CornerRadius(8.0),
			HorizontalAlignment = HorizontalAlignment.Right,
			Padding = new Thickness(22.0, 8.0, 22.0, 8.0),
			IsEnabled = false
		};
		grid.SelectionChanged += (object _, SelectionChangedEventArgs _) =>
		{
			addSelected.IsEnabled = grid.SelectedItems.Count > 0;
		};
		List<WallpaperItem> chosen = new List<WallpaperItem>();
		ContentDialog dialog = new ContentDialog
		{
			Title = "选择要添加的视频",
			CloseButtonText = "取消",
			XamlRoot = Content.XamlRoot,
			Content = new Grid
			{
				Width = 536.0,
				RowDefinitions = 
				{
					new RowDefinition
					{
						Height = GridLength.Auto
					},
					new RowDefinition
					{
						Height = new GridLength(1.0, GridUnitType.Star)
					},
					new RowDefinition
					{
						Height = GridLength.Auto
					}
				},
				Children = 
				{
					(UIElement)new StackPanel
					{
						Spacing = 2.0,
						Children = 
						{
							(UIElement)new TextBlock
							{
								Text = folder,
								FontSize = 11.0,
								Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
								TextTrimming = TextTrimming.CharacterEllipsis
							},
							(UIElement)new TextBlock
							{
								Text = $"{videos.Count} 个视频 · 可多选",
								FontSize = 12.0,
								Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
							}
						}
					},
					(UIElement)grid,
					(UIElement)new Grid
					{
						Margin = new Thickness(0.0, 10.0, 0.0, 0.0),
						ColumnDefinitions = 
						{
							new ColumnDefinition(),
							new ColumnDefinition()
						},
						Children = 
						{
							(UIElement)button,
							(UIElement)addSelected
						}
					}
				}
			}
		};
		Grid.SetRow(grid, 1);
		Grid.SetRow((FrameworkElement)((Grid)dialog.Content).Children[2], 2);
		Grid.SetColumn(addSelected, 1);
		button.Click += (object _, RoutedEventArgs _) =>
		{
			chosen = items.ToList();
			dialog.Hide();
		};
		addSelected.Click += (object _, RoutedEventArgs _) =>
		{
			chosen = grid.SelectedItems.Cast<WallpaperItem>().ToList();
			dialog.Hide();
		};
		await dialog.ShowAsync();
		if (chosen.Count == 0)
		{
			return;
		}
		int added = 0;
		string? lastStored = null;
		foreach (WallpaperItem item2 in chosen)
		{
			var stored = await ManagedLibraryService.ImportAsync(item2.Path);
			if (AddLibraryRoot(stored, WallpaperKind.Video, recursive: false))
			{
				added++;
				lastStored = stored;
			}
		}
		ConfigStore.Save(_config);
		await RefreshCatalogAsync();
		SearchBox.Text = string.Empty;
		ApplyFilter();
		RevealWallpaper(lastStored);
		SetStatus($"已从文件夹添加 {added} 个视频");
	}

	private static string StableId(string path)
	{
		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path).ToLowerInvariant()))).Substring(0, 12).ToLowerInvariant();
	}

	private async void AddFile_Click(object sender, RoutedEventArgs e)
	{
		_ = 3;
		try
		{
			FileOpenPicker fileOpenPicker = new FileOpenPicker();
			string[] array = new string[19]
			{
				".mp4", ".webm", ".mkv", ".mov", ".avi", ".wmv", ".m4v", ".flv", ".ts", ".m2ts",
				".png", ".jpg", ".jpeg", ".webp", ".bmp", ".gif",
				".html", ".htm", ".json"
			};
			foreach (string item in array)
			{
				fileOpenPicker.FileTypeFilter.Add(item);
			}
			InitializeWithWindow.Initialize(fileOpenPicker, WindowNative.GetWindowHandle(this));
			IReadOnlyList<StorageFile> files = await fileOpenPicker.PickMultipleFilesAsync();
			if (files == null || files.Count == 0)
			{
				return;
			}
			if (_kind == WallpaperKind.Sequence)
			{
				await ShowWrongPageDialogAsync();
				return;
			}
			foreach (StorageFile item2 in files)
			{
				if (KindForPath(item2.Path) != _kind)
				{
					await ShowWrongPageDialogAsync();
					return;
				}
			}
			string? lastStored = null;
			foreach (StorageFile item3 in files)
			{
				var stored = _kind == WallpaperKind.Video
					? await ManagedLibraryService.ImportAsync(item3.Path)
					: item3.Path;
				AddLibraryRoot(stored, _kind, recursive: false);
				lastStored = stored;
			}
			ConfigStore.Save(_config);
			await RefreshCatalogAsync();
			SearchBox.Text = string.Empty;
			ApplyFilter();
			RevealWallpaper(lastStored);
			SetStatus($"已添加 {files.Count} 个壁纸文件");
		}
		catch (Exception ex)
		{
			AppLog.Error("add file failed", ex);
			SetStatus("添加文件失败：" + ex.Message);
		}
	}

	private async void BatchDelete_Click(object sender, RoutedEventArgs e)
	{
		List<WallpaperItem> list = ((!string.IsNullOrWhiteSpace(SearchBox.Text)) ? _visible.ToList() : _catalog.ByKind(_kind).ToList());
		if (list.Count == 0)
		{
			SetStatus("当前分类没有可删除的壁纸");
			return;
		}
		GridView list2 = new GridView
		{
			ItemsSource = list,
			SelectionMode = ListViewSelectionMode.Multiple,
			ItemTemplate = (DataTemplate)Root.Resources["DeleteCardTemplate"],
			ItemsPanel = (ItemsPanelTemplate)Root.Resources["DeleteGridPanel"],
			Width = 536.0,
			Height = 360.0,
			IsItemClickEnabled = false
		};
		Button button = new Button
		{
			Content = "全选",
			HorizontalAlignment = HorizontalAlignment.Left
		};
		button.Click += (object _, RoutedEventArgs _) =>
		{
			list2.SelectAll();
		};
		ContentDialog contentDialog = new ContentDialog();
		contentDialog.Title = "删除" + CategoryTitle.Text;
		contentDialog.Content = new StackPanel
		{
			Spacing = 8.0,
			Children = 
			{
				(UIElement)new TextBlock
				{
					Text = "可以多选；从库中移除后保留源文件。组合壁纸会移除其组合条目。",
					TextWrapping = TextWrapping.Wrap
				},
				(UIElement)button,
				(UIElement)list2
			}
		};
		contentDialog.PrimaryButtonText = "删除选中";
		contentDialog.CloseButtonText = "取消";
		contentDialog.DefaultButton = ContentDialogButton.Close;
		contentDialog.XamlRoot = Content.XamlRoot;
		contentDialog.PrimaryButtonClick += (ContentDialog _, ContentDialogButtonClickEventArgs args) =>
		{
			if (list2.SelectedItems.Count == 0)
			{
				args.Cancel = true;
				SetStatus("请至少选择一个壁纸");
			}
		};
		if (await contentDialog.ShowAsync() == ContentDialogResult.Primary)
		{
			await DeleteWallpapersAsync(list2.SelectedItems.Cast<WallpaperItem>().ToList());
		}
	}

	private async Task DeleteWallpapersAsync(IReadOnlyList<WallpaperItem> items)
	{
		if (items.Count == 0)
		{
			return;
		}
		try
		{
			var deletedIds = items.Select(x => x.Id)
				.ToHashSet(StringComparer.OrdinalIgnoreCase);
			var wasPlaying = _config.Monitors.Values.Any(x =>
				deletedIds.Contains(x.WallpaperId));
			var pauseAfterDelete =
				string.Equals(_config.Playback.DeleteCurrentBehavior, "pause",
					StringComparison.OrdinalIgnoreCase) &&
				_config.Monitors.Values.Any(x => deletedIds.Contains(x.WallpaperId));
			var fallback = ChooseDeleteFallback(deletedIds);
			foreach (WallpaperItem item in items)
			{
				try { await RecycleBinService.AddAsync(item, deleteOriginal: false); }
				catch (Exception ex) { AppLog.Error("recycle add failed", ex); }
				if (item.Kind == WallpaperKind.Sequence)
				{
					_catalog.Remove(item);
				}
				else
				{
					if (!_config.ExcludedPaths.Any((string x) => string.Equals(x, item.Path, StringComparison.OrdinalIgnoreCase)))
					{
						_config.ExcludedPaths.Add(item.Path);
					}
					_config.Wallpapers.RemoveAll((WallpaperItem x) => string.Equals(x.Id, item.Id, StringComparison.OrdinalIgnoreCase));
				}
				foreach (string device in (from x in _config.Monitors
					where string.Equals(x.Value.WallpaperId, item.Id, StringComparison.OrdinalIgnoreCase)
					select x.Key).ToList())
				{
					if (fallback is not null)
					{
						_config.Monitors[device] = new MonitorAssignment
						{
							WallpaperId = fallback.Id,
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
			if (_selected != null && items.Any((WallpaperItem x) => x.Id == _selected.Id))
			{
				ClearSelection();
			}
			ConfigStore.Save(_config);
			await RefreshCatalogAsync();
			if (!wasPlaying)
			{
				// Nothing currently visible used these items.
			}
			else if (pauseAfterDelete)
			{
				_host.Stop();
				DesktopWallpaperService.Restore();
			}
			else if (_host.IsRunning)
			{
				_host.Restart();
			}
			SetStatus($"已删除 {items.Count} 个壁纸（源文件保留）");
		}
		catch (Exception ex)
		{
			AppLog.Error("batch delete failed", ex);
			SetStatus("批量删除失败：" + ex.Message);
		}
	}

	private async void EditQueue_Click(object sender, RoutedEventArgs e)
	{
		WallpaperItem item = _selected;
		if (item == null || item.Kind != WallpaperKind.Sequence)
		{
			return;
		}
		ObservableCollection<PlaylistEntry> entries = new ObservableCollection<PlaylistEntry>((item.Playlist.Count > 0) ? item.Playlist.Select(CloneEntry) : new PlaylistEntry[2]
		{
			new PlaylistEntry
			{
				Path = item.Path
			},
			new PlaylistEntry
			{
				Path = item.SecondPath
			}
		}.Where((PlaylistEntry x) => File.Exists(x.Path)));
		if (entries.Count == 0)
		{
			SetStatus("队列为空，请先添加视频");
			return;
		}
		ListView list = new ListView
		{
			ItemsSource = entries,
			DisplayMemberPath = "Display",
			Height = 220.0
		};
		Button button = new Button
		{
			Content = "添加视频"
		};
		Button button2 = new Button
		{
			Content = "移除"
		};
		Button button3 = new Button
		{
			Content = "上移"
		};
		Button button4 = new Button
		{
			Content = "下移"
		};
		NumberBox start = new NumberBox
		{
			Header = "入点（秒）",
			Minimum = 0.0,
			Maximum = 36000.0,
			SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
		};
		NumberBox end = new NumberBox
		{
			Header = "出点（秒）",
			Minimum = 0.0,
			Maximum = 36000.0,
			SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
		};
		bool queueUi = false;
		list.SelectionChanged += (object _, SelectionChangedEventArgs _) =>
		{
			if (list.SelectedItem is PlaylistEntry playlistEntry)
			{
				queueUi = true;
				start.Value = playlistEntry.StartSeconds;
				end.Value = playlistEntry.EndSeconds;
				queueUi = false;
			}
		};
		start.ValueChanged += (NumberBox _, NumberBoxValueChangedEventArgs _) =>
		{
			if (!queueUi && list.SelectedItem is PlaylistEntry playlistEntry)
			{
				playlistEntry.StartSeconds = Math.Max(0.0, start.Value);
				Refresh();
			}
		};
		end.ValueChanged += (NumberBox _, NumberBoxValueChangedEventArgs _) =>
		{
			if (!queueUi && list.SelectedItem is PlaylistEntry playlistEntry)
			{
				playlistEntry.EndSeconds = Math.Max(0.0, end.Value);
				Refresh();
			}
		};
		button.Click += async (object _, RoutedEventArgs _) =>
		{
			StorageFile storageFile = await PickVideoAsync("添加队列视频");
			if ((object)storageFile != null)
			{
				entries.Add(new PlaylistEntry
				{
					Path = storageFile.Path
				});
				Refresh();
			}
		};
		button2.Click += (object _, RoutedEventArgs _) =>
		{
			if (list.SelectedItem is PlaylistEntry item2)
			{
				entries.Remove(item2);
			}
			Refresh();
		};
		button3.Click += (object _, RoutedEventArgs _) =>
		{
			if (list.SelectedItem is PlaylistEntry playlistEntry)
			{
				int num = entries.IndexOf(playlistEntry);
				if (num > 0)
				{
					entries.Move(num, num - 1);
				}
				Refresh();
				list.SelectedItem = playlistEntry;
			}
		};
		button4.Click += (object _, RoutedEventArgs _) =>
		{
			if (list.SelectedItem is PlaylistEntry playlistEntry)
			{
				int num = entries.IndexOf(playlistEntry);
				if (num >= 0 && num < entries.Count - 1)
				{
					entries.Move(num, num + 1);
				}
				Refresh();
				list.SelectedItem = playlistEntry;
			}
		};
		ContentDialog contentDialog = new ContentDialog();
		contentDialog.Title = "时间轴裁剪与播放队列";
		contentDialog.PrimaryButtonText = "生成队列视频";
		contentDialog.CloseButtonText = "取消";
		contentDialog.DefaultButton = ContentDialogButton.Primary;
		contentDialog.XamlRoot = Content.XamlRoot;
		contentDialog.Content = new StackPanel
		{
			Spacing = 8.0,
			Children = 
			{
				(UIElement)new TextBlock
				{
					Text = "整个队列循环。硬切拼接；入点/出点单位为秒，0 表示使用完整视频。",
					TextWrapping = TextWrapping.Wrap,
					FontSize = 12.0
				},
				(UIElement)list,
				(UIElement)new StackPanel
				{
					Orientation = Orientation.Horizontal,
					Spacing = 6.0,
					Children = 
					{
						(UIElement)button,
						(UIElement)button2,
						(UIElement)button3,
						(UIElement)button4
					}
				},
				(UIElement)new StackPanel
				{
					Orientation = Orientation.Horizontal,
					Spacing = 8.0,
					Children = 
					{
						(UIElement)start,
						(UIElement)end
					}
				}
			}
		};
		contentDialog.PrimaryButtonClick += (ContentDialog _, ContentDialogButtonClickEventArgs args) =>
		{
			if (entries.Count == 0)
			{
				args.Cancel = true;
				SetStatus("请至少添加一个视频");
			}
		};
		if (await contentDialog.ShowAsync() != ContentDialogResult.Primary)
		{
			return;
		}
		item.Playlist = entries.ToList();
		Persist();
		ProgressBar progressBar = new ProgressBar
		{
			Minimum = 0.0,
			Maximum = 100.0
		};
		TextBlock status = new TextBlock
		{
			Text = "准备…",
			TextWrapping = TextWrapping.Wrap
		};
		ContentDialog progressDialog = new ContentDialog
		{
			Title = "生成队列视频",
			Content = new StackPanel
			{
				Spacing = 12.0,
				Children = 
				{
					(UIElement)status,
					(UIElement)progressBar
				}
			},
			CloseButtonText = "取消",
			XamlRoot = Content.XamlRoot
		};
		CancellationTokenSource cts = new CancellationTokenSource();
		try
		{
			progressDialog.CloseButtonClick += (ContentDialog _, ContentDialogButtonClickEventArgs _) =>
			{
				cts.Cancel();
			};
			progressDialog.ShowAsync();
			await Task.Delay(120);
			try
			{
				QueueRenderResult result = await new QueueRenderService().RenderAsync(entries, (int percent, string message) =>
				{
					DispatcherQueue.TryEnqueue(() =>
					{
						progressBar.Value = Math.Clamp(percent, 0, 100);
						status.Text = message;
					});
				}, cts.Token);
				progressDialog.Hide();
				AddLibraryRoot(result.OutputPath, WallpaperKind.Video, recursive: false);
				ConfigStore.Save(_config);
				await RefreshCatalogAsync();
				ApplyFilter();
				RevealWallpaper(result.OutputPath);
				SetStatus($"队列视频已生成：{result.Entries} 段，硬切，整个视频循环");
			}
			catch (OperationCanceledException)
			{
				progressDialog.Hide();
				SetStatus("队列生成已取消");
			}
			catch (Exception ex2)
			{
				progressDialog.Hide();
				AppLog.Error("queue render failed", ex2);
				await new ContentDialog
				{
					Title = "队列生成失败",
					Content = ex2.Message,
					CloseButtonText = "知道了",
					XamlRoot = Content.XamlRoot
				}.ShowAsync();
			}
		}
		finally
		{
			if (cts != null)
			{
				((IDisposable)cts).Dispose();
			}
		}
		void Refresh()
		{
			PlaylistEntry playlistEntry = list.SelectedItem as PlaylistEntry;
			list.ItemsSource = null;
			list.ItemsSource = entries;
			if (playlistEntry != null && entries.Contains(playlistEntry))
			{
				list.SelectedItem = playlistEntry;
			}
		}
	}

	private static PlaylistEntry CloneEntry(PlaylistEntry entry)
	{
		return new PlaylistEntry
		{
			Path = entry.Path,
			StartSeconds = entry.StartSeconds,
			EndSeconds = entry.EndSeconds
		};
	}

	private async void VideoToWeb_Click(object sender, RoutedEventArgs e)
	{
		if (_kind != WallpaperKind.Web)
		{
			await ShowWrongPageDialogAsync();
			return;
		}
		List<WallpaperItem> list = (from x in _catalog.ByKind(WallpaperKind.Video)
			where File.Exists(x.Path)
			select x).ToList();
		if (list.Count == 0)
		{
			await new ContentDialog
			{
				Title = "没有可转换的视频",
				Content = "请先在“视频”页面添加视频壁纸。",
				CloseButtonText = "知道了",
				XamlRoot = Content.XamlRoot
			}.ShowAsync();
			return;
		}
		ListView list2 = new ListView
		{
			ItemsSource = list,
			DisplayMemberPath = "Title",
			SelectionMode = ListViewSelectionMode.Multiple,
			Height = 320.0
		};
		ContentDialog contentDialog = new ContentDialog();
		contentDialog.Title = "视频转 Web 壁纸";
		contentDialog.Content = new StackPanel
		{
			Spacing = 8.0,
			Children = 
			{
				(UIElement)new TextBlock
				{
					Text = "选择视频后生成 Web 包装。原视频不转码、不缩放，画质不会降低。",
					TextWrapping = TextWrapping.Wrap
				},
				(UIElement)list2
			}
		};
		contentDialog.PrimaryButtonText = "转换";
		contentDialog.CloseButtonText = "取消";
		contentDialog.DefaultButton = ContentDialogButton.Primary;
		contentDialog.XamlRoot = Content.XamlRoot;
		contentDialog.PrimaryButtonClick += (ContentDialog _, ContentDialogButtonClickEventArgs args) =>
		{
			if (list2.SelectedItems.Count == 0)
			{
				args.Cancel = true;
				SetStatus("请至少选择一个视频");
			}
		};
		if (await contentDialog.ShowAsync() != ContentDialogResult.Primary)
		{
			return;
		}
		List<WallpaperItem> list3 = list2.SelectedItems.Cast<WallpaperItem>().ToList();
		string webRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Personal), "Wallerpaper", "Web", "converted");
		try
		{
			AddLibraryRoot(webRoot, WallpaperKind.Web, recursive: true);
			int converted = 0;
			foreach (WallpaperItem item in list3)
			{
				SetStatus("正在转换：" + item.Title);
				await VideoToWebConverter.ConvertAsync(item.Path, webRoot, item.Title);
				converted++;
			}
			ConfigStore.Save(_config);
			await RefreshCatalogAsync();
			_kind = WallpaperKind.Web;
			SelectCategory("Web");
			ApplyFilter();
			SetStatus($"已转换 {converted} 个视频为 Web 壁纸（源文件未转码）");
		}
		catch (Exception ex)
		{
			AppLog.Error("video to web conversion failed", ex);
			SetStatus("视频转 Web 失败：" + ex.Message);
		}
	}

	private async void ConvertToSequence_Click(object sender, RoutedEventArgs e)
	{
		if (_kind != WallpaperKind.Video)
		{
			await ShowWrongPageDialogAsync();
			return;
		}

		string? firstPath = null;
		string? secondPath = null;
		var firstImage = new Image { Width = 420, Height = 236, Stretch = Stretch.Uniform };
		var secondImage = new Image { Width = 420, Height = 236, Stretch = Stretch.Uniform };
		var firstName = new TextBlock { Text = "尚未选择", TextWrapping = TextWrapping.Wrap,
			Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20)) };
		var secondName = new TextBlock { Text = "尚未选择", TextWrapping = TextWrapping.Wrap,
			Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20)) };
		var firstButton = new Button { Content = "选择第一个视频", HorizontalAlignment = HorizontalAlignment.Stretch };
		var secondButton = new Button { Content = "选择第二个视频", HorizontalAlignment = HorizontalAlignment.Stretch };
		var generateButton = new Button
		{
			Content = "生成组合壁纸",
			IsEnabled = false,
			Padding = new Thickness(30, 8, 30, 8),
			Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4)),
			Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
			CornerRadius = new CornerRadius(8)
		};
		void RefreshState()
		{
			generateButton.IsEnabled = !string.IsNullOrWhiteSpace(firstPath) &&
				!string.IsNullOrWhiteSpace(secondPath) && File.Exists(firstPath) && File.Exists(secondPath);
		}

		Grid Framed(UIElement content)
		{
			var frame = new Grid();
			frame.Children.Add(new Border
			{
				Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
				BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x20, 0x20, 0x20)),
				BorderThickness = new Thickness(1),
				CornerRadius = new CornerRadius(14),
				Padding = new Thickness(16),
				Child = content
			});
			frame.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
			{
				Margin = new Thickness(10),
				Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0x20, 0x20, 0x20)),
				StrokeThickness = 2,
				StrokeDashArray = new DoubleCollection { 5, 4 },
				RadiusX = 12,
				RadiusY = 12,
				IsHitTestVisible = false
			});
			return frame;
		}

		UIElement Panel(string header, Image image, TextBlock name, Button button) => Framed(new StackPanel
		{
			Spacing = 10,
			Children =
			{
				(UIElement)new TextBlock { Text = header, FontSize = 20, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
					Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20)) },
				(UIElement)new Border
				{
					CornerRadius = new CornerRadius(10),
					Background = new SolidColorBrush(Color.FromArgb(0x22, 0x70, 0x70, 0x70)),
					Child = image
				},
				(UIElement)name,
				(UIElement)button
			}
		});

		var left = Panel("第一个视频", firstImage, firstName, firstButton);
		var right = Panel("第二个视频", secondImage, secondName, secondButton);
		var divider = new Microsoft.UI.Xaml.Shapes.Line
		{
			X1 = 0.5,
			X2 = 0.5,
			Y1 = 0,
			Y2 = 360,
			Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0x20, 0x20, 0x20)),
			StrokeThickness = 2,
			StrokeDashArray = new DoubleCollection { 5, 4 },
			HorizontalAlignment = HorizontalAlignment.Center
		};
		var body = new Grid
		{
			ColumnSpacing = 18,
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
				new ColumnDefinition { Width = new GridLength(2) },
				new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) }
			},
			Children = { (UIElement)left, (UIElement)divider, (UIElement)right }
		};
		Grid.SetColumn(divider, 1);
		Grid.SetColumn((FrameworkElement)right, 2);

		async Task Choose(bool first)
		{
			var selected = await PickLibraryVideoAsync();
			if (selected is null)
				return;
			var image = first ? firstImage : secondImage;
			var name = first ? firstName : secondName;
			if (first) firstPath = selected.Path; else secondPath = selected.Path;
			name.Text = selected.Title + "\n" + selected.Path;
			image.Source = selected.Thumbnail;
			RefreshState();
		}
		firstButton.Click += async (object _, RoutedEventArgs _) => await Choose(true);
		secondButton.Click += async (object _, RoutedEventArgs _) => await Choose(false);

		var overlay = new Grid
		{
			Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x15, 0x1F))
		};
		Canvas.SetZIndex(overlay, 120);
		Grid.SetRowSpan(overlay, 2);
		var card = new Grid
		{
			Width = 1000,
			Height = 562,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			RowDefinitions =
			{
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
				new RowDefinition { Height = GridLength.Auto }
			}
		};
		card.Children.Add(new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(0xDD, 0xF2, 0xF2, 0xF2)),
			CornerRadius = new CornerRadius(18),
			BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x20, 0x20, 0x20)),
			BorderThickness = new Thickness(1)
		});
		card.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
		{
			Margin = new Thickness(10),
			Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0x20, 0x20, 0x20)),
			StrokeThickness = 2,
			StrokeDashArray = new DoubleCollection { 5, 4 },
			RadiusX = 16,
			RadiusY = 16,
			IsHitTestVisible = false
		});
		var cardContent = new Grid
		{
			Margin = new Thickness(34, 26, 34, 24),
			RowDefinitions =
			{
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
				new RowDefinition { Height = GridLength.Auto }
			}
		};
		cardContent.Children.Add(new TextBlock
		{
			Text = "组合类视频转换",
			FontSize = 28,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20))
		});
		Grid.SetRow(body, 1);
		cardContent.Children.Add(body);
		var footer = new Grid { Margin = new Thickness(0, 14, 0, 0), ColumnSpacing = 12 };
		footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		footer.Children.Add(generateButton);
		var cancel = new Button
		{
			Content = "取消",
			HorizontalAlignment = HorizontalAlignment.Right,
			Padding = new Thickness(30, 8, 30, 8)
		};
		Grid.SetColumn(cancel, 1);
		footer.Children.Add(cancel);
		Grid.SetRow(footer, 2);
		cardContent.Children.Add(footer);
		card.Children.Add(cardContent);
		overlay.Children.Add(card);
		Root.Children.Add(overlay);
		var done = new TaskCompletionSource<bool>();
		generateButton.Click += (object _, RoutedEventArgs _) => done.TrySetResult(true);
		cancel.Click += (object _, RoutedEventArgs _) => done.TrySetResult(false);
		var accepted = await done.Task;
		Root.Children.Remove(overlay);
		if (!accepted || string.IsNullOrWhiteSpace(firstPath) || string.IsNullOrWhiteSpace(secondPath))
			return;

		var firstStored = await ManagedLibraryService.ImportAsync(firstPath);
		var secondStored = await ManagedLibraryService.ImportAsync(secondPath);
		var item = _catalog.AddSequence(firstStored, secondStored);
		ConfigStore.Save(_config);
		await RefreshCatalogAsync();
		_kind = WallpaperKind.Sequence;
		SelectCategory("Sequence");
		ApplyFilter();
		var added = _catalog.Items.FirstOrDefault(x => x.Id == item.Id);
		if (added is not null)
			WallpaperGrid.SelectedItem = added;
		SetStatus($"已生成组合壁纸：{item.Title}");
	}

	private async Task<WallpaperItem?> PickLibraryVideoAsync()
	{
		var videos = _catalog.ByKind(WallpaperKind.Video).Where(x => File.Exists(x.Path)).ToList();
		if (videos.Count == 0)
		{
			await new ContentDialog
			{
				Title = "视频库为空",
				Content = "请先在视频页面添加视频壁纸。",
				CloseButtonText = "知道了",
				XamlRoot = Content.XamlRoot
			}.ShowAsync();
			return null;
		}
		foreach (var video in videos.Where(x => x.Thumbnail is null))
		{
			var thumb = await _thumbnails.GetAsync(video);
			if (!string.IsNullOrWhiteSpace(thumb) && File.Exists(thumb))
				video.Thumbnail = new BitmapImage(new Uri(thumb));
		}

		var grid = new GridView
		{
			ItemsSource = videos,
			SelectionMode = ListViewSelectionMode.Single,
			ItemTemplate = (DataTemplate)Root.Resources["DeleteCardTemplate"],
			ItemsPanel = (ItemsPanelTemplate)Root.Resources["DeleteGridPanel"],
			Width = 850,
			Height = 390,
			IsItemClickEnabled = false
		};
		var choose = new Button
		{
			Content = "选择此视频",
			IsEnabled = false,
			Padding = new Thickness(30, 8, 30, 8),
			Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4)),
			Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
		};
		grid.SelectionChanged += (object _, SelectionChangedEventArgs _) =>
			choose.IsEnabled = grid.SelectedItem is WallpaperItem;
		grid.DoubleTapped += (object _, DoubleTappedRoutedEventArgs _) =>
		{
			if (grid.SelectedItem is WallpaperItem)
			{
				/* handled by the shared result below */
			}
		};

		var overlay = new Grid
		{
			Background = new SolidColorBrush(Color.FromArgb(0xF2, 0x10, 0x15, 0x1F))
		};
		Canvas.SetZIndex(overlay, 150);
		Grid.SetRowSpan(overlay, 2);
		var card = new Grid
		{
			Width = 1000,
			Height = 562,
			HorizontalAlignment = HorizontalAlignment.Center,
			VerticalAlignment = VerticalAlignment.Center,
			RowDefinitions =
			{
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
				new RowDefinition { Height = GridLength.Auto }
			}
		};
		card.Children.Add(new Border
		{
			Background = new SolidColorBrush(Color.FromArgb(0xDD, 0xF2, 0xF2, 0xF2)),
			CornerRadius = new CornerRadius(18),
			BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0x20, 0x20, 0x20)),
			BorderThickness = new Thickness(1)
		});
		card.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
		{
			Margin = new Thickness(10),
			Stroke = new SolidColorBrush(Color.FromArgb(0x99, 0x20, 0x20, 0x20)),
			StrokeThickness = 2,
			StrokeDashArray = new DoubleCollection { 5, 4 },
			RadiusX = 16,
			RadiusY = 16,
			IsHitTestVisible = false
		});
		var content = new Grid
		{
			Margin = new Thickness(34, 26, 34, 24),
			RowDefinitions =
			{
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
				new RowDefinition { Height = GridLength.Auto }
			}
		};
		content.Children.Add(new TextBlock
		{
			Text = "从视频库选择视频",
			FontSize = 26,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0x20, 0x20, 0x20))
		});
		grid.HorizontalAlignment = HorizontalAlignment.Center;
		Grid.SetRow(grid, 1);
		content.Children.Add(grid);
		var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Right };
		var cancel = new Button { Content = "取消", Padding = new Thickness(24, 8, 24, 8) };
		buttons.Children.Add(choose);
		buttons.Children.Add(cancel);
		Grid.SetRow(buttons, 2);
		content.Children.Add(buttons);
		card.Children.Add(content);
		overlay.Children.Add(card);
		Root.Children.Add(overlay);
		var selected = new TaskCompletionSource<WallpaperItem?>();
		choose.Click += (object _, RoutedEventArgs _) =>
			selected.TrySetResult(grid.SelectedItem as WallpaperItem);
		cancel.Click += (object _, RoutedEventArgs _) => selected.TrySetResult(null);
		grid.DoubleTapped += (object _, DoubleTappedRoutedEventArgs _) =>
		{
			if (grid.SelectedItem is WallpaperItem item)
				selected.TrySetResult(item);
		};
		var result = await selected.Task;
		Root.Children.Remove(overlay);
		return result;
	}

	private async void ConvertToMp4_Click(object sender, RoutedEventArgs e)
	{
		var item = _selected ?? WallpaperGrid.SelectedItem as WallpaperItem;
		if (item is null)
		{
			SetStatus("请先选择一个视频或组合壁纸");
			return;
		}
		if (item.Kind is not (WallpaperKind.Video or WallpaperKind.Sequence))
		{
			SetStatus("只有视频或组合壁纸可以转换为 MP4");
			return;
		}
		AppLog.Info($"convert to mp4 requested: {item.Kind} {item.Path}");

		var safeName = new string(item.Title
			.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch)
			.ToArray()).Trim();
		if (string.IsNullOrWhiteSpace(safeName)) safeName = "wallpaper";
		var picker = new FileSavePicker
		{
			SuggestedFileName = safeName + " MP4",
			DefaultFileExtension = ".mp4"
		};
		picker.FileTypeChoices.Add("MP4 视频", new List<string> { ".mp4" });
		InitializeWithWindow.Initialize(picker, WindowNative.GetWindowHandle(this));
		var targetFile = await picker.PickSaveFileAsync();
		if (targetFile is null)
		{
			SetStatus("已取消导出");
			return;
		}
		var exportPath = targetFile.Path;

		var entries = new List<PlaylistEntry>
		{
			new() { Path = item.Path, StartSeconds = 0, EndSeconds = 0 }
		};
		if (item.Kind == WallpaperKind.Sequence && File.Exists(item.SecondPath))
			entries.Add(new PlaylistEntry { Path = item.SecondPath, StartSeconds = 0, EndSeconds = 0 });

		var status = new TextBlock { Text = "准备…", TextWrapping = TextWrapping.Wrap };
		var bar = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
		var dialog = new ContentDialog
		{
			Title = "转换为 MP4",
			Content = new StackPanel
			{
				Spacing = 12,
				Children = { (UIElement)status, (UIElement)bar }
			},
			CloseButtonText = "取消",
			XamlRoot = Content.XamlRoot
		};
		var cts = new CancellationTokenSource();
		dialog.CloseButtonClick += (_, _) => cts.Cancel();
		_ = dialog.ShowAsync();
		try
		{
			var resultPath = await new Mp4ExportService().ExportAsync(
				entries.Select(x => x.Path).ToList(), exportPath, message =>
				{
					DispatcherQueue.TryEnqueue(() => status.Text = message);
				}, cts.Token);
			dialog.Hide();

			// Export is a file operation, not a second library entry.
			SetStatus($"已导出 MP4：{resultPath}");
		}
		catch (OperationCanceledException)
		{
			dialog.Hide();
			SetStatus("MP4 转换已取消");
		}
		catch (Exception ex)
		{
			dialog.Hide();
			AppLog.Error("convert to mp4 failed", ex);
			await new ContentDialog
			{
				Title = "转换失败",
				Content = ex.Message,
				CloseButtonText = "知道了",
				XamlRoot = Content.XamlRoot
			}.ShowAsync();
		}
	}

	private async void AddSequence_Click(object sender, RoutedEventArgs e)
	{
		_ = 3;
		try
		{
			if (_kind != WallpaperKind.Sequence)
			{
				await ShowWrongPageDialogAsync();
				return;
			}
			StorageFile first = await PickVideoAsync("选择第一个视频（播放一遍）");
			if ((object)first != null)
			{
				StorageFile storageFile = await PickVideoAsync("选择第二个视频（之后一直循环）");
				if ((object)storageFile != null)
				{
					var firstStored = await ManagedLibraryService.ImportAsync(first.Path);
					var secondStored = await ManagedLibraryService.ImportAsync(storageFile.Path);
					WallpaperItem item = _catalog.AddSequence(firstStored, secondStored);
					ConfigStore.Save(_config);
					await RefreshCatalogAsync();
					_kind = WallpaperKind.Sequence;
					SelectCategory("Sequence");
					ApplyFilter();
					WallpaperGrid.SelectedItem = item;
					RevealWallpaper(item.Path);
					SetStatus("已添加组合：" + item.Title);
				}
			}
		}
		catch (Exception ex)
		{
			AppLog.Error("add sequence failed", ex);
			SetStatus("添加组合失败：" + ex.Message);
		}
	}

	private async Task<StorageFile?> PickVideoAsync(string title)
	{
		FileOpenPicker fileOpenPicker = new FileOpenPicker();
		string[] array = new string[10] { ".mp4", ".webm", ".mkv", ".mov", ".avi", ".wmv", ".m4v", ".flv", ".ts", ".m2ts" };
		foreach (string item in array)
		{
			fileOpenPicker.FileTypeFilter.Add(item);
		}
		InitializeWithWindow.Initialize(fileOpenPicker, WindowNative.GetWindowHandle(this));
		StorageFile storageFile = await fileOpenPicker.PickSingleFileAsync();
		if ((object)storageFile == null)
		{
			SetStatus(title + "：已取消");
		}
		return storageFile;
	}

	private static WallpaperKind? KindForPath(string path)
	{
		switch (Path.GetExtension(path).ToLowerInvariant())
		{
		case ".mp4":
		case ".mkv":
		case ".mov":
		case ".avi":
		case ".wmv":
		case ".m4v":
		case ".flv":
		case ".m2ts":
		case ".webm":
		case ".ts":
			return WallpaperKind.Video;
		case ".png":
		case ".jpg":
		case ".jpeg":
		case ".webp":
		case ".bmp":
		case ".gif":
			return WallpaperKind.Static;
		case ".htm":
		case ".html":
			return WallpaperKind.Web;
		case ".json":
			return WallpaperKind.Scene;
		default:
			return null;
		}
	}

	private async Task ShowWrongPageDialogAsync()
	{
		SetStatus("请在正确的页面添加");
		await new ContentDialog
		{
			Title = "分类不匹配",
			Content = "请在正确的页面添加",
			CloseButtonText = "知道了",
			XamlRoot = Content.XamlRoot
		}.ShowAsync();
	}

	private bool AddLibraryRoot(string path, WallpaperKind kind, bool recursive)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return false;
		}
		string full = Path.GetFullPath(path);
		if (Directory.Exists(full))
		{
			string prefix = full.TrimEnd('\\') + "\\";
			_config.ExcludedPaths.RemoveAll((string x) => x.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || string.Equals(x, full, StringComparison.OrdinalIgnoreCase));
		}
		else
		{
			_config.ExcludedPaths.RemoveAll((string x) => string.Equals(x, full, StringComparison.OrdinalIgnoreCase));
		}
		if (_config.Library.Any((LibraryRoot x) => string.Equals(Path.GetFullPath(x.Path), full, StringComparison.OrdinalIgnoreCase) && x.Kind == kind))
		{
			return false;
		}
		_config.Library.Add(new LibraryRoot
		{
			Path = full,
			Kind = kind,
			Recursive = recursive
		});
		return true;
	}

	private void SelectCategory(string tag)
	{
		NavigationViewItem navigationViewItem = Nav.MenuItems.OfType<NavigationViewItem>().FirstOrDefault((NavigationViewItem x) => string.Equals(x.Tag as string, tag, StringComparison.OrdinalIgnoreCase));
		if ((object)navigationViewItem != null)
		{
			Nav.SelectedItem = navigationViewItem;
		}
	}

	private async Task AddVideoFileAsync(string path)
	{
		if (_kind != WallpaperKind.Video)
		{
			await ShowWrongPageDialogAsync();
			return;
		}
		if (!File.Exists(path))
		{
			SetStatus("视频不存在：" + path);
			return;
		}
		var storedPath = await ManagedLibraryService.ImportAsync(path);
		AddLibraryRoot(storedPath, WallpaperKind.Video, recursive: false);
		ConfigStore.Save(_config);
		_kind = WallpaperKind.Video;
		SelectCategory("Video");
		await RefreshCatalogAsync();
		ApplyFilter();
		WallpaperItem wallpaperItem = _catalog.Items.FirstOrDefault((WallpaperItem x) => string.Equals(x.Path, storedPath, StringComparison.OrdinalIgnoreCase));
		if (wallpaperItem != null)
		{
			WallpaperGrid.SelectedItem = wallpaperItem;
			RevealWallpaper(storedPath);
			SetStatus("已添加壁纸：" + wallpaperItem.Title);
		}
		else
		{
			SetStatus("已加入库，但目录扫描未返回：" + path);
		}
	}

	private void WallpaperGrid_DragOver(object sender, DragEventArgs e)
	{
		if (e.DataView.Contains(StandardDataFormats.StorageItems))
		{
			e.AcceptedOperation = DataPackageOperation.Copy;
			e.DragUIOverride.Caption = $"添加到{_kind}分类";
			e.DragUIOverride.IsCaptionVisible = true;
		}
	}

	private async void WallpaperGrid_Drop(object sender, DragEventArgs e)
	{
		if (!e.DataView.Contains(StandardDataFormats.StorageItems))
		{
			return;
		}
		IReadOnlyList<IStorageItem> readOnlyList = await e.DataView.GetStorageItemsAsync();
		if (_kind == WallpaperKind.Sequence)
		{
			await ShowWrongPageDialogAsync();
			return;
		}
		foreach (IStorageItem item in readOnlyList)
		{
			if (item is StorageFile storageFile && KindForPath(storageFile.Path) != _kind)
			{
				await ShowWrongPageDialogAsync();
				return;
			}
		}
		int added = 0;
		string? lastStored = null;
		foreach (IStorageItem item2 in readOnlyList)
		{
			if (item2 is StorageFolder storageFolder)
			{
				if (AddLibraryRoot(storageFolder.Path, _kind, recursive: true))
				{
					added++;
				}
			}
			else if (item2 is StorageFile storageFile2)
			{
				var stored = _kind == WallpaperKind.Video
					? await ManagedLibraryService.ImportAsync(storageFile2.Path)
					: storageFile2.Path;
				if (AddLibraryRoot(stored, _kind, recursive: false))
				{
					added++;
					lastStored = stored;
				}
			}
		}
		if (added != 0)
		{
			ConfigStore.Save(_config);
			await RefreshCatalogAsync();
			ApplyFilter();
			RevealWallpaper(lastStored);
			SetStatus($"已拖入 {added} 个壁纸来源");
		}
	}

	private async void Refresh_Click(object sender, RoutedEventArgs e)
	{
		await RefreshCatalogAsync();
	}

	private async void RebuildThumb_Click(object sender, RoutedEventArgs e)
	{
		_thumbnails.Clear();
		await RefreshCatalogAsync();
		SetStatus("缩略图已重新生成");
	}

	private void WallpaperGrid_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (WallpaperGrid.SelectedItem is WallpaperItem wallpaperItem)
		{
			_selected = wallpaperItem;
			ApplyButton.IsEnabled = true;
			PreviewButton.IsEnabled = true;
			ApplyToMonitorButton.IsEnabled = true;
			OpenDetailButton.IsEnabled = true;
			DeleteSelectedButton.IsEnabled = true;
			ShowProperties(wallpaperItem);
			SettingsViewControl.SetPreview(wallpaperItem);
		}
		else
		{
			ClearSelection();
		}
	}

	private void WallpaperImage_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not WallpaperItem item)
			return;
		_selected = item;
		WallpaperGrid.SelectedItem = item;
		ApplySelectedToTarget();
		e.Handled = true;
	}

	private void WallpaperTitle_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if ((sender as FrameworkElement)?.DataContext is not WallpaperItem item)
			return;
		_selected = item;
		WallpaperGrid.SelectedItem = item;
		Rename_Click(sender, new RoutedEventArgs());
		e.Handled = true;
	}

	private void WallpaperGrid_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
	{
		if (WallpaperGrid.SelectedItem is WallpaperItem item)
		{
			_selected = item;
			ApplySelectedToTarget();
		}
	}

	private void WallpaperGrid_ItemClick(object sender, ItemClickEventArgs e)
	{
		// Single click only selects. It must not restart the live wallpaper;
		// double click is the explicit apply gesture.
		if (e.ClickedItem is WallpaperItem selectedItem)
			WallpaperGrid.SelectedItem = selectedItem;
	}

	private void WallpaperGrid_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
	{
		var viewer = FindScrollViewer(WallpaperGrid);
		if (viewer is null || viewer.ScrollableHeight <= 0)
			return;
		var delta = e.GetCurrentPoint(WallpaperGrid).Properties.MouseWheelDelta;
		viewer.ChangeView(null, viewer.VerticalOffset - delta, null, true);
		e.Handled = true;
	}

	private static ScrollViewer? FindScrollViewer(DependencyObject root)
	{
		for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
		{
			var child = VisualTreeHelper.GetChild(root, i);
			if (child is ScrollViewer viewer) return viewer;
			var nested = FindScrollViewer(child);
			if (nested is not null) return nested;
		}
		return null;
	}

	private void ShowProperties(WallpaperItem item)
	{
		PropertyTitle.Text = ((item.Kind == WallpaperKind.Sequence) ? (item.Title + "\n" + item.Path + "\n↓\n" + item.SecondPath) : (item.Title + "\n" + item.Path));
		DetailTitleText.Text = item.Title;
		TextBlock detailKindText = DetailKindText;
		detailKindText.Text = item.Kind switch
		{
			WallpaperKind.Video => "动态壁纸", 
			WallpaperKind.Static => "静态壁纸",
			WallpaperKind.Web => "Web 壁纸", 
			WallpaperKind.Scene => "场景壁纸", 
			WallpaperKind.Sequence => "组合壁纸 · 第一段播放一遍，第二段循环", 
			_ => "壁纸", 
		};
		DetailPathText.Text = ((item.Kind == WallpaperKind.Sequence) ? (item.Path + "\n↓\n" + item.SecondPath) : item.Path);
		RenameButton.IsEnabled = true;
		DeleteButton.IsEnabled = true;
		bool flag = item.Kind == WallpaperKind.Video;
		bool flag2 = item.Kind == WallpaperKind.Web;
		bool flag3 = item.Kind == WallpaperKind.Scene;
		bool flag4 = item.Kind == WallpaperKind.Sequence;
		bool flag6 = item.Kind == WallpaperKind.Static;
		bool flag5 = flag | flag4;
		VideoInfoPanel.Visibility = ((!flag) ? Visibility.Collapsed : Visibility.Visible);
		StaticInfoPanel.Visibility = ((!flag6) ? Visibility.Collapsed : Visibility.Visible);
		WebInfoPanel.Visibility = ((!flag2) ? Visibility.Collapsed : Visibility.Visible);
		SceneInfoPanel.Visibility = ((!flag3) ? Visibility.Collapsed : Visibility.Visible);
		SequenceInfoPanel.Visibility = ((!flag4) ? Visibility.Collapsed : Visibility.Visible);
		SequencePathText.Text = (flag4 ? (item.Path + "\n↓\n" + item.SecondPath) : string.Empty);
		AiPanel.Visibility = ((!flag5) ? Visibility.Collapsed : Visibility.Visible);
		FpsPanel.Visibility = ((!flag5) ? Visibility.Collapsed : Visibility.Visible);
		SpeedPanel.Visibility = ((!(flag5 | flag2)) ? Visibility.Collapsed : Visibility.Visible);
		VolumePanel.Visibility = (flag3 ? Visibility.Collapsed : Visibility.Visible);
		ShaderPanel.Visibility = ((!flag3) ? Visibility.Collapsed : Visibility.Visible);
		AiInterpolateButton.IsEnabled = flag5;
		SuperResolutionButton.IsEnabled = flag5;
		TrimVideoButton.IsEnabled = flag;
		CropVideoButton.IsEnabled = flag;
		_fitUi = true;
		foreach (ComboBoxItem item2 in VideoFitCombo.Items.OfType<ComboBoxItem>())
		{
			if (string.Equals(item2.Tag?.ToString(), item.Fit, StringComparison.OrdinalIgnoreCase))
			{
				VideoFitCombo.SelectedItem = item2;
				break;
			}
		}
		_fitUi = false;
		_webZoomUi = true;
		WebZoomSlider.Value = item.Zoom;
		WebZoomValue.Text = $"{item.Zoom * 100.0:F0}%";
		_webZoomUi = false;
		_sceneQualityUi = true;
		string b = item.RenderScale.ToString("0.##", CultureInfo.InvariantCulture);
		foreach (ComboBoxItem item3 in SceneQualityCombo.Items.OfType<ComboBoxItem>())
		{
			if (string.Equals(item3.Tag?.ToString(), b, StringComparison.Ordinal))
			{
				SceneQualityCombo.SelectedItem = item3;
				break;
			}
		}
		_sceneQualityUi = false;
		VolumeSlider.Value = item.Volume;
		SpeedSlider.Value = item.Speed;
		ShaderSlider.Value = (item.ShaderParameters.TryGetValue("intensity", out var value) ? value : 1.0);
		VolumeSlider.IsEnabled = item.Kind != WallpaperKind.Scene;
		MuteToggle.IsEnabled = item.Kind != WallpaperKind.Scene;
		ShaderSlider.IsEnabled = item.Kind == WallpaperKind.Scene;
		VolumeValue.Text = $"{item.Volume:F0}";
		SpeedValue.Text = $"{item.Speed:F1}×";
		ShaderValue.Text = $"{ShaderSlider.Value:F2}";
		MuteToggle.IsOn = item.Muted;
		_fpsUi = true;
		SourceFpsText.Text = ((item.SourceFps > 0.1) ? $"{item.SourceFps:0.##} fps" : "播放时检测");
		ComboBox targetFpsCombo = TargetFpsCombo;
		WallpaperKind kind = item.Kind;
		bool isEnabled = ((kind == WallpaperKind.Video || kind == WallpaperKind.Sequence) ? true : false);
		targetFpsCombo.IsEnabled = isEnabled;
		string b2 = item.TargetFps.ToString();
		foreach (ComboBoxItem item4 in TargetFpsCombo.Items.OfType<ComboBoxItem>())
		{
			if (string.Equals(item4.Tag?.ToString(), b2, StringComparison.Ordinal))
			{
				TargetFpsCombo.SelectedItem = item4;
				break;
			}
		}
		UpdateFpsHint(item);
		_fpsUi = false;
		if (_monitors.Count > 0)
		{
			string target = _config.Monitors.FirstOrDefault((KeyValuePair<string, MonitorAssignment> x) => x.Value.WallpaperId == item.Id).Key;
			int num = _monitors.FindIndex((MonitorDisplay x) => x.Device == target);
			TargetMonitorCombo.SelectedIndex = ((num >= 0) ? num : 0);
		}
	}

	private void UpdateFpsHint(WallpaperItem item)
	{
		int num = ((item.TargetFps > 0) ? item.TargetFps : _config.Performance.AcFps);
		TextBlock fpsHintText = FpsHintText;
		string text;
		if (item.SourceFps > 0.1)
		{
			text = (((double)num > item.SourceFps) ? $"源 {item.SourceFps:0.##} fps → 输出 {num} fps（mpv 帧复制补帧，不生成新运动信息）" : $"源 {item.SourceFps:0.##} fps → 输出 {num} fps");
		}
		else
		{
			text = "播放时检测源帧率；提高输出帧率是帧复制，不是 AI 插帧";
		}
		fpsHintText.Text = text;
	}

	private void OnFpsReported(double sourceFps, int targetFps)
	{
		DispatcherQueue.TryEnqueue(() =>
		{
			WallpaperItem selected = _selected;
			bool flag = selected == null;
			if (!flag)
			{
				WallpaperKind kind = selected.Kind;
				bool flag2 = ((kind == WallpaperKind.Video || kind == WallpaperKind.Sequence) ? true : false);
				flag = !flag2;
			}
			if (!flag && !(sourceFps <= 0.1))
			{
				selected.SourceFps = sourceFps;
				Persist();
				if (selected == _selected)
				{
					_fpsUi = true;
					SourceFpsText.Text = $"{sourceFps:0.##} fps";
					UpdateFpsHint(selected);
					_fpsUi = false;
				}
			}
		});
	}

	private void VideoFitCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_fitUi)
		{
			return;
		}
		WallpaperItem selected = _selected;
		if (selected != null && VideoFitCombo.SelectedItem is ComboBoxItem comboBoxItem)
		{
			var fit = comboBoxItem.Tag?.ToString();
			selected.Fit = fit is "contain" or "blur" ? fit : "cover";
			Persist();
			if (_host.IsRunning)
			{
				_host.Restart();
			}
		}
	}

	private void WebZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
	{
		WebZoomValue.Text = $"{WebZoomSlider.Value * 100.0:F0}%";
		if (!_webZoomUi)
		{
			WallpaperItem selected = _selected;
			if (selected != null)
			{
				selected.Zoom = Math.Clamp(WebZoomSlider.Value, 0.5, 2.0);
			}
		}
	}

	private async void ApplyWebZoom_Click(object sender, RoutedEventArgs e)
	{
		WallpaperItem selected = _selected;
		if (selected != null)
		{
			selected.Zoom = Math.Clamp(WebZoomSlider.Value, 0.5, 2.0);
			Persist();
			if (_host.IsRunning)
			{
				_host.Restart();
			}
			SetStatus($"页面缩放已设为 {selected.Zoom * 100.0:F0}%");
			await Task.CompletedTask;
		}
	}

	private async void AdvancedFeatures_Click(object sender, RoutedEventArgs e)
	{
		string text = "已加入/已有\r\n──────────\r\n• 场景音频反应：SceneWallpaperHost 已通过 WASAPI loopback + FFT 驱动 Shader 和 JS。\r\n• Web 崩溃：父进程 watchdog 会重启 WebWallpaperHost；页面级错误页尚未加入。\r\n• 视频画质：Lanczos 缩放、画面填充 Cover/Contain。\r\n• Web：页面缩放、刷新、播放速度。\r\n• 场景：渲染质量 0.5×–2×。\r\n\r\n需要你操作后才能继续\r\n──────────\r\n1. HDR / 10bit\r\n   需要 HDR 显示器，并在 Windows 设置 → 系统 → 显示 → HDR 中开启 HDR。\r\n   还需要提供一段 HDR 视频用于验证。\r\n\r\n2. 视频超分辨率\r\n   动漫视频模型已安装到：\r\n   D:\\Desktop\\wallerpaper c\\REAL-ERGAN\r\n   当前可用：realesr-animevideov3（2×/4×）、realesrgan-x4plus-anime（4×）\r\n   和 realesrgan-x4plus（实拍通用，4×）。\r\n\r\n3. 组合交叉淡化\r\n   有两种路线，需要你选：\r\n   A. 实时双播放器：两个解码器 + 纹理混合，CPU/GPU 占用更高。\r\n   B. 离线生成：用 FFmpeg 生成一个带交叉淡化和音频过渡的视频；质量高，但会重新编码。\r\n\r\n4. 视频裁剪 / 播放列表\r\n   需要确定交互方式：时间轴裁剪，还是只设置起止时间；单视频循环还是多视频队列。\r\n\r\n暂未实现的原因\r\n──────────\r\n• 场景视频纹理：需要视频解码到 D3D11 纹理并接入 SceneRenderer。\r\n• 场景 3D / 深度视差：当前是 2D 图层渲染器。\r\n• 场景编辑器：需要独立的图层树、属性编辑、预览和保存格式。\r\n• 真正的运行时交叉淡化：当前组合是单 libmpv 实例，硬切；需要双解码或预渲染。\r\n• 无缝循环点：当前 loop-file 已开启，但关键帧/GOP 接缝仍需预滚或双实例。";
		await new ContentDialog
		{
			Title = "高级功能与前提",
			Content = new ScrollViewer
			{
				MaxHeight = 460.0,
				Content = new TextBlock
				{
					Text = text,
					TextWrapping = TextWrapping.Wrap,
					FontSize = 12.0
				}
			},
			CloseButtonText = "知道了",
			XamlRoot = Content.XamlRoot
		}.ShowAsync();
	}

	private async Task<int?> ShowAutoCropChooserAsync(
		IReadOnlyList<AutoCropCandidate> candidates)
	{
		var result = new TaskCompletionSource<int?>(TaskCreationOptions.RunContinuationsAsynchronously);
		var cards = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 14
		};
		var dialog = new ContentDialog
		{
			Title = "检测到多个画面",
			Content = new StackPanel
			{
				Spacing = 12,
				Children =
				{
					(UIElement)new TextBlock
					{
						Text = "这个视频里有两个独立场景。请选择接下来要去黑边并输出的场景。",
						TextWrapping = TextWrapping.Wrap,
						MaxWidth = 700
					},
					(UIElement)new ScrollViewer
					{
						Content = cards,
						HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
						HorizontalScrollMode = ScrollMode.Enabled,
						MaxWidth = 760
					}
				}
			},
			CloseButtonText = "取消",
			XamlRoot = Content.XamlRoot
		};
		foreach (var candidate in candidates)
		{
			var image = new Image
			{
				Width = 280,
				Height = 158,
				Stretch = Stretch.UniformToFill
			};
			if (File.Exists(candidate.ThumbnailPath))
				image.Source = new BitmapImage(new Uri(candidate.ThumbnailPath));
			var button = new Button
			{
				Content = $"选择场景 {candidate.Index + 1}",
				HorizontalAlignment = HorizontalAlignment.Stretch,
				Padding = new Thickness(12, 6, 12, 6)
			};
			button.Click += (_, _) =>
			{
				result.TrySetResult(candidate.Index);
				dialog.Hide();
			};
			cards.Children.Add(new Border
			{
				CornerRadius = new CornerRadius(10),
				Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
				Padding = new Thickness(10),
				Child = new StackPanel
				{
					Spacing = 8,
					Children =
					{
						(UIElement)new Border
						{
							CornerRadius = new CornerRadius(8),
							Child = image
						},
						(UIElement)new TextBlock
						{
							Text = $"{candidate.Width} × {candidate.Height}",
							FontSize = 11,
							Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
						},
						(UIElement)button
					}
				}
			});
		}
		await dialog.ShowAsync();
		result.TrySetResult(null);
		return await result.Task;
	}

	private async void AutoCrop_Click(object sender, RoutedEventArgs e)
	{
		WallpaperItem? item = _selected;
		if (item is null || item.Kind != WallpaperKind.Video)
			return;

		var status = new TextBlock { Text = "准备…", TextWrapping = TextWrapping.Wrap };
		var progress = new ProgressBar { Minimum = 0, Maximum = 100, IsIndeterminate = true };
		var dialog = new ContentDialog
		{
			Title = "自动去黑边",
			Content = new StackPanel
			{
				Spacing = 12,
				Children = { (UIElement)status, (UIElement)progress }
			},
			CloseButtonText = "取消",
			XamlRoot = Content.XamlRoot
		};
		var cts = new CancellationTokenSource();
		dialog.CloseButtonClick += (_, _) => cts.Cancel();
		_ = dialog.ShowAsync();
		try
		{
			async Task<int?> ChooseSceneAsync(IReadOnlyList<AutoCropCandidate> candidates)
			{
				dialog.Hide();
				await Task.Delay(120);
				var choice = await ShowAutoCropChooserAsync(candidates);
				if (choice is not null && !cts.IsCancellationRequested)
				{
					_ = dialog.ShowAsync();
					await Task.Delay(120);
				}
				return choice;
			}
			var result = await new AutoBorderCropService().RunAsync(item.Path, message =>
			{
				DispatcherQueue.TryEnqueue(() => status.Text = message);
			}, ChooseSceneAsync, cts.Token);
			dialog.Hide();

			AddLibraryRoot(result.OutputPath, WallpaperKind.Video, recursive: false);
			ConfigStore.Save(_config);
			await RefreshCatalogAsync();
			var added = _catalog.Items.FirstOrDefault(x =>
				string.Equals(x.Path, result.OutputPath, StringComparison.OrdinalIgnoreCase));
			if (added is not null)
			{
				added.Title = item.Title + " 去黑边";
				// Keep the cropped picture sharp; blur-fill showed soft bars
				// beside a portrait/narrow crop.
				added.Fit = "cover";
				TitleOverrideStore.Set(added.Path, added.Title, added.Kind);
				ConfigStore.Save(_config);
			}
			SearchBox.Text = string.Empty;
			ApplyFilter();
			RevealWallpaper(result.OutputPath);
			SetStatus($"已去除黑边：{result.Width}×{result.Height}");
		}
		catch (OperationCanceledException)
		{
			dialog.Hide();
			SetStatus("自动去黑边已取消");
		}
		catch (Exception ex)
		{
			dialog.Hide();
			AppLog.Error("auto border crop failed", ex);
			await new ContentDialog
			{
				Title = "自动去黑边失败",
				Content = ex.Message,
				CloseButtonText = "知道了",
				XamlRoot = Content.XamlRoot
			}.ShowAsync();
		}
	}

	private async void CropVideo_Click(object sender, RoutedEventArgs e)
	{
		if ((DateTime.UtcNow - _cropEditorClosedAt).TotalMilliseconds < 1000)
			return;
		WallpaperItem item = _selected;
		if (item == null || item.Kind != WallpaperKind.Video)
		{
			return;
		}
		CropVideoButton.IsEnabled = false;
		BitmapImage bitmap;
		string framePath;
		int pixelWidth;
		int pixelHeight;
		try
		{
			framePath = await VideoCropService.ExtractFirstFrameAsync(item.Path);
			using (System.Drawing.Image probe = System.Drawing.Image.FromFile(framePath))
			{
				pixelWidth = probe.Width;
				pixelHeight = probe.Height;
			}
			if (pixelWidth <= 0 || pixelHeight <= 0)
			{
				throw new InvalidOperationException("无法读取视频第一帧尺寸。");
			}
			bitmap = new BitmapImage(new Uri(framePath));
		}
		catch (Exception ex)
		{
			await new ContentDialog
			{
				Title = "无法打开裁剪界面",
				Content = ex.Message,
				CloseButtonText = "知道了",
				XamlRoot = Content.XamlRoot
			}.ShowAsync();
			CropVideoButton.IsEnabled = true;
			return;
		}
		double num = 770.0;
		double num2 = 432.0;
		// Fit the actual source frame into the fixed canvas. The previous code
		// stretched the displayed frame to the whole 16:9 canvas, so the box
		// coordinates did not match the real video pixels (especially for
		// portrait/ultrawide sources).
		double displayScale = Math.Min(num / Math.Max(1, pixelWidth),
			num2 / Math.Max(1, pixelHeight));
		double displayWidth = Math.Max(1, pixelWidth * displayScale);
		double displayHeight = Math.Max(1, pixelHeight * displayScale);
		double displayX = (num - displayWidth) / 2.0;
		double displayY = (num2 - displayHeight) / 2.0;
		var targetMonitor = TargetMonitorCombo.SelectedIndex >= 0 &&
		                    TargetMonitorCombo.SelectedIndex < _monitors.Count
			? _monitors[TargetMonitorCombo.SelectedIndex]
			: _monitors.FirstOrDefault();
		var monitorAspect = targetMonitor is { Width: > 0, Height: > 0 }
			? (double)targetMonitor.Width / targetMonitor.Height
			: 16.0 / 10.0;
		// Start in free mode. The user's drawn rectangle is the source of truth;
		// aspect buttons are opt-in.
		double currentAspect = 0;
		Canvas canvas = new Canvas
		{
			Width = num,
			Height = num2,
			Background = new SolidColorBrush(Color.FromArgb(byte.MaxValue, 16, 21, 31))
		};
		Image image = new Image
		{
			Source = bitmap,
			Width = displayWidth,
			Height = displayHeight,
			Stretch = Stretch.Fill
		};
		Canvas.SetLeft(image, displayX);
		Canvas.SetTop(image, displayY);
		canvas.Children.Add(image);
		Border selection = new Border
		{
			BorderBrush = new SolidColorBrush(Color.FromArgb(byte.MaxValue, 0, 120, 212)),
			BorderThickness = new Thickness(2.0),
			Background = new SolidColorBrush(Color.FromArgb(34, 0, 120, 212)),
			IsHitTestVisible = false
		};
		canvas.Children.Add(selection);
		double selectionWidth;
		double selectionHeight;
		if (currentAspect <= 0)
		{
			// Free mode starts without a pre-made full-frame selection. The
			// first drag on the picture creates exactly the box the user draws.
			selectionWidth = 0;
			selectionHeight = 0;
		}
		else if (displayWidth / displayHeight > currentAspect)
		{
			selectionHeight = displayHeight;
			selectionWidth = selectionHeight * currentAspect;
		}
		else
		{
			selectionWidth = displayWidth;
			selectionHeight = selectionWidth / currentAspect;
		}
		double selectionLeft = selectionWidth <= 0
			? displayX
			: displayX + (displayWidth - selectionWidth) / 2.0;
		double selectionTop = selectionHeight <= 0
			? displayY
			: displayY + (displayHeight - selectionHeight) / 2.0;
		double minSize = 36.0;
		Thumb topLeft = null;
		Thumb topRight = null;
		Thumb bottomLeft = null;
		Thumb bottomRight = null;
		string? previewVideoPath = null;
		Thumb move = new Thumb
		{
			Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0)),
			Opacity = 0.01
		};
		move.DragDelta += (object _, DragDeltaEventArgs e2) =>
		{
			selectionLeft += e2.HorizontalChange;
			selectionTop += e2.VerticalChange;
			previewVideoPath = null;
			ApplySelection();
			PlaceHandles();
		};
		canvas.Children.Add(move);
		topLeft = Corner(left: true, top: true);
		topRight = Corner(left: false, top: true);
		bottomLeft = Corner(left: true, top: false);
		bottomRight = Corner(left: false, top: false);
		ApplySelection();
		ApplySelection();
		PlaceHandles();

		var ratioBar = new StackPanel
		{
			Orientation = Orientation.Horizontal,
			Spacing = 8,
			Margin = new Thickness(0, 0, 0, 10)
		};
		void SetAspect(double aspect)
		{
			previewVideoPath = null;
			if (aspect <= 0)
			{
				currentAspect = 0;
				return;
			}
			currentAspect = aspect;
			var maxW = displayWidth;
			var maxH = displayHeight;
			var w = Math.Min(selectionWidth, maxW);
			var h = w / currentAspect;
			if (h > maxH)
			{
				h = maxH;
				w = h * currentAspect;
			}
			w = Math.Max(minSize, w);
			h = Math.Max(minSize, h);
			var centerX = selectionLeft + selectionWidth / 2;
			var centerY = selectionTop + selectionHeight / 2;
			selectionWidth = w;
			selectionHeight = h;
			selectionLeft = Math.Clamp(centerX - w / 2, displayX,
				displayX + displayWidth - w);
			selectionTop = Math.Clamp(centerY - h / 2, displayY,
				displayY + displayHeight - h);
			ApplySelection();
			PlaceHandles();
		}
		void ResetSelection()
		{
			previewVideoPath = null;
			if (currentAspect <= 0)
			{
				selectionLeft = displayX;
				selectionTop = displayY;
				selectionWidth = displayWidth;
				selectionHeight = displayHeight;
			}
			else if (displayWidth / displayHeight > currentAspect)
			{
				selectionHeight = displayHeight;
				selectionWidth = selectionHeight * currentAspect;
				selectionLeft = displayX + (displayWidth - selectionWidth) / 2;
				selectionTop = displayY;
			}
			else
			{
				selectionWidth = displayWidth;
				selectionHeight = selectionWidth / currentAspect;
				selectionLeft = displayX;
				selectionTop = displayY + (displayHeight - selectionHeight) / 2;
			}
			ApplySelection();
			PlaceHandles();
		}

		void AddRatioButton(string label, double aspect)
		{
			var button = new Button
			{
				Content = label,
				Padding = new Thickness(14, 5, 14, 5)
			};
			button.Click += (object _, RoutedEventArgs _) => SetAspect(aspect);
			ratioBar.Children.Add(button);
		}
		AddRatioButton("自由", 0);
		AddRatioButton("16:9", 16.0 / 9.0);
		AddRatioButton("21:9", 21.0 / 9.0);
		AddRatioButton("4:3", 4.0 / 3.0);
		AddRatioButton("适配显示器", monitorAspect);
		var reselectButton = new Button
		{
			Content = "重新框选",
			Padding = new Thickness(14, 5, 14, 5)
		};
		void ClearSelectionBox()
		{
			previewVideoPath = null;
			currentAspect = 0;
			selectionLeft = displayX;
			selectionTop = displayY;
			selectionWidth = 0;
			selectionHeight = 0;
			ApplySelection();
			PlaceHandles();
		}
		reselectButton.Click += (_, _) => ClearSelectionBox();
		ratioBar.Children.Add(reselectButton);
		var contentButton = new Button
		{
			Content = "框选内容",
			Padding = new Thickness(14, 5, 14, 5)
		};
		contentButton.Click += (_, _) =>
		{
			try
			{
				using var full = new System.Drawing.Bitmap(framePath);
				var cols = new int[(full.Width + 1) / 2];
				var rows = new int[(full.Height + 1) / 2];
				for (var y = 0; y < full.Height; y += 2)
				{
					for (var x = 0; x < full.Width; x += 2)
					{
						var c = full.GetPixel(x, y);
						if (Math.Max(c.R, Math.Max(c.G, c.B)) <= 24) continue;
						cols[x / 2]++;
						rows[y / 2]++;
					}
				}
				var minCol = Math.Max(2, rows.Length / 50);
				var minRow = Math.Max(2, cols.Length / 50);
				var xs = Enumerable.Range(0, cols.Length)
					.Where(i => cols[i] >= minCol).ToList();
				var ys = Enumerable.Range(0, rows.Length)
					.Where(i => rows[i] >= minRow).ToList();
				if (xs.Count == 0 || ys.Count == 0) return;
				var minX = xs[0] * 2;
				var minY = ys[0] * 2;
				var maxX = Math.Min(full.Width, xs[^1] * 2 + 2);
				var maxY = Math.Min(full.Height, ys[^1] * 2 + 2);
				var sx = displayWidth / pixelWidth;
				var sy = displayHeight / pixelHeight;
				currentAspect = 0;
				selectionLeft = displayX + minX * sx;
				selectionTop = displayY + minY * sy;
				selectionWidth = Math.Max(minSize, (maxX - minX) * sx);
				selectionHeight = Math.Max(minSize, (maxY - minY) * sy);
				ApplySelection();
				PlaceHandles();
			}
			catch { }
		};
		ratioBar.Children.Add(contentButton);
		var resetButton = new Button
		{
			Content = "重置",
			Padding = new Thickness(14, 5, 14, 5)
		};
		resetButton.Click += (object _, RoutedEventArgs _) => ResetSelection();
		ratioBar.Children.Add(resetButton);

		var drawing = false;
		var drawStart = new Windows.Foundation.Point();
		canvas.PointerPressed += (object _, PointerRoutedEventArgs e) =>
		{
			var p = e.GetCurrentPoint(canvas).Position;
			var inside = p.X >= selectionLeft && p.X <= selectionLeft + selectionWidth &&
			             p.Y >= selectionTop && p.Y <= selectionTop + selectionHeight;
			if (inside)
				return;
			drawing = true;
			drawStart = p;
			selectionLeft = p.X;
			selectionTop = p.Y;
			selectionWidth = 1;
			selectionHeight = 1;
			canvas.CapturePointer(e.Pointer);
			ApplySelection();
			PlaceHandles();
			e.Handled = true;
		};
		canvas.PointerMoved += (object _, PointerRoutedEventArgs e) =>
		{
			if (drawing)
			{
				previewVideoPath = null;
				var p = e.GetCurrentPoint(canvas).Position;
				selectionLeft = Math.Min(drawStart.X, p.X);
				selectionTop = Math.Min(drawStart.Y, p.Y);
				selectionWidth = Math.Abs(p.X - drawStart.X);
				selectionHeight = Math.Abs(p.Y - drawStart.Y);
				if (currentAspect > 0 && selectionWidth > 2 && selectionHeight > 2)
				{
					if (selectionWidth / selectionHeight > currentAspect)
						selectionWidth = selectionHeight * currentAspect;
					else
						selectionHeight = selectionWidth / currentAspect;
				}
				ApplySelection();
				PlaceHandles();
			}
			else
			{
				PlaceHandles();
			}
		};
		canvas.PointerReleased += (object _, PointerRoutedEventArgs e) =>
		{
			if (drawing)
			{
				drawing = false;
				canvas.ReleasePointerCapture(e.Pointer);
				if (selectionWidth < minSize || selectionHeight < minSize)
					ResetSelection();
				else
				{
					ApplySelection();
					PlaceHandles();
				}
				e.Handled = true;
			}
			else
			{
				PlaceHandles();
			}
		};
		var previewMedia = new MediaPlayer { IsLoopingEnabled = true, AutoPlay = true };
		var previewPlayer = new MediaPlayerElement
		{
			Width = 240,
			Height = 180,
			Stretch = Stretch.Uniform,
			AreTransportControlsEnabled = true,
			AutoPlay = true,
			HorizontalAlignment = HorizontalAlignment.Center
		};
		previewPlayer.SetMediaPlayer(previewMedia);
		TextBlock previewStatus = new TextBlock
		{
			Text = "点击“预览裁剪”生成并完整播放裁剪后的视频。",
			TextWrapping = TextWrapping.Wrap,
			FontSize = 11,
			Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
		};
		Button previewButton = new Button
		{
			Content = "预览裁剪视频",
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		var previewProgress = new ProgressBar
		{
			Minimum = 0,
			Maximum = 100,
			IsIndeterminate = false,
			Visibility = Visibility.Collapsed
		};
		Button fullscreenPreviewButton = new Button
		{
			Content = "全屏预览",
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		fullscreenPreviewButton.Click += (object _, RoutedEventArgs _) =>
		{
			if (string.IsNullOrWhiteSpace(previewVideoPath) || !File.Exists(previewVideoPath))
			{
				previewStatus.Text = "请先点击“预览裁剪视频”生成预览。";
				return;
			}
			var fsMedia = new MediaPlayer { IsLoopingEnabled = true, AutoPlay = true };
			fsMedia.Source = MediaSource.CreateFromUri(new Uri(previewVideoPath));
			var fsPlayer = new MediaPlayerElement
			{
				AreTransportControlsEnabled = true,
				AutoPlay = true,
				Stretch = Stretch.Uniform,
				HorizontalAlignment = HorizontalAlignment.Stretch,
				VerticalAlignment = VerticalAlignment.Stretch,
				Width = Math.Max(1, Root.ActualWidth),
				Height = Math.Max(1, Root.ActualHeight)
			};
			fsPlayer.SetMediaPlayer(fsMedia);
			var fsOverlay = new Grid
			{
				Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x00, 0x00)),
				Width = Math.Max(1, Root.ActualWidth),
				Height = Math.Max(1, Root.ActualHeight)
			};
			fsOverlay.Children.Add(fsPlayer);
			var fsClose = new Button
			{
				Content = "退出全屏",
				HorizontalAlignment = HorizontalAlignment.Right,
				VerticalAlignment = VerticalAlignment.Top,
				Margin = new Thickness(0, 18, 24, 0),
				Padding = new Thickness(18, 8, 18, 8),
				Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x20, 0x20, 0x20)),
				Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
			};
			fsOverlay.Children.Add(fsClose);
			Canvas.SetZIndex(fsOverlay, 200);
			Grid.SetRowSpan(fsOverlay, 2);
			Root.Children.Add(fsOverlay);
			AppWindow.SetPresenter(AppWindowPresenterKind.FullScreen);
			fsMedia.Play();
			fsOverlay.IsTabStop = true;
			fsOverlay.Focus(FocusState.Programmatic);
			void CloseFullscreen()
			{
				try { fsMedia.Pause(); fsMedia.Dispose(); } catch { }
				Root.Children.Remove(fsOverlay);
				try { AppWindow.SetPresenter(AppWindowPresenterKind.Overlapped); } catch { }
			}
			fsClose.Click += (object _, RoutedEventArgs _) => CloseFullscreen();
			fsOverlay.KeyDown += (object _, KeyRoutedEventArgs e) =>
			{
				if (e.Key == Windows.System.VirtualKey.Escape)
				{
					CloseFullscreen();
					e.Handled = true;
				}
			};
		};
		previewButton.Click += async (object _, RoutedEventArgs _) =>
		{
			previewButton.IsEnabled = false;
			fullscreenPreviewButton.IsEnabled = false;
			previewProgress.Visibility = Visibility.Visible;
			previewProgress.IsIndeterminate = true;
			previewStatus.Text = "正在生成裁剪预览…";
			try
			{
				var left = Math.Clamp((selectionLeft - displayX) / displayWidth * 100.0, 0.0, 90.0);
				var right = Math.Clamp((displayX + displayWidth - selectionLeft - selectionWidth) / displayWidth * 100.0, 0.0, 90.0);
				var top = Math.Clamp((selectionTop - displayY) / displayHeight * 100.0, 0.0, 90.0);
				var bottom = Math.Clamp((displayY + displayHeight - selectionTop - selectionHeight) / displayHeight * 100.0, 0.0, 90.0);
				var previewPath = await new VideoCropService().CreatePreviewAsync(
					item.Path, left, right, top, bottom);
				previewVideoPath = previewPath;
				// Show the preview at the crop's own aspect ratio, so the small
				// player contains only the selected image (no letterbox bars).
				var aspect = selectionWidth / Math.Max(1.0, selectionHeight);
				const double maxW = 240.0;
				const double maxH = 180.0;
				if (aspect > maxW / maxH)
				{
					previewPlayer.Width = maxW;
					previewPlayer.Height = maxW / aspect;
				}
				else
				{
					previewPlayer.Height = maxH;
					previewPlayer.Width = maxH * aspect;
				}
				previewMedia.Source = MediaSource.CreateFromUri(new Uri(previewPath));
				previewMedia.Play();
				previewStatus.Text = "按裁剪比例预览；全屏/铺满时按屏幕比例裁掉较长一边。";
			}
			catch (Exception ex)
			{
				previewStatus.Text = "预览失败：" + ex.Message;
			}
			finally
			{
				previewButton.IsEnabled = true;
				fullscreenPreviewButton.IsEnabled = true;
				previewProgress.IsIndeterminate = false;
				previewProgress.Visibility = Visibility.Collapsed;
			}
		};
		var sidePanel = new StackPanel
		{
			Spacing = 8,
			Children =
			{
				(UIElement)new TextBlock
				{
					Text = "拖动框内移动，拖动四角调整大小。",
					TextWrapping = TextWrapping.Wrap,
					FontSize = 12
				},
				(UIElement)previewButton,
				(UIElement)fullscreenPreviewButton,
				(UIElement)new Border
				{
					Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
					HorizontalAlignment = HorizontalAlignment.Center,
					Child = previewPlayer
				},
				(UIElement)previewStatus,
				(UIElement)previewProgress
			}
		};
		sidePanel.Width = 240;
		var cropContent = new Grid
		{
			ColumnSpacing = 12,
			ColumnDefinitions =
			{
				new ColumnDefinition { Width = new GridLength(770) },
				new ColumnDefinition { Width = new GridLength(240) }
			},
			Children =
			{
				(UIElement)canvas,
				(UIElement)sidePanel
			}
		};
		Grid.SetColumn(sidePanel, 1);

		// A full-window editor instead of ContentDialog: the dialog template
		// caps its width and clips the video frame on smaller windows.
		var overlay = new Grid
		{
			Background = new SolidColorBrush(Color.FromArgb(0xFA, 0x10, 0x15, 0x1F)),
			Padding = new Thickness(12),
			RowDefinitions =
			{
				new RowDefinition { Height = GridLength.Auto },
				new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
				new RowDefinition { Height = GridLength.Auto }
			}
		};
		Canvas.SetZIndex(overlay, 100);
		Grid.SetRowSpan(overlay, 2);
		var header = new Grid { ColumnSpacing = 12 };
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
		header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
		header.Children.Add(new TextBlock
		{
			Text = "视频画面裁剪",
			FontSize = 26,
			FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
			Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
		});
		var close = new Button { Content = "返回", HorizontalAlignment = HorizontalAlignment.Right };
		Grid.SetColumn(close, 1);
		header.Children.Add(close);
		var hint = new TextBlock
		{
			Text = "先选择裁剪比例，再拖动框内移动、拖动四角调整大小；右侧可预览裁剪结果。",
			Foreground = new SolidColorBrush(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF)),
			TextWrapping = TextWrapping.Wrap,
			Margin = new Thickness(0, 6, 0, 12)
		};
		var headerStack = new StackPanel();
		headerStack.Children.Add(header);
		headerStack.Children.Add(hint);
		headerStack.Children.Add(ratioBar);
		overlay.Children.Add(headerStack);
		Grid.SetRow(cropContent, 1);
		overlay.Children.Add(cropContent);
		var footer = new Grid { Margin = new Thickness(0, 12, 0, 0), ColumnSpacing = 10 };
		footer.ColumnDefinitions.Add(new ColumnDefinition());
		footer.ColumnDefinitions.Add(new ColumnDefinition());
		var cropButton = new Button
		{
			Content = "确认裁剪",
			HorizontalAlignment = HorizontalAlignment.Right,
			Padding = new Thickness(24, 8, 24, 8),
			Background = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0x78, 0xD4)),
			Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF))
		};
		var cancelButton = new Button
		{
			Content = "取消",
			HorizontalAlignment = HorizontalAlignment.Left,
			Padding = new Thickness(24, 8, 24, 8)
		};
		Grid.SetColumn(cropButton, 1);
		Grid.SetColumn(cancelButton, 0);
		footer.Children.Add(cancelButton);
		footer.Children.Add(cropButton);
		Grid.SetRow(footer, 2);
		overlay.Children.Add(footer);
		Root.Children.Add(overlay);
		var completed = new TaskCompletionSource<bool>();
		cropButton.Click += (object _, RoutedEventArgs _) => completed.TrySetResult(true);
		cancelButton.Click += (object _, RoutedEventArgs _) => completed.TrySetResult(false);
		close.Click += (object _, RoutedEventArgs _) => completed.TrySetResult(false);
		var accepted = await completed.Task;
		Root.Children.Remove(overlay);
		_cropEditorClosedAt = DateTime.UtcNow;
		CropVideoButton.IsEnabled = true;
		// Moving focus away from the detail-page button prevents a queued
		// Enter key from opening the crop editor again after it closes.
		WallpaperGrid.Focus(FocusState.Programmatic);
		try { previewMedia.Pause(); previewMedia.Dispose(); } catch { }
		if (!accepted)
		{
			return;
		}
		double leftPercent = Math.Clamp((selectionLeft - displayX) / displayWidth * 100.0, 0.0, 90.0);
		double rightPercent = Math.Clamp((displayX + displayWidth - selectionLeft - selectionWidth) / displayWidth * 100.0, 0.0, 90.0);
		double topPercent = Math.Clamp((selectionTop - displayY) / displayHeight * 100.0, 0.0, 90.0);
		double bottomPercent = Math.Clamp((displayY + displayHeight - selectionTop - selectionHeight) / displayHeight * 100.0, 0.0, 90.0);
		ProgressBar bar = new ProgressBar
		{
			Minimum = 0.0,
			Maximum = 100.0
		};
		TextBlock status = new TextBlock
		{
			Text = "准备…",
			TextWrapping = TextWrapping.Wrap
		};
		ContentDialog progressDialog = new ContentDialog
		{
			Title = "裁剪画面尺寸",
			Content = new StackPanel
			{
				Spacing = 12.0,
				Children = 
				{
					(UIElement)status,
					(UIElement)bar
				}
			},
			CloseButtonText = "取消",
			XamlRoot = Content.XamlRoot
		};
		CancellationTokenSource cts = new CancellationTokenSource();
		try
		{
			progressDialog.CloseButtonClick += (ContentDialog _, ContentDialogButtonClickEventArgs _) =>
			{
				cts.Cancel();
			};
			progressDialog.ShowAsync();
			await Task.Delay(120);
			try
			{
				VideoCropResult result = await new VideoCropService().CropAsync(item.Path, leftPercent, rightPercent, topPercent, bottomPercent, (int percent, string message) =>
				{
					DispatcherQueue.TryEnqueue(() =>
					{
						bar.Value = Math.Clamp(percent, 0, 100);
						status.Text = message;
					});
				}, cts.Token);
				progressDialog.Hide();
				AddLibraryRoot(result.OutputPath, WallpaperKind.Video, recursive: false);
				ConfigStore.Save(_config);
				await RefreshCatalogAsync();
				WallpaperItem wallpaperItem = _catalog.Items.FirstOrDefault((WallpaperItem x) => string.Equals(x.Path, result.OutputPath, StringComparison.OrdinalIgnoreCase));
				if (wallpaperItem != null)
				{
					wallpaperItem.Fit = "cover";
					ConfigStore.Save(_config);
				}
				ApplyFilter();
				RevealWallpaper(result.OutputPath);
				SetStatus("画面裁剪完成，已按原裁剪比例加入视频库");
			}
			catch (OperationCanceledException)
			{
				progressDialog.Hide();
				SetStatus("画面裁剪已取消");
			}
			catch (Exception ex3)
			{
				progressDialog.Hide();
				AppLog.Error("video crop failed", ex3);
				await new ContentDialog
				{
					Title = "画面裁剪失败",
					Content = ex3.Message,
					CloseButtonText = "知道了",
					XamlRoot = Content.XamlRoot
				}.ShowAsync();
			}
		}
		finally
		{
			if (cts != null)
			{
				((IDisposable)cts).Dispose();
			}
		}
		void ApplySelection()
		{
			selectionLeft = Math.Clamp(selectionLeft, displayX, displayX + displayWidth - minSize);
			selectionTop = Math.Clamp(selectionTop, displayY, displayY + displayHeight - minSize);
			selectionWidth = Math.Clamp(selectionWidth, minSize, displayX + displayWidth - selectionLeft);
			selectionHeight = Math.Clamp(selectionHeight, minSize, displayY + displayHeight - selectionTop);
			Canvas.SetLeft(selection, selectionLeft);
			Canvas.SetTop(selection, selectionTop);
			selection.Width = selectionWidth;
			selection.Height = selectionHeight;
		}
		Thumb Corner(bool left, bool top)
		{
			Thumb thumb = new Thumb
			{
				Width = 16.0,
				Height = 16.0,
				Background = new SolidColorBrush(Color.FromArgb(byte.MaxValue, byte.MaxValue, byte.MaxValue, byte.MaxValue)),
				BorderBrush = new SolidColorBrush(Color.FromArgb(byte.MaxValue, 0, 120, 212)),
				BorderThickness = new Thickness(2.0)
			};
			thumb.DragDelta += (object _, DragDeltaEventArgs e2) =>
			{
				previewVideoPath = null;
				double horizontalChange = e2.HorizontalChange;
				double verticalChange = e2.VerticalChange;
				var deltaW = left ? -horizontalChange : horizontalChange;
				var deltaH = top ? -verticalChange : verticalChange;
				double newWidth;
				double newHeight;
				if (currentAspect <= 0)
				{
					// Free ratio: each corner changes width and height independently.
					newWidth = selectionWidth + deltaW;
					newHeight = selectionHeight + deltaH;
				}
				else if (Math.Abs(deltaW) >= Math.Abs(deltaH) * currentAspect)
				{
					newWidth = selectionWidth + deltaW;
					newHeight = newWidth / currentAspect;
				}
				else
				{
					newHeight = selectionHeight + deltaH;
					newWidth = newHeight * currentAspect;
				}

				var maxWidth = left
					? selectionLeft + selectionWidth - displayX
					: displayX + displayWidth - selectionLeft;
				var maxHeight = top
					? selectionTop + selectionHeight - displayY
					: displayY + displayHeight - selectionTop;
				var fit = Math.Min(1.0, Math.Min(maxWidth / Math.Max(1, newWidth),
					maxHeight / Math.Max(1, newHeight)));
				newWidth = Math.Max(minSize, newWidth * fit);
				newHeight = currentAspect <= 0
					? Math.Max(minSize, newHeight * fit)
					: newWidth / currentAspect;
				if (newHeight < minSize)
				{
					newHeight = minSize;
					newWidth = currentAspect <= 0 ? newWidth : newHeight * currentAspect;
				}
				if (left)
					selectionLeft = selectionLeft + selectionWidth - newWidth;
				if (top)
					selectionTop = selectionTop + selectionHeight - newHeight;
				selectionWidth = newWidth;
				selectionHeight = newHeight;
				ApplySelection();
				PlaceHandles();
			};
			canvas.Children.Add(thumb);
			return thumb;
		}
		void PlaceHandles()
		{
			Canvas.SetLeft(topLeft, selectionLeft - 8.0);
			Canvas.SetTop(topLeft, selectionTop - 8.0);
			Canvas.SetLeft(topRight, selectionLeft + selectionWidth - 8.0);
			Canvas.SetTop(topRight, selectionTop - 8.0);
			Canvas.SetLeft(bottomLeft, selectionLeft - 8.0);
			Canvas.SetTop(bottomLeft, selectionTop + selectionHeight - 8.0);
			Canvas.SetLeft(bottomRight, selectionLeft + selectionWidth - 8.0);
			Canvas.SetTop(bottomRight, selectionTop + selectionHeight - 8.0);
			Canvas.SetLeft(move, selectionLeft);
			Canvas.SetTop(move, selectionTop);
			move.Width = selectionWidth;
			move.Height = selectionHeight;
		}
	}

	private async void TrimVideo_Click(object sender, RoutedEventArgs e)
	{
		WallpaperItem item = _selected;
		if (item == null || item.Kind != WallpaperKind.Video)
		{
			return;
		}
		NumberBox start = new NumberBox
		{
			Header = "开始时间（秒）",
			Minimum = 0.0,
			Maximum = 36000.0,
			Value = 0.0,
			SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
		};
		NumberBox end = new NumberBox
		{
			Header = "结束时间（秒，0 = 全部）",
			Minimum = 0.0,
			Maximum = 36000.0,
			Value = 0.0,
			SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
		};
		if (await new ContentDialog
		{
			Title = "裁剪视频",
			Content = new StackPanel
			{
				Spacing = 10.0,
				Children = 
				{
					(UIElement)new TextBlock
					{
						Text = "输入开始和结束时间。生成的是新视频，原文件不修改；使用高质量编码并保留音频。",
						TextWrapping = TextWrapping.Wrap
					},
					(UIElement)start,
					(UIElement)end
				}
			},
			PrimaryButtonText = "生成裁剪视频",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = Content.XamlRoot
		}.ShowAsync() != ContentDialogResult.Primary)
		{
			return;
		}
		PlaylistEntry entry = new PlaylistEntry
		{
			Path = item.Path,
			StartSeconds = Math.Max(0.0, start.Value),
			EndSeconds = Math.Max(0.0, end.Value)
		};
		ProgressBar bar = new ProgressBar
		{
			Minimum = 0.0,
			Maximum = 100.0
		};
		TextBlock status = new TextBlock
		{
			Text = "准备…",
			TextWrapping = TextWrapping.Wrap
		};
		ContentDialog progressDialog = new ContentDialog
		{
			Title = "裁剪视频",
			Content = new StackPanel
			{
				Spacing = 12.0,
				Children = 
				{
					(UIElement)status,
					(UIElement)bar
				}
			},
			CloseButtonText = "取消",
			XamlRoot = Content.XamlRoot
		};
		CancellationTokenSource cts = new CancellationTokenSource();
		try
		{
			progressDialog.CloseButtonClick += (ContentDialog _, ContentDialogButtonClickEventArgs _) =>
			{
				cts.Cancel();
			};
			progressDialog.ShowAsync();
			await Task.Delay(120);
			try
			{
				QueueRenderResult queueRenderResult = await new QueueRenderService().RenderAsync(new PlaylistEntry[1] { entry }, (int percent, string message) =>
				{
					DispatcherQueue.TryEnqueue(() =>
					{
						bar.Value = Math.Clamp(percent, 0, 100);
						status.Text = message;
					});
				}, cts.Token);
				progressDialog.Hide();
				AddLibraryRoot(queueRenderResult.OutputPath, WallpaperKind.Video, recursive: false);
				ConfigStore.Save(_config);
				await RefreshCatalogAsync();
				ApplyFilter();
				RevealWallpaper(queueRenderResult.OutputPath);
				SetStatus("视频裁剪完成，已加入视频库");
			}
			catch (OperationCanceledException)
			{
				progressDialog.Hide();
				SetStatus("视频裁剪已取消");
			}
			catch (Exception ex2)
			{
				progressDialog.Hide();
				AppLog.Error("video trim failed", ex2);
				await new ContentDialog
				{
					Title = "视频裁剪失败",
					Content = ex2.Message,
					CloseButtonText = "知道了",
					XamlRoot = Content.XamlRoot
				}.ShowAsync();
			}
		}
		finally
		{
			if (cts != null)
			{
				((IDisposable)cts).Dispose();
			}
		}
	}

	private async void SuperResolution_Click(object sender, RoutedEventArgs e)
	{
		WallpaperItem item = _selected;
		bool flag = item == null;
		if (!flag)
		{
			WallpaperKind kind = item.Kind;
			bool flag2 = ((kind == WallpaperKind.Video || kind == WallpaperKind.Sequence) ? true : false);
			flag = !flag2;
		}
		if (flag)
		{
			return;
		}
		if (SuperResolutionService.FindRealEsrgan() == null)
		{
			await new ContentDialog
			{
				Title = "超分辨率不可用",
				Content = "未找到 realesrgan-ncnn-vulkan.exe。\n请放到：\n" + SuperResolutionService.ToolRoot,
				CloseButtonText = "知道了",
				XamlRoot = Content.XamlRoot
			}.ShowAsync();
			return;
		}
		ComboBox modelBox = new ComboBox
		{
			Header = "模型",
			SelectedIndex = 0,
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		modelBox.Items.Add("动漫视频（realesr-animevideov3，支持 2×/4×）");
		modelBox.Items.Add("动漫高质（realesrgan-x4plus-anime，仅 4×）");
		modelBox.Items.Add("实拍通用（realesrgan-x4plus，仅 4×）");
		ComboBox factorBox = new ComboBox
		{
			Header = "放大倍数",
			SelectedIndex = 0,
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		factorBox.Items.Add("2×");
		factorBox.Items.Add("4×");
		modelBox.SelectionChanged += (object _, SelectionChangedEventArgs _) =>
		{
			bool flag3 = modelBox.SelectedIndex > 0;
			factorBox.IsEnabled = !flag3;
			if (flag3)
			{
				factorBox.SelectedIndex = 1;
			}
		};
		double sourceFps = ((item.SourceFps > 0.1) ? item.SourceFps : 30.0);
		if (await new ContentDialog
		{
			Title = "视频超分辨率",
			Content = new StackPanel
			{
				Spacing = 10.0,
				Children = 
				{
					(UIElement)new TextBlock
					{
						Text = $"源帧率约 {sourceFps:0.##} fps。\n" + "将逐帧运行 Real-ESRGAN，再无损中间层 + libx264 编码。\n2× 会把 2560×1440 放大到 5120×2880，文件会非常大。\n4× 对 1440p 会达到 10240×5760，超出安全范围，建议 2×。",
						TextWrapping = TextWrapping.Wrap
					},
					(UIElement)modelBox,
					(UIElement)factorBox
				}
			},
			PrimaryButtonText = "开始超分",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = Content.XamlRoot
		}.ShowAsync() != ContentDialogResult.Primary)
		{
			return;
		}
		string model = modelBox.SelectedIndex switch
		{
			1 => "realesrgan-x4plus-anime", 
			2 => "realesrgan-x4plus", 
			_ => "realesr-animevideov3", 
		};
		string text = model;
		flag = ((text == "realesrgan-x4plus" || text == "realesrgan-x4plus-anime") ? true : false);
		int factor;
		if (flag)
		{
			factor = 4;
		}
		else
		{
			factor = ((factorBox.SelectedIndex == 1) ? 4 : 2);
		}
		SuperResolutionService service = new SuperResolutionService();
		ProgressBar bar = new ProgressBar
		{
			Minimum = 0.0,
			Maximum = 100.0
		};
		TextBlock status = new TextBlock
		{
			Text = "准备…",
			TextWrapping = TextWrapping.Wrap
		};
		ContentDialog progressDialog = new ContentDialog
		{
			Title = $"超分辨率 · {factor}×",
			Content = new StackPanel
			{
				Spacing = 12.0,
				Children = 
				{
					(UIElement)status,
					(UIElement)bar
				}
			},
			CloseButtonText = "取消",
			XamlRoot = Content.XamlRoot
		};
		CancellationTokenSource cts = new CancellationTokenSource();
		try
		{
			progressDialog.CloseButtonClick += (ContentDialog _, ContentDialogButtonClickEventArgs _) =>
			{
				cts.Cancel();
			};
			progressDialog.ShowAsync();
			await Task.Delay(120);
			try
			{
				SuperResolutionResult superResolutionResult = await service.UpscaleAsync(item.Path, sourceFps, factor, model, (int percent, string message) =>
				{
					DispatcherQueue.TryEnqueue(() =>
					{
						bar.Value = Math.Clamp(percent, 0, 100);
						status.Text = message;
					});
				}, cts.Token);
				progressDialog.Hide();
				AddLibraryRoot(superResolutionResult.OutputPath, WallpaperKind.Video, recursive: false);
				ConfigStore.Save(_config);
				await RefreshCatalogAsync();
				ApplyFilter();
				RevealWallpaper(superResolutionResult.OutputPath);
				SetStatus($"超分辨率完成：{factor}×，已加入视频库");
			}
			catch (OperationCanceledException)
			{
				progressDialog.Hide();
				SetStatus("超分辨率已取消");
			}
			catch (Exception ex2)
			{
				progressDialog.Hide();
				AppLog.Error("super resolution failed", ex2);
				await new ContentDialog
				{
					Title = "超分辨率失败",
					Content = ex2.Message,
					CloseButtonText = "知道了",
					XamlRoot = Content.XamlRoot
				}.ShowAsync();
			}
		}
		finally
		{
			if (cts != null)
			{
				((IDisposable)cts).Dispose();
			}
		}
	}

	private void ReloadWeb_Click(object sender, RoutedEventArgs e)
	{
		WallpaperItem selected = _selected;
		if (selected != null && selected.Kind == WallpaperKind.Web)
		{
			if (_host.IsRunning)
			{
				_host.Restart();
			}
			SetStatus("正在刷新 Web 页面：" + selected.Title);
		}
	}

	private void SceneQualityCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_sceneQualityUi)
		{
			return;
		}
		WallpaperItem selected = _selected;
		if (selected != null && SceneQualityCombo.SelectedItem is ComboBoxItem { Tag: var tag } && double.TryParse(tag?.ToString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var result))
		{
			selected.RenderScale = Math.Clamp(result, 0.5, 2.0);
			Persist();
			if (_host.IsRunning)
			{
				_host.Restart();
			}
		}
	}

	private void TargetFpsCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
	{
		if (_fpsUi)
		{
			return;
		}
		WallpaperItem item = _selected;
		if (item != null && TargetFpsCombo.SelectedItem is ComboBoxItem { Tag: var tag } && int.TryParse(tag?.ToString(), out var result))
		{
			item.TargetFps = result;
			Persist();
			UpdateFpsHint(item);
			if (_config.Monitors.Values.Any((MonitorAssignment x) => string.Equals(x.WallpaperId, item.Id, StringComparison.OrdinalIgnoreCase)) && _host.IsRunning)
			{
				_host.Restart();
				SetStatus($"已设置输出帧率 {result} fps，调度器重启中");
			}
			else
			{
				SetStatus($"已设置输出帧率 {result} fps，应用壁纸后生效");
			}
		}
	}

	private void ClearSelection()
	{
		_selected = null;
		PropertyTitle.Text = "未选择壁纸";
		ApplyButton.IsEnabled = false;
		PreviewButton.IsEnabled = false;
		ApplyToMonitorButton.IsEnabled = false;
		OpenDetailButton.IsEnabled = false;
		DeleteSelectedButton.IsEnabled = false;
		RenameButton.IsEnabled = false;
		DeleteButton.IsEnabled = false;
		AiInterpolateButton.IsEnabled = false;
		_fpsProbeCts?.Cancel();
		StopDetailPreview();
		DetailPage.Visibility = Visibility.Collapsed;
		LibraryContent.Visibility = Visibility.Visible;
	}

	private async void CreateWebPage_Click(object sender, RoutedEventArgs e)
	{
		var titleBox = new TextBox { Header = "名称", Text = "新建 Web 壁纸" };
		var htmlBox = new TextBox
		{
			Header = "HTML",
			AcceptsReturn = true,
			TextWrapping = TextWrapping.Wrap,
			Height = 300,
			FontFamily = new FontFamily("Consolas"),
			Text = """
<!doctype html>
<html>
<head>
<meta charset="utf-8">
<style>
  html, body { margin:0; height:100%; overflow:hidden; background:#0b1020; }
  .dot { position:fixed; width:120px; height:120px; border-radius:50%;
         background:radial-gradient(circle at 30% 30%, #7ad7ff, #1a4b8f);
         opacity:.75; animation: float 8s ease-in-out infinite; }
  @keyframes float {
    0%   { transform: translate(0,0) scale(1); }
    50%  { transform: translate(40px,-60px) scale(1.2); }
    100% { transform: translate(0,0) scale(1); }
  }
</style>
</head>
<body>
<div class="dot" style="left:10%; top:20%;"></div>
<div class="dot" style="left:55%; top:60%; animation-delay:-3s;"></div>
<div class="dot" style="left:75%; top:15%; animation-delay:-5s;"></div>
</body>
</html>
"""
		};
		if (await new ContentDialog
		{
			Title = "新建 Web 壁纸",
			Content = new ScrollViewer
			{
				Content = new StackPanel
				{
					Spacing = 10,
					Children = { (UIElement)titleBox, (UIElement)htmlBox }
				},
				MaxHeight = 560
			},
			PrimaryButtonText = "创建",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = Content.XamlRoot
		}.ShowAsync() != ContentDialogResult.Primary)
			return;
		try
		{
			var result = await VideoToWebConverter.CreatePageAsync(
				Path.Combine(AppStorage.SoftwareRoot, "web"), titleBox.Text, htmlBox.Text);
			AddLibraryRoot(result.IndexPath, WallpaperKind.Web, recursive: false);
			ConfigStore.Save(_config);
			await RefreshCatalogAsync();
			_visible = _catalog.ByKind(WallpaperKind.Web).ToList();
			ApplyFilter();
			RevealWallpaper(result.IndexPath);
			SetStatus("已创建 Web 壁纸：" + result.IndexPath);
		}
		catch (Exception ex)
		{
			SetStatus("创建 Web 壁纸失败：" + ex.Message);
		}
	}

	private async void CreateScene_Click(object sender, RoutedEventArgs e)
	{
		var nameBox = new TextBox { Header = "场景名称", Text = "新建场景" };
		var imageBox = new TextBox { Header = "背景图片（可选，绝对路径）", PlaceholderText = @"D:\Pictures\bg.png" };
		var colorBox = new TextBox { Header = "背景颜色（#RRGGBB）", Text = "#0B1020" };
		var particles = new NumberBox
		{
			Header = "粒子数量",
			Value = 320,
			Minimum = 0,
			Maximum = 20000,
			SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact
		};
		if (await new ContentDialog
		{
			Title = "新建场景壁纸",
			Content = new StackPanel
			{
				Spacing = 10,
				Children =
				{
					(UIElement)nameBox,
					(UIElement)imageBox,
					(UIElement)colorBox,
					(UIElement)particles,
					(UIElement)new TextBlock
					{
						Text = "生成 scene.json；图片和粒子会作为图层实时渲染。",
						FontSize = 12,
						TextWrapping = TextWrapping.Wrap
					}
				}
			},
			PrimaryButtonText = "创建",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = Content.XamlRoot
		}.ShowAsync() != ContentDialogResult.Primary)
			return;
		try
		{
			var root = Path.Combine(AppStorage.SoftwareRoot, "scenes");
			var folder = Path.Combine(root, $"{SafeFolderName(nameBox.Text)}-{DateTime.Now:yyyyMMdd-HHmmss}");
			Directory.CreateDirectory(folder);
			var bg = imageBox.Text?.Trim() ?? "";
			if (!string.IsNullOrWhiteSpace(bg) && File.Exists(bg))
			{
				var copy = Path.Combine(folder, Path.GetFileName(bg));
				File.Copy(bg, copy, overwrite: true);
				bg = Path.GetFileName(bg);
			}
			else
			{
				bg = "";
			}
			var hex = (colorBox.Text ?? "#0B1020").TrimStart('#');
			if (hex.Length != 6) hex = "0B1020";
			var r = Convert.ToInt32(hex[..2], 16) / 255.0;
			var g = Convert.ToInt32(hex[2..4], 16) / 255.0;
			var b = Convert.ToInt32(hex[4..6], 16) / 255.0;
			var layers = new List<object>();
			if (!string.IsNullOrWhiteSpace(bg))
			{
				layers.Add(new
				{
					name = "background",
					type = "image",
					image = bg,
					position = new[] { 0.5, 0.5 },
					size = new[] { 1.0, 1.0 },
					opacity = 1.0
				});
			}
			layers.Add(new
			{
				name = "color",
				type = "color",
				color = new[] { r, g, b, 1.0 },
				position = new[] { 0.5, 0.5 },
				size = new[] { 1.0, 1.0 },
				opacity = 1.0
			});
			layers.Add(new
			{
				name = "particles",
				type = "particles",
				count = (int)Math.Clamp(particles.Value, 0, 20000),
				particleSize = 3.0,
				color = new[] { 0.55, 0.85, 1.0, 0.85 },
				opacity = 0.85
			});
			var scene = new
			{
				name = string.IsNullOrWhiteSpace(nameBox.Text) ? "新建场景" : nameBox.Text.Trim(),
				fps = 60,
				maxParticles = (int)Math.Clamp(particles.Value, 0, 20000) + 1024,
				layers
			};
			var scenePath = Path.Combine(folder, "scene.json");
			await File.WriteAllTextAsync(scenePath,
				JsonSerializer.Serialize(scene, new JsonSerializerOptions { WriteIndented = true }),
				new UTF8Encoding(false));
			AddLibraryRoot(scenePath, WallpaperKind.Scene, recursive: false);
			ConfigStore.Save(_config);
			await RefreshCatalogAsync();
			ApplyFilter();
			RevealWallpaper(scenePath);
			SetStatus("已创建场景壁纸：" + scenePath);
		}
		catch (Exception ex)
		{
			SetStatus("创建场景失败：" + ex.Message);
		}
	}

	private static string SafeFolderName(string? value)
	{
		var invalid = Path.GetInvalidFileNameChars();
		var sb = new StringBuilder();
		foreach (var ch in (value ?? "").Trim())
			sb.Append(invalid.Contains(ch) ? '_' : ch);
		var result = sb.ToString().Trim(' ', '.');
		return string.IsNullOrWhiteSpace(result) ? "scene" : result[..Math.Min(result.Length, 60)];
	}

	private void ApplyDetail_Click(object sender, RoutedEventArgs e)
	{
		if (_selected is null) return;
		ApplySelectedToTarget();
	}

	private async void OpenDetail_Click(object sender, RoutedEventArgs e)
	{
		var item = CurrentWallpaper();
		if (item is not null)
			await OpenDetailPageAsync(item);
	}

	private WallpaperItem? ChooseDeleteFallback(ISet<string> deletedIds)
	{
		var mode = _config.Playback.DeleteCurrentBehavior;
		if (string.Equals(mode, "pause", StringComparison.OrdinalIgnoreCase))
			return null;
		if (string.Equals(mode, "first", StringComparison.OrdinalIgnoreCase))
		{
			return _catalog.Items.FirstOrDefault(x =>
				!deletedIds.Contains(x.Id) && File.Exists(x.Path));
		}
		return PlaybackHistoryService.FindPrevious(_catalog.Items, deletedIds);
	}

	private async Task ShowPlaybackHistoryAsync()
	{
		var entries = PlaybackHistoryService.Load();
		WallpaperItem? chosen = null;
		var dialog = new ContentDialog
		{
			Title = "播放记录",
			CloseButtonText = "关闭",
			XamlRoot = Content.XamlRoot
		};
		var list = new StackPanel { Spacing = 8 };
		foreach (var entry in entries.Take(40))
		{
			var item = _catalog.Items.FirstOrDefault(x =>
				string.Equals(x.Id, entry.Id, StringComparison.OrdinalIgnoreCase));
			if (item is null) continue;
			var row = new Grid { ColumnSpacing = 10 };
			row.ColumnDefinitions.Add(new ColumnDefinition
			{
				Width = new GridLength(1, GridUnitType.Star)
			});
			row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			row.Children.Add(new StackPanel
			{
				Spacing = 2,
				Children =
				{
					(UIElement)new TextBlock
					{
						Text = entry.Title,
						FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
						TextTrimming = TextTrimming.CharacterEllipsis
					},
					(UIElement)new TextBlock
					{
						Text = $"{entry.Kind} · {entry.AppliedAt}",
						FontSize = 11,
						Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
					}
				}
			});
			var apply = new Button { Content = "播放", Padding = new Thickness(14, 5, 14, 5) };
			apply.Click += (_, _) =>
			{
				chosen = item;
				dialog.Hide();
			};
			Grid.SetColumn(apply, 1);
			row.Children.Add(apply);
			list.Children.Add(new Border
			{
				Padding = new Thickness(12, 8, 12, 8),
				CornerRadius = new CornerRadius(8),
				Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
				Child = row
			});
		}
		if (list.Children.Count == 0)
		{
			list.Children.Add(new TextBlock
			{
				Text = "还没有播放记录。应用过的壁纸会显示在这里。",
				TextWrapping = TextWrapping.Wrap
			});
		}
		dialog.Content = new ScrollViewer
		{
			Content = list,
			MaxHeight = 460,
			VerticalScrollBarVisibility = ScrollBarVisibility.Auto
		};
		await dialog.ShowAsync();
		if (chosen is not null)
		{
			WallpaperGrid.SelectedItem = chosen;
			_selected = chosen;
			ApplySelectedToTarget();
		}
		SelectCategory("Video");
	}

	private async Task ShowRecycleBinAsync()
	{
		var entries = RecycleBinService.Load();
		var list = new StackPanel { Spacing = 8 };
		var dialog = new ContentDialog
		{
			Title = $"回收站（{entries.Count}）",
			CloseButtonText = "关闭",
			XamlRoot = Content.XamlRoot
		};

		void Rebuild()
		{
			list.Children.Clear();
			entries = RecycleBinService.Load();
			dialog.Title = $"回收站（{entries.Count}）";
			if (entries.Count == 0)
			{
				list.Children.Add(new TextBlock
				{
					Text = "回收站是空的。删除的壁纸会保存在这里。",
					TextWrapping = TextWrapping.Wrap
				});
				return;
			}

			foreach (var entry in entries)
			{
				var row = new Grid { ColumnSpacing = 10 };
				row.ColumnDefinitions.Add(new ColumnDefinition
                {
                    Width = new GridLength(1, GridUnitType.Star)
                });
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
				row.Children.Add(new StackPanel
				{
					Spacing = 2,
					Children =
					{
						(UIElement)new TextBlock
						{
							Text = entry.Title,
							FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
							TextTrimming = TextTrimming.CharacterEllipsis
						},
						(UIElement)new TextBlock
						{
							Text = $"{entry.Kind} · {entry.DeletedAt}",
							FontSize = 11,
							Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
						}
					}
				});
				var restore = new Button { Content = "恢复", Padding = new Thickness(14, 5, 14, 5) };
				restore.Click += async (_, _) =>
				{
					try
					{
						var item = RecycleBinService.Restore(entry);
						_config.Wallpapers.Add(item);
						TitleOverrideStore.Set(item.Path, item.Title, item.Kind);
						ConfigStore.Save(_config);
						await RefreshCatalogAsync();
						ApplyFilter();
						RevealWallpaper(item.Path);
						Rebuild();
						SetStatus("已恢复：" + item.Title);
					}
					catch (Exception ex)
					{
						SetStatus("恢复失败：" + ex.Message);
					}
				};
				Grid.SetColumn(restore, 1);
				row.Children.Add(restore);
				var delete = new Button { Content = "永久删除", Padding = new Thickness(14, 5, 14, 5) };
				delete.Click += (_, _) =>
				{
					RecycleBinService.Delete(entry);
					Rebuild();
				};
				Grid.SetColumn(delete, 2);
				row.Children.Add(delete);
				list.Children.Add(new Border
				{
					Padding = new Thickness(12, 8, 12, 8),
					CornerRadius = new CornerRadius(8),
					Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
					Child = row
				});
			}
		}
		Rebuild();

		var deleteAll = new Button
		{
			Content = "删除全部",
			HorizontalAlignment = HorizontalAlignment.Left,
			Padding = new Thickness(16, 6, 16, 6)
		};
		deleteAll.Click += (_, _) =>
		{
			RecycleBinService.Clear();
			Rebuild();
		};
		dialog.Content = new StackPanel
		{
			Spacing = 12,
			Children =
			{
				(UIElement)new ScrollViewer
				{
					Content = list,
					MaxHeight = 420,
					VerticalScrollBarVisibility = ScrollBarVisibility.Auto
				},
				(UIElement)deleteAll
			}
		};
		await dialog.ShowAsync();
		SelectCategory("Video");
	}

	private async void StaticAdjust_Click(object sender, RoutedEventArgs e)
	{
		var item = _selected;
		if (item is { Kind: WallpaperKind.Static })
			await ShowColorAdjustmentsAsync(item);
	}

	private async void StaticUpscale_Click(object sender, RoutedEventArgs e)
	{
		var item = _selected;
		if (item is not { Kind: WallpaperKind.Static } || !File.Exists(item.Path))
			return;
		var factorBox = new ComboBox
		{
			Header = "放大倍数",
			SelectedIndex = 0,
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		factorBox.Items.Add(new ComboBoxItem { Content = "2×", Tag = 2 });
		factorBox.Items.Add(new ComboBoxItem { Content = "4×", Tag = 4 });
		var modelBox = new ComboBox
		{
			Header = "模型",
			SelectedIndex = 0,
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		modelBox.Items.Add(new ComboBoxItem
		{
			Content = "动漫图片（realesrgan-x4plus-anime）",
			Tag = "realesrgan-x4plus-anime"
		});
		modelBox.Items.Add(new ComboBoxItem
		{
			Content = "通用照片（realesrgan-x4plus）",
			Tag = "realesrgan-x4plus"
		});
		if (await new ContentDialog
		{
			Title = "静态图片超分",
			Content = new StackPanel
			{
				Spacing = 10,
				Children =
				{
					(UIElement)new TextBlock
					{
						Text = $"源图片：{Path.GetFileName(item.Path)}",
						TextWrapping = TextWrapping.Wrap
					},
					(UIElement)factorBox,
					(UIElement)modelBox
				}
			},
			PrimaryButtonText = "开始超分",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = Content.XamlRoot
		}.ShowAsync() != ContentDialogResult.Primary)
			return;

		var factor = ((ComboBoxItem)factorBox.SelectedItem).Tag is int f ? f : 2;
		var model = ((ComboBoxItem)modelBox.SelectedItem).Tag?.ToString()
		            ?? "realesrgan-x4plus-anime";
		var status = new TextBlock { Text = "准备…", TextWrapping = TextWrapping.Wrap };
		var bar = new ProgressBar { Minimum = 0, Maximum = 100 };
		var dialog = new ContentDialog
		{
			Title = "静态图片超分",
			Content = new StackPanel
			{
				Spacing = 10,
				Children = { (UIElement)status, (UIElement)bar }
			},
			CloseButtonText = "取消",
			XamlRoot = Content.XamlRoot
		};
		var cts = new CancellationTokenSource();
		dialog.CloseButtonClick += (_, _) => cts.Cancel();
		dialog.ShowAsync();
		try
		{
			var output = await StaticImageService.UpscaleAsync(item.Path, factor, model,
				(percent, message) => DispatcherQueue.TryEnqueue(() =>
				{
					bar.Value = Math.Clamp(percent, 0, 100);
					status.Text = message;
				}), cts.Token);
			dialog.Hide();
			var added = _catalog.AddPath(output, WallpaperKind.Static);
			ConfigStore.Save(_config);
			await RefreshCatalogAsync();
			ApplyFilter();
			if (added is not null)
			{
				added.Title = item.Title + " 超分";
				ConfigStore.Save(_config);
				RevealWallpaper(added.Path);
			}
			SetStatus("静态图片超分完成");
		}
		catch (OperationCanceledException)
		{
			dialog.Hide();
			SetStatus("静态超分已取消");
		}
		catch (Exception ex)
		{
			dialog.Hide();
			SetStatus("静态超分失败：" + ex.Message);
		}
		finally
		{
			cts.Dispose();
		}
	}

	private async Task ShowColorAdjustmentsAsync(WallpaperItem item)
	{
		var sliders = new Dictionary<string, Slider>(StringComparer.Ordinal);
		var panel = new StackPanel { Spacing = 8, Width = 420 };
		Slider AddSlider(string key, string label, double value)
		{
			var head = new Grid();
			head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
			head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
			var name = new TextBlock { Text = label, FontSize = 12 };
			var number = new TextBlock
			{
				Text = value.ToString("0"),
				FontSize = 12,
				Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"]
			};
			Grid.SetColumn(number, 1);
			head.Children.Add(name);
			head.Children.Add(number);
			var slider = new Slider
			{
				Minimum = -100,
				Maximum = 100,
				StepFrequency = 1,
				Value = Math.Clamp(value, -100, 100),
				HorizontalAlignment = HorizontalAlignment.Stretch
			};
			slider.ValueChanged += (_, e) => number.Text = e.NewValue.ToString("0");
			sliders[key] = slider;
			panel.Children.Add(head);
			panel.Children.Add(slider);
			return slider;
		}
		AddSlider("brightness", "亮度", item.Brightness);
		AddSlider("contrast", "对比度", item.Contrast);
		AddSlider("highlights", "高光", item.Highlights);
		AddSlider("hue", "色调", item.Hue);
		AddSlider("temperature", "色温", item.Temperature);
		AddSlider("exposure", "曝光", item.Exposure);
		AddSlider("sharpness", "锐度", item.Sharpness);
		if (item.Kind == WallpaperKind.Static)
		{
			AddSlider("rgbR", "RGB 曲线 · 红", item.RgbR);
			AddSlider("rgbG", "RGB 曲线 · 绿", item.RgbG);
			AddSlider("rgbB", "RGB 曲线 · 蓝", item.RgbB);
		}

		void ApplyLive()
		{
			item.Brightness = sliders["brightness"].Value;
			item.Contrast = sliders["contrast"].Value;
			item.Highlights = sliders["highlights"].Value;
			item.Hue = sliders["hue"].Value;
			item.Temperature = sliders["temperature"].Value;
			item.Exposure = sliders["exposure"].Value;
			item.Sharpness = sliders["sharpness"].Value;
			if (sliders.TryGetValue("rgbR", out var rgbR)) item.RgbR = rgbR.Value;
			if (sliders.TryGetValue("rgbG", out var rgbG)) item.RgbG = rgbG.Value;
			if (sliders.TryGetValue("rgbB", out var rgbB)) item.RgbB = rgbB.Value;
			ConfigStore.Save(_config);
			foreach (var monitor in _config.Monitors)
			{
				if (monitor.Value.WallpaperId == item.Id && _host.IsRunning)
					_host.SetColor(monitor.Key, item);
			}
			if (item.Kind == WallpaperKind.Static &&
			    _config.Monitors.Values.Any(x => x.WallpaperId == item.Id))
			{
				try
				{
					DesktopWallpaperService.SetImage(StaticImageService.RenderAdjusted(item));
				}
				catch { }
			}
		}
		foreach (var slider in sliders.Values)
			slider.ValueChanged += (_, _) => ApplyLive();

		ContentDialog? dialog = null;
		var reset = new Button
		{
			Content = "还原到最初状态",
			HorizontalAlignment = HorizontalAlignment.Stretch,
			Margin = new Thickness(0, 8, 0, 0)
		};
		reset.Click += (_, _) =>
		{
			foreach (var slider in sliders.Values)
				slider.Value = 0;
			item.Brightness = 0;
			item.Contrast = 0;
			item.Highlights = 0;
			item.Hue = 0;
			item.Temperature = 0;
			item.Exposure = 0;
			item.Sharpness = 0;
			item.RgbR = 0;
			item.RgbG = 0;
			item.RgbB = 0;
			ConfigStore.Save(_config);
			foreach (var monitor in _config.Monitors)
			{
				if (monitor.Value.WallpaperId == item.Id && _host.IsRunning)
					_host.SetColor(monitor.Key, item);
			}
			if (item.Kind == WallpaperKind.Static &&
			    _config.Monitors.Values.Any(x => x.WallpaperId == item.Id))
			{
				try { DesktopWallpaperService.SetImage(item.Path); } catch { }
			}
			SetStatus("已还原画面调节：" + item.Title);
			dialog?.Hide();
		};
		panel.Children.Add(reset);
		dialog = new ContentDialog
		{
			Title = "画面调节",
			Content = new ScrollViewer
			{
				Content = panel,
				MaxHeight = 560,
				VerticalScrollBarVisibility = ScrollBarVisibility.Auto
			},
			PrimaryButtonText = "应用",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = Content.XamlRoot
		};
		dialog.PrimaryButtonClick += (_, _) =>
		{
			ApplyLive();
			SetStatus("已更新画面调节：" + item.Title);
		};
		await dialog.ShowAsync();
	}

	private async Task OpenDetailPageAsync(WallpaperItem item)
	{
		ShowProperties(item);
		var colorTarget = item.Kind == WallpaperKind.Static ? StaticInfoPanel : VideoInfoPanel;
		if (item.Kind is WallpaperKind.Video or WallpaperKind.Sequence &&
		    !colorTarget.Children.OfType<Button>().Any(x => x.Name == "ColorAdjustButton"))
		{
			var colorButton = new Button
			{
				Name = "ColorAdjustButton",
				Content = item.Kind == WallpaperKind.Static
					? "画面调节与 RGB 曲线"
					: "画面调节",
				HorizontalAlignment = HorizontalAlignment.Stretch,
				Margin = new Thickness(0, 6, 0, 0)
			};
			colorButton.Click += async (_, _) =>
			{
				var current = _selected;
				if (current is not null) await ShowColorAdjustmentsAsync(current);
			};
			colorTarget.Children.Add(colorButton);
		}
		LibraryContent.Visibility = Visibility.Collapsed;
		DetailPage.Visibility = Visibility.Visible;
		await LoadDetailPreviewAsync(item);
		WallpaperKind kind = item.Kind;
		bool flag = ((kind == WallpaperKind.Video || kind == WallpaperKind.Sequence) ? true : false);
		if (flag && item.SourceFps <= 0.1)
		{
			ProbeSourceFpsAsync(item);
		}
	}

	private async Task ProbeSourceFpsAsync(WallpaperItem item)
	{
		_fpsProbeCts?.Cancel();
		_fpsProbeCts?.Dispose();
		_fpsProbeCts = new CancellationTokenSource();
		try
		{
			double num = await FrameRateProbe.ProbeAsync((!string.IsNullOrWhiteSpace(_config.Host.VideoChildExe)) ? _config.Host.VideoChildExe : Path.Combine(AppContext.BaseDirectory, "VideoWallpaperHost.exe"), item.Path, _fpsProbeCts.Token);
			if (item == _selected)
			{
				if (num <= 0.1)
				{
					_fpsUi = true;
					SourceFpsText.Text = "未知（VFR）";
					FpsHintText.Text = "源帧率无法从容器读取；输出仍可设为 60 fps，使用 mpv 帧复制";
					_fpsUi = false;
					return;
				}
				item.SourceFps = num;
				Persist();
				_fpsUi = true;
				SourceFpsText.Text = $"{num:0.##} fps";
				UpdateFpsHint(item);
				_fpsUi = false;
			}
		}
		catch
		{
		}
	}

	private async Task LoadDetailPreviewAsync(WallpaperItem item)
	{
		if ((object)_detailPlayer == null)
		{
			_detailPlayer = new MediaPlayer
			{
				IsMuted = true,
				AutoPlay = true
			};
		}
		DetailPlayer.SetMediaPlayer(_detailPlayer);
		WallpaperKind kind = item.Kind;
		if ((kind == WallpaperKind.Video || kind == WallpaperKind.Sequence) ? true : false)
		{
			DetailPlayer.Visibility = Visibility.Visible;
			DetailPreviewImage.Visibility = Visibility.Collapsed;
			_detailPlayer.Source = MediaSource.CreateFromUri(new Uri(item.Path));
			_detailPlayer.Play();
			return;
		}
		StopDetailPreview();
		DetailPlayer.Visibility = Visibility.Collapsed;
		DetailPreviewImage.Visibility = Visibility.Visible;
		DetailPreviewImage.Source = item.Thumbnail;
		if ((object)DetailPreviewImage.Source != null)
		{
			return;
		}
		string text = await _thumbnails.GetAsync(item);
		if (string.IsNullOrWhiteSpace(text))
		{
			return;
		}
		try
		{
			DetailPreviewImage.Source = new BitmapImage(new Uri(text));
		}
		catch
		{
		}
	}

	private void StopDetailPreview()
	{
		if ((object)_detailPlayer == null)
		{
			return;
		}
		try
		{
			_detailPlayer.Pause();
			_detailPlayer.Source = null;
		}
		catch
		{
		}
	}

	private void DetailBack_Click(object sender, RoutedEventArgs e)
	{
		_fpsProbeCts?.Cancel();
		StopDetailPreview();
		DetailPage.Visibility = Visibility.Collapsed;
		LibraryContent.Visibility = Visibility.Visible;
	}

	private async void Rename_Click(object sender, RoutedEventArgs e)
	{
		WallpaperItem selected = _selected;
		if (selected != null)
		{
			await RenameWallpaperAsync(selected);
		}
	}

	private async Task RenameWallpaperAsync(WallpaperItem item)
	{
		TextBox box = new TextBox
		{
			Text = item.Title,
			PlaceholderText = "输入新的壁纸名称",
			SelectionStart = item.Title.Length
		};
		if (await new ContentDialog
		{
			Title = "重命名壁纸",
			Content = box,
			PrimaryButtonText = "保存",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = Content.XamlRoot
		}.ShowAsync() != ContentDialogResult.Primary)
		{
			return;
		}
		string text = box.Text.Trim();
		if (string.IsNullOrWhiteSpace(text))
		{
			SetStatus("名称不能为空");
			return;
		}
		if (text.Length > 120)
		{
			text = text.Substring(0, 120);
		}
		item.Title = text;
		try
		{
			TitleOverrideStore.Set(item.Path, text, item.Kind);
			ConfigStore.Save(_config);
			ApplyFilter();
			ShowProperties(item);
			WallpaperGrid.SelectedItem = item;
			SetStatus("已重命名为“" + text + "”");
		}
		catch (Exception ex)
		{
			AppLog.Error("rename wallpaper failed", ex);
			SetStatus("重命名失败：" + ex.Message);
		}
	}

	private async void DeleteSelected_Click(object sender, RoutedEventArgs e)
	{
		var item = _selected ?? WallpaperGrid.SelectedItem as WallpaperItem;
		if (item is null)
		{
			SetStatus("请先选择要删除的壁纸");
			return;
		}
		await DeleteWallpapersAsync(new[] { item });
	}

	private async void Delete_Click(object sender, RoutedEventArgs e)
	{
		WallpaperItem item = _selected;
		if (item == null)
		{
			return;
		}
		List<string> sourcePaths = new List<string> { item.Path };
		if (item.Kind == WallpaperKind.Sequence && !string.IsNullOrWhiteSpace(item.SecondPath))
		{
			sourcePaths.Add(item.SecondPath);
		}
		ContentDialogResult result = await new ContentDialog
		{
			Title = "删除壁纸",
			Content = "“" + item.Title + "”要从 Wallerpaper 中删除吗？\n\n从库中移除：保留磁盘源文件\n同时删除源文件：永久删除主文件（不递归删除同目录素材）\n\n" + string.Join("\n", sourcePaths),
			PrimaryButtonText = "从库中移除",
			SecondaryButtonText = "同时删除源文件",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Close,
			XamlRoot = Content.XamlRoot
		}.ShowAsync();
		if (result == ContentDialogResult.None)
		{
			return;
		}
		try
		{
			List<string> sourceErrors = new List<string>();
			try
			{
				await RecycleBinService.AddAsync(item,
					deleteOriginal: result == ContentDialogResult.Secondary);
			}
			catch (Exception ex)
			{
				sourceErrors.Add("回收站：" + ex.Message);
			}
			if (item.Kind == WallpaperKind.Sequence)
			{
				_catalog.Remove(item);
			}
			else
			{
				if (!_config.ExcludedPaths.Any((string x) => string.Equals(x, item.Path, StringComparison.OrdinalIgnoreCase)))
				{
					_config.ExcludedPaths.Add(item.Path);
				}
				_config.Wallpapers.RemoveAll((WallpaperItem x) => string.Equals(x.Path, item.Path, StringComparison.OrdinalIgnoreCase));
			}
			var deletedIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { item.Id };
			var wasPlaying = _config.Monitors.Values.Any(x =>
				string.Equals(x.WallpaperId, item.Id, StringComparison.OrdinalIgnoreCase));
			var pauseAfterDelete =
				string.Equals(_config.Playback.DeleteCurrentBehavior, "pause",
					StringComparison.OrdinalIgnoreCase) &&
				_config.Monitors.Values.Any(x =>
					string.Equals(x.WallpaperId, item.Id, StringComparison.OrdinalIgnoreCase));
			var fallback = ChooseDeleteFallback(deletedIds);
			foreach (string device in (from x in _config.Monitors
				where string.Equals(x.Value.WallpaperId, item.Id, StringComparison.OrdinalIgnoreCase)
				select x.Key).ToList())
			{
				if (fallback is not null)
				{
					_config.Monitors[device] = new MonitorAssignment
					{
						WallpaperId = fallback.Id,
						Fps = _config.Performance.AcFps,
						Enabled = true
					};
				}
				else
				{
					_config.Monitors.Remove(device);
				}
			}
			ConfigStore.Save(_config);
			ClearSelection();
			await RefreshCatalogAsync();
			if (!wasPlaying)
			{
				// Removing an unapplied library item must not touch the live
				// renderer. Any Host restart here caused a visible flash/exit.
			}
			else if (pauseAfterDelete)
			{
				_host.Stop();
				DesktopWallpaperService.Restore();
			}
			else if (_host.IsRunning)
			{
				_host.Restart();
			}
			SetStatus((sourceErrors.Count == 0) ? ("已删除“" + item.Title + "”" + ((result == ContentDialogResult.Secondary) ? "（源文件已删除）" : "（源文件保留）")) : ("已从库删除，但部分源文件删除失败：" + string.Join("；", sourceErrors)));
		}
		catch (Exception ex2)
		{
			AppLog.Error("delete wallpaper failed", ex2);
			SetStatus("删除失败：" + ex2.Message);
		}
	}

	private async void AiInterpolate_Click(object sender, RoutedEventArgs e)
	{
		WallpaperItem item = _selected;
		bool flag = item == null;
		if (!flag)
		{
			WallpaperKind kind = item.Kind;
			bool flag2 = ((kind == WallpaperKind.Video || kind == WallpaperKind.Sequence) ? true : false);
			flag = !flag2;
		}
		if (flag)
		{
			return;
		}
		if (AiInterpolationService.FindRife() == null || AiInterpolationService.FindFfmpeg() == null)
		{
			await new ContentDialog
			{
				Title = "AI 插帧不可用",
				Content = "未找到 FFmpeg 或 RIFE。请把工具放到：\n" + AiInterpolationService.ToolRoot + "\n\n需要 rife-ncnn-vulkan.exe、rife-v4.6 模型和 ffmpeg.exe。",
				CloseButtonText = "知道了",
				XamlRoot = Content.XamlRoot
			}.ShowAsync();
			return;
		}
		ComboBox factorBox = new ComboBox
		{
			Header = "插帧倍数",
			SelectedIndex = 0,
			HorizontalAlignment = HorizontalAlignment.Stretch
		};
		factorBox.Items.Add("2×");
		factorBox.Items.Add("4×");
		double sourceFps = ((item.SourceFps > 0.1) ? item.SourceFps : 30.0);
		if (await new ContentDialog
		{
			Title = "AI 插帧",
			Content = new StackPanel
			{
				Spacing = 10.0,
				Children = 
				{
					(UIElement)new TextBlock
					{
						Text = $"源帧率约 {sourceFps:0.##} fps。\n" + "将用 RIFE 逐帧生成中间帧，重新编码为高帧率视频。\n处理时间可能较长，生成前会拆分 PNG 帧并占用临时磁盘空间。",
						TextWrapping = TextWrapping.Wrap
					},
					(UIElement)factorBox
				}
			},
			PrimaryButtonText = "开始生成",
			CloseButtonText = "取消",
			DefaultButton = ContentDialogButton.Primary,
			XamlRoot = Content.XamlRoot
		}.ShowAsync() != ContentDialogResult.Primary)
		{
			return;
		}
		int factor = ((factorBox.SelectedIndex == 1) ? 4 : 2);
		AiInterpolationService service = new AiInterpolationService();
		ProgressBar progressBar = new ProgressBar
		{
			Minimum = 0.0,
			Maximum = 100.0,
			Value = 0.0
		};
		TextBlock status = new TextBlock
		{
			Text = "准备…",
			TextWrapping = TextWrapping.Wrap
		};
		ContentDialog progressDialog = new ContentDialog
		{
			Title = $"AI 插帧 · {factor}×",
			Content = new StackPanel
			{
				Spacing = 12.0,
				Children = 
				{
					(UIElement)status,
					(UIElement)progressBar
				}
			},
			CloseButtonText = "取消",
			XamlRoot = Content.XamlRoot
		};
		CancellationTokenSource cts = new CancellationTokenSource();
		try
		{
			progressDialog.CloseButtonClick += (ContentDialog _, ContentDialogButtonClickEventArgs _) =>
			{
				cts.Cancel();
			};
			progressDialog.ShowAsync();
			await Task.Delay(120);
			try
			{
				AiInterpolationResult result = await service.InterpolateAsync(item.Path, sourceFps, factor, (int percent, string message) =>
				{
					DispatcherQueue.TryEnqueue(() =>
					{
						progressBar.Value = Math.Clamp(percent, 0, 100);
						status.Text = message;
					});
				}, cts.Token);
				progressDialog.Hide();
				AddLibraryRoot(result.OutputPath, WallpaperKind.Video, recursive: false);
				ConfigStore.Save(_config);
				await RefreshCatalogAsync();
				ApplyFilter();
				RevealWallpaper(result.OutputPath);
				SetStatus($"AI 插帧完成：{result.SourceFps:0.##} → {result.TargetFps:0.##} fps，已加入视频库");
			}
			catch (OperationCanceledException)
			{
				progressDialog.Hide();
				SetStatus("AI 插帧已取消");
			}
			catch (Exception ex2)
			{
				progressDialog.Hide();
				AppLog.Error("ai interpolation failed", ex2);
				await new ContentDialog
				{
					Title = "AI 插帧失败",
					Content = ex2.Message,
					CloseButtonText = "知道了",
					XamlRoot = Content.XamlRoot
				}.ShowAsync();
			}
		}
		finally
		{
			if (cts != null)
			{
				((IDisposable)cts).Dispose();
			}
		}
	}

	private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
	{
		VolumeValue.Text = $"{e.NewValue:F0}";
		WallpaperItem wallpaperItem = CurrentWallpaper();
		if (wallpaperItem != null && !(Math.Abs(wallpaperItem.Volume - e.NewValue) < 0.001))
		{
			wallpaperItem.Volume = e.NewValue;
			Persist();
			SendProperty("volume", wallpaperItem.Muted ? 0.0 : e.NewValue);
		}
	}

	private void SpeedSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
	{
		SpeedValue.Text = $"{e.NewValue:F1}×";
		WallpaperItem wallpaperItem = CurrentWallpaper();
		if (wallpaperItem != null && !(Math.Abs(wallpaperItem.Speed - e.NewValue) < 0.001))
		{
			wallpaperItem.Speed = e.NewValue;
			Persist();
			SendProperty("speed", e.NewValue);
		}
	}

	private void ShaderSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
	{
		ShaderValue.Text = $"{e.NewValue:F2}";
		WallpaperItem wallpaperItem = CurrentWallpaper();
		double num = ((wallpaperItem != null && wallpaperItem.ShaderParameters.TryGetValue("intensity", out var value)) ? value : 1.0);
		if (wallpaperItem != null && !(Math.Abs(num - e.NewValue) < 0.001))
		{
			wallpaperItem.ShaderParameters["intensity"] = e.NewValue;
			Persist();
			SendProperty("shader", e.NewValue);
		}
	}

	private void MuteToggle_Toggled(object sender, RoutedEventArgs e)
	{
		WallpaperItem wallpaperItem = CurrentWallpaper();
		if (wallpaperItem != null && wallpaperItem.Muted != MuteToggle.IsOn)
		{
			wallpaperItem.Muted = MuteToggle.IsOn;
			Persist();
			SendProperty("volume", MuteToggle.IsOn ? 0.0 : wallpaperItem.Volume);
		}
	}

	private void Persist()
	{
		try
		{
			_config.Wallpapers = _catalog.Items.ToList();
			ConfigStore.Save(_config);
		}
		catch (Exception ex)
		{
			SetStatus("保存配置失败：" + ex.Message);
		}
	}

	private void Apply_Click(object sender, RoutedEventArgs e)
	{
		ApplySelectedToTarget();
	}

	private void ApplyToMonitor_Click(object sender, RoutedEventArgs e)
	{
		ApplySelectedToTarget();
	}

	private void ApplySelectedToTarget()
	{
		WallpaperItem wallpaperItem = CurrentWallpaper();
		if (wallpaperItem != null && TargetMonitorCombo.SelectedIndex >= 0 && TargetMonitorCombo.SelectedIndex < _monitors.Count)
		{
			MonitorDisplay monitorDisplay = _monitors[TargetMonitorCombo.SelectedIndex];
			_config.Monitors.TryGetValue(monitorDisplay.Device, out var previousAssignment);
			var previousItem = previousAssignment is null ? null : _catalog.Find(previousAssignment.WallpaperId);
			if (string.Equals(previousAssignment?.WallpaperId, wallpaperItem.Id,
                    StringComparison.OrdinalIgnoreCase))
			{
				// Double-clicking the wallpaper that is already live must be a
				// no-op. Restarting here was what briefly dropped it out of
				// full-screen cover.
				SetStatus("当前正在播放：" + wallpaperItem.Title);
				return;
			}
			if (previousItem is not null && previousItem.Id != wallpaperItem.Id)
				PlaybackHistoryService.Record(previousItem);
			PlaybackHistoryService.Record(wallpaperItem);
			_config.Monitors[monitorDisplay.Device] = new MonitorAssignment
			{
				WallpaperId = wallpaperItem.Id,
				Fps = _config.Performance.AcFps,
				Enabled = true
			};
			Persist();
			if (wallpaperItem.Kind == WallpaperKind.Static)
			{
				_host.Stop();
				string imagePath;
				try
				{
					imagePath = StaticImageService.RenderAdjusted(wallpaperItem);
				}
				catch
				{
					imagePath = wallpaperItem.Path;
				}
				var applied = DesktopWallpaperService.SetImage(imagePath);
				SetStatus((applied ? "已应用静态壁纸：" : "静态壁纸应用失败：") +
				          wallpaperItem.Title);
				return;
			}
			var canLiveSwitch = wallpaperItem.Kind == WallpaperKind.Video &&
				!string.Equals(previousItem?.Path, wallpaperItem.Path, StringComparison.OrdinalIgnoreCase) &&
				_host.IsRunning;
			if (canLiveSwitch)
				ScheduleFastSwitch(monitorDisplay.Device, wallpaperItem.Path);
			else
				_host.Restart();
			SetStatus("已应用 " + wallpaperItem.Title + " → " + monitorDisplay.Label);
		}
	}

	private void ScheduleFastSwitch(string device, string path)
	{
		if (_fastSwitchDebounce.TryGetValue(device, out var previous))
		{
			try { previous.Cancel(); } catch { }
			previous.Dispose();
		}
		var cts = new CancellationTokenSource();
		_fastSwitchDebounce[device] = cts;
		_ = Task.Run(async () =>
		{
			try
			{
				// Coalesce rapid clicks. Three applies in one second should
				// produce one mpv loadfile, not three unload/load cycles.
				await Task.Delay(260, cts.Token);
				var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
				void OnApplied(string deviceName)
				{
					if (string.Equals(deviceName, device, StringComparison.OrdinalIgnoreCase))
						ack.TrySetResult(true);
				}
				void OnFailed(string deviceName)
				{
					if (string.Equals(deviceName, device, StringComparison.OrdinalIgnoreCase))
						ack.TrySetResult(false);
				}
				_host.SwitchApplied += OnApplied;
				_host.SwitchFailed += OnFailed;
				try
				{
					if (!_host.SwitchVideo(device, path))
					{
						ack.TrySetResult(false);
					}
					var completed = await Task.WhenAny(ack.Task, Task.Delay(1200, cts.Token));
					var switched = completed == ack.Task && await ack.Task;
					if (!switched)
					{
						// The live loadfile did not confirm; rebuild the host so
						// the desktop cannot remain stuck on the previous frame.
						_host.Restart();
					}
				}
				finally
				{
					_host.SwitchApplied -= OnApplied;
					_host.SwitchFailed -= OnFailed;
				}
			}
			catch (OperationCanceledException) { }
		}, cts.Token);
	}

	private WallpaperItem? CurrentWallpaper()
	{
		return _selected ?? (WallpaperGrid.SelectedItem as WallpaperItem);
	}

	private string? CurrentTargetDevice()
	{
		if (TargetMonitorCombo.SelectedIndex < 0 || TargetMonitorCombo.SelectedIndex >= _monitors.Count)
		{
			return null;
		}
		return _monitors[TargetMonitorCombo.SelectedIndex].Device;
	}

	private void SendProperty(string name, double value)
	{
		string text = CurrentTargetDevice();
		if (text != null && _host != null)
		{
			_host.SetProperty(text, name, value);
		}
	}

	private async void Preview_Click(object sender, RoutedEventArgs e)
	{
		WallpaperItem? item = CurrentWallpaper();
		if (item is null) return;

		var hwnd = WindowNative.GetWindowHandle(this);
		var screenshot = Path.Combine(Path.GetTempPath(),
			"wallerpaper-desktop-preview.png");
		try
		{
			// Capture the real desktop as a backdrop. The main window must be
			// out of the shot, so minimize it briefly; it stays minimized while
			// the full-screen preview is open.
			ShowWindow(hwnd, 6); // SW_MINIMIZE
			await Task.Delay(380);
			int width = GetSystemMetrics(0);
			int height = GetSystemMetrics(1);
			using (var bitmap = new System.Drawing.Bitmap(
                       Math.Max(1, width), Math.Max(1, height)))
            using (var graphics = System.Drawing.Graphics.FromImage(bitmap))
            {
                graphics.CopyFromScreen(0, 0, 0, 0, bitmap.Size);
                bitmap.Save(screenshot, System.Drawing.Imaging.ImageFormat.Png);
            }

			var preview = new DesktopPreviewWindow(item, screenshot, () =>
			{
				DispatcherQueue.TryEnqueue(() =>
				{
					try
					{
						ShowWindow(hwnd, 9); // SW_RESTORE
						AppWindow.Show();
						Activate();
					}
					catch { }
				});
			});
			preview.Activate();
		}
		catch
		{
			ShowWindow(hwnd, 9); // SW_RESTORE
			Activate();
			var fallback = new PreviewWindow(item);
			fallback.Activate();
			fallback.BringToFront();
		}
	}

	[DllImport("user32.dll")]
	private static extern bool ShowWindow(IntPtr hwnd, int command);

	[DllImport("user32.dll")]
	private static extern int GetSystemMetrics(int index);

	private void Pause_Click(object sender, RoutedEventArgs e)
	{
		TogglePause();
	}

	private void PauseNavItem_Tapped(object sender, TappedRoutedEventArgs e)
	{
		TogglePause();
		e.Handled = true;
	}

	private void TogglePause()
	{
		_paused = !_paused;
		if (_paused)
		{
			// Stop the renderer and remove its WorkerW child, then put the
			// normal Windows desktop wallpaper back. PauseAll alone leaves the
			// last decoded frame on screen.
			_host.Stop();
			DesktopWallpaperService.Restore();
		}
		else
		{
			_host.Start();
		}
		PauseLabel.Text = (_paused ? "播放" : "暂停");
		PauseIcon.Glyph = (_paused ? "\ue768" : "\ue769");
		_tray?.SetPaused(_paused);
		SetStatus(_paused ? "壁纸已暂停" : "壁纸已恢复");
	}

	private void SelectTrayWallpaper(WallpaperItem item)
	{
		DispatcherQueue.TryEnqueue(() =>
		{
			_kind = item.Kind;
			Nav.SelectedItem = Nav.MenuItems.FirstOrDefault((object x) => (x as NavigationViewItem)?.Tag as string == item.Kind.ToString());
			ApplyFilter();
			WallpaperGrid.SelectedItem = item;
			ApplySelectedToTarget();
		});
	}

	private async void Settings_Saved(object? sender, ShellConfig config)
	{
		try
		{
			ConfigStore.Save(config);
			_thumbnails = new ThumbnailService(config);
			_host.Restart();
			await RefreshCatalogAsync();
			ApplyFilter();
			SetStatus("设置已保存，调度器已重启");
		}
		catch (Exception ex)
		{
			SetStatus("保存设置失败：" + ex.Message);
		}
	}

	private void ShowSettings()
	{
		DispatcherQueue.TryEnqueue(() =>
		{
			AppWindow.Show();
			Activate();
			Nav.SelectedItem = Nav.MenuItems.FirstOrDefault((object x) => (x as NavigationViewItem)?.Tag as string == "Settings");
		});
	}

	public void ActivateFromSecondInstance() => ShowFromTray();

	private void ShowFromTray()
	{
		DispatcherQueue.TryEnqueue(() =>
		{
			AppWindow.Show();
			Activate();
		});
	}

	private void HideToTray()
	{
		_ = _config.General.MinimizeToTray;
		AppWindow.Hide();
	}

	private void ExitApp()
	{
		_exitRequested = true;
		_tray?.Dispose();
		_tray = null;
		_host.Dispose();
		Close();
		Application.Current.Exit();
	}

	private void OnClosing(AppWindow sender, AppWindowClosingEventArgs args)
	{
		if (!_exitRequested)
		{
			if (_config.General.MinimizeToTray)
			{
				args.Cancel = true;
				HideToTray();
			}
			else
			{
				_exitRequested = true;
				_host.Dispose();
				_tray?.Dispose();
			}
		}
	}

	private void OnClosed(object sender, WindowEventArgs args)
	{
		_fpsProbeCts?.Cancel();
		_fpsProbeCts?.Dispose();
		StopDetailPreview();
		_detailPlayer?.Dispose();
		_detailPlayer = null;
		_tray?.Dispose();
		_tray = null;
		_host.Dispose();
	}

	private void OnHostLog(string line)
	{
		string text;
		if (line.Contains("host started", StringComparison.OrdinalIgnoreCase))
		{
			text = "调度器已启动";
		}
		else if (line.Contains("host exited", StringComparison.OrdinalIgnoreCase))
		{
			text = "调度器已退出";
		}
		else if (line.Contains("property volume", StringComparison.OrdinalIgnoreCase))
		{
			text = "音量已应用";
		}
		else if (line.Contains("property speed", StringComparison.OrdinalIgnoreCase))
		{
			text = "速度已应用";
		}
		else if (line.Contains("property shader", StringComparison.OrdinalIgnoreCase))
		{
			text = "着色器参数已应用";
		}
		else
		{
			text = ((!line.StartsWith("ERR ", StringComparison.OrdinalIgnoreCase)) ? null : line);
		}
		string friendly = text;
		if (friendly != null)
		{
			DispatcherQueue.TryEnqueue(() =>
			{
				SetStatus(friendly);
			});
		}
	}

	private void SetStatus(string text)
	{
		if ((object)StatusText != null)
		{
			StatusText.Text = text;
		}
	}
}
