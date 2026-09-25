using System.Globalization;
using Keel.Application.Ledger;
using Keel.Application.Undo;
using Keel.Desktop.Resources;
using Keel.Domain;

namespace Keel.Desktop.Services;

/// <summary>Localized text for ledger enums and money (all strings come from Strings.resx).</summary>
public static class LedgerText
{
    /// <summary>User-facing explanation of a refused ledger operation.</summary>
    public static string Error(LedgerError error) => Lookup("LedgerError_" + error) ?? error.ToString();

    /// <summary>User-facing name of an undoable action, e.g. "delete transactions".</summary>
    public static string Action(LedgerAction action) => action == LedgerAction.TagCategoryFlex
        ? Strings.Flex_ActionTagCategory
        : Lookup("Action_" + action) ?? action.ToString();

    /// <summary>Display name of an account type.</summary>
    public static string AccountType(AccountType type) => Lookup("AccountType_" + type) ?? type.ToString();

    /// <summary>Sidebar heading of an account group.</summary>
    public static string Group(AccountGroup group) => Lookup("AccountGroup_" + group) ?? group.ToString();

    /// <summary>Display name of a cleared status.</summary>
    public static string Status(TransactionStatus status) => Lookup("TxnStatus_" + status) ?? status.ToString();

    /// <summary>Formats minor units as currency in the current culture, e.g. "$1,234.56".</summary>
    public static string Money(long amount, string currency) =>
        new Money(amount, Currency.IsValidCode(currency) ? currency : Currency.Default).Format(CultureInfo.CurrentCulture);

    /// <summary>The payee text that stands for a transfer to <paramref name="accountName"/>.</summary>
    public static string TransferPayee(string accountName) =>
        string.Format(CultureInfo.CurrentCulture, Strings.Register_TransferPayee, accountName);

    /// <summary>Formats a template from Strings.resx in the current culture.</summary>
    public static string Format(string template, params object?[] args) => string.Format(CultureInfo.CurrentCulture, template, args);

    private static string? Lookup(string key) => Strings.ResourceManager.GetString(key, Strings.Culture);
}
