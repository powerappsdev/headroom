using System;
using Headroom.Core.Model;

namespace Headroom.Core.Accounts;

/// <summary>
/// A configured account: a name, a provider, and the profile directory whose
/// credential Headroom reads.
/// </summary>
/// <remarks>
/// Headroom never creates, copies, moves or deletes a credential. It points at
/// a directory the provider's own CLI already owns, and switching accounts means
/// pointing new terminal sessions at a different one of those directories via
/// <c>CLAUDE_CONFIG_DIR</c> / <c>CODEX_HOME</c>. There are no symlinks and no
/// swapping of the CLI's real home, which is what makes it impossible for
/// Headroom to lose someone's sign-in.
/// </remarks>
public sealed record AccountDefinition
{
    public required string Id { get; init; }

    public required string DisplayName { get; init; }

    public required ProviderKind Provider { get; init; }

    /// <summary>
    /// The profile home: a <c>CLAUDE_CONFIG_DIR</c> for Claude, a
    /// <c>CODEX_HOME</c> for Codex. Read-only as far as Headroom is concerned.
    /// </summary>
    public required string ProfileDirectory { get; init; }

    public bool Enabled { get; init; } = true;

    /// <summary>Optional free-text note ("work", "side project"). Display only.</summary>
    public string? Purpose { get; init; }

    public static string NewId() => Guid.NewGuid().ToString("n")[..12];
}
