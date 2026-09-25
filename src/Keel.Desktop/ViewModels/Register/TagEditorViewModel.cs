using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain.Ledger;

namespace Keel.Desktop.ViewModels.Register;

/// <summary>
/// The tag box of the transaction editor (F-TXN-8): chips plus a text box with type-ahead over the file's tags.
/// Enter adds the typed tag (a new name becomes a tag when the transaction is saved), Backspace in the empty box
/// removes the last chip. Names compare case-insensitively; an existing tag keeps its spelling.
/// </summary>
public sealed partial class TagEditorViewModel : ObservableObject
{
    /// <summary>Creates the editor with the transaction's tags and the file's tag names for type-ahead.</summary>
    public TagEditorViewModel(IEnumerable<string> tags, IReadOnlyList<string> known)
    {
        ArgumentNullException.ThrowIfNull(known);
        Known = known;
        foreach (var tag in TagNames.Distinct(tags))
        {
            Chips.Add(new TagChipViewModel(tag, this));
        }

        Chips.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTags));
            OnPropertyChanged(nameof(Suggestions));
        };
    }

    /// <summary>The tags, in the order added.</summary>
    public ObservableCollection<TagChipViewModel> Chips { get; } = [];

    /// <summary>Tag names in the file.</summary>
    public IReadOnlyList<string> Known { get; }

    /// <summary>Type-ahead choices: known tags not on the transaction yet.</summary>
    public IReadOnlyList<string> Suggestions => Known.Where(k => !Chips.Any(c => TagNames.Same(c.Name, k))).ToList();

    /// <summary>Text being typed.</summary>
    [ObservableProperty]
    public partial string? Text { get; set; }

    /// <summary>Whether the transaction has tags.</summary>
    public bool HasTags => Chips.Count > 0;

    /// <summary>The tag names to save, including text typed but not yet added.</summary>
    public IReadOnlyList<string> Names => TagNames.Distinct(Chips.Select(c => c.Name).Append(Text));

    /// <summary>
    /// Adds the typed text as a tag (an existing tag's spelling wins). Returns false when there was nothing to
    /// add, so Enter falls through to saving the transaction.
    /// </summary>
    public bool CommitText()
    {
        var clean = TagNames.Clean(Text);
        if (clean.Length == 0)
        {
            Text = null;
            return false;
        }

        var name = Known.FirstOrDefault(k => TagNames.Same(k, clean)) ?? clean;
        if (!Chips.Any(c => TagNames.Same(c.Name, name)))
        {
            Chips.Add(new TagChipViewModel(name, this));
        }

        Text = null;
        return true;
    }

    /// <summary>Removes the last chip (Backspace in the empty box); false when there is none.</summary>
    public bool RemoveLast()
    {
        if (Chips.Count == 0 || !string.IsNullOrEmpty(Text))
        {
            return false;
        }

        Chips.RemoveAt(Chips.Count - 1);
        return true;
    }

    /// <summary>Removes a chip.</summary>
    [RelayCommand]
    private void Remove(TagChipViewModel? chip)
    {
        if (chip is not null)
        {
            Chips.Remove(chip);
        }
    }
}

/// <summary>One tag chip of the editor.</summary>
/// <param name="name">Tag name.</param>
/// <param name="owner">The tag box (the chip's remove button binds to its command).</param>
public sealed class TagChipViewModel(string name, TagEditorViewModel owner)
{
    /// <summary>Tag name.</summary>
    public string Name => name;

    /// <summary>The tag box.</summary>
    public TagEditorViewModel Owner => owner;

    /// <summary>"Remove tag Trip".</summary>
    public string RemoveName => LedgerText.Format(Strings.Tag_RemoveChip, name);
}
