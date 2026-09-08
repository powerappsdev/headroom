using System;
using System.Diagnostics;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Headroom.Core.Providers;

namespace Headroom.Core.Providers.Codex;

public enum CodexProbeStatus
{
    Ok,
    BinaryMissing,
    NotSignedIn,
    Timeout,
    Failed,
}

/// <summary>Raw outcome of one app-server conversation.</summary>
public sealed record CodexAppServerResult(CodexProbeStatus Status, string? Json, string? Detail)
{
    public static CodexAppServerResult Failure(CodexProbeStatus status, string detail) =>
        new(status, null, detail);
}

/// <summary>Lets the provider be tested without spawning a real CLI.</summary>
public interface ICodexAppServer
{
    Task<CodexAppServerResult> ReadRateLimitsAsync(string profileDirectory, CancellationToken cancellationToken = default);
}

/// <summary>
/// Speaks the Codex CLI's official app-server protocol over stdio.
/// </summary>
/// <remarks>
/// The whole conversation is three lines of newline-delimited JSON-RPC:
/// <c>initialize</c>, then <c>initialized</c> plus
/// <c>account/rateLimits/read</c>. Using the documented protocol rather than
/// scraping <c>codex</c> output means the numbers come from the same place the
/// CLI gets them, and a CLI update cannot silently change what Headroom reads.
/// </remarks>
public sealed class CodexAppServerClient : ICodexAppServer
{
    private readonly string _command;
    private readonly TimeSpan _timeout;

    public CodexAppServerClient(string command = "codex", TimeSpan? timeout = null)
    {
        _command = command;
        _timeout = timeout ?? TimeSpan.FromSeconds(25);
    }

    public async Task<CodexAppServerResult> ReadRateLimitsAsync(
        string profileDirectory, CancellationToken cancellationToken = default)
    {
        var executable = ExecutableResolver.Resolve(_command);
        if (executable is null)
        {
            return CodexAppServerResult.Failure(
                CodexProbeStatus.BinaryMissing,
                "The Codex CLI was not found on PATH.");
        }

        // npm installs the Codex CLI as a .cmd shim on Windows, and CreateProcess
        // cannot start one directly - it has to go through the command processor.
        var isBatch = ExecutableResolver.IsBatchScript(executable);
        var startInfo = new ProcessStartInfo(isBatch ? "cmd.exe" : executable)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (isBatch)
        {
            startInfo.ArgumentList.Add("/c");
            startInfo.ArgumentList.Add(executable);
        }

        startInfo.ArgumentList.Add("app-server");
        startInfo.ArgumentList.Add("--stdio");
        if (!string.IsNullOrWhiteSpace(profileDirectory))
            startInfo.Environment["CODEX_HOME"] = profileDirectory;

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
                return CodexAppServerResult.Failure(CodexProbeStatus.Failed, "The Codex CLI did not start.");
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return CodexAppServerResult.Failure(CodexProbeStatus.Failed, "The Codex CLI could not be launched.");
        }

        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(_timeout);

        try
        {
            return await ConverseAsync(process, timeoutSource.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return CodexAppServerResult.Failure(CodexProbeStatus.Timeout, "The Codex CLI did not answer in time.");
        }
        finally
        {
            TryKill(process);
        }
    }

    private static async Task<CodexAppServerResult> ConverseAsync(Process process, CancellationToken cancellationToken)
    {
        var initialize = JsonSerializer.Serialize(new
        {
            id = 1,
            method = "initialize",
            @params = new
            {
                clientInfo = new { name = "headroom", title = "Headroom", version = "1.0.0" },
                capabilities = new { },
            },
        });

        await process.StandardInput.WriteLineAsync(initialize.AsMemory(), cancellationToken).ConfigureAwait(false);
        await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);

        var handshakeSent = false;

        while (true)
        {
            var line = await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (line is null)
            {
                var stderr = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                return Classify(stderr);
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(line);
            }
            catch (JsonException)
            {
                // The CLI prints the occasional non-protocol line; ignore it rather
                // than treating a log message as a protocol failure.
                continue;
            }

            using (document)
            {
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;

                var id = root.TryGetProperty("id", out var idElement) && idElement.ValueKind == JsonValueKind.Number
                    ? idElement.GetInt32()
                    : (int?)null;

                if (id == 1 && !handshakeSent)
                {
                    if (root.TryGetProperty("error", out var initError))
                        return Classify(initError.ToString());

                    await process.StandardInput
                        .WriteLineAsync(JsonSerializer.Serialize(new { method = "initialized" }).AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    await process.StandardInput
                        .WriteLineAsync(
                            JsonSerializer.Serialize(new
                            {
                                id = 2,
                                method = "account/rateLimits/read",
                                @params = (object?)null,
                            }).AsMemory(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
                    handshakeSent = true;
                    continue;
                }

                if (id == 2)
                {
                    if (root.TryGetProperty("error", out var error)) return Classify(error.ToString());
                    return root.TryGetProperty("result", out var result)
                        ? new CodexAppServerResult(CodexProbeStatus.Ok, result.GetRawText(), null)
                        : new CodexAppServerResult(CodexProbeStatus.Ok, root.GetRawText(), null);
                }
            }
        }
    }

    /// <summary>
    /// Maps an error payload or stderr blob onto a status. Signed-out is only
    /// claimed when the text actually says so; anything else stays a plain
    /// failure so a transient hiccup never sends someone to re-run a login.
    /// </summary>
    internal static CodexAppServerResult Classify(string? text)
    {
        var message = (text ?? string.Empty).ToLowerInvariant();

        if (message.Contains("not logged in") ||
            message.Contains("not signed in") ||
            message.Contains("unauthenticated") ||
            message.Contains("token_invalidated") ||
            message.Contains("run `codex login`") ||
            message.Contains("codex login"))
        {
            return CodexAppServerResult.Failure(
                CodexProbeStatus.NotSignedIn, "Not signed in. Run codex login for this profile.");
        }

        return CodexAppServerResult.Failure(
            CodexProbeStatus.Failed,
            "The Codex CLI did not return usage.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException or System.ComponentModel.Win32Exception)
        {
            // The process already ended; nothing to clean up.
        }
    }
}
