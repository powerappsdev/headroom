using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Headroom.Core.Providers;

namespace Headroom.Core.Providers.Codex;

/// <summary>Display-only account metadata read from a Codex profile's auth.json.</summary>
public sealed record CodexAuthInfo(bool FilePresent, string? Identity, string? PlanTier)
{
    public static readonly CodexAuthInfo Absent = new(false, null, null);
}

/// <summary>
/// Reads the non-secret display fields from <c>CODEX_HOME/auth.json</c>.
/// </summary>
/// <remarks>
/// The id_token's signature is deliberately not verified: nothing here is used
/// for authentication or authorization, only to put an email and a plan name on
/// a card. No token value is ever returned, logged, or stored, and every
/// malformed input degrades to "unknown" rather than throwing, so a
/// half-written auth file during a login cannot break a refresh.
/// </remarks>
public static class CodexAuthReader
{
    public static async Task<CodexAuthInfo> ReadAsync(
        string profileDirectory, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileDirectory)) return CodexAuthInfo.Absent;

        var path = Path.Combine(profileDirectory, "auth.json");
        if (!File.Exists(path)) return CodexAuthInfo.Absent;

        try
        {
            var text = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
            return Parse(text);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return CodexAuthInfo.Absent;
        }
    }

    /// <summary>Pure parse step, separated for testing.</summary>
    public static CodexAuthInfo Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new CodexAuthInfo(true, null, null);

            if (!JsonReadHelpers.TryGetObject(root, "tokens", out var tokens))
                return new CodexAuthInfo(true, null, null);

            var idToken = JsonReadHelpers.StringAny(tokens, "id_token", "idToken");
            if (idToken is null) return new CodexAuthInfo(true, null, null);

            var claims = DecodeClaims(idToken);
            if (claims is null) return new CodexAuthInfo(true, null, null);

            using (claims)
            {
                var root2 = claims.RootElement;
                var email = JsonReadHelpers.StringAny(root2, "email", "preferred_username");
                string? plan = null;
                if (JsonReadHelpers.TryGetObject(root2, "https://api.openai.com/auth", out var auth))
                    plan = JsonReadHelpers.StringAny(auth, "chatgpt_plan_type");

                return new CodexAuthInfo(true, email, plan);
            }
        }
        catch (JsonException)
        {
            return new CodexAuthInfo(true, null, null);
        }
    }

    private static JsonDocument? DecodeClaims(string jwt)
    {
        var segments = jwt.Split('.');
        if (segments.Length != 3 || segments[1].Length == 0) return null;

        var payload = segments[1];
        foreach (var c in payload)
        {
            if (!char.IsLetterOrDigit(c) && c != '-' && c != '_') return null;
        }

        var remainder = payload.Length % 4;
        if (remainder == 1) return null;

        var standard = new StringBuilder(payload.Replace('-', '+').Replace('_', '/'));
        if (remainder != 0) standard.Append('=', 4 - remainder);

        try
        {
            var bytes = Convert.FromBase64String(standard.ToString());
            return JsonDocument.Parse(Encoding.UTF8.GetString(bytes));
        }
        catch (Exception e) when (e is FormatException or JsonException or DecoderFallbackException)
        {
            return null;
        }
    }
}
