using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Headroom.Core.Model;

namespace Headroom.Core.Providers.Claude;

/// <summary>What a credential file turned out to hold.</summary>
public enum CredentialStatus
{
    Ok,
    ProfileMissing,
    FileMissing,
    Unreadable,
    NoToken,
    Expired,

    /// <summary>
    /// The CLI has blanked its own tokens because the refresh token's lifetime
    /// ran out. Nothing short of a fresh sign-in brings this back, so it must
    /// not be presented as something that renews itself.
    /// </summary>
    SessionExpired,
}

/// <summary>
/// The outcome of reading one profile's credential file. The token is carried
/// only long enough to make one request; it is never logged, cached to disk, or
/// written into history.
/// </summary>
public sealed record ClaudeCredential(
    CredentialStatus Status,
    string? AccessToken,
    DateTimeOffset? ExpiresAt,
    string? Identity,
    DateTimeOffset? RefreshTokenExpiresAt = null)
{
    public bool IsUsable => Status == CredentialStatus.Ok && !string.IsNullOrEmpty(AccessToken);

    public AccountAvailability ToAvailability() => Status switch
    {
        CredentialStatus.ProfileMissing => AccountAvailability.NotConfigured,
        CredentialStatus.Expired => AccountAvailability.Idle,
        CredentialStatus.Unreadable => AccountAvailability.CredentialBlocked,
        CredentialStatus.Ok => AccountAvailability.Ok,
        _ => AccountAvailability.SignedOut,
    };

    public string? Explain() => Status switch
    {
        CredentialStatus.ProfileMissing => "This profile folder does not exist yet.",
        CredentialStatus.FileMissing => "No stored sign-in. Run claude /login for this profile.",
        CredentialStatus.NoToken =>
            "The stored sign-in has no access token. Run claude once in a terminal to renew it.",

        CredentialStatus.SessionExpired => RefreshTokenExpiresAt is { } expiry
            ? $"The CLI sign-in expired on {expiry.ToLocalTime():ddd d MMM, h:mm tt}. "
              + "Run claude and use /login to sign in again."
            : "The CLI sign-in has expired. Run claude and use /login to sign in again.",
        // Naming the action matters: an account that is only ever used through
        // the desktop app can sit with an expired CLI token indefinitely, and
        // "renews on next use" alone leaves someone waiting for something that
        // will not happen on its own.
        CredentialStatus.Expired =>
            "Idle - the stored CLI sign-in has expired. Run claude once in a terminal to renew it, "
            + "then press Refresh. Headroom never renews a token itself.",
        CredentialStatus.Unreadable => "The stored sign-in could not be read.",
        _ => null,
    };
}

/// <summary>
/// Reads Claude Code's stored OAuth credential from a profile directory.
/// </summary>
/// <remarks>
/// On Windows and Linux, Claude Code keeps this in a plain
/// <c>.credentials.json</c> inside the profile home, which is why Headroom needs
/// no platform keychain interop at all. Headroom only ever reads this file: it
/// never writes it, never refreshes the token, and never initiates a login.
/// Renewal belongs to the CLI that owns the credential.
/// </remarks>
public sealed class ClaudeCredentialReader
{
    private readonly TimeProvider _time;

    public ClaudeCredentialReader(TimeProvider? time = null) => _time = time ?? TimeProvider.System;

    public async Task<ClaudeCredential> ReadAsync(string profileDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory) || !Directory.Exists(profileDirectory))
            return new ClaudeCredential(CredentialStatus.ProfileMissing, null, null, null);

        var path = Path.Combine(profileDirectory, ".credentials.json");
        if (!File.Exists(path))
            return new ClaudeCredential(CredentialStatus.FileMissing, null, null, null);

        string text;
        try
        {
            text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new ClaudeCredential(CredentialStatus.Unreadable, null, null, null);
        }

        return ParseCredentialJson(text, _time.GetUtcNow());
    }

    /// <summary>Pure parse step, separated so it can be tested without a filesystem.</summary>
    public static ClaudeCredential ParseCredentialJson(string json, DateTimeOffset now)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            return new ClaudeCredential(CredentialStatus.Unreadable, null, null, null);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return new ClaudeCredential(CredentialStatus.Unreadable, null, null, null);

            var oauth = root;
            foreach (var wrapper in new[] { "claudeAiOauth", "oauth" })
            {
                if (JsonReadHelpers.TryGetObject(root, wrapper, out var inner))
                {
                    oauth = inner;
                    break;
                }
            }

            var identity = ReadIdentity(root);
            var refreshExpiry = ReadTimestamp(oauth, "refreshTokenExpiresAt", "refresh_token_expires_at");

            // The CLI blanks accessToken and refreshToken to empty strings (and
            // zeroes expiresAt) once the refresh token's own lifetime runs out.
            // The file is still perfectly valid JSON, so "no token" is a real
            // state to report rather than a parse failure - and when the refresh
            // window is demonstrably over, this is a genuine sign-out that no
            // amount of waiting will fix.
            var token = JsonReadHelpers.StringAny(oauth, "accessToken", "access_token");
            if (string.IsNullOrEmpty(token))
            {
                var status = refreshExpiry is { } refreshedUntil && refreshedUntil <= now
                    ? CredentialStatus.SessionExpired
                    : CredentialStatus.NoToken;

                return new ClaudeCredential(status, null, null, identity, refreshExpiry);
            }

            var expiresAt = ReadExpiry(oauth);
            if (expiresAt is { } expiry && expiry <= now)
                return new ClaudeCredential(CredentialStatus.Expired, null, expiry, identity, refreshExpiry);

            return new ClaudeCredential(CredentialStatus.Ok, token, expiresAt, identity, refreshExpiry);
        }
    }

    private static DateTimeOffset? ReadExpiry(JsonElement oauth) =>
        ReadTimestamp(oauth, "expiresAt", "expires_at");

    private static DateTimeOffset? ReadTimestamp(JsonElement oauth, params string[] names)
    {
        // A zeroed timestamp means "cleared", not 1970.
        if (JsonReadHelpers.NumberAny(oauth, names) is not { } raw || raw <= 0) return null;
        try
        {
            return raw < 10_000_000_000d
                ? DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(raw * 1000d))
                : DateTimeOffset.FromUnixTimeMilliseconds((long)Math.Round(raw));
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static string? ReadIdentity(JsonElement root)
    {
        if (JsonReadHelpers.TryGetObject(root, "account", out var account))
        {
            var email = JsonReadHelpers.StringAny(account, "email", "emailAddress", "email_address");
            if (email is not null) return email;
        }

        return JsonReadHelpers.StringAny(root, "email");
    }
}
