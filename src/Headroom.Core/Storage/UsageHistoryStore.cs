using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Headroom.Core.Model;

namespace Headroom.Core.Storage;

/// <summary>One recorded observation of one window.</summary>
public sealed record HistoryPoint(DateTimeOffset At, string AccountId, string Scope, double UsedPercent)
{
    public double RemainingPercent => Math.Clamp(100d - UsedPercent, 0d, 100d);
}

/// <summary>
/// Append-only usage history, stored as month-partitioned JSON Lines.
/// </summary>
/// <remarks>
/// A database would be the reflex choice here and it would be the wrong one.
/// The entire dataset is a handful of accounts times a few windows times a poll
/// every few minutes — kilobytes a day. JSON Lines gives durable appends, needs
/// no native library, survives a partial write (a torn last line is skipped on
/// read), can be inspected with a text editor, backed up by copying, and pruned
/// by deleting a file. Nothing about SQLite would improve any of that, and it
/// would add a native dependency to an app that currently has none.
/// </remarks>
public sealed class UsageHistoryStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

    private readonly HeadroomPaths _paths;
    private readonly SemaphoreSlim _writeLock = new(1, 1);

    public UsageHistoryStore(HeadroomPaths paths) => _paths = paths;

    /// <summary>Records every percentage-bearing window of one account observation.</summary>
    public async Task AppendAsync(
        string accountId,
        IReadOnlyList<UsageWindow> windows,
        DateTimeOffset at,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(accountId) || windows.Count == 0) return;

        var rows = windows
            .Where(w => w.UsedPercent is not null && w.Kind != WindowKind.Spend)
            .Select(w => new Row
            {
                T = at.ToUnixTimeSeconds(),
                A = accountId,
                S = w.Scope,
                U = Math.Round(w.UsedPercent!.Value, 2),
            })
            .ToList();

        if (rows.Count == 0) return;

        var builder = new StringBuilder();
        foreach (var row in rows) builder.AppendLine(JsonSerializer.Serialize(row, Options));

        _paths.EnsureCreated();
        var file = FileFor(at);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await File.AppendAllTextAsync(file, builder.ToString(), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // History is a nicety. Losing a point must never break a refresh.
        }
        finally
        {
            _writeLock.Release();
        }
    }

    /// <summary>Reads every point in a window of time, oldest first.</summary>
    public IReadOnlyList<HistoryPoint> Read(DateTimeOffset from, DateTimeOffset to, string? accountId = null)
    {
        if (!Directory.Exists(_paths.HistoryDirectory)) return Array.Empty<HistoryPoint>();

        var points = new List<HistoryPoint>();
        foreach (var file in MonthFilesBetween(from, to))
        {
            if (!File.Exists(file)) continue;

            IEnumerable<string> lines;
            try
            {
                lines = File.ReadLines(file);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                Row? row;
                try
                {
                    row = JsonSerializer.Deserialize<Row>(line, Options);
                }
                catch (JsonException)
                {
                    // A torn final line from an interrupted write. Skip it.
                    continue;
                }

                if (row is null || string.IsNullOrEmpty(row.A) || string.IsNullOrEmpty(row.S)) continue;
                if (accountId is not null && !string.Equals(row.A, accountId, StringComparison.OrdinalIgnoreCase)) continue;

                var at = DateTimeOffset.FromUnixTimeSeconds(row.T);
                if (at < from || at > to) continue;

                points.Add(new HistoryPoint(at, row.A, row.S, row.U));
            }
        }

        points.Sort((a, b) => a.At.CompareTo(b.At));
        return points;
    }

    /// <summary>
    /// Reduces a series to at most <paramref name="maxPoints"/> buckets by taking
    /// the worst (highest used) value in each, because a chart of a usage limit
    /// should never hide the peak that actually blocked someone.
    /// </summary>
    public static IReadOnlyList<HistoryPoint> Downsample(IReadOnlyList<HistoryPoint> points, int maxPoints)
    {
        if (maxPoints <= 0 || points.Count <= maxPoints) return points;

        var first = points[0].At;
        var last = points[^1].At;
        var span = last - first;
        if (span <= TimeSpan.Zero) return points;

        var bucketSeconds = span.TotalSeconds / maxPoints;
        var buckets = new Dictionary<long, HistoryPoint>();

        foreach (var point in points)
        {
            var index = (long)((point.At - first).TotalSeconds / bucketSeconds);
            if (!buckets.TryGetValue(index, out var existing) || point.UsedPercent > existing.UsedPercent)
                buckets[index] = point;
        }

        return buckets.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
    }

    /// <summary>Deletes month files entirely outside the retention window.</summary>
    public int Prune(int retentionDays, DateTimeOffset now)
    {
        if (!Directory.Exists(_paths.HistoryDirectory)) return 0;

        var cutoff = now.AddDays(-Math.Max(1, retentionDays));
        var removed = 0;

        foreach (var file in Directory.EnumerateFiles(_paths.HistoryDirectory, "usage-*.jsonl"))
        {
            var stamp = Path.GetFileNameWithoutExtension(file)["usage-".Length..];
            if (!DateTime.TryParseExact(stamp, "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var month))
                continue;

            // Only drop a file once its whole month is behind the cutoff.
            var monthEnd = new DateTimeOffset(month, TimeSpan.Zero).AddMonths(1);
            if (monthEnd >= cutoff) continue;

            try
            {
                File.Delete(file);
                removed++;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Leave it; it will be retried next time.
            }
        }

        return removed;
    }

    private string FileFor(DateTimeOffset at) =>
        Path.Combine(_paths.HistoryDirectory, $"usage-{at.UtcDateTime:yyyy-MM}.jsonl");

    private IEnumerable<string> MonthFilesBetween(DateTimeOffset from, DateTimeOffset to)
    {
        var cursor = new DateTime(from.UtcDateTime.Year, from.UtcDateTime.Month, 1, 0, 0, 0, DateTimeKind.Utc);
        var end = new DateTime(to.UtcDateTime.Year, to.UtcDateTime.Month, 1, 0, 0, 0, DateTimeKind.Utc);

        while (cursor <= end)
        {
            yield return Path.Combine(_paths.HistoryDirectory, $"usage-{cursor:yyyy-MM}.jsonl");
            cursor = cursor.AddMonths(1);
        }
    }

    private sealed class Row
    {
        public long T { get; set; }

        public string A { get; set; } = string.Empty;

        public string S { get; set; } = string.Empty;

        public double U { get; set; }
    }
}
