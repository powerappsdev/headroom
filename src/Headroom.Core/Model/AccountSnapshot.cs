using System;
using System.Collections.Generic;
using System.Linq;

namespace Headroom.Core.Model;

/// <summary>
/// Everything Headroom knows about one account at one moment, including the
/// reason it knows nothing when that is the case.
/// </summary>
public sealed record AccountSnapshot
{
    public required string AccountId { get; init; }

    public required string DisplayName { get; init; }

    public required ProviderKind Provider { get; init; }

    /// <summary>Account email, when the provider revealed one. Display only.</summary>
    public string? Identity { get; init; }

    /// <summary>Plan tier exactly as the provider stated it ("max_20x", "pro"). Never inferred.</summary>
    public string? PlanTier { get; init; }

    public IReadOnlyList<UsageWindow> Windows { get; init; } = Array.Empty<UsageWindow>();

    /// <summary>When the data in <see cref="Windows"/> was read. Null when it never has been.</summary>
    public DateTimeOffset? ObservedAt { get; init; }

    public AccountAvailability Availability { get; init; } = AccountAvailability.Ok;

    /// <summary>A human-readable explanation for a non-Ok availability. Never a raw exception dump.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// True when this account is expected to be stale, so the UI must not raise
    /// an alarm about its age. An idle or signed-out account holding week-old
    /// numbers is behaving correctly.
    /// </summary>
    public bool StalenessIsExplained => Availability is
        AccountAvailability.Idle or
        AccountAvailability.SignedOut or
        AccountAvailability.CredentialBlocked or
        AccountAvailability.NotConfigured;

    public bool HasData => Windows.Count > 0;

    /// <summary>
    /// The window that decides how this account is doing: the lowest remaining
    /// percentage among windows allowed to headline. Null when nothing qualifies.
    /// </summary>
    public UsageWindow? WorstWindow => Windows
        .Where(w => w.CanHeadline)
        .OrderBy(w => w.RemainingPercent ?? double.MaxValue)
        .ThenBy(w => w.KindOrder)
        .FirstOrDefault();

    public double? RemainingPercent => WorstWindow?.RemainingPercent;

    /// <summary>The soonest reset among all windows that stated one.</summary>
    public DateTimeOffset? NextReset => Windows
        .Where(w => w.ResetsAt is not null)
        .Select(w => w.ResetsAt!.Value)
        .DefaultIfEmpty()
        .Min() is { } min && min != default ? min : null;

    public IReadOnlyList<UsageWindow> OrderedWindows => Windows
        .OrderBy(w => w.KindOrder)
        .ThenBy(w => w.RemainingPercent ?? double.MaxValue)
        .ToList();
}
