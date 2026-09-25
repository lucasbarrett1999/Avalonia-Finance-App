using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Payees;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;

namespace Keel.Desktop.ViewModels.Rules;

/// <summary>
/// Settings → Payees (F-TXN-9): search, default category per payee (used by categorization at 95%
/// confidence), rename, which applies to every transaction of the payee, and merge: select payees and
/// "Merge into…" one of them (ADR 0097).
/// </summary>
public sealed partial class PayeesViewModel : ObservableObject, IRecipient<LedgerChanged>
{
    /// <summary>Most payees listed at once; searching narrows the list.</summary>
    public const int Limit = 200;

    private readonly IPayeeService _payees;
    private readonly ICategoryService _categories;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private readonly Dictionary<Guid, PayeeListItem> _selected = [];
    private int _version;
    private bool _loaded;

    /// <summary>Creates the view model.</summary>
    public PayeesViewModel(IPayeeService payees, ICategoryService categories, DialogService dialogs, StatusService status, IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        _payees = payees;
        _categories = categories;
        _dialogs = dialogs;
        _status = status;
        messenger.Register(this);
    }

    /// <summary>Payees shown.</summary>
    public ObservableCollection<PayeeRowViewModel> Payees { get; } = [];

    /// <summary>Default-category choices ("No default" first).</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CategoryOption> CategoryChoices { get; private set; } = [NoDefault];

    /// <summary>Search text.</summary>
    [ObservableProperty]
    public partial string? SearchText { get; set; }

    /// <summary>Loading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Nothing matches.</summary>
    public bool IsEmpty => !IsLoading && !HasError && Payees.Count == 0;

    /// <summary>"No default category".</summary>
    public static CategoryOption NoDefault { get; } = new(null, Strings.Payees_NoDefault, string.Empty);

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Payees checked for merging (kept across searches).</summary>
    public IReadOnlyList<PayeeListItem> SelectedPayees => [.. _selected.Values];

    /// <summary>"3 selected".</summary>
    public string SelectionText => LedgerText.Format(Strings.PayeeMerge_Selected, _selected.Count.ToString(CultureInfo.CurrentCulture));

    /// <summary>Whether any payee is checked.</summary>
    public bool HasSelection => _selected.Count > 0;

    /// <summary>Whether enough payees are checked to merge.</summary>
    public bool CanMerge => _selected.Count >= 2;

    /// <summary>The latest merge (tests await it).</summary>
    public Task Merging { get; private set; } = Task.CompletedTask;

    /// <summary>Loads once.</summary>
    public Task EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            Loading = LoadAsync();
        }

        return Loading;
    }

    /// <inheritdoc />
    public void Receive(LedgerChanged message) => Dispatcher.UIThread.Post(() =>
    {
        if (_loaded)
        {
            Loading = LoadAsync();
        }
    });

    [RelayCommand]
    private async Task RenameAsync(PayeeRowViewModel? row)
    {
        if (row is null)
        {
            return;
        }

        var dialog = new RenamePayeeDialogViewModel(_payees, row.Id, row.Name);
        if (await _dialogs.ShowAsync(dialog) && dialog.Result is { } renamed)
        {
            _status.Show(LedgerText.Format(Strings.Status_PayeeRenamed, row.Name, renamed.Name), offerUndo: true);
        }
    }

    /// <summary>Opens the merge dialog for the checked payees (F-TXN-9).</summary>
    [RelayCommand(CanExecute = nameof(CanMerge))]
    private Task MergeAsync() => Merging = MergeCoreAsync();

    /// <summary>Unchecks every payee.</summary>
    [RelayCommand]
    private void ClearSelection()
    {
        _selected.Clear();
        foreach (var row in Payees)
        {
            row.SetSelected(false);
        }

        SelectionChanged();
    }

    private async Task MergeCoreAsync()
    {
        var payees = SelectedPayees.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        if (payees.Count < 2)
        {
            return;
        }

        var dialog = new MergePayeesDialogViewModel(_payees, payees);
        await dialog.RefreshAsync();
        if (await _dialogs.ShowAsync(dialog) && dialog.Result is { } result)
        {
            ClearSelection();
            _status.Show(LedgerText.Format(Strings.PayeeMerge_Done, result.MergedCount.ToString(CultureInfo.CurrentCulture), result.Survivor.Name,
                result.Transactions.ToString("N0", CultureInfo.CurrentCulture)), offerUndo: true);
        }
    }

    internal void Toggle(PayeeRowViewModel row, bool selected)
    {
        if (selected)
        {
            _selected[row.Id] = row.Payee;
        }
        else
        {
            _selected.Remove(row.Id);
        }

        SelectionChanged();
    }

    private void SelectionChanged()
    {
        OnPropertyChanged(nameof(SelectedPayees));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(HasSelection));
        OnPropertyChanged(nameof(CanMerge));
        MergeCommand.NotifyCanExecuteChanged();
    }

    partial void OnSearchTextChanged(string? value)
    {
        if (_loaded)
        {
            Loading = LoadAsync();
        }
    }

    private async Task SetDefaultAsync(PayeeRowViewModel row, CategoryOption? category)
    {
        try
        {
            await _payees.SetDefaultCategoryAsync(row.Id, category?.Id, CancellationToken.None);
            _status.Show(category?.Id is null
                ? LedgerText.Format(Strings.Status_PayeeDefaultCleared, row.Name)
                : LedgerText.Format(Strings.Status_PayeeDefaultSet, row.Name, category.FullName), offerUndo: true);
        }
        catch (LedgerValidationException ex)
        {
            _status.Show(LedgerText.Error(ex.Error), isError: true);
        }
    }

    private async Task LoadAsync()
    {
        var version = ++_version;
        IsLoading = Payees.Count == 0;
        try
        {
            ErrorMessage = null;
            var categoriesTask = _categories.GetCategoriesAsync(includeHidden: false, CancellationToken.None);
            var payeesTask = _payees.ListAsync(SearchText, Limit, CancellationToken.None);
            var categories = await categoriesTask;
            var payees = await payeesTask;
            if (version != _version)
            {
                return;
            }

            var choices = new List<CategoryOption> { NoDefault };
            choices.AddRange(categories.Where(c => !c.IsCreditCardPayment && !c.IsSystem).Select(CategoryOption.From));
            CategoryChoices = choices;
            Payees.Clear();
            foreach (var payee in payees)
            {
                var row = new PayeeRowViewModel(payee, choices, SetDefaultAsync) { Owner = this };
                if (_selected.ContainsKey(payee.Id))
                {
                    _selected[payee.Id] = payee;
                    row.SetSelected(true);
                }

                Payees.Add(row);
            }

            SelectionChanged();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _version)
            {
                ErrorMessage = LedgerText.Format(Strings.Payees_ErrorLoading, ex.Message);
            }
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
            }
        }
    }
}

/// <summary>One payee row.</summary>
public sealed partial class PayeeRowViewModel : ObservableObject
{
    private readonly Func<PayeeRowViewModel, CategoryOption?, Task> _setDefault;
    private readonly bool _ready;

    /// <summary>Creates the row.</summary>
    public PayeeRowViewModel(PayeeListItem payee, IReadOnlyList<CategoryOption> choices, Func<PayeeRowViewModel, CategoryOption?, Task> setDefault)
    {
        ArgumentNullException.ThrowIfNull(payee);
        ArgumentNullException.ThrowIfNull(choices);
        Payee = payee;
        Choices = choices;
        _setDefault = setDefault;
        DefaultCategory = choices.FirstOrDefault(c => c.Id == payee.DefaultCategoryId) ?? choices[0];
        _ready = true;
    }

    /// <summary>The payee.</summary>
    public PayeeListItem Payee { get; }

    /// <summary>The list (the rename button binds to its command).</summary>
    public PayeesViewModel? Owner { get; init; }

    /// <summary>Id.</summary>
    public Guid Id => Payee.Id;

    /// <summary>Name.</summary>
    public string Name => Payee.Name;

    /// <summary>"12 transactions".</summary>
    public string CountText => LedgerText.Format(Strings.Payees_TransactionCount, Payee.TransactionCount.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>Default-category choices.</summary>
    public IReadOnlyList<CategoryOption> Choices { get; }

    /// <summary>Default category; changing it saves.</summary>
    [ObservableProperty]
    public partial CategoryOption? DefaultCategory { get; set; }

    /// <summary>Checked for merging.</summary>
    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    /// <summary>"Select Amazon for merging".</summary>
    public string SelectName => LedgerText.Format(Strings.PayeeMerge_SelectName, Payee.Name);

    private bool _silent;

    /// <summary>Sets the check without notifying the list.</summary>
    internal void SetSelected(bool selected)
    {
        _silent = true;
        IsSelected = selected;
        _silent = false;
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (!_silent)
        {
            Owner?.Toggle(this, value);
        }
    }

    partial void OnDefaultCategoryChanged(CategoryOption? value)
    {
        if (_ready && value is not null)
        {
            _ = _setDefault(this, value);
        }
    }
}

/// <summary>Rename a payee (applies to all its transactions).</summary>
public sealed partial class RenamePayeeDialogViewModel : DialogViewModel
{
    private readonly IPayeeService _payees;
    private readonly Guid _payeeId;

    /// <summary>Creates the dialog.</summary>
    public RenamePayeeDialogViewModel(IPayeeService payees, Guid payeeId, string currentName)
    {
        _payees = payees;
        _payeeId = payeeId;
        CurrentName = currentName;
        Name = currentName;
    }

    /// <inheritdoc />
    public override string Title => Strings.Payees_RenameTitle;

    /// <summary>The name before renaming.</summary>
    public string CurrentName { get; }

    /// <summary>Explanation.</summary>
    public string Message => LedgerText.Format(Strings.Payees_RenameMessage, CurrentName);

    /// <summary>New name.</summary>
    [ObservableProperty]
    public partial string? Name { get; set; }

    /// <summary>The renamed (or merged) payee.</summary>
    public PayeeDto? Result { get; private set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        try
        {
            Result = await _payees.RenameAsync(_payeeId, Name ?? string.Empty, CancellationToken.None);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
    }
}

/// <summary>
/// Merge payees (F-TXN-9): choose the payee that stays; the dialog shows how many transactions, scheduled
/// transactions, recurring items and rules move, and the default category the survivor ends with. One undo reverts it.
/// </summary>
public sealed partial class MergePayeesDialogViewModel : DialogViewModel
{
    private readonly IPayeeService _payees;

    /// <summary>Creates the dialog for <paramref name="payees"/> (two or more).</summary>
    public MergePayeesDialogViewModel(IPayeeService service, IReadOnlyList<PayeeListItem> payees)
    {
        ArgumentNullException.ThrowIfNull(payees);
        _payees = service;
        Choices = payees.Select(p => new MergeChoiceViewModel(p, this)).ToList();
        var survivor = Choices.OrderByDescending(c => c.Payee.TransactionCount).First();
        survivor.SetChecked(true);
        Survivor = survivor.Payee;
    }

    /// <inheritdoc />
    public override string Title => Strings.PayeeMerge_Title;

    /// <inheritdoc />
    public override double PreferredMaxWidth => 600;

    /// <summary>The payees, one of which stays.</summary>
    public IReadOnlyList<MergeChoiceViewModel> Choices { get; }

    /// <summary>The payee that stays.</summary>
    [ObservableProperty]
    public partial PayeeListItem Survivor { get; private set; }

    /// <summary>The counts for the chosen survivor.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Summary), nameof(DefaultText), nameof(HasPreview))]
    public partial PayeeMergePreview? Preview { get; private set; }

    /// <summary>Whether the counts are loaded.</summary>
    public bool HasPreview => Preview is not null;

    /// <summary>"Merge 2 payees into Amazon: 14 transactions, 1 scheduled transaction, 1 recurring item and 2 rules move."</summary>
    public string Summary => Preview is { } p
        ? LedgerText.Format(Strings.PayeeMerge_Summary, p.Merged.Count.ToString(CultureInfo.CurrentCulture), p.Survivor.Name,
            p.Transactions.ToString("N0", CultureInfo.CurrentCulture), p.ScheduledTransactions.ToString("N0", CultureInfo.CurrentCulture),
            p.RecurringItems.ToString("N0", CultureInfo.CurrentCulture), p.Rules.ToString("N0", CultureInfo.CurrentCulture))
        : Strings.PayeeMerge_Counting;

    /// <summary>Which default category the survivor keeps.</summary>
    public string DefaultText => Preview is { DefaultCategoryId: null } ? Strings.PayeeMerge_NoDefault : Strings.PayeeMerge_DefaultKept;

    /// <summary>The outcome.</summary>
    public PayeeMergeResult? Result { get; private set; }

    /// <summary>The latest count refresh (tests await it).</summary>
    public Task Refreshing { get; private set; } = Task.CompletedTask;

    /// <summary>Counts what the merge into the chosen survivor would move.</summary>
    public Task RefreshAsync() => Refreshing = RefreshCoreAsync();

    internal void Choose(MergeChoiceViewModel choice)
    {
        foreach (var other in Choices.Where(c => c != choice))
        {
            other.SetChecked(false);
        }

        Survivor = choice.Payee;
        _ = RefreshAsync();
    }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        try
        {
            var survivor = Survivor.Id;
            Result = await Task.Run(() => _payees.MergeAsync(Choices.Select(c => c.Payee.Id).ToList(), survivor, CancellationToken.None));
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
    }

    private async Task RefreshCoreAsync()
    {
        var survivor = Survivor.Id;
        Preview = null;
        try
        {
            var preview = await Task.Run(() => _payees.PreviewMergeAsync(Choices.Select(c => c.Payee.Id).ToList(), survivor, CancellationToken.None));
            if (Survivor.Id == survivor)
            {
                Preview = preview;
            }
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
        }
    }
}

/// <summary>One payee in the merge dialog (radio button: the survivor).</summary>
public sealed partial class MergeChoiceViewModel : ObservableObject
{
    private readonly MergePayeesDialogViewModel _owner;
    private bool _silent;

    /// <summary>Creates the choice.</summary>
    public MergeChoiceViewModel(PayeeListItem payee, MergePayeesDialogViewModel owner)
    {
        Payee = payee;
        _owner = owner;
    }

    /// <summary>The payee.</summary>
    public PayeeListItem Payee { get; }

    /// <summary>"Amazon (12 transactions)".</summary>
    public string Label => LedgerText.Format(Strings.PayeeMerge_Choice, Payee.Name, Payee.TransactionCount.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>Whether this payee stays.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    internal void SetChecked(bool value)
    {
        _silent = true;
        IsChecked = value;
        _silent = false;
    }

    partial void OnIsCheckedChanged(bool value)
    {
        if (value && !_silent)
        {
            _owner.Choose(this);
        }
    }
}
