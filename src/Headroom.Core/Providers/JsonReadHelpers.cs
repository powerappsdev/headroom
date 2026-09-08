using System;
using System.Globalization;
using System.Text.Json;

namespace Headroom.Core.Providers;

/// <summary>
/// Tolerant readers for provider payloads. These endpoints are internal and
/// undocumented: field names vary, numbers arrive as strings, and timestamps
/// come as ISO text, epoch seconds, or epoch milliseconds depending on the
/// window. Every helper here returns null rather than throwing, so a payload
/// that changed shape degrades to "unknown" instead of taking the app down.
/// </summary>
internal static class JsonReadHelpers
{
    /// <summary>Reads a number that may be encoded as a JSON number or a numeric string.</summary>
    public static double? Number(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Number when element.TryGetDouble(out var value) && double.IsFinite(value) => value,
        JsonValueKind.String when double.TryParse(
            element.GetString(),
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed) && double.IsFinite(parsed) => parsed,
        _ => null,
    };

    /// <summary>Reads the first present property from <paramref name="names"/> as a number.</summary>
    public static double? NumberAny(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out var child) &&
                Number(child) is { } value)
            {
                return value;
            }
        }

        return null;
    }

    /// <summary>Reads the first present property from <paramref name="names"/> as a non-blank string.</summary>
    public static string? StringAny(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind == JsonValueKind.Object &&
                parent.TryGetProperty(name, out var child) &&
                child.ValueKind == JsonValueKind.String &&
                child.GetString() is { Length: > 0 } text &&
                !string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    public static bool TryGetObject(JsonElement parent, string name, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out value) &&
            value.ValueKind == JsonValueKind.Object)
        {
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Reads a reset timestamp. Accepts ISO-8601 text, epoch seconds, and epoch
    /// milliseconds, disambiguating the last two by magnitude: any value below
    /// 10^10 is seconds, because 10^10 seconds is the year 2286 and 10^10
    /// milliseconds was 1970.
    /// </summary>
    public static DateTimeOffset? Timestamp(JsonElement parent, params string[] names)
    {
        foreach (var name in names)
        {
            if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var child))
                continue;

            switch (child.ValueKind)
            {
                case JsonValueKind.String:
                    var text = child.GetString();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    if (DateTimeOffset.TryParse(
                            text,
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                            out var parsed))
                    {
                        return parsed;
                    }

                    continue;

                case JsonValueKind.Number:
                    if (Number(child) is not { } epoch || epoch <= 0) continue;
                    try
                    {
                        return epoch < 10_000_000_000d
                            ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(epoch * 1000d))
                            : DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(epoch));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        continue;
                    }
            }
        }

        return null;
    }
}
