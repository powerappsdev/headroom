using System;
using System.Collections.Generic;
using System.Linq;
using Headroom.Core.Model;

namespace Headroom.Core.Refresh;

public enum StalenessTone
{
    /// <summary>Everything that can be current is current.</summary>
    Current,

    /// <summary>Some data is old, and every piece of it is old for a reason the card already states.</summary>
    Explained,

    /// <summary>Some data is old for no reason we can account for. This is the one worth an alarm.</summary>
    Unexplained,
}

/// <summary>The footer line, and which accounts caused it.</summary>
public sealed record StalenessSummary(StalenessTone Tone, string Text, IReadOnlyList<string> Offenders)
{
    public static readonly StalenessSummary Nothing = new(StalenessTone.Current, "No accounts configured", Array.Empty<string>());
}

/// <summary>
/// Works out whether old data is a problem.
/// </summary>
/// <remarks>
/// This is the piece most usage meters get wrong. An idle or signed-out account
/// holding week-old numbers is behaving exactly as it should, and painting that
/// amber teaches people to ignore the colour — which means they also ignore it
/// on the day a refresh really is silently failing. So staleness is only ever
/// raised when nothing else on the card already explains it.
/// </remarks>
public sealed class StalenessEvaluator
{
    private readonly TimeSpan _floor;
    private readonly double _intervalMultiple;

    public StalenessEvaluator(TimeSpan? floor = null, double intervalMultiple = 2.5d)
    {
        _floor = floor ?? TimeSpan.FromMinutes(15);
        _intervalMultiple = Math.Max(1d, intervalMultiple);
    }

    /// <summary>Data older than this is considered stale for a given refresh cadence.</summary>
    public TimeSpan StaleAfter(TimeSpan refreshInterval)
    {
        var scaled = TimeSpan.FromSeconds(refreshInterval.TotalSeconds * _intervalMultiple);
        return scaled > _floor ? scaled : _floor;
    }

    public StalenessSummary Evaluate(DeckSnapshot deck, DateTimeOffset now, TimeSpan refreshInterval)
    {
        if (deck.Accounts.Count == 0) return StalenessSummary.Nothing;

        var threshold = StaleAfter(refreshInterval);
        var unexplained = new List<AccountSnapshot>();
        var fresh = 0;
        var idle = 0;
        var signedOut = 0;
        var blocked = 0;
        var unconfigured = 0;

        foreach (var account in deck.Accounts)
        {
            switch (account.Availability)
            {
                case AccountAvailability.Idle: idle++; break;
                case AccountAvailability.SignedOut: signedOut++; break;
                case AccountAvailability.CredentialBlocked: blocked++; break;
                case AccountAvailability.NotConfigured: unconfigured++; break;
            }

            if (account.StalenessIsExplained) continue;

            var age = account.ObservedAt is { } observed ? now - observed : TimeSpan.MaxValue;
            if (age > threshold) unexplained.Add(account);
            else fresh++;
        }

        if (unexplained.Count > 0)
        {
            var oldest = unexplained
                .OrderByDescending(a => a.ObservedAt is { } o ? now - o : TimeSpan.MaxValue)
                .First();

            var age = oldest.ObservedAt is { } observedAt
                ? DurationText(now - observedAt) + " old"
                : "never refreshed";

            var text = unexplained.Count == 1
                ? $"{oldest.DisplayName}: {age}"
                : $"{unexplained.Count} accounts stale, oldest {oldest.DisplayName} ({age})";

            return new StalenessSummary(
                StalenessTone.Unexplained,
                text,
                unexplained.Select(a => a.AccountId).ToList());
        }

        var segments = new List<string>();
        if (fresh > 0) segments.Add(fresh == 1 ? "Live account current" : "Live accounts current");
        if (idle > 0) segments.Add($"{idle} idle");
        if (signedOut > 0) segments.Add($"{signedOut} signed out");
        if (blocked > 0) segments.Add($"{blocked} blocked");
        if (unconfigured > 0) segments.Add($"{unconfigured} not set up");

        var tone = (idle + signedOut + blocked + unconfigured) > 0
            ? StalenessTone.Explained
            : StalenessTone.Current;

        return new StalenessSummary(
            tone,
            segments.Count == 0 ? "Waiting for first refresh" : string.Join(" · ", segments),
            Array.Empty<string>());
    }

    /// <summary>Compact age text: "4 min", "3 h", "2 d".</summary>
    public static string DurationText(TimeSpan span)
    {
        if (span < TimeSpan.Zero) span = TimeSpan.Zero;
        if (span.TotalMinutes < 1) return "under a minute";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} min";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} h";
        return $"{(int)span.TotalDays} d";
    }
}
