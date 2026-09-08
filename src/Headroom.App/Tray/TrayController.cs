using System;
using System.Runtime.Versioning;
using Headroom.App.ViewModels;
using Headroom.Core.Formatting;
using Headroom.Core.Model;
using Forms = System.Windows.Forms;

namespace Headroom.App.Tray;

/// <summary>
/// Owns the notification-area icon, its menu, and the balloon notifications.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class TrayController : IDisposable
{
    private readonly Forms.NotifyIcon _notifyIcon;
    private TrayIconRenderer.TrayIcon? _currentIcon;
    private (double? Remaining, MeterTone Tone, bool ShowPercent, bool LightTray)? _iconState;
    private bool _disposed;

    public TrayController()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Open Headroom", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Refresh now", null, (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Usage history…", null, (_, _) => HistoryRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add("Settings…", null, (_, _) => SettingsRequested?.Invoke(this, EventArgs.Empty));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add("Quit Headroom", null, (_, _) => QuitRequested?.Invoke(this, EventArgs.Empty));

        _notifyIcon = new Forms.NotifyIcon
        {
            Text = "Headroom",
            Visible = true,
            ContextMenuStrip = menu,
        };

        _notifyIcon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OpenRequested?.Invoke(this, EventArgs.Empty);
        };

        _notifyIcon.BalloonTipClicked += (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty);

        SetIcon(null, MeterTone.Unknown, alwaysShowPercent: false);
    }

    public event EventHandler? OpenRequested;

    public event EventHandler? RefreshRequested;

    public event EventHandler? SettingsRequested;

    public event EventHandler? HistoryRequested;

    public event EventHandler? QuitRequested;

    /// <summary>Redraws the icon and rewrites the hover text from the current deck state.</summary>
    public void Update(DeckSnapshot deck, string? pinnedAccountId, bool alwaysShowPercent, Thresholds thresholds)
    {
        var account = deck.TrayAccount(pinnedAccountId);
        var remaining = account?.RemainingPercent;

        var tone = remaining is null
            ? MeterTone.Unknown
            : thresholds.Band(remaining) switch
            {
                AlertBand.Critical => MeterTone.Critical,
                AlertBand.Warning => MeterTone.Warning,
                _ => MeterTone.Healthy,
            };

        SetIcon(remaining, tone, alwaysShowPercent);
        _notifyIcon.Text = Truncate(BuildTooltip(account, deck));
    }

    /// <summary>
    /// Shows a shell notification. Balloon tips surface as ordinary Windows
    /// toasts on Windows 10 and 11, which is how this stays package-free.
    /// </summary>
    public void Notify(string title, string body, bool warning)
    {
        if (_disposed) return;

        _notifyIcon.BalloonTipTitle = Truncate(title, 63);
        _notifyIcon.BalloonTipText = Truncate(body, 255);
        _notifyIcon.BalloonTipIcon = warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info;
        _notifyIcon.ShowBalloonTip(8000);
    }

    private void SetIcon(double? remaining, MeterTone tone, bool alwaysShowPercent)
    {
        if (_disposed) return;

        // The deck re-renders every 30 seconds to keep relative times fresh. Only
        // redraw the icon when something it actually shows has changed, so the
        // shell is not handed a new HICON twice a minute for no reason.
        var rounded = remaining is null ? (double?)null : Math.Round(remaining.Value);
        var lightTray = IsLightTrayBackground();
        var state = (rounded, tone, alwaysShowPercent, lightTray);

        if (_iconState is { } previous && previous.Equals(state) && _currentIcon is not null) return;
        _iconState = state;

        var icon = TrayIconRenderer.Render(remaining, tone, alwaysShowPercent, lightTray);
        var outgoing = _currentIcon;

        // Assign before disposing the old one: the shell reads the handle during
        // the swap, and freeing it first produces a flash of a blank slot.
        _notifyIcon.Icon = icon.Icon;
        _currentIcon = icon;
        outgoing?.Dispose();
    }

    /// <summary>
    /// The taskbar follows the "apps vs system" theme split, so the glyph colour
    /// comes from SystemUsesLightTheme, not from the app's own theme setting.
    /// </summary>
    private static bool IsLightTrayBackground()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int value && value != 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or System.IO.IOException)
        {
            return false;
        }
    }

    private static string BuildTooltip(AccountSnapshot? account, DeckSnapshot deck)
    {
        if (account is null)
        {
            return deck.AnyConfigured
                ? "Headroom — no usage data yet"
                : "Headroom — no accounts configured";
        }

        var window = account.WorstWindow;
        var reset = window?.ResetsAt is null
            ? string.Empty
            : "\n" + DisplayText.ResetText(window.ResetsAt, DateTimeOffset.UtcNow);

        return $"{account.DisplayName} — {DisplayText.PercentLeft(account.RemainingPercent)}"
             + (window is null ? string.Empty : $" ({window.Scope})")
             + reset;
    }

    /// <summary>
    /// WinForms validates NotifyIcon.Text against a 63-character limit and throws
    /// above it, so the default here is the value that is safe on every version.
    /// </summary>
    private static string Truncate(string value, int max = 63) =>
        value.Length <= max ? value : value[..(max - 1)] + "…";

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _notifyIcon.Visible = false;
        _notifyIcon.ContextMenuStrip?.Dispose();
        _notifyIcon.Dispose();
        _currentIcon?.Dispose();
    }
}
