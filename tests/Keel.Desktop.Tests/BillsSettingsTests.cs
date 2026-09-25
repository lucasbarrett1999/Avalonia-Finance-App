using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Categories;
using Keel.Application.Recurring;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Budget;
using Keel.Desktop.ViewModels.Settings;
using Keel.Desktop.Views;
using Keel.Desktop.Views.Settings;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

/// <summary>Settings → Bills and subscriptions: the groups and tags that mark subscriptions (F-REC-2, carried over from M5).</summary>
public sealed class BillsSettingsTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    [AvaloniaFact]
    public async Task Checking_a_group_and_a_tag_saves_the_subscription_designations()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;

        // Empty states first: no groups, no tags.
        shell.NavigateToSettings("Bills");
        var vm = _host.Get<BillsSettingsViewModel>();
        await vm.Loading;
        Dispatcher.UIThread.RunJobs();
        vm.HasNoGroups.ShouldBeTrue();
        vm.HasNoTags.ShouldBeTrue();

        await Task.Run(() => _host.Get<ICategoryService>().ApplyTemplateAsync(BudgetTemplate.All[0].Groups, CancellationToken.None));
        await using (var db = await _host.Get<IDbContextFactory<KeelDbContext>>().CreateDbContextAsync())
        {
            db.Tags.Add(new Keel.Domain.Entities.Tag { Name = "streaming" });
            await db.SaveChangesAsync();
        }

        await vm.LoadAsync();
        Dispatcher.UIThread.RunJobs();
        vm.Groups.Count.ShouldBe(BudgetTemplate.All[0].Groups.Count);
        vm.Tags.ShouldHaveSingleItem().Name.ShouldBe("streaming");
        window.GetVisualDescendants().OfType<BillsSettingsView>().Single().Named<ItemsControl>("GroupList").ItemCount.ShouldBe(vm.Groups.Count);

        vm.Groups[0].IsChecked = true;
        await vm.Saving;
        vm.Tags[0].IsChecked = true;
        await vm.Saving;
        var saved = await Task.Run(() => _host.Get<IRecurringService>().GetSubscriptionDesignationsAsync(CancellationToken.None));
        saved.GroupIds.ShouldBe([vm.Groups[0].Id]);
        saved.TagIds.ShouldBe([vm.Tags[0].Id]);

        // Reloading shows the saved state.
        await vm.LoadAsync();
        vm.Groups[0].IsChecked.ShouldBeTrue();
        vm.Tags[0].IsChecked.ShouldBeTrue();
        window.Close();
    }
}
