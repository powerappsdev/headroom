using System;
using System.Text.Json;
using Headroom.Core.Model;

namespace Headroom.Core.Providers.Claude;

/// <summary>
/// Extracts extra-usage / spend budgets from a Claude usage payload.
/// </summary>
/// <remarks>
/// The single rule here is that a currency is never assumed. A dollar sign in
/// front of a number the payload never labelled is a lie, and a wrong money
/// figure is worse than no money figure — so every path below returns null
/// unless the payload states the currency explicitly, via a <c>currency</c>
/// field, a <c>_usd</c> field name, or a money object's own currency.
/// </remarks>
internal static class ClaudeSpendParser
{
    public static SpendAmount? Parse(JsonElement source)
    {
        if (source.ValueKind != JsonValueKind.Object) return null;

        foreach (var name in new[] { "extra_usage", "extraUsage" })
        {
            if (JsonReadHelpers.TryGetObject(source, name, out var extra) && FromExtraUsage(extra) is { } fromExtra)
                return fromExtra;
        }

        if (JsonReadHelpers.TryGetObject(source, "spend", out var spend) && FromSpendObject(spend) is { } fromSpend)
            return fromSpend;

        return null;
    }

    private static SpendAmount? FromExtraUsage(JsonElement extra)
    {
        // A disabled budget can retain stale numbers. Not live means no amounts.
        if (extra.TryGetProperty("is_enabled", out var enabled) && enabled.ValueKind == JsonValueKind.False) return null;
        if (extra.TryGetProperty("isEnabled", out var enabled2) && enabled2.ValueKind == JsonValueKind.False) return null;

        var currency = StatedCurrency(JsonReadHelpers.StringAny(extra, "currency"));
        if (currency is not null)
        {
            var used = JsonReadHelpers.NumberAny(extra, "used_credits", "usedCredits");
            var limit = JsonReadHelpers.NumberAny(
                extra, "monthly_limit", "monthlyLimit", "monthly_credit_limit", "monthlyCreditLimit");
            if (used is not null && limit is > 0)
            {
                return new SpendAmount(
                    (long)Math.Round(used.Value), (long)Math.Round(limit.Value), currency, 2);
            }
        }

        // Metered variant: major-unit dollars, currency stated by the field name.
        var usedUsd = JsonReadHelpers.NumberAny(extra, "spent_usd", "spentUsd", "used_usd", "usedUsd");
        var limitUsd = JsonReadHelpers.NumberAny(
            extra, "limit_usd", "limitUsd", "monthly_limit_usd", "monthlyLimitUsd");
        if (usedUsd is not null && limitUsd is > 0)
        {
            return new SpendAmount(
                (long)Math.Round(usedUsd.Value * 100d), (long)Math.Round(limitUsd.Value * 100d), "USD", 2);
        }

        return null;
    }

    private static SpendAmount? FromSpendObject(JsonElement spend)
    {
        var used = Money(spend, "used");
        var limit = Money(spend, "limit") ?? Money(spend, "monthly_limit") ?? Money(spend, "monthlyLimit");
        if (used is null || limit is null || limit.Value.Minor <= 0) return null;

        var currency = used.Value.Currency ?? limit.Value.Currency;
        if (currency is null) return null;
        if (used.Value.Currency is not null && limit.Value.Currency is not null &&
            !string.Equals(used.Value.Currency, limit.Value.Currency, StringComparison.Ordinal)) return null;

        // Each money object carries its own scale. Combining a used at exponent 2
        // with a limit at exponent 3 under one exponent shows a figure wrong by
        // 10x, so both are normalized to the larger exponent by exact integer
        // scaling, and implausible exponents are refused outright.
        if (used.Value.Exponent is < 0 or > 6 || limit.Value.Exponent is < 0 or > 6) return null;
        var exponent = Math.Max(used.Value.Exponent, limit.Value.Exponent);

        return new SpendAmount(
            Rescale(used.Value, exponent), Rescale(limit.Value, exponent), currency, exponent);
    }

    private static long Rescale(MoneyValue money, int exponent)
    {
        var factor = 1L;
        for (var i = 0; i < exponent - money.Exponent; i++) factor *= 10L;
        return money.Minor * factor;
    }

    private static MoneyValue? Money(JsonElement parent, string name)
    {
        if (parent.ValueKind != JsonValueKind.Object || !parent.TryGetProperty(name, out var value)) return null;

        if (value.ValueKind == JsonValueKind.Object)
        {
            var minor = JsonReadHelpers.NumberAny(value, "amount_minor", "amountMinor");
            if (minor is null) return null;
            var exponent = JsonReadHelpers.NumberAny(value, "exponent");
            return new MoneyValue(
                (long)Math.Round(minor.Value),
                StatedCurrency(JsonReadHelpers.StringAny(value, "currency")),
                exponent is null ? 2 : (int)Math.Round(exponent.Value));
        }

        var plain = JsonReadHelpers.Number(value);
        return plain is null ? null : new MoneyValue((long)Math.Round(plain.Value), null, 2);
    }

    private static string? StatedCurrency(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant();

    private readonly record struct MoneyValue(long Minor, string? Currency, int Exponent);
}
