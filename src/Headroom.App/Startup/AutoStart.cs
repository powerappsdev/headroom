using System;
using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace Headroom.App.Startup;

/// <summary>
/// Launch-at-login through the per-user Run key.
/// </summary>
/// <remarks>
/// The Run key rather than a scheduled task, deliberately: it needs no elevation,
/// the user can see and remove it from Task Manager's Startup tab like any other
/// app, and it cannot outlive an uninstall in a way they cannot find.
/// </remarks>
public static class AutoStart
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Headroom";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: false);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    /// <summary>Returns true when the requested state was actually achieved.</summary>
    public static bool Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key is null) return false;

            if (!enabled)
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                return true;
            }

            var path = ExecutablePath();
            if (path is null) return false;

            key.SetValue(ValueName, $"\"{path}\"", RegistryValueKind.String);
            return true;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    private static string? ExecutablePath()
    {
        // Environment.ProcessPath is the real host executable, which is what the
        // Run key needs - the entry assembly location is a .dll on modern .NET.
        var path = Environment.ProcessPath;
        if (!string.IsNullOrEmpty(path) && File.Exists(path)) return path;

        using var process = Process.GetCurrentProcess();
        return process.MainModule?.FileName;
    }
}
