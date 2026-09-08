using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Headroom.Core.Accounts;
using Headroom.Core.Alerts;
using Headroom.Core.Model;
using Headroom.Core.Providers;
using Headroom.Core.Providers.Claude;
using Headroom.Core.Providers.Codex;
using Headroom.Core.Refresh;
using Headroom.Core.Storage;

namespace Headroom.App.Services;

/// <summary>
/// The composition root: builds the object graph, owns the refresh loop, and is
/// the one place that knows how the pieces fit together.
/// </summary>
public sealed class AppHost : IDisposable
{
    private readonly HttpClient _http;
    private readonly SettingsStore _settingsStore;
    private readonly CancellationTokenSource _shutdown = new();

    private readonly StateSnapshotWriter _state;
    private RefreshCoordinator _coordinator;
    private HeadroomSettings _settings;
    private string? _claudeCliVersion;

    public AppHost()
    {
        Paths = new HeadroomPaths();
        Paths.EnsureCreated();

        _settingsStore = new SettingsStore(Paths);
        History = new UsageHistoryStore(Paths);
        _state = new StateSnapshotWriter(Paths);
        _settings = _settingsStore.Load();

        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _coordinator = BuildCoordinator();

        // A first run with nothing configured should still show real numbers, so
        // the profiles already on the machine are adopted rather than demanded.
        if (_settings.Accounts.Count == 0)
        {
            var discovered = ProfileDiscovery.Discover();
            if (discovered.Count > 0)
            {
                _settings = _settings with { Accounts = discovered.ToList() };
                _ = _settingsStore.SaveAsync(_settings, _shutdown.Token);
            }
        }

        _coordinator.SetAccounts(_settings.Accounts);
        _ = ResolveClaudeVersionAsync();
        History.Prune(_settings.HistoryRetentionDays, DateTimeOffset.UtcNow);
    }

    public HeadroomPaths Paths { get; }

    public UsageHistoryStore History { get; }

    public ThresholdWatcher Alerts { get; } = new();

    public HeadroomSettings Settings => _settings;

    public DeckSnapshot Deck => _coordinator.Current;

    /// <summary>Raised after every refresh, on whatever thread the refresh finished on.</summary>
    public event EventHandler<DeckSnapshot>? DeckUpdated
    {
        add => _coordinator.DeckUpdated += value;
        remove => _coordinator.DeckUpdated -= value;
    }

    public async Task<DeckSnapshot> RefreshAsync(bool force)
    {
        var deck = await _coordinator.RefreshAsync(force, _shutdown.Token).ConfigureAwait(false);
        _state.Write(deck);
        return deck;
    }

    /// <summary>Applies edited settings, rebuilding only what actually changed.</summary>
    public async Task ApplySettingsAsync(HeadroomSettings settings, EventHandler<DeckSnapshot>? deckHandler)
    {
        var previous = _settings;
        _settings = settings.Normalized();
        await _settingsStore.SaveAsync(_settings, _shutdown.Token).ConfigureAwait(false);

        // The Codex command feeds a provider that is built once, so a change to
        // it means a new coordinator; anything else reuses the running one and
        // keeps its cached results.
        if (!string.Equals(previous.CodexCommand, _settings.CodexCommand, StringComparison.OrdinalIgnoreCase))
        {
            if (deckHandler is not null) _coordinator.DeckUpdated -= deckHandler;
            var outgoing = _coordinator;
            _coordinator = BuildCoordinator();
            if (deckHandler is not null) _coordinator.DeckUpdated += deckHandler;
            outgoing.Dispose();
        }

        _coordinator.SetAccounts(_settings.Accounts);

        if (previous.HistoryRetentionDays != _settings.HistoryRetentionDays)
            History.Prune(_settings.HistoryRetentionDays, DateTimeOffset.UtcNow);
    }

    public IReadOnlyList<AccountDefinition> Accounts => _settings.Accounts;

    private RefreshCoordinator BuildCoordinator()
    {
        var providers = new Dictionary<ProviderKind, IUsageProvider>
        {
            [ProviderKind.Claude] = new ClaudeUsageProvider(
                _http,
                new ClaudeCredentialReader(),
                () => _claudeCliVersion ?? ClaudeUsageProvider.FallbackCliVersion),

            [ProviderKind.Codex] = new CodexUsageProvider(
                new CodexAppServerClient(_settings.CodexCommand, TimeSpan.FromSeconds(25))),
        };

        return new RefreshCoordinator(providers, History);
    }

    /// <summary>
    /// Asks the installed CLI for its version so requests can identify honestly.
    /// A generic user agent gets a stricter rate-limit bucket on the usage
    /// endpoint, and a wrong-but-plausible version is better than none.
    /// </summary>
    private async Task ResolveClaudeVersionAsync()
    {
        try
        {
            var executable = ExecutableResolver.Resolve(_settings.ClaudeCommand);
            if (executable is null) return;

            var isBatch = ExecutableResolver.IsBatchScript(executable);
            var startInfo = new ProcessStartInfo(isBatch ? "cmd.exe" : executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };

            if (isBatch)
            {
                startInfo.ArgumentList.Add("/c");
                startInfo.ArgumentList.Add(executable);
            }

            startInfo.ArgumentList.Add("--version");

            using var process = Process.Start(startInfo);
            if (process is null) return;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(8));

            var output = await process.StandardOutput.ReadToEndAsync(timeout.Token).ConfigureAwait(false);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);

            var match = System.Text.RegularExpressions.Regex.Match(output, @"\d+\.\d+\.\d+");
            if (match.Success) _claudeCliVersion = match.Value;
        }
        catch (Exception e) when (
            e is OperationCanceledException or System.ComponentModel.Win32Exception
              or InvalidOperationException or System.IO.IOException)
        {
            // The fallback version is used; this is a nicety, not a requirement.
        }
    }

    public void Dispose()
    {
        _shutdown.Cancel();
        _coordinator.Dispose();
        History.Dispose();
        _settingsStore.Dispose();
        _http.Dispose();
        _shutdown.Dispose();
    }
}
