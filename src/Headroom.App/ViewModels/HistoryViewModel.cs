using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using Headroom.Core.Model;
using Headroom.Core.Storage;

namespace Headroom.App.ViewModels;

/// <summary>A named time range offered by the history window.</summary>
public sealed record HistoryRange(string Label, TimeSpan Span, int MaxPoints)
{
    public static readonly IReadOnlyList<HistoryRange> All = new[]
    {
        new HistoryRange("24 hours", TimeSpan.FromHours(24), 240),
        new HistoryRange("7 days", TimeSpan.FromDays(7), 336),
        new HistoryRange("30 days", TimeSpan.FromDays(30), 360),
    };
}

/// <summary>
/// One line on the chart.
/// </summary>
/// <remarks>
/// <see cref="ColorSlot"/> is an index into the fixed categorical order, assigned
/// by the window's identity rather than by its rank in the current filter. That
/// is what stops a series from changing colour when another one is toggled off.
/// </remarks>
public sealed record HistorySeries(string Scope, int ColorSlot, IReadOnlyList<HistoryPoint> Points)
{
    public double MaxUsed => Points.Count == 0 ? 0d : Points.Max(p => p.UsedPercent);

    public double MinRemaining => Points.Count == 0 ? 100d : Points.Min(p => p.RemainingPercent);

    public double? LatestRemaining => Points.Count == 0 ? null : Points[^1].RemainingPercent;
}

/// <summary>A row of the accompanying table view, which is the accessibility relief for the chart.</summary>
public sealed record HistoryTableRow(string Scope, string Lowest, string Latest, string Samples);

public sealed class HistoryViewModel : ObservableObject
{
    private readonly UsageHistoryStore _history;

    private HistoryRange _range = HistoryRange.All[1];
    private string? _accountId;
    private string _accountName = string.Empty;
    private bool _showTable;
    private string _emptyMessage = "No history recorded yet.";

    public HistoryViewModel(UsageHistoryStore history) => _history = history;

    [System.Diagnostics.CodeAnalysis.SuppressMessage(
        "Performance",
        "CA1822:Mark members as static",
        Justification = "A XAML {Binding} resolves against the DataContext instance, so this cannot be static without breaking the range picker.")]
    public IReadOnlyList<HistoryRange> Ranges => HistoryRange.All;

    public ObservableCollection<HistorySeries> Series { get; } = new();

    public ObservableCollection<HistoryTableRow> TableRows { get; } = new();

    public HistoryRange Range
    {
        get => _range;
        set
        {
            if (Set(ref _range, value)) Reload(DateTimeOffset.UtcNow);
        }
    }

    public string AccountName
    {
        get => _accountName;
        private set => Set(ref _accountName, value);
    }

    /// <summary>Table view is always available, not just a fallback - some people simply prefer numbers.</summary>
    public bool ShowTable
    {
        get => _showTable;
        set => Set(ref _showTable, value);
    }

    public bool HasData => Series.Count > 0;

    public string EmptyMessage
    {
        get => _emptyMessage;
        private set => Set(ref _emptyMessage, value);
    }

    public void SelectAccount(string accountId, string accountName, DateTimeOffset now)
    {
        _accountId = accountId;
        AccountName = accountName;
        Reload(now);
    }

    public void Reload(DateTimeOffset now)
    {
        Series.Clear();
        TableRows.Clear();

        if (_accountId is null)
        {
            EmptyMessage = "Select an account to see its history.";
            Raise(nameof(HasData));
            return;
        }

        var points = _history.Read(now - _range.Span, now, _accountId);
        foreach (var series in BuildSeries(points, _range.MaxPoints))
        {
            Series.Add(series);
            TableRows.Add(new HistoryTableRow(
                series.Scope,
                series.MinRemaining.ToString("0", CultureInfo.CurrentCulture) + "%",
                series.LatestRemaining is { } latest
                    ? latest.ToString("0", CultureInfo.CurrentCulture) + "%"
                    : "—",
                series.Points.Count.ToString(CultureInfo.CurrentCulture)));
        }

        EmptyMessage = "No history recorded yet for this range. Headroom starts collecting from its first refresh.";
        Raise(nameof(HasData));
    }

    /// <summary>
    /// Groups points into series, ordering windows shortest-first so slot colours
    /// stay stable, and downsampling each line on its own.
    /// </summary>
    internal static IReadOnlyList<HistorySeries> BuildSeries(IReadOnlyList<HistoryPoint> points, int maxPoints)
    {
        if (points.Count == 0) return Array.Empty<HistorySeries>();

        var grouped = points
            .GroupBy(p => p.Scope, StringComparer.OrdinalIgnoreCase)
            .OrderBy(g => ScopeRank(g.Key))
            .ThenBy(g => g.Key, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        var series = new List<HistorySeries>(grouped.Count);
        for (var i = 0; i < grouped.Count; i++)
        {
            var ordered = grouped[i].OrderBy(p => p.At).ToList();
            series.Add(new HistorySeries(
                grouped[i].Key,
                i,
                UsageHistoryStore.Downsample(ordered, maxPoints)));
        }

        return series;
    }

    private static int ScopeRank(string scope)
    {
        if (scope.Equals("5-hour", StringComparison.OrdinalIgnoreCase)) return 0;
        if (scope.Equals("weekly", StringComparison.OrdinalIgnoreCase)) return 1;
        if (scope.EndsWith(" weekly", StringComparison.OrdinalIgnoreCase)) return 2;
        return 3;
    }
}
