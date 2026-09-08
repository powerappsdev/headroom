using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Headroom.App.Interop;
using Headroom.App.Services;
using Headroom.App.ViewModels;
using Headroom.Core.Model;

namespace Headroom.App.Views;

public partial class HistoryWindow : Window
{
    private readonly AppHost _host;
    private bool _suppressSelection;

    public HistoryWindow(AppHost host)
    {
        _host = host;
        ViewModel = new HistoryViewModel(host.History);

        InitializeComponent();
        DataContext = ViewModel;

        Chart.WarningPercent = host.Settings.WarningPercent;
        Chart.CriticalPercent = host.Settings.CriticalPercent;
    }

    public HistoryViewModel ViewModel { get; }

    /// <summary>Shows the window focused on one account, refilling the account picker first.</summary>
    public void ShowForAccount(AccountSnapshot? account, bool darkMode)
    {
        var accounts = _host.Deck.Accounts.ToList();

        _suppressSelection = true;
        AccountPicker.ItemsSource = accounts;
        AccountPicker.SelectedItem = account ?? accounts.FirstOrDefault();
        _suppressSelection = false;

        Chart.WarningPercent = _host.Settings.WarningPercent;
        Chart.CriticalPercent = _host.Settings.CriticalPercent;

        Load(AccountPicker.SelectedItem as AccountSnapshot);

        Show();
        DwmEffects.Apply(this, darkMode, DwmEffects.Backdrop.Mica, DwmEffects.Corners.Round);
        WindowState = WindowState.Normal;
    }

    private void OnAccountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        Load(AccountPicker.SelectedItem as AccountSnapshot);
    }

    private void OnReloadClick(object sender, RoutedEventArgs e) =>
        ViewModel.Reload(DateTimeOffset.UtcNow);

    private void Load(AccountSnapshot? account)
    {
        if (account is null) return;
        ViewModel.SelectAccount(account.AccountId, account.DisplayName, DateTimeOffset.UtcNow);
    }
}
