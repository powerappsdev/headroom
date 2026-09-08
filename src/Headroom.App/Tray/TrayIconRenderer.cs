using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Headroom.App.ViewModels;

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

    private static readonly Color Healthy = Color.FromArgb(0x39, 0x87, 0xE5);
    private static readonly Color Warning = Color.FromArgb(0xFA, 0xB2, 0x19);
    private static readonly Color Critical = Color.FromArgb(0xD0, 0x3B, 0x3B);

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

    /// <summary>The alert state: the number is the icon.</summary>
    private static void DrawPercent(Graphics g, int size, double remaining, MeterTone tone, bool lightBackground)
    {
        var color = tone switch
        {
            MeterTone.Critical => Critical,
            MeterTone.Warning => Warning,
            _ => lightBackground ? Color.FromArgb(0x1F, 0x1F, 0x1D) : Color.White,
        };

        // 100 will not fit legibly in a tray square, and "full" is not news anyway.
        var value = (int)Math.Round(Math.Clamp(remaining, 0d, 99d), MidpointRounding.AwayFromZero);
        var text = value.ToString(System.Globalization.CultureInfo.InvariantCulture);

        var barHeight = Math.Max(1.5f, size * 0.11f);
        var textArea = new RectangleF(0, 0, size, size - barHeight - 1f);

        var fontSize = size * (text.Length >= 2 ? 0.62f : 0.78f);
        using var font = new Font("Segoe UI", fontSize, FontStyle.Bold, GraphicsUnit.Pixel);
        using var brush = new SolidBrush(color);
        using var format = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap,
        };

        g.DrawString(text, font, brush, textArea, format);

        // A thin fraction bar under the number: redundant with the digits on
        // purpose, so the state still reads at a glance without being parsed.
        var trackColor = lightBackground
            ? Color.FromArgb(60, 0, 0, 0)
            : Color.FromArgb(70, 255, 255, 255);

        using var track = new SolidBrush(trackColor);
        using var fill = new SolidBrush(color);

        var trackRect = new RectangleF(1f, size - barHeight - 0.5f, size - 2f, barHeight);
        var radius = barHeight / 2f;
        FillRounded(g, track, trackRect, radius);

        var fraction = (float)Math.Clamp(remaining / 100d, 0d, 1d);
        var fillWidth = trackRect.Width * fraction;
        if (fillWidth > 0.5f)
            FillRounded(g, fill, new RectangleF(trackRect.X, trackRect.Y, Math.Max(fillWidth, barHeight), barHeight), radius);
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
