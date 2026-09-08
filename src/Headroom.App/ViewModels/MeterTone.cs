namespace Headroom.App.ViewModels;

/// <summary>
/// How a meter should read.
/// </summary>
/// <remarks>
/// View models expose this rather than a brush so that every colour decision
/// lives in one theme dictionary, the light and dark palettes stay in step, and
/// the view model layer can be compiled and tested without a UI framework.
/// </remarks>
public enum MeterTone
{
    /// <summary>Plenty left.</summary>
    Healthy,

    /// <summary>Below the warning threshold.</summary>
    Warning,

    /// <summary>Below the critical threshold.</summary>
    Critical,

    /// <summary>The provider stated no percentage. Not an alarm - an absence.</summary>
    Unknown,
}
