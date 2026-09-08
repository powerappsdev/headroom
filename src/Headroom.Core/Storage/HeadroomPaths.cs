using System;
using System.IO;

namespace Headroom.Core.Storage;

/// <summary>
/// Where Headroom keeps its own files. Everything lives in one folder so the
/// app can be removed by deleting it, and nothing is ever written into a
/// provider's directory.
/// </summary>
public sealed class HeadroomPaths
{
    public const string DataDirectoryVariable = "HEADROOM_DATA_DIR";

    public HeadroomPaths(string? dataDirectory = null)
    {
        Root = dataDirectory
               ?? Environment.GetEnvironmentVariable(DataDirectoryVariable)
               ?? DefaultRoot();

        HistoryDirectory = Path.Combine(Root, "history");
        SettingsFile = Path.Combine(Root, "settings.json");
        LogFile = Path.Combine(Root, "headroom.log");
    }

    public string Root { get; }

    public string HistoryDirectory { get; }

    public string SettingsFile { get; }

    public string LogFile { get; }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(HistoryDirectory);
    }

    private static string DefaultRoot()
    {
        var local = Environment.GetFolderPath(
            Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);

        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify),
                ".local",
                "share");
        }

        return Path.Combine(local, "Headroom");
    }
}
