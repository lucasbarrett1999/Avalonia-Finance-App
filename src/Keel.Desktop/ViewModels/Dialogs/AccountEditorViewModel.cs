using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Accounts;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Dialogs;

/// <summary>
/// "Add account" and "Edit account" (F-ACC-1): name, type, currency, on-budget flag (Savings and
/// Cash only), opening balance and date, notes, and for liabilities the interest rate and minimum
/// payment of the debt payoff planner (F-GOAL-2); editing also closes or reopens the account.
/// </summary>
public sealed partial class AccountEditorViewModel : DialogViewModel
{
    private readonly IAccountService _accounts;
    private readonly AccountDto? _existing;

    /// <summary>Creates the dialog; <paramref name="existing"/> null means "Add account".</summary>
    public AccountEditorViewModel(IAccountService accounts, AccountDto? existing = null)
    {
        _accounts = accounts;
        _existing = existing;
        Types = AccountTypeInfo.All.Select(t => new Choice<AccountType>(t, LedgerText.AccountType(t))).ToList();
        SelectedType = Types[0];
        Currency = Keel.Domain.Currency.Default;
        OpeningDate = DateTime.Today;
        if (existing is not null)
        {
            Name = existing.Name;
            SelectedType = Types.First(t => t.Value == existing.Type);
            Currency = existing.Balance.Currency;
            IsOnBudget = existing.IsOnBudget;
            Notes = existing.Notes;
            OpeningDate = existing.OpeningDate.ToDateTime(TimeOnly.MinValue);
            InterestRateText = existing.InterestRateBps is { } bps ? FormatRate(bps) : null;
            MinimumPayment = existing.MinimumPayment ?? 0;
        }
    }

    /// <inheritdoc />
    public override string Title => IsNew ? Strings.AccountEditor_AddTitle : Strings.AccountEditor_EditTitle;

    /// <summary>Whether this adds a new account.</summary>
    public bool IsNew => _existing is null;

    /// <summary>Whether the existing account is closed.</summary>
    public bool IsClosed => _existing?.IsClosed ?? false;

    /// <summary>The saved or edited account after confirmation.</summary>
    public AccountDto? Result { get; private set; }

    /// <summary>Account types.</summary>
    public IReadOnlyList<Choice<AccountType>> Types { get; }

    /// <summary>Name.</summary>
    [ObservableProperty]
    public partial string? Name { get; set; }

    /// <summary>Type.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanOverrideOnBudget), nameof(BalanceLabel), nameof(TypeHint), nameof(ShowDebtTerms))]
    public partial Choice<AccountType> SelectedType { get; set; }

    /// <summary>Interest rate as typed, in percent (e.g. "19.99"); empty when unknown.</summary>
    [ObservableProperty]
    public partial string? InterestRateText { get; set; }

    /// <summary>Minimum monthly payment in minor units; 0 means not entered.</summary>
    [ObservableProperty]
    public partial long MinimumPayment { get; set; }

    /// <summary>Whether the debt fields show (cards, lines of credit, loans and mortgages).</summary>
    public bool ShowDebtTerms => DebtTerms.AppliesTo(SelectedType.Value);

    /// <summary>ISO currency code.</summary>
    [ObservableProperty]
    public partial string Currency { get; set; }

    /// <summary>On-budget flag.</summary>
    [ObservableProperty]
    public partial bool IsOnBudget { get; set; } = true;

    /// <summary>Opening balance entered as a positive amount (money owed for liabilities).</summary>
    [ObservableProperty]
    public partial long OpeningBalance { get; set; }

    /// <summary>Opening date.</summary>
    [ObservableProperty]
    public partial DateTime? OpeningDate { get; set; }

    /// <summary>Notes.</summary>
    [ObservableProperty]
    public partial string? Notes { get; set; }

    /// <summary>Shown after "Close" when the balance is not zero.</summary>
    [ObservableProperty]
    public partial bool NeedsCloseConfirmation { get; set; }

    /// <summary>Whether the on-budget checkbox is enabled (Savings and Cash; new accounts only).</summary>
    public bool CanOverrideOnBudget => AccountTypeInfo.CanOverrideOnBudget(SelectedType.Value)
        && (IsNew || _existing!.CanOverrideOnBudget);

    /// <summary>Label of the balance field.</summary>
    public string BalanceLabel => AccountTypeInfo.IsLiability(SelectedType.Value) ? Strings.AccountEditor_AmountOwed : Strings.AccountEditor_Balance;

    /// <summary>What the type means for the budget.</summary>
    public string TypeHint => AccountTypeInfo.GroupOf(SelectedType.Value, AccountTypeInfo.IsOnBudgetByDefault(SelectedType.Value)) switch
    {
        AccountGroup.Tracking => Strings.AccountEditor_HintTracking,
        AccountGroup.Credit => Strings.AccountEditor_HintCredit,
        _ => Strings.AccountEditor_HintCash,
    };

    /// <summary>Closing message including the balance.</summary>
    public string CloseConfirmationText => _existing is null ? string.Empty
        : LedgerText.Format(Strings.AccountEditor_CloseWithBalance, _existing.Balance.Format());

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        DebtTerms? debt = null;
        if (ShowDebtTerms)
        {
            if (!TryParseRate(InterestRateText, out var bps))
            {
                Error = Strings.Debt_Editor_RateInvalid;
                return false;
            }

            debt = new DebtTerms(bps, MinimumPayment > 0 ? MinimumPayment : null);
        }

        try
        {
            if (_existing is null)
            {
                var liability = AccountTypeInfo.IsLiability(SelectedType.Value);
                Result = await _accounts.CreateAccountAsync(
                    new CreateAccountRequest(
                        Name ?? string.Empty,
                        SelectedType.Value,
                        (Currency ?? string.Empty).Trim().ToUpperInvariant(),
                        DateOnly.FromDateTime(OpeningDate ?? DateTime.Today),
                        liability ? -Math.Abs(OpeningBalance) : OpeningBalance,
                        CanOverrideOnBudget ? IsOnBudget : null,
                        Notes,
                        debt),
                    CancellationToken.None);
            }
            else
            {
                Result = await _accounts.UpdateAccountAsync(
                    new UpdateAccountRequest(_existing.Id, Name ?? string.Empty, CanOverrideOnBudget ? IsOnBudget : _existing.IsOnBudget, Notes, debt),
                    CancellationToken.None);
            }

            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
    }

    /// <summary>
    /// Parses an annual rate in percent ("19.99", "19,99 %" in a comma locale) to basis points; empty is
    /// "unknown" (null). Rates must be 0–100 with at most two decimals.
    /// </summary>
    public static bool TryParseRate(string? text, out int? bps)
    {
        bps = null;
        var trimmed = text?.Trim().TrimEnd('%').Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return true;
        }

        if (!decimal.TryParse(trimmed, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var percent)
            || percent < 0 || percent > 100 || decimal.Round(percent, 2) != percent)
        {
            return false;
        }

        bps = (int)(percent * 100);
        return true;
    }

    /// <summary>Basis points as a percent for the text box, e.g. 1999 → "19.99".</summary>
    public static string FormatRate(int bps) => (bps / 100m).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture);

    partial void OnSelectedTypeChanged(Choice<AccountType> value)
    {
        if (IsNew)
        {
            IsOnBudget = AccountTypeInfo.IsOnBudgetByDefault(value.Value);
        }
    }

    [RelayCommand]
    private async Task CloseAccountAsync()
    {
        if (_existing is null)
        {
            return;
        }

        try
        {
            await _accounts.CloseAccountAsync(_existing.Id, closeWithBalance: NeedsCloseConfirmation, CancellationToken.None);
            Result = await _accounts.GetAccountAsync(_existing.Id, CancellationToken.None);
            Close(true);
        }
        catch (LedgerValidationException ex) when (ex.Error == LedgerError.AccountHasBalance)
        {
            NeedsCloseConfirmation = true;
            OnPropertyChanged(nameof(CloseConfirmationText));
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
        }
    }

    [RelayCommand]
    private async Task ReopenAccountAsync()
    {
        if (_existing is null)
        {
            return;
        }

        await _accounts.ReopenAccountAsync(_existing.Id, CancellationToken.None);
        Result = await _accounts.GetAccountAsync(_existing.Id, CancellationToken.None);
        Close(true);
    }
}
