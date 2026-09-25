using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Register;

/// <summary>
/// One row of the virtualized register. A row starts as a placeholder at a fixed index and is
/// filled in place when its page arrives, so the grid never needs the whole list.
/// </summary>
public sealed partial class RegisterRowViewModel : ObservableObject
{
    internal RegisterRowViewModel(RegisterSource owner, int index)
    {
        Owner = owner;
        Index = index;
    }

    /// <summary>Position in the register (for the virtual list).</summary>
    public int Index { get; }

    internal RegisterSource Owner { get; }

    /// <summary>The loaded data, or null while the page is loading.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLoaded), nameof(Id), nameof(Date), nameof(DateText), nameof(AccountName), nameof(Payee), nameof(IsTransfer),
        nameof(Category), nameof(IsUncategorized), nameof(Memo), nameof(Outflow), nameof(Inflow), nameof(Amount), nameof(Status), nameof(IsCleared),
        nameof(IsReconciled), nameof(IsUncleared), nameof(StatusName), nameof(IsUnapproved), nameof(RunningBalance), nameof(IsNegativeBalance),
        nameof(IsSplit), nameof(Splits), nameof(AutomationName), nameof(Tags), nameof(HasTags), nameof(AttachmentCount), nameof(HasAttachments),
        nameof(AttachmentText), nameof(AttachmentTip))]
    public partial RegisterRow? Row { get; private set; }

    /// <summary>Whether split lines are shown under the row.</summary>
    [ObservableProperty]
    public partial bool IsExpanded { get; set; }

    /// <summary>Whether the page holding this row has loaded.</summary>
    public bool IsLoaded => Row is not null;

    /// <summary>Transaction id (empty while loading).</summary>
    public Guid Id => Row?.Id ?? Guid.Empty;

    /// <summary>Date.</summary>
    public DateOnly Date => Row?.Date ?? default;

    /// <summary>Short date in the current culture.</summary>
    public string DateText => Row is { } r ? r.Date.ToString("d", CultureInfo.CurrentCulture) : string.Empty;

    /// <summary>Account name (All Accounts register).</summary>
    public string AccountName => Row?.AccountName ?? string.Empty;

    /// <summary>Payee, or "Transfer: Account" for transfers.</summary>
    public string Payee => Row switch
    {
        null => string.Empty,
        { TransferAccountName: { } other } => LedgerText.TransferPayee(other),
        { } r => r.Payee,
    };

    /// <summary>Whether the row is one side of a transfer.</summary>
    public bool IsTransfer => Row?.IsTransfer ?? false;

    /// <summary>Category, "Split (n)" for splits, empty for uncategorized transfers.</summary>
    public string Category => Row switch
    {
        null => string.Empty,
        { IsSplit: true } r => LedgerText.Format(Strings.Register_SplitCategory, r.Splits.Count),
        { CategoryName: { } name } => name,
        { IsTransfer: true } => string.Empty,
        _ => Strings.Register_Uncategorized,
    };

    /// <summary>Whether a non-transfer, unsplit row has no category.</summary>
    public bool IsUncategorized => Row is { CategoryId: null, IsTransfer: false, IsSplit: false };

    /// <summary>Memo.</summary>
    public string Memo => Row?.Memo ?? string.Empty;

    /// <summary>Signed amount in minor units.</summary>
    public long Amount => Row?.Amount ?? 0;

    /// <summary>Outflow as a positive currency string, or empty.</summary>
    public string Outflow => Row is { Amount: < 0 } r ? LedgerText.Money(-r.Amount, r.Currency) : string.Empty;

    /// <summary>Inflow as a currency string, or empty.</summary>
    public string Inflow => Row is { Amount: > 0 } r ? LedgerText.Money(r.Amount, r.Currency) : string.Empty;

    /// <summary>Cleared status.</summary>
    public TransactionStatus Status => Row?.Status ?? TransactionStatus.Uncleared;

    /// <summary>Cleared (not reconciled).</summary>
    public bool IsCleared => Row?.Status == TransactionStatus.Cleared;

    /// <summary>Reconciled (locked).</summary>
    public bool IsReconciled => Row?.Status == TransactionStatus.Reconciled;

    /// <summary>Uncleared.</summary>
    public bool IsUncleared => Row is { Status: TransactionStatus.Uncleared };

    /// <summary>Status name for tooltips and screen readers.</summary>
    public string StatusName => Row is { } r ? LedgerText.Status(r.Status) : string.Empty;

    /// <summary>Unapproved rows get a left accent bar.</summary>
    public bool IsUnapproved => Row is { IsApproved: false };

    /// <summary>Running balance as currency.</summary>
    public string RunningBalance => Row is { } r ? LedgerText.Money(r.RunningBalance, r.Currency) : string.Empty;

    /// <summary>Whether the running balance is negative.</summary>
    public bool IsNegativeBalance => Row is { RunningBalance: < 0 };

    /// <summary>Whether the row is split.</summary>
    public bool IsSplit => Row?.IsSplit ?? false;

    /// <summary>Split lines for the expanded details.</summary>
    public IReadOnlyList<SplitRowViewModel> Splits => Row is { } r
        ? r.Splits.Select(s => new SplitRowViewModel(s.CategoryName ?? Strings.Register_Uncategorized, s.Memo ?? string.Empty,
            s.Amount < 0 ? LedgerText.Money(-s.Amount, r.Currency) : string.Empty,
            s.Amount > 0 ? LedgerText.Money(s.Amount, r.Currency) : string.Empty)).ToList()
        : [];

    /// <summary>Tag names (F-TXN-8), shown as chips.</summary>
    public IReadOnlyList<string> Tags => Row?.Tags ?? [];

    /// <summary>Whether the row has tags.</summary>
    public bool HasTags => Tags.Count > 0;

    /// <summary>Number of attached files (F-TXN-8).</summary>
    public int AttachmentCount => Row?.AttachmentCount ?? 0;

    /// <summary>Whether files are attached.</summary>
    public bool HasAttachments => AttachmentCount > 0;

    /// <summary>The count next to the paperclip.</summary>
    public string AttachmentText => AttachmentCount.ToString(CultureInfo.CurrentCulture);

    /// <summary>"2 attachments".</summary>
    public string AttachmentTip => LedgerText.Format(Strings.Attachment_Count, AttachmentCount.ToString(CultureInfo.CurrentCulture));

    /// <summary>Screen-reader summary of the row.</summary>
    public string AutomationName => Row is { } r
        ? LedgerText.Format(Strings.Register_RowAutomation, DateText, Payee, Category, LedgerText.Money(r.Amount, r.Currency), StatusName)
            + (HasTags ? ", " + LedgerText.Format(Strings.Tag_RowAutomation, string.Join(", ", Tags)) : string.Empty)
            + (HasAttachments ? ", " + AttachmentTip : string.Empty)
        : Strings.Register_Loading;

    internal void Fill(RegisterRow row) => Row = row;
}

/// <summary>A split line under an expanded register row.</summary>
public sealed record SplitRowViewModel(string Category, string Memo, string Outflow, string Inflow);
