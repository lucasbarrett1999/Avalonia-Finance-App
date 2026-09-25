using Keel.Domain;

namespace Keel.Application.Import;

/// <summary>
/// Moving to Keel from another budgeting app (PRD 9.10 step 1, P1): a YNAB register export or a Monarch
/// transactions export (every account in one file, with categories, cleared state and tags), and a YNAB
/// budget export (assigned amounts per category and month). Transactions go through the unified import
/// pipeline (<see cref="IImportService"/>: dedup, payees, rules and learner, transfers) one source account
/// at a time, all in one audited, undoable action; missing accounts, category groups, categories and tags
/// are created in the same action (ADR 0099).
/// </summary>
public interface IMigrationImportService
{
    /// <summary>
    /// Describes a parsed export: its accounts with row counts and the Keel account each one matches by name
    /// (or a suggested type for a new one), and the categories and tags the import would create. Reads only.
    /// </summary>
    Task<MigrationPlan> PlanAsync(ParseResult parsed, CancellationToken ct);

    /// <summary>A dry run of <see cref="ImportAsync"/> that is rolled back: exactly what the import would do.</summary>
    Task<MigrationSummary> PreviewAsync(MigrationRequest request, CancellationToken ct);

    /// <summary>Imports the chosen accounts in one database transaction: one undo entry, one <c>LedgerChanged</c>.</summary>
    Task<MigrationSummary> ImportAsync(MigrationRequest request, CancellationToken ct);

    /// <summary>Describes a budget export: months, categories to create, and assignments that would change.</summary>
    Task<BudgetImportSummary> PreviewBudgetAsync(ParseResult parsed, CancellationToken ct);

    /// <summary>Writes a budget export's assigned amounts (creating missing categories) as one undoable action.</summary>
    Task<BudgetImportSummary> ImportBudgetAsync(ParseResult parsed, CancellationToken ct);
}

/// <summary>What a migration export contains, before the user chooses where each account goes.</summary>
/// <param name="Format">YNAB or Monarch.</param>
/// <param name="Accounts">The file's accounts, in file order.</param>
/// <param name="NewCategories">Categories the file uses that the budget file does not have yet.</param>
/// <param name="NewTags">Tags the file uses that the budget file does not have yet.</param>
public sealed record MigrationPlan(
    ImportFileFormat Format,
    IReadOnlyList<MigrationAccountPlan> Accounts,
    IReadOnlyList<MigrationCategoryName> NewCategories,
    IReadOnlyList<string> NewTags)
{
    /// <summary>Parse warnings.</summary>
    public IReadOnlyList<ImportWarning> Warnings { get; init; } = [];

    /// <summary>Rows in the file.</summary>
    public int RowCount => Accounts.Sum(a => a.RowCount);
}

/// <summary>One account of a migration export.</summary>
/// <param name="SourceAccount">The account name as the file spells it.</param>
/// <param name="RowCount">Transactions of the account in the file.</param>
/// <param name="From">Oldest date.</param>
/// <param name="To">Newest date.</param>
/// <param name="Net">Sum of the amounts in minor units.</param>
/// <param name="MatchedAccountId">An open Keel account with the same name, if any.</param>
/// <param name="SuggestedType">The type suggested for a new account (from the name).</param>
public sealed record MigrationAccountPlan(string SourceAccount, int RowCount, DateOnly From, DateOnly To, long Net, Guid? MatchedAccountId, AccountType SuggestedType);

/// <summary>A category named by an export.</summary>
/// <param name="Group">Group name.</param>
/// <param name="Name">Category name.</param>
public sealed record MigrationCategoryName(string Group, string Name);

/// <summary>Where one source account goes.</summary>
/// <param name="SourceAccount">The file's account name.</param>
/// <param name="ExistingAccountId">Import into this Keel account.</param>
/// <param name="NewAccountName">Create an on-budget account with this name (when <paramref name="ExistingAccountId"/> is null).</param>
/// <param name="NewAccountType">Type of the new account (on-budget types only).</param>
public sealed record MigrationAccountChoice(string SourceAccount, Guid? ExistingAccountId, string? NewAccountName, AccountType NewAccountType = AccountType.Checking)
{
    /// <summary>Whether the account is left out.</summary>
    public bool Skip => ExistingAccountId is null && string.IsNullOrWhiteSpace(NewAccountName);

    /// <summary>Imports into an existing account.</summary>
    public static MigrationAccountChoice Into(string sourceAccount, Guid accountId) => new(sourceAccount, accountId, null);

    /// <summary>Creates a new on-budget account.</summary>
    public static MigrationAccountChoice Create(string sourceAccount, string name, AccountType type) => new(sourceAccount, null, name, type);

    /// <summary>Leaves the account out.</summary>
    public static MigrationAccountChoice Skipped(string sourceAccount) => new(sourceAccount, null, null);
}

/// <summary>A migration to run.</summary>
/// <param name="Parsed">The parsed export.</param>
/// <param name="Currency">Currency of new accounts (the budget's currency).</param>
/// <param name="Accounts">A choice per source account; accounts without one are skipped.</param>
public sealed record MigrationRequest(ParseResult Parsed, string Currency, IReadOnlyList<MigrationAccountChoice> Accounts);

/// <summary>The result for one source account.</summary>
/// <param name="SourceAccount">The file's account name.</param>
/// <param name="AccountId">The Keel account (new or existing).</param>
/// <param name="AccountName">Its name.</param>
/// <param name="Created">Whether the migration created it.</param>
/// <param name="Summary">The pipeline's summary for its rows.</param>
public sealed record MigrationAccountResult(string SourceAccount, Guid AccountId, string AccountName, bool Created, ImportSummary Summary);

/// <summary>What a migration did (or, for a preview, would do).</summary>
/// <param name="Accounts">Per source account, in file order (skipped accounts are absent).</param>
/// <param name="GroupsCreated">Category groups created.</param>
/// <param name="CategoriesCreated">Categories created.</param>
/// <param name="TagsCreated">Tags created.</param>
public sealed record MigrationSummary(IReadOnlyList<MigrationAccountResult> Accounts, int GroupsCreated, int CategoriesCreated, int TagsCreated)
{
    /// <summary>New transactions.</summary>
    public int Added => Accounts.Sum(a => a.Summary.Added);

    /// <summary>Rows skipped as duplicates (already imported).</summary>
    public int Duplicates => Accounts.Sum(a => a.Summary.DuplicatesSkipped);

    /// <summary>Rows matched to transactions entered by hand.</summary>
    public int Matched => Accounts.Sum(a => a.Summary.MatchedToExisting);

    /// <summary>Rows paired as transfers.</summary>
    public int Transfers => Accounts.Sum(a => a.Summary.TransfersMatched);

    /// <summary>New rows left without a category.</summary>
    public int Uncategorized => Accounts.Sum(a => a.Summary.Uncategorized);

    /// <summary>Accounts created.</summary>
    public int AccountsCreated => Accounts.Count(a => a.Created);

    /// <summary>Whether anything was written.</summary>
    public bool HasChanges => AccountsCreated + GroupsCreated + CategoriesCreated + TagsCreated > 0 || Accounts.Any(a => a.Summary.HasChanges);
}

/// <summary>What a budget export import did (or would do).</summary>
/// <param name="Months">Months in the file.</param>
/// <param name="Assignments">Category-months whose assigned amount is written (changed or new, non-zero).</param>
/// <param name="Unchanged">Category-months that already hold the file's amount.</param>
/// <param name="GroupsCreated">Category groups created.</param>
/// <param name="CategoriesCreated">Categories created.</param>
/// <param name="Skipped">Rows left out (credit card payment categories without a matching card, Ready to Assign).</param>
public sealed record BudgetImportSummary(int Months, int Assignments, int Unchanged, int GroupsCreated, int CategoriesCreated, int Skipped)
{
    /// <summary>First month in the file.</summary>
    public DateOnly? FirstMonth { get; init; }

    /// <summary>Last month in the file.</summary>
    public DateOnly? LastMonth { get; init; }

    /// <summary>Whether anything was written.</summary>
    public bool HasChanges => Assignments + GroupsCreated + CategoriesCreated > 0;
}
