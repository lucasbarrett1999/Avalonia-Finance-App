using Keel.Application.Ledger;
using Keel.Application.Settings;
using Keel.Application.Stats;
using Microsoft.Extensions.Logging;

namespace Keel.Desktop.Services;

/// <summary>
/// The measurements behind Settings → Privacy &amp; Stats that only the UI can take (PRD 4, ADR 0102): the first
/// launch, the cold start to interactive, and register scroll frame times. A session service (ADR 0080); results go
/// to settings.json (<see cref="LocalStats"/>) and never leave the computer.
/// </summary>
public sealed partial class StatsInstrumentation(
    BudgetSessions sessions,
    IAppSettingsStore settings,
    AppSession session,
    IRegisterQuery register,
    TimeProvider time,
    ILogger<StatsInstrumentation> logger)
{
    /// <summary>The cold start this session still has to report (only the first session of a process has one).</summary>
    public bool IsColdStartPending => sessions.ColdStartedAt is not null;

    /// <summary>The cold-start recording (tests await it).</summary>
    public Task ColdStartRecording { get; private set; } = Task.CompletedTask;

    /// <summary>Records the first launch of Keel on this computer (a fresh install only; older installs stay unknown).</summary>
    public static void RecordFirstLaunch(IAppSettingsStore settings, bool isFirstRun, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (isFirstRun && settings.Current.Stats.FirstLaunchAt is null)
        {
            settings.Update(s => s with { Stats = s.Stats with { FirstLaunchAt = nowUtc } });
        }
    }

    /// <summary>
    /// The main window drew its first frame with the file loaded: stores process start → now as the cold start,
    /// with the file's transaction count. Runs once per process; a locked or failed start is not a measurement.
    /// </summary>
    public Task MarkInteractiveAsync() => ColdStartRecording = MarkInteractiveCoreAsync();

    /// <summary>
    /// Stores the register's scroll session when it has at least <see cref="StatsReport.LargeRegisterRows"/> rows
    /// (called when a scroll burst ends, so at most a few times a second while the user scrolls).
    /// </summary>
    public void RecordScroll(ScrollFrameMeter meter, int rows)
    {
        ArgumentNullException.ThrowIfNull(meter);
        if (rows >= StatsReport.LargeRegisterRows && meter.Summarize(rows, time.GetUtcNow().UtcDateTime) is { } sample)
        {
            settings.Update(s => s with { Stats = s.Stats with { RegisterScroll = sample } });
        }
    }

    private async Task MarkInteractiveCoreAsync()
    {
        if (sessions.TakeColdStart() is not { } started)
        {
            return;
        }

        var elapsed = time.GetUtcNow().UtcDateTime - started;
        if (session.BudgetFile is not { } file || elapsed <= TimeSpan.Zero)
        {
            return;
        }

        try
        {
            var count = await Task.Run(() => register.CountAsync(new RegisterFilter(), CancellationToken.None));
            var sample = new ColdStartSample((long)elapsed.TotalMilliseconds, count, file.IsEncrypted, time.GetUtcNow().UtcDateTime);
            settings.Update(s => s with { Stats = s.Stats with { ColdStart = sample } });
            LogColdStart(logger, sample.Milliseconds);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Data.Common.DbException)
        {
            LogColdStartFailed(logger, ex);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Cold start to interactive: {Milliseconds} ms")]
    private static partial void LogColdStart(ILogger logger, long milliseconds);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording the cold start failed")]
    private static partial void LogColdStartFailed(ILogger logger, Exception exception);
}
