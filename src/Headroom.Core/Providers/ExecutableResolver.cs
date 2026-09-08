using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace Headroom.Core.Providers;

/// <summary>
/// Finds a CLI on PATH, including the shim forms npm installs on Windows.
/// </summary>
/// <remarks>
/// This exists because <c>Process.Start</c> with <c>UseShellExecute = false</c>
/// will not find <c>codex.cmd</c> or <c>claude.cmd</c> when asked for
/// <c>codex</c>. Both CLIs are commonly installed through npm, which is exactly
/// how they land as <c>.cmd</c> shims, so resolving the real file up front is
/// the difference between working and a mystifying "file not found".
/// </remarks>
public static class ExecutableResolver
{
    /// <summary>The extensions Windows itself falls back to when PATHEXT is missing or unusable.</summary>
    private static readonly string[] DefaultWindowsExtensions = { ".COM", ".EXE", ".BAT", ".CMD" };

    /// <summary>Returns the full path to <paramref name="command"/>, or null when it is not on PATH.</summary>
    public static string? Resolve(string command, string? pathVariable = null, string? pathExtVariable = null)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        // An explicit path is used as given, so a user override always wins.
        if (command.Contains(Path.DirectorySeparatorChar) || command.Contains(Path.AltDirectorySeparatorChar))
            return File.Exists(command) ? Path.GetFullPath(command) : null;

        var isWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
        var path = pathVariable ?? Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        var extensions = Extensions(isWindows, pathExtVariable);

        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = directory.Trim('"');
            if (trimmed.Length == 0) continue;

            foreach (var extension in extensions)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(trimmed, command + extension);
                }
                catch (ArgumentException)
                {
                    continue;
                }

                if (File.Exists(candidate)) return candidate;
            }
        }

        return null;
    }

    private static IReadOnlyList<string> Extensions(bool isWindows, string? pathExtVariable)
    {
        if (!isWindows) return new[] { string.Empty };

        var raw = pathExtVariable ?? Environment.GetEnvironmentVariable("PATHEXT");
        var extensions = new List<string> { string.Empty };

        if (!string.IsNullOrWhiteSpace(raw))
        {
            foreach (var extension in raw.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var normalized = extension.Trim();
                if (normalized.Length == 0) continue;
                if (!normalized.StartsWith('.')) normalized = "." + normalized;
                extensions.Add(normalized);
            }
        }
        else
        {
            extensions.AddRange(DefaultWindowsExtensions);
        }

        // npm shims are .cmd; make sure they are reachable even if PATHEXT is odd.
        if (!extensions.Contains(".CMD", StringComparer.OrdinalIgnoreCase)) extensions.Add(".CMD");
        if (!extensions.Contains(".EXE", StringComparer.OrdinalIgnoreCase)) extensions.Add(".EXE");

        return extensions;
    }
}
