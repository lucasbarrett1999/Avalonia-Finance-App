using System.Globalization;
using Keel.Application.Files;
using Keel.Application.Settings;
using Keel.Desktop.Resources;
using Microsoft.Extensions.Logging;

namespace Keel.Desktop.Services;

/// <summary>
/// Opens the budget file on launch: the last-used file if it still exists, otherwise the
/// default <c>budgets/Default.keel</c>, which is created and migrated on first launch.
/// Failure is a state, not a crash (PRD 1.1): the shell starts and shows the problem.
/// </summary>
public sealed partial class BudgetFileStartup(
    IBudgetFileService files,
    IAppSettingsStore settings,
    IDataDirectory dataDirectory,
    AppSession session,
    ILogger<BudgetFileStartup> logger)
{
    /// <summary>Opens (or creates) the initial budget file and records the outcome in <see cref="AppSession"/>.</summary>
    public async Task OpenInitialFileAsync(CancellationToken ct)
    {
        dataDirectory.EnsureCreated();
        var last = settings.Current.LastBudgetFile;
        var candidate = !string.IsNullOrWhiteSpace(last) && File.Exists(last) ? last : dataDirectory.DefaultBudgetFile;

        try
        {
            var info = await files.OpenOrCreateAsync(candidate, ct).ConfigureAwait(false);
            Remember(info, info.Created ? Strings.Shell_StatusCreated : Strings.Shell_StatusOpened);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            LogOpenFailed(logger, ex);
            if (!string.Equals(candidate, dataDirectory.DefaultBudgetFile, StringComparison.Ordinal))
            {
                try
                {
                    var fallback = await files.OpenOrCreateAsync(dataDirectory.DefaultBudgetFile, ct).ConfigureAwait(false);
                    Remember(fallback, Strings.Shell_StatusOpenedFallback);
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
    }

    private void Remember(BudgetFileInfo info, string messageFormat)
    {
        session.BudgetFile = info;
        session.StartupMessage = string.Format(CultureInfo.CurrentCulture, messageFormat, info.FileName);
        settings.Update(s => s with { LastBudgetFile = info.Path });
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Opening the budget file failed")]
    private static partial void LogOpenFailed(ILogger logger, Exception exception);
}
