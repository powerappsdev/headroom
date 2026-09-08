using System;

namespace Headroom.Core.Refresh;

/// <summary>
/// Decides how long to wait after a failed probe.
/// </summary>
/// <remarks>
/// Two rules matter. First, a provider that states a <c>Retry-After</c> knows
/// more than we do, so its value always wins when it is longer than ours.
/// Second, every delay carries jitter, because several accounts that all failed
/// at the same moment would otherwise retry in lockstep forever and turn one
/// rate-limit response into a self-sustaining stampede.
/// </remarks>
public sealed class BackoffPolicy
{
    private readonly Random _random;

    public BackoffPolicy(
        TimeSpan? baseDelay = null,
        TimeSpan? maxDelay = null,
        double jitterFraction = 0.2d,
        Random? random = null)
    {
        BaseDelay = baseDelay ?? TimeSpan.FromSeconds(30);
        MaxDelay = maxDelay ?? TimeSpan.FromMinutes(30);
        JitterFraction = Math.Clamp(jitterFraction, 0d, 0.9d);
        _random = random ?? Random.Shared;
    }

    public TimeSpan BaseDelay { get; }

    public TimeSpan MaxDelay { get; }

    public double JitterFraction { get; }

    /// <summary>
    /// The delay after <paramref name="consecutiveFailures"/> failures, honouring
    /// a provider-stated <paramref name="retryAfter"/>.
    /// </summary>
    public TimeSpan DelayFor(int consecutiveFailures, TimeSpan? retryAfter = null)
    {
        var delay = Unjittered(consecutiveFailures);
        if (retryAfter is { } stated && stated > delay) delay = stated;
        if (delay > MaxDelay) delay = MaxDelay;
        return ApplyJitter(delay);
    }

    /// <summary>The deterministic part of the curve, exposed so tests can assert the shape.</summary>
    public TimeSpan Unjittered(int consecutiveFailures)
    {
        if (consecutiveFailures <= 0) return TimeSpan.Zero;

        // Cap the exponent before it is applied so a long outage cannot overflow
        // the multiplication into a negative or infinite delay.
        var exponent = Math.Min(consecutiveFailures - 1, 16);
        var seconds = BaseDelay.TotalSeconds * Math.Pow(2d, exponent);
        var capped = Math.Min(seconds, MaxDelay.TotalSeconds);
        return TimeSpan.FromSeconds(capped);
    }

    private TimeSpan ApplyJitter(TimeSpan delay)
    {
        if (JitterFraction <= 0d || delay <= TimeSpan.Zero) return delay;
        var swing = delay.TotalSeconds * JitterFraction;
        var offset = (_random.NextDouble() * 2d - 1d) * swing;
        var jittered = Math.Max(0d, delay.TotalSeconds + offset);
        return TimeSpan.FromSeconds(jittered);
    }
}
