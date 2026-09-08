using System;
using System.Linq;
using Headroom.Core.Model;
using Headroom.Core.Providers.Claude;

namespace Headroom.Core.Tests;

public static class ClaudeParsingTests
{
    /// <summary>A payload shaped exactly like a real reply from the usage endpoint.</summary>
    private const string RealisticPayload = """
    {
      "five_hour":     { "utilization": 25, "resets_at": "2026-07-19T20:00:00Z" },
      "seven_day":     { "utilization": 40, "resets_at": "2026-07-25T20:00:00Z" },
      "weekly_scoped": [ { "model": "fable", "utilization": 61, "resets_at": "2026-07-24T20:00:00Z" } ],
      "extra_usage":   { "is_enabled": false },
      "limits": [
        {
          "kind": "weekly_scoped", "group": "weekly", "percent": 96,
          "severity": "critical", "resets_at": "2026-07-23T00:59:59Z",
          "scope": { "model": { "id": null, "display_name": "Opus" } }, "is_active": true
        }
      ]
    }
    """;

    [Test]
    public static void ReadsEveryWindowFromARealisticPayload()
    {
        var windows = ClaudeUsageParser.Parse(RealisticPayload);

        Check.Count(4, windows);
        Check.Close(25, Find(windows, "5-hour").UsedPercent);
        Check.Close(40, Find(windows, "weekly").UsedPercent);
        Check.Close(61, Find(windows, "Fable weekly").UsedPercent);
        Check.Close(96, Find(windows, "Opus weekly").UsedPercent);
    }

    [Test]
    public static void ClassifiesWindowKinds()
    {
        var windows = ClaudeUsageParser.Parse(RealisticPayload);

        Check.Equal(WindowKind.Session, Find(windows, "5-hour").Kind);
        Check.Equal(WindowKind.Weekly, Find(windows, "weekly").Kind);
        Check.Equal(WindowKind.WeeklyScoped, Find(windows, "Fable weekly").Kind);
        Check.Equal(WindowKind.WeeklyScoped, Find(windows, "Opus weekly").Kind);
    }

    [Test]
    public static void ReadsResetTimestamps()
    {
        var windows = ClaudeUsageParser.Parse(RealisticPayload);

        Check.Equal(
            Moment.At("2026-07-19T20:00:00Z").ToUniversalTime(),
            Check.NotNullValue(Find(windows, "5-hour").ResetsAt).ToUniversalTime());
        Check.Equal(
            Moment.At("2026-07-23T00:59:59Z").ToUniversalTime(),
            Check.NotNullValue(Find(windows, "Opus weekly").ResetsAt).ToUniversalTime());
    }

    [Test("Remaining percent is the inverse of used, clamped")]
    public static void ComputesRemaining()
    {
        var windows = ClaudeUsageParser.Parse(RealisticPayload);
        Check.Close(75, Find(windows, "5-hour").RemainingPercent);
        Check.Close(4, Find(windows, "Opus weekly").RemainingPercent);
    }

    [Test("weekly_scoped may arrive as an object map instead of an array")]
    public static void ReadsScopedWeeklyAsObjectMap()
    {
        var windows = ClaudeUsageParser.Parse("""
        { "weekly_scoped": { "opus": { "utilization": 12 }, "fable": { "utilization": 80 } } }
        """);

        Check.Count(2, windows);
        Check.Close(12, Find(windows, "Opus weekly").UsedPercent);
        Check.Close(80, Find(windows, "Fable weekly").UsedPercent);
    }

    [Test("A limits entry wins over a duplicate top-level window of the same scope")]
    public static void LimitsEntriesTakePrecedence()
    {
        var windows = ClaudeUsageParser.Parse("""
        {
          "five_hour": { "utilization": 10 },
          "limits": [ { "kind": "session", "percent": 77 } ]
        }
        """);

        Check.Count(1, windows);
        Check.Close(77, Find(windows, "5-hour").UsedPercent);
    }

    [Test("A scoped weekly limit with no model name is dropped, not shown as a mystery bar")]
    public static void DropsScopedLimitWithoutModel()
    {
        var windows = ClaudeUsageParser.Parse("""
        { "limits": [ { "kind": "weekly_scoped", "percent": 50, "scope": { "model": {} } } ] }
        """);

        Check.Count(0, windows);
    }

    [Test("Percentages arrive under several field names, including as strings")]
    public static void AcceptsAlternatePercentFields()
    {
        Check.Close(33, ClaudeUsageParser.Parse("""{"five_hour":{"usedPercent":33}}""")[0].UsedPercent);
        Check.Close(44, ClaudeUsageParser.Parse("""{"five_hour":{"used_percent":44}}""")[0].UsedPercent);
        Check.Close(55, ClaudeUsageParser.Parse("""{"five_hour":{"percent":55}}""")[0].UsedPercent);
        Check.Close(66, ClaudeUsageParser.Parse("""{"five_hour":{"utilization":"66"}}""")[0].UsedPercent);
    }

    [Test("Epoch seconds and milliseconds are told apart by magnitude")]
    public static void AcceptsEpochTimestamps()
    {
        var seconds = ClaudeUsageParser.Parse("""{"five_hour":{"utilization":1,"resets_at":1780000000}}""");
        Check.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1780000000).ToUniversalTime(),
            Check.NotNullValue(seconds[0].ResetsAt).ToUniversalTime());

        var millis = ClaudeUsageParser.Parse("""{"five_hour":{"utilization":1,"resets_at":1780000000000}}""");
        Check.Equal(
            DateTimeOffset.FromUnixTimeMilliseconds(1780000000000).ToUniversalTime(),
            Check.NotNullValue(millis[0].ResetsAt).ToUniversalTime());
    }

    [Test("A usage wrapper object is unwrapped")]
    public static void UnwrapsUsageEnvelope()
    {
        var windows = ClaudeUsageParser.Parse("""{"usage":{"five_hour":{"utilization":5}}}""");
        Check.Count(1, windows);
        Check.Close(5, windows[0].UsedPercent);
    }

    [Test("Garbage in, empty out - never an exception")]
    public static void DegradesToEmptyOnJunk()
    {
        Check.Count(0, ClaudeUsageParser.Parse("not json at all"));
        Check.Count(0, ClaudeUsageParser.Parse(""));
        Check.Count(0, ClaudeUsageParser.Parse("[1,2,3]"));
        Check.Count(0, ClaudeUsageParser.Parse("""{"plan":"max","organization":{"name":"x"}}"""));
    }

    [Test("A model name Headroom has never heard of still renders")]
    public static void HandlesUnknownModelNames()
    {
        var windows = ClaudeUsageParser.Parse("""
        { "limits": [ { "kind": "weekly_scoped", "percent": 3,
          "scope": { "model": { "display_name": "some-future-model" } } } ] }
        """);

        Check.Count(1, windows);
        Check.Equal("Some-future-model weekly", windows[0].Scope);
    }

    private static UsageWindow Find(System.Collections.Generic.IReadOnlyList<UsageWindow> windows, string scope) =>
        Check.NotNull(
            windows.FirstOrDefault(w => string.Equals(w.Scope, scope, StringComparison.OrdinalIgnoreCase)),
            $"no window named '{scope}' in [{string.Join(", ", windows.Select(w => w.Scope))}]");
}
