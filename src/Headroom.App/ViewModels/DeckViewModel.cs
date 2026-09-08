using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using Headroom.Core.Formatting;
using Headroom.Core.Model;
using Headroom.Core.Refresh;
using Headroom.Core.Storage;

namespace Headroom.App.ViewModels;

/// <summary>The deck popover: the account list plus the honest footer.</summary>
public sealed class DeckViewModel : ObservableObject
{
    private readonly StalenessEvaluator _staleness = new();

    private string _footerText = "Waiting for first refresh";
    private StalenessTone _footerTone = StalenessTone.Current;
    private string _updatedText = "Never refreshed";
    private bool _isRefreshing;
    private bool _isEmpty = true;

    public ObservableCollection<AccountCardViewModel> Cards { get; } = new();

    public string FooterText
    {
        get => _footerText;
        private set => Set(ref _footerText, value);
    }

    /// <summary>
    /// Amber only for staleness nothing else explains. Everything else stays
    /// neutral, which is what keeps amber meaningful.
    /// </summary>
    public StalenessTone FooterTone
    {
        get => _footerTone;
        private set => Set(ref _footerTone, value);
    }

    public bool FooterIsAlarming => _footerTone == StalenessTone.Unexplained;

    public string UpdatedText
    {
        get => _updatedText;
        private set => Set(ref _updatedText, value);
    }

    public bool IsRefreshing
    {
        get => _isRefreshing;
        set => Set(ref _isRefreshing, value);
    }

    /// <summary>True when no accounts are configured, so the view can show a welcome instead of a void.</summary>
    public bool IsEmpty
    {
        get => _isEmpty;
        private set => Set(ref _isEmpty, value);
    }

    /// <summary>Rebuilds the card list from a deck snapshot, reusing view models so scroll and expansion survive.</summary>
    public void Apply(
        DeckSnapshot deck,
        HeadroomSettings settings,
        DateTimeOffset now)
    {
        var thresholds = settings.ToThresholds();
        var ordered = Sort(deck.Accounts, settings.Sort).ToList();

        var byId = Cards.ToDictionary(c => c.AccountId, StringComparer.OrdinalIgnoreCase);
        Cards.Clear();

        foreach (var snapshot in ordered)
        {
            if (byId.TryGetValue(snapshot.AccountId, out var existing))
            {
                existing.Update(snapshot, thresholds, now);
                Cards.Add(existing);
            }
            else
            {
                Cards.Add(new AccountCardViewModel(snapshot, thresholds, now));
            }
        }

        var traySource = deck.TrayAccount(settings.PinnedAccountId);
        foreach (var card in Cards)
        {
            card.IsTraySource = traySource is not null &&
                string.Equals(card.AccountId, traySource.AccountId, StringComparison.OrdinalIgnoreCase);
        }

        var summary = _staleness.Evaluate(deck, now, settings.RefreshInterval);
        FooterText = summary.Text;
        FooterTone = summary.Tone;
        Raise(nameof(FooterIsAlarming));

        var newest = deck.Accounts
            .Where(a => a.ObservedAt is not null)
            .Select(a => a.ObservedAt!.Value)
            .DefaultIfEmpty()
            .Max();

        UpdatedText = newest == default ? "Never refreshed" : DisplayText.UpdatedAgo(newest, now);
        IsEmpty = Cards.Count == 0;
    }

    private static IEnumerable<AccountSnapshot> Sort(IReadOnlyList<AccountSnapshot> accounts, DeckSort sort) => sort switch
    {
        DeckSort.LowestRemaining => accounts
            .OrderBy(a => a.RemainingPercent ?? double.MaxValue)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase),

        DeckSort.Provider => accounts
            .OrderBy(a => a.Provider)
            .ThenBy(a => a.NextReset ?? DateTimeOffset.MaxValue)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase),

        _ => accounts
            .OrderBy(a => a.NextReset ?? DateTimeOffset.MaxValue)
            .ThenBy(a => a.DisplayName, StringComparer.CurrentCultureIgnoreCase),
    };
}
