using System.Globalization;
using Keel.Application.Import;
using Keel.Application.Portability;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain.Entities;

namespace Keel.Desktop.ViewModels.Portability;

/// <summary>User-facing text of the export, bundle and migration flows.</summary>
public static class PortabilityText
{
    /// <summary>The message for a bundle problem.</summary>
    public static string BundleError(BundleException error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var template = Strings.ResourceManager.GetString("Bundle_Error" + error.Code, Strings.Culture) ?? "{0}";
        return LedgerText.Format(template, error.Message);
    }

    /// <summary>"Exported from Household.keel on Sep 25, 2026 10:15 by Keel 1.0.0".</summary>
    public static string BundleSource(BundleInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return LedgerText.Format(Strings.Bundle_Source, string.IsNullOrEmpty(info.SourceFile) ? Strings.Bundle_UnknownFile : info.SourceFile,
            info.ExportedAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), info.AppVersion);
    }

    /// <summary>"8 accounts · 3,000 transactions · 45 categories · 2 rules · 4 scheduled".</summary>
    public static string BundleCounts(BundleInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        return LedgerText.Format(Strings.Bundle_Counts, info.Count(nameof(Account)), info.Count(nameof(Transaction)), info.Count(nameof(Category)),
            info.Count(nameof(Rule)), info.Count(nameof(ScheduledTransaction)));
    }

    /// <summary>The status-strip text after a migration.</summary>
    public static string MigrationSummary(MigrationSummary summary, string fileName)
    {
        ArgumentNullException.ThrowIfNull(summary);
        if (!summary.HasChanges)
        {
            return LedgerText.Format(Strings.Ynab_NothingNew, fileName);
        }

        var parts = new List<string>();
        void Add(int count, string template)
        {
            if (count > 0)
            {
                parts.Add(LedgerText.Format(template, count));
            }
        }

        Add(summary.AccountsCreated, Strings.Ynab_SummaryAccounts);
        Add(summary.Added, Strings.Ynab_SummaryAdded);
        Add(summary.Duplicates, Strings.Ynab_SummaryDuplicates);
        Add(summary.Transfers, Strings.Ynab_SummaryTransfers);
        Add(summary.CategoriesCreated, Strings.Ynab_SummaryCategories);
        Add(summary.TagsCreated, Strings.Ynab_SummaryTags);
        return LedgerText.Format(Strings.Ynab_Summary, fileName, string.Join(", ", parts));
    }

    /// <summary>The preview line of a migration: what the import would do.</summary>
    public static string MigrationPreview(MigrationSummary preview)
    {
        ArgumentNullException.ThrowIfNull(preview);
        return LedgerText.Format(Strings.Ynab_Preview, preview.Added, preview.Duplicates + preview.Matched, preview.Transfers, preview.AccountsCreated);
    }

    /// <summary>What a budget export would write.</summary>
    public static string BudgetSummary(BudgetImportSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        var range = summary.FirstMonth is { } first && summary.LastMonth is { } last
            ? LedgerText.Format(Strings.Ynab_BudgetRange, Month(first), Month(last))
            : string.Empty;
        return LedgerText.Format(Strings.Ynab_BudgetPreview, summary.Months, range, summary.Assignments, summary.Unchanged, summary.Skipped, summary.CategoriesCreated, summary.GroupsCreated);
    }

    /// <summary>"Jan 2026".</summary>
    public static string Month(DateOnly month) => month.ToString("MMM yyyy", CultureInfo.CurrentCulture);

    /// <summary>The dialog title for a migration export.</summary>
    public static string SourceName(ImportFileFormat format) => format == ImportFileFormat.Monarch ? Strings.Monarch_Name : Strings.Ynab_Name;
}
