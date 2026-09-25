using Avalonia.Controls;
using Avalonia.Threading;
using Keel.Application.Settings;
using Keel.Desktop.ViewModels;
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
            var host = _createHost((options ?? BudgetStartupOptions.Default) with { FilePath = path, Strict = true });
            try
            {
                await host.StartAsync(ct).ConfigureAwait(true);
                await Task.Run(() => host.Services.GetRequiredService<BudgetFileStartup>().OpenInitialFileAsync(ct), ct).ConfigureAwait(true);
            }
            catch
            {
                await StopAsync(host).ConfigureAwait(true);
                throw;
            }

            var previous = Current;
            Current = host;
            if (Window is { } window)
            {
                window.Attach(host.Services.GetRequiredService<ShellViewModel>(), host.Services.GetRequiredService<WindowPlacementService>());
            }

            App.Services = host.Services;
            Switched?.Invoke(this, EventArgs.Empty);
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
            await OpenAsync(path).ConfigureAwait(true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Current?.Services.GetService<StatusService>()?.Show(
                LedgerText.Format(Resources.Strings.Shell_StatusOpenRequestedFailed, Path.GetFileName(path), ex.Message), isError: true);
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
