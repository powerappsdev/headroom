using System;
using System.Collections.Generic;
using System.Linq;

namespace Headroom.Core.Model;

/// <summary>
/// The whole deck at one moment: every account, plus the single number the tray
/// icon shows and the honest summary of how trustworthy that number is.
/// </summary>
public sealed record DeckSnapshot
{
    public IReadOnlyList<AccountSnapshot> Accounts { get; init; } = Array.Empty<AccountSnapshot>();

    public DateTimeOffset GeneratedAt { get; init; }

    public static DeckSnapshot Empty => new() { GeneratedAt = DateTimeOffset.MinValue };

    /// <summary>
    /// The account whose worst window currently owns the tray percentage:
    /// the lowest remaining across every account. Null when nothing has data.
    /// </summary>
    public AccountSnapshot? LowestAccount => Accounts
        .Where(a => a.RemainingPercent is not null)
        .OrderBy(a => a.RemainingPercent!.Value)
        .ThenBy(a => a.DisplayName, StringComparer.OrdinalIgnoreCase)
        .FirstOrDefault();

    /// <summary>
    /// The account the tray should display, honouring a pin. An unresolvable pin
    /// falls back to lowest-across rather than showing nothing, so a stale pin
    /// never looks like a broken app.
    /// </summary>
    public AccountSnapshot? TrayAccount(string? pinnedAccountId)
    {
        if (!string.IsNullOrWhiteSpace(pinnedAccountId))
        {
            var pinned = Accounts.FirstOrDefault(a =>
                string.Equals(a.AccountId, pinnedAccountId, StringComparison.OrdinalIgnoreCase));
            if (pinned is { RemainingPercent: not null }) return pinned;
        }

        return LowestAccount;
    }

    public IReadOnlyList<AccountSnapshot> ByProvider(ProviderKind provider) => Accounts
        .Where(a => a.Provider == provider)
        .ToList();

    public bool AnyConfigured => Accounts.Count > 0;
}
