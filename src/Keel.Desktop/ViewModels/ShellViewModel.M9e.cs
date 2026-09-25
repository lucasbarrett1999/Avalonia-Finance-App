using Avalonia.Threading;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Keel.Desktop.ViewModels;

/// <summary>
/// M9 stream E parts of the shell: the passphrase prompt for a locked encrypted file (F-SET-4) and the cold-start
/// measurement for the Stats page (PRD 4).
/// </summary>
public sealed partial class ShellViewModel
{
    private string? _lockedFile;

    /// <summary>An encrypted file waiting for its passphrase (F-SET-4), or null.</summary>
    public string? LockedFile => _lockedFile;

    /// <summary>The passphrase prompt shown on start for a locked file (tests await it).</summary>
    public Task UnlockPrompt { get; private set; } = Task.CompletedTask;

    /// <summary>Stats measurements (null without DI).</summary>
    public StatsInstrumentation? StatsInstrumentation => _services?.GetService<StatsInstrumentation>();

    /// <summary>Asks for the passphrase of the locked file; a cancelled prompt leaves the shell locked with a hint.</summary>
    public async Task UnlockAsync()
    {
        if (_lockedFile is not { } path || _services?.GetService<BudgetSessions>() is not { } sessions)
        {
            return;
        }

        if (!await sessions.PromptUnlockAsync(Dialogs, path))
        {
            Status.Show(LedgerText.Format(Strings.Encrypt_LockedCancelled, Path.GetFileName(path)));
        }
    }

    /// <summary>
    /// The window has opened: once the sidebar and the first page have loaded and a frame is drawn, the window
    /// calls <see cref="StatsInstrumentation.MarkInteractiveAsync"/> for the cold start (PRD 4).
    /// </summary>
    public async Task WhenLoadedAsync()
    {
        await AccountsLoading;
        if (CurrentPage is HomeViewModel home)
        {
            await home.Loading;
        }
    }

    private void InitializeM9e(AppSession session)
    {
        _lockedFile = session.LockedFile;
        if (_lockedFile is not null)
        {
            // Post so the window binds the dialog layer first.
            Dispatcher.UIThread.Post(() => UnlockPrompt = UnlockAsync());
        }
    }
}
