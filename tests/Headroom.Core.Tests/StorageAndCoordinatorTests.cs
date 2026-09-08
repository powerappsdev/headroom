using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Headroom.Core.Accounts;
using Headroom.Core.Model;
using Headroom.Core.Providers;
using Headroom.Core.Refresh;
using Headroom.Core.Storage;

namespace Headroom.Core.Tests;

internal sealed class TempWorkspace : IDisposable
{
    public TempWorkspace()
    {
        Root = Path.Combine(Path.GetTempPath(), "headroom-tests-" + Guid.NewGuid().ToString("n")[..8]);
        Paths = new HeadroomPaths(Root);
        Paths.EnsureCreated();
    }

    public string Root { get; }

    public HeadroomPaths Paths { get; }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>A provider that returns whatever the test tells it to.</summary>
internal sealed class ScriptedProvider : IUsageProvider
{
    private readonly Queue<ProbeResult> _results = new();

    public ScriptedProvider(ProviderKind kind = ProviderKind.Claude) => Kind = kind;

    public ProviderKind Kind { get; }

    public int Calls { get; private set; }

    public ScriptedProvider Then(ProbeResult result)
    {
        _results.Enqueue(result);
        return this;
    }

    public Task<ProbeResult> ProbeAsync(AccountDefinition account, CancellationToken cancellationToken = default)
    {
        Calls++;
        var result = _results.Count > 0
            ? _results.Dequeue()
            : ProbeResult.Unavailable(AccountAvailability.ProviderUnreachable, "no scripted result", transient: true);
        return Task.FromResult(result);
    }
}

internal sealed class FixedClock : TimeProvider
{
    private DateTimeOffset _now;

    public FixedClock(DateTimeOffset now) => _now = now;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}

public static class HistoryStoreTests
{
    [Test]
    public static async Task AppendsAndReadsBack()
    {
        using var workspace = new TempWorkspace();
        var store = new UsageHistoryStore(workspace.Paths);
        var at = Moment.At("2026-09-07T12:00:00Z");

        await store.AppendAsync("acct", new[]
        {
            new UsageWindow { Scope = "5-hour", Kind = WindowKind.Session, UsedPercent = 40 },
            new UsageWindow { Scope = "weekly", Kind = WindowKind.Weekly, UsedPercent = 61.5 },
        }, at);

        var points = store.Read(at.AddHours(-1), at.AddHours(1));

        Check.Count(2, points);
        Check.Close(40, points.First(p => p.Scope == "5-hour").UsedPercent);
        Check.Close(38.5, points.First(p => p.Scope == "weekly").RemainingPercent);
    }

    [Test("Spend rows are not history - they are a budget, not a rate limit")]
    public static async Task SkipsSpendWindows()
    {
        using var workspace = new TempWorkspace();
        var store = new UsageHistoryStore(workspace.Paths);
        var at = Moment.At("2026-09-07T12:00:00Z");

        await store.AppendAsync("acct", new[]
        {
            new UsageWindow { Scope = "5-hour", Kind = WindowKind.Session, UsedPercent = 40 },
            new UsageWindow { Scope = "spend", Kind = WindowKind.Spend, UsedPercent = 90 },
        }, at);

        Check.Count(1, store.Read(at.AddHours(-1), at.AddHours(1)));
    }

    [Test]
    public static async Task FiltersByAccount()
    {
        using var workspace = new TempWorkspace();
        var store = new UsageHistoryStore(workspace.Paths);
        var at = Moment.At("2026-09-07T12:00:00Z");
        var window = new[] { new UsageWindow { Scope = "5-hour", Kind = WindowKind.Session, UsedPercent = 10 } };

        await store.AppendAsync("one", window, at);
        await store.AppendAsync("two", window, at);

        Check.Count(1, store.Read(at.AddHours(-1), at.AddHours(1), "one"));
        Check.Count(2, store.Read(at.AddHours(-1), at.AddHours(1)));
    }

    [Test("A torn final line from an interrupted write is skipped, not fatal")]
    public static async Task ToleratesTornLines()
    {
        using var workspace = new TempWorkspace();
        var store = new UsageHistoryStore(workspace.Paths);
        var at = Moment.At("2026-09-07T12:00:00Z");

        await store.AppendAsync("acct",
            new[] { new UsageWindow { Scope = "5-hour", Kind = WindowKind.Session, UsedPercent = 10 } }, at);

        var file = Path.Combine(workspace.Paths.HistoryDirectory, "usage-2026-09.jsonl");
        await File.AppendAllTextAsync(file, "{\"T\":1757246400,\"A\":\"acct\",\"S\":\"weekl");

        Check.Count(1, store.Read(at.AddHours(-1), at.AddHours(1)));
    }

    [Test("Downsampling keeps the peak, because the peak is what blocked you")]
    public static void DownsampleKeepsPeaks()
    {
        var start = Moment.At("2026-09-01T00:00:00Z");
        var points = Enumerable.Range(0, 1000)
            .Select(i => new HistoryPoint(start.AddMinutes(i), "a", "5-hour", i == 500 ? 99d : 10d))
            .ToList();

        var reduced = UsageHistoryStore.Downsample(points, 50);

        Check.True(reduced.Count <= 51, $"expected about 50 points, got {reduced.Count}");
        Check.True(reduced.Any(p => Math.Abs(p.UsedPercent - 99d) < 0.001), "the peak was lost in downsampling");
    }

    [Test]
    public static void DownsampleLeavesSmallSeriesAlone()
    {
        var start = Moment.At("2026-09-01T00:00:00Z");
        var points = Enumerable.Range(0, 10)
            .Select(i => new HistoryPoint(start.AddMinutes(i), "a", "5-hour", i))
            .ToList();

        Check.Count(10, UsageHistoryStore.Downsample(points, 50));
    }

    [Test]
    public static async Task PruneDropsOnlyFullyExpiredMonths()
    {
        using var workspace = new TempWorkspace();
        var store = new UsageHistoryStore(workspace.Paths);
        var window = new[] { new UsageWindow { Scope = "5-hour", Kind = WindowKind.Session, UsedPercent = 10 } };

        await store.AppendAsync("acct", window, Moment.At("2026-01-15T00:00:00Z"));
        await store.AppendAsync("acct", window, Moment.At("2026-09-01T00:00:00Z"));

        var removed = store.Prune(30, Moment.At("2026-09-07T00:00:00Z"));

        Check.Equal(1, removed);
        Check.False(File.Exists(Path.Combine(workspace.Paths.HistoryDirectory, "usage-2026-01.jsonl")));
        Check.True(File.Exists(Path.Combine(workspace.Paths.HistoryDirectory, "usage-2026-09.jsonl")));
    }
}

public static class SettingsStoreTests
{
    [Test]
    public static async Task RoundTripsSettings()
    {
        using var workspace = new TempWorkspace();
        var store = new SettingsStore(workspace.Paths);

        await store.SaveAsync(new HeadroomSettings
        {
            RefreshIntervalSeconds = 600,
            PinnedAccountId = "abc",
            Theme = DeckTheme.Dark,
            Accounts =
            {
                new AccountDefinition
                {
                    Id = "abc", DisplayName = "Work",
                    Provider = ProviderKind.Codex, ProfileDirectory = "/tmp/x",
                },
            },
        });

        var loaded = store.Load();

        Check.Equal(600, loaded.RefreshIntervalSeconds);
        Check.Equal("abc", loaded.PinnedAccountId);
        Check.Equal(DeckTheme.Dark, loaded.Theme);
        Check.Count(1, loaded.Accounts);
        Check.Equal(ProviderKind.Codex, loaded.Accounts[0].Provider);
    }

    [Test("A hand-edited file with nonsense values is clamped, not obeyed")]
    public static void ClampsOutOfRangeValues()
    {
        var normalized = new HeadroomSettings
        {
            RefreshIntervalSeconds = 1,
            WarningPercent = 500,
            CriticalPercent = -20,
            HistoryRetentionDays = 0,
            CodexCommand = "   ",
        }.Normalized();

        Check.Equal(60, normalized.RefreshIntervalSeconds);
        Check.True(normalized.WarningPercent <= 99);
        Check.True(normalized.CriticalPercent >= 0);
        Check.Equal(7, normalized.HistoryRetentionDays);
        Check.Equal("codex", normalized.CodexCommand);
    }

    [Test("A corrupt settings file is quarantined, never silently deleted")]
    public static void CorruptFileIsQuarantined()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllText(workspace.Paths.SettingsFile, "{ this is not json");

        var loaded = new SettingsStore(workspace.Paths).Load();

        Check.Equal(300, loaded.RefreshIntervalSeconds);
        Check.True(
            Directory.GetFiles(workspace.Paths.Root, "settings.json.corrupt-*").Length == 1,
            "the unreadable file should have been kept as a backup");
    }

    [Test]
    public static void MissingFileYieldsDefaults()
    {
        using var workspace = new TempWorkspace();
        var loaded = new SettingsStore(workspace.Paths).Load();

        Check.Equal(300, loaded.RefreshIntervalSeconds);
        Check.Count(0, loaded.Accounts);
        Check.True(loaded.AutoRefresh);
    }
}

public static class RefreshCoordinatorTests
{
    private static readonly DateTimeOffset Start = Moment.At("2026-09-07T12:00:00Z");

    private static AccountDefinition Account(string id = "a") => new()
    {
        Id = id,
        DisplayName = "Work",
        Provider = ProviderKind.Claude,
        ProfileDirectory = "/tmp/profile",
    };

    private static UsageWindow[] Windows(double used) => new[]
    {
        new UsageWindow { Scope = "5-hour", Kind = WindowKind.Session, UsedPercent = used },
    };

    [Test]
    public static async Task SuccessPopulatesTheDeck()
    {
        var provider = new ScriptedProvider().Then(ProbeResult.Success(Windows(30), "a@b.c", "max_20x"));
        var clock = new FixedClock(Start);
        var coordinator = new RefreshCoordinator(
            new Dictionary<ProviderKind, IUsageProvider> { [ProviderKind.Claude] = provider }, time: clock);

        coordinator.SetAccounts(new[] { Account() });
        var deck = await coordinator.RefreshAsync();

        Check.Count(1, deck.Accounts);
        Check.Close(70, deck.Accounts[0].RemainingPercent);
        Check.Equal("a@b.c", deck.Accounts[0].Identity);
        Check.Equal("max_20x", deck.Accounts[0].PlanTier);
        Check.Equal(Start, Check.NotNullValue(deck.Accounts[0].ObservedAt));
    }

    [Test("A failed refresh keeps the last good numbers and their original timestamp")]
    public static async Task FailureKeepsLastGoodData()
    {
        var provider = new ScriptedProvider()
            .Then(ProbeResult.Success(Windows(30)))
            .Then(ProbeResult.Unavailable(AccountAvailability.ProviderUnreachable, "boom", transient: true));

        var clock = new FixedClock(Start);
        var coordinator = new RefreshCoordinator(
            new Dictionary<ProviderKind, IUsageProvider> { [ProviderKind.Claude] = provider }, time: clock);

        coordinator.SetAccounts(new[] { Account() });
        await coordinator.RefreshAsync();

        clock.Advance(TimeSpan.FromMinutes(10));
        var deck = await coordinator.RefreshAsync(force: true);

        Check.Close(70, deck.Accounts[0].RemainingPercent, message: "old numbers should survive a failed refresh");
        Check.Equal(Start, Check.NotNullValue(deck.Accounts[0].ObservedAt), "the timestamp must stay honest");
        Check.Equal(AccountAvailability.ProviderUnreachable, deck.Accounts[0].Availability);
        Check.Equal("boom", deck.Accounts[0].Detail);
    }

    [Test("Transient failures open a backoff gate that a scheduled refresh respects")]
    public static async Task BackoffGateBlocksScheduledRefresh()
    {
        var provider = new ScriptedProvider()
            .Then(ProbeResult.Unavailable(AccountAvailability.ProviderUnreachable, "boom", transient: true));

        var clock = new FixedClock(Start);
        var coordinator = new RefreshCoordinator(
            new Dictionary<ProviderKind, IUsageProvider> { [ProviderKind.Claude] = provider },
            time: clock,
            backoff: new BackoffPolicy(TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), jitterFraction: 0));

        coordinator.SetAccounts(new[] { Account() });
        await coordinator.RefreshAsync();

        Check.Equal(1, provider.Calls);
        Check.Equal(1, coordinator.ConsecutiveFailuresFor("a"));

        await coordinator.RefreshAsync();
        Check.Equal(1, provider.Calls, "a scheduled refresh must respect the gate");

        clock.Advance(TimeSpan.FromMinutes(6));
        await coordinator.RefreshAsync();
        Check.Equal(2, provider.Calls, "the gate should reopen once the delay has elapsed");
    }

    [Test("Pressing Refresh always gets an attempt, gate or no gate")]
    public static async Task ForceIgnoresTheGate()
    {
        var provider = new ScriptedProvider()
            .Then(ProbeResult.Unavailable(AccountAvailability.ProviderUnreachable, "boom", transient: true));

        var coordinator = new RefreshCoordinator(
            new Dictionary<ProviderKind, IUsageProvider> { [ProviderKind.Claude] = provider },
            time: new FixedClock(Start));

        coordinator.SetAccounts(new[] { Account() });
        await coordinator.RefreshAsync();
        await coordinator.RefreshAsync(force: true);

        Check.Equal(2, provider.Calls);
    }

    [Test("A signed-out account is not retried on a backoff curve - nothing will change until you sign in")]
    public static async Task NonTransientFailureDoesNotBackOff()
    {
        var provider = new ScriptedProvider()
            .Then(ProbeResult.Unavailable(AccountAvailability.SignedOut, "sign in"))
            .Then(ProbeResult.Unavailable(AccountAvailability.SignedOut, "sign in"));

        var coordinator = new RefreshCoordinator(
            new Dictionary<ProviderKind, IUsageProvider> { [ProviderKind.Claude] = provider },
            time: new FixedClock(Start));

        coordinator.SetAccounts(new[] { Account() });
        await coordinator.RefreshAsync();
        await coordinator.RefreshAsync();

        Check.Equal(2, provider.Calls);
        Check.Equal(0, coordinator.ConsecutiveFailuresFor("a"));
        Check.Null(coordinator.NextAttemptFor("a"));
    }

    [Test("A provider that throws is contained, not fatal")]
    public static async Task ThrowingProviderIsContained()
    {
        var coordinator = new RefreshCoordinator(
            new Dictionary<ProviderKind, IUsageProvider> { [ProviderKind.Claude] = new ThrowingProvider() },
            time: new FixedClock(Start));

        coordinator.SetAccounts(new[] { Account() });
        var deck = await coordinator.RefreshAsync();

        Check.Equal(AccountAvailability.ProviderUnreachable, deck.Accounts[0].Availability);
    }

    [Test]
    public static async Task HistoryIsRecordedOnSuccess()
    {
        using var workspace = new TempWorkspace();
        var history = new UsageHistoryStore(workspace.Paths);
        var provider = new ScriptedProvider().Then(ProbeResult.Success(Windows(30)));

        var coordinator = new RefreshCoordinator(
            new Dictionary<ProviderKind, IUsageProvider> { [ProviderKind.Claude] = provider },
            history,
            new FixedClock(Start));

        coordinator.SetAccounts(new[] { Account() });
        await coordinator.RefreshAsync();

        Check.Count(1, history.Read(Start.AddHours(-1), Start.AddHours(1)));
    }

    [Test("Editing the roster keeps results for the accounts that survived")]
    public static async Task SetAccountsPreservesSurvivors()
    {
        var provider = new ScriptedProvider()
            .Then(ProbeResult.Success(Windows(30)))
            .Then(ProbeResult.Success(Windows(50)));

        var coordinator = new RefreshCoordinator(
            new Dictionary<ProviderKind, IUsageProvider> { [ProviderKind.Claude] = provider },
            time: new FixedClock(Start));

        coordinator.SetAccounts(new[] { Account("a"), Account("b") });
        await coordinator.RefreshAsync();

        coordinator.SetAccounts(new[] { Account("a") });

        Check.Count(1, coordinator.Current.Accounts);
        Check.True(coordinator.Current.Accounts[0].HasData, "surviving accounts should keep their data");
    }

    [Test]
    public static async Task DisabledAccountsAreNotProbed()
    {
        var provider = new ScriptedProvider().Then(ProbeResult.Success(Windows(30)));
        var coordinator = new RefreshCoordinator(
            new Dictionary<ProviderKind, IUsageProvider> { [ProviderKind.Claude] = provider },
            time: new FixedClock(Start));

        coordinator.SetAccounts(new[] { Account() with { Enabled = false } });
        var deck = await coordinator.RefreshAsync();

        Check.Equal(0, provider.Calls);
        Check.Count(0, deck.Accounts);
    }

    private sealed class ThrowingProvider : IUsageProvider
    {
        public ProviderKind Kind => ProviderKind.Claude;

        public Task<ProbeResult> ProbeAsync(AccountDefinition account, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("provider exploded");
    }
}

public static class DiscoveryTests
{
    [Test]
    public static void SafeNameProducesUsableFolderNames()
    {
        Check.Equal("side-project", ProfileDiscovery.SafeName("Side Project"));
        Check.Equal("work", ProfileDiscovery.SafeName("  Work!  "));
        Check.Equal("a-b", ProfileDiscovery.SafeName("a // b"));
        Check.Equal("profile", ProfileDiscovery.SafeName("***"));
        Check.Equal("profile", ProfileDiscovery.SafeName(""));
    }

    [Test]
    public static void DiscoversDefaultProfileHomes()
    {
        using var workspace = new TempWorkspace();
        Directory.CreateDirectory(Path.Combine(workspace.Root, ".claude"));
        Directory.CreateDirectory(Path.Combine(workspace.Root, ".codex"));
        Directory.CreateDirectory(Path.Combine(workspace.Root, ".claude-profiles", "side-project"));

        var found = ProfileDiscovery.Discover(workspace.Root);

        Check.Count(3, found);
        Check.Equal(2, found.Count(a => a.Provider == ProviderKind.Claude));
        Check.True(found.Any(a => a.DisplayName == "Side Project"));
    }

    [Test]
    public static void DiscoveryOnAnEmptyHomeFindsNothing()
    {
        using var workspace = new TempWorkspace();
        Check.Count(0, ProfileDiscovery.Discover(workspace.Root));
    }
}

public static class StateSnapshotTests
{
    [Test("The snapshot reports whether an identity exists, never what it is")]
    public static void NeverWritesAnIdentity()
    {
        var deck = new DeckSnapshot
        {
            GeneratedAt = Start,
            Accounts = new[]
            {
                new AccountSnapshot
                {
                    AccountId = "a",
                    DisplayName = "Work",
                    Provider = ProviderKind.Claude,
                    Identity = "dan@example.invalid",
                    Availability = AccountAvailability.Idle,
                    Detail = "Idle - renews on next use.",
                    Windows = new[]
                    {
                        new UsageWindow { Scope = "5-hour", Kind = WindowKind.Session, UsedPercent = 40 },
                    },
                },
            },
        };

        var json = StateSnapshotWriter.Render(deck);

        Check.False(json.Contains("dan@example.invalid", StringComparison.Ordinal),
            "the snapshot must never carry an account email");
        Check.Contains("\"identityKnown\": true", json);
        Check.Contains("Idle", json);
        Check.Contains("5-hour", json);
        Check.Contains("60", json, "remaining percent should be rendered");
    }

    [Test]
    public static void ReportsAnAccountWithNoDataAtAll()
    {
        var deck = new DeckSnapshot
        {
            GeneratedAt = Start,
            Accounts = new[]
            {
                new AccountSnapshot
                {
                    AccountId = "a",
                    DisplayName = "Codex",
                    Provider = ProviderKind.Codex,
                    Availability = AccountAvailability.SignedOut,
                    Detail = "No stored sign-in.",
                },
            },
        };

        var json = StateSnapshotWriter.Render(deck);

        Check.Contains("SignedOut", json);
        Check.Contains("\"identityKnown\": false", json);
    }

    private static readonly DateTimeOffset Start = Moment.At("2026-09-07T12:00:00Z");
}

public static class ExecutableResolverTests
{
    [Test("An explicit path is honoured exactly as given")]
    public static void ExplicitPathIsUsedDirectly()
    {
        using var workspace = new TempWorkspace();
        var file = Path.Combine(workspace.Root, "tool");
        File.WriteAllText(file, "#!/bin/sh\n");

        Check.Equal(Path.GetFullPath(file), ExecutableResolver.Resolve(file));
        Check.Null(ExecutableResolver.Resolve(Path.Combine(workspace.Root, "missing")));
    }

    [Test]
    public static void FindsACommandOnPath()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllText(Path.Combine(workspace.Root, "codex"), "");

        var resolved = ExecutableResolver.Resolve("codex", workspace.Root);

        Check.Equal(Path.Combine(workspace.Root, "codex"), Check.NotNull(resolved));
    }

    [Test("An npm .cmd shim is found even though the command has no extension")]
    public static void FindsWindowsStyleShim()
    {
        using var workspace = new TempWorkspace();
        File.WriteAllText(Path.Combine(workspace.Root, "codex.CMD"), "");

        var resolved = ExecutableResolver.Resolve("codex", workspace.Root, ".COM;.EXE;.BAT;.CMD");

        if (OperatingSystem.IsWindows())
        {
            Check.NotNull(resolved, "a .cmd shim must be resolvable on Windows");
        }
        else
        {
            // On a case-sensitive filesystem the extension search is Windows-only
            // behaviour, so only assert that the lookup stays exception-free here.
            Check.True(resolved is null or { Length: > 0 });
        }
    }

    [Test("npm ships a bare Unix script beside its .cmd shim; Windows must not pick the script")]
    public static void PrefersTheRunnableShimOverTheBareScript()
    {
        using var workspace = new TempWorkspace();

        // Exactly what `npm i -g` leaves behind on Windows.
        File.WriteAllText(Path.Combine(workspace.Root, "codex"), "#!/bin/sh\n");
        File.WriteAllText(Path.Combine(workspace.Root, "codex.cmd"), "@echo off\n");
        File.WriteAllText(Path.Combine(workspace.Root, "codex.ps1"), "# shim\n");

        var resolved = ExecutableResolver.Resolve("codex", workspace.Root, ".COM;.EXE;.BAT;.CMD");

        if (OperatingSystem.IsWindows())
        {
            Check.True(
                ExecutableResolver.IsBatchScript(resolved),
                $"expected the .cmd shim, got '{resolved}' - a bare extension-less script cannot be executed on Windows");
        }
        else
        {
            Check.Equal(Path.Combine(workspace.Root, "codex"), resolved);
        }
    }

    [Test]
    public static void RecognisesBatchShims()
    {
        Check.True(ExecutableResolver.IsBatchScript(@"C:\npm\codex.cmd"));
        Check.True(ExecutableResolver.IsBatchScript(@"C:\npm\codex.BAT"));
        Check.False(ExecutableResolver.IsBatchScript(@"C:\bin\claude.exe"));
        Check.False(ExecutableResolver.IsBatchScript(@"C:\npm\codex.ps1"));
        Check.False(ExecutableResolver.IsBatchScript(null));
    }

    [Test]
    public static void MissingCommandReturnsNull() =>
        Check.Null(ExecutableResolver.Resolve("definitely-not-a-real-command-xyz", "/nonexistent-dir"));
}
