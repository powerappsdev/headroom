using System;
using System.Collections.Generic;
using System.Linq;
using Headroom.Core.Model;

namespace Headroom.Core.Alerts;

/// <summary>One account crossing from one severity band into another.</summary>
public sealed record BandTransition(
    string AccountId,
    string DisplayName,
    ProviderKind Provider,
    AlertBand From,
    AlertBand To,
    double RemainingPercent,
    string WindowScope,
    DateTimeOffset? ResetsAt)
{
    /// <summary>True when things got worse, which is the only direction worth interrupting someone for.</summary>
    public bool IsWorsening => To > From;

    public string NotificationTitle => To switch
    {
        AlertBand.Critical => $"{DisplayName}: {RemainingPercent:0}% left",
        AlertBand.Warning => $"{DisplayName}: running low",
        _ => $"{DisplayName}: recovered",
    };

    public string NotificationBody
    {
        get
        {
            var window = string.IsNullOrWhiteSpace(WindowScope) ? "limit" : WindowScope;
            if (To == AlertBand.Healthy)
                return $"Back above the warning threshold on its {window} window.";

            var reset = ResetsAt is { } at
                ? $" Resets {at.ToLocalTime():ddd h:mm tt}."
                : string.Empty;
            return $"{RemainingPercent:0}% left on the {window} window.{reset}";
        }
    }
}

/// <summary>
/// Emits a notification only when an account <em>crosses</em> a threshold.
/// </summary>
/// <remarks>
/// A meter that re-notifies every poll while you sit at 9% is a meter people
/// mute, and a muted meter cannot warn anyone. So state is remembered per
/// account and a transition fires exactly once, at the crossing. Accounts that
/// lose their data entirely are forgotten rather than treated as recovered,
/// which stops a failed refresh from firing a cheerful "recovered" alert.
/// </remarks>
public sealed class ThresholdWatcher
{
    private readonly Dictionary<string, AlertBand> _bands = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Compares a new deck against remembered state and returns the crossings.</summary>
    public IReadOnlyList<BandTransition> Evaluate(DeckSnapshot deck, Thresholds thresholds)
    {
        var transitions = new List<BandTransition>();
        var live = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var account in deck.Accounts)
        {
            if (account.WorstWindow is not { RemainingPercent: { } remaining } window) continue;

            live.Add(account.AccountId);
            var band = thresholds.Band(remaining);

            if (_bands.TryGetValue(account.AccountId, out var previous))
            {
                if (previous != band)
                {
                    transitions.Add(new BandTransition(
                        account.AccountId,
                        account.DisplayName,
                        account.Provider,
                        previous,
                        band,
                        remaining,
                        window.Scope,
                        window.ResetsAt));
                }
            }
            else if (band != AlertBand.Healthy)
            {
                // First sighting already below a threshold: worth saying once, so
                // that launching the app while low is not silent.
                transitions.Add(new BandTransition(
                    account.AccountId,
                    account.DisplayName,
                    account.Provider,
                    AlertBand.Healthy,
                    band,
                    remaining,
                    window.Scope,
                    window.ResetsAt));
            }

            _bands[account.AccountId] = band;
        }

        // An account that stopped reporting is forgotten, not "recovered".
        foreach (var stale in _bands.Keys.Where(k => !live.Contains(k)).ToList())
            _bands.Remove(stale);

        return transitions;
    }

    /// <summary>Current remembered band, for tests and for restoring UI state.</summary>
    public AlertBand? BandFor(string accountId) =>
        _bands.TryGetValue(accountId, out var band) ? band : null;

    public void Reset() => _bands.Clear();
}
