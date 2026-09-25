using Avalonia.Controls;
using Avalonia.Threading;
using Keel.Application.Files;
using Keel.Application.Settings;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.Views;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Keel.Desktop.Services;

/// <summary>
/// The app's budget-file sessions (M8, ADR 0080). A session is one generic host (DI graph, services,
/// view models, undo history) over one open budget file. Opening, creating, moving or restoring a file
/// starts a fresh session and moves the one main window over to its shell, so no screen, cache or undo
/// entry of the previous file survives. The previous session keeps running when the new file cannot be
/// opened. settings.json is shared by every session through one <see cref="IAppSettingsStore"/>.
/// </summary>
public sealed class BudgetSessions : IDisposable
{
    private readonly Func<BudgetStartupOptions, IHost> _createHost;
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Creates the session manager; <paramref name="createHost"/> builds an unstarted host per session.</summary>
    public BudgetSessions(IAppSettingsStore settings, Func<BudgetStartupOptions, IHost> createHost)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(createHost);
        Settings = settings;
        _createHost = createHost;
    }

    /// <summary>The settings store every session shares.</summary>
    public IAppSettingsStore Settings { get; }

    /// <summary>Keys of encrypted files unlocked in this app run (F-SET-4), shared by every session.</summary>
    public BudgetFileKeyRing KeyRing { get; } = new();

    /// <summary>
    /// When the process started (UTC), set by Program.Main so the first session can report its cold start to the
    /// Stats page (PRD 4); consumed once by <see cref="TakeColdStart"/>.
    /// </summary>
    public DateTime? ColdStartedAt { get; set; }

    /// <summary>Returns and clears <see cref="ColdStartedAt"/> (only one session measures a cold start).</summary>
    public DateTime? TakeColdStart()
    {
        var value = ColdStartedAt;
        ColdStartedAt = null;
        return value;
    }

    /// <summary>The running session's host.</summary>
    public IHost? Current { get; private set; }

    /// <summary>The main window, moved from session to session.</summary>
    public ShellWindow? Window { get; private set; }

    /// <summary>Raised on the UI thread after a new session took over.</summary>
    public event EventHandler? Switched;

    /// <summary>Makes <paramref name="host"/> the current session (the first one, built by the composition root).</summary>
    public void Adopt(IHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        Current ??= host;
    }

    /// <summary>Called by the main window so a new session can take it over.</summary>
    public void AttachWindow(ShellWindow window) => Window = window;

    /// <summary>Builds, starts and opens the file of the first session (Program.Main).</summary>
    public IHost Start(BudgetStartupOptions options)
    {
        var host = _createHost(options);
        Current = host;
        host.Start();
        Task.Run(() => host.Services.GetRequiredService<BudgetFileStartup>().OpenInitialFileAsync(CancellationToken.None)).GetAwaiter().GetResult();
        return host;
    }

    /// <summary>
    /// Opens (or creates) <paramref name="path"/> in a new session and moves the window to it. Throws,
    /// leaving the current session untouched, when the file cannot be opened (e.g. made by a newer Keel).
    /// </summary>
    public async Task OpenAsync(string path, BudgetStartupOptions? options = null, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        try
        {
            var host = await StartAsync((options ?? BudgetStartupOptions.Default) with { FilePath = path, Strict = true }, ct).ConfigureAwait(true);
            var previous = Current;
            Switch(previous, host);
            if (previous is not null)
            {
                await StopAsync(previous).ConfigureAwait(true);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // Builds and starts a session host and opens its file; a host that fails is stopped and the error thrown.
    private async Task<IHost> StartAsync(BudgetStartupOptions options, CancellationToken ct)
    {
        var host = _createHost(options);
        try
        {
            await host.StartAsync(ct).ConfigureAwait(true);
            await Task.Run(() => host.Services.GetRequiredService<BudgetFileStartup>().OpenInitialFileAsync(ct), ct).ConfigureAwait(true);
            return host;
        }
        catch
        {
            await StopAsync(host).ConfigureAwait(true);
            throw;
        }
    }

    // Makes the started host current and moves the window to its shell.
    private void Switch(IHost? previous, IHost host)
    {
        Current = host;
        if (Window is { } window)
        {
            window.Attach(host.Services.GetRequiredService<ShellViewModel>(), host.Services.GetRequiredService<WindowPlacementService>());
        }

        if (previous is not null && ReferenceEquals(App.Services, previous.Services))
        {
            // Only the real app publishes its container (tests leave App.Services unset).
            App.Services = host.Services;
        }

        Switched?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// A second launch (PRD 8 single instance): bring the window to the front and, when a file was
    /// passed, open it. Problems go to the status strip.
    /// </summary>
    public async Task ActivateAsync(string? path)
    {
        if (Window is { } window)
        {
            if (window.WindowState == WindowState.Minimized)
            {
                window.WindowState = WindowState.Normal;
            }

            window.Activate();
        }

        if (string.IsNullOrWhiteSpace(path) || Current?.Services.GetService<AppSession>()?.BudgetFile?.Path is { } open && PathsEqual(open, path))
        {
            return;
        }

        try
        {
            await OpenWithPromptAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Current?.Services.GetService<StatusService>()?.Show(
                LedgerText.Format(Resources.Strings.Shell_StatusOpenRequestedFailed, Path.GetFileName(path), ex.Message), isError: true);
        }
    }

    /// <summary>
    /// Opens <paramref name="path"/> like <see cref="OpenAsync"/>; when it is encrypted and no key is known, asks
    /// for the passphrase in the current shell (F-SET-4) and keeps asking until it opens or the user cancels.
    /// Returns false when cancelled. Other failures throw, leaving the current session untouched.
    /// </summary>
    public async Task<bool> OpenWithPromptAsync(string path, BudgetStartupOptions? options = null)
    {
        try
        {
            await OpenAsync(path, options).ConfigureAwait(true);
            return true;
        }
        catch (BudgetFileLockedException) when (options?.Unlock is null && Current?.Services.GetService<DialogService>() is { } dialogs)
        {
            return await PromptUnlockAsync(dialogs, path, options).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Shows the passphrase prompt for the encrypted <paramref name="path"/> in <paramref name="dialogs"/>; each
    /// attempt opens the file in a new session. A wrong passphrase keeps the prompt open with an error.
    /// </summary>
    public Task<bool> PromptUnlockAsync(DialogService dialogs, string path, BudgetStartupOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(dialogs);
        var prompt = new UnlockFileViewModel(path, async (passphrase, remember) =>
        {
            await OpenAsync(path, (options ?? BudgetStartupOptions.Default) with { Unlock = new BudgetFileUnlock(passphrase, remember) }).ConfigureAwait(true);
        });
        return dialogs.ShowAsync(prompt);
    }

    /// <summary>
    /// Encrypts or decrypts the open file <paramref name="path"/> (F-SET-4): stops the current session so nothing
    /// holds the file, converts it in a new session (verified backup first) and opens it there. When the change
    /// fails the file is reopened unchanged and its status strip says why; returns false then.
    /// </summary>
    public async Task<bool> ChangeEncryptionAsync(string path, BudgetFileEncryptionChange change, string message, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(change);
        await _gate.WaitAsync(ct).ConfigureAwait(true);
        var window = Window;
        try
        {
            // The old shell stays on screen until the new one takes over, but its services are gone: no input.
            if (window is not null)
            {
                window.IsEnabled = false;
            }

            var previous = Current;
            if (previous is not null)
            {
                await StopAsync(previous).ConfigureAwait(true);
            }

            var options = new BudgetStartupOptions(path, Strict: true, Message: message, Encryption: change);
            IHost host;
            var succeeded = true;
            try
            {
                host = await StartAsync(options, ct).ConfigureAwait(true);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Reopen the file as it was (the conversion never touches it unless it succeeded).
                succeeded = false;
                var reason = LedgerText.Format(Resources.Strings.Encrypt_ChangeFailed, ex.Message).Replace("{", "{{", StringComparison.Ordinal).Replace("}", "}}", StringComparison.Ordinal);
                var unlock = change.CurrentPassphrase is { } current ? new BudgetFileUnlock(current) : null;
                host = await StartAsync(new BudgetStartupOptions(path, Message: reason, Unlock: unlock), ct).ConfigureAwait(true);
            }

            Switch(previous, host);
            return succeeded;
        }
        finally
        {
            if (window is not null)
            {
                window.IsEnabled = true;
            }

            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (Current is { } host)
        {
            Current = null;
            host.Dispose();
        }

        _gate.Dispose();
    }

    /// <summary>Compares two file paths the way the OS does (case-insensitive except on Linux).</summary>
    public static bool PathsEqual(string a, string b) => string.Equals(
        Path.GetFullPath(a),
        Path.GetFullPath(b),
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    private static async Task StopAsync(IHost host)
    {
        try
        {
            await host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        }
        finally
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                host.Dispose();
            }
            else
            {
                await Dispatcher.UIThread.InvokeAsync(host.Dispose);
            }
        }
    }
}
