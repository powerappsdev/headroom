using System;

namespace Headroom.Core.Model;

/// <summary>
/// The remaining-percentage boundaries that turn the tray icon gold and then red.
/// </summary>
public sealed record Thresholds(double WarningPercent = 25d, double CriticalPercent = 10d)
{
    public static readonly Thresholds Default = new();

    /// <summary>Which band a remaining percentage falls into. Null input is Healthy: absence of data is not an alarm.</summary>
    public AlertBand Band(double? remainingPercent)
    {
        if (remainingPercent is not { } remaining) return AlertBand.Healthy;
        if (remaining <= CriticalPercent) return AlertBand.Critical;
        if (remaining <= WarningPercent) return AlertBand.Warning;
        return AlertBand.Healthy;
    }

    /// <summary>Clamps user input into a sane, ordered pair so the settings UI cannot produce nonsense.</summary>
    public Thresholds Normalized()
    {
        var warning = Math.Clamp(WarningPercent, 1d, 99d);
        var critical = Math.Clamp(CriticalPercent, 0d, warning);
        return new Thresholds(warning, critical);
    }
}
