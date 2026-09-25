using Keel.Application.Import;
using Keel.Domain;
using Keel.Infrastructure.Import.Monarch;
using Keel.Infrastructure.Rules;

namespace Keel.Infrastructure.Import.Migration;

/// <summary>How a migrated row's category maps to Keel.</summary>
internal enum MigrationCategoryKind
{
    /// <summary>No category (uncategorized, or a transfer).</summary>
    None,

    /// <summary>Inflow: Ready to Assign.</summary>
    ReadyToAssign,

    /// <summary>A user category, found by group and name or created.</summary>
    Named,
}

/// <summary>A migrated row's category reference.</summary>
/// <param name="Kind">How it maps.</param>
/// <param name="Group">Group name (for <see cref="MigrationCategoryKind.Named"/>).</param>
/// <param name="Name">Category name (for <see cref="MigrationCategoryKind.Named"/>).</param>
/// <param name="IsTransfer">The source marks the row as money moving between the user's accounts.</param>
internal readonly record struct MigrationCategoryRef(MigrationCategoryKind Kind, string Group, string Name, bool IsTransfer);

/// <summary>
/// Reads what the YNAB and Monarch parsers keep in a row (category parts, cleared state, flag, tags) into the
/// pipeline's terms (ADR 0099). Pure.
/// </summary>
internal static class MigrationRows
{
    /// <summary>YNAB's inflow group and its budget-level category ("Ready to Assign", formerly "To be Budgeted").</summary>
    public const string YnabInflowGroup = "Inflow";

    /// <summary>YNAB's group of credit card payment categories (one per card, named after it).</summary>
    public const string YnabCreditCardGroup = "Credit Card Payments";

    private const string YnabTransferPrefix = "Transfer : ";

    /// <summary>The category of a parsed row.</summary>
    public static MigrationCategoryRef CategoryOf(ImportFileFormat format, ParsedTransaction row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (format == ImportFileFormat.Monarch)
        {
            var name = row.Category?.Trim() ?? string.Empty;
            if (MonarchCategories.Transfers.Contains(name))
            {
                return new(MigrationCategoryKind.None, string.Empty, string.Empty, IsTransfer: true);
            }

            if (MonarchCategories.Income.Contains(name))
            {
                return new(MigrationCategoryKind.ReadyToAssign, string.Empty, string.Empty, false);
            }

            return name.Length == 0 || string.Equals(name, MonarchCategories.Uncategorized, StringComparison.OrdinalIgnoreCase)
                ? new(MigrationCategoryKind.None, string.Empty, string.Empty, false)
                : new(MigrationCategoryKind.Named, MonarchCategories.GroupOf(name), name, false);
        }

        var transfer = row.PayeeRaw.StartsWith(YnabTransferPrefix, StringComparison.OrdinalIgnoreCase);
        var group = Extra(row, "Category Group");
        var category = Extra(row, "Category");
        if (group.Length == 0 && category.Length == 0 && row.Category is { } path && path.IndexOf(": ", StringComparison.Ordinal) is var colon and > 0)
        {
            group = path[..colon].Trim();
            category = path[(colon + 2)..].Trim();
        }

        if (string.Equals(group, YnabInflowGroup, StringComparison.OrdinalIgnoreCase))
        {
            return new(MigrationCategoryKind.ReadyToAssign, string.Empty, string.Empty, transfer);
        }

        if (string.Equals(group, YnabCreditCardGroup, StringComparison.OrdinalIgnoreCase) || category.Length == 0)
        {
            return new(MigrationCategoryKind.None, string.Empty, string.Empty, transfer || group.Length > 0);
        }

        return new(MigrationCategoryKind.Named, group.Length == 0 ? MonarchCategories.OtherGroup : group, category, transfer);
    }

    /// <summary>YNAB's Cleared column as a status; null for Monarch and unknown values (pipeline default).</summary>
    public static TransactionStatus? StatusOf(ImportFileFormat format, ParsedTransaction row)
    {
        ArgumentNullException.ThrowIfNull(row);
        if (format != ImportFileFormat.Ynab)
        {
            return null;
        }

        return Extra(row, "Cleared").ToUpperInvariant() switch
        {
            "RECONCILED" => TransactionStatus.Reconciled,
            "CLEARED" => TransactionStatus.Cleared,
            "UNCLEARED" => TransactionStatus.Uncleared,
            _ => null,
        };
    }

    /// <summary>Tag names: Monarch's Tags column; a YNAB flag (any colour) becomes the reserved "Flagged" tag.</summary>
    public static IReadOnlyList<string> TagsOf(ImportFileFormat format, ParsedTransaction row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return format switch
        {
            ImportFileFormat.Monarch => MonarchImportParser.TagsOf(row),
            ImportFileFormat.Ynab when Extra(row, "Flag").Length > 0 => [SnapshotSource.FlaggedTag],
            _ => [],
        };
    }

    /// <summary>The type suggested for a new account named <paramref name="name"/> (on-budget types only).</summary>
    public static AccountType SuggestType(string name)
    {
        var n = (name ?? string.Empty).ToUpperInvariant();
        if (n.Contains("LINE OF CREDIT", StringComparison.Ordinal) || n.Contains("HELOC", StringComparison.Ordinal))
        {
            return AccountType.LineOfCredit;
        }

        string[] card = ["CREDIT", "CARD", "VISA", "MASTERCARD", "AMEX", "AMERICAN EXPRESS", "DISCOVER"];
        if (card.Any(k => n.Contains(k, StringComparison.Ordinal)))
        {
            return AccountType.CreditCard;
        }

        if (n.Contains("SAVING", StringComparison.Ordinal))
        {
            return AccountType.Savings;
        }

        return n.Contains("CASH", StringComparison.Ordinal) || n.Contains("WALLET", StringComparison.Ordinal) ? AccountType.Cash : AccountType.Checking;
    }

    private static string Extra(ParsedTransaction row, string name) => row.Extras.TryGetValue(name, out var value) ? value.Trim() : string.Empty;
}
