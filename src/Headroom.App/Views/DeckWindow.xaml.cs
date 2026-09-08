using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Headroom.App.Interop;
using Headroom.App.ViewModels;

namespace Headroom.App.Views;

public partial class DeckWindow : Window
{
    public DeckWindow()
    {
        InitializeComponent();
        DataContext = ViewModel;

        // A popover that outlives a click elsewhere is a popover people learn to
        // dismiss twice. Losing focus closes it, exactly like the Windows flyouts.
        Deactivated += (_, _) =>
        {
            HiddenAt = DateTimeOffset.UtcNow;
            Hide();
        };

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Hide();
        };
    }

    public DeckViewModel ViewModel { get; } = new();

    /// <summary>
    /// When the deck last hid itself because it lost focus. Clicking the tray
    /// icon deactivates this window before the click is delivered, so without
    /// this the icon could only ever open the deck, never close it.
    /// </summary>
    public DateTimeOffset HiddenAt { get; private set; } = DateTimeOffset.MinValue;

    public event EventHandler? RefreshRequested;

    public event EventHandler? SettingsRequested;

    /// <summary>Carries the account id whose history was asked for, or null for "whatever is first".</summary>
    public event EventHandler<string?>? HistoryRequested;

    /// <summary>Shows the deck against the notification area, applying the current theme.</summary>
    public void ShowNearTray(bool darkMode)
    {
        Show();

        // Measure before positioning: SizeToContent means ActualHeight is only
        // meaningful once layout has run, and placing first puts the window in
        // the wrong spot on the very first open.
        UpdateLayout();

        DwmEffects.Apply(this, darkMode, DwmEffects.Backdrop.Acrylic, DwmEffects.Corners.Round);
        TrayPositioning.PlaceNearTray(this);

        Activate();
        Focus();
    }

    private void OnRefreshClick(object sender, RoutedEventArgs e) =>
        RefreshRequested?.Invoke(this, EventArgs.Empty);

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        Hide();
        SettingsRequested?.Invoke(this, EventArgs.Empty);
    }

    private void OnHistoryClick(object sender, RoutedEventArgs e)
    {
        Hide();
        HistoryRequested?.Invoke(this, null);
    }

    private void OnCardHistoryClick(object sender, RoutedEventArgs e)
    {
        // Stops the click from also toggling the card it sits inside.
        e.Handled = true;
        Hide();
        HistoryRequested?.Invoke(this, (sender as Button)?.Tag as string);
    }
}
