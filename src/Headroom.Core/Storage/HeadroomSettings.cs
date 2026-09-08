using System;
using System.Collections.Generic;
using Headroom.Core.Accounts;
using Headroom.Core.Model;

namespace Headroom.Core.Storage;

public enum DeckTheme
{
    System,
    Dark,
    Light,
}

public enum DeckSort
{
    NextReset,
    LowestRemaining,
    Provider,
}

/// <summary>Everything the user can change, in one serializable object.</summary>
public sealed record HeadroomSettings
{
    public const int SchemaVersion = 1;

    public int Version { get; init; } = SchemaVersion;

    public List<AccountDefinition> Accounts { get; init; } = new();

    /// <summary>How often to poll, in seconds. Clamped on load, never trusted raw.</summary>
    public int RefreshIntervalSeconds { get; init; } = 300;

    public bool AutoRefresh { get; init; } = true;

    public double WarningPercent { get; init; } = 25d;

    public double CriticalPercent { get; init; } = 10d;

    public bool NotificationsEnabled { get; init; } = true;

    /// <summary>Account whose percentage stays in the tray. Null means "whichever is lowest".</summary>
    public string? PinnedAccountId { get; init; }

    /// <summary>Show the percentage in the tray even when everything is healthy.</summary>
    public bool AlwaysShowPercent { get; init; }

    public DeckTheme Theme { get; init; } = DeckTheme.System;

    public DeckSort Sort { get; init; } = DeckSort.NextReset;

    public bool LaunchAtLogin { get; init; }

    public int HistoryRetentionDays { get; init; } = 120;

    /// <summary>Command used to launch the Codex CLI. Overridable for nonstandard installs.</summary>
    public string CodexCommand { get; init; } = "codex";

    /// <summary>Command used to read the Claude CLI version for the request user agent.</summary>
    public string ClaudeCommand { get; init; } = "claude";

    public Thresholds ToThresholds() => new Thresholds(WarningPercent, CriticalPercent).Normalized();

    public TimeSpan RefreshInterval => TimeSpan.FromSeconds(Math.Clamp(RefreshIntervalSeconds, 60, 3600));

    /// <summary>
    /// Clamps everything that a hand-edited settings file could get wrong, so a
    /// bad value degrades to a sane one instead of breaking the app.
    /// </summary>
    public HeadroomSettings Normalized()
    {
        var thresholds = ToThresholds();
        return this with
        {
            Version = SchemaVersion,
            RefreshIntervalSeconds = Math.Clamp(RefreshIntervalSeconds, 60, 3600),
            WarningPercent = thresholds.WarningPercent,
            CriticalPercent = thresholds.CriticalPercent,
            HistoryRetentionDays = Math.Clamp(HistoryRetentionDays, 7, 3650),
            CodexCommand = string.IsNullOrWhiteSpace(CodexCommand) ? "codex" : CodexCommand.Trim(),
            ClaudeCommand = string.IsNullOrWhiteSpace(ClaudeCommand) ? "claude" : ClaudeCommand.Trim(),
            Accounts = Accounts ?? new List<AccountDefinition>(),
        };
    }
}
