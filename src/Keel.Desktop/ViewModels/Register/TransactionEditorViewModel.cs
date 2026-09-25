using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Ledger;
using Keel.Application.Payees;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;
using Keel.Domain.Ledger;

namespace Keel.Desktop.ViewModels.Register;

/// <summary>
/// The inline add/edit row of the register (PRD 9.4): date, account (All Accounts), payee with
/// autocomplete (known payees and "Transfer: account" entries), category, memo, outflow, inflow,
/// cleared, and optional split lines. Choosing a known payee on a new row pre-fills its last
/// category and memo. Tags and attachments (F-TXN-8) sit on a second line under the fields.
/// </summary>
public sealed partial class TransactionEditorViewModel : ObservableObject
{
    private readonly IPayeeService _payees;
    private readonly TransactionStatus _originalStatus;
    private bool _suggestionApplied;

    /// <summary>Creates an editor for a new transaction or for <paramref name="existing"/>.</summary>
    public TransactionEditorViewModel(
        IPayeeService payees,
        IReadOnlyList<AccountOption> accounts,
        IReadOnlyList<CategoryOption> categories,
        AccountOption? account,
        TransactionDto? existing,
        bool canChooseAccount,
        IReadOnlyList<string>? knownTags = null,
        AttachmentContext? attachments = null)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(categories);
        _payees = payees;
        Accounts = accounts;
        Categories = categories;
        CanChooseAccount = canChooseAccount;
        Splits.CollectionChanged += (_, _) => OnSplitsChanged();
        Account = account;
        PayeePopulator = PopulatePayeesAsync;
        Tags = new TagEditorViewModel(existing?.Tags ?? [], knownTags ?? []);
        Attachments = attachments is null ? null : new EditorAttachmentsViewModel(attachments, existing?.Id);

        if (existing is null)
        {
            Date = DateTime.Today;
            _originalStatus = TransactionStatus.Uncleared;
            IsApproved = true;
            return;
        }

        Id = existing.Id;
        Account = accounts.FirstOrDefault(a => a.Id == existing.AccountId) ?? account;
        Date = existing.Date.ToDateTime(TimeOnly.MinValue);
        var transfer = existing.TransferAccountId is { } t ? accounts.FirstOrDefault(a => a.Id == t) : null;
        Payee = transfer is not null ? LedgerText.TransferPayee(transfer.Name) : existing.Payee;
        Category = categories.FirstOrDefault(c => c.Id == existing.CategoryId);
        CategoryText = Category?.FullName;
        Memo = existing.Memo;
        Outflow = existing.Amount < 0 ? -existing.Amount : 0;
        Inflow = existing.Amount > 0 ? existing.Amount : 0;
        _originalStatus = existing.Status;
        IsCleared = existing.Status != TransactionStatus.Uncleared;
        IsApproved = existing.IsApproved;
        IsReconciled = existing.Status == TransactionStatus.Reconciled;
        foreach (var split in existing.Splits)
        {
            var line = NewLine();
            line.Category = categories.FirstOrDefault(c => c.Id == split.CategoryId);
            line.CategoryText = line.Category?.FullName;
            line.Memo = split.Memo;
            line.SetAmount(split.Amount);
            Splits.Add(line);
        }

        _suggestionApplied = true;
    }

    /// <summary>Id of the edited transaction; null for a new one.</summary>
    public Guid? Id { get; }

    /// <summary>The tag box (F-TXN-8).</summary>
    public TagEditorViewModel Tags { get; }

    /// <summary>The attachment list (F-TXN-8), or null when attachments are not available.</summary>
    public EditorAttachmentsViewModel? Attachments { get; }

    /// <summary>Whether files can be attached here.</summary>
    public bool CanAttach => Attachments is not null;

    /// <summary>Focus the tag box instead of the payee when the editor opens (the register's T key).</summary>
    public bool FocusTagsOnOpen { get; init; }

    /// <summary>Whether this is a new transaction.</summary>
    public bool IsNew => Id is null;

    /// <summary>Open accounts (All Accounts register and transfer targets).</summary>
    public IReadOnlyList<AccountOption> Accounts { get; }

    /// <summary>Assignable categories.</summary>
    public IReadOnlyList<CategoryOption> Categories { get; }

    /// <summary>Whether the account is chosen in the editor (All Accounts register).</summary>
    public bool CanChooseAccount { get; }

    /// <summary>Autocomplete source for the payee box.</summary>
    public Func<string?, CancellationToken, Task<IEnumerable<object>>> PayeePopulator { get; }

    /// <summary>Split lines; empty for an unsplit transaction.</summary>
    public ObservableCollection<SplitLineViewModel> Splits { get; } = [];

    /// <summary>Account.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCategoryEnabled), nameof(Currency))]
    public partial AccountOption? Account { get; set; }

    /// <summary>Date (the date picker works in <see cref="DateTime"/>).</summary>
    [ObservableProperty]
    public partial DateTime? Date { get; set; }

    /// <summary>Payee text, or "Transfer: account".</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(TransferAccount), nameof(IsTransfer), nameof(IsCategoryEnabled), nameof(CanSplit))]
    public partial string? Payee { get; set; }

    /// <summary>Selected category.</summary>
    [ObservableProperty]
    public partial CategoryOption? Category { get; set; }

    /// <summary>Category text as typed.</summary>
    [ObservableProperty]
    public partial string? CategoryText { get; set; }

    /// <summary>Memo.</summary>
    [ObservableProperty]
    public partial string? Memo { get; set; }

    /// <summary>Outflow in minor units (positive).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingText), nameof(IsBalanced))]
    public partial long Outflow { get; set; }

    /// <summary>Inflow in minor units (positive).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RemainingText), nameof(IsBalanced))]
    public partial long Inflow { get; set; }

    /// <summary>Cleared (or reconciled).</summary>
    [ObservableProperty]
    public partial bool IsCleared { get; set; }

    /// <summary>Whether the transaction is reconciled (amount, date and account are locked).</summary>
    public bool IsReconciled { get; }

    /// <summary>Whether amount, date and account may be edited.</summary>
    public bool IsUnlocked => !IsReconciled;

    /// <summary>Approval flag (kept from the existing row; new manual rows are approved).</summary>
    public bool IsApproved { get; }

    /// <summary>Validation or save error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? Error { get; set; }

    /// <summary>Whether <see cref="Error"/> is set.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Currency of the account.</summary>
    public string Currency => Account?.Currency ?? Keel.Domain.Currency.Default;

    /// <summary>Signed amount.</summary>
    public long Amount => Inflow - Outflow;

    /// <summary>The transfer target named by the payee text, if any.</summary>
    public AccountOption? TransferAccount =>
        string.IsNullOrWhiteSpace(Payee) ? null
        : Accounts.FirstOrDefault(a => a.Id != Account?.Id && string.Equals(LedgerText.TransferPayee(a.Name), Payee.Trim(), StringComparison.CurrentCultureIgnoreCase));

    /// <summary>Whether this is a transfer.</summary>
    public bool IsTransfer => TransferAccount is not null;

    /// <summary>Whether the category box applies (not split; transfers only between on- and off-budget accounts).</summary>
    public bool IsCategoryEnabled => !IsSplit && (TransferAccount is not { } other
        || TransferRules.TransferRequiresCategory(Account?.IsOnBudget ?? true, other.IsOnBudget));

    /// <summary>Whether split lines are in use.</summary>
    public bool IsSplit => Splits.Count > 0;

    /// <summary>Whether the transaction can be split (transfers cannot).</summary>
    public bool CanSplit => !IsTransfer && !IsReconciled;

    /// <summary>What is left to allocate across split lines.</summary>
    public long Remaining => SplitRules.Remaining(Amount, Splits.Select(s => s.Amount));

    /// <summary>Remaining amount as text, e.g. "Remaining: $2.00".</summary>
    public string RemainingText => LedgerText.Format(Strings.Editor_Remaining, LedgerText.Money(Remaining, Currency));

    /// <summary>Whether split lines add up.</summary>
    public bool IsBalanced => Remaining == 0;

    /// <summary>The category chosen or typed: the selection, else an exact or unique match on the text.</summary>
    public static CategoryOption? ResolveCategory(IReadOnlyList<CategoryOption> categories, CategoryOption? selected, string? text)
    {
        ArgumentNullException.ThrowIfNull(categories);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (selected is not null && string.Equals(selected.FullName, text.Trim(), StringComparison.CurrentCultureIgnoreCase))
        {
            return selected;
        }

        var trimmed = text.Trim();
        var exact = categories.Where(c => string.Equals(c.FullName, trimmed, StringComparison.CurrentCultureIgnoreCase)
            || string.Equals(c.Name, trimmed, StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (exact.Count == 1)
        {
            return exact[0];
        }

        var partial = categories.Where(c => c.FullName.Contains(trimmed, StringComparison.CurrentCultureIgnoreCase)).ToList();
        return partial.Count == 1 ? partial[0] : selected;
    }

    /// <summary>Validates and builds the save request; sets <see cref="Error"/> and returns null when invalid.</summary>
    public SaveTransactionRequest? BuildRequest()
    {
        Error = null;
        if (Account is null)
        {
            Error = Strings.Editor_ErrorAccount;
            return null;
        }

        if (Date is not { } date)
        {
            Error = Strings.Editor_ErrorDate;
            return null;
        }

        var category = IsCategoryEnabled ? ResolveCategory(Categories, Category, CategoryText) : null;
        if (IsCategoryEnabled && !string.IsNullOrWhiteSpace(CategoryText) && category is null)
        {
            Error = Strings.Editor_ErrorCategory;
            return null;
        }

        var splits = IsSplit
            ? Splits.Select(s => new SplitLine(s.ResolveCategory()?.Id, s.Memo, s.Amount)).ToList()
            : null;
        var status = IsReconciled ? TransactionStatus.Reconciled
            : IsCleared ? TransactionStatus.Cleared
            : TransactionStatus.Uncleared;
        if (_originalStatus == TransactionStatus.Reconciled && !IsCleared)
        {
            status = TransactionStatus.Uncleared;
        }

        var transfer = TransferAccount;
        return new SaveTransactionRequest(
            Id,
            Account.Id,
            DateOnly.FromDateTime(date),
            Amount,
            transfer is null ? Payee : null,
            category?.Id,
            Memo,
            status,
            IsApproved,
            transfer?.Id,
            splits,
            Tags.Names);
    }

    /// <summary>
    /// When a known payee is chosen on a new row, pre-fills its last category and memo (PRD 9.4).
    /// Runs once per editor, and never overwrites what the user already typed.
    /// </summary>
    public async Task ApplyPayeeSuggestionAsync()
    {
        if (!IsNew || _suggestionApplied || IsTransfer || string.IsNullOrWhiteSpace(Payee))
        {
            return;
        }

        var suggestion = await _payees.GetSuggestionAsync(Payee, Account?.Id, CancellationToken.None);
        if (suggestion is null)
        {
            return;
        }

        _suggestionApplied = true;
        if (string.IsNullOrWhiteSpace(CategoryText) && !IsSplit && suggestion.CategoryId is { } categoryId
            && Categories.FirstOrDefault(c => c.Id == categoryId) is { } option)
        {
            Category = option;
            CategoryText = option.FullName;
        }

        if (string.IsNullOrWhiteSpace(Memo) && !string.IsNullOrWhiteSpace(suggestion.Memo))
        {
            Memo = suggestion.Memo;
        }
    }

    [RelayCommand]
    private void Split()
    {
        if (!CanSplit)
        {
            return;
        }

        if (!IsSplit)
        {
            var first = NewLine();
            first.Category = ResolveCategory(Categories, Category, CategoryText);
            first.CategoryText = first.Category?.FullName;
            first.SetAmount(Amount);
            Splits.Add(first);
        }

        Splits.Add(NewLine());
    }

    [RelayCommand]
    private void RemoveSplitLine(SplitLineViewModel? line)
    {
        if (line is not null)
        {
            Splits.Remove(line);
        }
    }

    private SplitLineViewModel NewLine() => new(Categories, () =>
    {
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(IsBalanced));
    });

    private void OnSplitsChanged()
    {
        OnPropertyChanged(nameof(IsSplit));
        OnPropertyChanged(nameof(IsCategoryEnabled));
        OnPropertyChanged(nameof(Remaining));
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(IsBalanced));
    }

    private async Task<IEnumerable<object>> PopulatePayeesAsync(string? text, CancellationToken ct)
    {
        var search = text?.Trim() ?? string.Empty;
        var payees = await _payees.SearchAsync(search, 8, ct);
        var transfers = Accounts
            .Where(a => a.Id != Account?.Id && !a.IsClosed)
            .Select(a => LedgerText.TransferPayee(a.Name))
            .Where(t => search.Length == 0 || t.Contains(search, StringComparison.CurrentCultureIgnoreCase));
        return payees.Select(p => p.Name).Concat(transfers).Cast<object>().ToList();
    }
}
