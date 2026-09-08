namespace Headroom.Core.Model;

/// <summary>Which AI coding provider an account belongs to.</summary>
public enum ProviderKind
{
    Claude,
    Codex,
}

/// <summary>
/// The shape of a rate-limit window, independent of what the provider calls it.
/// Used for ordering and for deciding which windows may headline a card.
/// </summary>
public enum WindowKind
{
    /// <summary>The short rolling window (Claude's 5-hour, Codex's primary 300-minute).</summary>
    Session,

    /// <summary>The all-models weekly cap.</summary>
    Weekly,

    /// <summary>A weekly cap scoped to one model, e.g. "Opus weekly".</summary>
    WeeklyScoped,

    /// <summary>A spend or credit budget. Never headlines a card: it is not a rate limit.</summary>
    Spend,

    /// <summary>Anything the provider reports that does not match a known shape.</summary>
    Other,
}

/// <summary>
/// Why an account's data is what it is. Every value here is something Headroom
/// can prove from an observation; there is deliberately no "probably fine".
/// </summary>
public enum AccountAvailability
{
    /// <summary>Live data was read from the provider.</summary>
    Ok,

    /// <summary>
    /// A stored sign-in exists but its token has decayed from disuse. The CLI
    /// renews it the next time the account is used, so this is not a sign-out
    /// and must never be presented as one.
    /// </summary>
    Idle,

    /// <summary>No usable credential. The user must sign in through the provider's own flow.</summary>
    SignedOut,

    /// <summary>A credential exists but the OS or provider refused to hand it over.</summary>
    CredentialBlocked,

    /// <summary>Credential fine; the provider did not answer, or answered with something unusable.</summary>
    ProviderUnreachable,

    /// <summary>The account is configured but its profile directory does not exist yet.</summary>
    NotConfigured,
}

/// <summary>Severity band for a remaining-percentage value.</summary>
public enum AlertBand
{
    Healthy,
    Warning,
    Critical,
}

/// <summary>
/// A money amount stated by a provider payload, kept in minor units so no
/// floating-point rounding can move a dollar figure. Currency is never assumed:
/// if the payload did not state one, no amount is produced at all.
/// </summary>
public sealed record SpendAmount(long UsedMinor, long LimitMinor, string Currency, int Exponent)
{
    public decimal Used => Scale(UsedMinor);

    public decimal Limit => Scale(LimitMinor);

    private decimal Scale(long minor)
    {
        decimal divisor = 1m;
        for (var i = 0; i < Exponent; i++) divisor *= 10m;
        return minor / divisor;
    }
}
