using Keel.Application.Sync;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.ViewModels.Sync;

/// <summary>Confirms unlinking a connection: the item is removed at the provider, local transactions stay.</summary>
public sealed class UnlinkConnectionViewModel(ISyncService sync, SyncConnectionDto connection) : DialogViewModel
{
    /// <inheritdoc />
    public override string Title => LedgerText.Format(Strings.Unlink_Title, connection.InstitutionName);

    /// <summary>What happens.</summary>
    public string Message => LedgerText.Format(
        Strings.Unlink_Message,
        connection.InstitutionName,
        connection.Accounts.Count == 0 ? Strings.Unlink_NoAccounts : string.Join(", ", connection.Accounts.Select(a => a.Name)));

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        try
        {
            await sync.UnlinkAsync(connection.Id, CancellationToken.None);
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Error = LedgerText.Format(Strings.Sync_Failed, ex.Message);
            return false;
        }
    }
}
