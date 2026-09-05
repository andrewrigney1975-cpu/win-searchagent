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
    private const int RowHeight = 56;
    private const int MaxVisibleResults = 8;
    private const int ExpandedHeight = CollapsedHeight + MaxVisibleResults * RowHeight;
    private const int DebounceMs = 120;

    private readonly DocketIndexReader _indexReader;
    private readonly ShellIconCacheService _iconCache;
    private readonly ObservableCollection<SearchResultViewModel> _results = new();
    private readonly DispatcherQueueTimer _debounceTimer;
    private CancellationTokenSource? _searchCts;
    private AppWindow? _appWindow;
    private bool _isExpanded;

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

        // SetBorderAndTitleBar(false, false) removes the window chrome, but Windows 11's DWM
        // still draws its own ~1px accent-colored active-window border and rounded-corner frame
        // on top of that by default - on a borderless custom-chrome window this is exactly what
        // rendered as an inconsistent single/double-width edge (some sides get the DWM border,
        // some don't, depending on how it interacts with the window's own corner radius).
        // Explicitly disabling both via DwmSetWindowAttribute removes DWM's own frame entirely,
        // leaving only what this window's own XAML content draws.
        var noBorder = DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref noBorder, sizeof(uint));
        var noRound = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref noRound, sizeof(uint));

        // Size the window (still hidden at this point - a WinUI3 Window isn't shown until
        // Show()/Activate() is first called) *before* attaching the Mica backdrop, so the
        // backdrop's swapchain initializes at its final size instead of being resized right
        // after creation - resizing a just-attached backdrop surface is what produced the
        // diagonal tearing/ghost-pixel artifacts seen on the very first open.
        _isExpanded = false;
        ResizeAndCenter(CollapsedHeight);
        SystemBackdrop = new MicaBackdrop();
    }

    /// Re-sizes and re-centers the window on whichever monitor currently has focus, converting
    /// the fixed logical (effective-pixel) dimensions to physical pixels for this window's DPI -
    /// AppWindow.MoveAndResize operates in physical pixels, while XAML layout (and the
    /// dimensions in this file) are effective pixels. Uses one atomic MoveAndResize call rather
    /// than separate Resize+Move calls, which was producing an extra visible composition pass
    /// per call (part of the same rendering-artifact family reported on first open).
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

        var displayArea = DisplayArea.GetFromWindowId(_appWindow.Id, DisplayAreaFallback.Primary);
        var workArea = displayArea.WorkArea;
        var x = workArea.X + (workArea.Width - width) / 2;

        // Anchored on CollapsedHeight, not the height being resized to, so the search box's own
        // top edge never moves - only the window's bottom edge drops down as results appear.
        // Computing this from the current (variable) height instead made the whole box - search
        // bar included - visibly slide up the screen every time results appeared.
        var collapsedHeight = (int)(CollapsedHeight * scale);
        var y = workArea.Y + (workArea.Height - collapsedHeight) / 3;

        _appWindow.MoveAndResize(new Windows.Graphics.RectInt32(x, y, width, height));
    }

    public void ShowCentered()
    {
        QueryBox.Text = string.Empty;
        _results.Clear();
        ResultsList.Visibility = Visibility.Collapsed;

        if (_isExpanded)
        {
            _isExpanded = false;
            ResizeAndCenter(CollapsedHeight);
        }

        var hwnd = WindowNative.GetWindowHandle(this);
        _appWindow?.Show();
        ForceForeground(hwnd);

        // SetForegroundWindow only *posts* WM_ACTIVATE/WM_SETFOCUS - calling FocusManager
        // synchronously, right after, races those messages: this thread's own message loop
        // hasn't drained them yet, so the window doesn't yet consider itself focused when asked
        // to focus a child control. Posting via TryEnqueue lets those already-queued messages
        // process first. TryFocusInBox also retries a couple of times with a short delay, since
        // even after that there can be one more frame of lag before the XamlRoot accepts input.
        DispatcherQueue.TryEnqueue(() => _ = TryFocusQueryBoxAsync());
    }

    /// Windows' foreground-lock rules block a background process's own SetForegroundWindow call
    /// unless it shares input state with the thread that currently owns the foreground window -
    /// confirmed by hands-on testing, where Window.Activate()/AppWindow.Show(activateWindow:
    /// true) left focus in whatever app had it before the hotkey was pressed.
    /// AttachThreadInput does exactly that; it's the standard bypass used by launcher-style apps
    /// (PowerToys Run, Wox, etc.) for this "global shortcut summons a window" scenario.
    private static void ForceForeground(IntPtr hwnd)
    {
        var foregroundWindow = GetForegroundWindow();
        var foregroundThreadId = GetWindowThreadProcessId(foregroundWindow, out _);
        var currentThreadId = GetCurrentThreadId();

        var attached = foregroundThreadId != 0
            && foregroundThreadId != currentThreadId
            && AttachThreadInput(currentThreadId, foregroundThreadId, true);

        try
        {
            SetForegroundWindow(hwnd);
            BringWindowToTop(hwnd);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(currentThreadId, foregroundThreadId, false);
            }
        }
    }

    private async Task TryFocusQueryBoxAsync()
    {
        for (var attempt = 0; attempt < 5; attempt++)
        {
            var result = await FocusManager.TryFocusAsync(QueryBox, FocusState.Programmatic);
            if (result is not null && result.Succeeded)
            {
                return;
            }

            await Task.Delay(30);
        }
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
            SetExpanded(false);
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
        SetExpanded(showResults);
    }

    /// The window only ever has two sizes - collapsed (search box only) and expanded (search
    /// box + a fixed-height results area, however many of the up-to-20 results actually fit).
    /// Resizing to a new height per keystroke as the result count fluctuated between 1 and 20
    /// was the other contributor to the composition artifacts reported on open - a single fixed
    /// expanded size, changed only on the collapsed/expanded transition, resizes the window at
    /// most once per search session instead of once per keystroke.
    private void SetExpanded(bool expanded)
    {
        if (expanded == _isExpanded)
        {
            return;
        }

        _isExpanded = expanded;
        ResizeAndCenter(expanded ? ExpandedHeight : CollapsedHeight);
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

    private const uint DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const uint DWMWA_BORDER_COLOR = 34;
    private const uint DWMWCP_DONOTROUND = 1;
    private const uint DWMWA_COLOR_NONE = 0xFFFFFFFE;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, uint dwAttribute, ref uint pvAttribute, uint cbAttribute);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);
}
