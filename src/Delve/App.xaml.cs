using Delve.Services;
using Delve.Views;
using H.NotifyIcon;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Delve;

/// Delve has no main window at startup - it lives entirely in the tray until the hotkey (or
/// the tray's "Open" item) summons SearchPopupWindow. Composition root for the handful of
/// long-lived services wired up here.
public partial class App : Application
{
    private TaskbarIcon? _trayIcon;
    private GlobalHotkeyService? _hotkeyService;
    private DocketAvailabilityService? _availabilityService;
    private DocketIndexReader? _indexReader;
    private ShellIconCacheService? _iconCache;
    private SearchPopupWindow? _popup;
    private DispatcherQueue? _uiDispatcher;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => e.Handled = true;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _uiDispatcher = DispatcherQueue.GetForCurrentThread();
        _indexReader = new DocketIndexReader(DocketIndexReader.DefaultDbPath);
        _iconCache = new ShellIconCacheService();

        _hotkeyService = new GlobalHotkeyService();
        _hotkeyService.Start(OnHotkeyPressed);

        InitializeTrayIcon();

        _availabilityService = new DocketAvailabilityService(_indexReader);
        _availabilityService.AvailabilityChanged += OnAvailabilityChanged;
        _availabilityService.Start();
    }

    private void InitializeTrayIcon()
    {
        _trayIcon = (TaskbarIcon)Resources["TrayIcon"];

        var menu = (MenuFlyout)_trayIcon.ContextFlyout!;
        foreach (var item in menu.Items)
        {
            if (item is not MenuFlyoutItem menuItem)
            {
                continue;
            }

            switch (menuItem.Text)
            {
                case "Open":
                    menuItem.Click += (_, _) => ShowPopup();
                    break;
                case "Hide":
                    menuItem.Click += (_, _) => HidePopup();
                    break;
                case "Quit":
                    menuItem.Click += (_, _) => Quit();
                    break;
            }
        }

        _trayIcon.IconSource = LoadTrayIcon(available: false);
        _trayIcon.ForceCreate();
    }

    private static Microsoft.UI.Xaml.Media.Imaging.BitmapImage LoadTrayIcon(bool available) =>
        new(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets",
            available ? "TrayIconActive.ico" : "TrayIconInactive.ico")));

    private void OnAvailabilityChanged(object? sender, DocketAvailabilityChangedEventArgs e)
    {
        var available = e.Availability == DocketAvailability.Available;

        _hotkeyService?.SetEnabled(available);

        _uiDispatcher?.TryEnqueue(() =>
        {
            if (_trayIcon is null)
            {
                return;
            }

            _trayIcon.IconSource = LoadTrayIcon(available);
            _trayIcon.ToolTipText = available
                ? "Delve — Ctrl+Win+D to search"
                : "Delve — Docket search index not available";
        });
    }

    /// GlobalHotkeyService fires this from its own dedicated message-loop thread - hop back to
    /// the UI thread before touching any WinUI object.
    private void OnHotkeyPressed()
    {
        _uiDispatcher?.TryEnqueue(ShowPopup);
    }

    private void ShowPopup()
    {
        if (_popup is null)
        {
            _popup = new SearchPopupWindow(_indexReader!, _iconCache!);
            _popup.Closed += (_, _) => _popup = null;
        }

        _popup.ShowCentered();
    }

    private void HidePopup() => _popup?.HidePopup();

    private void Quit()
    {
        _availabilityService?.Dispose();
        _hotkeyService?.Dispose();
        _popup?.Close();
        _trayIcon?.Dispose();
        Environment.Exit(0);
    }
}
