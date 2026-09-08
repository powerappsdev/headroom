using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Headroom.Core.Accounts;
using Headroom.Core.Model;
using Headroom.Core.Providers;

namespace Headroom.Core.Providers.Codex;

/// <summary>
/// Reads a Codex account's rate-limit windows through the CLI's app-server protocol.
/// </summary>
public sealed class CodexUsageProvider : IUsageProvider
{
    private readonly ICodexAppServer _appServer;

    public CodexUsageProvider(ICodexAppServer? appServer = null) =>
        _appServer = appServer ?? new CodexAppServerClient();

    public ProviderKind Kind => ProviderKind.Codex;

    public async Task<ProbeResult> ProbeAsync(
        AccountDefinition account, CancellationToken cancellationToken = default)
    {
        var auth = await CodexAuthReader.ReadAsync(account.ProfileDirectory, cancellationToken).ConfigureAwait(false);

        if (!auth.FilePresent)
        {
            var missingProfile = string.IsNullOrWhiteSpace(account.ProfileDirectory) ||
                                 !System.IO.Directory.Exists(account.ProfileDirectory);
            return ProbeResult.Unavailable(
                missingProfile ? AccountAvailability.NotConfigured : AccountAvailability.SignedOut,
                missingProfile
                    ? "This profile folder does not exist yet."
                    : "No stored sign-in. Run codex login for this profile.");
        }

        var result = await _appServer
            .ReadRateLimitsAsync(account.ProfileDirectory, cancellationToken)
            .ConfigureAwait(false);

        return Interpret(result, auth);
    }

    /// <summary>Pure interpretation step so every branch is testable without a CLI.</summary>
    internal static ProbeResult Interpret(CodexAppServerResult result, CodexAuthInfo auth)
    {
        switch (result.Status)
        {
            case CodexProbeStatus.BinaryMissing:
                return ProbeResult.Unavailable(
                    AccountAvailability.NotConfigured, result.Detail, auth.Identity);

            case CodexProbeStatus.NotSignedIn:
                return ProbeResult.Unavailable(
                    AccountAvailability.SignedOut, result.Detail, auth.Identity);

            case CodexProbeStatus.Timeout:
            case CodexProbeStatus.Failed:
                return ProbeResult.Unavailable(
                    AccountAvailability.ProviderUnreachable, result.Detail, auth.Identity, transient: true);
        }

        if (result.Json is null)
        {
            return ProbeResult.Unavailable(
                AccountAvailability.ProviderUnreachable,
                "The Codex CLI returned an empty reply.",
                auth.Identity,
                transient: true);
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(result.Json);
        }
        catch (JsonException)
        {
            return ProbeResult.Unavailable(
                AccountAvailability.ProviderUnreachable,
                "The Codex CLI returned a reply Headroom could not read.",
                auth.Identity,
                transient: true);
        }

        using (document)
        {
            var windows = CodexRateLimitParser.Parse(document.RootElement);
            if (windows.Count == 0)
            {
                return ProbeResult.Unavailable(
                    AccountAvailability.ProviderUnreachable,
                    "The Codex CLI reported no usage windows.",
                    auth.Identity,
                    transient: true);
            }

            var plan = auth.PlanTier ?? CodexRateLimitParser.ReadPlanTier(document.RootElement);
            return ProbeResult.Success(windows, auth.Identity, plan);
        }
    }
}
