using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Budget;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.ViewModels.Budget;

/// <summary>A side of a money move: a category, or Ready to Assign (null id).</summary>
/// <param name="Id">Category, or null for Ready to Assign.</param>
/// <param name="Label">Name.</param>
/// <param name="AvailableText">Its Available (or Ready to Assign) this month.</param>
public sealed record MoveMoneyOption(Guid? Id, string Label, string AvailableText)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// "Move money" (F-BUD-3, <c>M</c> or dropping an Available pill on another row): from and to
/// (either may be Ready to Assign) and an amount, in the shown month. Undoable.
/// </summary>
public sealed partial class MoveMoneyDialogViewModel : DialogViewModel
{
    private readonly IBudgetService _budget;
    private readonly DateOnly _month;
    private readonly Func<Func<Task>, Task> _queue;

    /// <summary>Creates the dialog.</summary>
    /// <param name="budget">Budget service.</param>
    /// <param name="month">Month the money moves in.</param>
    /// <param name="currency">Budget currency.</param>
    /// <param name="options">Ready to Assign and the categories.</param>
    /// <param name="from">Preset source (null: Ready to Assign).</param>
    /// <param name="to">Preset destination (null: none chosen yet unless the source is a category, then Ready to Assign is not preset).</param>
    /// <param name="amount">Preset amount.</param>
    /// <param name="page">The budget page (its write queue keeps the undo order).</param>
    public MoveMoneyDialogViewModel(IBudgetService budget, DateOnly month, string currency, IReadOnlyList<MoveMoneyOption> options, Guid? from, Guid? to, long amount, BudgetViewModel page)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(page);
        _budget = budget;
        _month = month;
        _queue = page.QueueWrite;
        Currency = currency;
        Options = options;
        From = options.FirstOrDefault(o => o.Id == from);
        To = to is null && from is null ? null : options.FirstOrDefault(o => o.Id == to);
        Amount = amount;
    }

    /// <inheritdoc />
    public override string Title => Strings.MoveMoney_Title;

    /// <summary>"In September 2026".</summary>
    public string MonthText => LedgerText.Format(Strings.MoveMoney_Month, BudgetText.Month(_month));

    /// <summary>Budget currency.</summary>
    public string Currency { get; }

    /// <summary>Ready to Assign and every shown category.</summary>
    public IReadOnlyList<MoveMoneyOption> Options { get; }

    /// <summary>Source.</summary>
    [ObservableProperty]
    public partial MoveMoneyOption? From { get; set; }

    /// <summary>Destination.</summary>
    [ObservableProperty]
    public partial MoveMoneyOption? To { get; set; }

    /// <summary>Amount in minor units.</summary>
    [ObservableProperty]
    public partial long Amount { get; set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        if (From is null || To is null)
        {
            Error = Strings.MoveMoney_ChooseBoth;
            return false;
        }

        if (From.Id == To.Id)
        {
            Error = Strings.MoveMoney_SameSide;
            return false;
        }

        if (Amount <= 0)
        {
            Error = Strings.MoveMoney_AmountRequired;
            return false;
        }

        var request = new MoveMoneyRequest(_month, From.Id, To.Id, Amount);
        string? failure = null;
        await _queue(async () =>
        {
            try
            {
                await _budget.MoveMoneyAsync(request, CancellationToken.None);
            }
            catch (LedgerValidationException ex)
            {
                failure = LedgerText.Error(ex.Error);
            }
            catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or KeyNotFoundException)
            {
                failure = ex.Message;
            }
        });
        Error = failure;
        return failure is null;
    }
}

/// <summary>The quick-assign palette (F-BUD-5, <c>Q</c>): pick a value for Assigned with the keyboard.</summary>
public sealed partial class QuickAssignPaletteViewModel : DialogViewModel
{
    private readonly string _category;

    /// <summary>Creates the palette.</summary>
    public QuickAssignPaletteViewModel(string category, IReadOnlyList<QuickAssignChoice> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _category = category;
        Options = options;
        Selected = options.Count > 0 ? options[0] : null;
    }

    /// <inheritdoc />
    public override string Title => LedgerText.Format(Strings.Quick_Title, _category);

    /// <summary>The actions and their values.</summary>
    public IReadOnlyList<QuickAssignChoice> Options { get; }

    /// <summary>The chosen action.</summary>
    [ObservableProperty]
    public partial QuickAssignChoice? Selected { get; set; }

    /// <inheritdoc />
    protected override Task<bool> ConfirmCoreAsync()
    {
        if (Selected is null)
        {
            Error = Strings.Picker_ErrorNothingSelected;
            return Task.FromResult(false);
        }

        return Task.FromResult(true);
    }
}
