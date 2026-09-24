using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommunityToolkit.Mvvm.Messaging;
using Keel.Application.Ledger;
using Keel.Application.Messaging;
using Keel.Application.Navigation;
using Keel.Application.Rules;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Rules;

/// <summary>
/// The rules list (Settings → Rules, and the "Manage rules" page; F-TXN-4): evaluation order with
/// move up/down and drag, enabled toggle, name, summary, match count on demand, edit, delete,
/// apply to existing transactions, and new rule.
/// </summary>
public sealed partial class RulesViewModel : PageViewModel, INavigationTarget, IRecipient<RulesChanged>, IRecipient<LedgerChanged>
{
    private readonly IRuleService _rules;
    private readonly RuleEditorFlow _flow;
    private readonly StatusService _status;
    private int _version;
    private bool _loaded;

    /// <summary>Creates the view model.</summary>
    public RulesViewModel(IRuleService rules, RuleEditorFlow flow, StatusService status, IMessenger messenger)
    {
        ArgumentNullException.ThrowIfNull(messenger);
        _rules = rules;
        _flow = flow;
        _status = status;
        messenger.Register<RulesChanged>(this);
        messenger.Register<LedgerChanged>(this);
    }

    /// <inheritdoc />
    public override string Title => Strings.Rules_Title;

    /// <inheritdoc />
    public override string Subtitle => Strings.Rules_Subtitle;

    /// <inheritdoc />
    public override string EmptyHeading => Strings.Rules_EmptyHeading;

    /// <inheritdoc />
    public override string EmptyMessage => Strings.Rules_EmptyMessage;

    /// <summary>Rules in evaluation order.</summary>
    public ObservableCollection<RuleRowViewModel> Rules { get; } = [];

    /// <summary>Loading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty), nameof(HasRules))]
    public partial bool IsLoading { get; private set; }

    /// <summary>Load error.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasError), nameof(IsEmpty), nameof(HasRules))]
    public partial string? ErrorMessage { get; private set; }

    /// <summary>Whether loading failed.</summary>
    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>No rules yet.</summary>
    public bool IsEmpty => !IsLoading && !HasError && Rules.Count == 0;

    /// <summary>Rules to show.</summary>
    public bool HasRules => !HasError && Rules.Count > 0;

    /// <summary>"12 rules, 10 enabled".</summary>
    [ObservableProperty]
    public partial string CountText { get; private set; } = string.Empty;

    /// <summary>The current load (tests await it).</summary>
    public Task Loading { get; private set; } = Task.CompletedTask;

    /// <summary>Loads once; later calls return the current load.</summary>
    public Task EnsureLoadedAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            Loading = LoadAsync();
        }

        return Loading;
    }

    /// <inheritdoc />
    public void OnNavigatedTo(object? parameter) => Loading = LoadAsync();

    /// <inheritdoc />
    public void Receive(RulesChanged message) => Dispatcher.UIThread.Post(Refresh);

    /// <inheritdoc />
    public void Receive(LedgerChanged message) => Dispatcher.UIThread.Post(Refresh);

    /// <summary>Moves a rule to a new position (drag and drop) and persists the order.</summary>
    public async Task MoveToAsync(RuleRowViewModel row, int index)
    {
        ArgumentNullException.ThrowIfNull(row);
        var order = Rules.Select(r => r.Id).ToList();
        var from = order.IndexOf(row.Id);
        if (from < 0)
        {
            return;
        }

        index = Math.Clamp(index, 0, order.Count - 1);
        if (index == from)
        {
            return;
        }

        order.RemoveAt(from);
        order.Insert(index, row.Id);
        Rules.Move(from, index);
        Renumber();
        await RunAsync(() => _rules.ReorderAsync(order, CancellationToken.None), Strings.Status_RulesReordered);
    }

    [RelayCommand]
    private Task NewRuleAsync() => _flow.NewAsync();

    [RelayCommand]
    private async Task EditAsync(RuleRowViewModel? row)
    {
        if (row is not null)
        {
            await _flow.EditAsync(row.Rule);
        }
    }

    [RelayCommand]
    private async Task DeleteAsync(RuleRowViewModel? row)
    {
        if (row is not null)
        {
            await _flow.DeleteAsync(row.Rule);
        }
    }

    [RelayCommand]
    private Task MoveUpAsync(RuleRowViewModel? row) => row is null ? Task.CompletedTask
        : RunAsync(() => _rules.MoveAsync(row.Id, -1, CancellationToken.None), Strings.Status_RulesReordered);

    [RelayCommand]
    private Task MoveDownAsync(RuleRowViewModel? row) => row is null ? Task.CompletedTask
        : RunAsync(() => _rules.MoveAsync(row.Id, +1, CancellationToken.None), Strings.Status_RulesReordered);

    [RelayCommand]
    private async Task CountMatchesAsync(RuleRowViewModel? row)
    {
        if (row?.Rule.Definition is not { } definition)
        {
            return;
        }

        row.IsCounting = true;
        try
        {
            var count = await _rules.CountMatchesAsync(definition, CancellationToken.None);
            row.MatchCountText = LedgerText.Format(Strings.Rules_MatchCount, count.ToString("N0", CultureInfo.CurrentCulture));
        }
        finally
        {
            row.IsCounting = false;
        }
    }

    [RelayCommand]
    private async Task ApplyToExistingAsync(RuleRowViewModel? row)
    {
        if (row is not null)
        {
            await _flow.ApplyToExistingAsync([row.Id], LedgerText.Format(Strings.Retroactive_OneRule, row.Name));
        }
    }

    [RelayCommand]
    private async Task ApplyAllAsync()
    {
        var enabled = Rules.Where(r => r.IsEnabled && r.Rule.IsReadable).Select(r => r.Id).ToList();
        if (enabled.Count > 0)
        {
            await _flow.ApplyToExistingAsync(enabled, LedgerText.Format(Strings.Retroactive_AllRules, enabled.Count));
        }
    }

    [RelayCommand]
    private void Retry() => Loading = LoadAsync();

    private void Refresh()
    {
        if (_loaded)
        {
            Loading = LoadAsync();
        }
    }

    private async Task SetEnabledAsync(RuleRowViewModel row, bool enabled) =>
        await RunAsync(() => _rules.SetEnabledAsync(row.Id, enabled, CancellationToken.None),
            LedgerText.Format(enabled ? Strings.Status_RuleEnabled : Strings.Status_RuleDisabled, row.Name));

    private async Task RunAsync(Func<Task> action, string done)
    {
        try
        {
            await action();
            _status.Show(done, offerUndo: true);
        }
        catch (LedgerValidationException ex)
        {
            _status.Show(LedgerText.Error(ex.Error), isError: true);
            Loading = LoadAsync();
        }
    }

    private async Task LoadAsync()
    {
        _loaded = true;
        var version = ++_version;
        IsLoading = Rules.Count == 0;
        try
        {
            ErrorMessage = null;
            var rules = await _rules.GetRulesAsync(CancellationToken.None);
            if (version != _version)
            {
                return;
            }

            var counts = Rules.ToDictionary(r => r.Id, r => r.MatchCountText);
            Rules.Clear();
            foreach (var rule in rules)
            {
                var row = new RuleRowViewModel(rule, SetEnabledAsync) { Owner = this };
                if (counts.TryGetValue(rule.Id, out var text))
                {
                    row.MatchCountText = text;
                }

                Rules.Add(row);
            }

            Renumber();
            CountText = LedgerText.Format(Strings.Rules_Count, rules.Count, rules.Count(r => r.IsEnabled));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            if (version == _version)
            {
                ErrorMessage = LedgerText.Format(Strings.Rules_ErrorLoading, ex.Message);
            }
        }
        finally
        {
            if (version == _version)
            {
                IsLoading = false;
                OnPropertyChanged(nameof(IsEmpty));
                OnPropertyChanged(nameof(HasRules));
            }
        }
    }

    private void Renumber()
    {
        for (var i = 0; i < Rules.Count; i++)
        {
            Rules[i].Position = i + 1;
            Rules[i].IsFirst = i == 0;
            Rules[i].IsLast = i == Rules.Count - 1;
        }
    }
}

/// <summary>One row of the rules list.</summary>
public sealed partial class RuleRowViewModel : ObservableObject
{
    private readonly Func<RuleRowViewModel, bool, Task> _setEnabled;
    private bool _syncing;

    /// <summary>Creates the row.</summary>
    public RuleRowViewModel(RuleDto rule, Func<RuleRowViewModel, bool, Task> setEnabled)
    {
        ArgumentNullException.ThrowIfNull(rule);
        Rule = rule;
        _setEnabled = setEnabled;
        _syncing = true;
        IsEnabled = rule.IsEnabled;
        _syncing = false;
    }

    /// <summary>The rule.</summary>
    public RuleDto Rule { get; }

    /// <summary>The list (row buttons bind to its commands).</summary>
    public RulesViewModel? Owner { get; init; }

    /// <summary>Id.</summary>
    public Guid Id => Rule.Id;

    /// <summary>Name.</summary>
    public string Name => Rule.Name;

    /// <summary>Summary, or the reason it cannot be read.</summary>
    public string Summary => Rule.IsReadable ? Rule.Summary : LedgerText.Format(Strings.Rules_Unreadable, Rule.FormatError);

    /// <summary>Whether the rule has validation errors (the engine skips it).</summary>
    public bool HasProblems => Rule.HasErrors || !Rule.IsReadable;

    /// <summary>The first error, for the warning line.</summary>
    public string ProblemText => Rule.Problems.FirstOrDefault(p => p.Severity == Keel.Domain.Rules.RuleProblemSeverity.Error)?.Message ?? string.Empty;

    /// <summary>Whether the rule continues after a match.</summary>
    public bool Continues => Rule.ContinueAfterMatch;

    /// <summary>Enabled; toggling saves.</summary>
    [ObservableProperty]
    public partial bool IsEnabled { get; set; }

    /// <summary>1-based position.</summary>
    [ObservableProperty]
    public partial int Position { get; set; }

    /// <summary>First row (move up disabled).</summary>
    [ObservableProperty]
    public partial bool IsFirst { get; set; }

    /// <summary>Last row (move down disabled).</summary>
    [ObservableProperty]
    public partial bool IsLast { get; set; }

    /// <summary>"42 matches", after counting.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMatchCount))]
    public partial string? MatchCountText { get; set; }

    /// <summary>Whether a count is shown.</summary>
    public bool HasMatchCount => !string.IsNullOrEmpty(MatchCountText);

    /// <summary>Counting.</summary>
    [ObservableProperty]
    public partial bool IsCounting { get; set; }

    partial void OnIsEnabledChanged(bool value)
    {
        if (!_syncing)
        {
            _ = _setEnabled(this, value);
        }
    }
}
