using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Budget;
using Keel.Desktop.Controls;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Budget;
using Keel.Desktop.Views;
using Keel.Domain;
using Xunit.Abstractions;

namespace Keel.Desktop.Tests;

public sealed class BudgetTests(ITestOutputHelper output) : IDisposable
{
    private static readonly DateOnly August = BudgetTestLedger.August;
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private async Task<(ShellWindow Window, ShellViewModel Shell, BudgetViewModel Vm, BudgetView View)> OpenAsync(DateOnly? month = null)
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        shell.PrimaryItems.Single(i => i.PageType == typeof(BudgetViewModel)).NavigateCommand.Execute(null);
        var vm = _host.Get<BudgetViewModel>();
        await vm.SettleAsync();
        vm.GoToMonth(month ?? August);
        await vm.SettleAsync();
        return (window, shell, vm, window.Budget());
    }

    [AvaloniaFact]
    public async Task Worked_example_6_4_7_shows_the_expected_numbers_and_colours()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, _, vm, view) = await OpenAsync();

        vm.MonthTitle.ShouldBe(BudgetText.Month(August));
        vm.ReadyToAssign.ShouldBe(1_100_00);
        vm.IsReadyToAssignNegative.ShouldBeFalse();
        view.Named<TextBlock>("ReadyToAssignText").Text.ShouldBe(new Money(1_100_00, "USD").Format());

        var groceries = vm.Row(ledger.Groceries);
        groceries.Available.ShouldBe(-50_00);
        groceries.IsCreditOverspent.ShouldBeTrue();      // yellow
        groceries.IsCashOverspent.ShouldBeFalse();
        vm.Row(ledger.Rent).Available.ShouldBe(0);
        vm.Row(ledger.Rent).IsZero.ShouldBeTrue();       // gray

        var pay = vm.Row(ledger.PayVisa);
        pay.Available.ShouldBe(300_00);
        pay.IsPositive.ShouldBeTrue();                   // green
        pay.CardDetailText.ShouldContain(new Money(50_00, "USD").Format());
        pay.IsCardUncovered.ShouldBeTrue();

        // The rendered pill carries the credit class.
        var container = view.FindNamed<ItemsControl>("BudgetRows")!.ContainerFromItem(groceries)!;
        container.GetVisualDescendants().OfType<Border>().Single(b => b.Name == "AvailablePill").Classes.ShouldContain("credit");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Assigning_in_a_cell_updates_ready_to_assign_and_enter_moves_down()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, _, vm, view) = await OpenAsync();
        var groceries = vm.Row(ledger.Groceries);
        vm.Select(groceries, BudgetColumn.Assigned);
        view.Grid.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();

        window.Press(PhysicalKey.Enter);
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is MoneyTextBox { Name: "AssignedEditor", DataContext: var d } && ReferenceEquals(d, groceries), "editor focused");
        window.Type("400+50");
        window.Press(PhysicalKey.Enter);
        await vm.SettleAsync();

        vm.ReadyToAssign.ShouldBe(1_050_00);
        vm.Row(ledger.Groceries).Assigned.ShouldBe(450_00);
        vm.Row(ledger.Groceries).Available.ShouldBe(0);
        vm.Row(ledger.PayVisa).Available.ShouldBe(350_00);                       // 6.4.7: covered becomes 450
        view.Named<TextBlock>("ReadyToAssignText").Text.ShouldBe(new Money(1_050_00, "USD").Format());
        vm.SelectedRow.ShouldBe(vm.Row(ledger.Dining));                          // Enter commits and moves down
        vm.SelectedColumn.ShouldBe(BudgetColumn.Assigned);
        vm.EditingRow.ShouldBeNull();
        window.Focused().ShouldBe(view.Grid);

        // Typing a digit on a selected Assigned cell starts editing with it.
        window.Type("7");
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is MoneyTextBox { Text: "7" }, "typed into the editor");
        window.Press(PhysicalKey.Escape);
        await vm.SettleAsync();
        vm.Row(ledger.Dining).Assigned.ShouldBe(0);                              // Esc cancels
        window.Close();
    }

    [AvaloniaFact]
    public async Task Tab_moves_down_the_assigned_column_and_arrows_move_the_cursor()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, _, vm, view) = await OpenAsync();
        vm.BeginEdit(vm.Row(ledger.Rent));
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is MoneyTextBox, "rent editor");
        window.Type("1600");
        window.Press(PhysicalKey.Tab);
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is MoneyTextBox { DataContext: BudgetCategoryRowViewModel { Name: "Groceries" } }, "groceries editor");
        window.Type("500");
        window.Press(PhysicalKey.Tab);
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is MoneyTextBox { DataContext: BudgetCategoryRowViewModel { Name: "Dining" } }, "dining editor");
        window.Press(PhysicalKey.Tab, RawInputModifiers.Shift);
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is MoneyTextBox { DataContext: BudgetCategoryRowViewModel { Name: "Groceries" } }, "back up");
        window.Press(PhysicalKey.Escape);
        await vm.SettleAsync();

        vm.Row(ledger.Rent).Assigned.ShouldBe(1_600_00);
        vm.Row(ledger.Groceries).Assigned.ShouldBe(500_00);
        vm.ReadyToAssign.ShouldBe(3_000_00 - 1_600_00 - 500_00);

        // Arrow keys move the cell cursor across rows (group rows included) and columns.
        vm.Select(vm.Row(ledger.Groceries), BudgetColumn.Assigned);
        view.Grid.Focus(NavigationMethod.Tab);
        window.Press(PhysicalKey.ArrowDown);
        vm.SelectedRow.ShouldBe(vm.Row(ledger.Dining));
        window.Press(PhysicalKey.ArrowRight);
        vm.SelectedColumn.ShouldBe(BudgetColumn.Activity);
        window.Press(PhysicalKey.ArrowUp);
        window.Press(PhysicalKey.ArrowUp);
        vm.SelectedRow.ShouldBeOfType<BudgetGroupRowViewModel>().Name.ShouldBe("Everyday");
        window.Press(PhysicalKey.Enter);                                          // Enter on a group row collapses it
        vm.Rows.ShouldNotContain(vm.Row(ledger.Groceries));
        window.Press(PhysicalKey.Space);
        vm.Rows.ShouldContain(vm.Row(ledger.Groceries));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Move_money_with_the_dialog_and_undo_it()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, shell, vm, view) = await OpenAsync();
        vm.Select(vm.Row(ledger.Groceries), BudgetColumn.Available);
        view.Grid.Focus(NavigationMethod.Tab);
        window.Press(PhysicalKey.M);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is MoveMoneyDialogViewModel, "dialog");
        var dialog = (MoveMoneyDialogViewModel)shell.Dialogs.Current!;
        dialog.From!.Id.ShouldBeNull();                                           // overspent: from Ready to Assign
        dialog.To!.Id.ShouldBe(ledger.Groceries);
        dialog.Amount.ShouldBe(50_00);
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is MoneyTextBox { Name: "AmountBox" }, "amount focused");
        window.Type("60");                                                        // replaces the selected 50.00
        window.Press(PhysicalKey.Enter);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null, "dialog closed");
        await vm.SettleAsync();

        vm.ReadyToAssign.ShouldBe(1_040_00);
        vm.Row(ledger.Groceries).Available.ShouldBe(10_00);
        shell.Status.CanUndo.ShouldBeTrue();

        // Category to category, then undo from the header.
        await Task.WhenAll(vm.MoveMoneyAsync(ledger.PayVisa), SetAndConfirmAsync(shell, ledger.Dining, 25_00));
        await vm.SettleAsync();
        vm.Row(ledger.Dining).Available.ShouldBe(25_00);
        vm.Row(ledger.PayVisa).Assigned.ShouldBe(-25_00);
        vm.ReadyToAssign.ShouldBe(1_040_00);

        view.Named<Button>("BudgetUndoButton").Command!.Execute(null);
        await vm.SettleAsync();
        vm.Row(ledger.Dining).Available.ShouldBe(0);
        vm.Row(ledger.PayVisa).Assigned.ShouldBe(0);
        await vm.UndoAsync();
        await vm.SettleAsync();
        vm.ReadyToAssign.ShouldBe(1_100_00);
        window.Close();
    }

    private static async Task SetAndConfirmAsync(ShellViewModel shell, Guid to, long amount)
    {
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is MoveMoneyDialogViewModel, "dialog");
        var dialog = (MoveMoneyDialogViewModel)shell.Dialogs.Current!;
        dialog.To = dialog.Options.Single(o => o.Id == to);
        dialog.Amount = amount;
        await dialog.ConfirmCommand.ExecuteAsync(null);
    }

    [AvaloniaFact]
    public async Task Dropping_an_available_pill_on_another_row_moves_money()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, shell, vm, view) = await OpenAsync();
        var rows = view.Named<ItemsControl>("BudgetRows");
        var target = rows.ContainerFromItem(vm.Row(ledger.Dining))!;
        var point = target.TranslatePoint(new Avalonia.Point(40, 10), window)!.Value;
        var data = new DataTransfer();
        data.Add(DataTransferItem.Create(BudgetView.CategoryFormat, ledger.PayVisa.ToString("D")));

        window.DragDrop(point, Avalonia.Input.Raw.RawDragEventType.DragEnter, data, DragDropEffects.Move);
        window.DragDrop(point, Avalonia.Input.Raw.RawDragEventType.DragOver, data, DragDropEffects.Move);
        vm.Row(ledger.Dining).IsDropTarget.ShouldBeTrue();
        window.DragDrop(point, Avalonia.Input.Raw.RawDragEventType.Drop, data, DragDropEffects.Move);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is MoveMoneyDialogViewModel, "dialog");
        var dialog = (MoveMoneyDialogViewModel)shell.Dialogs.Current!;
        (dialog.From!.Id, dialog.To!.Id, dialog.Amount).ShouldBe((ledger.PayVisa, ledger.Dining, 300_00));
        vm.Row(ledger.Dining).IsDropTarget.ShouldBeFalse();
        dialog.Amount = 20_00;
        await dialog.ConfirmCommand.ExecuteAsync(null);
        await vm.SettleAsync();
        vm.Row(ledger.Dining).Available.ShouldBe(20_00);
        vm.Row(ledger.PayVisa).Available.ShouldBe(280_00);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Setting_a_target_shows_the_underfunded_badge_and_fund_targets_funds_it()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, shell, vm, view) = await OpenAsync();
        vm.IsInspectorOpen = false;
        vm.Select(vm.Row(ledger.Dining), BudgetColumn.Name);
        view.Grid.Focus(NavigationMethod.Tab);
        window.Press(PhysicalKey.T);
        vm.IsInspectorOpen.ShouldBeTrue();
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is MoneyTextBox { Name: "TargetAmountBox" }, "target amount focused");
        vm.Inspector.SelectedTargetType = vm.Inspector.TargetTypes.Single(t => t.Value == TargetType.MonthlySetAside);
        window.Type("120");
        window.Press(PhysicalKey.Enter);
        await vm.SettleAsync();

        var dining = vm.Row(ledger.Dining);
        dining.HasTarget.ShouldBeTrue();
        dining.IsUnderfunded.ShouldBeTrue();
        dining.TargetBadgeText.ShouldBe(LedgerText.Format(Resources.Strings.Budget_TargetNeeds, new Money(120_00, "USD").Format()));
        vm.Inspector.TargetProgress.ShouldContain(LedgerText.Format(Resources.Strings.Inspector_Underfunded, new Money(120_00, "USD").Format()));

        view.Grid.Focus(NavigationMethod.Tab);
        window.Press(PhysicalKey.F, _host.Get<PlatformShortcuts>().Command() | RawInputModifiers.Shift);
        await vm.SettleAsync();
        dining = vm.Row(ledger.Dining);
        dining.Assigned.ShouldBe(120_00);
        dining.IsFunded.ShouldBeTrue();
        dining.TargetBadgeText.ShouldBe(Resources.Strings.Budget_TargetFunded);
        vm.ReadyToAssign.ShouldBe(1_100_00 - 120_00);
        shell.Status.Message.ShouldBe(LedgerText.Format(Resources.Strings.Budget_FundDone, new Money(120_00, "USD").Format(), 1));
        window.Close();
    }

    [AvaloniaFact]
    public async Task Months_switch_in_memory_with_the_keyboard()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, _, vm, view) = await OpenAsync();
        view.Grid.Focus(NavigationMethod.Tab);
        var groceries = vm.Row(ledger.Groceries);

        window.Press(PhysicalKey.ArrowRight, RawInputModifiers.Alt);
        vm.CurrentMonth.ShouldBe(new DateOnly(2026, 9, 1));
        vm.Row(ledger.Groceries).ShouldBeSameAs(groceries);                       // rows updated in place
        groceries.Available.ShouldBe(0);                                          // negative Available does not carry
        vm.Row(ledger.PayVisa).Available.ShouldBe(300_00);                        // Carry(Pay_Visa) = 300
        vm.ReadyToAssign.ShouldBe(1_100_00);                                      // credit overspending does not reduce RTA
        vm.IsLoading.ShouldBeFalse();

        window.Press(PhysicalKey.ArrowLeft, RawInputModifiers.Alt);
        vm.CurrentMonth.ShouldBe(August);
        groceries.Available.ShouldBe(-50_00);

        // Jump to a month outside the loaded range with the month picker, then back to today.
        var picker = view.Named<Button>("MonthPickerButton");
        picker.Flyout!.ShowAt(picker);
        Dispatcher.UIThread.RunJobs();
        vm.PickerYear.ShouldBe(2026);
        vm.PickerPreviousYearCommand.Execute(null);
        vm.PickerPreviousYearCommand.Execute(null);
        vm.PickerPreviousYearCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var panel = (Control)((Flyout)picker.Flyout).Content!;
        var january = panel.GetVisualDescendants().OfType<Button>().Single(b => b.DataContext is MonthChoice { Month: { Year: 2023, Month: 1 } });
        january.Command!.Execute(january.CommandParameter);
        picker.Flyout.Hide();
        await vm.SettleAsync();
        vm.MonthTitle.ShouldBe(BudgetText.Month(new DateOnly(2023, 1, 1)));
        vm.ReadyToAssign.ShouldBe(-1_900_00);                                     // assigned in future months
        vm.IsReadyToAssignNegative.ShouldBeTrue();
        view.Named<Border>("OverAssignedBanner").IsVisible.ShouldBeTrue();
        view.Named<Button>("ReadyToAssignPill").Classes.ShouldContain("negative");
        vm.GoToTodayCommand.Execute(null);
        await vm.SettleAsync();
        vm.IsThisMonth.ShouldBeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Month_switch_takes_less_than_100_ms_over_the_100k_fixture()
    {
        var fixture = await Task.Run(() => Keel.Infrastructure.Fixtures.LedgerFixtureGenerator.GenerateAsync(
            _host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Keel.Infrastructure.Persistence.KeelDbContext>>(),
            new Keel.Infrastructure.Fixtures.LedgerFixtureOptions(EndDate: new DateOnly(2026, 9, 30)), CancellationToken.None));
        fixture.TransactionCount.ShouldBe(100_000);
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        var vm = _host.Get<BudgetViewModel>();
        vm.GoToMonth(new DateOnly(2026, 9, 1));
        var load = Stopwatch.StartNew();
        shell.PrimaryItems.Single(i => i.PageType == typeof(BudgetViewModel)).NavigateCommand.Execute(null);
        await vm.Loading;
        load.Stop();
        await vm.SettleAsync();
        vm.Rows.Count.ShouldBeGreaterThan(40);

        var times = new List<double>();
        var renders = new List<double>();
        for (var i = 0; i < 12; i++)
        {
            var watch = Stopwatch.StartNew();
            if (i % 2 == 0)
            {
                vm.PreviousMonth();
            }
            else
            {
                vm.NextMonth();
            }

            window.UpdateLayout();                                                // the new numbers measured and arranged
            watch.Stop();
            vm.IsLoading.ShouldBeFalse();
            times.Add(watch.Elapsed.TotalMilliseconds);
            var render = Stopwatch.StartNew();
            Dispatcher.UIThread.RunJobs();                                        // headless: software rendering of the frame
            renders.Add(render.Elapsed.TotalMilliseconds);
        }

        times.Sort();
        var median = times[times.Count / 2];
        renders.Sort();
        output.WriteLine($"First budget load with 100k transactions: {load.ElapsedMilliseconds} ms; month switch (view model + layout) median {median:F1} ms, "
            + $"max {times[^1]:F1} ms; headless frame rendering afterwards median {renders[renders.Count / 2]:F1} ms");
        median.ShouldBeLessThan(100);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Activity_opens_the_register_filtered_to_the_category_and_month()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, shell, vm, view) = await OpenAsync();
        var container = view.Named<ItemsControl>("BudgetRows").ContainerFromItem(vm.Row(ledger.Groceries))!;
        var link = container.GetVisualDescendants().OfType<Button>().Single(b => b.Classes.Contains("activityLink"));
        link.Command.ShouldBeNull();
        link.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();

        var register = shell.CurrentPage.ShouldBeOfType<AccountsViewModel>();
        await register.SettleAsync();
        register.IsAllAccounts.ShouldBeTrue();
        register.SelectedCategoryFilter.Id.ShouldBe(ledger.Groceries);
        register.SearchText.ShouldBe("date:2026-08");
        register.RowCount.ShouldBe(2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Ledger_changes_refresh_the_grid_in_place()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, _, vm, _) = await OpenAsync();
        var dining = vm.Row(ledger.Dining);
        await Task.Run(() => _host.Get<Keel.Application.Ledger.ITransactionService>().SaveAsync(
            new Keel.Application.Ledger.SaveTransactionRequest(null, ledger.Checking, new DateOnly(2026, 8, 30), -20_00, "Cafe", ledger.Dining, null), CancellationToken.None));
        await vm.SettleAsync();
        vm.Row(ledger.Dining).ShouldBeSameAs(dining);
        dining.Activity.ShouldBe(-20_00);
        dining.IsCashOverspent.ShouldBeTrue();                                    // red
        window.Close();
    }

    [AvaloniaFact]
    public async Task Empty_state_offers_templates_and_category_management()
    {
        var (window, shell, vm, view) = await OpenAsync(new DateOnly(2026, 9, 1));
        vm.ShowEmptyState.ShouldBeTrue();
        view.Named<EmptyState>("BudgetEmptyState").IsEffectivelyVisible.ShouldBeTrue();

        await vm.ApplyTemplateAsync(vm.Templates[0]);
        await vm.SettleAsync();
        vm.ShowGrid.ShouldBeTrue();
        vm.Groups.Select(g => g.Name).ShouldBe(vm.Templates[0].Groups.Select(g => g.Name));

        // Manage categories: add one, rename it, delete it (no history: no replacement needed).
        var manage = vm.ManageCategoriesAsync();
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is ManageCategoriesDialogViewModel, "dialog");
        var dialog = (ManageCategoriesDialogViewModel)shell.Dialogs.Current!;
        var group = dialog.Groups.First(g => !g.IsSystem);
        group.NewCategoryName = "Coffee";
        await group.AddCategoryCommand.ExecuteAsync(null);
        var coffee = dialog.Groups.First(g => g.Id == group.Id).Categories.Single(c => c.Name == "Coffee");
        coffee.EditName = "Coffee beans";
        await coffee.RenameCommand.ExecuteAsync(null);
        dialog.Groups.First(g => g.Id == group.Id).Categories.ShouldContain(c => c.Name == "Coffee beans");
        dialog.ConfirmCommand.Execute(null);
        await manage;
        await vm.SettleAsync();
        vm.Groups.First(g => g.Id == group.Id).Children.ShouldContain(c => c.Name == "Coffee beans");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Deleting_a_category_with_history_asks_for_a_replacement()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, shell, vm, _) = await OpenAsync();
        var manage = vm.ManageCategoriesAsync();
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is ManageCategoriesDialogViewModel, "dialog");
        var dialog = (ManageCategoriesDialogViewModel)shell.Dialogs.Current!;
        var groceries = dialog.Groups.SelectMany(g => g.Categories).Single(c => c.Id == ledger.Groceries);
        await groceries.DeleteCommand.ExecuteAsync(null);
        groceries.IsConfirmingDelete.ShouldBeTrue();
        groceries.ReplacementOptions.ShouldNotContain(c => c.Id == ledger.Groceries);
        groceries.Replacement = groceries.ReplacementOptions.Single(c => c.Id == ledger.Dining);
        await groceries.ConfirmDeleteCommand.ExecuteAsync(null);
        dialog.Error.ShouldBeNull();
        dialog.ConfirmCommand.Execute(null);
        await manage;
        await vm.SettleAsync();

        vm.FindCategory(ledger.Groceries).ShouldBeNull();
        var dining = vm.Row(ledger.Dining);
        (dining.Assigned, dining.Activity, dining.Available).ShouldBe((400_00, -450_00, -50_00));
        vm.ReadyToAssign.ShouldBe(1_100_00);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Quick_assign_from_the_context_menu_and_the_keyboard_palette()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, shell, vm, view) = await OpenAsync(new DateOnly(2026, 9, 1));
        var container = view.Named<ItemsControl>("BudgetRows").ContainerFromItem(vm.Row(ledger.Rent))!;
        var row = container.GetVisualDescendants().OfType<Border>().First(b => b.Classes.Contains("budgetRow"));
        row.ContextMenu!.Open(row);
        Dispatcher.UIThread.RunJobs();
        var item = row.ContextMenu.Items.OfType<MenuItem>().First();
        item.Command!.Execute(item.CommandParameter);                              // Assigned last month
        row.ContextMenu.Close();
        await vm.SettleAsync();
        vm.Row(ledger.Rent).Assigned.ShouldBe(1_500_00);

        vm.Select(vm.Row(ledger.Groceries), BudgetColumn.Assigned);
        view.Grid.Focus(NavigationMethod.Tab);
        window.Press(PhysicalKey.Q);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is QuickAssignPaletteViewModel, "palette");
        var palette = (QuickAssignPaletteViewModel)shell.Dialogs.Current!;
        palette.Options.Select(o => o.Value).ShouldBe([400_00, 450_00, 13_333, 15_000, 0]);
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is ListBoxItem or ListBox, "list focused");
        window.Press(PhysicalKey.ArrowDown);                                        // Spent last month
        window.Press(PhysicalKey.Enter);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is null, "palette closed");
        await vm.SettleAsync();
        vm.Row(ledger.Groceries).Assigned.ShouldBe(450_00);
        vm.ReadyToAssign.ShouldBe(1_100_00 - 1_500_00 - 450_00);
        window.Close();
    }

    [AvaloniaFact]
    public async Task A_load_failure_shows_the_error_state_with_retry()
    {
        await BudgetTestLedger.CreateAsync(_host);
        var factory = _host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<Keel.Infrastructure.Persistence.KeelDbContext>>();
        await Task.Run(async () =>
        {
            await using var db = factory.CreateDbContext();
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, "ALTER TABLE \"Targets\" RENAME TO \"TargetsAside\"");
        });
        var (window, _, vm, view) = await OpenAsync();
        vm.HasError.ShouldBeTrue();
        vm.ShowGrid.ShouldBeFalse();
        view.Named<StackPanel>("ErrorState").IsEffectivelyVisible.ShouldBeTrue();

        await Task.Run(async () =>
        {
            await using var db = factory.CreateDbContext();
            await Microsoft.EntityFrameworkCore.RelationalDatabaseFacadeExtensions.ExecuteSqlRawAsync(db.Database, "ALTER TABLE \"TargetsAside\" RENAME TO \"Targets\"");
        });
        vm.RetryCommand.Execute(null);
        await vm.SettleAsync();
        vm.HasError.ShouldBeFalse();
        vm.ShowGrid.ShouldBeTrue();
        vm.ReadyToAssign.ShouldBe(1_100_00);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Inspector_explains_available_and_saves_category_and_month_notes()
    {
        var ledger = await BudgetTestLedger.CreateAsync(_host);
        var (window, _, vm, view) = await OpenAsync();
        vm.Select(vm.Row(ledger.Groceries), BudgetColumn.Available);
        await vm.SettleAsync();
        var inspector = vm.Inspector;
        inspector.Title.ShouldBe("Groceries");
        inspector.TotalText.ShouldBe(new Money(-50_00, "USD").Format());
        inspector.Lines.Where(l => l.IsTerm).Select(l => l.AmountText).ShouldBe(
            [new Money(0, "USD").Format(), new Money(400_00, "USD").Format(), new Money(-450_00, "USD").Format()]);
        inspector.Lines.ShouldContain(l => l.Label.Contains("Visa", StringComparison.Ordinal) && !l.IsTerm);   // covered per card
        inspector.History.Last().Value.ShouldBe(-50_00);

        inspector.NoteText = "Costco run on the 1st";
        await inspector.SaveNoteCommand.ExecuteAsync(null);
        await vm.SettleAsync();
        (await _host.Get<Keel.Application.Categories.ICategoryService>().GetNoteAsync(ledger.Groceries, CancellationToken.None)).ShouldBe("Costco run on the 1st");

        vm.ExplainReadyToAssign();
        await vm.SettleAsync();
        inspector.IsReadyToAssign.ShouldBeTrue();
        inspector.TotalText.ShouldBe(new Money(1_100_00, "USD").Format());
        view.GetVisualDescendants().OfType<TextBox>().Single(b => b.Name == "MonthNoteBox").IsEffectivelyVisible.ShouldBeTrue();
        inspector.MonthNoteText = "Paycheck came early";
        await inspector.SaveMonthNoteCommand.ExecuteAsync(null);
        await vm.SettleAsync();
        (await _host.Get<IBudgetService>().GetMonthNoteAsync(August, CancellationToken.None)).ShouldBe("Paycheck came early");

        window.Press(PhysicalKey.I);                                              // I toggles the inspector
        vm.IsInspectorOpen.ShouldBeFalse();
        window.Close();
    }

    [AvaloniaFact]
    public void Budget_shortcuts_are_platform_aware_and_listed_in_settings()
    {
        var mac = new PlatformShortcuts(KeyModifiers.Meta);
        mac.Format(mac.FundTargets).ShouldBe("⇧⌘F");
        mac.Format(mac.PreviousMonth).ShouldBe("⌥←");
        var pc = new PlatformShortcuts(KeyModifiers.Control);
        pc.Format(pc.FundTargets).ShouldBe("Ctrl+Shift+F");
        pc.Format(pc.NextMonth).ShouldBe("Alt+→");

        var shortcuts = _host.Get<PlatformShortcuts>();
        var listed = _host.Get<SettingsViewModel>().Shortcuts;
        listed.ShouldContain(new ShortcutViewModel(Resources.Strings.Shortcut_BudgetFundTargets, shortcuts.Format(shortcuts.FundTargets)));
        listed.ShouldContain(new ShortcutViewModel(Resources.Strings.Shortcut_BudgetMoveMoney, "M"));
        listed.ShouldContain(new ShortcutViewModel(Resources.Strings.Shortcut_BudgetSetTarget, "T"));
        listed.ShouldContain(new ShortcutViewModel(Resources.Strings.Shortcut_BudgetInspector, "I"));
        listed.ShouldContain(new ShortcutViewModel(Resources.Strings.Shortcut_BudgetPreviousMonth, shortcuts.Format(shortcuts.PreviousMonth)));
    }
}
