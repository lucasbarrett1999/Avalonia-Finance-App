using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Desktop.Resources;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain;
using Keel.Domain.Rules;

namespace Keel.Desktop.ViewModels.Rules;

/// <summary>What the rule editor can refer to: categories, accounts and the display currency.</summary>
/// <param name="Categories">Assignable categories.</param>
/// <param name="Accounts">Accounts (closed ones included so existing rules still show them).</param>
/// <param name="Currency">Currency for amount fields.</param>
/// <param name="Validation">What the validator checks references against.</param>
public sealed record RuleEditorContext(
    IReadOnlyList<CategoryOption> Categories,
    IReadOnlyList<AccountOption> Accounts,
    string Currency,
    RuleValidationContext Validation);

/// <summary>Condition kinds the editor offers (every kind the engine supports, ADR 0020).</summary>
public enum ConditionKind
{
    /// <summary>Payee text.</summary>
    Payee,

    /// <summary>Memo text.</summary>
    Memo,

    /// <summary>Amount comparison.</summary>
    Amount,

    /// <summary>Inflow or outflow.</summary>
    Direction,

    /// <summary>Account is one of.</summary>
    Account,

    /// <summary>Source is one of.</summary>
    Source,

    /// <summary>Date range.</summary>
    DateRange,

    /// <summary>Has tag.</summary>
    Tag,
}

/// <summary>Action kinds the editor offers (every kind the engine supports, ADR 0020).</summary>
public enum ActionKind
{
    /// <summary>Rename the payee.</summary>
    SetPayee,

    /// <summary>Set the category.</summary>
    SetCategory,

    /// <summary>Replace the memo.</summary>
    SetMemo,

    /// <summary>Append to the memo.</summary>
    AppendMemo,

    /// <summary>Add a tag.</summary>
    AddTag,

    /// <summary>Mark approved.</summary>
    MarkApproved,

    /// <summary>Flag.</summary>
    Flag,

    /// <summary>Split by fixed amounts.</summary>
    SplitByAmounts,

    /// <summary>Split by percentages.</summary>
    SplitByPercentages,

    /// <summary>Make a transfer.</summary>
    SetTransferAccount,
}

/// <summary>A checkable item (accounts and sources of set conditions).</summary>
public sealed partial class CheckItem : ObservableObject
{
    private readonly Action _changed;

    /// <summary>Creates the item.</summary>
    public CheckItem(object value, string label, bool isChecked, Action changed)
    {
        Value = value;
        Label = label;
        IsChecked = isChecked;
        _changed = changed;
    }

    /// <summary>Value (an account id or a <see cref="TransactionSource"/>).</summary>
    public object Value { get; }

    /// <summary>Label.</summary>
    public string Label { get; }

    /// <summary>Whether it is included.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => _changed();
}

/// <summary>Shared plumbing of condition and action rows: a change callback and the row's problems.</summary>
public abstract partial class RulePartViewModel : ObservableObject
{
    private Action _changed = () => { };

    /// <summary>Validation messages for this row (one per line), or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasProblem))]
    public partial string? Problem { get; set; }

    /// <summary>Whether the row has a problem.</summary>
    public bool HasProblem => !string.IsNullOrEmpty(Problem);

    /// <summary>Whether any of the row's problems is an error (not just a warning).</summary>
    [ObservableProperty]
    public partial bool ProblemIsError { get; set; }

    /// <summary>The editor this row belongs to (its remove buttons bind to the editor's commands).</summary>
    public RuleEditorViewModel? Editor { get; private set; }

    /// <summary>The action a split line belongs to.</summary>
    public ActionEditorViewModel? ParentAction { get; private set; }

    /// <summary>Sets the callback run after every edit.</summary>
    internal void Attach(Action changed, RuleEditorViewModel? editor = null, ActionEditorViewModel? action = null)
    {
        _changed = changed;
        Editor = editor ?? Editor;
        ParentAction = action ?? ParentAction;
    }

    /// <summary>Tells the editor something changed.</summary>
    protected void Changed() => _changed();

    /// <inheritdoc />
    protected override void OnPropertyChanged(System.ComponentModel.PropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.PropertyName is not (nameof(Problem) or nameof(HasProblem) or nameof(ProblemIsError)))
        {
            _changed();
        }
    }

    /// <summary>Label of a resource key, falling back to the key.</summary>
    protected static string Label(string key) => Strings.ResourceManager.GetString(key, Strings.Culture) ?? key;
}

/// <summary>One condition row of the rule editor.</summary>
public sealed partial class ConditionEditorViewModel : RulePartViewModel
{
    /// <summary>Creates a row, optionally from an existing condition.</summary>
    public ConditionEditorViewModel(RuleEditorContext context, RuleCondition? condition = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        Kinds = Enum.GetValues<ConditionKind>().Select(k => new Choice<ConditionKind>(k, Label("RuleCondition_" + k))).ToList();
        TextOperators = Enum.GetValues<TextOperator>().Select(k => new Choice<TextOperator>(k, Label("RuleTextOperator_" + k))).ToList();
        AmountOperators = Enum.GetValues<AmountOperator>().Select(k => new Choice<AmountOperator>(k, Label("RuleAmountOperator_" + k))).ToList();
        Directions = Enum.GetValues<TransactionDirection>().Select(k => new Choice<TransactionDirection>(k, Label("RuleDirection_" + k))).ToList();
        Accounts = context.Accounts.Select(a => new CheckItem(a.Id, a.Name, false, Changed)).ToList();
        Sources = Enum.GetValues<TransactionSource>().Select(s => new CheckItem(s, Label("RuleSource_" + s), false, Changed)).ToList();
        Kind = Kinds[0];
        TextOperator = TextOperators[0];
        AmountOperator = AmountOperators[0];
        Direction = Directions.First(d => d.Value == TransactionDirection.Outflow);
        if (condition is not null)
        {
            Load(condition);
        }
    }

    /// <summary>Editor context.</summary>
    public RuleEditorContext Context { get; }

    /// <summary>Kinds.</summary>
    public IReadOnlyList<Choice<ConditionKind>> Kinds { get; }

    /// <summary>Text operators.</summary>
    public IReadOnlyList<Choice<TextOperator>> TextOperators { get; }

    /// <summary>Amount operators.</summary>
    public IReadOnlyList<Choice<AmountOperator>> AmountOperators { get; }

    /// <summary>Directions.</summary>
    public IReadOnlyList<Choice<TransactionDirection>> Directions { get; }

    /// <summary>Accounts to check.</summary>
    public IReadOnlyList<CheckItem> Accounts { get; }

    /// <summary>Sources to check.</summary>
    public IReadOnlyList<CheckItem> Sources { get; }

    /// <summary>Selected kind.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsPayee), nameof(IsAmount), nameof(IsDirection), nameof(IsAccount), nameof(IsSource), nameof(IsDateRange), nameof(IsTag))]
    public partial Choice<ConditionKind> Kind { get; set; }

    /// <summary>Text operator (payee, memo).</summary>
    [ObservableProperty]
    public partial Choice<TextOperator> TextOperator { get; set; }

    /// <summary>Text or pattern (payee, memo).</summary>
    [ObservableProperty]
    public partial string? Text { get; set; }

    /// <summary>Compare normalized payees.</summary>
    [ObservableProperty]
    public partial bool Normalized { get; set; }

    /// <summary>Amount operator.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsBetween))]
    public partial Choice<AmountOperator> AmountOperator { get; set; }

    /// <summary>Amount (or lower bound) in minor units.</summary>
    [ObservableProperty]
    public partial long Amount { get; set; }

    /// <summary>Upper bound in minor units.</summary>
    [ObservableProperty]
    public partial long AmountMax { get; set; }

    /// <summary>Compare the signed amount instead of the magnitude.</summary>
    [ObservableProperty]
    public partial bool Signed { get; set; }

    /// <summary>Direction.</summary>
    [ObservableProperty]
    public partial Choice<TransactionDirection> Direction { get; set; }

    /// <summary>First date.</summary>
    [ObservableProperty]
    public partial DateTime? From { get; set; }

    /// <summary>Last date.</summary>
    [ObservableProperty]
    public partial DateTime? To { get; set; }

    /// <summary>Tag name.</summary>
    [ObservableProperty]
    public partial string? Tag { get; set; }

    /// <summary>Payee or memo.</summary>
    public bool IsText => Kind.Value is ConditionKind.Payee or ConditionKind.Memo;

    /// <summary>Payee (shows "normalized").</summary>
    public bool IsPayee => Kind.Value == ConditionKind.Payee;

    /// <summary>Amount.</summary>
    public bool IsAmount => Kind.Value == ConditionKind.Amount;

    /// <summary>"Between" shows the upper bound.</summary>
    public bool IsBetween => AmountOperator.Value == Keel.Domain.Rules.AmountOperator.Between;

    /// <summary>Direction.</summary>
    public bool IsDirection => Kind.Value == ConditionKind.Direction;

    /// <summary>Account.</summary>
    public bool IsAccount => Kind.Value == ConditionKind.Account;

    /// <summary>Source.</summary>
    public bool IsSource => Kind.Value == ConditionKind.Source;

    /// <summary>Date range.</summary>
    public bool IsDateRange => Kind.Value == ConditionKind.DateRange;

    /// <summary>Tag.</summary>
    public bool IsTag => Kind.Value == ConditionKind.Tag;

    /// <summary>Builds the condition.</summary>
    public RuleCondition Build() => Kind.Value switch
    {
        ConditionKind.Payee => new PayeeCondition(TextOperator.Value, Text ?? string.Empty, Normalized),
        ConditionKind.Memo => new MemoCondition(TextOperator.Value, Text ?? string.Empty),
        ConditionKind.Amount => new AmountCondition(AmountOperator.Value, Amount, IsBetween ? AmountMax : null, Signed),
        ConditionKind.Direction => new DirectionCondition(Direction.Value),
        ConditionKind.Account => new AccountCondition(Accounts.Where(a => a.IsChecked).Select(a => (Guid)a.Value).ToList()),
        ConditionKind.Source => new SourceCondition(Sources.Where(s => s.IsChecked).Select(s => (TransactionSource)s.Value).ToList()),
        ConditionKind.DateRange => new DateRangeCondition(From is { } f ? DateOnly.FromDateTime(f) : null, To is { } t ? DateOnly.FromDateTime(t) : null),
        _ => new TagCondition(Tag ?? string.Empty),
    };

    private void Load(RuleCondition condition)
    {
        switch (condition)
        {
            case PayeeCondition p:
                SetKind(ConditionKind.Payee);
                TextOperator = TextOperators.First(o => o.Value == p.Operator);
                Text = p.Value;
                Normalized = p.Normalized;
                break;
            case MemoCondition m:
                SetKind(ConditionKind.Memo);
                TextOperator = TextOperators.First(o => o.Value == m.Operator);
                Text = m.Value;
                break;
            case AmountCondition a:
                SetKind(ConditionKind.Amount);
                AmountOperator = AmountOperators.First(o => o.Value == a.Operator);
                Amount = a.Amount;
                AmountMax = a.AmountMax ?? 0;
                Signed = a.Signed;
                break;
            case DirectionCondition d:
                SetKind(ConditionKind.Direction);
                Direction = Directions.First(o => o.Value == d.Direction);
                break;
            case AccountCondition ac:
                SetKind(ConditionKind.Account);
                foreach (var item in Accounts)
                {
                    item.IsChecked = ac.AccountIds.Contains((Guid)item.Value);
                }

                break;
            case SourceCondition s:
                SetKind(ConditionKind.Source);
                foreach (var item in Sources)
                {
                    item.IsChecked = s.Sources.Contains((TransactionSource)item.Value);
                }

                break;
            case DateRangeCondition r:
                SetKind(ConditionKind.DateRange);
                From = r.From?.ToDateTime(TimeOnly.MinValue);
                To = r.To?.ToDateTime(TimeOnly.MinValue);
                break;
            case TagCondition t:
                SetKind(ConditionKind.Tag);
                Tag = t.Tag;
                break;
        }
    }

    private void SetKind(ConditionKind kind) => Kind = Kinds.First(k => k.Value == kind);
}

/// <summary>One split line of a split action.</summary>
public sealed partial class RuleSplitLineViewModel : RulePartViewModel
{
    /// <summary>Creates a line.</summary>
    public RuleSplitLineViewModel(IReadOnlyList<CategoryOption> categories) => Categories = categories;

    /// <summary>Categories.</summary>
    public IReadOnlyList<CategoryOption> Categories { get; }

    /// <summary>Category.</summary>
    [ObservableProperty]
    public partial CategoryOption? Category { get; set; }

    /// <summary>Category text as typed.</summary>
    [ObservableProperty]
    public partial string? CategoryText { get; set; }

    /// <summary>Fixed amount (magnitude) in minor units.</summary>
    [ObservableProperty]
    public partial long Amount { get; set; }

    /// <summary>Percentage.</summary>
    [ObservableProperty]
    public partial decimal? Percent { get; set; }

    /// <summary>Memo.</summary>
    [ObservableProperty]
    public partial string? Memo { get; set; }

    /// <summary>Whether this is the last fixed-amount line (it takes the rest).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAmount))]
    public partial bool IsRemainder { get; set; }

    /// <summary>Whether the amount box shows.</summary>
    public bool HasAmount => !IsRemainder;

    /// <summary>The chosen category (typed text resolved).</summary>
    public CategoryOption? ResolveCategory() => TransactionEditorViewModel.ResolveCategory(Categories, Category, CategoryText);

    partial void OnCategoryChanged(CategoryOption? value)
    {
        if (value is not null && CategoryText != value.FullName)
        {
            CategoryText = value.FullName;
        }
    }
}

/// <summary>One action row of the rule editor.</summary>
public sealed partial class ActionEditorViewModel : RulePartViewModel
{
    /// <summary>Creates a row, optionally from an existing action.</summary>
    public ActionEditorViewModel(RuleEditorContext context, RuleAction? action = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        Kinds = Enum.GetValues<ActionKind>().Select(k => new Choice<ActionKind>(k, Label("RuleAction_" + k))).ToList();
        Lines.CollectionChanged += (_, e) =>
        {
            foreach (var line in e.NewItems?.OfType<RuleSplitLineViewModel>() ?? [])
            {
                line.Attach(Changed, action: this);
            }

            MarkRemainder();
            Changed();
        };
        Kind = Kinds.First(k => k.Value == ActionKind.SetCategory);
        if (action is not null)
        {
            Load(action);
        }
    }

    /// <summary>Editor context.</summary>
    public RuleEditorContext Context { get; }

    /// <summary>Kinds.</summary>
    public IReadOnlyList<Choice<ActionKind>> Kinds { get; }

    /// <summary>Categories.</summary>
    public IReadOnlyList<CategoryOption> Categories => Context.Categories;

    /// <summary>Accounts (transfer targets).</summary>
    public IReadOnlyList<AccountOption> Accounts => Context.Accounts;

    /// <summary>Split lines.</summary>
    public ObservableCollection<RuleSplitLineViewModel> Lines { get; } = [];

    /// <summary>Selected kind.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsText), nameof(IsCategory), nameof(IsSplit), nameof(IsPercentSplit), nameof(IsAmountSplit), nameof(IsTransfer), nameof(TextWatermark))]
    public partial Choice<ActionKind> Kind { get; set; }

    /// <summary>Text: payee, memo, appended text or tag.</summary>
    [ObservableProperty]
    public partial string? Text { get; set; }

    /// <summary>Category.</summary>
    [ObservableProperty]
    public partial CategoryOption? Category { get; set; }

    /// <summary>Category text as typed.</summary>
    [ObservableProperty]
    public partial string? CategoryText { get; set; }

    /// <summary>Transfer account.</summary>
    [ObservableProperty]
    public partial AccountOption? Account { get; set; }

    /// <summary>Kinds with a text value.</summary>
    public bool IsText => Kind.Value is ActionKind.SetPayee or ActionKind.SetMemo or ActionKind.AppendMemo or ActionKind.AddTag;

    /// <summary>Set category.</summary>
    public bool IsCategory => Kind.Value == ActionKind.SetCategory;

    /// <summary>Either split kind.</summary>
    public bool IsSplit => Kind.Value is ActionKind.SplitByAmounts or ActionKind.SplitByPercentages;

    /// <summary>Split by percentages.</summary>
    public bool IsPercentSplit => Kind.Value == ActionKind.SplitByPercentages;

    /// <summary>Split by amounts.</summary>
    public bool IsAmountSplit => Kind.Value == ActionKind.SplitByAmounts;

    /// <summary>Make a transfer.</summary>
    public bool IsTransfer => Kind.Value == ActionKind.SetTransferAccount;

    /// <summary>Watermark of the text box.</summary>
    public string TextWatermark => Kind.Value switch
    {
        ActionKind.SetPayee => Strings.RuleEditor_PayeeWatermark,
        ActionKind.AddTag => Strings.RuleEditor_TagWatermark,
        _ => Strings.RuleEditor_MemoWatermark,
    };

    /// <summary>Builds the action.</summary>
    public RuleAction Build() => Kind.Value switch
    {
        ActionKind.SetPayee => new SetPayeeAction(Text ?? string.Empty),
        ActionKind.SetCategory => new SetCategoryAction(ResolveCategory()?.Id ?? Guid.Empty),
        ActionKind.SetMemo => new SetMemoAction(Text ?? string.Empty),
        ActionKind.AppendMemo => new AppendMemoAction(Text ?? string.Empty),
        ActionKind.AddTag => new AddTagAction(Text ?? string.Empty),
        ActionKind.MarkApproved => new MarkApprovedAction(),
        ActionKind.Flag => new FlagAction(),
        ActionKind.SplitByAmounts => new SplitByAmountsAction(Lines.Select((l, i) =>
            new AmountSplitLine(l.ResolveCategory()?.Id, i == Lines.Count - 1 ? null : l.Amount, Blank(l.Memo))).ToList()),
        ActionKind.SplitByPercentages => new SplitByPercentagesAction(Lines.Select(l =>
            new PercentSplitLine(l.ResolveCategory()?.Id, l.Percent ?? 0m, Blank(l.Memo))).ToList()),
        _ => new SetTransferAccountAction(Account?.Id ?? Guid.Empty),
    };

    /// <summary>The chosen category (typed text resolved).</summary>
    public CategoryOption? ResolveCategory() => TransactionEditorViewModel.ResolveCategory(Categories, Category, CategoryText);

    [RelayCommand]
    private void AddLine() => Lines.Add(new RuleSplitLineViewModel(Categories));

    partial void OnCategoryChanged(CategoryOption? value)
    {
        if (value is not null && CategoryText != value.FullName)
        {
            CategoryText = value.FullName;
        }
    }

    [RelayCommand]
    private void RemoveLine(RuleSplitLineViewModel? line)
    {
        if (line is not null)
        {
            Lines.Remove(line);
        }
    }

    partial void OnKindChanged(Choice<ActionKind> value)
    {
        if (IsSplit && Lines.Count == 0)
        {
            Lines.Add(new RuleSplitLineViewModel(Categories));
            Lines.Add(new RuleSplitLineViewModel(Categories));
        }
    }

    private void MarkRemainder()
    {
        for (var i = 0; i < Lines.Count; i++)
        {
            Lines[i].IsRemainder = i == Lines.Count - 1;
        }
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private void Load(RuleAction action)
    {
        void SetKind(ActionKind kind) => Kind = Kinds.First(k => k.Value == kind);
        void SetCategory(Guid? id)
        {
            Category = Categories.FirstOrDefault(c => c.Id == id);
            CategoryText = Category?.FullName;
        }

        switch (action)
        {
            case SetPayeeAction p:
                SetKind(ActionKind.SetPayee);
                Text = p.Payee;
                break;
            case SetCategoryAction c:
                SetKind(ActionKind.SetCategory);
                SetCategory(c.CategoryId);
                break;
            case SetMemoAction m:
                SetKind(ActionKind.SetMemo);
                Text = m.Memo;
                break;
            case AppendMemoAction a:
                SetKind(ActionKind.AppendMemo);
                Text = a.Text;
                break;
            case AddTagAction t:
                SetKind(ActionKind.AddTag);
                Text = t.Tag;
                break;
            case MarkApprovedAction:
                SetKind(ActionKind.MarkApproved);
                break;
            case FlagAction:
                SetKind(ActionKind.Flag);
                break;
            case SplitByAmountsAction s:
                Lines.Clear();
                SetKind(ActionKind.SplitByAmounts);
                Lines.Clear();
                foreach (var line in s.Lines)
                {
                    var vm = new RuleSplitLineViewModel(Categories) { Amount = line.Amount ?? 0, Memo = line.Memo };
                    vm.Category = Categories.FirstOrDefault(c => c.Id == line.CategoryId);
                    vm.CategoryText = vm.Category?.FullName;
                    Lines.Add(vm);
                }

                break;
            case SplitByPercentagesAction s:
                SetKind(ActionKind.SplitByPercentages);
                Lines.Clear();
                foreach (var line in s.Lines)
                {
                    var vm = new RuleSplitLineViewModel(Categories) { Percent = line.Percent, Memo = line.Memo };
                    vm.Category = Categories.FirstOrDefault(c => c.Id == line.CategoryId);
                    vm.CategoryText = vm.Category?.FullName;
                    Lines.Add(vm);
                }

                break;
            case SetTransferAccountAction t:
                SetKind(ActionKind.SetTransferAccount);
                Account = Accounts.FirstOrDefault(a => a.Id == t.AccountId);
                break;
        }
    }
}
