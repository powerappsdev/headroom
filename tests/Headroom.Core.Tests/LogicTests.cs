using System;
using System.Collections.Generic;
using System.Linq;
using Headroom.Core.Alerts;
using Headroom.Core.Formatting;
using Headroom.Core.Model;
using Headroom.Core.Refresh;

namespace Headroom.Core.Tests;

internal static class Sample
{
    public static AccountSnapshot Account(
        string id,
        double? remainingPercent,
        AccountAvailability availability = AccountAvailability.Ok,
        DateTimeOffset? observedAt = null,
        string? name = null)
    {
        var windows = remainingPercent is { } remaining
            ? new[]
            {
                new UsageWindow
                {
                    Scope = "5-hour",
                    Kind = WindowKind.Session,
                    UsedPercent = 100d - remaining,
                    ResetsAt = DateTimeOffset.Parse("2026-09-07T20:00:00Z"),
                },
            }
            : Array.Empty<UsageWindow>();

        return new AccountSnapshot
        {
            AccountId = id,
            DisplayName = name ?? id,
            Provider = ProviderKind.Claude,
            Windows = windows,
            ObservedAt = observedAt,
            Availability = availability,
        };
    }

    public static DeckSnapshot Deck(params AccountSnapshot[] accounts) =>
        new() { Accounts = accounts, GeneratedAt = DateTimeOffset.Parse("2026-09-07T12:00:00Z") };
}

public static class ThresholdTests
{
    [Test]
    public static void BandBoundariesAreInclusive()
    {
        var thresholds = new Thresholds(25, 10);

        Check.Equal(AlertBand.Healthy, thresholds.Band(26));
        Check.Equal(AlertBand.Warning, thresholds.Band(25));
        Check.Equal(AlertBand.Warning, thresholds.Band(11));
        Check.Equal(AlertBand.Critical, thresholds.Band(10));
        Check.Equal(AlertBand.Critical, thresholds.Band(0));
    }

    [Test("No data is not an alarm")]
    public static void NullIsHealthy() => Check.Equal(AlertBand.Healthy, Thresholds.Default.Band(null));

    [Test("Nonsense settings are clamped into an ordered pair")]
    public static void NormalizationClamps()
    {
        var normalized = new Thresholds(-5, 200).Normalized();
        Check.True(normalized.CriticalPercent <= normalized.WarningPercent);
        Check.True(normalized.WarningPercent is >= 1 and <= 99);
    }
}

public static class ThresholdWatcherTests
{
    [Test("A crossing fires once, and staying low stays quiet")]
    public static void FiresOnlyOnTransition()
    {
        var watcher = new ThresholdWatcher();
        var thresholds = new Thresholds(25, 10);

        Check.Count(0, watcher.Evaluate(Sample.Deck(Sample.Account("a", 80)), thresholds));

        var crossing = watcher.Evaluate(Sample.Deck(Sample.Account("a", 20)), thresholds);
        Check.Count(1, crossing);
        Check.Equal(AlertBand.Warning, crossing[0].To);
        Check.True(crossing[0].IsWorsening);

        Check.Count(0, watcher.Evaluate(Sample.Deck(Sample.Account("a", 18)), thresholds));
        Check.Count(0, watcher.Evaluate(Sample.Deck(Sample.Account("a", 12)), thresholds));

        var critical = watcher.Evaluate(Sample.Deck(Sample.Account("a", 8)), thresholds);
        Check.Count(1, critical);
        Check.Equal(AlertBand.Critical, critical[0].To);
    }

    [Test("Launching while already low says something once")]
    public static void FirstSightingBelowThresholdFires()
    {
        var watcher = new ThresholdWatcher();
        var transitions = watcher.Evaluate(Sample.Deck(Sample.Account("a", 5)), new Thresholds(25, 10));

        Check.Count(1, transitions);
        Check.Equal(AlertBand.Critical, transitions[0].To);
    }

    [Test("Launching healthy says nothing")]
    public static void FirstSightingHealthyIsSilent() =>
        Check.Count(0, new ThresholdWatcher().Evaluate(Sample.Deck(Sample.Account("a", 90)), Thresholds.Default));

    [Test("Recovery is reported, and marked as not worsening")]
    public static void RecoveryIsReported()
    {
        var watcher = new ThresholdWatcher();
        var thresholds = new Thresholds(25, 10);

        watcher.Evaluate(Sample.Deck(Sample.Account("a", 5)), thresholds);
        var recovery = watcher.Evaluate(Sample.Deck(Sample.Account("a", 90)), thresholds);

        Check.Count(1, recovery);
        Check.Equal(AlertBand.Healthy, recovery[0].To);
        Check.False(recovery[0].IsWorsening);
    }

    [Test("An account that stops reporting is forgotten, not congratulated")]
    public static void DisappearingAccountDoesNotFireRecovery()
    {
        var watcher = new ThresholdWatcher();
        var thresholds = new Thresholds(25, 10);

        watcher.Evaluate(Sample.Deck(Sample.Account("a", 5)), thresholds);
        Check.Count(0, watcher.Evaluate(Sample.Deck(Sample.Account("a", null)), thresholds));
        Check.Null(watcher.BandFor("a"));
    }

    [Test]
    public static void NotificationTextNamesTheWindow()
    {
        var watcher = new ThresholdWatcher();
        var transition = watcher.Evaluate(Sample.Deck(Sample.Account("a", 7, name: "Work")), Thresholds.Default)[0];

        Check.Contains("Work", transition.NotificationTitle);
        Check.Contains("7%", transition.NotificationTitle);
        Check.Contains("5-hour", transition.NotificationBody);
    }
}

public static class StalenessTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-07T12:00:00Z");
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);

    [Test]
    public static void FreshDataIsCurrent()
    {
        var summary = new StalenessEvaluator().Evaluate(
            Sample.Deck(Sample.Account("a", 50, observedAt: Now.AddMinutes(-2))), Now, Interval);

        Check.Equal(StalenessTone.Current, summary.Tone);
        Check.Contains("current", summary.Text);
    }

    [Test("An idle account holding week-old numbers is behaving correctly, not failing")]
    public static void IdleStalenessIsExplained()
    {
        var summary = new StalenessEvaluator().Evaluate(
            Sample.Deck(
                Sample.Account("a", 50, observedAt: Now.AddMinutes(-2)),
                Sample.Account("b", 30, AccountAvailability.Idle, Now.AddDays(-7))),
            Now,
            Interval);

        Check.Equal(StalenessTone.Explained, summary.Tone);
        Check.Contains("1 idle", summary.Text);
        Check.Count(0, summary.Offenders);
    }

    [Test("A silent refresh failure is the one thing worth an alarm, and it names the offender")]
    public static void UnexplainedStalenessIsFlagged()
    {
        var summary = new StalenessEvaluator().Evaluate(
            Sample.Deck(
                Sample.Account("a", 50, observedAt: Now.AddMinutes(-2)),
                Sample.Account("b", 30, AccountAvailability.Ok, Now.AddHours(-4), name: "Side Project")),
            Now,
            Interval);

        Check.Equal(StalenessTone.Unexplained, summary.Tone);
        Check.Contains("Side Project", summary.Text);
        Check.Count(1, summary.Offenders);
    }

    [Test("The stale threshold never drops below a sane floor for fast poll intervals")]
    public static void ThresholdHasAFloor()
    {
        var evaluator = new StalenessEvaluator(TimeSpan.FromMinutes(15), 2.5d);

        Check.Equal(TimeSpan.FromMinutes(15), evaluator.StaleAfter(TimeSpan.FromMinutes(1)));
        Check.Equal(TimeSpan.FromMinutes(75), evaluator.StaleAfter(TimeSpan.FromMinutes(30)));
    }

    [Test]
    public static void NoAccountsIsNotAnAlarm() =>
        Check.Equal(StalenessTone.Current, new StalenessEvaluator().Evaluate(Sample.Deck(), Now, Interval).Tone);
}

public static class BackoffTests
{
    [Test]
    public static void DelayDoublesAndThenCaps()
    {
        var policy = new BackoffPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), jitterFraction: 0);

        Check.Equal(TimeSpan.Zero, policy.Unjittered(0));
        Check.Equal(TimeSpan.FromSeconds(30), policy.Unjittered(1));
        Check.Equal(TimeSpan.FromSeconds(60), policy.Unjittered(2));
        Check.Equal(TimeSpan.FromSeconds(120), policy.Unjittered(3));
        Check.Equal(TimeSpan.FromMinutes(30), policy.Unjittered(12));
        Check.Equal(TimeSpan.FromMinutes(30), policy.Unjittered(500), "a long outage must not overflow the curve");
    }

    [Test("A provider that states Retry-After knows better than we do")]
    public static void RetryAfterWinsWhenLonger()
    {
        var policy = new BackoffPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), jitterFraction: 0);

        Check.Equal(TimeSpan.FromMinutes(10), policy.DelayFor(1, TimeSpan.FromMinutes(10)));
        Check.Equal(TimeSpan.FromSeconds(30), policy.DelayFor(1, TimeSpan.FromSeconds(5)));
    }

    [Test("Retry-After is still capped, so a hostile header cannot park the app forever")]
    public static void RetryAfterIsCapped() =>
        Check.Equal(
            TimeSpan.FromMinutes(30),
            new BackoffPolicy(TimeSpan.FromSeconds(30), TimeSpan.FromMinutes(30), jitterFraction: 0)
                .DelayFor(1, TimeSpan.FromDays(1)));

    [Test("Jitter stays inside its stated band so retries spread without going wild")]
    public static void JitterStaysInBand()
    {
        var policy = new BackoffPolicy(TimeSpan.FromSeconds(100), TimeSpan.FromMinutes(30), 0.2d, new Random(7));

        for (var i = 0; i < 200; i++)
        {
            var delay = policy.DelayFor(1).TotalSeconds;
            Check.True(delay is >= 80 and <= 120, $"jittered delay {delay} escaped the +/-20% band");
        }
    }
}

public static class DeckTests
{
    [Test]
    public static void LowestAccountOwnsTheTray()
    {
        var deck = Sample.Deck(Sample.Account("a", 80), Sample.Account("b", 12), Sample.Account("c", 40));
        Check.Equal("b", Check.NotNull(deck.LowestAccount).AccountId);
    }

    [Test]
    public static void APinnedAccountOverridesLowest()
    {
        var deck = Sample.Deck(Sample.Account("a", 80), Sample.Account("b", 12));
        Check.Equal("a", Check.NotNull(deck.TrayAccount("a")).AccountId);
    }

    [Test("A pin that no longer resolves falls back rather than showing nothing")]
    public static void UnresolvablePinFallsBack()
    {
        var deck = Sample.Deck(Sample.Account("a", 80), Sample.Account("b", 12));

        Check.Equal("b", Check.NotNull(deck.TrayAccount("deleted-account")).AccountId);
        Check.Equal("b", Check.NotNull(deck.TrayAccount(null)).AccountId);
    }

    [Test("An account with no data cannot own the tray")]
    public static void AccountsWithoutDataAreSkipped()
    {
        var deck = Sample.Deck(Sample.Account("a", null), Sample.Account("b", 55));
        Check.Equal("b", Check.NotNull(deck.LowestAccount).AccountId);
    }

    [Test]
    public static void NextResetIsTheSoonest()
    {
        var account = new AccountSnapshot
        {
            AccountId = "a", DisplayName = "a", Provider = ProviderKind.Claude,
            Windows = new[]
            {
                new UsageWindow { Scope = "weekly", Kind = WindowKind.Weekly, UsedPercent = 5, ResetsAt = DateTimeOffset.Parse("2026-09-12T00:00:00Z") },
                new UsageWindow { Scope = "5-hour", Kind = WindowKind.Session, UsedPercent = 5, ResetsAt = DateTimeOffset.Parse("2026-09-07T15:00:00Z") },
            },
        };

        Check.Equal(DateTimeOffset.Parse("2026-09-07T15:00:00Z"), Check.NotNullValue(account.NextReset));
    }

    [Test]
    public static void WindowsAreOrderedShortestFirst()
    {
        var account = new AccountSnapshot
        {
            AccountId = "a", DisplayName = "a", Provider = ProviderKind.Claude,
            Windows = new[]
            {
                new UsageWindow { Scope = "spend", Kind = WindowKind.Spend, UsedPercent = 1 },
                new UsageWindow { Scope = "Opus weekly", Kind = WindowKind.WeeklyScoped, UsedPercent = 1 },
                new UsageWindow { Scope = "weekly", Kind = WindowKind.Weekly, UsedPercent = 1 },
                new UsageWindow { Scope = "5-hour", Kind = WindowKind.Session, UsedPercent = 1 },
            },
        };

        Check.Equal(
            "5-hour, weekly, Opus weekly, spend",
            string.Join(", ", account.OrderedWindows.Select(w => w.Scope)));
    }
}

public static class DisplayTextTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-09-07T12:00:00Z");

    [Test]
    public static void PercentTextIsRounded()
    {
        Check.Equal("38% left", DisplayText.PercentLeft(37.6));
        Check.Equal("—", DisplayText.PercentLeft(null));
        Check.Equal("38%", DisplayText.PercentCompact(37.6));
        Check.Equal("", DisplayText.PercentCompact(null));
    }

    [Test("A window with no stated reset renders an empty slot, not a placeholder")]
    public static void MissingResetIsEmpty() => Check.Equal("", DisplayText.ResetText(null, Now));

    [Test]
    public static void ResetTextVariesWithDistance()
    {
        Check.Contains("Resets", DisplayText.ResetText(Now.AddHours(3), Now));
        Check.Contains("tomorrow", DisplayText.ResetText(Now.AddDays(1), Now));
        Check.Equal("Resetting now", DisplayText.ResetText(Now.AddMinutes(-1), Now));
    }

    [Test]
    public static void CountdownReadsNaturally()
    {
        Check.Equal("in 45 m", DisplayText.TimeUntil(Now.AddMinutes(45), Now));
        Check.Equal("in 3 h 30 m", DisplayText.TimeUntil(Now.AddMinutes(210), Now));
        Check.Equal("now", DisplayText.TimeUntil(Now.AddMinutes(-1), Now));
    }

    [Test]
    public static void UpdatedAgoReadsNaturally()
    {
        Check.Equal("Never refreshed", DisplayText.UpdatedAgo(null, Now));
        Check.Equal("Updated just now", DisplayText.UpdatedAgo(Now.AddSeconds(-10), Now));
        Check.Equal("Updated 12 min ago", DisplayText.UpdatedAgo(Now.AddMinutes(-12), Now));
        Check.Equal("Updated 3 h ago", DisplayText.UpdatedAgo(Now.AddHours(-3), Now));
    }

    [Test("Plan labels are tidied, never invented")]
    public static void PlanLabelsAreTidied()
    {
        Check.Equal("Max 20x", DisplayText.PlanLabel("max_20x"));
        Check.Equal("Pro", DisplayText.PlanLabel("pro"));
        Check.Null(DisplayText.PlanLabel(null));
        Check.Null(DisplayText.PlanLabel("   "));
    }

    [Test]
    public static void AvailabilityNoticesUseTheRightTone()
    {
        Check.Contains("Idle", DisplayText.AvailabilityNotice(AccountAvailability.Idle));
        Check.Contains("Sign in", DisplayText.AvailabilityNotice(AccountAvailability.SignedOut));
        Check.Equal("", DisplayText.AvailabilityNotice(AccountAvailability.Ok));
    }
}
