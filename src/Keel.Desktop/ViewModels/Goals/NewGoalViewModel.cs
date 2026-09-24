using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Accounts;
using Keel.Application.Goals;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Reports;
using Keel.Domain.Budgeting;

namespace Keel.Desktop.ViewModels.Goals;

/// <summary>A choice of linked account in the goal wizard (null id: none).</summary>
public sealed record GoalAccountOption(Guid? Id, string Name)
{
    /// <inheritdoc />
    public override string ToString() => Name;
}

/// <summary>
/// The "New goal" wizard (PRD 9.7): step 1 name, amount and date; step 2 an optional linked
/// tracking account and a summary. Creates the category (in the Goals group, created on demand)
/// and its savings-balance-by-date target through <see cref="IGoalService"/>.
/// </summary>
public sealed partial class NewGoalViewModel : DialogViewModel
{
    private readonly IGoalService _goals;
    private readonly DateOnly _month;

    /// <summary>Creates the wizard; <paramref name="trackingAccounts"/> are the accounts a goal can be linked to.</summary>
    public NewGoalViewModel(IGoalService goals, IReadOnlyList<AccountDto> trackingAccounts, string currency, DateOnly today)
    {
        ArgumentNullException.ThrowIfNull(trackingAccounts);
        _goals = goals;
        _month = BudgetMonth.Of(today);
        Currency = currency;
        AccountOptions = [new GoalAccountOption(null, Strings.Goals_NoLinkedAccount), .. trackingAccounts.Select(a => new GoalAccountOption(a.Id, a.Name))];
        SelectedAccount = AccountOptions[0];
        TargetDate = today.AddMonths(12).ToDateTime(TimeOnly.MinValue);
    }

    /// <inheritdoc />
    public override string Title => Strings.Goals_NewTitle;

    /// <summary>Currency of the amount.</summary>
    public string Currency { get; }

    /// <summary>Wizard step (1 or 2).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsFirstStep), nameof(IsSecondStep), nameof(StepText), nameof(ConfirmText))]
    public partial int Step { get; private set; } = 1;

    /// <summary>Step 1 is showing.</summary>
    public bool IsFirstStep => Step == 1;

    /// <summary>Step 2 is showing.</summary>
    public bool IsSecondStep => Step == 2;

    /// <summary>"Step 1 of 2".</summary>
    public string StepText => LedgerText.Format(Strings.Goals_StepOf, Step, 2);

    /// <summary>"Next" or "Create goal".</summary>
    public string ConfirmText => IsFirstStep ? Strings.Goals_Next : Strings.Goals_Create;

    /// <summary>Goal name (the category name).</summary>
    [ObservableProperty]
    public partial string? Name { get; set; }

    /// <summary>Target amount in minor units.</summary>
    [ObservableProperty]
    public partial long Amount { get; set; }

    /// <summary>Target date.</summary>
    [ObservableProperty]
    public partial DateTime? TargetDate { get; set; }

    /// <summary>Linked account choices ("None" first).</summary>
    public IReadOnlyList<GoalAccountOption> AccountOptions { get; }

    /// <summary>Whether any tracking account exists to link.</summary>
    public bool HasTrackingAccounts => AccountOptions.Count > 1;

    /// <summary>Selected linked account.</summary>
    [ObservableProperty]
    public partial GoalAccountOption SelectedAccount { get; set; }

    /// <summary>"Set aside $100.00 a month for 12 months to reach $1,200.00 by September 2027."</summary>
    public string SummaryText
    {
        get
        {
            if (TargetDate is not { } date || Amount <= 0)
            {
                return string.Empty;
            }

            var target = DateOnly.FromDateTime(date);
            var months = Math.Max(1, BudgetMonth.Between(_month, target) + 1);
            var monthly = (Amount + months - 1) / months;
            return LedgerText.Format(Strings.Goals_Summary, ReportFormat.Money(monthly, Currency), months,
                ReportFormat.Money(Amount, Currency), target.ToString("MMMM yyyy", System.Globalization.CultureInfo.CurrentCulture));
        }
    }

    /// <summary>The created goal after confirmation.</summary>
    public GoalDto? Result { get; private set; }

    /// <summary>Returns to step 1.</summary>
    [RelayCommand]
    public void Back()
    {
        Error = null;
        Step = 1;
    }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        var name = Name?.Trim();
        if (string.IsNullOrEmpty(name))
        {
            Error = Strings.Goals_ErrorName;
            return false;
        }

        if (Amount <= 0)
        {
            Error = Strings.Goals_ErrorAmount;
            return false;
        }

        if (TargetDate is not { } date || DateOnly.FromDateTime(date) < _month)
        {
            Error = Strings.Goals_ErrorDate;
            return false;
        }

        if (IsFirstStep)
        {
            OnPropertyChanged(nameof(SummaryText));
            Step = 2;
            return false;
        }

        try
        {
            Result = await _goals.CreateGoalAsync(
                new CreateGoalRequest(name, Amount, DateOnly.FromDateTime(date), SelectedAccount?.Id, Strings.Goals_GroupName), _month, CancellationToken.None);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
            return false;
        }
    }
}
