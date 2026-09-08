using System;
using System.Linq;
using Headroom.Core.Formatting;
using Headroom.Core.Model;
using Headroom.Core.Providers.Claude;

namespace Headroom.Core.Tests;

public static class SpendTests
{
    [Test("A disabled extra-usage budget contributes no amounts, however stale numbers linger")]
    public static void DisabledBudgetIsIgnored()
    {
        var windows = ClaudeUsageParser.Parse("""
        { "five_hour": { "utilization": 5 },
          "extra_usage": { "is_enabled": false, "used_credits": 900, "monthly_limit": 5000, "currency": "USD" } }
        """);

        Check.Count(1, windows);
        Check.Null(windows[0].Spend);
    }

    [Test("Minor-unit credits with a stated currency become an amount")]
    public static void ReadsMinorUnitCredits()
    {
        var windows = ClaudeUsageParser.Parse("""
        { "five_hour": { "utilization": 5 },
          "extra_usage": { "is_enabled": true, "used_credits": 24563, "monthly_limit": 50000, "currency": "usd" } }
        """);

        var spend = Check.NotNull(windows.First(w => w.Kind == WindowKind.Spend).Spend);
        Check.Equal("USD", spend.Currency);
        Check.Equal(245.63m, spend.Used);
        Check.Equal(500.00m, spend.Limit);
        Check.Equal("$245.63 of $500.00", DisplayText.SpendText(spend));
    }

    [Test("The _usd field name states the currency by itself")]
    public static void ReadsMeteredUsdVariant()
    {
        var windows = ClaudeUsageParser.Parse("""
        { "five_hour": { "utilization": 5 },
          "extra_usage": { "spent_usd": 12.5, "limit_usd": 100 } }
        """);

        var spend = Check.NotNull(windows.First(w => w.Kind == WindowKind.Spend).Spend);
        Check.Equal("USD", spend.Currency);
        Check.Equal(12.50m, spend.Used);
        Check.Equal(100.00m, spend.Limit);
    }

    [Test("Money objects at different scales are normalized, not naively combined")]
    public static void NormalizesMismatchedExponents()
    {
        var windows = ClaudeUsageParser.Parse("""
        { "five_hour": { "utilization": 5 },
          "spend": { "used":  { "amount_minor": 1500, "currency": "USD", "exponent": 3 },
                     "limit": { "amount_minor": 50,   "currency": "USD", "exponent": 2 } } }
        """);

        var spend = Check.NotNull(windows.First(w => w.Kind == WindowKind.Spend).Spend);
        Check.Equal(3, spend.Exponent);
        Check.Equal(1.500m, spend.Used);
        Check.Equal(0.500m, spend.Limit);
    }

    [Test("No stated currency means no amount - a bare number never gets a dollar sign")]
    public static void RefusesUnstatedCurrency()
    {
        var windows = ClaudeUsageParser.Parse("""
        { "five_hour": { "utilization": 5 },
          "extra_usage": { "is_enabled": true, "used_credits": 100, "monthly_limit": 500 } }
        """);

        Check.False(windows.Any(w => w.Spend is not null), "amounts must not appear without a stated currency");
    }

    [Test("Mismatched currencies are refused rather than guessed at")]
    public static void RefusesMixedCurrencies()
    {
        var windows = ClaudeUsageParser.Parse("""
        { "five_hour": { "utilization": 5 },
          "spend": { "used":  { "amount_minor": 100, "currency": "USD" },
                     "limit": { "amount_minor": 900, "currency": "EUR" } } }
        """);

        Check.False(windows.Any(w => w.Spend is not null));
    }

    [Test("Amounts with no percentage still reach the deck as their own row")]
    public static void AmountOnlySpendStillAppears()
    {
        var windows = ClaudeUsageParser.Parse("""
        { "five_hour": { "utilization": 5 },
          "extra_usage": { "is_enabled": true, "used_credits": 100, "monthly_limit": 500, "currency": "USD" } }
        """);

        var spend = windows.First(w => w.Kind == WindowKind.Spend);
        Check.Null(spend.UsedPercent);
        Check.NotNull(spend.Spend);
    }

    [Test("Spend never headlines a card, even when it is the lowest number on it")]
    public static void SpendNeverHeadlines()
    {
        var account = new AccountSnapshot
        {
            AccountId = "a", DisplayName = "Work", Provider = ProviderKind.Claude,
            Windows = new[]
            {
                new UsageWindow { Scope = "5-hour", Kind = WindowKind.Session, UsedPercent = 20 },
                new UsageWindow { Scope = "spend", Kind = WindowKind.Spend, UsedPercent = 99 },
            },
        };

        Check.Equal("5-hour", Check.NotNull(account.WorstWindow).Scope);
        Check.Close(80, account.RemainingPercent);
    }
}

public static class CredentialTests
{
    private static readonly DateTimeOffset Now = Moment.At("2026-09-07T12:00:00Z");

    [Test]
    public static void ReadsAValidCredential()
    {
        var credential = ClaudeCredentialReader.ParseCredentialJson("""
        { "claudeAiOauth": { "accessToken": "tok-123", "expiresAt": 4102444800000 },
          "account": { "email": "dan@example.invalid" } }
        """, Now);

        Check.Equal(CredentialStatus.Ok, credential.Status);
        Check.True(credential.IsUsable);
        Check.Equal("tok-123", credential.AccessToken);
        Check.Equal("dan@example.invalid", credential.Identity);
    }

    [Test("An expired token is idle, not signed out - the CLI renews it on next use")]
    public static void ExpiredTokenIsIdle()
    {
        var credential = ClaudeCredentialReader.ParseCredentialJson("""
        { "claudeAiOauth": { "accessToken": "tok", "expiresAt": 1600000000000 } }
        """, Now);

        Check.Equal(CredentialStatus.Expired, credential.Status);
        Check.Equal(AccountAvailability.Idle, credential.ToAvailability());
        Check.Contains("renews", credential.Explain());
        Check.Null(credential.AccessToken, "an expired credential must not carry its token onward");
    }

    [Test("Expiry stated in seconds is understood as well as milliseconds")]
    public static void AcceptsSecondsExpiry()
    {
        var credential = ClaudeCredentialReader.ParseCredentialJson("""
        { "claudeAiOauth": { "accessToken": "tok", "expiresAt": 4102444800 } }
        """, Now);

        Check.Equal(CredentialStatus.Ok, credential.Status);
    }

    [Test]
    public static void MissingTokenIsSignedOut()
    {
        var credential = ClaudeCredentialReader.ParseCredentialJson("""{ "claudeAiOauth": { } }""", Now);
        Check.Equal(CredentialStatus.NoToken, credential.Status);
        Check.Equal(AccountAvailability.SignedOut, credential.ToAvailability());
    }

    [Test]
    public static void UnparseableFileIsBlockedNotSignedOut()
    {
        var credential = ClaudeCredentialReader.ParseCredentialJson("{ not json", Now);
        Check.Equal(CredentialStatus.Unreadable, credential.Status);
        Check.Equal(AccountAvailability.CredentialBlocked, credential.ToAvailability());
    }

    [Test("A credential stored without the claudeAiOauth wrapper still works")]
    public static void AcceptsUnwrappedCredential()
    {
        var credential = ClaudeCredentialReader.ParseCredentialJson("""
        { "access_token": "flat", "expires_at": 4102444800 }
        """, Now);

        Check.Equal(CredentialStatus.Ok, credential.Status);
        Check.Equal("flat", credential.AccessToken);
    }
}
