using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Headroom.Core.Accounts;
using Headroom.Core.Model;

namespace Headroom.Core.Providers.Claude;

/// <summary>
/// Reads a Claude account's rate-limit windows from Anthropic's OAuth usage
/// endpoint, using only the credential the CLI already stored.
/// </summary>
public sealed class ClaudeUsageProvider : IUsageProvider
{
    /// <summary>
    /// A generic user agent lands this endpoint in a stricter rate-limit bucket,
    /// so requests identify as the CLI whose credential they are carrying.
    /// </summary>
    public const string FallbackCliVersion = "2.1.83";

    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private const string OAuthBetaHeader = "oauth-2025-04-20";

    private readonly HttpClient _http;
    private readonly ClaudeCredentialReader _credentials;
    private readonly Func<string> _cliVersion;

    public ClaudeUsageProvider(
        HttpClient http,
        ClaudeCredentialReader? credentials = null,
        Func<string>? cliVersion = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credentials = credentials ?? new ClaudeCredentialReader();
        _cliVersion = cliVersion ?? (() => FallbackCliVersion);
    }

    public ProviderKind Kind => ProviderKind.Claude;

    public async Task<ProbeResult> ProbeAsync(
        AccountDefinition account, CancellationToken cancellationToken = default)
    {
        var credential = await _credentials
            .ReadAsync(account.ProfileDirectory, cancellationToken)
            .ConfigureAwait(false);

        if (!credential.IsUsable)
        {
            return ProbeResult.Unavailable(
                credential.ToAvailability(), credential.Explain(), credential.Identity);
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", OAuthBetaHeader);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("User-Agent", $"claude-code/{_cliVersion()}");

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
            return ProbeResult.Unavailable(
                AccountAvailability.ProviderUnreachable,
                "Could not reach the provider.",
                credential.Identity,
                transient: true);
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return Interpret(response.StatusCode, response.Headers.RetryAfter?.Delta, body, credential.Identity);
        }
    }

    /// <summary>
    /// Turns an HTTP outcome into a probe result. Pure, so every branch below is
    /// covered by tests without a network.
    /// </summary>
    internal static ProbeResult Interpret(
        HttpStatusCode status, TimeSpan? retryAfter, string body, string? identity)
    {
        if (status == HttpStatusCode.TooManyRequests)
        {
            return ProbeResult.Unavailable(
                AccountAvailability.ProviderUnreachable,
                "Rate limited by the provider. Backing off.",
                identity,
                transient: true,
                retryAfter: retryAfter);
        }

        if (status == HttpStatusCode.Unauthorized)
        {
            // Only a structured authentication_error proves the token is dead.
            // An unstructured 401 is treated as transient, because guessing
            // "signed out" here sends people to re-run a login they do not need.
            return IsAuthenticationError(body)
                ? ProbeResult.Unavailable(
                    AccountAvailability.SignedOut,
                    "The provider rejected this sign-in. Sign in again.",
                    identity)
                : ProbeResult.Unavailable(
                    AccountAvailability.ProviderUnreachable,
                    "The provider returned 401 without an error type.",
                    identity,
                    transient: true);
        }

        if ((int)status is < 200 or > 299)
        {
            return ProbeResult.Unavailable(
                AccountAvailability.ProviderUnreachable,
                $"The provider returned HTTP {(int)status}.",
                identity,
                transient: true,
                retryAfter: retryAfter);
        }

        var windows = ClaudeUsageParser.Parse(body);
        if (windows.Count == 0)
        {
            return ProbeResult.Unavailable(
                AccountAvailability.ProviderUnreachable,
                "The provider's reply contained no usage windows.",
                identity,
                transient: true);
        }

        return ProbeResult.Success(windows, identity, ReadPlanTier(body));
    }

    private static bool IsAuthenticationError(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return false;
        try
        {
            using var document = JsonDocument.Parse(body);
            return JsonReadHelpers.TryGetObject(document.RootElement, "error", out var error) &&
                   JsonReadHelpers.StringAny(error, "type") == "authentication_error";
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? ReadPlanTier(string body)
    {
        if (string.IsNullOrWhiteSpace(body)) return null;
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (JsonReadHelpers.StringAny(root, "plan", "planType", "plan_type") is { } direct) return direct;
            if (JsonReadHelpers.TryGetObject(root, "plan", out var plan))
                return JsonReadHelpers.StringAny(plan, "name", "display_name", "displayName", "tier", "type");

            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
