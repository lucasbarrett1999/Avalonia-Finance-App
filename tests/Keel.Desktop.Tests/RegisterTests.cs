using System.Diagnostics;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Accounts;
using Keel.Application.Categories;
using Keel.Application.Ledger;
using Keel.Desktop.Controls;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;
using Keel.Desktop.Views;
using Keel.Domain;
using Keel.Infrastructure.Fixtures;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

public sealed class RegisterTests : IDisposable
{
    private static readonly DateOnly Opening = new(2026, 8, 1);
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    private async Task<AccountDto> CheckingAsync(long opening = 100_000) =>
        await Task.Run(() => _host.Get<IAccountService>().CreateAccountAsync(
            new CreateAccountRequest("Checking", AccountType.Checking, "USD", Opening, opening), Ct));

    private async Task<(ShellWindow Window, ShellViewModel Shell, AccountsViewModel Vm, AccountsView View)> OpenAsync(Guid? accountId)
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        if (accountId is { } id)
        {
            shell.OpenAccount(id);
        }
        else
        {
            shell.AccountItems[0].NavigateCommand.Execute(null);
        }

        var vm = _host.Get<AccountsViewModel>();
        await vm.SettleAsync();
        return (window, shell, vm, window.Register());
    }

    private static async Task<RegisterRowViewModel> SelectAsync(AccountsView view, AccountsViewModel vm, Func<RegisterRowViewModel, bool> predicate)
    {
        await UiTestHelpers.WaitUntilAsync(() => vm.Rows.LoadedRows.Any(predicate), "row loaded");
        var row = vm.Rows.LoadedRows.First(predicate);
        view.Grid.SelectedItem = row;
        view.Grid.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();
        vm.Selection.ShouldBe([row]);
        return row;
    }

    private async Task<TransactionDto> AddAsync(Guid account, long amount, string payee, Guid? category = null) =>
        await Task.Run(() => _host.Get<ITransactionService>().SaveAsync(new SaveTransactionRequest(null, account, new DateOnly(2026, 8, 10), amount, payee, category, null), Ct));

    [AvaloniaFact]
    public async Task Adds_a_transaction_with_the_keyboard()
    {
        var checking = await CheckingAsync();
        var groceries = await Task.Run(() => _host.Get<ICategoryService>().CreateCategoryAsync("Everyday", "Groceries", Ct));
        var (window, _, vm, view) = await OpenAsync(checking.Id);
        vm.RowCount.ShouldBe(1);

        view.Grid.Focus(NavigationMethod.Tab);
        window.Press(PhysicalKey.N);
        await UiTestHelpers.WaitUntilAsync(() => vm.IsEditing && window.Focused() is TextBox { Parent: not null } box && box.FindAncestorOfType<AutoCompleteBox>()?.Name == "EditorPayee", "payee focused");

        window.Type("Corner Store");
        window.Press(PhysicalKey.Tab);
        await UiTestHelpers.WaitUntilAsync(() => (window.Focused() as Control)?.FindAncestorOfType<AutoCompleteBox>()?.Name == "EditorCategory", "category focused");
        window.Type("Groceries");
        window.Press(PhysicalKey.Tab);
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is TextBox { Name: "EditorMemo" }, "memo focused");
        window.Type("snacks");
        window.Press(PhysicalKey.Tab);
        window.Focused().ShouldBeOfType<MoneyTextBox>().Name.ShouldBe("EditorOutflow");
        window.Type("12.50+3");
        window.Press(PhysicalKey.Enter);

        await UiTestHelpers.WaitUntilAsync(() => vm.RowCount == 2 && !vm.IsEditing, "saved");
        await vm.SettleAsync();
        await using var db = _host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<KeelDbContext>>().CreateDbContext();
        var saved = await db.Transactions.SingleAsync(t => t.PayeeRaw == "Corner Store");
        saved.Amount.ShouldBe(-1_550);
        saved.CategoryId.ShouldBe(groceries.Id);
        saved.Memo.ShouldBe("snacks");
        saved.Date.ShouldBe(DateOnly.FromDateTime(DateTime.Today));
        vm.Selection.ShouldHaveSingleItem().Id.ShouldBe(saved.Id);
        vm.Selection[0].Outflow.ShouldBe(new Money(1_550, "USD").Format());
        vm.Summary!.Ledger.ShouldBe(100_000 - 1_550);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Save_and_new_keeps_the_editor_open_and_prefills_from_a_known_payee()
    {
        var checking = await CheckingAsync();
        var dining = await Task.Run(() => _host.Get<ICategoryService>().CreateCategoryAsync("Everyday", "Dining", Ct));
        await Task.Run(() => _host.Get<ITransactionService>().SaveAsync(new SaveTransactionRequest(null, checking.Id, Opening, -900, "Taco Truck", dining.Id, "lunch"), Ct));
        var (window, shell, vm, _) = await OpenAsync(checking.Id);

        await vm.NewTransactionAsync();
        await UiTestHelpers.WaitUntilAsync(() => window.Focused() is TextBox, "editor focused");
        window.Type("Taco Truck");
        window.Press(PhysicalKey.Tab);
        await UiTestHelpers.WaitUntilAsync(() => vm.Editor?.CategoryText == dining.FullName, "category pre-filled from the payee");
        vm.Editor!.Memo.ShouldBe("lunch");
        vm.Editor.Outflow = 1_000;

        window.Press(PhysicalKey.Enter, _host.Get<PlatformShortcuts>().Command());
        await UiTestHelpers.WaitUntilAsync(() => vm.RowCount == 3, "saved");
        vm.IsEditing.ShouldBeTrue("Ctrl/Cmd+Enter saves and starts another");
        vm.Editor!.IsNew.ShouldBeTrue();
        shell.UndoCommand.CanExecute(null).ShouldBeTrue();

        window.Press(PhysicalKey.Escape);
        await UiTestHelpers.WaitUntilAsync(() => !vm.IsEditing, "cancelled");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Edits_a_row_inline()
    {
        var checking = await CheckingAsync();
        var txn = await AddAsync(checking.Id, -2_000, "Hardware");
        var (window, _, vm, view) = await OpenAsync(checking.Id);

        await SelectAsync(view, vm, r => r.Id == txn.Id);
        window.Press(PhysicalKey.Enter);
        await UiTestHelpers.WaitUntilAsync(() => vm.Editor is { IsNew: false } && window.Focused() is TextBox, "editor open");
        vm.Editor!.Payee.ShouldBe("Hardware");
        vm.Editor.Outflow.ShouldBe(2_000);

        var memo = window.Named<TextBox>("EditorMemo");
        memo.Focus(NavigationMethod.Tab);
        window.Type("nails and glue");
        var outflow = window.Named<MoneyTextBox>("EditorOutflow");
        outflow.Focus(NavigationMethod.Tab);
        outflow.SelectAll();
        window.Type("25*2");
        window.Press(PhysicalKey.Enter);

        await UiTestHelpers.WaitUntilAsync(() => !vm.IsEditing, "saved");
        await vm.SettleAsync();
        var row = vm.Rows.LoadedRows.Single(r => r.Id == txn.Id);
        row.Memo.ShouldBe("nails and glue");
        row.Amount.ShouldBe(-5_000);
        row.RunningBalance.ShouldBe(new Money(95_000, "USD").Format());
        vm.RowCount.ShouldBe(2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task C_toggles_cleared_on_the_selected_row()
    {
        var checking = await CheckingAsync();
        var txn = await AddAsync(checking.Id, -700, "Bakery");
        var (window, _, vm, view) = await OpenAsync(checking.Id);
        vm.Summary!.Cleared.ShouldBe(100_000);

        var row = await SelectAsync(view, vm, r => r.Id == txn.Id);
        row.IsUncleared.ShouldBeTrue();
        window.Press(PhysicalKey.C);
        await UiTestHelpers.WaitUntilAsync(() => row.IsCleared, "cleared");
        await vm.SettleAsync();
        vm.Summary!.Cleared.ShouldBe(99_300);
        vm.Selection.ShouldBe([row], "an in-place refresh keeps the selection");

        window.Press(PhysicalKey.C);
        await UiTestHelpers.WaitUntilAsync(() => row.IsUncleared, "uncleared again");
        (await Task.Run(() => _host.Get<ITransactionService>().GetAsync(txn.Id, Ct)))!.Status.ShouldBe(TransactionStatus.Uncleared);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Delete_shows_an_undo_toast_and_ctrl_z_restores_the_row()
    {
        var checking = await CheckingAsync();
        var txn = await AddAsync(checking.Id, -4_200, "Florist");
        var (window, shell, vm, view) = await OpenAsync(checking.Id);

        await SelectAsync(view, vm, r => r.Id == txn.Id);
        window.Press(PhysicalKey.Delete);
        await UiTestHelpers.WaitUntilAsync(() => vm.RowCount == 1, "deleted");
        shell.Status.CanUndo.ShouldBeTrue();
        shell.StatusMessage.ShouldContain("Deleted 1");
        window.Named<Button>("StatusUndoButton").IsVisible.ShouldBeTrue();
        shell.UndoToolTip.ShouldContain("delete transactions");

        view.Grid.Focus(NavigationMethod.Tab);
        window.Press(PhysicalKey.Z, _host.Get<PlatformShortcuts>().Command());
        await UiTestHelpers.WaitUntilAsync(() => vm.RowCount == 2, "restored by undo");
        await vm.SettleAsync();
        vm.Rows.LoadedRows.ShouldContain(r => r.Id == txn.Id);
        shell.StatusMessage.ShouldContain("Undid delete transactions");
        shell.RedoCommand.CanExecute(null).ShouldBeTrue();

        window.Press(PhysicalKey.Z, _host.Get<PlatformShortcuts>().Command() | RawInputModifiers.Shift);
        await UiTestHelpers.WaitUntilAsync(() => vm.RowCount == 1, "deleted again by redo");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reconcile_flow_clears_to_zero_and_locks_the_cleared_rows()
    {
        var checking = await CheckingAsync(opening: 100_000);
        var rent = await AddAsync(checking.Id, -50_000, "Landlord");
        var coffee = await AddAsync(checking.Id, -450, "Cafe");
        var (window, _, vm, view) = await OpenAsync(checking.Id);

        window.Named<Button>("ReconcileButton").Command!.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => vm.IsReconciling, "reconcile bar open");
        var bar = vm.Reconcile!;
        var statement = window.Named<MoneyTextBox>("StatementBalanceBox");
        statement.Focus(NavigationMethod.Tab);
        statement.SelectAll();
        window.Type("500");
        window.Press(PhysicalKey.Tab);
        await UiTestHelpers.WaitUntilAsync(() => bar.StatementBalance == 50_000 && bar.Difference == -50_000, "difference shown");
        bar.FinishCommand.CanExecute(null).ShouldBeFalse();

        await SelectAsync(view, vm, r => r.Id == rent.Id);
        window.Press(PhysicalKey.C);
        await UiTestHelpers.WaitUntilAsync(() => bar.Difference == 0, "difference is zero after clearing the rent");
        bar.FinishCommand.CanExecute(null).ShouldBeTrue();

        window.Named<Button>("FinishReconcileButton").Command!.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => !vm.IsReconciling, "finished");
        await vm.SettleAsync();

        var txns = _host.Get<ITransactionService>();
        (await Task.Run(() => txns.GetAsync(rent.Id, Ct)))!.Status.ShouldBe(TransactionStatus.Reconciled);
        (await Task.Run(() => txns.GetAsync(coffee.Id, Ct)))!.Status.ShouldBe(TransactionStatus.Uncleared);
        vm.Rows.LoadedRows.Single(r => r.Id == rent.Id).IsReconciled.ShouldBeTrue();

        // Reconciled rows are locked: C reports it instead of changing the status.
        await SelectAsync(view, vm, r => r.Id == rent.Id);
        window.Press(PhysicalKey.C);
        await UiTestHelpers.WaitUntilAsync(() => _host.Get<StatusService>().IsError, "locked message");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Reconcile_can_record_a_balance_adjustment()
    {
        var checking = await CheckingAsync(opening: 100_000);
        var (window, _, vm, _) = await OpenAsync(checking.Id);

        vm.StartReconcile();
        var bar = vm.Reconcile!;
        bar.StatementBalance = 99_000;
        await UiTestHelpers.WaitUntilAsync(() => bar.Difference == -1_000, "difference");
        window.Named<Button>("AdjustAndFinishButton").Command!.Execute(null);
        await UiTestHelpers.WaitUntilAsync(() => !vm.IsReconciling && vm.RowCount == 2, "adjusted");
        await vm.SettleAsync();
        vm.Summary!.Ledger.ShouldBe(99_000);
        vm.Rows.LoadedRows.ShouldContain(r => r.Payee == "Reconciliation Balance Adjustment" && r.IsReconciled);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Splits_expand_inline_and_transfers_show_the_other_account()
    {
        await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(3_000, Seed: 3), Ct));
        var visa = (await Task.Run(() => _host.Get<IAccountService>().GetAccountsAsync(false, Ct))).Single(a => a.Name == "Visa Rewards");
        var (window, _, vm, view) = await OpenAsync(null);
        vm.SearchText = "category:household";
        await vm.SettleAsync();

        var split = await SelectAsync(view, vm, r => r.IsSplit);
        split.Category.ShouldStartWith("Split (");
        view.Grid.ScrollIntoView(split, null);
        Dispatcher.UIThread.RunJobs();
        var toggle = view.Grid.GetVisualDescendants().OfType<Avalonia.Controls.Primitives.ToggleButton>()
            .First(t => t.Classes.Contains("expander") && ReferenceEquals(t.DataContext, split));
        toggle.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
        Dispatcher.UIThread.RunJobs();
        split.IsExpanded.ShouldBeTrue();
        toggle.FindAncestorOfType<DataGridRow>()!.AreDetailsVisible.ShouldBeTrue();

        window.Close();
        var window2 = _host.Get<ShellWindow>();
        window2.Show();
        ((ShellViewModel)window2.DataContext!).OpenAccount(visa.Id);
        await vm.SettleAsync();
        vm.SearchText = "payee:everyday";
        await vm.SettleAsync();
        await UiTestHelpers.WaitUntilAsync(() => vm.Rows.LoadedRows.Any(), "rows");
        vm.Rows.LoadedRows.ShouldAllBe(r => r.IsTransfer && r.Payee == "Transfer: Everyday Checking");
        window2.Close();
    }

    [AvaloniaFact]
    public async Task All_accounts_register_over_100k_rows_is_virtualized()
    {
        await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(), Ct));
        var watch = Stopwatch.StartNew();
        var (window, _, vm, view) = await OpenAsync(null);
        watch.Stop();

        vm.RowCount.ShouldBe(100_000);
        vm.IsAllAccounts.ShouldBeTrue();
        view.Grid.Columns[1].IsVisible.ShouldBeTrue("the All Accounts register has an Account column");
        vm.Rows.CachedPageCount.ShouldBeLessThanOrEqualTo(2, "only the visible page is read");
        var realized = view.Grid.GetVisualDescendants().OfType<DataGridRow>().Count();
        realized.ShouldBeLessThan(80);
        vm.Rows.LoadedRows.First().RunningBalance.ShouldNotBeNullOrEmpty();

        var last = await vm.Rows.GetLoadedAsync(99_999);
        last.IsLoaded.ShouldBeTrue();
        last.Payee.ShouldBe("Starting Balance");
        view.Grid.ScrollIntoView(last, null);
        Dispatcher.UIThread.RunJobs();
        vm.Rows.CachedPageCount.ShouldBeLessThanOrEqualTo(RegisterSource.MaxCachedPages);
        watch.ElapsedMilliseconds.ShouldBeLessThan(5_000, "headless open including the first page");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Filters_and_sorting_run_in_the_database()
    {
        await Task.Run(() => LedgerFixtureGenerator.GenerateAsync(_host.Get<Microsoft.EntityFrameworkCore.IDbContextFactory<KeelDbContext>>(), new LedgerFixtureOptions(2_000, Seed: 5), Ct));
        var (window, _, vm, view) = await OpenAsync(null);
        var total = vm.RowCount;

        vm.SelectedStatusFilter = vm.StatusFilters.Single(s => s.Value == StatusFilter.Uncleared);
        await vm.SettleAsync();
        vm.RowCount.ShouldBeLessThan(total);
        vm.Rows.LoadedRows.ShouldAllBe(r => r.IsUncleared);
        vm.HasActiveFilters.ShouldBeTrue();

        vm.ClearFilters();
        await vm.SettleAsync();
        vm.RowCount.ShouldBe(total);

        // Header click sorts through the source's sort descriptions.
        view.Grid.Columns[6].SortMemberPath.ShouldBe("Amount");
        vm.Rows.SortDescriptions.Add(Avalonia.Collections.DataGridSortDescription.FromPath("Amount", System.ComponentModel.ListSortDirection.Ascending));
        await vm.SettleAsync();
        await UiTestHelpers.WaitUntilAsync(() => vm.Rows.LoadedRows.Count() >= 20, "rows");
        var amounts = vm.Rows.LoadedRows.OrderBy(r => r.Index).Take(20).Select(r => r.Amount).ToList();
        amounts.ShouldBe(amounts.OrderBy(a => a).ToList());
        window.Close();
    }

    [AvaloniaFact]
    public async Task Sidebar_lists_accounts_by_group_and_add_account_opens_its_register()
    {
        var accounts = _host.Get<IAccountService>();
        await Task.Run(async () =>
        {
            await accounts.CreateAccountAsync(new CreateAccountRequest("Checking", AccountType.Checking, "USD", Opening, 150_000), Ct);
            await accounts.CreateAccountAsync(new CreateAccountRequest("Visa", AccountType.CreditCard, "USD", Opening, -20_000), Ct);
            var old = await accounts.CreateAccountAsync(new CreateAccountRequest("Old Savings", AccountType.Savings, "USD", Opening, 0), Ct);
            await accounts.CloseAccountAsync(old.Id, false, Ct);
        });
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        Dispatcher.UIThread.RunJobs();

        shell.AccountGroups.Select(g => g.Title).ShouldBe(["Cash", "Credit"]);
        shell.AccountGroups[0].Accounts.ShouldHaveSingleItem().BalanceText.ShouldBe(new Money(150_000, "USD").Format());
        shell.AccountGroups[1].Accounts[0].IsNegative.ShouldBeTrue();
        shell.HasClosedAccounts.ShouldBeTrue();
        window.GetVisualDescendants().OfType<TextBlock>().ShouldContain(t => t.Text == "Visa");

        shell.AddAccountCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var dialog = shell.Dialogs.Current.ShouldBeOfType<AccountEditorViewModel>();
        window.Named<Border>("DialogLayer").IsVisible.ShouldBeTrue();
        dialog.Name = "Brokerage";
        dialog.SelectedType = dialog.Types.Single(t => t.Value == AccountType.Investment);
        dialog.IsOnBudget.ShouldBeFalse();
        dialog.OpeningBalance = 1_000_000;
        await dialog.ConfirmCommand.ExecuteAsync(null);
        await UiTestHelpers.WaitUntilAsync(() => shell.AccountGroups.Any(g => g.Title == "Tracking"), "tracking group shown");

        var vm = _host.Get<AccountsViewModel>();
        await vm.SettleAsync();
        vm.Account!.Name.ShouldBe("Brokerage");
        vm.IsTracking.ShouldBeTrue();
        shell.AccountGroups.Single(g => g.Title == "Tracking").Accounts.Single().IsSelected.ShouldBeTrue();
        shell.AccountItems[0].IsSelected.ShouldBeFalse();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Global_search_opens_all_accounts_filtered()
    {
        var checking = await CheckingAsync();
        await AddAsync(checking.Id, -100, "Zebra Cafe");
        await AddAsync(checking.Id, -200, "Other");
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        shell.SearchText = "zebra";
        shell.SearchCommand.Execute(null);
        var vm = _host.Get<AccountsViewModel>();
        await vm.SettleAsync();
        vm.IsAllAccounts.ShouldBeTrue();
        vm.SearchText.ShouldBe("zebra");
        vm.RowCount.ShouldBe(1);
        window.Close();
    }
}
