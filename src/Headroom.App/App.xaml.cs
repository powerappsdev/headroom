using System;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Headroom.App.Services;
using Headroom.App.Startup;
using Headroom.App.Tray;
using Headroom.App.Views;
using Headroom.Core.Model;
using Microsoft.Win32;

namespace Headroom.App;

[System.Diagnostics.CodeAnalysis.SuppressMessage(
    "Design",
    "CA1001:Types that own disposable fields should be disposable",
    Justification = "A WPF Application's lifetime is owned by the framework, not by a caller holding a reference. Everything disposable here is released in OnExit, which is the framework's teardown hook.")]
public partial class App : Application
{
    private SingleInstance? _instance;
    private AppHost? _host;
    private TrayController? _tray;
    private ThemeManager? _theme;
    private DeckWindow? _deck;
    private SettingsWindow? _settings;
    private HistoryWindow? _history;
    private DispatcherTimer? _refreshTimer;
    private DispatcherTimer? _clockTimer;
    private bool _refreshInFlight;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instance = SingleInstance.Acquire();
        if (!_instance.IsFirstInstance)
        {
            // A second launch is a request to see the deck, not a second copy.
            _instance.SignalExistingInstance();
            Shutdown();
            return;
        }

        _instance.ShowRequested += (_, _) => Dispatcher.BeginInvoke(ShowDeck);
        _instance.StartListening();

        DispatcherUnhandledException += OnUnhandledException;

        _host = new AppHost();
        _theme = new ThemeManager();
        _theme.Apply(_host.Settings.Theme);

        _tray = new TrayController();
        _tray.OpenRequested += (_, _) => ToggleDeck();
        _tray.RefreshRequested += (_, _) => _ = RefreshAsync(force: true);
        _tray.SettingsRequested += (_, _) => ShowSettings();
        _tray.HistoryRequested += (_, _) => ShowHistory(null);
        _tray.QuitRequested += (_, _) => Shutdown();

        _host.DeckUpdated += OnDeckUpdated;

        // MeterBar and HistoryChart resolve their colours in OnRender rather than
        // through DynamicResource, so a palette swap has to force them to repaint.
        _theme.ThemeChanged += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            InvalidateVisualTree(_deck);
            InvalidateVisualTree(_history);
        });

        SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;

        _refreshTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = _host.Settings.RefreshInterval,
        };
        _refreshTimer.Tick += (_, _) =>
        {
            if (_host?.Settings.AutoRefresh == true) _ = RefreshAsync(force: false);
        };
        _refreshTimer.Start();

        // Relative times ("Updated 4 min ago", "Resets in 2 h") go stale on their
        // own, so the deck re-renders them on a slow tick even with no new data.
        _clockTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(30),
        };
        _clockTimer.Tick += (_, _) => RenderDeck();
        _clockTimer.Start();

        _ = RefreshAsync(force: true);
    }

    private async Task RefreshAsync(bool force)
    {
        if (_host is null || _refreshInFlight) return;

        _refreshInFlight = true;
        if (_deck is not null) _deck.ViewModel.IsRefreshing = true;

        try
        {
            await _host.RefreshAsync(force).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // A refresh must never take the app down. The deck's own per-account
            // states already describe whatever went wrong.
        }
        finally
        {
            _refreshInFlight = false;
            if (_deck is not null) _deck.ViewModel.IsRefreshing = false;
        }
    }

    private void OnDeckUpdated(object? sender, DeckSnapshot deck) =>
        Dispatcher.BeginInvoke(() =>
        {
            RenderDeck();
            RaiseNotifications(deck);
        });

    private void RenderDeck()
    {
        if (_host is null || _tray is null) return;

        var settings = _host.Settings;
        var deck = _host.Deck;

        _tray.Update(deck, settings.PinnedAccountId, settings.AlwaysShowPercent, settings.ToThresholds());
        _deck?.ViewModel.Apply(deck, settings, DateTimeOffset.UtcNow);
    }

    private void RaiseNotifications(DeckSnapshot deck)
    {
        if (_host is null || _tray is null || !_host.Settings.NotificationsEnabled) return;

        foreach (var transition in _host.Alerts.Evaluate(deck, _host.Settings.ToThresholds()))
        {
            // Recovery is remembered so the next dip fires again, but it is not
            // worth interrupting anyone over.
            if (!transition.IsWorsening) continue;

            _tray.Notify(
                transition.NotificationTitle,
                transition.NotificationBody,
                warning: transition.To == AlertBand.Critical);
        }
    }

    private void ToggleDeck()
    {
        // Treat a click that lands immediately after a focus-loss hide as the
        // close half of the toggle: the deck was still open when the user aimed
        // at the icon, so reopening it would ignore what they actually asked for.
        var justHidden = _deck is not null &&
                         DateTimeOffset.UtcNow - _deck.HiddenAt < TimeSpan.FromMilliseconds(300);

        if (_deck is { IsVisible: true } || justHidden)
        {
            _deck?.Hide();
            return;
        }

        ShowDeck();
    }

    private void ShowDeck()
    {
        if (_host is null) return;

        if (_deck is null)
        {
            _deck = new DeckWindow();
            _deck.RefreshRequested += (_, _) => _ = RefreshAsync(force: true);
            _deck.SettingsRequested += (_, _) => ShowSettings();
            _deck.HistoryRequested += (_, id) => ShowHistory(id);
        }

        _deck.ViewModel.Apply(_host.Deck, _host.Settings, DateTimeOffset.UtcNow);
        _deck.ShowNearTray(_theme?.IsDark ?? true);

        // Opening the deck is the moment someone actually wants current numbers.
        if (_host.Settings.AutoRefresh) _ = RefreshAsync(force: false);
    }

    private void ShowSettings()
    {
        if (_host is null) return;

        if (_settings is null || !_settings.IsLoaded)
        {
            _settings = new SettingsWindow(_host.Settings, _theme?.IsDark ?? true);
            _settings.Saved += async (_, updated) =>
            {
                await _host.ApplySettingsAsync(updated, OnDeckUpdated).ConfigureAwait(true);
                _theme?.Apply(_host.Settings.Theme);
                AutoStart.Set(_host.Settings.LaunchAtLogin);
                if (_refreshTimer is not null) _refreshTimer.Interval = _host.Settings.RefreshInterval;
                RenderDeck();
                await RefreshAsync(force: true).ConfigureAwait(true);
            };
            _settings.Closed += (_, _) => _settings = null;
        }

        _settings.Show();
        _settings.Activate();
    }

    private void ShowHistory(string? accountId)
    {
        if (_host is null) return;

        if (_history is null)
        {
            _history = new HistoryWindow(_host);
            _history.Closed += (_, _) => _history = null;
        }

        var accounts = _host.Deck.Accounts;
        var account = accountId is null
            ? (accounts.Count > 0 ? accounts[0] : null)
            : accounts.FirstOrDefault(a =>
                string.Equals(a.AccountId, accountId, StringComparison.OrdinalIgnoreCase));

        _history.ShowForAccount(account, _theme?.IsDark ?? true);
        _history.Activate();
    }

    private void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
    {
        if (e.Category is not (UserPreferenceCategory.General or UserPreferenceCategory.VisualStyle)) return;

        Dispatcher.BeginInvoke(() =>
        {
            _theme?.Refresh();
            RenderDeck();
        });
    }

    private static void InvalidateVisualTree(DependencyObject? root)
    {
        if (root is null) return;
        if (root is UIElement element) element.InvalidateVisual();

        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            InvalidateVisualTree(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // Keep the tray icon alive: a crashed usage meter that silently vanishes
        // is worse than one that reports a bad moment and carries on.
        try
        {
            System.IO.File.AppendAllText(
                _host?.Paths.LogFile ?? "headroom.log",
                $"{DateTimeOffset.Now:O} {e.Exception}{Environment.NewLine}");
        }
        catch (Exception logFailure) when (logFailure is System.IO.IOException or UnauthorizedAccessException)
        {
            // Nothing useful left to do about a failed log write.
        }

        e.Handled = true;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
        _refreshTimer?.Stop();
        _clockTimer?.Stop();
        _tray?.Dispose();
        _host?.Dispose();
        _instance?.Dispose();
        base.OnExit(e);
    }
}
