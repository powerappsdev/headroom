using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Headroom.Core.Model;

namespace Headroom.Core.Providers.Codex;

/// <summary>
/// Turns a Codex app-server <c>account/rateLimits/read</c> reply into windows.
/// </summary>
/// <remarks>
/// Codex reports each limit as a bucket with a <c>primary</c> and
/// <c>secondary</c> window, and newer builds return several named buckets under
/// <c>rateLimitsByLimitId</c> (Codex itself, plus any additional product limits
/// on the account). Window names come from the stated duration rather than the
/// slot name, so a 300-minute primary reads "5-hour" and a 10080-minute one
/// reads "weekly" wherever it appears.
/// </remarks>
public static class CodexRateLimitParser
{
    public static IReadOnlyList<UsageWindow> Parse(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Array.Empty<UsageWindow>();
        try
        {
            using var document = JsonDocument.Parse(json);
            return Parse(document.RootElement);
        }
        catch (JsonException)
        {
            return Array.Empty<UsageWindow>();
        }
    }

    public static IReadOnlyList<UsageWindow> Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return Array.Empty<UsageWindow>();

        // The reply may arrive bare, wrapped in a JSON-RPC "result", or already
        // unwrapped by the caller. Prefer whichever level actually has limits.
        var payload = root;
        if (!HasLimits(payload) &&
            root.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object)
        {
            payload = result;
        }

        var windows = new List<UsageWindow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var bucket in Buckets(payload))
        {
            var limitName = JsonReadHelpers.StringAny(bucket, "limitName", "limit_name");
            var prefix = limitName is not null && !limitName.Equals("codex", StringComparison.OrdinalIgnoreCase)
                ? limitName + " "
                : string.Empty;

            foreach (var slot in new[] { "primary", "secondary" })
            {
                if (!JsonReadHelpers.TryGetObject(bucket, slot, out var window)) continue;

                var used = JsonReadHelpers.NumberAny(window, "usedPercent", "used_percent");
                if (used is null) continue;

                var label = (prefix + WindowLabel(window, slot)).Trim();
                if (!seen.Add(label)) continue;

                windows.Add(new UsageWindow
                {
                    Scope = label,
                    Kind = KindFor(window, slot),
                    UsedPercent = used,
                    ResetsAt = JsonReadHelpers.Timestamp(window, "resetsAt", "resets_at"),
                });
            }
        }

        return windows;
    }

    /// <summary>Reads the plan tier the app-server states alongside the limits, when present.</summary>
    public static string? ReadPlanTier(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;

        var payload = root;
        if (!HasLimits(payload) &&
            root.TryGetProperty("result", out var result) &&
            result.ValueKind == JsonValueKind.Object)
        {
            payload = result;
        }

        foreach (var bucket in Buckets(payload))
        {
            if (JsonReadHelpers.StringAny(bucket, "planType", "plan_type") is { } plan) return plan;
        }

        return null;
    }

    private static bool HasLimits(JsonElement element) =>
        element.ValueKind == JsonValueKind.Object &&
        (element.TryGetProperty("rateLimits", out _) || element.TryGetProperty("rateLimitsByLimitId", out _));

    private static IEnumerable<JsonElement> Buckets(JsonElement payload)
    {
        if (JsonReadHelpers.TryGetObject(payload, "rateLimitsByLimitId", out var byId))
        {
            foreach (var property in byId.EnumerateObject())
            {
                if (property.Value.ValueKind == JsonValueKind.Object) yield return property.Value;
            }

            yield break;
        }

        if (JsonReadHelpers.TryGetObject(payload, "rateLimits", out var single))
        {
            yield return single;
            yield break;
        }

        if (payload.ValueKind == JsonValueKind.Object) yield return payload;
    }

    private static string WindowLabel(JsonElement window, string fallback)
    {
        var minutes = JsonReadHelpers.NumberAny(window, "windowDurationMins", "window_duration_mins");
        return minutes switch
        {
            300d => "5-hour",
            10080d => "weekly",
            { } m and > 0 => ((int)m).ToString(CultureInfo.InvariantCulture) + "-minute",
            _ => fallback,
        };
    }

    private static WindowKind KindFor(JsonElement window, string slot)
    {
        var minutes = JsonReadHelpers.NumberAny(window, "windowDurationMins", "window_duration_mins");
        if (minutes is 300d) return WindowKind.Session;
        if (minutes is 10080d) return WindowKind.Weekly;
        return slot == "primary" ? WindowKind.Session : WindowKind.Other;
    }
}
