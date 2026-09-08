using System;
using System.Linq;
using System.Net;
using Headroom.Core.Model;
using Headroom.Core.Providers.Claude;
using Headroom.Core.Providers.Codex;

namespace Headroom.Core.Tests;

public static class ClaudeProviderTests
{
    private const string GoodBody = """{"five_hour":{"utilization":10},"plan":"max_20x"}""";

    [Test]
    public static void SuccessProducesWindowsAndPlan()
    {
        var result = ClaudeUsageProvider.Interpret(HttpStatusCode.OK, null, GoodBody, "a@b.c");

        Check.True(result.Succeeded);
        Check.Count(1, result.Windows);
        Check.Equal("max_20x", result.PlanTier);
        Check.Equal("a@b.c", result.Identity);
    }

    [Test("A structured authentication_error is the only thing that means signed out")]
    public static void StructuredUnauthorizedIsSignedOut()
    {
        var result = ClaudeUsageProvider.Interpret(
            HttpStatusCode.Unauthorized, null, """{"error":{"type":"authentication_error"}}""", null);

        Check.Equal(AccountAvailability.SignedOut, result.Availability);
        Check.False(result.IsTransientFailure, "a real sign-out must not trigger backoff escalation");
    }

    [Test("An unstructured 401 is transient - guessing 'signed out' sends people to a needless login")]
    public static void UnstructuredUnauthorizedIsTransient()
    {
        var result = ClaudeUsageProvider.Interpret(HttpStatusCode.Unauthorized, null, "<html>nope</html>", null);

        Check.Equal(AccountAvailability.ProviderUnreachable, result.Availability);
        Check.True(result.IsTransientFailure);
    }

    [Test("A 429 carries the provider's own Retry-After through to the scheduler")]
    public static void RateLimitCarriesRetryAfter()
    {
        var result = ClaudeUsageProvider.Interpret(
            HttpStatusCode.TooManyRequests, TimeSpan.FromMinutes(9), "", null);

        Check.True(result.IsTransientFailure);
        Check.Equal(TimeSpan.FromMinutes(9), Check.NotNullValue(result.RetryAfter));
        Check.Contains("rate limited", result.Detail);
    }

    [Test]
    public static void ServerErrorIsTransient()
    {
        var result = ClaudeUsageProvider.Interpret(HttpStatusCode.InternalServerError, null, "", null);
        Check.True(result.IsTransientFailure);
        Check.Contains("500", result.Detail);
    }

    [Test("A 200 whose shape we cannot read is unknown, not zero usage")]
    public static void UnreadableSuccessBodyIsTransient()
    {
        var result = ClaudeUsageProvider.Interpret(HttpStatusCode.OK, null, """{"something":"new"}""", null);

        Check.False(result.Succeeded);
        Check.True(result.IsTransientFailure);
        Check.Count(0, result.Windows);
    }
}

public static class CodexParsingTests
{
    [Test("Several named buckets each contribute their own windows")]
    public static void ReadsMultipleLimitBuckets()
    {
        var windows = CodexRateLimitParser.Parse("""
        {
          "rateLimits": { "planType": "pro",
            "primary":   { "usedPercent": 12, "windowDurationMins": 300,   "resetsAt": 1780000000 },
            "secondary": { "usedPercent": 44, "windowDurationMins": 10080, "resetsAt": 1780100000 } },
          "rateLimitsByLimitId": {
            "codex": { "limitId": "codex", "limitName": "Codex", "planType": "pro",
              "primary":   { "usedPercent": 12, "windowDurationMins": 300,   "resetsAt": 1780000000 },
              "secondary": { "usedPercent": 44, "windowDurationMins": 10080, "resetsAt": 1780100000 } },
            "spark": { "limitId": "spark", "limitName": "Spark", "planType": "pro",
              "secondary": { "usedPercent": 5, "windowDurationMins": 10080, "resetsAt": 1780200000 } }
          }
        }
        """);

        Check.Count(3, windows);
        Check.Close(12, Find(windows, "5-hour").UsedPercent);
        Check.Close(44, Find(windows, "weekly").UsedPercent);
        Check.Close(5, Find(windows, "Spark weekly").UsedPercent);
    }

    [Test("Window names come from the stated duration, not the primary/secondary slot")]
    public static void NamesWindowsByDuration()
    {
        var windows = CodexRateLimitParser.Parse("""
        { "rateLimits": { "limitId": "codex", "planType": "pro",
          "primary": { "usedPercent": 64, "windowDurationMins": 10080, "resetsAt": 1784950179 },
          "secondary": null } }
        """);

        Check.Count(1, windows);
        Check.Equal("weekly", windows[0].Scope);
        Check.Equal(WindowKind.Weekly, windows[0].Kind);
        Check.Equal(
            DateTimeOffset.FromUnixTimeSeconds(1784950179).ToUniversalTime(),
            Check.NotNullValue(windows[0].ResetsAt).ToUniversalTime());
    }

    [Test("An unusual window duration is labelled honestly rather than forced into a known bucket")]
    public static void LabelsUnknownDurations()
    {
        var windows = CodexRateLimitParser.Parse("""
        { "rateLimits": { "primary": { "usedPercent": 7, "windowDurationMins": 1440 } } }
        """);

        Check.Equal("1440-minute", windows[0].Scope);
    }

    [Test]
    public static void UnwrapsJsonRpcResult()
    {
        var windows = CodexRateLimitParser.Parse("""
        { "result": { "rateLimits": { "primary": { "usedPercent": 3, "windowDurationMins": 300 } } } }
        """);

        Check.Count(1, windows);
        Check.Close(3, windows[0].UsedPercent);
    }

    [Test]
    public static void ReadsPlanTier()
    {
        using var doc = System.Text.Json.JsonDocument.Parse("""
        { "rateLimits": { "planType": "plus", "primary": { "usedPercent": 1, "windowDurationMins": 300 } } }
        """);

        Check.Equal("plus", CodexRateLimitParser.ReadPlanTier(doc.RootElement));
    }

    [Test]
    public static void DegradesToEmptyOnJunk()
    {
        Check.Count(0, CodexRateLimitParser.Parse("nonsense"));
        Check.Count(0, CodexRateLimitParser.Parse("""{"rateLimits":{}}"""));
    }

    private static UsageWindow Find(System.Collections.Generic.IReadOnlyList<UsageWindow> windows, string scope) =>
        Check.NotNull(
            windows.FirstOrDefault(w => string.Equals(w.Scope, scope, StringComparison.OrdinalIgnoreCase)),
            $"no window named '{scope}' in [{string.Join(", ", windows.Select(w => w.Scope))}]");
}

public static class CodexAuthTests
{
    /// <summary>An id_token whose payload states an email and a ChatGPT plan claim.</summary>
    private static string SampleJwt()
    {
        var payload = """
        {"email":"dan@example.invalid","https://api.openai.com/auth":{"chatgpt_plan_type":"pro"}}
        """;
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(payload))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return "header." + encoded + ".signature";
    }

    [Test]
    public static void ReadsEmailAndPlanFromIdToken()
    {
        var info = CodexAuthReader.Parse($$"""{ "tokens": { "id_token": "{{SampleJwt()}}" } }""");

        Check.True(info.FilePresent);
        Check.Equal("dan@example.invalid", info.Identity);
        Check.Equal("pro", info.PlanTier);
    }

    [Test("A half-written or malformed auth file degrades to unknown, never throws")]
    public static void TolerateMalformedAuth()
    {
        Check.Null(CodexAuthReader.Parse("""{"tokens":{"id_token":"not-a-jwt"}}""").Identity);
        Check.Null(CodexAuthReader.Parse("""{"tokens":{}}""").Identity);
        Check.Null(CodexAuthReader.Parse("{").Identity);
        Check.True(CodexAuthReader.Parse("{").FilePresent);
    }
}

public static class CodexClassificationTests
{
    [Test("Only text that actually says so is treated as signed out")]
    public static void RecognizesSignedOut()
    {
        Check.Equal(CodexProbeStatus.NotSignedIn, CodexAppServerClient.Classify("Error: not logged in").Status);
        Check.Equal(CodexProbeStatus.NotSignedIn, CodexAppServerClient.Classify("please run `codex login`").Status);
        Check.Equal(CodexProbeStatus.NotSignedIn, CodexAppServerClient.Classify("token_invalidated").Status);
    }

    [Test]
    public static void OtherFailuresStayGeneric()
    {
        Check.Equal(CodexProbeStatus.Failed, CodexAppServerClient.Classify("connection reset by peer").Status);
        Check.Equal(CodexProbeStatus.Failed, CodexAppServerClient.Classify(null).Status);
    }

    [Test("A signed-out Codex account is not treated as a transient failure to retry")]
    public static void SignedOutIsNotTransient()
    {
        var result = CodexUsageProvider.Interpret(
            CodexAppServerClient.Classify("not logged in"), new CodexAuthInfo(true, "a@b.c", "pro"));

        Check.Equal(AccountAvailability.SignedOut, result.Availability);
        Check.False(result.IsTransientFailure);
        Check.Equal("a@b.c", result.Identity);
    }

    [Test("A timeout keeps the account retryable and does not claim a sign-out")]
    public static void TimeoutIsTransient()
    {
        var result = CodexUsageProvider.Interpret(
            CodexAppServerResult.Failure(CodexProbeStatus.Timeout, "too slow"), CodexAuthInfo.Absent);

        Check.Equal(AccountAvailability.ProviderUnreachable, result.Availability);
        Check.True(result.IsTransientFailure);
    }

    [Test]
    public static void GoodReplyProducesWindows()
    {
        var result = CodexUsageProvider.Interpret(
            new CodexAppServerResult(
                CodexProbeStatus.Ok,
                """{"rateLimits":{"planType":"pro","primary":{"usedPercent":20,"windowDurationMins":300}}}""",
                null),
            new CodexAuthInfo(true, "a@b.c", null));

        Check.True(result.Succeeded);
        Check.Count(1, result.Windows);
        Check.Equal("pro", result.PlanTier);
    }
}
