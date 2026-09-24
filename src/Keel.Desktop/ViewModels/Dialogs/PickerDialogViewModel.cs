using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Desktop.Resources;

namespace Keel.Desktop.ViewModels.Dialogs;

/// <summary>A searchable single-choice list (bulk categorize, move to account).</summary>
public sealed partial class PickerDialogViewModel : DialogViewModel
{
    private readonly string _title;

    /// <summary>Creates the picker.</summary>
    public PickerDialogViewModel(string title, string prompt, IReadOnlyList<PickerItem> items)
    {
        _title = title;
        Prompt = prompt;
        Items = items;
        Filtered = items;
    }

    /// <inheritdoc />
    public override string Title => _title;

    /// <summary>Instruction above the list.</summary>
    public string Prompt { get; }

    /// <summary>All items.</summary>
    public IReadOnlyList<PickerItem> Items { get; }

    /// <summary>Items matching <see cref="SearchText"/>.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<PickerItem> Filtered { get; private set; }

    /// <summary>Search text.</summary>
    [ObservableProperty]
    public partial string? SearchText { get; set; }

    /// <summary>The chosen item.</summary>
    [ObservableProperty]
    public partial PickerItem? Selected { get; set; }

    /// <inheritdoc />
    protected override Task<bool> ConfirmCoreAsync()
    {
        Selected ??= Filtered.Count == 1 ? Filtered[0] : null;
        if (Selected is null)
        {
            Error = Strings.Picker_ErrorNothingSelected;
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }

    partial void OnSearchTextChanged(string? value)
    {
        Filtered = string.IsNullOrWhiteSpace(value)
            ? Items
            : Items.Where(i => i.Label.Contains(value.Trim(), StringComparison.CurrentCultureIgnoreCase)).ToList();
        if (Selected is not null && !Filtered.Contains(Selected))
        {
            Selected = null;
        }
    }
}

/// <summary>An item of <see cref="PickerDialogViewModel"/>.</summary>
public sealed record PickerItem(Guid Id, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}
