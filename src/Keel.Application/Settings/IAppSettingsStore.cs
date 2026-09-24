namespace Keel.Application.Settings;

/// <summary>Loads and saves <see cref="AppSettings"/> (settings.json).</summary>
public interface IAppSettingsStore
{
    /// <summary>The current settings (loaded lazily on first access; defaults when the file is missing or corrupt).</summary>
    AppSettings Current { get; }

    /// <summary>Applies <paramref name="update"/> to the current settings and writes the file atomically.</summary>
    AppSettings Update(Func<AppSettings, AppSettings> update);

    /// <summary>Asynchronous variant of <see cref="Update"/>.</summary>
    Task<AppSettings> UpdateAsync(Func<AppSettings, AppSettings> update, CancellationToken ct);
}
