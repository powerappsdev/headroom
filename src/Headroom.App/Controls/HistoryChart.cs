using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Headroom.App.ViewModels;
using Headroom.Core.Storage;

namespace Headroom.App.Controls;

/// <summary>
/// The usage history plot: remaining capacity over time, one line per window.
/// </summary>
/// <remarks>
/// Deliberately hand-drawn rather than delegated to a charting library, because
/// the rules that make this chart readable are specific and short: one y-axis
/// only, recessive grid, 2px lines, every series direct-labelled at its right
/// end (which is also what carries identity where a pale hue sits below 3:1 on
/// the light surface), a crosshair with a tooltip, and colours assigned by the
/// window's identity rather than its position in the current filter - so
/// toggling a series never repaints the survivors.
/// </remarks>
public sealed class HistoryChart : FrameworkElement
{
    private const double LeftGutter = 34d;
    private const double RightGutter = 74d;
    private const double TopGutter = 10d;
    private const double BottomGutter = 22d;

    private static readonly string[] SeriesBrushKeys =
    {
        "Series1Color", "Series2Color", "Series3Color", "Series4Color",
    };

    private readonly Dictionary<string, Point> _lineEnds = new(StringComparer.OrdinalIgnoreCase);
    private Point? _hoverPoint;

    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(
        nameof(Series),
        typeof(IEnumerable<HistorySeries>),
        typeof(HistoryChart),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, OnSeriesChanged));

    public static readonly DependencyProperty WarningPercentProperty = DependencyProperty.Register(
        nameof(WarningPercent),
        typeof(double),
        typeof(HistoryChart),
        new FrameworkPropertyMetadata(25d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty CriticalPercentProperty = DependencyProperty.Register(
        nameof(CriticalPercent),
        typeof(double),
        typeof(HistoryChart),
        new FrameworkPropertyMetadata(10d, FrameworkPropertyMetadataOptions.AffectsRender));

    public HistoryChart()
    {
        ClipToBounds = true;
        Focusable = false;
    }

    public IEnumerable<HistorySeries>? Series
    {
        get => (IEnumerable<HistorySeries>?)GetValue(SeriesProperty);
        set => SetValue(SeriesProperty, value);
    }

    public double WarningPercent
    {
        get => (double)GetValue(WarningPercentProperty);
        set => SetValue(WarningPercentProperty, value);
    }

    public double CriticalPercent
    {
        get => (double)GetValue(CriticalPercentProperty);
        set => SetValue(CriticalPercentProperty, value);
    }

    /// <summary>
    /// The bound collection is refilled in place rather than replaced, so the
    /// dependency property alone never fires. Watching the collection is what
    /// makes the chart redraw when a range or account changes.
    /// </summary>
    private static void OnSeriesChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not HistoryChart chart) return;

        if (e.OldValue is System.Collections.Specialized.INotifyCollectionChanged oldCollection)
            oldCollection.CollectionChanged -= chart.OnSeriesCollectionChanged;

        if (e.NewValue is System.Collections.Specialized.INotifyCollectionChanged newCollection)
            newCollection.CollectionChanged += chart.OnSeriesCollectionChanged;

        chart.InvalidateVisual();
    }

    private void OnSeriesCollectionChanged(
        object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e) => InvalidateVisual();

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _hoverPoint = e.GetPosition(this);
        InvalidateVisual();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hoverPoint = null;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var dc = drawingContext;
        var series = Series?.Where(s => s.Points.Count > 0).ToList() ?? new List<HistorySeries>();
        var plot = new Rect(
            LeftGutter,
            TopGutter,
            Math.Max(0d, ActualWidth - LeftGutter - RightGutter),
            Math.Max(0d, ActualHeight - TopGutter - BottomGutter));

        if (plot.Width <= 4d || plot.Height <= 4d) return;

        // Rebuilt every frame: a series that disappeared must not leave a label behind.
        _lineEnds.Clear();

        // A transparent fill gives the element a hit-test surface for hover.
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, ActualWidth, ActualHeight));

        var muted = FindBrush("TextMutedBrush", Colors.Gray);
        var hairline = FindPen("HairlineBrush", Colors.DimGray, 1d);

        DrawGrid(dc, plot, muted, hairline);

        if (series.Count == 0) return;

        var (from, to) = TimeExtent(series);
        if (to <= from) to = from + TimeSpan.FromMinutes(1);

        DrawThresholdGuides(dc, plot);

        for (var i = 0; i < series.Count; i++)
            DrawSeries(dc, plot, series[i], from, to, ColorFor(series[i].ColorSlot));

        DrawCrosshair(dc, plot, series, from, to, muted);
    }

    private void DrawGrid(DrawingContext dc, Rect plot, Brush muted, Pen hairline)
    {
        for (var value = 0; value <= 100; value += 25)
        {
            var y = plot.Bottom - (plot.Height * (value / 100d));
            dc.DrawLine(hairline, new Point(plot.Left, Snap(y)), new Point(plot.Right, Snap(y)));

            var label = Text(value.ToString(CultureInfo.CurrentCulture) + "%", muted, 9.5d);
            dc.DrawText(label, new Point(plot.Left - label.Width - 6d, y - (label.Height / 2d)));
        }
    }

    /// <summary>
    /// Dashed guides at the alert thresholds. These are the lines that make the
    /// chart answer "how often do I actually run out" instead of just "what
    /// happened".
    /// </summary>
    private void DrawThresholdGuides(DrawingContext dc, Rect plot)
    {
        DrawGuide(WarningPercent, "MeterWarningBrush");
        DrawGuide(CriticalPercent, "MeterCriticalBrush");

        void DrawGuide(double percent, string brushKey)
        {
            if (percent is <= 0d or >= 100d) return;

            var brush = FindBrush(brushKey, Colors.Goldenrod).Clone();
            brush.Opacity = 0.55d;
            brush.Freeze();

            var pen = new Pen(brush, 1d) { DashStyle = new DashStyle(new double[] { 3, 3 }, 0) };
            pen.Freeze();

            var y = Snap(plot.Bottom - (plot.Height * (percent / 100d)));
            dc.DrawLine(pen, new Point(plot.Left, y), new Point(plot.Right, y));
        }
    }

    private void DrawSeries(
        DrawingContext dc, Rect plot, HistorySeries series, DateTimeOffset from, DateTimeOffset to, Color color)
    {
        var span = (to - from).TotalSeconds;
        if (span <= 0d) return;

        var brush = new SolidColorBrush(color);
        brush.Freeze();
        var pen = new Pen(brush, 2d) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round };
        pen.Freeze();

        var geometry = new StreamGeometry();
        var started = false;
        Point last = default;

        using (var context = geometry.Open())
        {
            foreach (var point in series.Points)
            {
                var x = plot.Left + (plot.Width * ((point.At - from).TotalSeconds / span));
                var y = plot.Bottom - (plot.Height * (point.RemainingPercent / 100d));
                last = new Point(x, y);

                if (!started)
                {
                    context.BeginFigure(last, isFilled: false, isClosed: false);
                    started = true;
                }
                else
                {
                    context.LineTo(last, isStroked: true, isSmoothJoin: true);
                }
            }
        }

        if (!started) return;

        geometry.Freeze();
        dc.DrawGeometry(null, pen, geometry);
        _lineEnds[series.Scope] = last;

        // Direct label at the right end. This is the accessibility relief for a
        // series colour that sits below 3:1 on a light surface, and it also
        // removes the need to read a legend back and forth.
        if (_lineEnds.TryGetValue(series.Scope, out var end))
        {
            var label = Text(series.Scope, brush, 10d);
            // Math.Clamp throws when min exceeds max, which a very short plot can produce.
            var y = Math.Clamp(
                end.Y - (label.Height / 2d),
                plot.Top,
                Math.Max(plot.Top, plot.Bottom - label.Height));
            dc.DrawEllipse(brush, null, end, 2.5d, 2.5d);
            dc.DrawText(label, new Point(Math.Min(end.X + 7d, ActualWidth - label.Width - 2d), y));
        }
    }

    private void DrawCrosshair(
        DrawingContext dc,
        Rect plot,
        IReadOnlyList<HistorySeries> series,
        DateTimeOffset from,
        DateTimeOffset to,
        Brush muted)
    {
        if (_hoverPoint is not { } hover) return;
        if (hover.X < plot.Left || hover.X > plot.Right || hover.Y < plot.Top || hover.Y > plot.Bottom) return;

        var fraction = (hover.X - plot.Left) / plot.Width;
        var at = from + TimeSpan.FromSeconds((to - from).TotalSeconds * fraction);

        var crosshair = new Pen(muted, 1d) { DashStyle = new DashStyle(new double[] { 2, 2 }, 0) };
        crosshair.Freeze();
        dc.DrawLine(crosshair, new Point(Snap(hover.X), plot.Top), new Point(Snap(hover.X), plot.Bottom));

        var lines = new List<(string Text, Brush Brush)>
        {
            (at.ToLocalTime().ToString("ddd MMM d, h:mm tt", CultureInfo.CurrentCulture),
                FindBrush("TextSecondaryBrush", Colors.Gray)),
        };

        foreach (var s in series)
        {
            var nearest = Nearest(s, at);
            if (nearest is null) continue;

            var brush = new SolidColorBrush(ColorFor(s.ColorSlot));
            brush.Freeze();
            lines.Add((
                $"{s.Scope}   {nearest.RemainingPercent.ToString("0", CultureInfo.CurrentCulture)}% left",
                brush));
        }

        DrawTooltip(dc, plot, hover, lines);
    }

    private void DrawTooltip(DrawingContext dc, Rect plot, Point hover, List<(string Text, Brush Brush)> lines)
    {
        const double padding = 8d;
        var texts = lines.Select(l => Text(l.Text, l.Brush, 10.5d)).ToList();
        var width = texts.Max(t => t.Width) + (padding * 2d);
        var height = texts.Sum(t => t.Height + 2d) + (padding * 2d) - 2d;

        var left = hover.X + 12d;
        if (left + width > ActualWidth - 2d) left = hover.X - width - 12d;
        left = Math.Max(2d, left);

        var top = Math.Clamp(hover.Y - (height / 2d), plot.Top, Math.Max(plot.Top, plot.Bottom - height));

        var background = FindBrush("SurfaceElevatedBrush", Colors.Black);
        var border = FindPen("HairlineBrush", Colors.DimGray, 1d);
        dc.DrawRoundedRectangle(background, border, new Rect(left, top, width, height), 6d, 6d);

        var y = top + padding;
        foreach (var text in texts)
        {
            dc.DrawText(text, new Point(left + padding, y));
            y += text.Height + 2d;
        }
    }

    private static HistoryPoint? Nearest(HistorySeries series, DateTimeOffset at)
    {
        HistoryPoint? best = null;
        var bestDistance = double.MaxValue;

        foreach (var point in series.Points)
        {
            var distance = Math.Abs((point.At - at).TotalSeconds);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = point;
        }

        return best;
    }

    private static (DateTimeOffset From, DateTimeOffset To) TimeExtent(IReadOnlyList<HistorySeries> series)
    {
        var from = series.Min(s => s.Points[0].At);
        var to = series.Max(s => s.Points[^1].At);
        return (from, to);
    }

    private Color ColorFor(int slot)
    {
        var key = SeriesBrushKeys[Math.Abs(slot) % SeriesBrushKeys.Length];
        return TryFindResource(key) is Color color ? color : Colors.SteelBlue;
    }

    private Brush FindBrush(string key, Color fallback)
    {
        if (TryFindResource(key) is Brush brush) return brush;
        var solid = new SolidColorBrush(fallback);
        solid.Freeze();
        return solid;
    }

    private Pen FindPen(string brushKey, Color fallback, double thickness)
    {
        var pen = new Pen(FindBrush(brushKey, fallback), thickness);
        pen.Freeze();
        return pen;
    }

    private FormattedText Text(string value, Brush brush, double size) => new(
        value,
        CultureInfo.CurrentCulture,
        FlowDirection.LeftToRight,
        new Typeface("Segoe UI"),
        size,
        brush,
        VisualTreeHelper.GetDpi(this).PixelsPerDip);

    /// <summary>Aligns a hairline to the device pixel grid so it renders crisp rather than grey.</summary>
    private static double Snap(double value) => Math.Round(value) + 0.5d;
}
