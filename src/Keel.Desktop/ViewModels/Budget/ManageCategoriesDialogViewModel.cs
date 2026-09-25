using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Budget;

/// <summary>
/// "Manage categories" (F-BUD-1): create, rename, reorder, hide and delete groups and categories.
/// Every change is saved at once as its own undoable action; deleting something with history
/// first asks for a replacement category. System groups are shown read-only.
/// </summary>
public sealed partial class ManageCategoriesDialogViewModel : DialogViewModel
{
    private readonly ICategoryService _categories;
    private readonly StatusService _status;

    /// <summary>Creates the dialog; call <see cref="LoadAsync"/> before showing it.</summary>
    public ManageCategoriesDialogViewModel(ICategoryService categories, StatusService status)
    {
        _categories = categories;
        _status = status;
    }

    /// <inheritdoc />
    public override string Title => Strings.Manage_Title;

    /// <inheritdoc />
    public override double PreferredMaxWidth => 640;

    /// <summary>Groups in display order.</summary>
    public ObservableCollection<ManageGroupItem> Groups { get; } = [];

    /// <summary>Name of a new group.</summary>
    [ObservableProperty]
    public partial string? NewGroupName { get; set; }

    /// <summary>Loads (or reloads) the groups.</summary>
    public async Task LoadAsync()
    {
        var groups = await _categories.GetGroupsAsync(CancellationToken.None);
        Groups.Clear();
        var userCategories = groups.Where(g => !g.IsSystem).SelectMany(g => g.Categories).ToList();
        foreach (var group in groups)
        {
            var item = new ManageGroupItem(this, group);
            foreach (var category in group.Categories)
            {
                item.Categories.Add(new ManageCategoryItem(this, item, category));
            }

            Groups.Add(item);
        }

        UserCategories = userCategories;
    }

    /// <summary>Every user category (replacement choices).</summary>
    public IReadOnlyList<CategoryDto> UserCategories { get; private set; } = [];

    /// <summary>Adds a group.</summary>
    [RelayCommand]
    public Task AddGroupAsync() => RunAsync(async () =>
    {
        if (string.IsNullOrWhiteSpace(NewGroupName))
        {
            return;
        }

        var group = await _categories.CreateGroupAsync(NewGroupName, CancellationToken.None);
        NewGroupName = null;
        _status.Show(LedgerText.Format(Strings.Manage_GroupAdded, group.Name), offerUndo: true);
    });

    /// <inheritdoc />
    protected override Task<bool> ConfirmCoreAsync() => Task.FromResult(true);

    /// <summary>Runs a change, shows a refusal as the dialog error, and reloads.</summary>
    internal async Task RunAsync(Func<Task> change)
    {
        Error = null;
        try
        {
            await Task.Run(change);
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
        }
        catch (ArgumentException ex)
        {
            Error = ex.Message;
        }

        await LoadAsync();
    }

    internal ICategoryService Service => _categories;

    internal StatusService Status => _status;

    /// <summary>Moves a group up or down.</summary>
    internal Task MoveGroupAsync(ManageGroupItem group, int delta)
    {
        var order = Groups.Select(g => g.Id).ToList();
        var index = order.IndexOf(group.Id);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= order.Count)
        {
            return Task.CompletedTask;
        }

        (order[index], order[target]) = (order[target], order[index]);
        return RunAsync(() => _categories.ReorderGroupsAsync(order, CancellationToken.None));
    }

    /// <summary>Moves a category up or down; past the first or last position it moves into the neighbouring user group.</summary>
    internal Task MoveCategoryAsync(ManageCategoryItem category, int delta)
    {
        var group = category.Group;
        var index = group.Categories.IndexOf(category);
        var target = index + delta;
        if (target >= 0 && target < group.Categories.Count)
        {
            return RunAsync(() => _categories.MoveCategoryAsync(category.Id, group.Id, target, CancellationToken.None));
        }

        var groups = Groups.Where(g => !g.IsSystem).ToList();
        var neighbour = groups.IndexOf(group) + Math.Sign(delta);
        if (neighbour < 0 || neighbour >= groups.Count)
        {
            return Task.CompletedTask;
        }

        var into = groups[neighbour];
        return RunAsync(() => _categories.MoveCategoryAsync(category.Id, into.Id, delta < 0 ? into.Categories.Count : 0, CancellationToken.None));
    }
}

/// <summary>Shared behaviour of a group or category line: inline rename and the delete-with-replacement step.</summary>
public abstract partial class ManageItem : ObservableObject
{
    /// <summary>Creates the item.</summary>
    protected ManageItem(ManageCategoriesDialogViewModel owner, Guid id, string name, bool isSystem, bool isHidden)
    {
        Owner = owner;
        Id = id;
        Name = name;
        EditName = name;
        IsSystem = isSystem;
        IsHidden = isHidden;
    }

    /// <summary>The dialog.</summary>
    protected ManageCategoriesDialogViewModel Owner { get; }

    /// <summary>Id.</summary>
    public Guid Id { get; }

    /// <summary>Saved name.</summary>
    public string Name { get; }

    /// <summary>Name being edited.</summary>
    [ObservableProperty]
    public partial string? EditName { get; set; }

    /// <summary>System item: read-only.</summary>
    public bool IsSystem { get; }

    /// <summary>Whether it can be edited.</summary>
    public bool IsEditable => !IsSystem;

    /// <summary>Hidden from the budget.</summary>
    public bool IsHidden { get; }

    /// <summary>Label of the hide/show button.</summary>
    public string HideLabel => LedgerText.Format(IsHidden ? Strings.Manage_Show : Strings.Manage_Hide, Name);

    /// <summary>Label of the delete button.</summary>
    public string DeleteLabel => LedgerText.Format(Strings.Manage_Delete, Name);

    /// <summary>Label of the move-up button.</summary>
    public string MoveUpLabel => LedgerText.Format(Strings.Manage_MoveUp, Name);

    /// <summary>Label of the move-down button.</summary>
    public string MoveDownLabel => LedgerText.Format(Strings.Manage_MoveDown, Name);

    /// <summary>Whether the replacement step is open.</summary>
    [ObservableProperty]
    public partial bool IsConfirmingDelete { get; private set; }

    /// <summary>Explains what happens to the history.</summary>
    [ObservableProperty]
    public partial string? DeleteMessage { get; private set; }

    /// <summary>Categories that can receive the history.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<CategoryDto> ReplacementOptions { get; private set; } = [];

    /// <summary>The chosen replacement.</summary>
    [ObservableProperty]
    public partial CategoryDto? Replacement { get; set; }

    /// <summary>Saves the edited name (Enter or leaving the field).</summary>
    [RelayCommand]
    public Task RenameAsync()
    {
        var name = EditName?.Trim();
        if (IsSystem || string.IsNullOrEmpty(name) || string.Equals(name, Name, StringComparison.Ordinal))
        {
            EditName = Name;
            return Task.CompletedTask;
        }

        return Owner.RunAsync(() => RenameCoreAsync(name));
    }

    /// <summary>Hides or shows.</summary>
    [RelayCommand]
    public Task ToggleHiddenAsync() => Owner.RunAsync(() => SetHiddenCoreAsync(!IsHidden));

    /// <summary>Deletes, or opens the replacement step when there is history.</summary>
    [RelayCommand]
    public async Task DeleteAsync()
    {
        var (history, count) = await HistoryAsync();
        if (!history)
        {
            await Owner.RunAsync(async () =>
            {
                await DeleteCoreAsync(null);
                Owner.Status.Show(LedgerText.Format(Strings.Manage_Deleted, Name), offerUndo: true);
            });
            return;
        }

        var excluded = ExcludedFromReplacement();
        ReplacementOptions = Owner.UserCategories.Where(c => !excluded.Contains(c.Id)).ToList();
        Replacement = ReplacementOptions.FirstOrDefault();
        DeleteMessage = LedgerText.Format(Strings.Manage_DeleteHistory, Name, count);
        IsConfirmingDelete = true;
    }

    /// <summary>Deletes, moving the history to <see cref="Replacement"/>.</summary>
    [RelayCommand]
    public Task ConfirmDeleteAsync()
    {
        if (Replacement is not { } replacement)
        {
            Owner.Error = LedgerText.Error(LedgerError.ReplacementCategoryRequired);
            return Task.CompletedTask;
        }

        return Owner.RunAsync(async () =>
        {
            await DeleteCoreAsync(replacement.Id);
            Owner.Status.Show(LedgerText.Format(Strings.Manage_DeletedInto, Name, replacement.Name), offerUndo: true);
        });
    }

    /// <summary>Closes the replacement step.</summary>
    [RelayCommand]
    public void CancelDelete() => IsConfirmingDelete = false;

    /// <summary>Renames in the service.</summary>
    protected abstract Task RenameCoreAsync(string name);

    /// <summary>Hides or shows in the service.</summary>
    protected abstract Task SetHiddenCoreAsync(bool hidden);

    /// <summary>Deletes in the service.</summary>
    protected abstract Task DeleteCoreAsync(Guid? replacement);

    /// <summary>Whether deleting needs a replacement, and how many rows of history exist.</summary>
    protected abstract Task<(bool HasHistory, int Count)> HistoryAsync();

    /// <summary>Categories that cannot be the replacement (being deleted).</summary>
    protected abstract IReadOnlySet<Guid> ExcludedFromReplacement();
}

/// <summary>A group line of the dialog.</summary>
public sealed partial class ManageGroupItem : ManageItem
{
    /// <summary>Creates the item.</summary>
    public ManageGroupItem(ManageCategoriesDialogViewModel owner, CategoryGroupDto group)
        : base(owner, group.Id, group.Name, group.IsSystem, group.IsHidden)
    {
    }

    /// <summary>Categories of the group.</summary>
    public ObservableCollection<ManageCategoryItem> Categories { get; } = [];

    /// <summary>Name of a new category in this group.</summary>
    [ObservableProperty]
    public partial string? NewCategoryName { get; set; }

    /// <summary>Label of the add-category field.</summary>
    public string AddCategoryLabel => LedgerText.Format(Strings.Manage_AddCategoryTo, Name);

    /// <summary>Adds a category to the group.</summary>
    [RelayCommand]
    public Task AddCategoryAsync()
    {
        if (string.IsNullOrWhiteSpace(NewCategoryName))
        {
            return Task.CompletedTask;
        }

        var name = NewCategoryName;
        return Owner.RunAsync(async () =>
        {
            var created = await Owner.Service.CreateCategoryAsync(Id, name, CancellationToken.None);
            Owner.Status.Show(LedgerText.Format(Strings.Manage_CategoryAdded, created.Name), offerUndo: true);
        });
    }

    /// <summary>Moves the group up.</summary>
    [RelayCommand]
    public Task MoveUpAsync() => Owner.MoveGroupAsync(this, -1);

    /// <summary>Moves the group down.</summary>
    [RelayCommand]
    public Task MoveDownAsync() => Owner.MoveGroupAsync(this, 1);

    /// <inheritdoc />
    protected override Task RenameCoreAsync(string name) => Owner.Service.RenameGroupAsync(Id, name, CancellationToken.None);

    /// <inheritdoc />
    protected override Task SetHiddenCoreAsync(bool hidden) => Owner.Service.SetGroupHiddenAsync(Id, hidden, CancellationToken.None);

    /// <inheritdoc />
    protected override Task DeleteCoreAsync(Guid? replacement) => Owner.Service.DeleteGroupAsync(Id, replacement, CancellationToken.None);

    /// <inheritdoc />
    protected override async Task<(bool HasHistory, int Count)> HistoryAsync()
    {
        var total = 0;
        var any = false;
        foreach (var category in Categories)
        {
            var usage = await Owner.Service.GetUsageAsync(category.Id, CancellationToken.None);
            any |= usage.HasHistory;
            total += usage.Transactions;
        }

        return (any, total);
    }

    /// <inheritdoc />
    protected override IReadOnlySet<Guid> ExcludedFromReplacement() => Categories.Select(c => c.Id).ToHashSet();
}

/// <summary>A category line of the dialog.</summary>
public sealed partial class ManageCategoryItem : ManageItem
{
    /// <summary>Creates the item.</summary>
    public ManageCategoryItem(ManageCategoriesDialogViewModel owner, ManageGroupItem group, CategoryDto category)
        : base(owner, category.Id, category.Name, category.IsSystem || category.IsCreditCardPayment, category.IsHidden)
    {
        Group = group;
        FlexChoices = new[] { FlexKind.Unset, FlexKind.Fixed, FlexKind.NonMonthly, FlexKind.Flex }
            .Select(k => new Choice<FlexKind>(k, BudgetText.FlexKind(k))).ToList();
        SelectedFlex = FlexChoices.First(c => c.Value == category.FlexKind);
    }

    /// <summary>Flex-mode tags: Automatic, Fixed, Non-monthly, Flex (F-BUD-6).</summary>
    public IReadOnlyList<Choice<FlexKind>> FlexChoices { get; }

    /// <summary>The saved tag; choosing another saves it at once (undoable).</summary>
    [ObservableProperty]
    public partial Choice<FlexKind> SelectedFlex { get; set; }

    /// <summary>The last tag change (tests await it).</summary>
    public Task FlexSaving { get; private set; } = Task.CompletedTask;

    /// <summary>Accessible name of the tag picker.</summary>
    public string FlexLabel => LedgerText.Format(Strings.Flex_ManageLabel, Name);

    partial void OnSelectedFlexChanged(Choice<FlexKind> oldValue, Choice<FlexKind> newValue)
    {
        if (oldValue is not null && !IsSystem && oldValue.Value != newValue.Value)
        {
            var kind = newValue.Value;
            FlexSaving = Owner.RunAsync(async () =>
            {
                await Owner.Service.SetFlexKindAsync(Id, kind, CancellationToken.None);
                Owner.Status.Show(LedgerText.Format(Strings.Flex_Tagged, Name, BudgetText.FlexKind(kind)), offerUndo: true);
            });
        }
    }

    /// <summary>The group line.</summary>
    public ManageGroupItem Group { get; }

    /// <summary>Moves the category up (into the previous group at the top).</summary>
    [RelayCommand]
    public Task MoveUpAsync() => Owner.MoveCategoryAsync(this, -1);

    /// <summary>Moves the category down (into the next group at the bottom).</summary>
    [RelayCommand]
    public Task MoveDownAsync() => Owner.MoveCategoryAsync(this, 1);

    /// <inheritdoc />
    protected override Task RenameCoreAsync(string name) => Owner.Service.RenameCategoryAsync(Id, name, CancellationToken.None);

    /// <inheritdoc />
    protected override Task SetHiddenCoreAsync(bool hidden) => Owner.Service.SetCategoryHiddenAsync(Id, hidden, CancellationToken.None);

    /// <inheritdoc />
    protected override Task DeleteCoreAsync(Guid? replacement) => Owner.Service.DeleteCategoryAsync(Id, replacement, CancellationToken.None);

    /// <inheritdoc />
    protected override async Task<(bool HasHistory, int Count)> HistoryAsync()
    {
        var usage = await Owner.Service.GetUsageAsync(Id, CancellationToken.None);
        return (usage.HasHistory, usage.Transactions);
    }

    /// <inheritdoc />
    protected override IReadOnlySet<Guid> ExcludedFromReplacement() => new HashSet<Guid> { Id };
}
