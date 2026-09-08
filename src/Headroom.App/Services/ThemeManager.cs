using System;
using System.Windows;
using Headroom.Core.Storage;

namespace Headroom.App.Services;

/// <summary>
/// Swaps the palette dictionary so the whole app changes theme at once.
/// </summary>
/// <remarks>
/// The two palettes share every resource key, so switching is a single
/// dictionary replacement and every <c>DynamicResource</c> in the app follows.
/// Dark mode is a selected palette rather than an inverted light one; nothing
/// here computes a colour.
/// </remarks>
public sealed class ThemeManager
{
    // Pack URIs, not relative ones: a relative Uri handed to a ResourceDictionary
    // at runtime resolves against the process working directory, which is not
    // where the app's resources live once it is launched from a shortcut.
    private const string DarkSource = "pack://application:,,,/Headroom;component/Theme/Palette.Dark.xaml";
    private const string LightSource = "pack://application:,,,/Headroom;component/Theme/Palette.Light.xaml";

    private DeckTheme _theme = DeckTheme.System;
    private bool _applied;

    public bool IsDark { get; private set; } = true;

    public event EventHandler? ThemeChanged;

    public void Apply(DeckTheme theme)
    {
        _theme = theme;
        Refresh();
    }

    /// <summary>Re-evaluates the system setting. Called when Windows reports a settings change.</summary>
    public void Refresh()
    {
        var dark = _theme switch
        {
            DeckTheme.Dark => true,
            DeckTheme.Light => false,
            _ => SystemPrefersDark(),
        };

        var dictionaries = Application.Current?.Resources.MergedDictionaries;
        if (dictionaries is null || dictionaries.Count == 0) return;

        // Slot 0 is the palette by convention; slot 1 onwards are the control
        // styles, which reference the palette through DynamicResource.
        if (_applied && IsDark == dark) return;

        dictionaries[0] = new ResourceDictionary
        {
            Source = new Uri(dark ? DarkSource : LightSource, UriKind.Absolute),
        };

        IsDark = dark;
        _applied = true;
        ThemeChanged?.Invoke(this, EventArgs.Empty);
    }

    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value ? value == 0 : true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return true;
        }
    }
}
