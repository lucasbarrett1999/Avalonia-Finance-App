using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Keel.Application.Import;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Desktop.ViewModels.Register;

namespace Keel.Desktop.ViewModels.Import;

/// <summary>A CSV column in the mapping combo boxes; a null index is "(none)".</summary>
public sealed record ColumnOption(int? Index, string Label)
{
    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>One parsed row in the mapping dialog's live preview.</summary>
public sealed record CsvPreviewRow(string Date, string Payee, string Memo, string Amount, bool IsNegative);

/// <summary>
/// The CSV column-mapping dialog (F-TXN-2): prefilled from the account's remembered mapping or
/// from layout detection; date, payee, memo and amount columns (signed, debit/credit, or amount
/// plus type), date format with the ambiguity prompt, sign convention, decimal separator, lines to
/// skip and header; a live preview of the first 10 rows parsed with the current choices.
/// </summary>
public sealed partial class CsvMappingViewModel : DialogViewModel
{
    /// <summary>Rows shown in the live preview.</summary>
    public const int PreviewSize = 10;

    private readonly Func<ImportOptions, Task<ParseResult>> _parse;
    private readonly CsvColumnMapping _initial;
    private readonly string _currency;
    private readonly string _fileName;
    private readonly bool _initializing;
    private int _version;

    /// <summary>Creates the dialog.</summary>
    /// <param name="fileName">File being imported.</param>
    /// <param name="currency">Account currency (preview amounts, parse option).</param>
    /// <param name="layout">The detected layout (headers, date-format candidates, ambiguity).</param>
    /// <param name="initial">The mapping to start from (remembered, else detected).</param>
    /// <param name="isRemembered">Whether <paramref name="initial"/> is the account's remembered mapping.</param>
    /// <param name="parse">Parses the file with the given options (the live preview).</param>
    public CsvMappingViewModel(string fileName, string currency, DetectedCsvLayout layout, CsvColumnMapping initial, bool isRemembered, Func<ImportOptions, Task<ParseResult>> parse)
    {
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(initial);
        _fileName = fileName;
        _currency = currency;
        _initial = initial;
        _parse = parse;
        IsRemembered = isRemembered;
        IsDateAmbiguous = layout.IsDateFormatAmbiguous && !isRemembered;
        Columns = [new ColumnOption(null, Strings.CsvMapping_None), .. layout.Headers.Select((h, i) =>
            new ColumnOption(i, LedgerText.Format(Strings.CsvMapping_Column, i + 1, string.IsNullOrWhiteSpace(h) ? "…" : h.Trim())))];
        AmountLayouts = Enum.GetValues<CsvAmountLayout>().Select(v => new Choice<CsvAmountLayout>(v, Label("CsvLayout_" + v))).ToList();
        SignConventions = Enum.GetValues<CsvSignConvention>().Select(v => new Choice<CsvSignConvention>(v, Label("CsvSign_" + v))).ToList();
        DecimalSeparators = [new Choice<char>('.', Strings.CsvMapping_DecimalPoint), new Choice<char>(',', Strings.CsvMapping_DecimalComma)];
        DateFormats = CsvDateFormats.All.Contains(initial.DateFormat) ? CsvDateFormats.All : [.. CsvDateFormats.All, initial.DateFormat];

        _initializing = true;
        DateColumn = Column(initial.DateColumn);
        PayeeColumn = Column(initial.PayeeColumn);
        MemoColumn = Column(initial.MemoColumn);
        AmountColumn = Column(initial.AmountColumn);
        DebitColumn = Column(initial.DebitColumn);
        CreditColumn = Column(initial.CreditColumn);
        TypeColumn = Column(initial.TypeColumn);
        SelectedAmountLayout = AmountLayouts.First(c => c.Value == initial.AmountLayout);
        SelectedSignConvention = SignConventions.First(c => c.Value == initial.SignConvention);
        SelectedDecimalSeparator = DecimalSeparators.FirstOrDefault(c => c.Value == initial.DecimalSeparator) ?? DecimalSeparators[0];
        SelectedDateFormat = initial.DateFormat;
        SkipRows = initial.SkipRows;
        HasHeader = initial.HasHeader;
        _initializing = false;
        Refreshing = RefreshAsync();
    }

    /// <inheritdoc />
    public override string Title => LedgerText.Format(Strings.CsvMapping_Title, _fileName);

    /// <inheritdoc />
    public override double PreferredMaxWidth => 820;

    /// <summary>Whether the dialog started from the account's remembered mapping.</summary>
    public bool IsRemembered { get; }

    /// <summary>Where the prefilled mapping came from.</summary>
    public string SourceText => IsRemembered ? Strings.CsvMapping_Remembered : Strings.CsvMapping_Detected;

    /// <summary>"(none)" plus every column of the file.</summary>
    public IReadOnlyList<ColumnOption> Columns { get; }

    /// <summary>Amount layouts.</summary>
    public IReadOnlyList<Choice<CsvAmountLayout>> AmountLayouts { get; }

    /// <summary>Sign conventions of a signed amount column.</summary>
    public IReadOnlyList<Choice<CsvSignConvention>> SignConventions { get; }

    /// <summary>Decimal separators.</summary>
    public IReadOnlyList<Choice<char>> DecimalSeparators { get; }

    /// <summary>Date format names.</summary>
    public IReadOnlyList<string> DateFormats { get; }

    /// <summary>Date column.</summary>
    [ObservableProperty]
    public partial ColumnOption? DateColumn { get; set; }

    /// <summary>Payee column.</summary>
    [ObservableProperty]
    public partial ColumnOption? PayeeColumn { get; set; }

    /// <summary>Memo column.</summary>
    [ObservableProperty]
    public partial ColumnOption? MemoColumn { get; set; }

    /// <summary>Signed amount (or amount of amount-plus-type) column.</summary>
    [ObservableProperty]
    public partial ColumnOption? AmountColumn { get; set; }

    /// <summary>Debit column.</summary>
    [ObservableProperty]
    public partial ColumnOption? DebitColumn { get; set; }

    /// <summary>Credit column.</summary>
    [ObservableProperty]
    public partial ColumnOption? CreditColumn { get; set; }

    /// <summary>Type column.</summary>
    [ObservableProperty]
    public partial ColumnOption? TypeColumn { get; set; }

    /// <summary>Amount layout.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSignedAmount), nameof(IsDebitCredit), nameof(IsAmountWithType), nameof(UsesAmountColumn))]
    public partial Choice<CsvAmountLayout> SelectedAmountLayout { get; set; }

    /// <summary>Sign convention (signed layout).</summary>
    [ObservableProperty]
    public partial Choice<CsvSignConvention> SelectedSignConvention { get; set; }

    /// <summary>Decimal separator.</summary>
    [ObservableProperty]
    public partial Choice<char> SelectedDecimalSeparator { get; set; }

    /// <summary>Date format.</summary>
    [ObservableProperty]
    public partial string SelectedDateFormat { get; set; }

    /// <summary>Non-blank lines before the header (or first row).</summary>
    [ObservableProperty]
    public partial decimal? SkipRows { get; set; }

    /// <summary>Whether the first line after the skipped ones is a header.</summary>
    [ObservableProperty]
    public partial bool HasHeader { get; set; }

    /// <summary>Whether numeric dates fit both day orders and the user has not chosen yet.</summary>
    [ObservableProperty]
    public partial bool IsDateAmbiguous { get; private set; }

    /// <summary>One signed amount column.</summary>
    public bool IsSignedAmount => SelectedAmountLayout.Value == CsvAmountLayout.SignedAmount;

    /// <summary>Debit and credit columns.</summary>
    public bool IsDebitCredit => SelectedAmountLayout.Value == CsvAmountLayout.DebitCredit;

    /// <summary>Amount plus type.</summary>
    public bool IsAmountWithType => SelectedAmountLayout.Value == CsvAmountLayout.AmountWithType;

    /// <summary>Whether the amount column applies.</summary>
    public bool UsesAmountColumn => !IsDebitCredit;

    /// <summary>First rows parsed with the current mapping.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPreviewRows))]
    public partial IReadOnlyList<CsvPreviewRow> PreviewRows { get; private set; } = [];

    /// <summary>Whether the preview has rows.</summary>
    public bool HasPreviewRows => PreviewRows.Count > 0;

    /// <summary>"42 rows read", or why none could be read.</summary>
    [ObservableProperty]
    public partial string PreviewMessage { get; private set; } = string.Empty;

    /// <summary>Rows the current mapping reads.</summary>
    [ObservableProperty]
    public partial int ParsedCount { get; private set; }

    /// <summary>The parse with the current mapping (used after confirming).</summary>
    public ParseResult? LastResult { get; private set; }

    /// <summary>The confirmed mapping.</summary>
    public CsvColumnMapping? Result { get; private set; }

    /// <summary>The latest preview refresh (tests await it).</summary>
    public Task Refreshing { get; private set; }

    /// <summary>The mapping the current choices describe, or null when a required column is missing.</summary>
    public CsvColumnMapping? BuildMapping()
    {
        var layout = SelectedAmountLayout.Value;
        if (DateColumn?.Index is not { } date)
        {
            return null;
        }

        var amountOk = layout switch
        {
            CsvAmountLayout.DebitCredit => DebitColumn?.Index is not null || CreditColumn?.Index is not null,
            CsvAmountLayout.AmountWithType => AmountColumn?.Index is not null && TypeColumn?.Index is not null,
            _ => AmountColumn?.Index is not null,
        };
        if (!amountOk)
        {
            return null;
        }

        return _initial with
        {
            DateColumn = date,
            DateFormat = SelectedDateFormat,
            PayeeColumn = PayeeColumn?.Index,
            MemoColumn = MemoColumn?.Index,
            AmountLayout = layout,
            AmountColumn = layout == CsvAmountLayout.DebitCredit ? null : AmountColumn?.Index,
            DebitColumn = layout == CsvAmountLayout.DebitCredit ? DebitColumn?.Index : null,
            CreditColumn = layout == CsvAmountLayout.DebitCredit ? CreditColumn?.Index : null,
            TypeColumn = layout == CsvAmountLayout.AmountWithType ? TypeColumn?.Index : _initial.AmountLayout == CsvAmountLayout.AmountWithType ? null : _initial.TypeColumn,
            SignConvention = SelectedSignConvention.Value,
            DecimalSeparator = SelectedDecimalSeparator.Value,
            SkipRows = (int)Math.Clamp(SkipRows ?? 0, 0, 1000),
            HasHeader = HasHeader,
        };
    }

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        if (IsDateAmbiguous)
        {
            Error = Strings.CsvMapping_ChooseDateOrder;
            return false;
        }

        if (BuildMapping() is not { } mapping)
        {
            Error = DateColumn?.Index is null ? Strings.CsvMapping_ErrorDateColumn : Strings.CsvMapping_ErrorAmountColumns;
            return false;
        }

        await Refreshing;
        if (ParsedCount == 0)
        {
            Error = Strings.CsvMapping_PreviewEmpty;
            return false;
        }

        Result = mapping;
        return true;
    }

    [RelayCommand]
    private void ChooseMonthFirst() => ChooseDateOrder(dayFirst: false);

    [RelayCommand]
    private void ChooseDayFirst() => ChooseDateOrder(dayFirst: true);

    private void ChooseDateOrder(bool dayFirst)
    {
        var twoDigitYear = SelectedDateFormat.EndsWith("yy", StringComparison.Ordinal) && !SelectedDateFormat.EndsWith("yyyy", StringComparison.Ordinal);
        SelectedDateFormat = (dayFirst, twoDigitYear) switch
        {
            (true, true) => "dd/MM/yy",
            (true, false) => "dd/MM/yyyy",
            (false, true) => "MM/dd/yy",
            _ => "MM/dd/yyyy",
        };
        IsDateAmbiguous = false;
        Error = null;
        Refresh();
    }

    partial void OnDateColumnChanged(ColumnOption? value) => Refresh();

    partial void OnPayeeColumnChanged(ColumnOption? value) => Refresh();

    partial void OnMemoColumnChanged(ColumnOption? value) => Refresh();

    partial void OnAmountColumnChanged(ColumnOption? value) => Refresh();

    partial void OnDebitColumnChanged(ColumnOption? value) => Refresh();

    partial void OnCreditColumnChanged(ColumnOption? value) => Refresh();

    partial void OnTypeColumnChanged(ColumnOption? value) => Refresh();

    partial void OnSelectedAmountLayoutChanged(Choice<CsvAmountLayout> value) => Refresh();

    partial void OnSelectedSignConventionChanged(Choice<CsvSignConvention> value) => Refresh();

    partial void OnSelectedDecimalSeparatorChanged(Choice<char> value) => Refresh();

    partial void OnSelectedDateFormatChanged(string value)
    {
        if (!_initializing)
        {
            IsDateAmbiguous = false;
        }

        Refresh();
    }

    partial void OnSkipRowsChanged(decimal? value) => Refresh();

    partial void OnHasHeaderChanged(bool value) => Refresh();

    private static string Label(string key) => Strings.ResourceManager.GetString(key, Strings.Culture) ?? key;

    private ColumnOption Column(int? index) => Columns.FirstOrDefault(c => c.Index == index) ?? Columns[0];

    private void Refresh()
    {
        if (!_initializing)
        {
            Refreshing = RefreshAsync();
        }
    }

    private async Task RefreshAsync()
    {
        var version = ++_version;
        if (BuildMapping() is not { } mapping)
        {
            PreviewRows = [];
            ParsedCount = 0;
            LastResult = null;
            PreviewMessage = DateColumn?.Index is null ? Strings.CsvMapping_ErrorDateColumn : Strings.CsvMapping_ErrorAmountColumns;
            return;
        }

        ParseResult result;
        try
        {
            result = await _parse(ImportOptions.Default with { Currency = _currency, CsvMapping = mapping });
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or FormatException or ArgumentException)
        {
            if (version == _version)
            {
                PreviewRows = [];
                ParsedCount = 0;
                PreviewMessage = ex.Message;
            }

            return;
        }

        if (version != _version)
        {
            return;
        }

        LastResult = result;
        ParsedCount = result.Transactions.Count;
        PreviewRows = result.Transactions.Take(PreviewSize).Select(t => new CsvPreviewRow(
            t.Date.ToString("d", CultureInfo.CurrentCulture),
            t.PayeeRaw,
            t.Memo ?? string.Empty,
            LedgerText.Money(t.Amount, t.Currency),
            t.Amount < 0)).ToList();
        var skipped = result.Warnings.Count(w => w.Code is ImportWarningCode.InvalidDate or ImportWarningCode.InvalidAmount or ImportWarningCode.SkippedRow);
        PreviewMessage = ParsedCount == 0 ? Strings.CsvMapping_PreviewEmpty
            : skipped > 0 ? LedgerText.Format(Strings.CsvMapping_PreviewSkipped, ParsedCount, skipped)
            : LedgerText.Format(Strings.CsvMapping_PreviewCount, ParsedCount);
    }
}
