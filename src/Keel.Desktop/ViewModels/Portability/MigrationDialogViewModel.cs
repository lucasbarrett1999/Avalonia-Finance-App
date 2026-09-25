using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Keel.Application.Accounts;
using Keel.Application.Import;
using Keel.Application.Ledger;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Desktop.ViewModels.Dialogs;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Portability;

/// <summary>Where one source account of a migration goes.</summary>
/// <param name="AccountId">An existing Keel account, or null.</param>
/// <param name="IsNew">Create a new on-budget account.</param>
/// <param name="Label">Display text.</param>
public sealed record MigrationTarget(Guid? AccountId, bool IsNew, string Label)
{
    /// <summary>Whether the account is left out.</summary>
    public bool IsSkip => AccountId is null && !IsNew;

    /// <inheritdoc />
    public override string ToString() => Label;
}

/// <summary>
/// The migration preview (PRD 9.10, ADR 0099): each account of the YNAB or Monarch export with its rows and
/// where it goes (a new on-budget account with a type, an existing account, or left out), the categories and
/// tags that will be created, and a live dry run of the import. Confirming runs the migration as one undoable
/// action.
/// </summary>
public sealed partial class MigrationDialogViewModel : DialogViewModel
{
    private readonly IMigrationImportService _service;
    private readonly ParseResult _parsed;
    private readonly string _currency;
    private readonly string _fileName;
    private int _previewVersion;

    /// <summary>Creates the dialog.</summary>
    public MigrationDialogViewModel(string fileName, ParseResult parsed, MigrationPlan plan, IReadOnlyList<AccountDto> accounts, string currency, IMigrationImportService service)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(accounts);
        _fileName = fileName;
        _parsed = parsed;
        _currency = currency;
        _service = service;
        Plan = plan;
        var open = accounts.Where(a => !a.IsClosed).ToList();
        foreach (var account in plan.Accounts)
        {
            Accounts.Add(new MigrationAccountRowViewModel(this, account, open, currency));
        }

        NewCategoriesText = plan.NewCategories.Count == 0
            ? Strings.Ynab_NoNewCategories
            : LedgerText.Format(Strings.Ynab_NewCategories, plan.NewCategories.Count, plan.NewCategories.Select(c => c.Group).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
                string.Join(", ", plan.NewCategories.Take(12).Select(c => $"{c.Group}: {c.Name}")) + (plan.NewCategories.Count > 12 ? " …" : string.Empty));
        NewTagsText = plan.NewTags.Count == 0 ? string.Empty : LedgerText.Format(Strings.Ynab_NewTags, string.Join(", ", plan.NewTags));
        Warnings = plan.Warnings.GroupBy(w => w.Code)
            .Select(g => LedgerText.Format(Strings.ImportWarning_WithCount, Strings.ResourceManager.GetString("ImportWarning_" + g.Key, Strings.Culture) ?? g.Key.ToString(), g.Count()))
            .ToList();
        _ = RefreshPreviewAsync();
    }

    /// <inheritdoc />
    public override string Title => LedgerText.Format(Strings.Ynab_Title, PortabilityText.SourceName(Plan.Format), _fileName);

    /// <inheritdoc />
    public override double PreferredMaxWidth => 860;

    /// <summary>The plan the dialog shows.</summary>
    public MigrationPlan Plan { get; }

    /// <summary>One row per source account.</summary>
    public ObservableCollection<MigrationAccountRowViewModel> Accounts { get; } = [];

    /// <summary>"Creates 5 categories in 3 groups: …".</summary>
    public string NewCategoriesText { get; }

    /// <summary>"Creates tags: …" (empty when none).</summary>
    public string NewTagsText { get; }

    /// <summary>Whether tags are created.</summary>
    public bool HasNewTags => NewTagsText.Length > 0;

    /// <summary>Parse warnings, one line per kind.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>Whether there are warnings.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>"13 new transactions, 0 already in Keel, 2 transfers, 3 accounts to create".</summary>
    [ObservableProperty]
    public partial string PreviewText { get; private set; } = Strings.Ynab_PreviewRunning;

    /// <summary>Whether the dry run is running.</summary>
    [ObservableProperty]
    public partial bool IsPreviewing { get; private set; }

    /// <summary>The latest dry run (tests await it).</summary>
    public Task Previewing { get; private set; } = Task.CompletedTask;

    /// <summary>The import's result after confirming.</summary>
    public MigrationSummary? Summary { get; private set; }

    /// <summary>The request for the current choices.</summary>
    public MigrationRequest Request() => new(_parsed, _currency, Accounts.Select(a => a.Choice()).ToList());

    /// <summary>Runs the dry run again for the current choices (after a row changed).</summary>
    internal Task RefreshPreviewAsync() => Previewing = PreviewCoreAsync();

    /// <inheritdoc />
    protected override async Task<bool> ConfirmCoreAsync()
    {
        if (Accounts.All(a => a.Target?.IsSkip != false))
        {
            Error = Strings.Ynab_NothingChosen;
            return false;
        }

        try
        {
            Summary = await _service.ImportAsync(Request(), CancellationToken.None);
            return true;
        }
        catch (LedgerValidationException ex)
        {
            Error = LedgerText.Error(ex.Error);
            return false;
        }
        catch (Exception ex) when (ex is InvalidOperationException or System.Data.Common.DbException)
        {
            Error = LedgerText.Format(Strings.Import_Failed, ex.Message);
            return false;
        }
    }

    private async Task PreviewCoreAsync()
    {
        var version = ++_previewVersion;
        IsPreviewing = true;
        try
        {
            var preview = await _service.PreviewAsync(Request(), CancellationToken.None);
            if (version == _previewVersion)
            {
                PreviewText = PortabilityText.MigrationPreview(preview);
                foreach (var row in Accounts)
                {
                    row.Preview = preview.Accounts.FirstOrDefault(a => a.SourceAccount == row.SourceAccount);
                }
            }
        }
        catch (LedgerValidationException ex)
        {
            if (version == _previewVersion)
            {
                PreviewText = LedgerText.Error(ex.Error);
            }
        }
        finally
        {
            if (version == _previewVersion)
            {
                IsPreviewing = false;
            }
        }
    }
}

/// <summary>One source account in the migration preview.</summary>
public sealed partial class MigrationAccountRowViewModel : ObservableObject
{
    private static readonly AccountType[] NewTypes = [AccountType.Checking, AccountType.Savings, AccountType.Cash, AccountType.CreditCard, AccountType.LineOfCredit];
    private readonly MigrationDialogViewModel _owner;
    private readonly string _currency;
    private readonly bool _ready;

    /// <summary>Creates the row: the account with the same name when there is one, else a new account of the suggested type.</summary>
    public MigrationAccountRowViewModel(MigrationDialogViewModel owner, MigrationAccountPlan plan, IReadOnlyList<AccountDto> open, string currency)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _owner = owner;
        _currency = currency;
        SourceAccount = plan.SourceAccount;
        DetailText = LedgerText.Format(Strings.Ynab_AccountDetail, plan.RowCount, plan.From.ToString("d", CultureInfo.CurrentCulture), plan.To.ToString("d", CultureInfo.CurrentCulture));
        NetText = LedgerText.Money(plan.Net, currency);
        Targets =
        [
            new MigrationTarget(null, true, LedgerText.Format(Strings.Ynab_TargetNew, plan.SourceAccount)),
            .. open.Select(a => new MigrationTarget(a.Id, false, LedgerText.Format(Strings.Ynab_TargetExisting, a.Name))),
            new MigrationTarget(null, false, Strings.Ynab_TargetSkip),
        ];
        TypeNames = NewTypes.Select(LedgerText.AccountType).ToList();
        Target = plan.MatchedAccountId is { } matched ? Targets.First(t => t.AccountId == matched) : Targets[0];
        TypeIndex = Math.Max(0, Array.IndexOf(NewTypes, plan.SuggestedType));
        TargetName = LedgerText.Format(Strings.Ynab_TargetFor, plan.SourceAccount);
        TypeName = LedgerText.Format(Strings.Ynab_TypeFor, plan.SourceAccount);
        _ready = true;
    }

    /// <summary>The account name in the file.</summary>
    public string SourceAccount { get; }

    /// <summary>"8 transactions, 1/2/2026 – 1/18/2026".</summary>
    public string DetailText { get; }

    /// <summary>Net of the rows.</summary>
    public string NetText { get; }

    /// <summary>New, existing accounts, skip.</summary>
    public IReadOnlyList<MigrationTarget> Targets { get; }

    /// <summary>Names of the on-budget types a new account can have.</summary>
    public IReadOnlyList<string> TypeNames { get; }

    /// <summary>Where the account goes.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsNew))]
    public partial MigrationTarget? Target { get; set; }

    /// <summary>Index into <see cref="TypeNames"/> for a new account.</summary>
    [ObservableProperty]
    public partial int TypeIndex { get; set; }

    /// <summary>Whether a new account is created (the type box shows).</summary>
    public bool IsNew => Target?.IsNew == true;

    /// <summary>Screen-reader name of the target box.</summary>
    public string TargetName { get; }

    /// <summary>Screen-reader name of the type box.</summary>
    public string TypeName { get; }

    /// <summary>The dry run's result for this account, or null when left out.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ResultText))]
    public partial MigrationAccountResult? Preview { get; set; }

    /// <summary>"8 new · 0 already imported".</summary>
    public string ResultText => Preview is { } p
        ? LedgerText.Format(Strings.Ynab_AccountResult, p.Summary.Added, p.Summary.DuplicatesSkipped + p.Summary.MatchedToExisting)
        : Strings.Ynab_AccountSkipped;

    /// <summary>The service's choice for this row.</summary>
    public MigrationAccountChoice Choice() => Target switch
    {
        { IsNew: true } => MigrationAccountChoice.Create(SourceAccount, SourceAccount, NewTypes[Math.Clamp(TypeIndex, 0, NewTypes.Length - 1)]),
        { AccountId: { } id } => MigrationAccountChoice.Into(SourceAccount, id),
        _ => MigrationAccountChoice.Skipped(SourceAccount),
    };

    partial void OnTargetChanged(MigrationTarget? value)
    {
        if (_ready)
        {
            _ = _owner.RefreshPreviewAsync();
        }
    }

    partial void OnTypeIndexChanged(int value)
    {
        if (_ready)
        {
            _ = _owner.RefreshPreviewAsync();
        }
    }
}
