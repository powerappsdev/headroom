using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Headroom.Core.Accounts;
using Headroom.Core.Model;

namespace Headroom.Core.Providers;

/// <summary>
/// The result of one attempt to read an account's usage.
/// </summary>
/// <remarks>
/// A probe never throws for an expected condition. Signed out, idle, rate
/// limited and unreachable are all ordinary answers with their own shape,
/// because the difference between them is exactly what the user needs to see.
/// </remarks>
public sealed record ProbeResult
{
    public required AccountAvailability Availability { get; init; }

    public IReadOnlyList<UsageWindow> Windows { get; init; } = Array.Empty<UsageWindow>();

    public string? Identity { get; init; }

    public string? PlanTier { get; init; }

    /// <summary>Human-readable explanation shown on the card. Never a stack trace.</summary>
    public string? Detail { get; init; }

    /// <summary>
    /// Set when the provider asked us to slow down. The scheduler honours this
    /// before its own backoff, because the provider knows better than we do.
    /// </summary>
    public TimeSpan? RetryAfter { get; init; }

    /// <summary>True when this attempt failed in a way that should escalate backoff.</summary>
    public bool IsTransientFailure { get; init; }

    public bool Succeeded => Availability == AccountAvailability.Ok && Windows.Count > 0;

    public static ProbeResult Success(
        IReadOnlyList<UsageWindow> windows, string? identity = null, string? planTier = null) => new()
    {
        Availability = AccountAvailability.Ok,
        Windows = windows,
        Identity = identity,
        PlanTier = planTier,
    };

    public static ProbeResult Unavailable(
        AccountAvailability availability,
        string? detail,
        string? identity = null,
        bool transient = false,
        TimeSpan? retryAfter = null) => new()
    {
        Availability = availability,
        Detail = detail,
        Identity = identity,
        IsTransientFailure = transient,
        RetryAfter = retryAfter,
    };
}

/// <summary>Reads one account's current usage from its provider.</summary>
public interface IUsageProvider
{
    ProviderKind Kind { get; }

    Task<ProbeResult> ProbeAsync(AccountDefinition account, CancellationToken cancellationToken = default);
}
