using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Headroom.Core.Formatting;
using Headroom.Core.Model;

namespace Headroom.App.ViewModels;

/// <summary>One rate-limit window row inside an expanded card.</summary>
public sealed class WindowRowViewModel : ObservableObject
{
    private UsageWindow _window;
    private Thresholds _thresholds;
    private DateTimeOffset _now;

    public WindowRowViewModel(UsageWindow window, Thresholds thresholds, DateTimeOffset now)
    {
        _window = window;
        _thresholds = thresholds;
        _now = now;
    }

    public string Scope => _window.Scope;

    public double? RemainingPercent => _window.RemainingPercent;

    /// <summary>Bar fill fraction, 0-1. An unknown window draws an empty track, not a full one.</summary>
    public double Fill => (_window.RemainingPercent ?? 0d) / 100d;

    public MeterTone Tone => _window.RemainingPercent is null
        ? MeterTone.Unknown
        : _thresholds.Band(_window.RemainingPercent) switch
        {
            AlertBand.Critical => MeterTone.Critical,
            AlertBand.Warning => MeterTone.Warning,
            _ => MeterTone.Healthy,
        };

    /// <summary>Money text for a spend row, which replaces the bare percentage.</summary>
    public string? SpendText => DisplayText.SpendText(_window.Spend);

    public string ValueText => SpendText ?? DisplayText.PercentLeft(_window.RemainingPercent);

    /// <summary>Empty when the provider stated no reset. An empty slot beats a placeholder.</summary>
    public string ResetText => DisplayText.ResetText(_window.ResetsAt, _now);

    public string ResetTooltip => _window.ResetsAt is null
        ? string.Empty
        : $"{DisplayText.ResetTooltip(_window.ResetsAt)}  ({DisplayText.TimeUntil(_window.ResetsAt, _now)})";

    public bool HasReset => _window.ResetsAt is not null;

    public void Update(UsageWindow window, Thresholds thresholds, DateTimeOffset now)
    {
        _window = window;
        _thresholds = thresholds;
        _now = now;
        RaiseMany(
            nameof(Scope), nameof(RemainingPercent), nameof(Fill), nameof(Tone),
            nameof(SpendText), nameof(ValueText), nameof(ResetText), nameof(ResetTooltip), nameof(HasReset));
    }
}

/// <summary>One account card in the deck.</summary>
public sealed class AccountCardViewModel : ObservableObject
{
    private AccountSnapshot _snapshot;
    private Thresholds _thresholds;
    private DateTimeOffset _now;
    private bool _isExpanded;
    private bool _isTraySource;

    public AccountCardViewModel(AccountSnapshot snapshot, Thresholds thresholds, DateTimeOffset now)
    {
        _snapshot = snapshot;
        _thresholds = thresholds;
        _now = now;
        Rebuild();
    }

    public string AccountId => _snapshot.AccountId;

    public string DisplayName => _snapshot.DisplayName;

    public ProviderKind Provider => _snapshot.Provider;

    public bool IsClaude => _snapshot.Provider == ProviderKind.Claude;

    /// <summary>"Claude · Max 20x", or just the provider when no plan was stated.</summary>
    public string SubtitleText
    {
        get
        {
            var provider = _snapshot.Provider == ProviderKind.Claude ? "Claude" : "Codex";
            var plan = DisplayText.PlanLabel(_snapshot.PlanTier);
            return plan is null ? provider : $"{provider} · {plan}";
        }
    }

    public string? Identity => _snapshot.Identity;

    public ObservableCollection<WindowRowViewModel> Windows { get; } = new();

    public double? RemainingPercent => _snapshot.RemainingPercent;

    public double Fill => (_snapshot.RemainingPercent ?? 0d) / 100d;

    public MeterTone Tone => _snapshot.RemainingPercent is null
        ? MeterTone.Unknown
        : _thresholds.Band(_snapshot.RemainingPercent) switch
        {
            AlertBand.Critical => MeterTone.Critical,
            AlertBand.Warning => MeterTone.Warning,
            _ => MeterTone.Healthy,
        };

    public string HeadlineValue => DisplayText.PercentLeft(_snapshot.RemainingPercent);

    public string HeadlineScope => _snapshot.WorstWindow?.Scope ?? string.Empty;

    public string HeadlineReset => DisplayText.ResetText(_snapshot.WorstWindow?.ResetsAt, _now);

    public string UpdatedText => DisplayText.UpdatedAgo(_snapshot.ObservedAt, _now);

    /// <summary>Short notice such as "Idle — renews on next use". Empty when all is well.</summary>
    public string NoticeText => DisplayText.AvailabilityNotice(_snapshot.Availability);

    public bool HasNotice => _snapshot.Availability != AccountAvailability.Ok;

    /// <summary>
    /// True for notices that deserve attention. Idle is deliberately excluded:
    /// it is normal, and painting it amber trains people to ignore amber.
    /// </summary>
    public bool NoticeIsAlarming => _snapshot.Availability is
        AccountAvailability.SignedOut or
        AccountAvailability.CredentialBlocked or
        AccountAvailability.ProviderUnreachable;

    public string NoticeTooltip => _snapshot.Detail ?? NoticeText;

    public bool HasData => _snapshot.HasData;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => Set(ref _isExpanded, value);
    }

    /// <summary>Marks the one account whose percentage is currently in the tray.</summary>
    public bool IsTraySource
    {
        get => _isTraySource;
        set => Set(ref _isTraySource, value);
    }

    public void Update(AccountSnapshot snapshot, Thresholds thresholds, DateTimeOffset now)
    {
        _snapshot = snapshot;
        _thresholds = thresholds;
        _now = now;
        Rebuild();

        RaiseMany(
            nameof(DisplayName), nameof(SubtitleText), nameof(Identity), nameof(RemainingPercent),
            nameof(Fill), nameof(Tone), nameof(HeadlineValue), nameof(HeadlineScope), nameof(HeadlineReset),
            nameof(UpdatedText), nameof(NoticeText), nameof(HasNotice), nameof(NoticeIsAlarming),
            nameof(NoticeTooltip), nameof(HasData), nameof(IsClaude));
    }

    private void Rebuild()
    {
        var ordered = _snapshot.OrderedWindows;

        // Reuse existing rows so expanding a card does not flicker on refresh.
        for (var i = 0; i < ordered.Count; i++)
        {
            if (i < Windows.Count) Windows[i].Update(ordered[i], _thresholds, _now);
            else Windows.Add(new WindowRowViewModel(ordered[i], _thresholds, _now));
        }

        while (Windows.Count > ordered.Count) Windows.RemoveAt(Windows.Count - 1);
    }
}
