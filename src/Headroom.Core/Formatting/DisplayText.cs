using System;
using System.Globalization;
using Headroom.Core.Model;

namespace Headroom.Core.Formatting;

/// <summary>
/// Every string the deck shows for a number, in one place.
/// </summary>
/// <remarks>
/// Reset times are rendered in the viewer's local clock with no zone
/// abbreviation, because on a desktop the displayed time is understood to be
/// local and labelling it adds noise to every single row. The full timestamp
/// with its zone lives in tooltips, one hover away.
/// </remarks>
public static class DisplayText
{
    /// <summary>"38% left", or an em dash when the provider stated no percentage.</summary>
    public static string PercentLeft(double? remainingPercent) =>
        remainingPercent is { } value
            ? value.ToString("0", CultureInfo.CurrentCulture) + "% left"
            : "—";

    /// <summary>Just the number and sign, for the tray where space is scarce.</summary>
    public static string PercentCompact(double? remainingPercent) =>
        remainingPercent is { } value
            ? value.ToString("0", CultureInfo.CurrentCulture) + "%"
            : string.Empty;

    /// <summary>
    /// "Resets 5:59 PM" for today, "Resets Wed 5:59 PM" further out, and an empty
    /// string when the provider stated nothing — an empty slot reads better than
    /// a placeholder saying there is no data.
    /// </summary>
    public static string ResetText(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset) return string.Empty;

        var local = reset.ToLocalTime();
        var localNow = now.ToLocalTime();

        if (reset <= now) return "Resetting now";

        var days = (local.Date - localNow.Date).Days;
        return days switch
        {
            0 => "Resets " + local.ToString("h:mm tt", CultureInfo.CurrentCulture),
            1 => "Resets tomorrow " + local.ToString("h:mm tt", CultureInfo.CurrentCulture),
            < 7 => "Resets " + local.ToString("ddd h:mm tt", CultureInfo.CurrentCulture),
            _ => "Resets " + local.ToString("MMM d, h:mm tt", CultureInfo.CurrentCulture),
        };
    }

    /// <summary>Full timestamp with zone, for tooltips where certainty beats brevity.</summary>
    public static string ResetTooltip(DateTimeOffset? resetsAt)
    {
        if (resetsAt is not { } reset) return string.Empty;
        var local = reset.ToLocalTime();
        return local.ToString("ddd MMM d, h:mm tt ", CultureInfo.CurrentCulture) + TimeZoneAbbreviation(local);
    }

    /// <summary>"in 2 h 15 m" — the countdown, which is often the real question.</summary>
    public static string TimeUntil(DateTimeOffset? resetsAt, DateTimeOffset now)
    {
        if (resetsAt is not { } reset) return string.Empty;
        var span = reset - now;
        if (span <= TimeSpan.Zero) return "now";
        if (span.TotalMinutes < 60) return $"in {(int)span.TotalMinutes} m";
        if (span.TotalHours < 24) return $"in {(int)span.TotalHours} h {span.Minutes} m";
        return $"in {(int)span.TotalDays} d {span.Hours} h";
    }

    /// <summary>"Updated 4 min ago", or "Never refreshed".</summary>
    public static string UpdatedAgo(DateTimeOffset? observedAt, DateTimeOffset now)
    {
        if (observedAt is not { } observed) return "Never refreshed";
        var span = now - observed;
        if (span < TimeSpan.FromSeconds(45)) return "Updated just now";
        if (span.TotalMinutes < 60) return $"Updated {(int)span.TotalMinutes} min ago";
        if (span.TotalHours < 24) return $"Updated {(int)span.TotalHours} h ago";
        return $"Updated {(int)span.TotalDays} d ago";
    }

    /// <summary>
    /// Turns a provider's plan token into something readable ("max_20x" becomes
    /// "Max 20x") without inventing tiers the provider never stated.
    /// </summary>
    public static string? PlanLabel(string? planTier)
    {
        if (string.IsNullOrWhiteSpace(planTier)) return null;

        var words = planTier.Replace('_', ' ').Replace('-', ' ')
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return null;

        for (var i = 0; i < words.Length; i++)
        {
            var word = words[i];
            words[i] = word.Length switch
            {
                0 => word,
                _ => char.ToUpper(word[0], CultureInfo.CurrentCulture) + word[1..],
            };
        }

        return string.Join(' ', words);
    }

    /// <summary>"$245.63 of $500.00", using the payload's own currency.</summary>
    public static string? SpendText(SpendAmount? spend)
    {
        if (spend is null) return null;

        var format = spend.Currency switch
        {
            "USD" => "$#,##0.00",
            "EUR" => "€#,##0.00",
            "GBP" => "£#,##0.00",
            _ => null,
        };

        return format is null
            ? $"{spend.Used:#,##0.00} of {spend.Limit:#,##0.00} {spend.Currency}"
            : $"{spend.Used.ToString(format, CultureInfo.CurrentCulture)} of {spend.Limit.ToString(format, CultureInfo.CurrentCulture)}";
    }

    /// <summary>Short card notice for a non-Ok account, matched to the tone it deserves.</summary>
    public static string AvailabilityNotice(AccountAvailability availability) => availability switch
    {
        AccountAvailability.Idle => "Idle — renews on next use",
        AccountAvailability.SignedOut => "Sign in needed",
        AccountAvailability.CredentialBlocked => "Sign-in unreadable",
        AccountAvailability.ProviderUnreachable => "Couldn't refresh",
        AccountAvailability.NotConfigured => "Not set up",
        _ => string.Empty,
    };

    private static string TimeZoneAbbreviation(DateTimeOffset local)
    {
        var zone = TimeZoneInfo.Local;
        var name = zone.IsDaylightSavingTime(local) ? zone.DaylightName : zone.StandardName;
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;

        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 1) return words[0];

        Span<char> initials = stackalloc char[words.Length];
        for (var i = 0; i < words.Length; i++) initials[i] = char.ToUpperInvariant(words[i][0]);
        return new string(initials);
    }
}
