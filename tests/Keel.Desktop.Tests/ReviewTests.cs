using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Keel.Application.Categorization;
using Keel.Application.Ledger;
using Keel.Desktop.Controls;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Review;
using Keel.Desktop.ViewModels.Rules;
using Keel.Desktop.Views;
using Keel.Domain.Categorization;
using Keel.Infrastructure.Categorization;
using Keel.Infrastructure.Ledger;
using Keel.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Keel.Desktop.Tests;

public sealed class ReviewTests : IDisposable
{
    private readonly TestHost _host = TestHost.Create();

    public void Dispose() => _host.Dispose();

    private static CancellationToken Ct => CancellationToken.None;

    internal static async Task SettleAsync(ReviewViewModel vm)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            await vm.WhenIdleAsync();
            await Task.Delay(15);
        }

        Dispatcher.UIThread.RunJobs();
    }

    private async Task<(ShellWindow Window, ShellViewModel Shell, ReviewViewModel Vm)> OpenAsync()
    {
        var window = _host.Get<ShellWindow>();
        window.Show();
        var shell = (ShellViewModel)window.DataContext!;
        await shell.AccountsLoading;
        shell.ReviewItem.NavigateCommand.Execute(null);
        var vm = _host.Get<ReviewViewModel>();
        await SettleAsync(vm);
        await UiTestHelpers.WaitUntilAsync(() => window.GetVisualDescendants().OfType<ReviewView>().Any(), "review view shown");
        window.GetVisualDescendants().OfType<ReviewView>().Single().Focus();
        Dispatcher.UIThread.RunJobs();
        return (window, shell, vm);
    }

    private async Task<TransactionDto> GetAsync(Guid id) => (await Task.Run(() => _host.Get<ITransactionService>().GetAsync(id, Ct)))!;

    private static async Task PressAsync(ShellWindow window, ReviewViewModel vm, PhysicalKey key)
    {
        window.Press(key);
        await SettleAsync(vm);
    }

    [AvaloniaFact]
    public async Task Keyboard_triage_approves_picks_changes_category_moves_and_creates_a_rule()
    {
        var ledger = await ReviewTestLedger.CreateAsync(_host);
        var (window, shell, vm) = await OpenAsync();

        vm.ShowQueue.ShouldBeTrue();
        vm.ProgressText.ShouldBe("1 of 4");
        vm.Focused!.Id.ShouldBe(ledger.Pending[0]);
        vm.Suggestions.ShouldNotBeEmpty();
        var primary = vm.Suggestions[0];
        (primary.CategoryName, primary.Suggestion.Source, primary.IsPrimary).ShouldBe(("Groceries", CategorizationSource.Learner, true));
        primary.Explanation.ShouldBe("Suggested because 4 of 4 past 'TRADER JOES' transactions were Groceries");
        vm.TraceSteps.ShouldNotBeEmpty();
        await shell.ReviewBadgeLoading;
        shell.ReviewItem.Badge.ShouldBe(4);

        // A: approve with the primary suggestion; the next transaction takes the focus.
        await PressAsync(window, vm, PhysicalKey.A);
        var first = await GetAsync(ledger.Pending[0]);
        (first.IsApproved, first.CategoryId).ShouldBe((true, (Guid?)ledger.Groceries));
        vm.Focused!.Id.ShouldBe(ledger.Pending[1]);
        vm.ProgressText.ShouldBe("2 of 4");
        await UiTestHelpers.WaitUntilAsync(() => shell.ReviewItem.Badge == 3, "badge counts down");

        // 1: pick the first suggestion.
        vm.Suggestions[0].CategoryName.ShouldBe("Dining");
        await PressAsync(window, vm, PhysicalKey.Digit1);
        var second = await GetAsync(ledger.Pending[1]);
        (second.IsApproved, second.CategoryId).ShouldBe((true, (Guid?)ledger.Dining));

        // J / K move without approving.
        vm.Focused!.Id.ShouldBe(ledger.Pending[2]);
        vm.ShowNoSuggestions.ShouldBeTrue();
        await PressAsync(window, vm, PhysicalKey.J);
        vm.Focused!.Id.ShouldBe(ledger.Pending[3]);
        vm.ProgressText.ShouldBe("4 of 4");
        await PressAsync(window, vm, PhysicalKey.K);
        vm.Focused!.Id.ShouldBe(ledger.Pending[2]);
        (await GetAsync(ledger.Pending[2])).IsApproved.ShouldBeFalse();

        // C: search-as-you-type picker, then approve.
        window.Press(PhysicalKey.C);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is PickerDialogViewModel, "category picker open");
        var picker = (PickerDialogViewModel)shell.Dialogs.Current!;
        picker.SearchText = "house";
        picker.Filtered.ShouldHaveSingleItem();
        picker.ConfirmCommand.Execute(null);
        await SettleAsync(vm);
        var third = await GetAsync(ledger.Pending[2]);
        (third.IsApproved, third.CategoryId).ShouldBe((true, (Guid?)ledger.Household));

        // R: create a rule from the transaction (prefilled), then approve with the rule's category.
        vm.Focused!.Id.ShouldBe(ledger.Pending[3]);
        window.Press(PhysicalKey.R);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is RuleEditorViewModel, "rule editor open");
        var editor = (RuleEditorViewModel)shell.Dialogs.Current!;
        editor.IsNew.ShouldBeTrue();
        editor.Name.ShouldBe("Costco");
        editor.Conditions.Select(c => c.Kind.Value).ShouldBe([ConditionKind.Payee, ConditionKind.Direction]);
        editor.Conditions[0].Text.ShouldBe("COSTCO");
        editor.Conditions[0].Normalized.ShouldBeTrue();
        editor.HasErrors.ShouldBeTrue("the transaction has no category yet, so the rule has no action");
        editor.AddActionCommand.Execute(null);
        editor.Actions[0].Category = editor.Actions[0].Categories.Single(c => c.Name == "Household");
        editor.HasErrors.ShouldBeFalse();
        await editor.SaveCommand.ExecuteAsync(null);
        await SettleAsync(vm);
        vm.Suggestions[0].Suggestion.Source.ShouldBe(CategorizationSource.Rule);
        vm.Suggestions[0].CategoryName.ShouldBe("Household");

        await PressAsync(window, vm, PhysicalKey.A);
        var fourth = await GetAsync(ledger.Pending[3]);
        (fourth.IsApproved, fourth.CategoryId).ShouldBe((true, (Guid?)ledger.Household));

        // Designed empty state.
        vm.ShowEmptyState.ShouldBeTrue();
        var empty = window.GetVisualDescendants().OfType<ReviewView>().Single().GetVisualDescendants().OfType<EmptyState>().Single();
        empty.Heading.ShouldBe("Nothing to review");
        empty.IsEffectivelyVisible.ShouldBeTrue();
        await UiTestHelpers.WaitUntilAsync(() => shell.ReviewItem.Badge == 0, "badge cleared");

        // The learner learned from the approvals.
        var model = await Task.Run(() => _host.Get<ILearnerService>().GetModelAsync(Ct));
        model.PayeeHistory("Corner Store")[ledger.Household].ShouldBe(1);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Delete_offers_undo_and_split_and_transfer_approve()
    {
        var ledger = await ReviewTestLedger.CreateAsync(_host);
        var (window, shell, vm) = await OpenAsync();

        await PressAsync(window, vm, PhysicalKey.D);
        (await GetAsync(ledger.Pending[0])).IsDeleted.ShouldBeTrue();
        shell.Status.CanUndo.ShouldBeTrue();
        vm.RemainingCount.ShouldBe(3);
        await shell.UndoCommand.ExecuteAsync(null);
        await SettleAsync(vm);
        vm.RemainingCount.ShouldBe(4);

        // S: split the focused transaction.
        await vm.FocusIndexAsync(3);
        vm.Focused!.Id.ShouldBe(ledger.Pending[3]);
        window.Press(PhysicalKey.S);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is ReviewSplitViewModel, "split editor open");
        var split = (ReviewSplitViewModel)shell.Dialogs.Current!;
        split.Lines[0].Category = split.Categories.Single(c => c.Name == "Groceries");
        split.Lines[0].SetAmount(-10_000);
        split.Lines[1].Category = split.Categories.Single(c => c.Name == "Household");
        split.Lines[1].SetAmount(-5_640);
        split.IsBalanced.ShouldBeTrue();
        split.ConfirmCommand.Execute(null);
        await SettleAsync(vm);
        var costco = await GetAsync(ledger.Pending[3]);
        (costco.IsApproved, costco.Splits.Count).ShouldBe((true, 2));

        // T: make it a transfer.
        await vm.FocusIndexAsync(2);
        window.Press(PhysicalKey.T);
        await UiTestHelpers.WaitUntilAsync(() => shell.Dialogs.Current is PickerDialogViewModel, "account picker open");
        var picker = (PickerDialogViewModel)shell.Dialogs.Current!;
        picker.Selected = picker.Items.Single(i => i.Label == "Savings");
        picker.ConfirmCommand.Execute(null);
        await SettleAsync(vm);
        var transfer = await GetAsync(ledger.Pending[2]);
        (transfer.IsApproved, transfer.TransferAccountId).ShouldBe((true, (Guid?)ledger.Savings));
        vm.RemainingCount.ShouldBe(2);
        window.Close();
    }

    [AvaloniaFact]
    public async Task Batch_approval_takes_only_confident_suggestions()
    {
        var ledger = await ReviewTestLedger.CreateAsync(_host);
        var (window, _, vm) = await OpenAsync();

        // TRADER JOES (4 of 4) and Taco Truck (3 of 3) reach 90%; Corner Store and Costco have no history.
        vm.BatchCount.ShouldBe(2);
        vm.BatchText.ShouldBe("Approve 2 with confidence ≥ 90%");
        await vm.BatchApproveCommand.ExecuteAsync(null);
        await SettleAsync(vm);

        vm.RemainingCount.ShouldBe(2);
        vm.BatchCount.ShouldBe(0);
        vm.BatchApproveCommand.CanExecute(null).ShouldBeFalse();
        (await GetAsync(ledger.Pending[0])).CategoryId.ShouldBe(ledger.Groceries);
        (await GetAsync(ledger.Pending[1])).CategoryId.ShouldBe(ledger.Dining);
        (await GetAsync(ledger.Pending[2])).IsApproved.ShouldBeFalse();
        vm.DoneCount.ShouldBe(2);
        vm.ProgressText.ShouldBe("3 of 4");
        window.Close();
    }

    [AvaloniaFact]
    public async Task Suggestions_show_a_preparing_state_while_the_learner_loads()
    {
        await ReviewTestLedger.CreateAsync(_host);
        var learner = new BlockingLearner();
        var categorization = new CategorizationService(_host.Get<IDbContextFactory<KeelDbContext>>(), _host.Get<LedgerWriter>(), learner, _host.Get<ICategorizationEngine>());
        var vm = new ReviewViewModel(_host.Get<IRegisterQuery>(), categorization, learner, _host.Get<ITransactionService>(),
            _host.Get<Keel.Application.Categories.ICategoryService>(), _host.Get<Keel.Application.Accounts.IAccountService>(), _host.Get<RuleEditorFlow>(),
            _host.Get<DialogService>(), _host.Get<StatusService>(), _host.Get<Keel.Application.Navigation.INavigationService>(),
            new CommunityToolkit.Mvvm.Messaging.WeakReferenceMessenger());

        vm.OnNavigatedTo(null);
        await vm.Loading;
        Dispatcher.UIThread.RunJobs();
        vm.Focused.ShouldNotBeNull();
        vm.IsPreparingSuggestions.ShouldBeTrue();

        learner.Release();
        await SettleAsync(vm);
        vm.IsPreparingSuggestions.ShouldBeFalse();
        vm.SuggestionState.ShouldBe(SuggestionState.Ready);
    }

    private sealed class BlockingLearner : ILearnerService
    {
        private readonly TaskCompletionSource _gate = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public LearnerStatus Status { get; private set; } = LearnerStatus.Preparing;

        public event EventHandler? StatusChanged;

        public void Release()
        {
            Status = LearnerStatus.Ready;
            StatusChanged?.Invoke(this, EventArgs.Empty);
            _gate.TrySetResult();
        }

        public async Task<LearnerModel> GetModelAsync(CancellationToken ct)
        {
            await _gate.Task;
            return CategoryLearner.Train([]);
        }

        public Task<LearnerModel> PeekModelAsync(CancellationToken ct) => GetModelAsync(ct);

        public Task WarmUpAsync(CancellationToken ct) => GetModelAsync(ct);

        public Task<LearnerModel> RebuildAsync(CancellationToken ct) => GetModelAsync(ct);
    }
}
