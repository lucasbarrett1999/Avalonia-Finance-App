using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Budget;
using Keel.Application.Categories;
using Keel.Application.Settings;
using Keel.Desktop.Controls;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Budget;
using Keel.Desktop.Views;
using Keel.Domain;

namespace Keel.Desktop.Tests;

/// <summary>M9a: the three-month budget view (F-BUD-2 P1, ADR 0090) and Flex mode (F-BUD-6, ADR 0091), driven headlessly over the PRD 6.4.7 ledger.</summary>
public sealed class BudgetViewsTests(Xunit.Abstractions.ITestOutputHelper output) : IDisposable
{
    private static readonly DateOnly August = BudgetTestLedger.August;
    private static readonly DateOnly September = new(2026, 9, 1);
    private static readonly DateOnly October = new(2026, 10, 1);
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static string Usd(long amount) => new Money(amount, "USD").Format();

    private async Task<(ShellWindow Window, ShellViewModel Shell, BudgetViewModel Vm, BudgetView View)> OpenAsync()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        shell.PrimaryItems.Single(i => i.PageType == typeof(BudgetViewModel)).NavigateCommand.Execute(null);
        var vm = _host.Get<BudgetViewModel>();
        await vm.SettleAsync();
        vm.GoToMonth(August);
        await vm.SettleAsync();
        return (window, shell, vm, window.Budget());
    }

    private static Control Cell(ItemsControl rows, BudgetRowViewModel row, int month, string column) =>
        rows.ContainerFromItem(row)!.GetVisualDescendants().OfType<Border>()
            .First(b => (b.Tag as string) == column && b.DataContext is BudgetMonthCellViewModel { Offset: var o } && o == month);

    [AvaloniaFact]
    public async Task Three_months_show_side_by_side_from_the_loaded_ledger_and_are_remembered()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, _, vm, view) = await OpenAsync();
        var loaded = vm.Ledger;
        view.Grid.Focus(NavigationMethod.Tab);

        window.Press(PhysicalKey.W);
        await vm.SettleAsync();
        vm.IsThreeMonths.ShouldBeTrue();
        vm.Ledger.ShouldBeSameAs(loaded);                                         // recompute only, no ledger reload
        vm.VisibleMonths.ShouldBe([August, September, October]);
        vm.MonthTitle.ShouldBe(BudgetText.MonthRange(August, October));
        _host.Get<IAppSettingsStore>().Current.BudgetThreeMonths.ShouldBeTrue();

        // Header: Ready to Assign per month; rows: Assigned/Activity/Available per month; groups sum.
        vm.MonthHeaders.Select(h => (h.Month, h.ReadyToAssignText)).ShouldBe([(August, Usd(1_100_00)), (September, Usd(1_100_00)), (October, Usd(1_100_00))]);
        var groceries = vm.Row(ledger.Groceries);
        groceries.Months.Select(c => c.AvailableText).ShouldBe([Usd(-50_00), Usd(0), Usd(0)]);
        groceries.Months[0].IsCreditOverspent.ShouldBeTrue();                    // yellow in August only
        vm.Row(ledger.PayVisa).Months.Select(c => c.Available).ShouldBe([300_00, 300_00, 300_00]);
        var everyday = vm.Groups.Single(g => g.Name == "Everyday");
        everyday.Months[0].AssignedText.ShouldBe(Usd(400_00));
        everyday.Months[0].ActivityText.ShouldBe(Usd(-450_00));

        // The rows use the three-month templates: three month blocks, no single-month pill.
        var rows = view.Named<ItemsControl>("BudgetRows");
        var container = rows.ContainerFromItem(groceries)!;
        container.GetVisualDescendants().OfType<Grid>().Count(g => g.Classes.Contains("monthCells")).ShouldBe(3);
        container.GetVisualDescendants().OfType<Border>().ShouldNotContain(b => b.Name == "AvailablePill");
        view.Named<Border>("MonthsHeader").IsEffectivelyVisible.ShouldBeTrue();

        // W again: one month, the rows rebuilt with the single-month template.
        window.Press(PhysicalKey.W);
        await vm.SettleAsync();
        vm.IsThreeMonths.ShouldBeFalse();
        vm.CurrentMonth.ShouldBe(August);
        rows.ContainerFromItem(vm.Row(ledger.Groceries))!.GetVisualDescendants().OfType<Border>().ShouldContain(b => b.Name == "AvailablePill");
        _host.Get<IAppSettingsStore>().Current.BudgetThreeMonths.ShouldBeFalse();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Cursor_editing_tab_move_money_and_month_keys_work_in_the_month_of_the_cursor()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, shell, vm, view) = await OpenAsync();
        vm.SetThreeMonths(true);
        await vm.SettleAsync();
        var groceries = vm.Row(ledger.Groceries);
        vm.Select(groceries, BudgetColumn.Assigned);
        view.Grid.Focus(NavigationMethod.Tab);

        // → walks Assigned, Activity, Available of August, then into September.
        window.Press(PhysicalKey.ArrowRight);
        window.Press(PhysicalKey.ArrowRight);
        vm.SelectedColumn.ShouldBe(BudgetColumn.Available);
        window.Press(PhysicalKey.ArrowRight);
        (vm.MonthOffset, vm.CurrentMonth, vm.SelectedColumn).ShouldBe((1, September, BudgetColumn.Assigned));
        groceries.Months[1].IsAssignedSelected.ShouldBeTrue();
        groceries.Months[0].IsAssignedSelected.ShouldBeFalse();
        vm.Inspector.Title.ShouldBe("Groceries");

        // Enter edits September's Assigned; Tab saves and edits the next row in the same month.
        window.Press(PhysicalKey.Enter);
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is MoneyTextBox { Name: "AssignedEditor", IsEffectivelyVisible: true, DataContext: BudgetCategoryRowViewModel { Name: "Groceries" } }, "September editor");
        window.Type("75");
        window.Press(PhysicalKey.Tab);
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is MoneyTextBox { DataContext: BudgetCategoryRowViewModel { Name: "Dining" } }, "next row editor");
        vm.Row(ledger.Dining).Months[1].IsEditing.ShouldBeTrue();
        window.Type("20");
        window.Press(PhysicalKey.Enter);
        await vm.SettleAsync();

        groceries.Months.Select(c => c.AssignedText).ShouldBe([Usd(400_00), Usd(75_00), Usd(0)]);
        vm.Row(ledger.Dining).Months[1].Assigned().ShouldBe(Usd(20_00));
        vm.MonthHeaders.Select(h => h.ReadyToAssignText).ShouldBe([Usd(1_005_00), Usd(1_005_00), Usd(1_005_00)]);   // later months' assignments count everywhere
        (await _host.Get<IBudgetService>().GetMonthAsync(September, CancellationToken.None)).Groups.SelectMany(g => g.Categories)
            .Single(c => c.Id == ledger.Groceries).Assigned.Amount.ShouldBe(75_00);

        // M moves money in September.
        vm.Select(groceries, BudgetColumn.Available);
        view.Grid.Focus(NavigationMethod.Tab);
        window.Press(PhysicalKey.M);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is MoveMoneyDialogViewModel, "move money");
        var dialog = (MoveMoneyDialogViewModel)shell.Dialogs.Current!;
        dialog.MonthText.ShouldContain(BudgetText.Month(September));
        (dialog.From!.Id, dialog.Amount).ShouldBe((ledger.Groceries, 75_00));
        dialog.To = dialog.Options.Single(o => o.Id == ledger.Dining);
        dialog.Amount = 25_00;
        await dialog.ConfirmCommand.ExecuteAsync(null);
        await vm.SettleAsync();
        groceries.Months[1].Available.ShouldBe(50_00);
        vm.Row(ledger.Dining).Months[1].Available.ShouldBe(45_00);
        groceries.Months[0].Available.ShouldBe(-50_00);                         // August untouched

        // Alt+→ shifts the window; the cursor stays in the same column of the window.
        window.Press(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        await vm.SettleAsync();
        (vm.WindowStart, vm.CurrentMonth, vm.MonthOffset).ShouldBe((September, October, 1));
        groceries.Months[0].AvailableText.ShouldBe(Usd(50_00));
        window.Press(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        await vm.SettleAsync();
        (vm.WindowStart, vm.CurrentMonth).ShouldBe((August, September));

        // Clicking October's Assigned edits it there; ← from a month's Assigned goes to the previous month's Available.
        var rows = view.Named<ItemsControl>("BudgetRows");
        var cell = Cell(rows, vm.Row(ledger.Rent), 2, "Assigned");
        cell.BringIntoView();                                                       // the grid may scroll sideways
        Dispatcher.UIThread.RunJobs();
        var point = cell.TranslatePoint(new Point(cell.Bounds.Width / 2, cell.Bounds.Height / 2), window)!.Value;
        window.MouseDown(point, MouseButton.Left);
        window.MouseUp(point, MouseButton.Left);
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is MoneyTextBox { DataContext: BudgetCategoryRowViewModel { Name: "Rent" } }, "October editor");
        (vm.CurrentMonth, vm.MonthOffset).ShouldBe((October, 2));
        window.Type("1500");
        window.Press(PhysicalKey.Escape);                                          // Esc cancels
        await vm.SettleAsync();
        vm.Row(ledger.Rent).Months[2].AssignedText.ShouldBe(Usd(0));
        view.Grid.Focus(NavigationMethod.Tab);
        window.Press(PhysicalKey.ArrowLeft);
        (vm.CurrentMonth, vm.SelectedColumn).ShouldBe((September, BudgetColumn.Available));

        // Undo from the header undoes the move, then the assignments, in order.
        await vm.UndoAsync();
        await vm.SettleAsync();
        groceries.Months[1].Available.ShouldBe(75_00);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Flex_view_sums_the_grid_by_kind_and_every_number_drills_down()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        await Task.Run(() => _host.Get<IBudgetService>().SetTargetAsync(new TargetDto(ledger.Rent, TargetType.MonthlySetAside, 1_500_00), CancellationToken.None));
        var (window, shell, vm, view) = await OpenAsync();
        view.Grid.Focus(NavigationMethod.Tab);

        window.Press(PhysicalKey.F);
        await vm.SettleAsync();
        vm.IsFlexView.ShouldBeTrue();
        view.Named<Border>("GridHost").IsEffectivelyVisible.ShouldBeFalse();
        view.Named<ContentControl>("FlexHost").IsEffectivelyVisible.ShouldBeTrue();
        _host.Get<IAppSettingsStore>().Current.BudgetFlexView.ShouldBeTrue();
        var flex = vm.Flex;
        flex.IncomeText.ShouldBe(Usd(3_000_00));
        flex.FixedText.ShouldBe(Usd(1_500_00));                                   // Rent: Fixed from its monthly target
        flex.NonMonthlyText.ShouldBe(Usd(0));
        flex.FlexText.ShouldBe(Usd(400_00));                                      // Groceries + Dining: Carry + Assigned
        flex.FlexSpentText.ShouldBe(LedgerText.Format(Resources.Strings.Flex_Spent, Usd(450_00)));
        flex.FlexLeftText.ShouldBe(LedgerText.Format(Resources.Strings.Flex_Over, Usd(50_00)));
        flex.IsFlexOver.ShouldBeTrue();
        flex.FlexProgress.ShouldBe(100);
        flex.PaceText.ShouldBe(Resources.Strings.Flex_MonthEnded);               // August is over
        view.Named<TextBlock>("FlexNumberText").Text.ShouldBe(Usd(400_00));
        vm.Row(ledger.PayVisa).Flex.ShouldBe(FlexKind.Unset);                     // card payments are in no group
        flex.HasCardPayments.ShouldBeTrue();

        // Clicking the Flex number shows the grid with only the Flex categories.
        var number = view.Named<Button>("FlexNumberButton");
        number.Command!.Execute(null);
        await vm.SettleAsync();
        vm.IsFlexView.ShouldBeFalse();
        vm.FlexFilter.ShouldBe(FlexKind.Flex);
        vm.Rows.OfType<BudgetCategoryRowViewModel>().Select(r => r.Id).ShouldBe([ledger.Groceries, ledger.Dining]);
        vm.Rows.OfType<BudgetGroupRowViewModel>().Select(g => g.Name).ShouldBe(["Everyday"]);
        view.Named<Border>("FlexFilterBanner").IsEffectivelyVisible.ShouldBeTrue();
        vm.SelectedRow.ShouldBe(vm.Row(ledger.Groceries));
        view.Named<Button>("ClearFlexFilterButton").Command!.Execute(null);
        await vm.SettleAsync();
        vm.Rows.ShouldContain(vm.Row(ledger.Rent));

        // Fixed drills down to Rent.
        vm.SetFlexView(true);
        await vm.SettleAsync();
        flex.ShowFixedCommand.Execute(null);
        await vm.SettleAsync();
        vm.Rows.OfType<BudgetCategoryRowViewModel>().Select(r => r.Id).ShouldBe([ledger.Rent]);

        // Income drills down to this month's Ready to Assign transactions.
        vm.SetFlexView(true);
        await vm.SettleAsync();
        flex.ShowIncomeCommand.Execute(null);
        var register = shell.CurrentPage.ShouldBeOfType<AccountsViewModel>();
        await register.SettleAsync();
        register.SelectedCategoryFilter.Id.ShouldBe(Keel.Domain.Entities.SystemIds.ReadyToAssignCategory);
        register.SearchText.ShouldBe("date:2026-08");
        register.RowCount.ShouldBe(1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Categories_are_tagged_in_the_inspector_and_in_manage_categories_and_tags_undo()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, shell, vm, _) = await OpenAsync();
        vm.IsInspectorOpen = true;
        vm.Select(vm.Row(ledger.Dining), BudgetColumn.Name);
        await vm.SettleAsync();
        var inspector = vm.Inspector;
        inspector.CanTagFlex.ShouldBeTrue();
        inspector.SelectedFlex!.Value.ShouldBe(FlexKind.Unset);
        inspector.FlexHint.ShouldBe(LedgerText.Format(Resources.Strings.Flex_HintAutomaticNoTarget, BudgetText.FlexKind(FlexKind.Flex)));

        inspector.SelectedFlex = inspector.FlexChoices.Single(c => c.Value == FlexKind.NonMonthly);
        await UiTestHelpers.WaitUntilAsync(() => vm.Row(ledger.Dining).FlexTag == FlexKind.NonMonthly, "tag saved and reloaded");
        await vm.SettleAsync();
        vm.Row(ledger.Dining).Flex.ShouldBe(FlexKind.NonMonthly);
        inspector.FlexHint.ShouldBe(LedgerText.Format(Resources.Strings.Flex_HintTagged, BudgetText.FlexKind(FlexKind.NonMonthly)));
        vm.Month!.Flex!.NonMonthly.CategoryIds.ShouldBe([ledger.Dining]);
        shell.Status.Message.ShouldBe(LedgerText.Format(Resources.Strings.Flex_Tagged, "Dining", BudgetText.FlexKind(FlexKind.NonMonthly)));
        shell.Status.CanUndo.ShouldBeTrue();

        await vm.UndoAsync();
        await vm.SettleAsync();
        vm.Row(ledger.Dining).FlexTag.ShouldBe(FlexKind.Unset);
        inspector.SelectedFlex!.Value.ShouldBe(FlexKind.Unset);

        // Card payment categories cannot be tagged.
        vm.Select(vm.Row(ledger.PayVisa), BudgetColumn.Name);
        await vm.SettleAsync();
        inspector.CanTagFlex.ShouldBeFalse();

        // Manage categories: one picker per user category, saved at once.
        var manage = vm.ManageCategoriesAsync();
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is ManageCategoriesDialogViewModel, "dialog");
        var dialog = (ManageCategoriesDialogViewModel)shell.Dialogs.Current!;
        await UiTestHelpers.WaitUntilAsync(() => window.GetVisualDescendants().OfType<ComboBox>().Any(c => c.Name == "FlexKindBox"), "pickers shown");
        var rent = dialog.Groups.SelectMany(g => g.Categories).Single(c => c.Id == ledger.Rent);
        rent.SelectedFlex.Value.ShouldBe(FlexKind.Unset);
        rent.SelectedFlex = rent.FlexChoices.Single(c => c.Value == FlexKind.Fixed);
        await rent.FlexSaving;
        dialog.Error.ShouldBeNull();
        dialog.Groups.SelectMany(g => g.Categories).Single(c => c.Id == ledger.Rent).SelectedFlex.Value.ShouldBe(FlexKind.Fixed);
        dialog.Groups.SelectMany(g => g.Categories).ShouldNotContain(c => c.Id == ledger.PayVisa && c.IsEditable);
        dialog.ConfirmCommand.Execute(null);
        await manage;
        await vm.SettleAsync();
        vm.Row(ledger.Rent).Flex.ShouldBe(FlexKind.Fixed);
        (await _host.Get<ICategoryService>().GetCategoriesAsync(true, CancellationToken.None)).Single(c => c.Id == ledger.Rent).FlexKind.ShouldBe(FlexKind.Fixed);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_views_are_in_the_shortcut_registry_the_palette_and_the_view_menu()
    {
        await BudgetTestLedger.CreateAsync(_host);
        var (window, shell, vm, _) = await OpenAsync();
        var registry = _host.Get<ShortcutRegistry>();
        registry.Find("budget-three-months")!.Keys.ShouldBe("W");
        registry.Find("budget-flex")!.Keys.ShouldBe("F");
        _host.Get<SettingsViewModel>().Shortcuts.ShouldContain(new ShortcutViewModel(Resources.Strings.Flex_Shortcut, "F"));

        var commands = _host.Get<AppCommands>();
        shell.NavigateTo<HomeViewModel>();
        commands.Build(shell).Single(c => c.Id == "budget-three-months").Execute();
        await vm.SettleAsync();
        shell.CurrentPage.ShouldBeOfType<BudgetViewModel>();
        vm.IsThreeMonths.ShouldBeTrue();

        var view = commands.BuildMenu(shell, macOS: false).Single(n => n.Header == Resources.Strings.Menu_View);
        var flexItem = view.Children!.Single(n => n.Command?.Id == "budget-flex");
        flexItem.Command!.Keys.ShouldBe("F");
        flexItem.Command.Execute();
        await vm.SettleAsync();
        vm.IsFlexView.ShouldBeTrue();
        vm.IsThreeMonths.ShouldBeFalse();                                          // the Flex view shows one month
        window.Close();
    }

    [AvaloniaFact]
    public async Task Three_month_window_shifts_stay_fast_over_the_100k_fixture()
    {
        var fixture = await Task.Run(() => Keel.Infrastructure.Fixtures.LedgerFixtureGenerator.GenerateAsync(
            _host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Keel.Infrastructure.Persistence.KeelDbContext>>(),
            new Keel.Infrastructure.Fixtures.LedgerFixtureOptions(EndDate: new DateOnly(2026, 9, 30)), CancellationToken.None));
        fixture.TransactionCount.ShouldBe(100_000);
        _host.Get<IAppSettingsStore>().Update(s => s with { BudgetThreeMonths = true });
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var vm = _host.Get<BudgetViewModel>();
        vm.GoToMonth(new DateOnly(2026, 6, 1));
        shell.NavigateTo<BudgetViewModel>();
        await vm.SettleAsync();
        vm.IsThreeMonths.ShouldBeTrue();
        vm.Rows.Count.ShouldBeGreaterThan(40);
        var loaded = vm.Ledger;

        var times = new List<double>();
        var vmTimes = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            if (i % 2 == 0)
            {
                vm.PreviousMonth();
            }
            else
            {
                vm.NextMonth();
            }

            var vmOnly = watch.Elapsed.TotalMilliseconds;
            window.UpdateLayout();
            watch.Stop();
            vm.IsLoading.ShouldBeFalse();
            times.Add(watch.Elapsed.TotalMilliseconds);
            vmTimes.Add(vmOnly);
            Dispatcher.UIThread.RunJobs();
        }

        // Toggling the mode recomputes nothing and reloads nothing: the months are already computed.
        var toggle = System.Diagnostics.Stopwatch.StartNew();
        vm.SetThreeMonths(false);
        window.UpdateLayout();
        vm.SetThreeMonths(true);
        window.UpdateLayout();
        toggle.Stop();
        vm.Ledger.ShouldBeSameAs(loaded);
        times.Sort();
        vmTimes.Sort();
        output.WriteLine($"Three-month window shift over 100k transactions: view model median {vmTimes[vmTimes.Count / 2]:F1} ms, "
            + $"with layout median {times[times.Count / 2]:F1} ms (max {times[^1]:F1} ms); mode toggle and back {toggle.Elapsed.TotalMilliseconds:F1} ms");

        // Three months lay out three times the cells of one month; ADR 0090 sets their budget at 250 ms (one month stays under 100 ms, BudgetTests).
        vmTimes[vmTimes.Count / 2].ShouldBeLessThan(100);
        times[times.Count / 2].ShouldBeLessThan(250);
        toggle.Elapsed.TotalMilliseconds.ShouldBeLessThan(2_000);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_mode_is_restored_from_settings_and_a_new_window_opens_in_it()
    {
        _host.Get<IAppSettingsStore>().Update(s => s with { BudgetThreeMonths = true });
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, _, vm, view) = await OpenAsync();
        vm.IsThreeMonths.ShouldBeTrue();
        vm.Row(ledger.Groceries).Months[0].AvailableText.ShouldBe(Usd(-50_00));
        view.Named<ScrollViewer>("GridHScroller").HorizontalScrollBarVisibility.ShouldBe(Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        window.Close();
    }
}

internal static class BudgetMonthCellTestExtensions
{
    public static string Assigned(this BudgetMonthCellViewModel cell) => cell.AssignedText;
}
