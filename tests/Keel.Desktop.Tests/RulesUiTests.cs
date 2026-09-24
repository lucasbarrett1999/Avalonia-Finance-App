using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Ledger;
using Keel.Application.Payees;
using Keel.Application.Rules;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Register;
using Keel.Desktop.ViewModels.Rules;
using Keel.Desktop.Views;
using Keel.Domain.Rules;

namespace Keel.Desktop.Tests;

public sealed class RulesUiTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    private async Task<(ShellWindow Window, ShellViewModel Shell, RulesViewModel Rules)> OpenSettingsAsync()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        shell.SettingsItem.NavigateCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var rules = _host.Get<RulesViewModel>();
        await rules.EnsureLoadedAsync();
        await SettleAsync(rules);
        return (window, shell, rules);
    }

    private static async Task SettleAsync(RulesViewModel rules)
    {
        for (var i = 0; i < 4; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await rules.Loading;
            await Task.Delay(15);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private static async Task<T> DialogAsync<T>(ShellViewModel shell)
        where T : class
    {
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is T, typeof(T).Name + " open");
        return (T)(object)shell.Dialogs.Current!;
    }

    private Task<RuleDto> SaveRuleAsync(string name, string payeeContains, Guid category) =>
        Task.Run(() => _host.Get<IRuleService>().SaveAsync(new RuleDefinition
        {
            Name = name,
            Conditions = new RuleConditionSet { Conditions = [new PayeeCondition(TextOperator.Contains, payeeContains)] },
            Actions = new RuleActionSet { Actions = [new SetCategoryAction(category)] },
        }, Ct));

    [AvaloniaFact]
    public async Task The_editor_validates_live_and_saves_only_valid_rules()
    {
        await ReviewTestLedger.CreateAsync(_host, withPending: false);
        var (window, shell, rules) = await OpenSettingsAsync();
        rules.IsEmpty.ShouldBeTrue();
        window.GetVisualDescendants().OfType<Keel.Desktop.Views.Rules.RulesPanel>().ShouldHaveSingleItem();

        var creating = rules.NewRuleCommand.ExecuteAsync(null);
        var editor = await DialogAsync<RuleEditorViewModel>(shell);
        editor.HasErrors.ShouldBeTrue();
        editor.SaveCommand.CanExecute(null).ShouldBeFalse();
        editor.GeneralProblems.ShouldContain(p => p.IsError && p.Message.Contains("name", StringComparison.OrdinalIgnoreCase));
        editor.Conditions[0].HasProblem.ShouldBeTrue("the payee text is empty");
        editor.Actions[0].HasProblem.ShouldBeTrue("no category chosen");

        editor.Name = "Coffee";
        editor.Conditions[0].Text = "(coffee";
        editor.Conditions[0].TextOperator = editor.Conditions[0].TextOperators.Single(o => o.Value == TextOperator.Regex);
        editor.Conditions[0].Problem.ShouldNotBeNull().ShouldContain("regular expression", Case.Insensitive);
        editor.Conditions[0].Text = "coffee|espresso";
        editor.Conditions[0].HasProblem.ShouldBeFalse();

        editor.AddConditionCommand.Execute(null);
        var amount = editor.Conditions[1];
        amount.Kind = amount.Kinds.Single(k => k.Value == ConditionKind.Amount);
        amount.AmountOperator = amount.AmountOperators.Single(o => o.Value == AmountOperator.Between);
        amount.Amount = 2_000;
        amount.AmountMax = 500;
        amount.HasProblem.ShouldBeTrue("reversed bounds");
        amount.AmountMax = 5_000;
        amount.HasProblem.ShouldBeFalse();

        editor.Actions[0].Category = editor.Actions[0].Categories.Single(c => c.Name == "Dining");
        editor.AddActionCommand.Execute(null);
        var split = editor.Actions[1];
        split.Kind = split.Kinds.Single(k => k.Value == ActionKind.SplitByPercentages);
        split.Lines.Count.ShouldBe(2);
        split.Lines[0].Percent = 70;
        split.Lines[1].Percent = 20;
        split.HasProblem.ShouldBeTrue("percentages must sum to 100");
        split.ProblemIsError.ShouldBeTrue();
        split.Lines[1].Percent = 30;
        split.ProblemIsError.ShouldBeFalse();
        split.Problem.ShouldNotBeNull().ShouldStartWith("Warning:", customMessage: "setting a category and splitting is a warning, not an error");
        editor.HasErrors.ShouldBeFalse();

        await editor.SaveCommand.ExecuteAsync(null);
        await creating;
        await SettleAsync(rules);
        var row = rules.Rules.ShouldHaveSingleItem();
        row.Name.ShouldBe("Coffee");
        row.Summary.ShouldContain("matches /coffee|espresso/");
        row.Summary.ShouldContain("split into 70% to no category, 30% to no category");
        shell.Status.CanUndo.ShouldBeTrue();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Test_rule_and_apply_to_existing_use_the_preview()
    {
        var ledger = await ReviewTestLedger.CreateAsync(_host);
        await SaveRuleAsync("Costco", "costco", ledger.Household);
        var (window, shell, rules) = await OpenSettingsAsync();
        var row = rules.Rules.ShouldHaveSingleItem();

        await rules.CountMatchesCommand.ExecuteAsync(row);
        row.MatchCountText.ShouldBe("Matches: 1");

        var editing = rules.EditCommand.ExecuteAsync(row);
        var editor = await DialogAsync<RuleEditorViewModel>(shell);
        editor.IsNew.ShouldBeFalse();
        await editor.TestCommand.ExecuteAsync(null);
        editor.TestResult!.Summary.ShouldStartWith("Matches 1 of ");
        editor.TestResult.Rows.ShouldHaveSingleItem().ChangesText.ShouldBe("Category: Uncategorized → Household");
        editor.CancelCommand.Execute(null);
        await editing;

        var applying = rules.ApplyToExistingCommand.ExecuteAsync(row);
        var preview = await DialogAsync<RetroactiveApplyViewModel>(shell);
        await preview.Loading;
        preview.Changes.ShouldHaveSingleItem().Payee.ShouldBe("Costco");
        preview.Summary.ShouldStartWith("1 of ");
        preview.UnapprovedOnly = true;
        await preview.Loading;
        preview.Changes.Count.ShouldBe(1);
        (await Task.Run(() => _host.Get<ITransactionService>().GetAsync(ledger.Pending[3], Ct)))!.CategoryId.ShouldBeNull("the preview writes nothing");

        preview.ConfirmCommand.Execute(null);
        await applying;
        (await Task.Run(() => _host.Get<ITransactionService>().GetAsync(ledger.Pending[3], Ct)))!.CategoryId.ShouldBe(ledger.Household);
        shell.Status.CanUndo.ShouldBeTrue();
        await shell.UndoCommand.ExecuteAsync(null);
        (await Task.Run(() => _host.Get<ITransactionService>().GetAsync(ledger.Pending[3], Ct)))!.CategoryId.ShouldBeNull();
        window.Close();
    }

    [AvaloniaFact]
    public async Task Rules_reorder_toggle_and_delete_with_confirmation()
    {
        var ledger = await ReviewTestLedger.CreateAsync(_host, withPending: false);
        await SaveRuleAsync("First", "a", ledger.Dining);
        await SaveRuleAsync("Second", "b", ledger.Dining);
        await SaveRuleAsync("Third", "c", ledger.Dining);
        var (window, shell, rules) = await OpenSettingsAsync();
        rules.Rules.Select(r => r.Name).ShouldBe(["First", "Second", "Third"]);
        rules.Rules[0].IsFirst.ShouldBeTrue();

        await rules.MoveDownCommand.ExecuteAsync(rules.Rules[0]);
        await SettleAsync(rules);
        rules.Rules.Select(r => r.Name).ShouldBe(["Second", "First", "Third"]);

        await rules.MoveToAsync(rules.Rules[2], 0);
        await SettleAsync(rules);
        rules.Rules.Select(r => r.Name).ShouldBe(["Third", "Second", "First"]);
        (await Task.Run(() => _host.Get<IRuleService>().GetRulesAsync(Ct))).Select(r => r.Name).ShouldBe(["Third", "Second", "First"]);

        rules.Rules[1].IsEnabled = false;
        await UiTestHelpers.WaitUntilAsync(() => rules.CountText == "3 rules, 2 enabled", "disabled rule saved");

        var deleting = rules.DeleteCommand.ExecuteAsync(rules.Rules[0]);
        var confirm = await DialogAsync<ConfirmDialogViewModel>(shell);
        confirm.Message.ShouldContain("Third");
        confirm.ConfirmCommand.Execute(null);
        await deleting;
        await SettleAsync(rules);
        rules.Rules.Select(r => r.Name).ShouldBe(["Second", "First"]);
        window.Close();
    }

    [AvaloniaFact]
    public async Task The_register_context_menu_creates_a_rule_from_the_transaction()
    {
        var ledger = await ReviewTestLedger.CreateAsync(_host);
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        shell.OpenAccount(ledger.Checking);
        var register = _host.Get<AccountsViewModel>();
        await register.SettleAsync();
        var view = window.Register();
        view.Grid.ContextMenu.ShouldNotBeNull().Items.OfType<MenuItem>().ShouldContain(m => m.Name == "CreateRuleMenuItem");

        await UiTestHelpers.WaitUntilAsync(() => register.Rows.LoadedRows.Any(r => r.Payee == "Taco Truck"), "rows loaded");
        view.Grid.SelectedItem = register.Rows.LoadedRows.First(r => r.Payee == "Taco Truck" && r.Category == "Dining");
        view.Grid.Focus(NavigationMethod.Tab);
        Dispatcher.UIThread.RunJobs();

        var creating = register.CreateRuleFromTransactionCommand.ExecuteAsync(null);
        var editor = await DialogAsync<RuleEditorViewModel>(shell);
        editor.Name.ShouldBe("Taco Truck → Dining");
        editor.Actions.ShouldHaveSingleItem().Category!.Name.ShouldBe("Dining");
        editor.HasErrors.ShouldBeFalse();
        await editor.SaveCommand.ExecuteAsync(null);
        await creating;
        (await Task.Run(() => _host.Get<IRuleService>().GetRulesAsync(Ct))).ShouldHaveSingleItem().Name.ShouldBe("Taco Truck → Dining");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Settings_payees_set_a_default_category_and_rename()
    {
        var ledger = await ReviewTestLedger.CreateAsync(_host);
        var (window, shell, _) = await OpenSettingsAsync();
        var payees = _host.Get<PayeesViewModel>();
        await payees.EnsureLoadedAsync();
        payees.SearchText = "cost";
        await payees.Loading;
        var costco = payees.Payees.ShouldHaveSingleItem();
        costco.CountText.ShouldBe("Transactions: 1");

        costco.DefaultCategory = costco.Choices.Single(c => c.Name == "Household");
        await UiTestHelpers.WaitUntilAsync(() => shell.Status.Message.Contains("Household", StringComparison.Ordinal), "default saved");
        (await Task.Run(() => _host.Get<IPayeeService>().SearchAsync("costco", 1, Ct))).Single().DefaultCategoryId.ShouldBe(ledger.Household);

        var renaming = payees.RenameCommand.ExecuteAsync(costco);
        var dialog = await DialogAsync<RenamePayeeDialogViewModel>(shell);
        dialog.Name = "Costco Wholesale";
        dialog.ConfirmCommand.Execute(null);
        await renaming;
        (await Task.Run(() => _host.Get<ITransactionService>().GetAsync(ledger.Pending[3], Ct)))!.Payee.ShouldBe("Costco Wholesale");
        CategoryOption.All.ShouldNotBeNull();
        window.Close();
    }
}
