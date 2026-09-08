using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Headroom.Core.Accounts;
using Headroom.Core.Model;
using Headroom.Core.Providers;
using Headroom.Core.Storage;

namespace Headroom.Core.Refresh;

/// <summary>
/// Owns the account roster and turns provider probes into a deck snapshot.
/// </summary>
/// <remarks>
/// The rule that shapes this class: a failed probe never destroys good data.
/// When a refresh fails, the last known windows stay on the card and keep their
/// original timestamp, and only the availability changes. That way the deck
/// shows "here is what I last knew, and here is why it is old" instead of
/// blanking out — which is both less useful and less honest.
/// </remarks>
public sealed class RefreshCoordinator
{
    private readonly IReadOnlyDictionary<ProviderKind, IUsageProvider> _providers;
    private readonly UsageHistoryStore? _history;
    private readonly TimeProvider _time;
    private readonly BackoffPolicy _backoff;
    private readonly ConcurrentDictionary<string, AccountState> _state = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    private IReadOnlyList<AccountDefinition> _accounts = Array.Empty<AccountDefinition>();

    public RefreshCoordinator(
        IReadOnlyDictionary<ProviderKind, IUsageProvider> providers,
        UsageHistoryStore? history = null,
        TimeProvider? time = null,
        BackoffPolicy? backoff = null)
    {
        _providers = providers;
        _history = history;
        _time = time ?? TimeProvider.System;
        _backoff = backoff ?? new BackoffPolicy();
    }

    /// <summary>Maximum probes in flight. Small on purpose: these spawn CLI processes.</summary>
    public int MaxConcurrency { get; init; } = 4;

    public DeckSnapshot Current { get; private set; } = DeckSnapshot.Empty;

    public event EventHandler<DeckSnapshot>? DeckUpdated;

    /// <summary>
    /// Replaces the roster, keeping cached results for accounts that survived so
    /// editing one account's name does not blank every other card.
    /// </summary>
    public void SetAccounts(IEnumerable<AccountDefinition> accounts)
    {
        _accounts = accounts.ToList();
        var live = new HashSet<string>(_accounts.Select(a => a.Id), StringComparer.OrdinalIgnoreCase);

        foreach (var key in _state.Keys.Where(k => !live.Contains(k)).ToList())
            _state.TryRemove(key, out _);

        Current = BuildSnapshot();
        DeckUpdated?.Invoke(this, Current);
    }

    public IReadOnlyList<AccountDefinition> Accounts => _accounts;

    /// <summary>When the given account is next allowed to be probed.</summary>
    public DateTimeOffset? NextAttemptFor(string accountId) =>
        _state.TryGetValue(accountId, out var state) ? state.NextAttemptAt : null;

    public int ConsecutiveFailuresFor(string accountId) =>
        _state.TryGetValue(accountId, out var state) ? state.ConsecutiveFailures : 0;

    /// <summary>
    /// Probes every enabled account whose backoff gate has opened.
    /// <paramref name="force"/> ignores the gate, which is what the manual
    /// Refresh button does: an explicit human request always gets an attempt.
    /// </summary>
    public async Task<DeckSnapshot> RefreshAsync(bool force = false, CancellationToken cancellationToken = default)
    {
        await _refreshLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var now = _time.GetUtcNow();
            var due = _accounts
                .Where(a => a.Enabled)
                .Where(a => force || IsDue(a.Id, now))
                .ToList();

            if (due.Count > 0)
            {
                using var throttle = new SemaphoreSlim(Math.Max(1, MaxConcurrency));
                await Task.WhenAll(due.Select(account => ProbeOneAsync(account, throttle, cancellationToken)))
                    .ConfigureAwait(false);
            }

            Current = BuildSnapshot();
        }
        finally
        {
            _refreshLock.Release();
        }

        DeckUpdated?.Invoke(this, Current);
        return Current;
    }

    private bool IsDue(string accountId, DateTimeOffset now) =>
        !_state.TryGetValue(accountId, out var state) || state.NextAttemptAt is null || state.NextAttemptAt <= now;

    private async Task ProbeOneAsync(
        AccountDefinition account, SemaphoreSlim throttle, CancellationToken cancellationToken)
    {
        await throttle.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_providers.TryGetValue(account.Provider, out var provider))
            {
                Record(account, ProbeResult.Unavailable(
                    AccountAvailability.NotConfigured, "No provider is registered for this account."));
                return;
            }

            ProbeResult result;
            try
            {
                result = await provider.ProbeAsync(account, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception)
            {
                // A provider that throws is a bug in the provider, not a reason to
                // take the app down. Treat it as a transient failure and move on.
                result = ProbeResult.Unavailable(
                    AccountAvailability.ProviderUnreachable,
                    "The provider probe failed unexpectedly.",
                    transient: true);
            }

            Record(account, result);

            if (result.Succeeded && _history is not null)
            {
                await _history
                    .AppendAsync(account.Id, result.Windows, _time.GetUtcNow(), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        finally
        {
            throttle.Release();
        }
    }

    private void Record(AccountDefinition account, ProbeResult result)
    {
        var now = _time.GetUtcNow();
        var previous = _state.TryGetValue(account.Id, out var existing) ? existing : new AccountState();

        if (result.Succeeded)
        {
            _state[account.Id] = new AccountState
            {
                Windows = result.Windows,
                ObservedAt = now,
                Availability = AccountAvailability.Ok,
                Detail = null,
                Identity = result.Identity ?? previous.Identity,
                PlanTier = result.PlanTier ?? previous.PlanTier,
                ConsecutiveFailures = 0,
                NextAttemptAt = null,
            };
            return;
        }

        var failures = result.IsTransientFailure ? previous.ConsecutiveFailures + 1 : 0;
        var nextAttempt = result.IsTransientFailure
            ? now + _backoff.DelayFor(failures, result.RetryAfter)
            : (DateTimeOffset?)null;

        _state[account.Id] = new AccountState
        {
            // Keep the last good numbers rather than blanking the card.
            Windows = previous.Windows,
            ObservedAt = previous.ObservedAt,
            Availability = result.Availability,
            Detail = result.Detail,
            Identity = result.Identity ?? previous.Identity,
            PlanTier = previous.PlanTier,
            ConsecutiveFailures = failures,
            NextAttemptAt = nextAttempt,
        };
    }

    private DeckSnapshot BuildSnapshot()
    {
        var snapshots = _accounts
            .Where(a => a.Enabled)
            .Select(account =>
            {
                var state = _state.TryGetValue(account.Id, out var s) ? s : new AccountState();
                return new AccountSnapshot
                {
                    AccountId = account.Id,
                    DisplayName = account.DisplayName,
                    Provider = account.Provider,
                    Identity = state.Identity,
                    PlanTier = state.PlanTier,
                    Windows = state.Windows,
                    ObservedAt = state.ObservedAt,
                    Availability = state.Availability,
                    Detail = state.Detail,
                };
            })
            .ToList();

        return new DeckSnapshot { Accounts = snapshots, GeneratedAt = _time.GetUtcNow() };
    }

    private sealed record AccountState
    {
        public IReadOnlyList<UsageWindow> Windows { get; init; } = Array.Empty<UsageWindow>();

        public DateTimeOffset? ObservedAt { get; init; }

        public AccountAvailability Availability { get; init; } = AccountAvailability.Ok;

        public string? Detail { get; init; }

        public string? Identity { get; init; }

        public string? PlanTier { get; init; }

        public int ConsecutiveFailures { get; init; }

        public DateTimeOffset? NextAttemptAt { get; init; }
    }
}
