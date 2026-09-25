using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Categories;
using Keel.Application.Recurring;
using Keel.Application.Tags;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Settings;

/// <summary>
/// Settings → Bills and subscriptions (F-REC-2): which category groups and tags mark a recurring item as a
/// subscription rather than a bill. Saved in the budget file through <see cref="IRecurringService"/>.
/// </summary>
public sealed partial class BillsSettingsViewModel(IRecurringService recurring, ICategoryService categories, ITagService tags, StatusService status) : ViewModelBase
{
    private bool _loading;

    /// <summary>Category groups (checked = subscription group).</summary>
    public ObservableCollection<DesignationOption> Groups { get; } = [];

    /// <summary>Tags (checked = subscription tag).</summary>
    public ObservableCollection<DesignationOption> Tags { get; } = [];

    /// <summary>No tags exist in this file.</summary>
    [ObservableProperty]
    public partial bool HasNoTags { get; private set; }

    /// <summary>No category groups exist yet.</summary>
    [ObservableProperty]
    public partial bool HasNoGroups { get; private set; }

    /// <summary>Loading.</summary>
    [ObservableProperty]
    public partial bool IsLoading { get; private set; }

    /// <summary>Load failure.</summary>
    [ObservableProperty]
    public partial string? Error { get; private set; }

    /// <summary>The latest load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The latest save (tests await it).</summary>
    public Task Saving { get; private set; } = Task.CompletedTask;

    /// <summary>Loads groups, tags and the current designations.</summary>
    public Task LoadAsync() => Loading = LoadCoreAsync();

    private async Task LoadCoreAsync()
    {
        IsLoading = true;
        Error = null;
        try
        {
            var all = await Task.Run(() => categories.GetCategoriesAsync(includeHidden: false, CancellationToken.None));
            var groups = all.Where(c => !c.IsSystem && !c.IsCreditCardPayment).GroupBy(c => (c.GroupId, c.GroupName)).Select(g => g.Key).ToList();
            var tagList = await Task.Run(() => tags.GetTagsAsync(CancellationToken.None));
            var designations = await Task.Run(() => recurring.GetSubscriptionDesignationsAsync(CancellationToken.None));
            _loading = true;
            Groups.Clear();
            foreach (var (id, name) in groups)
            {
                Groups.Add(new DesignationOption(id, name, Changed) { IsChecked = designations.GroupIds.Contains(id) });
            }

            Tags.Clear();
            foreach (var tag in tagList)
            {
                Tags.Add(new DesignationOption(tag.Id, tag.Name, Changed) { IsChecked = designations.TagIds.Contains(tag.Id) });
            }

            HasNoGroups = Groups.Count == 0;
            HasNoTags = Tags.Count == 0;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            Error = LedgerText.Format(Strings.Settings_BillsLoadFailed, ex.Message);
        }
        finally
        {
            _loading = false;
            IsLoading = false;
        }
    }

    private void Changed()
    {
        if (!_loading)
        {
            Saving = SaveAsync();
        }
    }

    private async Task SaveAsync()
    {
        var designations = new SubscriptionDesignations(
            Groups.Where(g => g.IsChecked).Select(g => g.Id).ToList(),
            Tags.Where(t => t.IsChecked).Select(t => t.Id).ToList());
        try
        {
            await Task.Run(() => recurring.SetSubscriptionDesignationsAsync(designations, CancellationToken.None));
            status.Show(Strings.Settings_BillsSaved);
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            status.Show(ex.Message, isError: true);
        }
    }
}

/// <summary>A group or tag that can mark subscriptions.</summary>
/// <param name="id">Group or tag id.</param>
/// <param name="name">Name.</param>
/// <param name="changed">Called after the check box changes.</param>
public sealed partial class DesignationOption(Guid id, string name, Action changed) : ObservableObject
{
    /// <summary>Id.</summary>
    public Guid Id => id;

    /// <summary>Name.</summary>
    public string Name => name;

    /// <summary>Marks subscriptions.</summary>
    [ObservableProperty]
    public partial bool IsChecked { get; set; }

    partial void OnIsCheckedChanged(bool value) => changed();
}
