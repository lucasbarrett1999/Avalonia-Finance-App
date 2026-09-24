using Going.Plaid.Entity;
using Keel.Application.Sync;
using Keel.Domain;
using AccountType = Keel.Domain.AccountType;
using PlaidAccountType = Going.Plaid.Entity.AccountType;
using PlaidTransaction = Going.Plaid.Entity.Transaction;

namespace Keel.Infrastructure.Sync.Plaid;

/// <summary>Maps Plaid entities to Keel's provider records (sign convention, minor units, account types, health).</summary>
internal static class PlaidMapping
{
    /// <summary>Plaid error codes that mean the user must sign in again (update mode).</summary>
    public static IReadOnlySet<string> ReauthCodes { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "ITEM_LOGIN_REQUIRED",
        "PENDING_EXPIRATION",
        "PENDING_DISCONNECT",
        "ACCESS_NOT_GRANTED",
        "INSUFFICIENT_CREDENTIALS",
        "INVALID_CREDENTIALS",
        "INVALID_MFA",
        "INVALID_UPDATED_USERNAME",
        "ITEM_LOCKED",
        "USER_SETUP_REQUIRED",
        "USER_INPUT_TIMEOUT",
        "MFA_NOT_SUPPORTED",
        "NO_ACCOUNTS",
    };

    /// <summary>The Keel type suggested for a Plaid account.</summary>
    public static AccountType SuggestType(PlaidAccountType type, AccountSubtype? subtype) => type switch
    {
        PlaidAccountType.Depository => subtype switch
        {
            AccountSubtype.Savings or AccountSubtype.MoneyMarket or AccountSubtype.Cd or AccountSubtype.Hsa or AccountSubtype.CashManagement => AccountType.Savings,
            _ => AccountType.Checking,
        },
        PlaidAccountType.Credit => subtype == AccountSubtype.LineOfCredit ? AccountType.LineOfCredit : AccountType.CreditCard,
        PlaidAccountType.Loan => subtype is AccountSubtype.LineOfCredit or AccountSubtype.HomeEquity or AccountSubtype.Overdraft
            ? AccountType.LineOfCredit
            : AccountType.Loan,
        PlaidAccountType.Investment => AccountType.Investment,
        _ => AccountType.OtherAsset,
    };

    /// <summary>Whether Plaid reports the account's balance as money owed (credit and loan accounts).</summary>
    public static bool IsLiability(PlaidAccountType type) => type is PlaidAccountType.Credit or PlaidAccountType.Loan;

    /// <summary>A valid ISO code for a Plaid amount: the ISO code, else the fallback (unofficial codes such as crypto are not money Keel can hold).</summary>
    public static string CurrencyOf(string? isoCode, string fallback) =>
        Currency.IsValidCode(isoCode?.Trim().ToUpperInvariant()) ? Currency.Normalize(isoCode!) : Currency.Normalize(fallback);

    /// <summary>A Plaid account as a provider account.</summary>
    public static ProviderAccount ToProviderAccount(Account account) => new(
        account.AccountId,
        string.IsNullOrWhiteSpace(account.Name) ? account.OfficialName ?? account.AccountId : account.Name,
        string.IsNullOrWhiteSpace(account.Mask) ? null : account.Mask,
        SuggestType(account.Type, account.Subtype),
        CurrencyOf(account.Balances?.IsoCurrencyCode, Currency.Default));

    /// <summary>A Plaid balance in Keel's sign convention: liabilities are negative.</summary>
    public static ProviderBalance? ToProviderBalance(Account account, DateTimeOffset now)
    {
        if (account.Balances is not { Current: { } current } balances)
        {
            return null;
        }

        var currency = CurrencyOf(balances.IsoCurrencyCode, Currency.Default);
        var sign = IsLiability(account.Type) ? -1 : 1;
        var available = balances.Available is { } a ? sign * Money.FromDecimal(a, currency).Amount : (long?)null;
        return new ProviderBalance(account.AccountId, sign * Money.FromDecimal(current, currency).Amount, available, currency, balances.LastUpdatedDatetime ?? now);
    }

    /// <summary>
    /// A Plaid transaction in Keel's sign convention. Plaid amounts are positive for money leaving
    /// the account, so the sign flips. The payee is Plaid's merchant name when it has one, else the
    /// cleaned description.
    /// </summary>
    public static ProviderTransaction ToProviderTransaction(PlaidTransaction transaction, IReadOnlyDictionary<string, string> accountCurrencies)
    {
        var accountId = transaction.AccountId ?? string.Empty;
        var fallback = accountCurrencies.TryGetValue(accountId, out var c) ? c : Currency.Default;
        var currency = CurrencyOf(transaction.IsoCurrencyCode, fallback);
        var amount = -Money.FromDecimal(transaction.Amount ?? 0m, currency).Amount;
#pragma warning disable CS0612 // Plaid still fills "name"; merchant_name is missing for many rows.
        var payee = !string.IsNullOrWhiteSpace(transaction.MerchantName) ? transaction.MerchantName : transaction.Name ?? string.Empty;
#pragma warning restore CS0612
        return new ProviderTransaction(
            transaction.TransactionId ?? string.Empty,
            accountId,
            transaction.Date ?? transaction.AuthorizedDate ?? DateOnly.FromDateTime(DateTime.UtcNow),
            amount,
            payee.Trim(),
            null,
            transaction.Pending ?? false,
            string.IsNullOrEmpty(transaction.PendingTransactionId) ? null : transaction.PendingTransactionId);
    }

    /// <summary>Whether the sync response says the account history has been pulled.</summary>
    public static bool IsHistoryComplete(TransactionsUpdateStatus status) =>
        status is not (TransactionsUpdateStatus.NotReady or TransactionsUpdateStatus.InitialUpdateComplete);

    /// <summary>The connection health a Plaid error implies.</summary>
    public static SyncStatus StatusOf(PlaidError error) =>
        ReauthCodes.Contains(error.ErrorCode ?? string.Empty) ? SyncStatus.NeedsReauth : SyncStatus.Error;

    /// <summary>A provider exception for a Plaid error (code and Plaid's non-secret message).</summary>
    public static BankProviderException ToException(PlaidError error, string operation) =>
        new(StatusOf(error), string.IsNullOrEmpty(error.ErrorCode) ? BankErrorCodes.Unknown : error.ErrorCode, $"Plaid {operation} failed: {error.ErrorCode}.");
}
