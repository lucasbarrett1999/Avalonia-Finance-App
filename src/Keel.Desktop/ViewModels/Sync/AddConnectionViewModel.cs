using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.ViewModels.Sync;

/// <summary>Where the add-connection dialog is.</summary>
public enum AddConnectionStep
{
    /// <summary>Choosing a provider (or, for a reconnect, confirming).</summary>
    Choose,

    /// <summary>Waiting for the user to finish in the browser.</summary>
    Waiting,

    /// <summary>The link failed; the user can try again.</summary>
    Failed,
}

/// <summary>
/// Add a bank connection (F-SET-3), or reconnect one (Plaid update mode): choose a provider, open
/// Hosted Link in the system browser, wait (with cancel) while Keel polls for the result. The
/// waiting state shows the link so it can be opened again. Closing the dialog cancels the wait.
/// </summary>
public sealed partial class AddConnectionViewModel : DialogViewModel
{
    private readonly ISyncService _sync;
    private readonly IBrowserLauncher _browser;
    private readonly Guid? _reconnectId;
    private readonly string _institution;
    private CancellationTokenSource? _cts;

    /// <summary>Creates the dialog for a new connection.</summary>
    public AddConnectionViewModel(ISyncService sync, IBrowserLauncher browser, IReadOnlyList<ProviderChoice> providers)
        : this(sync, browser, null, string.Empty)
    {
        ArgumentNullException.ThrowIfNull(providers);
        Providers = providers;
        SelectedProvider = providers.FirstOrDefault(p => p.IsReady) ?? providers.FirstOrDefault();
    }

    /// <summary>Creates the dialog for a reconnect.</summary>
    public AddConnectionViewModel(ISyncService sync, IBrowserLauncher browser, Guid? reconnectConnectionId, string institutionName)
    {
        _sync = sync;
        _browser = browser;
        _reconnectId = reconnectConnectionId;
        _institution = institutionName;
        _ = Completion.ContinueWith(_ => _cts?.Cancel(), TaskScheduler.Default);
    }

    /// <inheritdoc />
    public override string Title => IsReconnect ? LedgerText.Format(Strings.AddConnection_ReconnectTitle, _institution) : Strings.AddConnection_Title;

    /// <summary>Whether this repairs an existing connection.</summary>
    public bool IsReconnect => _reconnectId is not null;

    /// <summary>Providers offered (empty for a reconnect).</summary>
    public IReadOnlyList<ProviderChoice> Providers { get; } = [];

    /// <summary>The chosen provider.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanStart), nameof(ProviderHint))]
    public partial ProviderChoice? SelectedProvider { get; set; }

    /// <summary>Hint when the chosen provider is not set up.</summary>
    public string? ProviderHint => SelectedProvider is { IsReady: false } p ? p.NotReadyHint : null;

    /// <summary>Whether the link can start.</summary>
    public bool CanStart => IsReconnect || SelectedProvider is { IsReady: true };

    /// <summary>Current step.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsChoosing), nameof(IsWaiting), nameof(IsFailed), nameof(ConfirmText))]
    public partial AddConnectionStep Step { get; private set; }

    /// <summary>Choosing a provider.</summary>
    public bool IsChoosing => Step == AddConnectionStep.Choose;

    /// <summary>Waiting for the browser.</summary>
    public bool IsWaiting => Step == AddConnectionStep.Waiting;

    /// <summary>The link failed.</summary>
    public bool IsFailed => Step == AddConnectionStep.Failed;

    /// <summary>Label of the primary button.</summary>
    public string ConfirmText => IsFailed ? Strings.AddConnection_TryAgain : Strings.AddConnection_Continue;

    /// <summary>The Hosted Link URL while waiting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLinkUrl), nameof(WaitingMessage))]
    public partial string? LinkUrl { get; private set; }

    /// <summary>Whether the link runs in the browser (SimpleFIN setup tokens do not).</summary>
    public bool HasLinkUrl => LinkUrl is not null;

    /// <summary>Whether the browser could not be opened (the user copies the link instead).</summary>
    [ObservableProperty]
    public partial bool BrowserFailed { get; private set; }

    /// <summary>What the user is waiting for.</summary>
    public string WaitingMessage => IsReconnect ? Strings.AddConnection_WaitingReconnect
        : HasLinkUrl ? Strings.AddConnection_Waiting
        : Strings.AddConnection_WaitingNoBrowser;

    /// <summary>The finished link (new connections).</summary>
    public PendingConnection? Pending { get; private set; }

    /// <summary>The repaired connection (reconnects).</summary>
    public SyncConnectionDto? Reconnected { get; private set; }

    /// <summary>Raised when the waiting state begins (tests finish the fake link then).</summary>
    public event EventHandler<LinkSession>? LinkStarted;

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        if (!CanStart || IsWaiting)
        {
            return false;
        }

        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        try
        {
            var session = _reconnectId is { } id
                ? await _sync.BeginReconnectAsync(id, ct)
                : await _sync.BeginLinkAsync(SelectedProvider!.ProviderId, ct);
            if (session.OpensBrowser)
            {
                LinkUrl = session.LinkUrl.ToString();
                BrowserFailed = !await _browser.OpenAsync(session.LinkUrl);
            }

            Step = AddConnectionStep.Waiting;
            LinkStarted?.Invoke(this, session);
            if (_reconnectId is not null)
            {
                Reconnected = await _sync.CompleteReconnectAsync(session, ct);
            }
            else
            {
                Pending = await _sync.CompleteLinkAsync(session, ct);
            }

            return true;
        }
        catch (BankProviderException ex)
        {
            Step = AddConnectionStep.Failed;
            Error = SyncText.ErrorSentence(ex.Code);
            return false;
        }
        catch (SecretStoreException ex)
        {
            Step = AddConnectionStep.Failed;
            Error = LedgerText.Format(Strings.Connections_SecretStoreError, ex.Message);
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    [RelayCommand]
    private async Task OpenBrowserAgainAsync()
    {
        if (LinkUrl is { } url)
        {
            BrowserFailed = !await _browser.OpenAsync(new Uri(url));
        }
    }
}
