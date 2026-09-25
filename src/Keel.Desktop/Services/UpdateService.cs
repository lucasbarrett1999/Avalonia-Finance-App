using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Microsoft.Extensions.Logging;
using Velopack;
using Velopack.Sources;

namespace Keel.Desktop.Services;

/// <summary>Finds and applies updates; the Velopack implementation talks to GitHub releases.</summary>
public interface IUpdateSource
{
    /// <summary>Whether the app was installed by Velopack (a development build cannot update itself).</summary>
    bool IsInstalled { get; }

    /// <summary>The newer version available, or null.</summary>
    Task<string?> CheckAsync(CancellationToken ct);

    /// <summary>Downloads the pending update and restarts into it.</summary>
    Task ApplyAndRestartAsync(CancellationToken ct);
}

/// <summary>Velopack's <see cref="UpdateManager"/> over the GitHub releases feed.</summary>
public sealed class VelopackUpdateSource : IUpdateSource
{
    private readonly Lazy<UpdateManager> _manager = new(() => new UpdateManager(new GithubSource(KeelInfo.Repository.ToString(), accessToken: null, prerelease: true)));
    private UpdateInfo? _pending;

    /// <inheritdoc />
    public bool IsInstalled => _manager.Value.IsInstalled;

    /// <inheritdoc />
    public async Task<string?> CheckAsync(CancellationToken ct)
    {
        _pending = await _manager.Value.CheckForUpdatesAsync().ConfigureAwait(false);
        return _pending?.TargetFullRelease.Version.ToString();
    }

    /// <inheritdoc />
    public async Task ApplyAndRestartAsync(CancellationToken ct)
    {
        if (_pending is null)
        {
            return;
        }

        await _manager.Value.DownloadUpdatesAsync(_pending, cancelToken: ct).ConfigureAwait(false);
        _manager.Value.ApplyUpdatesAndRestart(_pending);
    }
}

/// <summary>
/// The update check (PRD 10): the only network call besides bank sync, OFF by default until a release
/// feed exists, switched on in Settings → Updates. With it off nothing contacts the network, and the
/// source is not even created.
/// </summary>
public sealed partial class UpdateService(IAppSettingsStore settings, StatusService status, Func<IUpdateSource> sourceFactory, ILogger<UpdateService> logger) : ObservableObject
{
    private IUpdateSource? _source;

    /// <summary>Whether the user allowed update checks.</summary>
    public bool IsEnabled => settings.Current.CheckForUpdates;

    /// <summary>The last result, for Settings.</summary>
    [ObservableProperty]
    public partial string? StatusText { get; private set; }

    /// <summary>A newer version was found and can be installed.</summary>
    [ObservableProperty]
    public partial bool IsUpdateAvailable { get; private set; }

    /// <summary>Whether a check has contacted the update source in this session (tests).</summary>
    public bool HasContactedSource { get; private set; }

    /// <summary>Turns update checks on or off.</summary>
    public void SetEnabled(bool enabled)
    {
        settings.Update(s => s with { CheckForUpdates = enabled });
        if (!enabled)
        {
            StatusText = null;
            IsUpdateAvailable = false;
        }
    }

    /// <summary>Checks for an update when enabled; <paramref name="announce"/> puts a found update in the status strip.</summary>
    public async Task CheckAsync(bool announce)
    {
        if (!IsEnabled)
        {
            return;
        }

        try
        {
            var source = _source ??= sourceFactory();
            if (!source.IsInstalled)
            {
                StatusText = Strings.Updates_NotInstalled;
                return;
            }

            HasContactedSource = true;
            var version = await source.CheckAsync(CancellationToken.None).ConfigureAwait(true);
            IsUpdateAvailable = version is not null;
            StatusText = version is null ? LedgerText.Format(Strings.Updates_UpToDate, KeelInfo.Version) : LedgerText.Format(Strings.Updates_Available, version);
            if (announce && version is not null)
            {
                status.Show(StatusText);
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException)
        {
            LogCheckFailed(logger, ex);
            StatusText = Strings.Updates_CheckFailed;
        }
    }

    /// <summary>Installs the found update and restarts.</summary>
    public async Task ApplyAsync()
    {
        if (_source is { } source && IsUpdateAvailable)
        {
            await source.ApplyAndRestartAsync(CancellationToken.None).ConfigureAwait(true);
        }
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Update check failed")]
    private static partial void LogCheckFailed(ILogger logger, Exception exception);
}
