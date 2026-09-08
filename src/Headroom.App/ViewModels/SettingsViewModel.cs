using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Headroom.Core.Accounts;
using Headroom.Core.Model;
using Headroom.Core.Storage;

namespace Headroom.App.ViewModels;

/// <summary>An account row in Settings.</summary>
public sealed class AccountEditViewModel : ObservableObject
{
    private string _displayName;
    private string _profileDirectory;
    private bool _enabled;
    private ProviderKind _provider;

    public AccountEditViewModel(AccountDefinition definition)
    {
        Id = definition.Id;
        _displayName = definition.DisplayName;
        _profileDirectory = definition.ProfileDirectory;
        _enabled = definition.Enabled;
        _provider = definition.Provider;
    }

    public string Id { get; }

    public string DisplayName
    {
        get => _displayName;
        set => Set(ref _displayName, value);
    }

    public string ProfileDirectory
    {
        get => _profileDirectory;
        set => Set(ref _profileDirectory, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    public ProviderKind Provider
    {
        get => _provider;
        set
        {
            if (Set(ref _provider, value)) Raise(nameof(ProviderName));
        }
    }

    public string ProviderName => _provider == ProviderKind.Claude ? "Claude" : "Codex";

    public AccountDefinition ToDefinition() => new()
    {
        Id = Id,
        DisplayName = string.IsNullOrWhiteSpace(DisplayName) ? "Account" : DisplayName.Trim(),
        Provider = Provider,
        ProfileDirectory = ProfileDirectory?.Trim() ?? string.Empty,
        Enabled = Enabled,
    };
}

/// <summary>Everything in the Settings window, as plain values.</summary>
public sealed class SettingsViewModel : ObservableObject
{
    private int _refreshIntervalSeconds;
    private bool _autoRefresh;
    private double _warningPercent;
    private double _criticalPercent;
    private bool _notificationsEnabled;
    private bool _alwaysShowPercent;
    private bool _launchAtLogin;
    private string? _pinnedAccountId;
    private DeckTheme _theme;
    private DeckSort _sort;
    private int _historyRetentionDays;

    public SettingsViewModel(HeadroomSettings settings)
    {
        _refreshIntervalSeconds = settings.RefreshIntervalSeconds;
        _autoRefresh = settings.AutoRefresh;
        _warningPercent = settings.WarningPercent;
        _criticalPercent = settings.CriticalPercent;
        _notificationsEnabled = settings.NotificationsEnabled;
        _alwaysShowPercent = settings.AlwaysShowPercent;
        _launchAtLogin = settings.LaunchAtLogin;
        _pinnedAccountId = settings.PinnedAccountId;
        _theme = settings.Theme;
        _sort = settings.Sort;
        _historyRetentionDays = settings.HistoryRetentionDays;

        foreach (var account in settings.Accounts) Accounts.Add(new AccountEditViewModel(account));
    }

    public ObservableCollection<AccountEditViewModel> Accounts { get; } = new();

    /// <summary>Offered intervals. Anything faster than a minute would only annoy the provider.</summary>
    public IReadOnlyList<int> IntervalChoices { get; } = new[] { 60, 120, 300, 600, 900, 1800, 3600 };

    public IReadOnlyList<DeckTheme> ThemeChoices { get; } =
        new[] { DeckTheme.System, DeckTheme.Dark, DeckTheme.Light };

    public IReadOnlyList<DeckSort> SortChoices { get; } =
        new[] { DeckSort.NextReset, DeckSort.LowestRemaining, DeckSort.Provider };

    public int RefreshIntervalSeconds
    {
        get => _refreshIntervalSeconds;
        set => Set(ref _refreshIntervalSeconds, value);
    }

    public bool AutoRefresh
    {
        get => _autoRefresh;
        set => Set(ref _autoRefresh, value);
    }

    public double WarningPercent
    {
        get => _warningPercent;
        set => Set(ref _warningPercent, value);
    }

    public double CriticalPercent
    {
        get => _criticalPercent;
        set => Set(ref _criticalPercent, value);
    }

    public bool NotificationsEnabled
    {
        get => _notificationsEnabled;
        set => Set(ref _notificationsEnabled, value);
    }

    public bool AlwaysShowPercent
    {
        get => _alwaysShowPercent;
        set => Set(ref _alwaysShowPercent, value);
    }

    public bool LaunchAtLogin
    {
        get => _launchAtLogin;
        set => Set(ref _launchAtLogin, value);
    }

    public string? PinnedAccountId
    {
        get => _pinnedAccountId;
        set => Set(ref _pinnedAccountId, value);
    }

    public DeckTheme Theme
    {
        get => _theme;
        set => Set(ref _theme, value);
    }

    public DeckSort Sort
    {
        get => _sort;
        set => Set(ref _sort, value);
    }

    public int HistoryRetentionDays
    {
        get => _historyRetentionDays;
        set => Set(ref _historyRetentionDays, value);
    }

    public void AddAccount(ProviderKind provider)
    {
        var name = provider == ProviderKind.Claude ? "Claude account" : "Codex account";
        Accounts.Add(new AccountEditViewModel(new AccountDefinition
        {
            Id = AccountDefinition.NewId(),
            DisplayName = name,
            Provider = provider,
            ProfileDirectory = ProfileDiscovery.SuggestedProfileDirectory(provider, name),
        }));
    }

    public void Remove(AccountEditViewModel account) => Accounts.Remove(account);

    /// <summary>Folds the edited values back into a settings object, dropping incomplete rows.</summary>
    public HeadroomSettings ToSettings(HeadroomSettings original) => (original with
    {
        RefreshIntervalSeconds = RefreshIntervalSeconds,
        AutoRefresh = AutoRefresh,
        WarningPercent = WarningPercent,
        CriticalPercent = CriticalPercent,
        NotificationsEnabled = NotificationsEnabled,
        AlwaysShowPercent = AlwaysShowPercent,
        LaunchAtLogin = LaunchAtLogin,
        PinnedAccountId = string.IsNullOrWhiteSpace(PinnedAccountId) ? null : PinnedAccountId,
        Theme = Theme,
        Sort = Sort,
        HistoryRetentionDays = HistoryRetentionDays,
        Accounts = Accounts
            .Select(a => a.ToDefinition())
            .Where(a => !string.IsNullOrWhiteSpace(a.ProfileDirectory))
            .ToList(),
    }).Normalized();
}
