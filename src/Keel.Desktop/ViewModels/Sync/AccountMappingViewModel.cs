using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Accounts;
using Keel.Application.Ledger;
using Keel.Application.Sync;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Sync;

/// <summary>
/// Maps the accounts of a finished link to Keel accounts (F-TXN-3): create a new account, link an
/// existing unlinked account (suggested by type and name or mask), or skip. Saving creates the
/// connection; cancelling removes the link at the provider.
/// </summary>
public sealed partial class AccountMappingViewModel : DialogViewModel
{
    private readonly ISyncService _sync;
    private readonly PendingConnection _pending;

    /// <summary>Creates the dialog.</summary>
    public AccountMappingViewModel(ISyncService sync, PendingConnection pending, IReadOnlyList<AccountDto> accounts)
    {
        ArgumentNullException.ThrowIfNull(pending);
        ArgumentNullException.ThrowIfNull(accounts);
        _sync = sync;
        _pending = pending;
        var types = Enum.GetValues<AccountType>().Select(t => new Choice<AccountType>(t, LedgerText.AccountType(t))).ToList();
        var candidates = accounts.Where(a => !a.IsClosed && a.SyncStatus is null).ToList();
        foreach (var account in pending.Accounts)
        {
            Rows.Add(new AccountMappingRowViewModel(account, candidates, types));
        }
    }

    /// <inheritdoc />
    public override string Title => LedgerText.Format(Strings.Mapping_Title, _pending.InstitutionName);

    /// <inheritdoc />
    public override double PreferredMaxWidth => 860;

    /// <summary>Institution.</summary>
    public string InstitutionName => _pending.InstitutionName;

    /// <summary>One row per provider account.</summary>
    public ObservableCollection<AccountMappingRowViewModel> Rows { get; } = [];

    /// <summary>Whether the institution returned no accounts.</summary>
    public bool HasNoAccounts => Rows.Count == 0;

    /// <summary>The saved connection.</summary>
    public SyncConnectionDto? Result { get; private set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        var choices = Rows.Select(r => r.ToChoice()).ToList();
        if (choices.All(c => c.Action == AccountLinkAction.Skip))
        {
            Error = Strings.Mapping_ErrorNothingChosen;
            return false;
        }

        var linked = choices.Where(c => c.Action == AccountLinkAction.LinkExisting).Select(c => c.ExistingAccountId).ToList();
        if (linked.Distinct().Count() != linked.Count)
        {
            Error = Strings.Mapping_ErrorSameAccountTwice;
            return false;
        }

        if (choices.Any(c => c.Action == AccountLinkAction.CreateNew && string.IsNullOrWhiteSpace(c.NewName)))
        {
            Error = Strings.Mapping_ErrorNameRequired;
            return false;
        }

        try
        {
            Result = await _sync.SaveLinkAsync(_pending, choices, CancellationToken.None);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Error = LedgerText.Format(Strings.Sync_Failed, ex.Message);
            return false;
        }
    }
}

/// <summary>What a provider account can be mapped to.</summary>
/// <param name="Action">Create, link, or skip.</param>
/// <param name="ExistingAccountId">The existing account for a link.</param>
/// <param name="Label">Display text.</param>
public sealed record MappingOption(AccountLinkAction Action, Guid? ExistingAccountId, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>One provider account in the mapping dialog.</summary>
public sealed partial class AccountMappingRowViewModel : ObservableObject
{
    private readonly PendingAccount _account;

    /// <summary>Creates the row with its options and the suggested choice.</summary>
    public AccountMappingRowViewModel(PendingAccount account, IReadOnlyList<AccountDto> candidates, IReadOnlyList<Choice<AccountType>> types)
    {
        ArgumentNullException.ThrowIfNull(account);
        _account = account;
        var currency = account.Account.Currency;
        Options =
        [
            new MappingOption(AccountLinkAction.CreateNew, null, Strings.Mapping_CreateNew),
            .. candidates.Where(c => c.Balance.Currency == currency)
                .Select(c => new MappingOption(AccountLinkAction.LinkExisting, c.Id, LedgerText.Format(Strings.Mapping_LinkTo, c.Name))),
            new MappingOption(AccountLinkAction.Skip, null, Strings.Mapping_Skip),
        ];
        SelectedOption = Options.FirstOrDefault(o => o.ExistingAccountId is { } id && id == account.SuggestedExistingAccountId) ?? Options[0];
        Types = types;
        SelectedType = types.First(t => t.Value == account.Account.SuggestedType);
        NewName = account.Account.Name;
    }

    /// <summary>Provider account name.</summary>
    public string Name => _account.Account.Name;

    /// <summary>"•••• 1234", or empty.</summary>
    public string MaskText => _account.Account.Mask is { Length: > 0 } mask ? LedgerText.Format(Strings.Mapping_Mask, mask) : string.Empty;

    /// <summary>Reported balance, or empty.</summary>
    public string BalanceText => _account.Balance is { } b ? LedgerText.Money(b.Current, b.Currency) : string.Empty;

    /// <summary>Suggested type name.</summary>
    public string TypeText => LedgerText.AccountType(_account.Account.SuggestedType);

    /// <summary>Screen-reader name of the row's choice box.</summary>
    public string AutomationName => LedgerText.Format(Strings.Mapping_ChoiceName, Name);

    /// <summary>Create, link to each candidate, skip.</summary>
    public IReadOnlyList<MappingOption> Options { get; }

    /// <summary>The chosen option.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCreatingNew))]
    public partial MappingOption SelectedOption { get; set; }

    /// <summary>Whether a new account is created (shows name and type).</summary>
    public bool IsCreatingNew => SelectedOption.Action == AccountLinkAction.CreateNew;

    /// <summary>Name of the new account.</summary>
    [ObservableProperty]
    public partial string NewName { get; set; }

    /// <summary>Account types.</summary>
    public IReadOnlyList<Choice<AccountType>> Types { get; }

    /// <summary>Type of the new account.</summary>
    [ObservableProperty]
    public partial Choice<AccountType> SelectedType { get; set; }

    /// <summary>The choice sent to the sync service.</summary>
    public AccountLinkChoice ToChoice() => new(
        _account.Account.ProviderAccountId,
        SelectedOption.Action,
        SelectedOption.ExistingAccountId,
        NewName?.Trim(),
        SelectedType.Value);
}
