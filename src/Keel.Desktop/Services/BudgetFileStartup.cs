using System.Globalization;
using Keel.Application.Files;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Microsoft.Extensions.Logging;

namespace Keel.Desktop.Services;

/// <summary>
/// Opens the budget file of a session: a requested file (command line, second launch, Settings), else
/// the last-used file if it still exists, else the default <c>budgets/Default.keel</c>. On the very
/// first launch (no remembered file, no default file, setup never finished) it opens nothing and starts
/// the first-run setup instead (PRD 9.10). Failure is a state, not a crash (PRD 1.1): the shell starts
/// and shows the problem. An encrypted file without a known key (F-SET-4) leaves the session locked: the
/// shell asks for the passphrase instead of falling back to another file.
/// </summary>
public sealed partial class BudgetFileStartup(
    IBudgetFileService files,
    IAppSettingsStore settings,
    IDataDirectory dataDirectory,
    AppSession session,
    ILogger<BudgetFileStartup> logger,
    BudgetStartupOptions? options = null,
    IBudgetFileEncryption? encryption = null)
{
    private readonly BudgetStartupOptions _options = options ?? BudgetStartupOptions.Default;

    /// <summary>Whether this launch is the first one: the setup runs instead of creating a file silently.</summary>
    public bool IsFirstRun =>
        !settings.Current.FirstRunCompleted
        && string.IsNullOrWhiteSpace(settings.Current.LastBudgetFile)
        && !File.Exists(dataDirectory.DefaultBudgetFile);

    /// <summary>Opens (or creates) the session's budget file and records the outcome in <see cref="AppSession"/>.</summary>
    /// <exception cref="Exception">With <see cref="BudgetStartupOptions.Strict"/>, whatever opening the file threw.</exception>
    public async Task OpenInitialFileAsync(CancellationToken ct)
    {
        dataDirectory.EnsureCreated();
        StatsInstrumentation.RecordFirstLaunch(settings, IsFirstRun, DateTime.UtcNow);
        string? requestedFailure = null;
        if (_options.FilePath is { } requested)
        {
            try
            {
                if (_options.Encryption is { } change)
                {
                    await (encryption ?? throw new InvalidOperationException("Encryption is not available.")).ConvertAsync(requested, change, ct).ConfigureAwait(false);
                }

                var info = await files.OpenOrCreateAsync(requested, _options.Unlock, ct).ConfigureAwait(false);
                Remember(info, _options.Message ?? (info.Created ? Strings.Shell_StatusCreated : Strings.Shell_StatusOpened), completesFirstRun: !_options.ResumeFirstRun);
                session.FirstRun = _options.ResumeFirstRun ? FirstRunStep.Template : null;
                return;
            }
            catch (BudgetFileLockedException locked) when (!_options.Strict || _options.AllowLocked)
            {
                Lock(locked);
                return;
            }
            catch (Exception ex) when (ex is not OperationCanceledException && !_options.Strict)
            {
                LogOpenFailed(logger, ex);
                requestedFailure = string.Format(CultureInfo.CurrentCulture, Strings.Shell_StatusOpenRequestedFailed, Path.GetFileName(requested), ex.Message);
            }
        }

        if (requestedFailure is null && IsFirstRun)
        {
            session.BudgetFile = null;
            session.FirstRun = FirstRunStep.Welcome;
            session.StartupMessage = Strings.FirstRun_Status;
            return;
        }

        var last = settings.Current.LastBudgetFile;
        var candidate = !string.IsNullOrWhiteSpace(last) && File.Exists(last) ? last : dataDirectory.DefaultBudgetFile;

        try
        {
            var info = await files.OpenOrCreateAsync(candidate, ct).ConfigureAwait(false);
            Remember(info, info.Created ? Strings.Shell_StatusCreated : Strings.Shell_StatusOpened, completesFirstRun: true);
        }
        catch (BudgetFileLockedException locked)
        {
            // Never fall back to another file: the user opens this one with its passphrase.
            Lock(locked);
            return;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOpenFailed(logger, ex);
            if (!string.Equals(candidate, dataDirectory.DefaultBudgetFile, StringComparison.Ordinal))
            {
                try
                {
                    var fallback = await files.OpenOrCreateAsync(dataDirectory.DefaultBudgetFile, ct).ConfigureAwait(false);
                    Remember(fallback, Strings.Shell_StatusOpenedFallback, completesFirstRun: true);
                    return;
                }
                catch (Exception fallbackEx) when (fallbackEx is not OperationCanceledException)
                {
                    LogOpenFailed(logger, fallbackEx);
                    ex = fallbackEx;
                }
            }

            session.BudgetFile = null;
            session.StartupMessage = string.Format(CultureInfo.CurrentCulture, Strings.Shell_StatusOpenFailed, ex.Message);
        }

        if (requestedFailure is not null)
        {
            session.StartupMessage = requestedFailure;
        }
    }

    private void Lock(BudgetFileLockedException locked)
    {
        session.BudgetFile = null;
        session.LockedFile = locked.Path;
        session.StartupMessage = string.Format(CultureInfo.CurrentCulture, Strings.Encrypt_LockedStatus, Path.GetFileName(locked.Path));
        settings.Update(s => s with { LastBudgetFile = locked.Path, FirstRunCompleted = true });
    }

    private void Remember(BudgetFileInfo info, string messageFormat, bool completesFirstRun)
    {
        session.BudgetFile = info;
        session.StartupMessage = string.Format(CultureInfo.CurrentCulture, messageFormat, info.FileName);
        settings.Update(s => s with { LastBudgetFile = info.Path, FirstRunCompleted = s.FirstRunCompleted || completesFirstRun });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Opening the budget file failed")]
    private static partial void LogOpenFailed(ILogger logger, Exception exception);
}
