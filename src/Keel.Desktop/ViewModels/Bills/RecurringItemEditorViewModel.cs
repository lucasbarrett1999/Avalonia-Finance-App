using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Ledger;
using Keel.Application.Payees;
using Keel.Application.Recurring;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Bills;

/// <summary>
/// "Add recurring item" and "Edit recurring item" (F-REC-1): payee, account, cadence, amount (outflow or
/// inflow), next date, category and the subscription flag.
/// </summary>
public sealed partial class RecurringItemEditorViewModel : DialogViewModel
{
    private readonly IRecurringService _recurring;
    private readonly IPayeeService _payees;
    private readonly RecurringItemDto? _existing;

    /// <summary>Creates the dialog; <paramref name="existing"/> null adds a new item.</summary>
    public RecurringItemEditorViewModel(
        IRecurringService recurring,
        IPayeeService payees,
        IReadOnlyList<AccountOption> accounts,
        IReadOnlyList<CategoryOption> categories,
        DateOnly today,
        RecurringItemDto? existing = null)
    {
        _recurring = recurring;
        _payees = payees;
        _existing = existing;
        Accounts = [AnyAccount, .. accounts.Where(a => !a.IsClosed || a.Id == existing?.AccountId)];
        Categories = [NoCategory, .. categories];
        Cadences = Enum.GetValues<RecurrenceCadence>().Select(c => new Choice<RecurrenceCadence>(c, BillsFormat.Cadence(c))).ToList();
        Cadence = Cadences.First(c => c.Value == (existing?.Cadence ?? RecurrenceCadence.Monthly));
        Account = Accounts.FirstOrDefault(a => a.Id == existing?.AccountId) ?? (Accounts.Count > 1 ? Accounts[1] : AnyAccount);
        Category = Categories.FirstOrDefault(c => c.Id == existing?.CategoryId) ?? NoCategory;
        NextDate = (existing?.NextExpectedDate ?? today).ToDateTime(TimeOnly.MinValue);
        if (existing is not null)
        {
            Payee = existing.PayeeName;
            IsInflow = existing.ExpectedAmount.Amount > 0;
            Amount = Math.Abs(existing.ExpectedAmount.Amount);
            IsSubscription = existing.IsSubscription;
        }
    }

    /// <summary>"Any account" (an item not tied to one account).</summary>
    public static AccountOption AnyAccount { get; } = new(Guid.Empty, Strings.Bills_AnyAccount, true, false, Keel.Domain.Currency.Default);

    /// <summary>"No category".</summary>
    public static CategoryOption NoCategory { get; } = new(null, Strings.Bills_NoCategory, string.Empty);

    /// <inheritdoc />
    public override string Title => _existing is null ? Strings.Bills_AddItemTitle : Strings.Bills_EditItemTitle;

    /// <summary>Whether this adds a new item.</summary>
    public bool IsNew => _existing is null;

    /// <summary>The saved item.</summary>
    public RecurringItemDto? Result { get; private set; }

    /// <summary>Account choices.</summary>
    public IReadOnlyList<AccountOption> Accounts { get; }

    /// <summary>Category choices.</summary>
    public IReadOnlyList<CategoryOption> Categories { get; }

    /// <summary>Cadence choices.</summary>
    public IReadOnlyList<Choice<RecurrenceCadence>> Cadences { get; }

    /// <summary>Payee name.</summary>
    [ObservableProperty]
    public partial string? Payee { get; set; }

    /// <summary>Account.</summary>
    [ObservableProperty]
    public partial AccountOption Account { get; set; }

    /// <summary>Cadence.</summary>
    [ObservableProperty]
    public partial Choice<RecurrenceCadence> Cadence { get; set; }

    /// <summary>Amount as a positive number.</summary>
    [ObservableProperty]
    public partial long Amount { get; set; }

    /// <summary>Money comes in (a paycheck) rather than goes out.</summary>
    [ObservableProperty]
    public partial bool IsInflow { get; set; }

    /// <summary>Next expected date.</summary>
    [ObservableProperty]
    public partial DateTime? NextDate { get; set; }

    /// <summary>Category.</summary>
    [ObservableProperty]
    public partial CategoryOption Category { get; set; }

    /// <summary>Subscription rather than bill.</summary>
    [ObservableProperty]
    public partial bool IsSubscription { get; set; }

    /// <summary>Currency of the amount box.</summary>
    public string Currency => Account.Currency;

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        if (string.IsNullOrWhiteSpace(Payee))
        {
            Error = Strings.Bills_ErrorPayee;
            return false;
        }

        if (Amount <= 0)
        {
            Error = Strings.Bills_ErrorAmount;
            return false;
        }

        if (NextDate is not { } next)
        {
            Error = Strings.Bills_ErrorDate;
            return false;
        }

        try
        {
            var payee = await _payees.GetOrCreateAsync(Payee.Trim(), CancellationToken.None);
            var edit = new RecurringItemEdit(
                payee.Id,
                Account.Id == Guid.Empty ? null : Account.Id,
                Cadence.Value,
                IsInflow ? Amount : -Amount,
                DateOnly.FromDateTime(next),
                Category.Id,
                IsSubscription);
            Result = _existing is null
                ? await _recurring.CreateAsync(edit, CancellationToken.None)
                : await _recurring.UpdateAsync(_existing.Id, edit, CancellationToken.None);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            Error = ex.Message;
            return false;
        }
    }

    partial void OnAccountChanged(AccountOption value) => OnPropertyChanged(nameof(Currency));
}
