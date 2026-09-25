using Avalonia.Threading;
using Keel.Application.Backup;
using Keel.Application.Files;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Microsoft.Extensions.Logging;

namespace Keel.Desktop.Services;

/// <summary>
/// Daily care of the open budget file (F-SET-1, PRD 10): once per app day the <c>PRAGMA integrity_check</c>
/// (a failure goes to the status strip as an error with the next step) and the automatic backup with
/// keep-N, plus the opt-in update check. It starts a little after the session so it never competes
/// with startup, then checks for a new day every few minutes. Successful runs are silent.
/// </summary>
public sealed partial class MaintenanceJobs : IDisposable
{
    /// <summary>Delay after the session starts before the first run.</summary>
    public static readonly TimeSpan StartDelay = TimeSpan.FromSeconds(20);

    private readonly IDataFileMaintenance _maintenance;
    private readonly IBackupService _backups;
    private readonly IAppSettingsStore _settings;
    private readonly StatusService _status;
    private readonly AppSession _session;
    private readonly UpdateService _updates;
    private readonly TimeProvider _time;
    private readonly ILogger<MaintenanceJobs> _logger;
    private DispatcherTimer? _timer;
    private DateOnly? _lastDay;

    /// <summary>Creates the jobs (not started).</summary>
    public MaintenanceJobs(IDataFileMaintenance maintenance, IBackupService backups, IAppSettingsStore settings, StatusService status, AppSession session, UpdateService updates, TimeProvider time, ILogger<MaintenanceJobs> logger)
    {
        _maintenance = maintenance;
        _backups = backups;
        _settings = settings;
        _status = status;
        _session = session;
        _updates = updates;
        _time = time;
        _logger = logger;
    }

    /// <summary>The latest run (tests await it).</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>The last integrity-check result of this session, or null.</summary>
    public IntegrityCheckResult? LastIntegrity { get; private set; }

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <summary>Schedules the first run after <see cref="StartDelay"/> and a day check every five minutes.</summary>
    public void Start()
    {
        if (_timer is not null || _session.BudgetFile is null)
        {
            return;
        }

        _timer = new DispatcherTimer(StartDelay, DispatcherPriority.Background, (_, _) =>
        {
            if (_timer is { } timer)
            {
                timer.Interval = TimeSpan.FromMinutes(5);
            }

            if (_lastDay != Today && Running.IsCompleted)
            {
                Running = RunAsync();
            }
        });
        _timer.Start();
    }

    /// <summary>Runs the day's work now (integrity check if due, automatic backup if due, update check if enabled).</summary>
    public async Task RunAsync()
    {
        var today = Today;
        _lastDay = today;
        try
        {
            var result = await Task.Run(() => _maintenance.CheckIntegrityIfDueAsync(today, CancellationToken.None)).ConfigureAwait(true);
            if (result is not null)
            {
                LastIntegrity = result;
                if (!result.IsOk)
                {
                    _status.Show(Strings.Maintenance_IntegrityFailed, isError: true);
                    return; // Never back up a damaged file over good backups.
                }
            }

            var settings = _settings.Current;
            if (settings.AutoBackupEnabled)
            {
                await Task.Run(() => _backups.RunDailyBackupAsync(today, Math.Max(1, settings.AutoBackupKeep), CancellationToken.None)).ConfigureAwait(true);
            }

            if (settings.CheckForUpdates)
            {
                await _updates.CheckAsync(announce: true).ConfigureAwait(true);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or UnauthorizedAccessException or BackupVerificationException or System.Data.Common.DbException)
        {
            LogFailed(_logger, ex);
            _status.Show(LedgerText.Format(Strings.Maintenance_Failed, ex.Message), isError: true);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Daily maintenance (integrity check or automatic backup) failed")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
