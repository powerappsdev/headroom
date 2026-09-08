using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Headroom.App.ViewModels;

namespace Headroom.App.Controls;

/// <summary>
/// The remaining-capacity bar.
/// </summary>
/// <remarks>
/// Drawn rather than composed from Border elements for three reasons: the
/// rounded data-end has to stay anchored to the baseline at every width, a tiny
/// non-zero value must still render as a visible cap instead of vanishing, and
/// a deck of thirty of these should cost thirty draw calls rather than a hundred
/// and twenty layout objects.
/// </remarks>
public sealed class MeterBar : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value),
        typeof(double),
        typeof(MeterBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender, OnValueChanged));

    /// <summary>The animated value actually drawn, so a refresh glides instead of jumping.</summary>
    private static readonly DependencyProperty RenderValueProperty = DependencyProperty.Register(
        nameof(RenderValue),
        typeof(double),
        typeof(MeterBar),
        new FrameworkPropertyMetadata(0d, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty ToneProperty = DependencyProperty.Register(
        nameof(Tone),
        typeof(MeterTone),
        typeof(MeterBar),
        new FrameworkPropertyMetadata(MeterTone.Healthy, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty BarHeightProperty = DependencyProperty.Register(
        nameof(BarHeight),
        typeof(double),
        typeof(MeterBar),
        new FrameworkPropertyMetadata(4d, FrameworkPropertyMetadataOptions.AffectsMeasure | FrameworkPropertyMetadataOptions.AffectsRender));

    /// <summary>0 to 1.</summary>
    public double Value
    {
        get => (double)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    private double RenderValue
    {
        get => (double)GetValue(RenderValueProperty);
        set => SetValue(RenderValueProperty, value);
    }

    public MeterTone Tone
    {
        get => (MeterTone)GetValue(ToneProperty);
        set => SetValue(ToneProperty, value);
    }

    public double BarHeight
    {
        get => (double)GetValue(BarHeightProperty);
        set => SetValue(BarHeightProperty, value);
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsInfinity(availableSize.Width) ? 120d : availableSize.Width;
        return new Size(width, BarHeight);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var height = Math.Max(2d, BarHeight);
        var width = ActualWidth;
        if (width <= 1d) return;

        var radius = height / 2d;
        var y = (ActualHeight - height) / 2d;

        var track = Brush("TrackBrush", Colors.Gray);
        drawingContext.DrawRoundedRectangle(track, null, new Rect(0, y, width, height), radius, radius);

        var fraction = Math.Clamp(RenderValue, 0d, 1d);
        if (fraction <= 0d) return;

        // Never let a small-but-real value round away to nothing: the shortest
        // bar Headroom will draw is one cap wide, which still reads as "some".
        var fillWidth = Math.Max(width * fraction, height);
        var fill = Brush(ToneBrushKey(Tone), Colors.SteelBlue);
        drawingContext.DrawRoundedRectangle(fill, null, new Rect(0, y, fillWidth, height), radius, radius);
    }

    private static string ToneBrushKey(MeterTone tone) => tone switch
    {
        MeterTone.Critical => "MeterCriticalBrush",
        MeterTone.Warning => "MeterWarningBrush",
        MeterTone.Unknown => "MeterUnknownBrush",
        _ => "MeterHealthyBrush",
    };

    private Brush Brush(string resourceKey, Color fallback) =>
        TryFindResource(resourceKey) as Brush ?? new SolidColorBrush(fallback);

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not MeterBar bar) return;

        var target = Math.Clamp((double)e.NewValue, 0d, 1d);
        var animation = new DoubleAnimation
        {
            To = target,
            Duration = TimeSpan.FromMilliseconds(420),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };

        bar.BeginAnimation(RenderValueProperty, animation);
    }
}
