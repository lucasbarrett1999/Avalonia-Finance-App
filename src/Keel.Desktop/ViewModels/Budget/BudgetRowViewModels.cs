using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Budget;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;
using Keel.Domain.Budgeting;

namespace Keel.Desktop.ViewModels.Budget;

/// <summary>A column of the budget grid (keyboard cell navigation, PRD 9.3).</summary>
public enum BudgetColumn
{
    /// <summary>Category or group name.</summary>
    Name,

    /// <summary>Assigned (editable on category rows).</summary>
    Assigned,

    /// <summary>Activity (opens the filtered register).</summary>
    Activity,

    /// <summary>Available (drag to move money).</summary>
    Available,
}

/// <summary>The quick-assign actions of F-BUD-5.</summary>
public enum QuickAssignKind
{
    /// <summary>Assigned last month.</summary>
    AssignedLastMonth,

    /// <summary>Spent last month.</summary>
    SpentLastMonth,

    /// <summary>Average assigned over three months.</summary>
    AverageAssigned,

    /// <summary>Average spent over three months.</summary>
    AverageSpent,

    /// <summary>What the target needs.</summary>
    FundTarget,

    /// <summary>Zero.</summary>
    ResetToZero,
}

/// <summary>A row of the budget grid: a group row or a category row. Rows are updated in place when the month or the numbers change.</summary>
public abstract partial class BudgetRowViewModel : ObservableObject
{
    /// <summary>Creates a row.</summary>
    protected BudgetRowViewModel(Guid id, string name)
    {
        Id = id;
        Name = name;
        Months = [new(this, 0), new(this, 1), new(this, 2)];
    }

    /// <summary>The three months of the three-month view (ADR 0090).</summary>
    public IReadOnlyList<BudgetMonthCellViewModel> Months { get; }

    /// <summary>Group or category id.</summary>
    public Guid Id { get; }

    /// <summary>Display name.</summary>
    [ObservableProperty]
    public partial string Name { get; protected set; }

    /// <summary>Whether this is a group row.</summary>
    public abstract bool IsGroup { get; }

    /// <summary>Assigned text.</summary>
    [ObservableProperty]
    public partial string AssignedText { get; protected set; } = string.Empty;

    /// <summary>Activity text.</summary>
    [ObservableProperty]
    public partial string ActivityText { get; protected set; } = string.Empty;

    /// <summary>Available text.</summary>
    [ObservableProperty]
    public partial string AvailableText { get; protected set; } = string.Empty;

    /// <summary>The selected cell of this row, or null when the row is not selected.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSelected), nameof(IsNameSelected), nameof(IsAssignedSelected), nameof(IsActivitySelected), nameof(IsAvailableSelected))]
    public partial BudgetColumn? SelectedColumn { get; set; }

    /// <summary>Whether the row holds the cell cursor.</summary>
    public bool IsSelected => SelectedColumn is not null;

    /// <summary>Name cell selected.</summary>
    public bool IsNameSelected => SelectedColumn == BudgetColumn.Name;

    /// <summary>Assigned cell selected.</summary>
    public bool IsAssignedSelected => SelectedColumn == BudgetColumn.Assigned;

    /// <summary>Activity cell selected.</summary>
    public bool IsActivitySelected => SelectedColumn == BudgetColumn.Activity;

    /// <summary>Available cell selected.</summary>
    public bool IsAvailableSelected => SelectedColumn == BudgetColumn.Available;

    /// <summary>Tells the month cells that the cursor or the editor moved.</summary>
    protected void NotifyMonthCells()
    {
        foreach (var cell in Months)
        {
            cell.NotifyCursor();
        }
    }

    partial void OnSelectedColumnChanged(BudgetColumn? value) => NotifyMonthCells();
}

/// <summary>A group row: collapsible, with totals of its visible categories (6.4.3).</summary>
public sealed partial class BudgetGroupRowViewModel : BudgetRowViewModel
{
    /// <summary>Creates the row.</summary>
    public BudgetGroupRowViewModel(BudgetGroupDto group)
        : base(group.Id, group.Name)
    {
        IsSystem = group.IsSystem;
    }

    /// <inheritdoc />
    public override bool IsGroup => true;

    /// <summary>System group (Credit Card Payments).</summary>
    public bool IsSystem { get; }

    /// <summary>Category rows of the group (visible ones only).</summary>
    public List<BudgetCategoryRowViewModel> Children { get; } = [];

    /// <summary>Whether the category rows are shown.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ExpandLabel))]
    public partial bool IsExpanded { get; set; } = true;

    /// <summary>Accessible name of the expander.</summary>
    public string ExpandLabel => LedgerText.Format(IsExpanded ? Strings.Budget_CollapseGroup : Strings.Budget_ExpandGroup, Name);

    /// <summary>Accessible description of the row.</summary>
    public string AutomationName => LedgerText.Format(Strings.Budget_GroupAutomation, Name, AssignedText, ActivityText, AvailableText);

    /// <summary>Takes the numbers of a month.</summary>
    public void Update(BudgetGroupDto group)
    {
        Name = group.Name;
        AssignedText = LedgerText.Money(group.Assigned.Amount, group.Assigned.Currency);
        ActivityText = LedgerText.Money(group.Activity.Amount, group.Activity.Currency);
        AvailableText = LedgerText.Money(group.Available.Amount, group.Available.Currency);
        OnPropertyChanged(nameof(AutomationName));
    }
}

/// <summary>A category row: Name (with target badge), Assigned, Activity, Available pill (PRD 9.3).</summary>
public sealed partial class BudgetCategoryRowViewModel : BudgetRowViewModel
{
    private readonly BudgetViewModel? _page;

    /// <summary>Creates the row.</summary>
    public BudgetCategoryRowViewModel(BudgetGroupRowViewModel group, BudgetCategoryDto category, BudgetViewModel? page = null)
        : base(category.Id, category.Name)
    {
        _page = page;
        Group = group;
        Kind = category.Kind;
        Currency = category.Assigned.Currency;
    }

    /// <inheritdoc />
    public override bool IsGroup => false;

    /// <summary>The group row.</summary>
    public BudgetGroupRowViewModel Group { get; }

    /// <summary>Regular or Credit Card Payment.</summary>
    public BudgetCategoryKind Kind { get; }

    /// <summary>Whether this is a Credit Card Payment category.</summary>
    public bool IsCardPayment => Kind == BudgetCategoryKind.CreditCardPayment;

    /// <summary>Budget currency.</summary>
    [ObservableProperty]
    public partial string Currency { get; private set; }

    /// <summary>The month's numbers.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Assigned), nameof(Activity), nameof(Available), nameof(Carry), nameof(IsPositive), nameof(IsZero), nameof(IsCreditOverspent),
        nameof(IsCashOverspent), nameof(IsOverspent), nameof(Target), nameof(HasTarget), nameof(IsUnderfunded), nameof(IsFunded), nameof(TargetBadgeText),
        nameof(TargetToolTip), nameof(CardPayment), nameof(HasCardDetail), nameof(CardDetailText), nameof(IsCardUncovered), nameof(AutomationName), nameof(AvailableStateText), nameof(FlexTag), nameof(Flex))]
    public partial BudgetCategoryDto? Data { get; private set; }

    /// <summary>Assigned in minor units.</summary>
    public long Assigned => Data?.Assigned.Amount ?? 0;

    /// <summary>Activity in minor units.</summary>
    public long Activity => Data?.Activity.Amount ?? 0;

    /// <summary>Available in minor units.</summary>
    public long Available => Data?.Available.Amount ?? 0;

    /// <summary>Carried in from last month.</summary>
    public long Carry => Data?.Carry.Amount ?? 0;

    /// <summary>Available &gt; 0 (green pill).</summary>
    public bool IsPositive => Available > 0;

    /// <summary>Available = 0 (gray pill).</summary>
    public bool IsZero => Available == 0;

    /// <summary>Credit-only overspending (yellow pill, 6.4.4).</summary>
    public bool IsCreditOverspent => Available < 0 && Data?.Overspending == OverspendingKind.Credit;

    /// <summary>Cash overspending (red pill, 6.4.4).</summary>
    public bool IsCashOverspent => Available < 0 && !IsCreditOverspent;

    /// <summary>Any overspending.</summary>
    public bool IsOverspent => Available < 0;

    /// <summary>Screen-reader text for the pill state (colour is never the only signal).</summary>
    public string AvailableStateText => IsCashOverspent ? Strings.Budget_StateCashOverspent
        : IsCreditOverspent ? Strings.Budget_StateCreditOverspent
        : IsPositive ? Strings.Budget_StateFunded
        : Strings.Budget_StateEmpty;

    /// <summary>Target progress.</summary>
    public TargetProgressDto? Target => Data?.Target;

    /// <summary>Whether a target exists.</summary>
    public bool HasTarget => Target is not null;

    /// <summary>Target underfunded this month.</summary>
    public bool IsUnderfunded => Target is { Underfunded.Amount: > 0 };

    /// <summary>Target met this month.</summary>
    public bool IsFunded => Target is { Underfunded.Amount: 0 };

    /// <summary>Badge next to the name: "Needs $50.00" or "Funded".</summary>
    public string TargetBadgeText => Target switch
    {
        null => string.Empty,
        { IsComplete: true } => Strings.Budget_TargetComplete,
        { Underfunded.Amount: > 0 } t => LedgerText.Format(Strings.Budget_TargetNeeds, LedgerText.Money(t.Underfunded.Amount, t.Underfunded.Currency)),
        _ => Strings.Budget_TargetFunded,
    };

    /// <summary>Badge tooltip: the target and what it asks for this month.</summary>
    public string? TargetToolTip => Target is { } t
        ? LedgerText.Format(Strings.Budget_TargetTip, BudgetText.TargetSummary(t), LedgerText.Money(t.NeededThisMonth.Amount, t.NeededThisMonth.Currency))
        : null;

    /// <summary>Card details of a Credit Card Payment row.</summary>
    public CardPaymentDto? CardPayment => Data?.CardPayment;

    /// <summary>Whether the card line is shown.</summary>
    public bool HasCardDetail => CardPayment is not null;

    /// <summary>"Balance −$350.00 · $50.00 not yet covered" (6.4.5).</summary>
    public string CardDetailText => CardPayment is { } c ? BudgetText.CardDetail(c) : string.Empty;

    /// <summary>Whether part of the card balance is not covered by Available.</summary>
    public bool IsCardUncovered => CardPayment is { Difference.Amount: < 0 };

    /// <summary>The Assigned value being edited.</summary>
    [ObservableProperty]
    public partial long EditValue { get; set; }

    /// <summary>Whether the Assigned cell is in edit mode.</summary>
    [ObservableProperty]
    public partial bool IsEditing { get; set; }

    /// <summary>The user's Flex-mode tag (Unset: automatic, F-BUD-6).</summary>
    public FlexKind FlexTag => Data?.FlexTag ?? FlexKind.Unset;

    /// <summary>The kind the category counts as in the Flex view.</summary>
    public FlexKind Flex => Data?.Flex ?? FlexKind.Unset;

    /// <summary>Whether a drag is hovering this row (drop target highlight).</summary>
    [ObservableProperty]
    public partial bool IsDropTarget { get; set; }

    /// <summary>Accessible description of the row.</summary>
    public string AutomationName => LedgerText.Format(Strings.Budget_CategoryAutomation, Name, AssignedText, ActivityText, AvailableText, AvailableStateText);

    /// <summary>Context menu: a quick-assign action (F-BUD-5).</summary>
    [RelayCommand]
    public Task QuickAssignAsync(QuickAssignKind kind) => _page?.QuickAssignAsync(this, kind) ?? Task.CompletedTask;

    /// <summary>Context menu: move money from (or, when overspent, to) this category.</summary>
    [RelayCommand]
    public Task MoveMoneyAsync() => _page?.MoveMoneyAsync(Id) ?? Task.CompletedTask;

    /// <summary>Context menu: edit the target in the inspector.</summary>
    [RelayCommand]
    public void EditTarget()
    {
        if (_page is { } page)
        {
            page.Select(this, BudgetColumn.Name);
            page.SetTarget();
        }
    }

    /// <summary>Context menu: show this month's transactions.</summary>
    [RelayCommand]
    public void ShowTransactions() => _page?.OpenActivity(this);

    /// <summary>Shows a just-committed Assigned value until the recomputed month arrives.</summary>
    public void ShowPendingAssigned(long value)
    {
        AssignedText = LedgerText.Money(value, Currency);
        foreach (var cell in Months.Where(c => c.IsActive))
        {
            cell.ShowPendingAssigned(value, Currency);
        }
    }

    partial void OnIsEditingChanged(bool value) => NotifyMonthCells();

    /// <summary>Takes the numbers of a month.</summary>
    public void Update(BudgetCategoryDto category)
    {
        Name = category.Name;
        Currency = category.Assigned.Currency;
        AssignedText = LedgerText.Money(category.Assigned.Amount, Currency);
        ActivityText = LedgerText.Money(category.Activity.Amount, Currency);
        AvailableText = LedgerText.Money(category.Available.Amount, Currency);
        Data = category;
        if (!IsEditing)
        {
            EditValue = category.Assigned.Amount;
        }
    }
}
