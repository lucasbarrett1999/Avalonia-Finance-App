using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;
using Keel.Domain.Ledger;

namespace Keel.Desktop.ViewModels.Review;

/// <summary>
/// The review queue's compact split editor (S): split lines reuse the register's
/// <see cref="SplitLineViewModel"/>; saving writes the splits and approves the transaction.
/// </summary>
public sealed partial class ReviewSplitViewModel : DialogViewModel
{
    private readonly ITransactionService _transactions;
    private readonly TransactionDto _transaction;

    /// <summary>Creates the editor with the whole amount on the first line.</summary>
    public ReviewSplitViewModel(ITransactionService transactions, TransactionDto transaction, IReadOnlyList<CategoryOption> categories, string currency)
    {
        ArgumentNullException.ThrowIfNull(transaction);
        _transactions = transactions;
        _transaction = transaction;
        Categories = categories;
        Currency = currency;
        Lines.CollectionChanged += (_, _) => Recalculate();
        if (transaction.Splits.Count > 0)
        {
            foreach (var split in transaction.Splits)
            {
                Lines.Add(Line(split.CategoryId, split.Memo, split.Amount));
            }
        }
        else
        {
            Lines.Add(Line(transaction.CategoryId, null, transaction.Amount));
            Lines.Add(Line(null, null, 0));
        }
    }

    /// <inheritdoc />
    public override string Title => Strings.ReviewSplit_Title;

    /// <summary>Categories.</summary>
    public IReadOnlyList<CategoryOption> Categories { get; }

    /// <summary>Currency.</summary>
    public string Currency { get; }

    /// <summary>"Split $42.50 from Costco".</summary>
    public string Prompt => LedgerText.Format(Strings.ReviewSplit_Prompt, LedgerText.Money(Math.Abs(_transaction.Amount), Currency), _transaction.Payee);

    /// <summary>Lines.</summary>
    public ObservableCollection<SplitLineViewModel> Lines { get; } = [];

    /// <summary>"Remaining: $2.00".</summary>
    [ObservableProperty]
    public partial string RemainingText { get; private set; } = string.Empty;

    /// <summary>Whether the lines add up.</summary>
    [ObservableProperty]
    public partial bool IsBalanced { get; private set; }

    /// <summary>The saved transaction.</summary>
    public TransactionDto? Result { get; private set; }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        var amounts = Lines.Select(l => l.Amount).ToList();
        if (SplitRules.Validate(_transaction.Amount, amounts) is { } problem)
        {
            Error = problem switch
            {
                SplitProblem.TooFewLines => LedgerText.Error(LedgerError.SplitTooFewLines),
                SplitProblem.ZeroLine => LedgerText.Error(LedgerError.SplitZeroLine),
                _ => LedgerText.Error(LedgerError.SplitSumMismatch),
            };
            return false;
        }

        var splits = Lines.Select(l => new SplitLine(l.ResolveCategory()?.Id, l.Memo, l.Amount)).ToList();
        try
        {
            Result = await _transactions.SaveAsync(
                new SaveTransactionRequest(_transaction.Id, _transaction.AccountId, _transaction.Date, _transaction.Amount, _transaction.Payee,
                    null, _transaction.Memo, _transaction.Status, IsApproved: true, Splits: splits),
                CancellationToken.None);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
    }

    [RelayCommand]
    private void AddLine() => Lines.Add(Line(null, null, SplitRules.Remaining(_transaction.Amount, Lines.Select(l => l.Amount))));

    [RelayCommand]
    private void RemoveLine(SplitLineViewModel? line)
    {
        if (line is not null && Lines.Count > 2)
        {
            Lines.Remove(line);
        }
    }

    private SplitLineViewModel Line(Guid? category, string? memo, long amount)
    {
        var line = new SplitLineViewModel(Categories, Recalculate) { Memo = memo };
        line.Category = Categories.FirstOrDefault(c => c.Id == category);
        line.CategoryText = line.Category?.FullName;
        line.SetAmount(amount);
        return line;
    }

    private void Recalculate()
    {
        var remaining = SplitRules.Remaining(_transaction.Amount, Lines.Select(l => l.Amount));
        RemainingText = LedgerText.Format(Strings.Editor_Remaining, LedgerText.Money(remaining, Currency));
        IsBalanced = remaining == 0;
    }
}
