using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Headroom.App.ViewModels;
using Headroom.Core.Model;

namespace Headroom.App.Tray;

/// <summary>
/// Draws the tray icon.
/// </summary>
/// <remarks>
/// macOS menu-bar apps can put a glyph and a percentage side by side; a Windows
/// tray slot is a small square, so the icon changes role instead of growing.
/// While everything is healthy it is a quiet three-bar glyph that reads as an
/// app icon. The moment something crosses a threshold, the number itself becomes
/// the icon, in its status colour, with a thin bar underneath showing the
/// fraction - so the state is legible at 16 pixels without any text beside it.
/// </remarks>
[SupportedOSPlatform("windows")]
public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr handle);

    /// <summary>
    /// An icon together with the unmanaged handle it was built from.
    /// </summary>
    /// <remarks>
    /// <see cref="Icon.FromHandle"/> does not take ownership of its handle, so
    /// the handle has to be destroyed separately or the process leaks one HICON
    /// per redraw. Pairing the two in a disposable is the documented way to do
    /// that, and it avoids the trap of freeing the handle while the shell is
    /// still drawing from it.
    /// </remarks>
    public sealed class TrayIcon : IDisposable
    {
        private bool _disposed;

        internal TrayIcon(Icon icon, IntPtr handle)
        {
            Icon = icon;
            Handle = handle;
        }

        public Icon Icon { get; }

        private IntPtr Handle { get; }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            Icon.Dispose();
            if (Handle != IntPtr.Zero) DestroyIcon(Handle);
        }
    }

    /// <summary>
    /// Builds an icon. The caller owns the result and must dispose it - but only
    /// after the replacement has been handed to the shell, never before.
    /// </summary>
    public static TrayIcon Render(double? remainingPercent, MeterTone tone, bool alwaysShowPercent, bool lightTrayBackground)
    {
        var size = IconSize();
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            // ClearType composites against the (transparent) background and leaves
            // dark fringes around the digits on a light taskbar.
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            var showNumber = remainingPercent is not null &&
                             (alwaysShowPercent || tone is MeterTone.Warning or MeterTone.Critical);

            if (showNumber) DrawPercent(g, size, remainingPercent!.Value, tone, lightTrayBackground);
            else DrawGlyph(g, size, lightTrayBackground);
        }

        var handle = bitmap.GetHicon();
        try
        {
            return new TrayIcon(Icon.FromHandle(handle), handle);
        }
        catch
        {
            DestroyIcon(handle);
            throw;
        }
    }

    /// <summary>Tray icons scale with the system DPI; ask Windows rather than assuming 16.</summary>
    private static int IconSize()
    {
        try
        {
            var size = System.Windows.Forms.SystemInformation.SmallIconSize.Width;
            return size is >= 16 and <= 64 ? size : 16;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return 16;
        }
    }

    /// <summary>The resting state: three stacked bars, like a deck of cards seen edge-on.</summary>
    private static void DrawGlyph(Graphics g, int size, bool lightBackground)
    {
        var ink = lightBackground ? Color.FromArgb(0x1F, 0x1F, 0x1D) : Color.White;
        using var brush = new SolidBrush(ink);

        var barHeight = Math.Max(2f, size * 0.135f);
        var gap = Math.Max(1.5f, size * 0.105f);
        var totalHeight = (barHeight * 3f) + (gap * 2f);
        var top = (size - totalHeight) / 2f;
        var radius = barHeight / 2f;

        // Bars of decreasing width read as "less left as you go down".
        float[] widths = { 0.74f, 0.60f, 0.44f };
        for (var i = 0; i < 3; i++)
        {
            var width = size * widths[i];
            var left = (size - (size * 0.74f)) / 2f;
            var y = top + (i * (barHeight + gap));
            FillRounded(g, brush, new RectangleF(left, y, width, barHeight), radius);
        }
    }

    /// <summary>
    /// The alert state: the number is the icon, on a badge of its own.
    /// </summary>
    /// <remarks>
    /// Two things make this legible at sixteen pixels where the previous
    /// version was not. The digits are built as a glyph path and scaled to fill
    /// the space, rather than drawn at a guessed point size and left to land
    /// wherever the font's internal leading puts them. And an alerting icon
    /// paints its own background, so the contrast is one this app sets rather
    /// than one it inherits from whatever the taskbar happens to be.
    /// </remarks>
    private static void DrawPercent(Graphics g, int size, double remaining, MeterTone tone, bool lightBackground)
    {
        var style = TrayBadge.For(BandFor(tone), lightBackground);
        var ink = ToColor(style.Ink);

        // 100 does not fit legibly in a tray square, and "full" is not news.
        var value = (int)Math.Round(Math.Clamp(remaining, 0d, 99d), MidpointRounding.AwayFromZero);
        var text = value.ToString(CultureInfo.InvariantCulture);

        RectangleF target;
        if (style.Filled)
        {
            using var badge = new SolidBrush(ToColor(style.Fill));
            var plate = new RectangleF(0f, 0f, size, size);
            FillRounded(g, badge, plate, size * 0.26f);

            // Inset so the digits sit inside the badge rather than against its
            // edge. Two digits need the horizontal room more than one does.
            var padX = text.Length >= 2 ? size * 0.10f : size * 0.24f;
            var padY = size * 0.16f;
            target = RectangleF.Inflate(plate, -padX, -padY);
        }
        else
        {
            var padX = text.Length >= 2 ? size * 0.04f : size * 0.20f;
            target = RectangleF.Inflate(new RectangleF(0f, 0f, size, size), -padX, -size * 0.10f);
        }

        DrawFittedDigits(g, text, ink, target);
    }

    private static AlertBand BandFor(MeterTone tone) => tone switch
    {
        MeterTone.Critical => AlertBand.Critical,
        MeterTone.Warning => AlertBand.Warning,
        _ => AlertBand.Healthy,
    };

    private static Color ToColor(Rgb value) => Color.FromArgb(value.R, value.G, value.B);

    /// <summary>
    /// Renders text as a filled glyph path scaled to fill <paramref name="target"/>.
    /// </summary>
    /// <remarks>
    /// Graphics.DrawString positions by the font's line box, which at this size
    /// wastes most of the icon on ascent and descent the digits never use. Going
    /// through a path lets the actual ink bounds be measured and scaled, so "9"
    /// and "91" both fill the space they are given.
    /// </remarks>
    private static void DrawFittedDigits(Graphics g, string text, Color ink, RectangleF target)
    {
        if (string.IsNullOrEmpty(text) || target.Width <= 0f || target.Height <= 0f) return;

        using var family = new FontFamily("Segoe UI");
        using var path = new GraphicsPath();

        // A large em size keeps the outline smooth; it is scaled down below.
        path.AddString(
            text,
            family,
            (int)FontStyle.Bold,
            100f,
            new PointF(0f, 0f),
            StringFormat.GenericTypographic);

        var bounds = path.GetBounds();
        if (bounds.Width <= 0f || bounds.Height <= 0f) return;

        var scale = Math.Min(target.Width / bounds.Width, target.Height / bounds.Height);

        using var transform = new Matrix();
        transform.Translate(
            target.X + ((target.Width - (bounds.Width * scale)) / 2f) - (bounds.X * scale),
            target.Y + ((target.Height - (bounds.Height * scale)) / 2f) - (bounds.Y * scale));
        transform.Scale(scale, scale);
        path.Transform(transform);

        using var brush = new SolidBrush(ink);
        g.FillPath(brush, path);
    }

    private static void FillRounded(Graphics g, Brush brush, RectangleF rect, float radius)
    {
        if (rect.Width <= 0 || rect.Height <= 0) return;

        radius = Math.Min(radius, Math.Min(rect.Width, rect.Height) / 2f);
        if (radius <= 0.5f)
        {
            g.FillRectangle(brush, rect);
            return;
        }

        var diameter = radius * 2f;
        using var path = new GraphicsPath();
        path.AddArc(rect.X, rect.Y, diameter, diameter, 180, 90);
        path.AddArc(rect.Right - diameter, rect.Y, diameter, diameter, 270, 90);
        path.AddArc(rect.Right - diameter, rect.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rect.X, rect.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        g.FillPath(brush, path);
    }
}
