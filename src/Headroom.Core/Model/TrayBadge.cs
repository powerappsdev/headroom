using System;

namespace Headroom.Core.Model;

/// <summary>A plain colour, kept free of any drawing library so it can live in Core.</summary>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    public static Rgb FromHex(uint value) =>
        new((byte)((value >> 16) & 0xFF), (byte)((value >> 8) & 0xFF), (byte)(value & 0xFF));

    /// <summary>WCAG relative luminance.</summary>
    public double RelativeLuminance
    {
        get
        {
            static double Channel(byte raw)
            {
                var c = raw / 255d;
                return c <= 0.04045d ? c / 12.92d : Math.Pow((c + 0.055d) / 1.055d, 2.4d);
            }

            return (0.2126d * Channel(R)) + (0.7152d * Channel(G)) + (0.0722d * Channel(B));
        }
    }

    /// <summary>WCAG contrast ratio between two colours, 1.0 to 21.0.</summary>
    public static double Contrast(Rgb a, Rgb b)
    {
        var first = a.RelativeLuminance;
        var second = b.RelativeLuminance;
        var lighter = Math.Max(first, second);
        var darker = Math.Min(first, second);
        return (lighter + 0.05d) / (darker + 0.05d);
    }
}

/// <summary>How the tray icon should be painted for a given severity.</summary>
public readonly record struct TrayBadgeStyle(Rgb Fill, Rgb Ink, bool Filled);

/// <summary>
/// Chooses the tray icon's colours.
/// </summary>
/// <remarks>
/// The notification area is the one surface whose background this app does not
/// control: it is whatever the user's wallpaper and accent colour make it, and
/// it changes when they change theme. Drawing a bare coloured digit there means
/// betting on a contrast ratio that is not ours to set, and the bet loses -
/// critical red on a dark taskbar measures about 3.6:1, which is unreadable at
/// sixteen pixels.
///
/// So an alerting icon carries its own background: a filled badge in the status
/// colour with the digits knocked out of it. That fixes the contrast at a value
/// we choose rather than one we inherit, and it makes the alert states read as
/// deliberately different from the resting glyph rather than merely tinted.
/// The resting and always-on states stay unfilled, because a badge is the alarm
/// and a healthy account is not sounding one.
/// </remarks>
public static class TrayBadge
{
    private static readonly Rgb Critical = Rgb.FromHex(0xD03B3B);
    private static readonly Rgb Warning = Rgb.FromHex(0xFAB219);
    private static readonly Rgb DarkInk = Rgb.FromHex(0x1A1A19);
    private static readonly Rgb LightInk = Rgb.FromHex(0xFFFFFF);

    /// <summary>The minimum contrast the knocked-out digits must clear against their badge.</summary>
    public const double MinimumContrast = 4.5d;

    public static TrayBadgeStyle For(AlertBand band, bool lightTaskbar) => band switch
    {
        // White on this red clears 4.5:1; black on it does not.
        AlertBand.Critical => new TrayBadgeStyle(Critical, LightInk, Filled: true),

        // Amber is a light colour: it takes dark digits, not white ones.
        AlertBand.Warning => new TrayBadgeStyle(Warning, DarkInk, Filled: true),

        // Healthy needs no badge; it takes the taskbar's own ink.
        _ => new TrayBadgeStyle(
            lightTaskbar ? LightInk : DarkInk,
            lightTaskbar ? DarkInk : LightInk,
            Filled: false),
    };
}
