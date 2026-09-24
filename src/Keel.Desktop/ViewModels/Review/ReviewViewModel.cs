using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Accounts;
using Keel.Application.Categories;
using Keel.Application.Categorization;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Application.Rules;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;
using Keel.Desktop.ViewModels.Review;
using Keel.Desktop.ViewModels.Rules;

namespace Keel.Desktop.ViewModels;

/// <summary>
/// The review queue (PRD 9.5, F-TXN-6): unapproved transactions across all accounts, oldest first,
/// paged from the database, one focused at a time with its details and up to five suggestions
/// (rule match first, then payee default and learner suggestions with confidence and explanation).
/// Keyboard triage: A approve, 1–9 pick a suggestion, C change category, S split, T transfer,
/// R create rule, D delete, J/K move; plus "Approve all with confidence ≥ 90%". Every decision
/// approves through the ledger services, so the learner learns from it (ADR 0028).
/// </summary>
public sealed partial class ReviewViewModel : PageViewModel, INavigationTarget, IRecipient<LedgerChanged>, IRecipient<RulesChanged>
{
    /// <summary>Rows loaded per page.</summary>
    public const int PageSize = 50;

    /// <summary>The batch-approval threshold.</summary>
    public const double BatchThreshold = 0.90;

    /// <summary>Most suggestions shown.</summary>
    public const int MaxSuggestions = 5;

    private static readonly RegisterFilter Unapproved = new(UnapprovedOnly: true);
    private static readonly RegisterSort OldestFirst = new(RegisterSortColumn.Date, Descending: false);

    private readonly IRegisterQuery _register;
    private readonly ICategorizationService _categorization;
    private readonly ILearnerService _learner;
    private readonly ITransactionService _transactions;
    private readonly ICategoryService _categories;
    private readonly IAccountService _accounts;
    private readonly RuleEditorFlow _rules;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private readonly INavigationService _navigation;
    private int _loadVersion;
    private int _suggestVersion;
    private int _batchVersion;
    private int _pageStart;
    private bool _opened;
    private IReadOnlyList<ReviewDecision> _batch = [];

    /// <summary>Creates the view model.</summary>
    public ReviewViewModel(
        IRegisterQuery register,
        ICategorizationService categorization,
        ILearnerService learner,
        ITransactionService transactions,
        ICategoryService categories,
        IAccountService accounts,
        RuleEditorFlow rules,
        DialogService dialogs,
        StatusService status,
        INavigationService navigation,
        IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(learner);
        ArgumentNullException.ThrowIfNull(messenger);
        _register = register;
        _categorization = categorization;
        _learner = learner;
        _transactions = transactions;
        _categories = categories;
        _accounts = accounts;
        _rules = rules;
        _dialogs = dialogs;
        _status = status;
        _navigation = navigation;
        _learner.StatusChanged += (_, _) => Dispatcher.UIThread.Post(OnLearnerStatusChanged);
        messenger.Register<LedgerChanged>(this);
        messenger.Register<RulesChanged>(this);
    }

    /// <inheritdoc />
    public override string Title => Strings.Page_Review_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Review_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Review_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Review_EmptyMessage;

    /// <summary>The loaded page of the queue.</summary>
    public ObservableCollection<ReviewItemViewModel> Items { get; } = [];

    /// <summary>Unapproved transactions left.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressValue), nameof(ShowQueue), nameof(ShowEmptyState))]
    public partial int RemainingCount { get; private set; }

    /// <summary>Transactions approved or removed since the queue was opened.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ProgressText), nameof(ProgressValue))]
    public partial int DoneCount { get; private set; }

    /// <summary>The focused transaction.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasFocus), nameof(ProgressText), nameof(ProgressValue))]
    public partial ReviewItemViewModel? Focused { get; private set; }

    /// <summary>Whether a transaction is focused.</summary>
    public bool HasFocus => Focused is not null;

    /// <summary>"23 of 61".</summary>
    public string ProgressText => RemainingCount == 0 ? string.Empty
        : LedgerText.Format(Strings.Review_Progress,
            (DoneCount + (Focused?.Index ?? 0) + 1).ToString("N0", CultureInfo.CurrentCulture),
            (DoneCount + RemainingCount).ToString("N0", CultureInfo.CurrentCulture));

    /// <summary>Progress 0–100.</summary>
    public double ProgressValue => DoneCount + RemainingCount == 0 ? 0 : 100.0 * DoneCount / (DoneCount + RemainingCount);

    /// <summary>Suggestions for the focused transaction.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSuggestions), nameof(ShowNoSuggestions))]
    public partial IReadOnlyList<SuggestionViewModel> Suggestions { get; private set; } = [];

    /// <summary>Whether suggestions exist.</summary>
    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>The suggestions panel state.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsPreparingSuggestions), nameof(IsLoadingSuggestions), nameof(ShowNoSuggestions), nameof(HasSuggestionError))]
    public partial SuggestionState SuggestionState { get; private set; }

    /// <summary>"Preparing suggestions" (the learner is loading).</summary>
    public bool IsPreparingSuggestions => SuggestionState == SuggestionState.Preparing;

    /// <summary>Scoring the focused transaction.</summary>
    public bool IsLoadingSuggestions => SuggestionState == SuggestionState.Loading;

    /// <summary>Scored and nothing to suggest.</summary>
    public bool ShowNoSuggestions => SuggestionState == SuggestionState.Ready && Suggestions.Count == 0;

    /// <summary>Scoring failed.</summary>
    public bool HasSuggestionError => SuggestionState == SuggestionState.Error;

    /// <summary>One-line reasoning (the trace summary).</summary>
    [ObservableProperty]
    public partial string? TraceSummary { get; private set; }

    /// <summary>The bank descriptor, when it differs from the payee name.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasDescriptor))]
    public partial string? Descriptor { get; private set; }

    /// <summary>Whether <see cref="Descriptor"/> is shown.</summary>
    public bool HasDescriptor => !string.IsNullOrWhiteSpace(Descriptor);

    /// <summary>The "Why?" steps.</summary>
    [ObservableProperty]
    public partial IReadOnlyList<TraceStepViewModel> TraceSteps { get; private set; } = [];

    /// <summary>Whether the "Why?" steps are expanded.</summary>
    [ObservableProperty]
    public partial bool ShowTrace { get; set; }

    /// <summary>Transactions the batch approval would approve.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(BatchText), nameof(CanBatchApprove))]
    [NotifyCanExecuteChangedFor(nameof(BatchApproveCommand))]
    public partial int BatchCount { get; private set; }

    /// <summary>"Approve 12 with confidence ≥ 90%".</summary>
    public string BatchText => LedgerText.Format(Strings.Review_BatchApprove, BatchCount.ToString("N0", CultureInfo.CurrentCulture),
        BatchThreshold.ToString("0%", CultureInfo.CurrentCulture));

    /// <summary>Whether the batch approval has anything to do.</summary>
    public bool CanBatchApprove => BatchCount > 0;

    /// <summary>First load.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowQueue), nameof(ShowEmptyState))]
    public partial bool IsLoading { get; private set; } = true;

    /// <summary>Load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(ShowQueue), nameof(ShowEmptyState))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Whether the queue is shown.</summary>
    public bool ShowQueue => !IsLoading && !HasError && RemainingCount > 0;

    /// <summary>"Nothing to review".</summary>
    public bool ShowEmptyState => !IsLoading && !HasError && RemainingCount == 0;

    /// <summary>Keyboard reference shown in the footer.</summary>
    public string KeyHints => Strings.Review_KeyHints;

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The current suggestion load (tests await it).</summary>
    public Task SuggestionsLoading { get; private set; } = Task.CompletedTask;

    /// <summary>The current batch count (tests await it).</summary>
    public Task BatchLoading { get; private set; } = Task.CompletedTask;

    /// <summary>Raised when the view should put keyboard focus back on the queue.</summary>
    public event EventHandler? FocusRequested;

    /// <summary>Waits for loads, suggestions and the batch count to settle.</summary>
    public async Task WhenIdleAsync()
    {
        for (var i = 0; i < 20; i++)
        {
            var (loading, suggestions, batch) = (Loading, SuggestionsLoading, BatchLoading);
            await loading;
            await suggestions;
            await batch;
            if (ReferenceEquals(loading, Loading) && ReferenceEquals(suggestions, SuggestionsLoading) && ReferenceEquals(batch, BatchLoading))
            {
                return;
            }
        }
    }

    /// <inheritdoc />
    public void OnNavigatedTo(object? parameter)
    {
        _opened = true;
        DoneCount = 0;
        _ = WarmUpLearnerAsync();
        Loading = LoadAsync(Focused?.Id, Focused?.Index ?? 0, showSpinner: Items.Count == 0);
    }

    /// <inheritdoc />
    public void Receive(LedgerChanged message) => Dispatcher.UIThread.Post(Reload);

    /// <inheritdoc />
    public void Receive(RulesChanged message) => Dispatcher.UIThread.Post(() =>
    {
        if (Focused is { } focused)
        {
            SuggestionsLoading = LoadSuggestionsAsync(focused);
            BatchLoading = LoadBatchAsync();
        }
    });

    /// <summary>Moves the focus to a queue position (clicking a row).</summary>
    public Task FocusIndexAsync(int index)
    {
        if (RemainingCount == 0)
        {
            return Task.CompletedTask;
        }

        index = Math.Clamp(index, 0, RemainingCount - 1);
        if (index >= _pageStart && index < _pageStart + Items.Count)
        {
            SetFocus(Items[index - _pageStart]);
            return Task.CompletedTask;
        }

        Loading = LoadAsync(null, index, showSpinner: false);
        return Loading;
    }

    /// <summary>A: approve; an uncategorized row takes its primary suggestion.</summary>
    [RelayCommand]
    public Task ApproveAsync()
    {
        if (Focused is not { } item)
        {
            return Task.CompletedTask;
        }

        var primary = Suggestions.FirstOrDefault(s => s.IsPrimary)?.Suggestion;
        var decision = item.IsUncategorized && primary is not null
            ? new ReviewDecision(item.Id, primary.CategoryId, primary.Source == CategorizationSource.Rule)
            : new ReviewDecision(item.Id, null);
        return DecideAsync(item, decision, Strings.Status_ReviewApproved);
    }

    /// <summary>1–9: approve with that suggestion.</summary>
    [RelayCommand]
    public Task PickSuggestionAsync(int number)
    {
        if (Focused is not { } item || number < 1 || number > Suggestions.Count)
        {
            return Task.CompletedTask;
        }

        var suggestion = Suggestions[number - 1].Suggestion;
        return DecideAsync(item, new ReviewDecision(item.Id, suggestion.CategoryId, suggestion.Source == CategorizationSource.Rule),
            LedgerText.Format(Strings.Status_ReviewCategorized, suggestion.CategoryName));
    }

    /// <summary>Clicking a suggestion.</summary>
    [RelayCommand]
    public Task ChooseSuggestionAsync(SuggestionViewModel? suggestion) =>
        suggestion is null ? Task.CompletedTask : PickSuggestionAsync(suggestion.Number);

    /// <summary>C: pick any category (search as you type), then approve.</summary>
    [RelayCommand]
    public async Task ChangeCategoryAsync()
    {
        if (Focused is not { } item)
        {
            return;
        }

        var categories = await _categories.GetCategoriesAsync(includeHidden: false, CancellationToken.None);
        var picker = new PickerDialogViewModel(Strings.Review_ChangeCategoryTitle, LedgerText.Format(Strings.Review_ChangeCategoryPrompt, item.Payee),
            categories.Where(c => !c.IsCreditCardPayment).Select(c => new PickerItem(c.Id, c.FullName)).ToList());
        if (await _dialogs.ShowAsync(picker) && picker.Selected is { } choice)
        {
            await DecideAsync(item, new ReviewDecision(item.Id, choice.Id), LedgerText.Format(Strings.Status_ReviewCategorized, choice.Label));
        }

        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>S: split, then approve.</summary>
    [RelayCommand]
    public async Task SplitAsync()
    {
        if (Focused is not { } item || item.IsTransfer)
        {
            return;
        }

        var dto = await _transactions.GetAsync(item.Id, CancellationToken.None);
        if (dto is null)
        {
            return;
        }

        var categories = await _categories.GetCategoriesAsync(includeHidden: false, CancellationToken.None);
        var options = categories.Where(c => !c.IsCreditCardPayment).Select(CategoryOption.From).ToList();
        var dialog = new ReviewSplitViewModel(_transactions, dto, options, item.Row.Currency);
        if (await _dialogs.ShowAsync(dialog))
        {
            Advance(Strings.Status_ReviewSplit);
        }

        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>T: make it a transfer (pick the other account), then approve.</summary>
    [RelayCommand]
    public async Task MarkTransferAsync()
    {
        if (Focused is not { } item || item.IsTransfer)
        {
            return;
        }

        var accounts = await _accounts.GetAccountsAsync(includeClosed: false, CancellationToken.None);
        var picker = new PickerDialogViewModel(Strings.Review_TransferTitle, LedgerText.Format(Strings.Review_TransferPrompt, item.AmountText, item.Account),
            accounts.Where(a => a.Id != item.Row.AccountId).Select(a => new PickerItem(a.Id, a.Name)).ToList());
        if (await _dialogs.ShowAsync(picker) && picker.Selected is { } choice)
        {
            var dto = await _transactions.GetAsync(item.Id, CancellationToken.None);
            if (dto is not null)
            {
                try
                {
                    await _transactions.SaveAsync(
                        new SaveTransactionRequest(dto.Id, dto.AccountId, dto.Date, dto.Amount, null, dto.CategoryId, dto.Memo, dto.Status, IsApproved: true, TransferAccountId: choice.Id),
                        CancellationToken.None);
                    Advance(LedgerText.Format(Strings.Status_ReviewTransfer, choice.Label));
                }
                catch (LedgerValidationException ex)
                {
                    _status.Show(LedgerText.Error(ex.Error), isError: true);
                }
            }
        }

        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>R: create a rule from this transaction (prefilled), then rescore.</summary>
    [RelayCommand]
    public async Task CreateRuleAsync()
    {
        if (Focused is { } item)
        {
            await _rules.CreateFromTransactionAsync(item.Id);
            SuggestionsLoading = LoadSuggestionsAsync(item);
        }

        FocusRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>D: delete with an undo toast.</summary>
    [RelayCommand]
    public async Task DeleteAsync()
    {
        if (Focused is not { } item)
        {
            return;
        }

        try
        {
            var count = await _transactions.DeleteAsync([item.Id], CancellationToken.None);
            DoneCount++;
            _status.Show(LedgerText.Format(Strings.Status_Deleted, count), offerUndo: true);
            Loading = LoadAsync(null, item.Index, showSpinner: false);
            await Loading;
        }
        catch (LedgerValidationException ex)
        {
            _status.Show(LedgerText.Error(ex.Error), isError: true);
        }
    }

    /// <summary>J: next transaction.</summary>
    [RelayCommand]
    public Task NextAsync() => Focused is { } item ? FocusIndexAsync(item.Index + 1) : Task.CompletedTask;

    /// <summary>K: previous transaction.</summary>
    [RelayCommand]
    public Task PreviousAsync() => Focused is { } item ? FocusIndexAsync(item.Index - 1) : Task.CompletedTask;

    /// <summary>Approve every transaction whose primary suggestion is at least 90% confident.</summary>
    [RelayCommand(CanExecute = nameof(CanBatchApprove))]
    public async Task BatchApproveAsync()
    {
        var decisions = _batch;
        if (decisions.Count == 0)
        {
            return;
        }

        try
        {
            var count = await _categorization.ApproveAsync(decisions, CancellationToken.None);
            DoneCount += count;
            _status.Show(LedgerText.Format(Strings.Status_ReviewBatchApproved, count), offerUndo: count > 0);
            Loading = LoadAsync(null, Focused?.Index ?? 0, showSpinner: false);
            await Loading;
        }
        catch (LedgerValidationException ex)
        {
            _status.Show(LedgerText.Error(ex.Error), isError: true);
        }
    }

    /// <summary>Opens the rules page.</summary>
    [RelayCommand]
    public void ManageRules() => _navigation.NavigateTo<RulesViewModel>();

    /// <summary>Retries after a load error.</summary>
    [RelayCommand]
    public void Retry() => Loading = LoadAsync(Focused?.Id, Focused?.Index ?? 0, showSpinner: true);

    [RelayCommand]
    private void ToggleTrace() => ShowTrace = !ShowTrace;

    private async Task DecideAsync(ReviewItemViewModel item, ReviewDecision decision, string done)
    {
        try
        {
            await _categorization.ApproveAsync([decision], CancellationToken.None);
            Advance(done);
        }
        catch (LedgerValidationException ex)
        {
            _status.Show(LedgerText.Error(ex.Error), isError: true);
        }

        await Loading;
    }

    // The decided row leaves the queue; the next one moves into the same position.
    private void Advance(string done)
    {
        var index = Focused?.Index ?? 0;
        DoneCount++;
        _status.Show(done, offerUndo: true);
        Loading = LoadAsync(null, index, showSpinner: false);
    }

    private void Reload()
    {
        if (_opened)
        {
            Loading = LoadAsync(Focused?.Id, Focused?.Index ?? 0, showSpinner: false);
        }
    }

    private async Task WarmUpLearnerAsync()
    {
        try
        {
            await _learner.WarmUpAsync(CancellationToken.None);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Suggestions fall back to rules and payee defaults; the state is shown per transaction.
        }
    }

    private void OnLearnerStatusChanged()
    {
        if (SuggestionState is SuggestionState.Preparing or SuggestionState.Loading)
        {
            SuggestionState = _learner.Status == LearnerStatus.Preparing ? SuggestionState.Preparing : SuggestionState.Loading;
        }
    }

    // Loads the count and the page containing the focus. Keeps the focused transaction when it is
    // still unapproved; otherwise focuses whatever now sits at its position.
    private async Task LoadAsync(Guid? keepId, int index, bool showSpinner)
    {
        var version = ++_loadVersion;
        if (showSpinner)
        {
            IsLoading = true;
        }

        try
        {
            ErrorMessage = null;
            var count = await _register.CountAsync(Unapproved, CancellationToken.None);
            if (keepId is { } id)
            {
                var found = await _register.IndexOfAsync(Unapproved, OldestFirst, id, CancellationToken.None);
                if (found >= 0)
                {
                    index = found;
                }
            }

            if (version != _loadVersion)
            {
                return;
            }

            if (count == 0)
            {
                Items.Clear();
                _pageStart = 0;
                RemainingCount = 0;
                SetFocus(null);
                BatchCount = 0;
                _batch = [];
                return;
            }

            index = Math.Clamp(index, 0, count - 1);
            var start = index / PageSize * PageSize;
            var rows = await _register.GetPageAsync(Unapproved, OldestFirst, start, PageSize, CancellationToken.None);
            if (version != _loadVersion)
            {
                return;
            }

            var previous = Focused;
            Items.Clear();
            for (var i = 0; i < rows.Count; i++)
            {
                Items.Add(new ReviewItemViewModel(rows[i], start + i));
            }

            _pageStart = start;
            RemainingCount = count;
            var focus = Items.Count == 0 ? null : Items[Math.Clamp(index - start, 0, Items.Count - 1)];
            SetFocus(focus, force: focus is not null && previous is not null && focus.Id == previous.Id && !SameRow(focus.Row, previous.Row));
            BatchLoading = LoadBatchAsync();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _loadVersion)
            {
                ErrorMessage = LedgerText.Format(Strings.Review_ErrorLoading, ex.Message);
            }
        }
        finally
        {
            if (version == _loadVersion)
            {
                IsLoading = false;
            }
        }
    }

    private static bool SameRow(RegisterRow a, RegisterRow b) =>
        a.CategoryId == b.CategoryId && a.Amount == b.Amount && a.Payee == b.Payee && a.Splits.Count == b.Splits.Count
        && a.TransferAccountId == b.TransferAccountId && a.Memo == b.Memo && a.Date == b.Date && a.AccountId == b.AccountId;

    private void SetFocus(ReviewItemViewModel? item, bool force = false)
    {
        var changed = Focused?.Id != item?.Id || force;
        if (Focused is not null)
        {
            Focused.IsFocused = false;
        }

        Focused = item;
        if (item is not null)
        {
            item.IsFocused = true;
        }

        OnPropertyChanged(nameof(ProgressText));
        if (item is null)
        {
            Suggestions = [];
            TraceSteps = [];
            TraceSummary = null;
            Descriptor = null;
            SuggestionState = SuggestionState.None;
            return;
        }

        if (changed)
        {
            SuggestionsLoading = LoadSuggestionsAsync(item);
        }
    }

    private async Task LoadSuggestionsAsync(ReviewItemViewModel item)
    {
        var version = ++_suggestVersion;
        Descriptor = null;
        Suggestions = [];
        TraceSteps = [];
        TraceSummary = null;
        SuggestionState = _learner.Status is LearnerStatus.Preparing or LearnerStatus.NotLoaded ? SuggestionState.Preparing : SuggestionState.Loading;
        try
        {
            var result = await _categorization.SuggestAsync(item.Id, CancellationToken.None);
            if (version != _suggestVersion)
            {
                return;
            }

            if (result is null)
            {
                SuggestionState = SuggestionState.Ready;
                return;
            }

            var raw = result.Snapshot.PayeeRaw.Trim();
            Descriptor = raw.Length > 0 && !string.Equals(raw, result.Snapshot.Payee.Trim(), StringComparison.Ordinal) ? raw : null;
            Suggestions = result.Result.Suggestions.Take(MaxSuggestions).Select((s, i) => new SuggestionViewModel(i + 1, s) { Choose = ChooseSuggestionCommand }).ToList();
            TraceSummary = result.Result.Trace.Summary;
            TraceSteps = result.Result.Trace.Steps
                .Select(s => new TraceStepViewModel(Strings.ResourceManager.GetString("Review_Stage_" + s.Stage, Strings.Culture) ?? s.Stage.ToString(), s.Detail))
                .ToList();
            SuggestionState = SuggestionState.Ready;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _suggestVersion)
            {
                TraceSummary = LedgerText.Format(Strings.Review_SuggestionsError, ex.Message);
                SuggestionState = SuggestionState.Error;
            }
        }
    }

    private async Task LoadBatchAsync()
    {
        var version = ++_batchVersion;
        try
        {
            var plan = await _categorization.PlanBatchApprovalAsync(BatchThreshold, CancellationToken.None);
            if (version == _batchVersion)
            {
                _batch = plan;
                BatchCount = plan.Count;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _batchVersion)
            {
                _batch = [];
                BatchCount = 0;
            }
        }
    }
}
