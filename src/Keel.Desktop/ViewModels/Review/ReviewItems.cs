using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Categorization;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;

namespace Keel.Desktop.ViewModels.Review;

/// <summary>One unapproved transaction in the review queue.</summary>
public sealed partial class ReviewItemViewModel : ObservableObject
{
    /// <summary>Creates the item.</summary>
    public ReviewItemViewModel(RegisterRow row, int index)
    {
        ArgumentNullException.ThrowIfNull(row);
        Row = row;
        Index = index;
    }

    /// <summary>The register row.</summary>
    public RegisterRow Row { get; }

    /// <summary>Position in the whole queue (0-based).</summary>
    public int Index { get; }

    /// <summary>Id.</summary>
    public Guid Id => Row.Id;

    /// <summary>Date.</summary>
    public string DateText => Row.Date.ToString("d", CultureInfo.CurrentCulture);

    /// <summary>Long date for the detail panel.</summary>
    public string LongDateText => Row.Date.ToString("D", CultureInfo.CurrentCulture);

    /// <summary>Payee, or "Transfer: account".</summary>
    public string Payee => Row.IsTransfer ? LedgerText.TransferPayee(Row.TransferAccountName ?? string.Empty)
        : string.IsNullOrWhiteSpace(Row.Payee) ? Strings.Review_NoPayee : Row.Payee;

    /// <summary>Account name.</summary>
    public string Account => Row.AccountName;

    /// <summary>Signed amount.</summary>
    public string AmountText => LedgerText.Money(Row.Amount, Row.Currency);

    /// <summary>Whether it is an inflow.</summary>
    public bool IsInflow => Row.Amount > 0;

    /// <summary>"Outflow" / "Inflow".</summary>
    public string DirectionText => Row.Amount >= 0 ? Strings.Review_Inflow : Strings.Review_Outflow;

    /// <summary>Memo.</summary>
    public string? Memo => Row.Memo;

    /// <summary>Whether a memo exists.</summary>
    public bool HasMemo => !string.IsNullOrWhiteSpace(Row.Memo);

    /// <summary>Current category text.</summary>
    public string CategoryText => Row.IsSplit ? LedgerText.Format(Strings.Review_SplitInto, Row.Splits.Count)
        : Row.IsTransfer && Row.CategoryName is null ? Strings.Review_TransferNoCategory
        : Row.CategoryName ?? Strings.Review_Uncategorized;

    /// <summary>Whether there is no category (and it needs one).</summary>
    public bool IsUncategorized => Row.CategoryId is null && !Row.IsSplit && !Row.IsTransfer;

    /// <summary>Whether it is split.</summary>
    public bool IsSplit => Row.IsSplit;

    /// <summary>Whether it is a transfer.</summary>
    public bool IsTransfer => Row.IsTransfer;

    /// <summary>Whether this item has the review focus.</summary>
    [ObservableProperty]
    public partial bool IsFocused { get; set; }
}

/// <summary>A suggested category (keys 1–9).</summary>
/// <param name="Number">1-based key.</param>
/// <param name="Suggestion">The engine's suggestion.</param>
public sealed record SuggestionViewModel(int Number, CategorizationSuggestion Suggestion)
{
    /// <summary>Approves with this suggestion (the review screen's command).</summary>
    public System.Windows.Input.ICommand? Choose { get; init; }

    /// <summary>Key label.</summary>
    public string Key => Number.ToString(CultureInfo.CurrentCulture);

    /// <summary>Category.</summary>
    public string CategoryName => Suggestion.CategoryName;

    /// <summary>"92%".</summary>
    public string ConfidenceText => Suggestion.Confidence.ToString("0%", CultureInfo.CurrentCulture);

    /// <summary>Where it came from.</summary>
    public string SourceText => Strings.ResourceManager.GetString("Review_Source_" + Suggestion.Source, Strings.Culture) ?? Suggestion.Source.ToString();

    /// <summary>The explanation string (principle 7).</summary>
    public string Explanation => Suggestion.Explanation;

    /// <summary>Whether it is the one "A" writes.</summary>
    public bool IsPrimary => Suggestion.IsPrimary;

    /// <summary>Confidence as 0–100 for the meter.</summary>
    public double ConfidencePercent => Math.Round(Suggestion.Confidence * 100, 1);

    /// <summary>Accessible name.</summary>
    public string AutomationName => LedgerText.Format(Strings.Review_SuggestionAutomation, Number, CategoryName, ConfidenceText);
}

/// <summary>One stage of the "Why?" trace.</summary>
/// <param name="Stage">Localized stage name.</param>
/// <param name="Detail">English detail from the engine.</param>
public sealed record TraceStepViewModel(string Stage, string Detail);

/// <summary>The suggestions panel's state.</summary>
public enum SuggestionState
{
    /// <summary>Nothing focused.</summary>
    None,

    /// <summary>The learner is loading or training ("Preparing suggestions").</summary>
    Preparing,

    /// <summary>Scoring the focused transaction.</summary>
    Loading,

    /// <summary>Suggestions shown (possibly none).</summary>
    Ready,

    /// <summary>Scoring failed.</summary>
    Error,
}
