using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;

namespace Keel.Desktop.ViewModels.Import;

/// <summary>An account of a file with several (OFX statements, QIF account blocks).</summary>
/// <param name="AccountId">The file's account id (<see cref="DetectedAccount.AccountId"/>).</param>
/// <param name="Label">Display text.</param>
public sealed record StatementOption(string? AccountId, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>Names the preview needs to show rows.</summary>
/// <param name="Accounts">Account names by id (transfer partners).</param>
/// <param name="Categories">Category names by id.</param>
public sealed record ImportPreviewNames(IReadOnlyDictionary<Guid, string> Accounts, IReadOnlyDictionary<Guid, string> Categories);

/// <summary>
/// The import preview (F-TXN-2): every row with its dedup status (new, duplicate, matched to
/// existing, updated, transfer pair), checkboxes to override, totals, and Import/Cancel. Confirming
/// runs the import with the user's choices and keeps the <see cref="Summary"/>.
/// </summary>
public sealed partial class ImportPreviewViewModel : DialogViewModel
{
    private readonly Func<ImportBatch, Task<ImportSummary>> _import;
    private readonly Func<StatementOption, Task<(ImportBatch Batch, ImportPreview Preview)>>? _switchStatement;
    private readonly ImportPreviewNames _names;
    private readonly string _accountName;
    private readonly string _fileName;
    private readonly string _currency;
    private bool _switching;

    /// <summary>Creates the preview.</summary>
    public ImportPreviewViewModel(
        string fileName,
        string accountName,
        string currency,
        ImportBatch batch,
        ImportPreview preview,
        ImportPreviewNames names,
        Func<ImportBatch, Task<ImportSummary>> import,
        IReadOnlyList<StatementOption>? statements = null,
        Func<StatementOption, Task<(ImportBatch Batch, ImportPreview Preview)>>? switchStatement = null)
    {
        ArgumentNullException.ThrowIfNull(names);
        _fileName = fileName;
        _accountName = accountName;
        _currency = currency;
        _names = names;
        _import = import;
        _switchStatement = switchStatement;
        Statements = statements ?? [];
        _switching = true;
        SelectedStatement = Statements.FirstOrDefault();
        _switching = false;
        Load(batch, preview);
    }

    /// <inheritdoc />
    public override string Title => LedgerText.Format(Strings.ImportPreview_Title, _fileName, _accountName);

    /// <inheritdoc />
    public override double PreferredMaxWidth => 1100;

    /// <summary>The batch being previewed.</summary>
    public ImportBatch Batch { get; private set; } = null!;

    /// <summary>Every row.</summary>
    public ObservableCollection<ImportPreviewRowViewModel> Rows { get; } = [];

    /// <summary>File accounts to choose from (empty for single-account files).</summary>
    public IReadOnlyList<StatementOption> Statements { get; }

    /// <summary>Whether the file has several accounts.</summary>
    public bool HasStatements => Statements.Count > 1;

    /// <summary>The chosen file account.</summary>
    [ObservableProperty]
    public partial StatementOption? SelectedStatement { get; set; }

    /// <summary>Warnings from the file and the pipeline, one line per kind.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasWarnings))]
    public partial IReadOnlyList<string> Warnings { get; private set; } = [];

    /// <summary>Whether there are warnings.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>The reported balance line, or null.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasReportedBalance))]
    public partial string? ReportedBalanceText { get; private set; }

    /// <summary>Whether the file reports a balance.</summary>
    public bool HasReportedBalance => ReportedBalanceText is not null;

    /// <summary>"12 of 15 rows will be imported: ...".</summary>
    [ObservableProperty]
    public partial string TotalsText { get; private set; } = string.Empty;

    /// <summary>Net amount of the new rows.</summary>
    [ObservableProperty]
    public partial string NetText { get; private set; } = string.Empty;

    /// <summary>"Import 12".</summary>
    [ObservableProperty]
    public partial string ImportButtonText { get; private set; } = string.Empty;

    /// <summary>Rows that will be written.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsEmpty))]
    public partial int IncludedCount { get; private set; }

    /// <summary>Whether the file has no rows for the chosen account.</summary>
    public bool IsEmpty => Rows.Count == 0;

    /// <summary>The import's result after confirming.</summary>
    public ImportSummary? Summary { get; private set; }

    /// <summary>The user's choices as batch overrides (every row, so previews and imports agree).</summary>
    public IReadOnlyDictionary<int, ImportRowOverride> Overrides() =>
        Rows.ToDictionary(r => r.Index, r => new ImportRowOverride(r.Include, r.PairTransfer));

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        try
        {
            Summary = await _import(Batch with { Overrides = Overrides() });
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
        catch (InvalidOperationException ex)
        {
            Error = LedgerText.Format(Strings.Import_Failed, ex.Message);
            return false;
        }
    }

    /// <summary>Recomputes totals after a checkbox changed.</summary>
    internal void UpdateTotals()
    {
        var included = Rows.Where(r => r.Include).ToList();
        var inserted = included.Where(r => r.Outcome is DedupOutcome.Insert or DedupOutcome.DuplicateSkipped).ToList();
        IncludedCount = included.Count;
        TotalsText = LedgerText.Format(
            Strings.ImportPreview_Totals,
            included.Count,
            Rows.Count,
            inserted.Count,
            included.Count(r => r.Outcome == DedupOutcome.MatchedToExisting),
            included.Count(r => r.Outcome is DedupOutcome.UpdateInPlace or DedupOutcome.PendingToPosted),
            inserted.Count(r => r.IsPairedTransfer),
            Rows.Count(r => r.Outcome == DedupOutcome.DuplicateSkipped && !r.Include));
        NetText = LedgerText.Format(Strings.ImportPreview_Net, LedgerText.Money(inserted.Sum(r => r.Amount), _currency));
        ImportButtonText = LedgerText.Format(Strings.ImportPreview_Import, included.Count);
    }

    partial void OnSelectedStatementChanged(StatementOption? value)
    {
        if (_switching || value is null || _switchStatement is null)
        {
            return;
        }

        _ = SwitchAsync(value);
    }

    private async Task SwitchAsync(StatementOption statement)
    {
        IsBusy = true;
        try
        {
            var (batch, preview) = await _switchStatement!(statement);
            Load(batch, preview);
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Load(ImportBatch batch, ImportPreview preview)
    {
        Batch = batch;
        Rows.Clear();
        foreach (var row in preview.Rows)
        {
            Rows.Add(new ImportPreviewRowViewModel(this, row, _names, _currency));
        }

        Warnings = preview.Warnings
            .GroupBy(w => w.Code)
            .Select(g => LedgerText.Format(Strings.ImportWarning_WithCount,
                Strings.ResourceManager.GetString("ImportWarning_" + g.Key, Strings.Culture) ?? g.Key.ToString(), g.Count()))
            .ToList();
        ReportedBalanceText = preview.ReportedBalance is { } balance
            ? LedgerText.Format(Strings.ImportPreview_ReportedBalance, LedgerText.Money(balance.Balance, _currency), balance.Date.ToString("d", CultureInfo.CurrentCulture))
            : null;
        OnPropertyChanged(nameof(IsEmpty));
        UpdateTotals();
    }
}

/// <summary>One row of the import preview.</summary>
public sealed partial class ImportPreviewRowViewModel : ObservableObject
{
    private readonly ImportPreviewViewModel _owner;
    private readonly bool _ready;

    /// <summary>Creates the row.</summary>
    public ImportPreviewRowViewModel(ImportPreviewViewModel owner, ImportPreviewRow row, ImportPreviewNames names, string currency)
    {
        ArgumentNullException.ThrowIfNull(row);
        ArgumentNullException.ThrowIfNull(names);
        _owner = owner;
        Index = row.Index;
        Outcome = row.Outcome;
        Amount = row.Incoming.Amount;
        CanOverride = row.CanOverride;
        HasTransfer = row.HasTransferPartner && row.Outcome is DedupOutcome.Insert or DedupOutcome.DuplicateSkipped;
        IsAmbiguousTransfer = row.IsTransferAmbiguous;
        Date = row.Incoming.Date.ToString("d", CultureInfo.CurrentCulture);
        Payee = string.IsNullOrEmpty(row.PayeeName) ? row.Incoming.PayeeRaw : row.PayeeName;
        RawPayee = row.Incoming.PayeeRaw;
        Memo = row.Incoming.Memo ?? string.Empty;
        AmountText = LedgerText.Money(Amount, currency);
        Category = row.CategoryId is { } c && names.Categories.TryGetValue(c, out var category) ? category : string.Empty;
        var partner = row.TransferAccountId is { } a && names.Accounts.TryGetValue(a, out var name) ? name : string.Empty;
        TransferText = HasTransfer
            ? LedgerText.Format(row.IsTransferAmbiguous ? Strings.ImportPreview_TransferAmbiguous : Strings.ImportPreview_TransferTo, partner)
            : string.Empty;
        IncludeName = LedgerText.Format(Strings.ImportPreview_IncludeRow, Index + 1);
        PairName = LedgerText.Format(Strings.ImportPreview_PairRow, Index + 1);
        Include = row.IncludeByDefault;
        PairTransfer = HasTransfer && row.PairTransferByDefault;
        _ready = true;
    }

    /// <summary>Position in the batch.</summary>
    public int Index { get; }

    /// <summary>Dedup outcome.</summary>
    public DedupOutcome Outcome { get; }

    /// <summary>Amount in minor units.</summary>
    public long Amount { get; }

    /// <summary>Whether the checkbox can change.</summary>
    public bool CanOverride { get; }

    /// <summary>Whether a transfer partner was found.</summary>
    public bool HasTransfer { get; }

    /// <summary>Whether another partner was equally close.</summary>
    public bool IsAmbiguousTransfer { get; }

    /// <summary>Date text.</summary>
    public string Date { get; }

    /// <summary>Payee the row gets (or the raw text).</summary>
    public string Payee { get; }

    /// <summary>The payee exactly as the file has it (tooltip).</summary>
    public string RawPayee { get; }

    /// <summary>Memo.</summary>
    public string Memo { get; }

    /// <summary>Amount text.</summary>
    public string AmountText { get; }

    /// <summary>Whether the amount is an outflow.</summary>
    public bool IsNegative => Amount < 0;

    /// <summary>Category the row gets, if any.</summary>
    public string Category { get; }

    /// <summary>"Transfer: Savings".</summary>
    public string TransferText { get; }

    /// <summary>Screen-reader name of the import checkbox.</summary>
    public string IncludeName { get; }

    /// <summary>Screen-reader name of the transfer checkbox.</summary>
    public string PairName { get; }

    /// <summary>Whether the row is written.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsPairedTransfer), nameof(CanPair))]
    public partial bool Include { get; set; }

    /// <summary>Whether the detected transfer is paired.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusText), nameof(IsPairedTransfer))]
    public partial bool PairTransfer { get; set; }

    /// <summary>Whether the transfer checkbox applies (an included new row with a partner).</summary>
    public bool CanPair => HasTransfer && Include;

    /// <summary>Whether the row is inserted as one side of a transfer.</summary>
    public bool IsPairedTransfer => HasTransfer && Include && PairTransfer;

    /// <summary>Status column text.</summary>
    public string StatusText => IsPairedTransfer
        ? Strings.ImportStatus_Transfer
        : Strings.ResourceManager.GetString("ImportStatus_" + Outcome, Strings.Culture) ?? Outcome.ToString();

    partial void OnIncludeChanged(bool value)
    {
        if (_ready)
        {
            _owner.UpdateTotals();
        }
    }

    partial void OnPairTransferChanged(bool value)
    {
        if (_ready)
        {
            _owner.UpdateTotals();
        }
    }
}
