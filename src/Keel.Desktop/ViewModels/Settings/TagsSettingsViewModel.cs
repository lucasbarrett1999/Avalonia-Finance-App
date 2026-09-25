using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Application.Tags;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Rules;

namespace Keel.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → Tags (F-TXN-8, ADR 0096): every tag with its transaction and rule counts; rename, merge into another
/// tag, delete with a count confirmation, and a link to the tagged transactions. The reserved "Flagged" tag
/// (the rules' flag) can only be deleted. Every change is undoable from the status strip.
/// </summary>
public sealed partial class TagsSettingsViewModel : ViewModelBase, IRecipient<LedgerChanged>
{
    private readonly ITagService _tags;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private readonly INavigationService _navigation;
    private int _version;
    private bool _loaded;

    /// <summary>Creates the view model.</summary>
    public TagsSettingsViewModel(ITagService tags, DialogService dialogs, StatusService status, INavigationService navigation, IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        _tags = tags;
        _dialogs = dialogs;
        _status = status;
        _navigation = navigation;
        messenger.Register(this);
    }

    /// <summary>The tags.</summary>
    public ObservableCollection<TagRowViewModel> Tags { get; } = [];

    /// <summary>Loading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Load failure.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty))]
    public partial string? Error { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>No tags yet.</summary>
    public bool IsEmpty => !IsLoading && !HasError && Tags.Count == 0;

    /// <summary>The latest load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The latest action (tests await it).</summary>
    public Task Working { get; private set; } = Task.CompletedTask;

    /// <summary>Loads (or reloads) the list.</summary>
    public Task LoadAsync()
    {
        _loaded = true;
        return Loading = LoadCoreAsync();
    }

    /// <inheritdoc />
    public void Receive(LedgerChanged message) => Dispatcher.UIThread.Post(() =>
    {
        if (_loaded)
        {
            Loading = LoadCoreAsync();
        }
    });

    [RelayCommand]
    private Task RenameAsync(TagRowViewModel? row) => row is null ? Task.CompletedTask : Working = RenameCoreAsync(row);

    [RelayCommand]
    private Task MergeAsync(TagRowViewModel? row) => row is null ? Task.CompletedTask : Working = MergeCoreAsync(row);

    [RelayCommand]
    private Task DeleteAsync(TagRowViewModel? row) => row is null ? Task.CompletedTask : Working = DeleteCoreAsync(row);

    [RelayCommand]
    private void ShowTransactions(TagRowViewModel? row)
    {
        if (row is not null)
        {
            _navigation.NavigateTo<AccountsViewModel>(new RegisterNavigation(null, TagId: row.Id));
        }
    }

    private async Task RenameCoreAsync(TagRowViewModel row)
    {
        var dialog = new RenameTagDialogViewModel(_tags, row.Tag);
        if (await _dialogs.ShowAsync(dialog) && dialog.Result is { } renamed)
        {
            _status.Show(LedgerText.Format(Strings.Tag_Renamed, row.Name, renamed.Name), offerUndo: true);
        }
    }

    private async Task MergeCoreAsync(TagRowViewModel row)
    {
        var targets = Tags.Where(t => t.Id != row.Id && !t.Tag.IsReserved).Select(t => t.Tag).ToList();
        var dialog = new MergeTagDialogViewModel(_tags, row.Tag, targets);
        if (await _dialogs.ShowAsync(dialog) && dialog.Result is { } result)
        {
            _status.Show(LedgerText.Format(Strings.Tag_Merged, row.Name, result.Target.Name), offerUndo: true);
        }
    }

    private async Task DeleteCoreAsync(TagRowViewModel row)
    {
        var message = LedgerText.Format(Strings.Tag_DeleteMessage, row.Name, row.Tag.TransactionCount.ToString("N0", CultureInfo.CurrentCulture))
            + (row.Tag.RuleCount > 0 ? " " + LedgerText.Format(Strings.Tag_DeleteRulesNote, row.Tag.RuleCount.ToString("N0", CultureInfo.CurrentCulture)) : string.Empty)
            + (row.Tag.IsReserved ? " " + Strings.Tag_DeleteFlaggedNote : string.Empty);
        var confirm = new ConfirmDialogViewModel(Strings.Tag_DeleteTitle, message, Strings.Tag_Delete);
        if (!await _dialogs.ShowAsync(confirm))
        {
            return;
        }

        try
        {
            var count = await Task.Run(() => _tags.DeleteAsync(row.Id, CancellationToken.None));
            _status.Show(LedgerText.Format(Strings.Tag_Deleted, row.Name, count.ToString("N0", CultureInfo.CurrentCulture)), offerUndo: true);
        }
        catch (LedgerValidationException ex)
        {
            _status.Show(LedgerText.Error(ex.Error), isError: true);
        }
    }

    private async Task LoadCoreAsync()
    {
        var version = ++_version;
        IsLoading = Tags.Count == 0;
        try
        {
            Error = null;
            var list = await Task.Run(() => _tags.ListAsync(CancellationToken.None));
            if (version != _version)
            {
                return;
            }

            Tags.Clear();
            foreach (var tag in list)
            {
                Tags.Add(new TagRowViewModel(tag, this));
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            if (version == _version)
            {
                Error = LedgerText.Format(Strings.Tag_LoadFailed, ex.Message);
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

/// <summary>One tag in Settings → Tags.</summary>
/// <param name="tag">The tag and its counts.</param>
/// <param name="owner">The list (row buttons bind to its commands).</param>
public sealed class TagRowViewModel(TagUsage tag, TagsSettingsViewModel owner)
{
    /// <summary>The tag.</summary>
    public TagUsage Tag => tag;

    /// <summary>The list.</summary>
    public TagsSettingsViewModel Owner => owner;

    /// <summary>Id.</summary>
    public Guid Id => tag.Id;

    /// <summary>Name.</summary>
    public string Name => tag.Name;

    /// <summary>"12 transactions".</summary>
    public string CountText => LedgerText.Format(Strings.Tag_TransactionCount, tag.TransactionCount.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>"Show the 12 transactions tagged Trip".</summary>
    public string ShowName => LedgerText.Format(Strings.Tag_ShowTransactionsName, tag.TransactionCount.ToString("N0", CultureInfo.CurrentCulture), tag.Name);

    /// <summary>"Used by 2 rules", or null.</summary>
    public string? RuleText => tag.RuleCount == 0 ? null : LedgerText.Format(Strings.Tag_RuleCount, tag.RuleCount.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>Whether rules name the tag.</summary>
    public bool HasRules => tag.RuleCount > 0;

    /// <summary>The reserved Flagged tag (rename and merge are off).</summary>
    public bool IsReserved => tag.IsReserved;

    /// <summary>Whether the tag can be renamed or merged.</summary>
    public bool CanChange => !tag.IsReserved;

    /// <summary>Marks subscriptions.</summary>
    public bool IsSubscriptionTag => tag.IsSubscriptionTag;

    /// <summary>"Rename Trip".</summary>
    public string RenameName => LedgerText.Format(Strings.Tag_RenameName, tag.Name);

    /// <summary>"Merge Trip into another tag".</summary>
    public string MergeName => LedgerText.Format(Strings.Tag_MergeName, tag.Name);

    /// <summary>"Delete Trip".</summary>
    public string DeleteName => LedgerText.Format(Strings.Tag_DeleteName, tag.Name);
}

/// <summary>Rename a tag (every transaction keeps it; rules follow).</summary>
public sealed partial class RenameTagDialogViewModel : DialogViewModel
{
    private readonly ITagService _tags;
    private readonly TagUsage _tag;

    /// <summary>Creates the dialog.</summary>
    public RenameTagDialogViewModel(ITagService tags, TagUsage tag)
    {
        ArgumentNullException.ThrowIfNull(tag);
        _tags = tags;
        _tag = tag;
        Name = tag.Name;
    }

    /// <inheritdoc />
    public override string Title => Strings.Tag_RenameTitle;

    /// <summary>Explanation.</summary>
    public string Message => LedgerText.Format(Strings.Tag_RenameMessage, _tag.Name, _tag.TransactionCount.ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>New name.</summary>
    [ObservableProperty]
    public partial string? Name { get; set; }

    /// <summary>The renamed tag.</summary>
    public TagDto? Result { get; private set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        try
        {
            Result = await Task.Run(() => _tags.RenameAsync(_tag.Id, Name ?? string.Empty, CancellationToken.None));
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
    }
}

/// <summary>Merge a tag into another one (its transactions, rules and subscription mark move).</summary>
public sealed partial class MergeTagDialogViewModel : DialogViewModel
{
    private readonly ITagService _tags;

    /// <summary>Creates the dialog.</summary>
    public MergeTagDialogViewModel(ITagService tags, TagUsage source, IReadOnlyList<TagUsage> targets)
    {
        ArgumentNullException.ThrowIfNull(source);
        _tags = tags;
        Source = source;
        Targets = targets;
        Target = targets.FirstOrDefault();
    }

    /// <inheritdoc />
    public override string Title => Strings.Tag_MergeTitle;

    /// <summary>The tag that goes.</summary>
    public TagUsage Source { get; }

    /// <summary>Tags to merge into.</summary>
    public IReadOnlyList<TagUsage> Targets { get; }

    /// <summary>Whether there is a tag to merge into.</summary>
    public bool HasTargets => Targets.Count > 0;

    /// <summary>The tag that stays.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Message))]
    public partial TagUsage? Target { get; set; }

    /// <summary>What will happen, with counts.</summary>
    public string Message => Target is { } target
        ? LedgerText.Format(Strings.Tag_MergeMessage, Source.Name, target.Name, Source.TransactionCount.ToString("N0", CultureInfo.CurrentCulture))
            + (Source.RuleCount > 0 ? " " + LedgerText.Format(Strings.Tag_MergeRulesNote, Source.RuleCount.ToString("N0", CultureInfo.CurrentCulture)) : string.Empty)
        : Strings.Tag_MergeNoTargets;

    /// <summary>The outcome.</summary>
    public TagMergeResult? Result { get; private set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        if (Target is not { } target)
        {
            Error = Strings.Tag_MergeNoTargets;
            return false;
        }

        try
        {
            Result = await Task.Run(() => _tags.MergeAsync(Source.Id, target.Id, CancellationToken.None));
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
    }
}
