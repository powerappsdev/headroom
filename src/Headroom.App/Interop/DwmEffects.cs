using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Headroom.App.Interop;

/// <summary>
/// Applies the Windows 11 window effects - rounded corners, a system backdrop
/// and the dark title-bar mode.
/// </summary>
/// <remarks>
/// Every call is best-effort. On Windows 10 these attributes simply do not
/// exist, and DwmSetWindowAttribute returns a failure HRESULT that is safely
/// ignored: the window then renders with its own painted background, which is
/// why the theme never relies on the backdrop for legibility.
/// </remarks>
public static class DwmEffects
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int CornerPreferenceRound = 2;
    private const int CornerPreferenceRoundSmall = 3;

    private const int BackdropMica = 2;
    private const int BackdropTransientWindow = 3;   // acrylic, for transient popups
    private const int BackdropTabbed = 4;

    public enum Backdrop
    {
        None = 1,
        Mica = BackdropMica,
        Acrylic = BackdropTransientWindow,
        Tabbed = BackdropTabbed,
    }

    public enum Corners
    {
        Default = 0,
        Round = CornerPreferenceRound,
        RoundSmall = CornerPreferenceRoundSmall,
    }

    [DllImport("dwmapi.dll", CharSet = CharSet.Unicode, SetLastError = false)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Applies dark mode, corner rounding and a backdrop in one call.</summary>
    public static void Apply(Window window, bool darkMode, Backdrop backdrop, Corners corners = Corners.Round)
    {
        var handle = new WindowInteropHelper(window).Handle;
        if (handle == IntPtr.Zero) return;

        SetAttribute(handle, DwmwaUseImmersiveDarkMode, darkMode ? 1 : 0);
        if (corners != Corners.Default) SetAttribute(handle, DwmwaWindowCornerPreference, (int)corners);
        SetAttribute(handle, DwmwaSystemBackdropType, (int)backdrop);
    }

    /// <summary>True when the OS is new enough for the backdrop attributes to mean anything.</summary>
    public static bool SupportsBackdrop => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621);

    private static void SetAttribute(IntPtr handle, int attribute, int value)
    {
        try
        {
            var local = value;
            _ = DwmSetWindowAttribute(handle, attribute, ref local, sizeof(int));
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows without these attributes. The painted theme covers it.
        }
    }
}
