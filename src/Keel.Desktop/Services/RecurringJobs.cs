using System.Data.Common;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Forecast;
using Keel.Application.Messaging;
using Keel.Application.Recurring;
using Keel.Application.Scheduling;
using Keel.Desktop.Resources;
using Keel.Desktop.ViewModels.Bills;
using Microsoft.Extensions.Logging;

namespace Keel.Desktop.Services;

/// <summary>
/// The M5 background work of a running app (ADR 0035): on start and on every day change it enters due
/// auto-enter scheduled transactions (and prompts for the others), runs recurring detection once per app
/// day with its alerts, and after every <see cref="LedgerChanged"/> runs detection for newly imported
/// rows. It also drops cached forecasts on <see cref="LedgerChanged"/> and <see cref="RecurringChanged"/>.
/// All service calls run off the UI thread; failures go to the status strip and the log.
/// </summary>
public sealed partial class RecurringJobs : IRecipient<LedgerChanged>, IRecipient<RecurringChanged>, IDisposable
{
    private readonly IRecurringService _recurring;
    private readonly IScheduledTransactionService _scheduled;
    private readonly IForecastService _forecast;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private readonly AppSession _session;
    private readonly TimeProvider _time;
    private readonly ILogger<RecurringJobs> _logger;
    private readonly IMessenger _messenger;
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private DispatcherTimer? _timer;
    private DateOnly _lastDay;
    private bool _importPending;

    /// <summary>Creates the jobs (not started).</summary>
    public RecurringJobs(
        IRecurringService recurring,
        IScheduledTransactionService scheduled,
        IForecastService forecast,
        DialogService dialogs,
        StatusService status,
        AppSession session,
        TimeProvider time,
        IMessenger messenger,
        ILogger<RecurringJobs> logger)
    {
        _recurring = recurring;
        _scheduled = scheduled;
        _forecast = forecast;
        _dialogs = dialogs;
        _status = status;
        _session = session;
        _time = time;
        _messenger = messenger;
        _logger = logger;
    }

    /// <summary>The start or day-change run in progress (tests await it).</summary>
    public Task Running { get; private set; } = Task.CompletedTask;

    /// <summary>Whether <see cref="Start"/> ran.</summary>
    public bool IsStarted { get; private set; }

    private DateOnly Today => DateOnly.FromDateTime(_time.GetLocalNow().DateTime);

    /// <summary>Starts the jobs for the open budget file: the day's run now, then a day-change check every minute.</summary>
    public void Start()
    {
        if (IsStarted || _session.BudgetFile is null)
        {
            return;
        }

        IsStarted = true;
        _messenger.Register<LedgerChanged>(this);
        _messenger.Register<RecurringChanged>(this);
        _lastDay = Today;
        Running = RunDayAsync(_lastDay);
        _timer = new DispatcherTimer(TimeSpan.FromMinutes(1), DispatcherPriority.Background, (_, _) => CheckDay());
        _timer.Start();
    }

    /// <summary>Runs the day's work if the date changed since the last run.</summary>
    public void CheckDay()
    {
        var today = Today;
        if (today != _lastDay && Running.IsCompleted)
        {
            _lastDay = today;
            Running = RunDayAsync(today);
        }
    }

    /// <inheritdoc />
    public void Receive(LedgerChanged message)
    {
        _forecast.Invalidate();
        if (!_importPending)
        {
            _importPending = true;
            _ = Task.Run(DetectImportsAsync);
        }
    }

    /// <inheritdoc />
    public void Receive(RecurringChanged message) => _forecast.Invalidate();

    /// <summary>Scheduled entry, the startup prompt and the daily detection for <paramref name="today"/>.</summary>
    public async Task RunDayAsync(DateOnly today)
    {
        try
        {
            var due = await Task.Run(() => _scheduled.EnterDueAsync(today, CancellationToken.None)).ConfigureAwait(true);
            if (due.EnteredTransactionIds.Count > 0)
            {
                _status.Show(LedgerText.Format(Strings.Schedule_AutoEnteredCount, due.EnteredTransactionIds.Count), offerUndo: true);
            }

            await Task.Run(() => _recurring.DetectNewImportsAsync(today, CancellationToken.None)).ConfigureAwait(true);
            await Task.Run(() => _recurring.RunDailyAsync(today, CancellationToken.None)).ConfigureAwait(true);
            if (due.NeedsPrompt.Count > 0)
            {
                var prompt = new ScheduledPromptViewModel(_scheduled, due.NeedsPrompt);
                _ = ShowPromptAsync(prompt);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException or FormatException)
        {
            LogJobFailed(_logger, ex);
            _status.Show(LedgerText.Format(Strings.Bills_JobsFailed, ex.Message), isError: true);
        }
    }

    /// <summary>Stops the day-change timer and message handlers (the session closed, M8 file switching).</summary>
    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        _messenger.UnregisterAll(this);
        _importGate.Dispose();
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Scheduled entry or recurring detection failed")]
    private static partial void LogJobFailed(ILogger logger, Exception exception);

    private async Task ShowPromptAsync(ScheduledPromptViewModel prompt)
    {
        await _dialogs.ShowAsync(prompt).ConfigureAwait(true);
        if (prompt.EnteredCount > 0)
        {
            _status.Show(LedgerText.Format(Strings.Schedule_EnteredCount, prompt.EnteredCount), offerUndo: true);
        }
    }

    private async Task DetectImportsAsync()
    {
        await _importGate.WaitAsync().ConfigureAwait(false);
        try
        {
            _importPending = false;
            await _recurring.DetectNewImportsAsync(Today, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            LogJobFailed(_logger, ex);
        }
        finally
        {
            _importGate.Release();
        }
    }
}
