using System;
using System.Windows;
using System.Windows.Interop;
using Forms = System.Windows.Forms;

namespace Headroom.App.Interop;

/// <summary>
/// Places the deck against the notification area, on the right monitor, at the
/// right corner for wherever the taskbar happens to live.
/// </summary>
/// <remarks>
/// Getting this right is most of what makes a tray app feel native. The window
/// is positioned from the working area of the monitor the pointer is on, which
/// keeps it correct on multi-monitor setups, and the taskbar edge is inferred by
/// comparing the working area to the full bounds rather than assumed to be at
/// the bottom.
/// </remarks>
public static class TrayPositioning
{
    private const double Margin = 12d;

    public static void PlaceNearTray(Window window)
    {
        if (window is null) return;

        var screen = SafeScreenFromCursor();
        if (screen is null) return;

        var scale = ScaleFor(window);
        var work = new Rect(
            screen.WorkingArea.Left / scale.X,
            screen.WorkingArea.Top / scale.Y,
            screen.WorkingArea.Width / scale.X,
            screen.WorkingArea.Height / scale.Y);

        var bounds = new Rect(
            screen.Bounds.Left / scale.X,
            screen.Bounds.Top / scale.Y,
            screen.Bounds.Width / scale.X,
            screen.Bounds.Height / scale.Y);

        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        if (double.IsNaN(width) || double.IsNaN(height)) return;

        // Which edge did the taskbar take out of the working area?
        var taskbarLeft = work.Left > bounds.Left + 1;
        var taskbarTop = work.Top > bounds.Top + 1;

        var left = taskbarLeft ? work.Left + Margin : work.Right - width - Margin;
        var top = taskbarTop ? work.Top + Margin : work.Bottom - height - Margin;

        // Never let the window escape the working area, whatever the arithmetic said.
        window.Left = Math.Clamp(left, work.Left + 2, Math.Max(work.Left + 2, work.Right - width - 2));
        window.Top = Math.Clamp(top, work.Top + 2, Math.Max(work.Top + 2, work.Bottom - height - 2));
    }

    private static Forms.Screen? SafeScreenFromCursor()
    {
        try
        {
            return Forms.Screen.FromPoint(Forms.Cursor.Position) ?? Forms.Screen.PrimaryScreen;
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return Forms.Screen.PrimaryScreen;
        }
    }

    /// <summary>Device pixels per DIP for the monitor this window is on.</summary>
    private static Point ScaleFor(Window window)
    {
        var source = PresentationSource.FromVisual(window);
        var matrix = source?.CompositionTarget?.TransformToDevice;
        return matrix is { } m && m.M11 > 0 && m.M22 > 0
            ? new Point(m.M11, m.M22)
            : new Point(1d, 1d);
    }
}
