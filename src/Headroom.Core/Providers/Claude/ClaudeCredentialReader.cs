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
    string? Identity)
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
        CredentialStatus.NoToken => "The stored sign-in has no access token. Sign in again.",
        CredentialStatus.Expired => "Idle - the CLI renews this the next time you use the account.",
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
            var token = JsonReadHelpers.StringAny(oauth, "accessToken", "access_token");
            if (string.IsNullOrEmpty(token))
                return new ClaudeCredential(CredentialStatus.NoToken, null, null, identity);

            var expiresAt = ReadExpiry(oauth);
            if (expiresAt is { } expiry && expiry <= now)
                return new ClaudeCredential(CredentialStatus.Expired, null, expiry, identity);

            return new ClaudeCredential(CredentialStatus.Ok, token, expiresAt, identity);
        }
    }

    private static DateTimeOffset? ReadExpiry(JsonElement oauth)
    {
        if (JsonReadHelpers.NumberAny(oauth, "expiresAt", "expires_at") is not { } raw || raw <= 0) return null;
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
