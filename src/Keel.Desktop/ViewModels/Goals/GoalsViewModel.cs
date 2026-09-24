using System.Collections.ObjectModel;
using System.Data.Common;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Accounts;
using Keel.Application.Goals;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Goals;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain;
using Keel.Domain.Budgeting;

// The page view model stays in Keel.Desktop.ViewModels so the ViewLocator maps it to Views/GoalsView.
namespace Keel.Desktop.ViewModels;

/// <summary>
/// Goals (PRD 9.7, F-GOAL-1): a card per category with a savings-balance-by-date target, and a
/// "New goal" wizard. Refreshes on <see cref="LedgerChanged"/> and <see cref="BudgetChanged"/>.
/// </summary>
public sealed partial class GoalsViewModel : PageViewModel, INavigationTarget, IRecipient<LedgerChanged>, IRecipient<BudgetChanged>
{
    private readonly IGoalService _goals;
    private readonly IAccountService _accounts;
    private readonly INavigationService _navigation;
    private readonly DialogService _dialogs;
    private readonly StatusService _status;
    private readonly TimeProvider _time;
    private int _version;

    /// <summary>Creates the screen.</summary>
    public GoalsViewModel(IGoalService goals, IAccountService accounts, INavigationService navigation, DialogService dialogs, StatusService status, TimeProvider time, IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        _goals = goals;
        _accounts = accounts;
        _navigation = navigation;
        _dialogs = dialogs;
        _status = status;
        _time = time;
        messenger.Register<LedgerChanged>(this);
        messenger.Register<BudgetChanged>(this);
    }

    /// <inheritdoc />
    public override string Title => Strings.Page_Goals_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Page_Goals_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Page_Goals_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Page_Goals_EmptyMessage;

    /// <summary>Goal cards.</summary>
    public ObservableCollection<GoalCardViewModel> Goals { get; } = [];

    /// <summary>Whether the first load finished.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowGoals))]
    public partial bool IsInitialized { get; private set; }

    /// <summary>Whether any goal exists.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowEmptyState), nameof(ShowGoals))]
    public partial bool HasGoals { get; private set; }

    /// <summary>The designed empty state.</summary>
    public bool ShowEmptyState => IsInitialized && !HasGoals;

    /// <summary>The card list.</summary>
    public bool ShowGoals => IsInitialized && HasGoals;

    /// <summary>Summary line, e.g. "3 goals · $1,250.00 saved of $9,000.00".</summary>
    [ObservableProperty]
    public partial string SummaryText { get; private set; } = string.Empty;

    /// <summary>Load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>The wizard currently open (tests drive it).</summary>
    public NewGoalViewModel? Wizard { get; private set; }

    /// <inheritdoc />
    public void OnNavigatedTo(object? parameter) => Loading = LoadAsync();

    /// <inheritdoc />
    public void Receive(LedgerChanged message) => Dispatcher.UIThread.Post(Refresh);

    /// <inheritdoc />
    public void Receive(BudgetChanged message) => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Opens the "New goal" wizard.</summary>
    [RelayCommand]
    public async Task NewGoalAsync()
    {
        var accounts = await _accounts.GetAccountsAsync(includeClosed: false, CancellationToken.None);
        var currency = accounts.FirstOrDefault(a => a.IsOnBudget)?.Balance.Currency ?? Currency.Default;
        var today = DateOnly.FromDateTime(_time.GetLocalNow().DateTime);
        var wizard = Wizard = new NewGoalViewModel(_goals, accounts.Where(a => !a.IsOnBudget).ToList(), currency, today);
        try
        {
            if (await _dialogs.ShowAsync(wizard) && wizard.Result is { } created)
            {
                _status.Show(LedgerText.Format(Strings.Goals_Created, created.Name));
                await LoadAsync();
            }
        }
        finally
        {
            Wizard = null;
        }
    }

    /// <summary>Opens the goal category's transactions in the register.</summary>
    [RelayCommand]
    public void OpenTransactions(GoalCardViewModel? goal)
    {
        if (goal is not null)
        {
            _navigation.NavigateTo<AccountsViewModel>(new RegisterNavigation(null, null, new CategoryOption(goal.CategoryId, goal.Name, goal.Goal.GroupName)));
        }
    }

    /// <summary>Opens Budget (to assign to a goal).</summary>
    [RelayCommand]
    public void OpenBudget() => _navigation.NavigateTo<BudgetViewModel>();

    private void Refresh()
    {
        if (IsInitialized)
        {
            Loading = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        var version = ++_version;
        try
        {
            var month = BudgetMonth.Of(DateOnly.FromDateTime(_time.GetLocalNow().DateTime));
            var goals = await _goals.GetGoalsAsync(month, CancellationToken.None);
            if (version != _version)
            {
                return;
            }

            ErrorMessage = null;
            var extras = Goals.ToDictionary(g => g.CategoryId, g => g.ExtraPerMonth);
            Goals.Clear();
            foreach (var goal in goals.OrderBy(g => g.TargetDate).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase))
            {
                Goals.Add(new GoalCardViewModel(goal, month) { ExtraPerMonth = extras.GetValueOrDefault(goal.CategoryId) });
            }

            HasGoals = Goals.Count > 0;
            SummaryText = goals.Count == 0 ? string.Empty : LedgerText.Format(Strings.Goals_Summary_Line, goals.Count,
                Reports.ReportFormat.Money(goals.Sum(g => g.Available), goals[0].Currency), Reports.ReportFormat.Money(goals.Sum(g => g.Target), goals[0].Currency));
        }
        catch (Exception ex) when (ex is InvalidOperationException or DbException)
        {
            ErrorMessage = LedgerText.Format(Strings.Goals_ErrorLoading, ex.Message);
        }
        finally
        {
            if (version == _version)
            {
                IsInitialized = true;
            }
        }
    }
}
