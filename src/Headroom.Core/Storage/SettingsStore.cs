using System;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Headroom.Core.Storage;

/// <summary>
/// Loads and saves <see cref="HeadroomSettings"/> as readable JSON.
/// </summary>
/// <remarks>
/// Saves are atomic: the file is written beside its destination and moved into
/// place, so a crash or a power cut during a write leaves the previous settings
/// intact rather than a half-written file the app cannot start from. A settings
/// file that fails to parse is backed up rather than deleted, because it is the
/// user's account list and losing it silently would be unforgivable.
/// </remarks>
public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly HeadroomPaths _paths;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private bool _disposed;

    public SettingsStore(HeadroomPaths paths) => _paths = paths;

    public string FilePath => _paths.SettingsFile;

    public HeadroomSettings Load()
    {
        if (!File.Exists(_paths.SettingsFile)) return new HeadroomSettings().Normalized();

        string text;
        try
        {
            text = File.ReadAllText(_paths.SettingsFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new HeadroomSettings().Normalized();
        }

        try
        {
            var loaded = JsonSerializer.Deserialize<HeadroomSettings>(text, Options);
            return (loaded ?? new HeadroomSettings()).Normalized();
        }
        catch (JsonException)
        {
            QuarantineUnreadableFile();
            return new HeadroomSettings().Normalized();
        }
    }

    public async Task SaveAsync(HeadroomSettings settings, CancellationToken cancellationToken = default)
    {
        // A save already in flight when the app quits must not fault on a
        // disposed lock; there is nothing useful left to write at that point.
        if (_disposed) return;

        _paths.EnsureCreated();
        var payload = JsonSerializer.Serialize(settings.Normalized(), Options);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var temporary = _paths.SettingsFile + ".tmp";
            await File.WriteAllTextAsync(temporary, payload, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, _paths.SettingsFile, overwrite: true);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _writeLock.Dispose();
    }

    private void QuarantineUnreadableFile()
    {
        try
        {
            var backup = _paths.SettingsFile + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture);
            File.Move(_paths.SettingsFile, backup, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Nothing more we can do; defaults are still returned to the caller.
        }
    }
}
