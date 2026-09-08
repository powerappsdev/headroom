using System;

namespace Headroom.Core.Model;

/// <summary>
/// One rate-limit window as the provider reported it.
/// </summary>
/// <remarks>
/// Headroom stores <em>used</em> percent because that is what both providers
/// send, and displays <em>remaining</em> percent because that is the question a
/// human actually asks. Doing the flip in exactly one place (<see cref="RemainingPercent"/>)
/// is why the two can never drift apart.
/// </remarks>
public sealed record UsageWindow
{
    /// <summary>Display label, provider-stated where possible: "5-hour", "weekly", "Opus weekly".</summary>
    public required string Scope { get; init; }

    public required WindowKind Kind { get; init; }

    /// <summary>
    /// Percent of the window consumed, 0-100. Null is legitimate: a spend budget
    /// may state dollar amounts without a percentage.
    /// </summary>
    public double? UsedPercent { get; init; }

    /// <summary>When this window rolls over, if the provider stated it. Null is common and not an error.</summary>
    public DateTimeOffset? ResetsAt { get; init; }

    /// <summary>Payload-stated money amounts, for spend windows that carry them.</summary>
    public SpendAmount? Spend { get; init; }

    /// <summary>Percent of the window still available, 0-100, or null when the provider stated no percentage.</summary>
    public double? RemainingPercent =>
        UsedPercent is null ? null : Math.Clamp(100d - UsedPercent.Value, 0d, 100d);

    /// <summary>
    /// True when this window may headline an account card. Spend budgets are
    /// excluded: running out of a rate-limit window stops your work, spending
    /// your budget does not, and conflating them makes the headline lie.
    /// </summary>
    public bool CanHeadline => Kind != WindowKind.Spend && UsedPercent is not null;

    /// <summary>Sort order for display: shortest, most-binding window first.</summary>
    public int KindOrder => Kind switch
    {
        WindowKind.Session => 0,
        WindowKind.Weekly => 1,
        WindowKind.WeeklyScoped => 2,
        WindowKind.Other => 3,
        WindowKind.Spend => 4,
        _ => 5,
    };
}
