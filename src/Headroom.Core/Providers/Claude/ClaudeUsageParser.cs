using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using Headroom.Core.Model;

namespace Headroom.Core.Providers.Claude;

/// <summary>
/// Turns the payload from Anthropic's OAuth usage endpoint into windows.
/// </summary>
/// <remarks>
/// The endpoint is internal and its payload has been observed in several shapes:
/// top-level window objects keyed by name, a kind-tagged <c>limits</c> array,
/// and a <c>weekly_scoped</c> collection that arrives as either an array or an
/// object map. All three are handled, and model names are never hardcoded —
/// whatever the payload says is what gets displayed, so a new model appears in
/// the deck the day it ships without a Headroom update.
/// </remarks>
public static class ClaudeUsageParser
{
    /// <summary>Keys inside the usage object that are metadata, not windows.</summary>
    private static readonly HashSet<string> NotWindows = new(StringComparer.OrdinalIgnoreCase)
    {
        "extra_usage", "extraUsage", "plan", "organization", "account",
        "status", "limits", "expiresAt", "expires_at",
    };

    /// <summary>
    /// Parses a usage payload. Returns an empty list when the payload contained
    /// no recognizable window, which callers must treat as "unknown" rather than
    /// as zero usage.
    /// </summary>
    public static IReadOnlyList<UsageWindow> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<UsageWindow>();

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return Array.Empty<UsageWindow>();
        }

        using (document)
        {
            return Parse(document.RootElement);
        }
    }

    public static IReadOnlyList<UsageWindow> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<UsageWindow>();

        var usage = root;
        foreach (var wrapper in new[] { "usage", "rateLimits", "rate_limits" })
        {
            if (JsonReadHelpers.TryGetObject(root, wrapper, out var inner))
            {
                usage = inner;
                break;
            }
        }

        var windows = new List<UsageWindow>();

        // The kind-tagged limits array is the most explicit source, so it is read
        // first and wins ties against the looser top-level objects below.
        foreach (var source in new[] { root, usage })
        {
            if (source.ValueKind == JsonValueKind.Object &&
                source.TryGetProperty("limits", out var limits) &&
                limits.ValueKind == JsonValueKind.Array)
            {
                ReadLimitEntries(limits, windows);
                break;
            }
        }

        foreach (var property in usage.EnumerateObject())
        {
            if (NotWindows.Contains(property.Name)) continue;

            if (property.Name is "weekly_scoped" or "weeklyScoped")
            {
                ReadWeeklyScoped(property.Value, windows);
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            if (UsedPercent(property.Value) is not { } percent) continue;

            var label = WindowLabel(property.Name, property.Value);
            windows.Add(new UsageWindow
            {
                Scope = label,
                Kind = KindForLabel(label),
                UsedPercent = percent,
                ResetsAt = ResetsAt(property.Value),
            });
        }

        var unique = DeduplicateByScope(windows);
        AttachSpend(root, usage, unique);
        DropMeaninglessSpend(unique);
        return unique;
    }

    /// <summary>
    /// Reads the credential expiry that the probe layer attaches to the payload
    /// root. Used only to tell an idle token from a live one; never displayed.
    /// </summary>
    public static DateTimeOffset? CredentialExpiry(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        if (JsonReadHelpers.NumberAny(root, "expiresAt", "expires_at") is not { } value || value <= 0) return null;
        try
        {
            return value < 10_000_000_000d
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(value * 1000d))
                : DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(value));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static void ReadLimitEntries(JsonElement limits, List<UsageWindow> into)
    {
        foreach (var entry in limits.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object) continue;

            var percent = JsonReadHelpers.NumberAny(entry, "percent") ?? UsedPercent(entry);
            if (percent is null) continue;

            var scope = LimitEntryScope(entry);
            if (scope is null) continue;

            into.Add(new UsageWindow
            {
                Scope = scope,
                Kind = KindForLabel(scope, JsonReadHelpers.StringAny(entry, "kind", "group")),
                UsedPercent = percent,
                ResetsAt = ResetsAt(entry),
            });
        }
    }

    private static string? LimitEntryScope(JsonElement entry)
    {
        var kind = (JsonReadHelpers.StringAny(entry, "kind", "group") ?? string.Empty).ToLowerInvariant();

        if (kind == "weekly_scoped")
        {
            // A scoped weekly limit is meaningless without knowing which model it
            // scopes, so an entry that omits the model is dropped rather than
            // shown as a mystery bar.
            if (!JsonReadHelpers.TryGetObject(entry, "scope", out var scope)) return null;
            if (!JsonReadHelpers.TryGetObject(scope, "model", out var model)) return null;
            var name = JsonReadHelpers.StringAny(model, "display_name", "displayName", "id");
            return name is null ? null : $"{Capitalize(name)} weekly";
        }

        if (kind is "session" or "five_hour" or "5_hour" or "primary") return "5-hour";
        if (kind is "weekly_all" or "weekly" or "seven_day" or "7_day" or "week" or "secondary") return "weekly";

        return kind.Length == 0 ? null : kind.Replace('_', ' ');
    }

    private static void ReadWeeklyScoped(JsonElement value, List<UsageWindow> into)
    {
        IEnumerable<(string? Name, JsonElement Row)> rows = value.ValueKind switch
        {
            JsonValueKind.Array => value.EnumerateArray().Select(row => ((string?)null, row)),
            JsonValueKind.Object => value.EnumerateObject().Select(p => ((string?)p.Name, p.Value)),
            _ => Array.Empty<(string?, JsonElement)>(),
        };

        foreach (var (key, row) in rows)
        {
            if (row.ValueKind != JsonValueKind.Object) continue;
            if (UsedPercent(row) is not { } percent) continue;

            var name = JsonReadHelpers.StringAny(row, "model", "name", "label", "display_name", "displayName") ?? key;
            if (string.IsNullOrWhiteSpace(name)) continue;

            into.Add(new UsageWindow
            {
                Scope = $"{Capitalize(name)} weekly",
                Kind = WindowKind.WeeklyScoped,
                UsedPercent = percent,
                ResetsAt = ResetsAt(row),
            });
        }
    }

    private static double? UsedPercent(JsonElement window) => JsonReadHelpers.NumberAny(
        window, "usedPercent", "used_percent", "utilization", "percent", "pct", "usage");

    private static DateTimeOffset? ResetsAt(JsonElement window) => JsonReadHelpers.Timestamp(
        window, "resetsAt", "resets_at", "resetAt", "reset_at");

    private static string WindowLabel(string key, JsonElement window)
    {
        var raw = JsonReadHelpers.StringAny(window, "label", "displayName", "display_name", "name") ?? key;
        var normalized = raw.ToLowerInvariant().Replace('-', '_').Replace(' ', '_');

        if (normalized is "five_hour" or "5_hour" or "session" or "primary") return "5-hour";
        if (normalized is "seven_day" or "7_day" or "weekly" or "week" or "secondary") return "weekly";

        // Model-scoped spellings such as "weekly_opus" or "opus_seven_day".
        foreach (var marker in new[] { "seven_day_", "7_day_", "weekly_" })
        {
            if (normalized.StartsWith(marker, StringComparison.Ordinal) && normalized.Length > marker.Length)
                return $"{Capitalize(normalized[marker.Length..])} weekly";
        }

        foreach (var marker in new[] { "_seven_day", "_7_day", "_weekly" })
        {
            if (normalized.EndsWith(marker, StringComparison.Ordinal) && normalized.Length > marker.Length)
                return $"{Capitalize(normalized[..^marker.Length])} weekly";
        }

        // A window the provider names but Headroom has no pattern for still
        // deserves to render properly: whatever it said, tidied, never dropped.
        return Capitalize(raw);
    }

    private static WindowKind KindForLabel(string label, string? kindHint = null)
    {
        var hint = (kindHint ?? string.Empty).ToLowerInvariant();
        if (hint == "weekly_scoped") return WindowKind.WeeklyScoped;

        var normalized = label.ToLowerInvariant();
        if (normalized == "5-hour") return WindowKind.Session;
        if (normalized == "weekly") return WindowKind.Weekly;
        if (normalized.EndsWith(" weekly", StringComparison.Ordinal)) return WindowKind.WeeklyScoped;
        if (normalized.Contains("spend", StringComparison.Ordinal) ||
            normalized.Contains("credit", StringComparison.Ordinal)) return WindowKind.Spend;
        return WindowKind.Other;
    }

    private static List<UsageWindow> DeduplicateByScope(IEnumerable<UsageWindow> windows)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<UsageWindow>();
        foreach (var window in windows)
        {
            if (seen.Add(window.Scope)) result.Add(window);
        }

        return result;
    }

    private static string Capitalize(string value)
    {
        var trimmed = value.Trim().Replace('_', ' ');
        if (trimmed.Length == 0) return trimmed;
        return char.ToUpper(trimmed[0], CultureInfo.InvariantCulture) + trimmed[1..];
    }

    /// <summary>
    /// Removes a spend row that carries neither money nor usage.
    /// </summary>
    /// <remarks>
    /// An extra-usage budget you have not touched reports 0% with no amounts,
    /// which renders as a full bar reading "100% left" next to the rate limits
    /// that actually constrain you. That is noise dressed as data. A budget with
    /// stated amounts is kept even at zero, because "$0.00 of $500.00" is
    /// genuinely information.
    /// </remarks>
    private static void DropMeaninglessSpend(List<UsageWindow> windows) =>
        windows.RemoveAll(w =>
            w.Kind == WindowKind.Spend &&
            w.Spend is null &&
            w.UsedPercent is null or 0d);

    private static void AttachSpend(JsonElement root, JsonElement usage, List<UsageWindow> windows)
    {
        var amounts = ClaudeSpendParser.Parse(root) ?? ClaudeSpendParser.Parse(usage);
        if (amounts is null) return;

        var index = windows.FindIndex(w =>
            w.Scope.Contains("spend", StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
        {
            windows[index] = windows[index] with { Kind = WindowKind.Spend, Spend = amounts };
            return;
        }

        // Amounts with no percent-bearing row still deserve to reach the deck:
        // "$245.63 of $500.00" is live information even without a percentage.
        windows.Add(new UsageWindow
        {
            Scope = "spend",
            Kind = WindowKind.Spend,
            UsedPercent = null,
            ResetsAt = null,
            Spend = amounts,
        });
    }
}
