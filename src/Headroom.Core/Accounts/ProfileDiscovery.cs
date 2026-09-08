using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Headroom.Core.Model;

namespace Headroom.Core.Accounts;

/// <summary>
/// Finds provider profiles already present on the machine so first launch shows
/// real numbers instead of an empty-state screen and a setup chore.
/// </summary>
public static class ProfileDiscovery
{
    /// <summary>
    /// Looks for the default homes (<c>~/.claude</c>, <c>~/.codex</c>) plus any
    /// sibling profile directories Headroom's own convention creates
    /// (<c>~/.claude-profiles/*</c>, <c>~/.codex-profiles/*</c>).
    /// </summary>
    public static IReadOnlyList<AccountDefinition> Discover(string? homeDirectory = null)
    {
        var home = homeDirectory ?? Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);

        var found = new List<AccountDefinition>();
        if (string.IsNullOrWhiteSpace(home)) return found;

        AddIfPresent(found, Path.Combine(home, ".claude"), ProviderKind.Claude, "Claude");
        AddIfPresent(found, Path.Combine(home, ".codex"), ProviderKind.Codex, "Codex");

        AddProfilesUnder(found, Path.Combine(home, ".claude-profiles"), ProviderKind.Claude);
        AddProfilesUnder(found, Path.Combine(home, ".codex-profiles"), ProviderKind.Codex);

        return found;
    }

    /// <summary>The directory Headroom suggests for a new named profile of a given provider.</summary>
    public static string SuggestedProfileDirectory(ProviderKind provider, string profileName, string? homeDirectory = null)
    {
        var home = homeDirectory ?? Environment.GetFolderPath(
            Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        var root = provider == ProviderKind.Claude ? ".claude-profiles" : ".codex-profiles";
        return Path.Combine(home, root, SafeName(profileName));
    }

    /// <summary>Strips a display name down to something safe to use as a folder name.</summary>
    public static string SafeName(string profileName)
    {
        var cleaned = new string((profileName ?? string.Empty)
            .Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? char.ToLowerInvariant(c) : '-')
            .ToArray())
            .Trim('-');

        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);

        return cleaned.Length == 0 ? "profile" : cleaned;
    }

    private static void AddProfilesUnder(List<AccountDefinition> into, string root, ProviderKind provider)
    {
        if (!Directory.Exists(root)) return;

        IEnumerable<string> children;
        try
        {
            children = Directory.EnumerateDirectories(root);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return;
        }

        foreach (var child in children.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
            AddIfPresent(into, child, provider, TitleCase(Path.GetFileName(child)));
    }

    private static void AddIfPresent(List<AccountDefinition> into, string directory, ProviderKind provider, string name)
    {
        if (!Directory.Exists(directory)) return;
        if (into.Any(a => string.Equals(a.ProfileDirectory, directory, StringComparison.OrdinalIgnoreCase))) return;

        into.Add(new AccountDefinition
        {
            Id = AccountDefinition.NewId(),
            DisplayName = name,
            Provider = provider,
            ProfileDirectory = directory,
        });
    }

    private static string TitleCase(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "Profile";
        var words = value.Replace('-', ' ').Replace('_', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', words.Select(w => char.ToUpperInvariant(w[0]) + w[1..]));
    }
}
