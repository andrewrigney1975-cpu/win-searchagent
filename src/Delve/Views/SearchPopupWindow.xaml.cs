using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using Delve.Models;
using Delve.Services;
using H.NotifyIcon;
using Microsoft.UI;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;
using VirtualKey = Windows.System.VirtualKey;
using CoreVirtualKeyStates = Windows.UI.Core.CoreVirtualKeyStates;

namespace Delve.Views;

/// The centered, always-on-top, borderless search bar + results flyout - Delve's whole UI.
/// One instance is created lazily and reused (hidden, not closed, between hotkey presses) so
/// re-opening is instant and doesn't re-pay AppWindow/backdrop setup cost.
public sealed partial class SearchPopupWindow : Window
{
    private const int WindowWidth = 640;
    private const int CollapsedHeight = 64;
    private const int DebounceMs = 120;

    private readonly DocketIndexReader _indexReader;
    private readonly ShellIconCacheService _iconCache;
    private readonly ObservableCollection<SearchResultViewModel> _results = new();
    private readonly DispatcherQueueTimer _debounceTimer;
    private CancellationTokenSource? _searchCts;
    private AppWindow? _appWindow;

    public SearchPopupWindow(DocketIndexReader indexReader, ShellIconCacheService iconCache)
    {
        _indexReader = indexReader;
        _iconCache = iconCache;

        InitializeComponent();
        ResultsList.ItemsSource = _results;

        _debounceTimer = DispatcherQueue.CreateTimer();
        _debounceTimer.Interval = TimeSpan.FromMilliseconds(DebounceMs);
        _debounceTimer.IsRepeating = false;
        _debounceTimer.Tick += (_, _) =>
        {
            _debounceTimer.Stop();
            _ = RunSearchAsync(QueryBox.Text);
        };

        ConfigureWindowChrome();
        Activated += OnActivated;
    }

    private void ConfigureWindowChrome()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var windowId = Win32Interop.GetWindowIdFromWindow(hwnd);
        _appWindow = AppWindow.GetFromWindowId(windowId);

        if (_appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsAlwaysOnTop = true;
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.SetBorderAndTitleBar(false, false);
        }

        SystemBackdrop = new MicaBackdrop();
        ResizeAndCenter(CollapsedHeight);
    }

    /// Re-sizes and re-centers the window on whichever monitor currently has focus, converting
    /// the fixed logical (effective-pixel) dimensions to physical pixels for this window's DPI -
    /// AppWindow.ResizeClient/Move both operate in physical pixels, while XAML layout (and the
    /// dimensions in this file) are effective pixels.
    private void ResizeAndCenter(int logicalHeight)
    {
        if (_appWindow is null)
        {
            return;
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        var dpi = GetDpiForWindow(hwnd);
        var scale = dpi / 96.0;

        var width = (int)(WindowWidth * scale);
        var height = (int)(logicalHeight * scale);

        _appWindow.ResizeClient(new Windows.Graphics.SizeInt32(width, height));

        var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var x = workArea.X + (workArea.Width - width) / 2;
        var y = workArea.Y + (workArea.Height - height) / 3;
        _appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    public void ShowCentered()
    {
        QueryBox.Text = string.Empty;
        _results.Clear();
        ResultsList.Visibility = Visibility.Collapsed;
        ResizeAndCenter(CollapsedHeight);

        this.Show();
        this.Activate();
        QueryBox.Focus(FocusState.Programmatic);
    }

    public void HidePopup() => this.Hide();

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            HidePopup();
        }
    }

    private void RootGrid_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            HidePopup();
        }
    }

    private void QueryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _debounceTimer.Stop();
        _debounceTimer.Start();
    }

    private async Task RunSearchAsync(string query)
    {
        _searchCts?.Cancel();
        var cts = new CancellationTokenSource();
        _searchCts = cts;

        if (string.IsNullOrWhiteSpace(query))
        {
            _results.Clear();
            ResultsList.Visibility = Visibility.Collapsed;
            ResizeAndCenter(CollapsedHeight);
            return;
        }

        List<SearchResultItem> matches;
        try
        {
            matches = await _indexReader.SearchAsync(query, maxResults: 20, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (cts.IsCancellationRequested)
        {
            return;
        }

        _results.Clear();
        foreach (var match in matches)
        {
            var icon = _iconCache.GetIcon(match.Path, match.IsDirectory);
            _results.Add(new SearchResultViewModel(match, icon));
        }

        var showResults = _results.Count > 0;
        ResultsList.Visibility = showResults ? Visibility.Visible : Visibility.Collapsed;

        var rowHeight = 56;
        var resultsHeight = showResults ? Math.Min(_results.Count, 8) * rowHeight : 0;
        ResizeAndCenter(CollapsedHeight + resultsHeight);
    }

    private void ResultsList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SearchResultViewModel item)
        {
            return;
        }

        var ctrlDown = InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control)
            .HasFlag(CoreVirtualKeyStates.Down);

        if (ctrlDown)
        {
            ShellOpenService.RevealInExplorer(item.Path);
        }
        else
        {
            ShellOpenService.OpenDefault(item.Path);
        }

        HidePopup();
    }

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);
}
