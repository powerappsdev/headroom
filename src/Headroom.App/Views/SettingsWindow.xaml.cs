using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Headroom.App.Interop;
using Headroom.App.ViewModels;
using Headroom.Core.Accounts;
using Headroom.Core.Model;
using Headroom.Core.Storage;

namespace Headroom.App.Views;

public partial class SettingsWindow : Window
{
    private readonly HeadroomSettings _original;

    public SettingsWindow(HeadroomSettings settings, bool darkMode)
    {
        _original = settings;
        ViewModel = new SettingsViewModel(settings);

        InitializeComponent();
        DataContext = ViewModel;

        DataPathText.Text = $"Settings and history live in {new HeadroomPaths().Root}";

        SourceInitialized += (_, _) =>
            DwmEffects.Apply(this, darkMode, DwmEffects.Backdrop.Mica, DwmEffects.Corners.Round);
    }

    public SettingsViewModel ViewModel { get; }

    /// <summary>Raised with the edited settings when the user saves.</summary>
    public event EventHandler<HeadroomSettings>? Saved;

    private void OnAddClaudeClick(object sender, RoutedEventArgs e) =>
        ViewModel.AddAccount(ProviderKind.Claude);

    private void OnAddCodexClick(object sender, RoutedEventArgs e) =>
        ViewModel.AddAccount(ProviderKind.Codex);

    /// <summary>Adopts any profile folders on disk that are not already listed.</summary>
    private void OnDiscoverClick(object sender, RoutedEventArgs e)
    {
        var known = ViewModel.Accounts
            .Select(a => a.ProfileDirectory)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var added = 0;
        foreach (var found in ProfileDiscovery.Discover())
        {
            if (!known.Add(found.ProfileDirectory)) continue;
            ViewModel.Accounts.Add(new AccountEditViewModel(found));
            added++;
        }

        MessageBox.Show(
            this,
            added == 0
                ? "No profile folders were found that are not already listed."
                : $"Added {added} profile folder{(added == 1 ? string.Empty : "s")}.",
            "Find existing profiles",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private void OnRemoveAccountClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AccountEditViewModel account) return;

        var answer = MessageBox.Show(
            this,
            $"Remove \"{account.DisplayName}\" from Headroom?\n\n"
            + "This only removes Headroom's reference to the folder. Your sign-in and the folder itself are left exactly as they are.",
            "Remove account",
            MessageBoxButton.OKCancel,
            MessageBoxImage.Question);

        if (answer == MessageBoxResult.OK) ViewModel.Remove(account);
    }

    private void OnBrowseClick(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not AccountEditViewModel account) return;

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "Choose the profile folder for this account",
            Multiselect = false,
        };

        if (!string.IsNullOrWhiteSpace(account.ProfileDirectory) &&
            System.IO.Directory.Exists(account.ProfileDirectory))
        {
            dialog.InitialDirectory = account.ProfileDirectory;
        }

        if (dialog.ShowDialog(this) == true) account.ProfileDirectory = dialog.FolderName;
    }

    private void OnOpenDataFolderClick(object sender, RoutedEventArgs e)
    {
        var paths = new HeadroomPaths();
        paths.EnsureCreated();

        try
        {
            Process.Start(new ProcessStartInfo(paths.Root) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or System.IO.IOException)
        {
            MessageBox.Show(this, paths.Root, "Data folder", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        Saved?.Invoke(this, ViewModel.ToSettings(_original));
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e) => Close();
}
