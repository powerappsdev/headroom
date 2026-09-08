using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Headroom.Core.Model;

namespace Headroom.Core.Storage;

/// <summary>
/// Writes a human-readable snapshot of the last refresh to disk.
/// </summary>
/// <remarks>
/// This exists so that "it isn't working" can be answered by reading one file
/// rather than by asking someone to describe what a card says. It deliberately
/// carries no secrets: no token, no account email, only whether an identity was
/// read at all. Failure to write is silent, because a diagnostics file that can
/// break a refresh would be worse than no diagnostics file.
/// </remarks>
public sealed class StateSnapshotWriter
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HeadroomPaths _paths;

    public StateSnapshotWriter(HeadroomPaths paths) => _paths = paths;

    public void Write(DeckSnapshot deck)
    {
        try
        {
            _paths.EnsureCreated();
            File.WriteAllText(_paths.StateFile, Render(deck));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Diagnostics are a courtesy; never let them affect a refresh.
        }
    }

    /// <summary>Pure rendering step, separated so the redaction rules are testable.</summary>
    public static string Render(DeckSnapshot deck) => JsonSerializer.Serialize(
        new
        {
            generatedAt = deck.GeneratedAt,
            accounts = deck.Accounts.Select(account => new
            {
                name = account.DisplayName,
                provider = account.Provider,
                availability = account.Availability,
                detail = account.Detail,
                observedAt = account.ObservedAt,
                planTier = account.PlanTier,

                // Whether an identity was read, never the identity itself.
                identityKnown = !string.IsNullOrWhiteSpace(account.Identity),

                windows = account.OrderedWindows.Select(window => new
                {
                    scope = window.Scope,
                    kind = window.Kind,
                    usedPercent = window.UsedPercent,
                    remainingPercent = window.RemainingPercent,
                    resetsAt = window.ResetsAt,
                }),
            }),
        },
        Options);
}
