using Keel.Application.Accounts;
using Keel.Application.Alerts;
using Keel.Application.Forecast;
using Keel.Application.Ledger;
using Keel.Application.Recurring;
using Keel.Application.Scheduling;
using Keel.Domain;
using Keel.Infrastructure.Tests.Ledger;

namespace Keel.Infrastructure.Tests.Recurring;

/// <summary>Builds recurring series on a real budget file relative to today (the services use the system clock).</summary>
internal sealed class M5TestLedger(LedgerTestHost host)
{
    public static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);

    private static CancellationToken Ct => CancellationToken.None;

    public LedgerTestHost Host { get; } = host;

    public IRecurringService Recurring => Host.Get<IRecurringService>();

    public IScheduledTransactionService Scheduled => Host.Get<IScheduledTransactionService>();

    public IAlertService Alerts => Host.Get<IAlertService>();

    public IForecastService Forecast => Host.Get<IForecastService>();

    public async Task<AccountDto> AccountAsync(string name, AccountType type = AccountType.Checking, long opening = 5_000_00) =>
        await Host.Accounts.CreateAccountAsync(new CreateAccountRequest(name, type, "USD", Today.AddYears(-2), opening), Ct);

    /// <summary>Adds <paramref name="count"/> monthly charges ending on the last same-day date on or before <paramref name="last"/>.</summary>
    public async Task<List<TransactionDto>> MonthlyAsync(Guid account, string payee, long amount, int count, DateOnly last, Guid? category = null)
    {
        var rows = new List<TransactionDto>();
        for (var i = count - 1; i >= 0; i--)
        {
            rows.Add(await AddAsync(account, payee, amount, last.AddMonths(-i), category));
        }

        return rows;
    }

    /// <summary>Adds charges every <paramref name="days"/> days ending on <paramref name="last"/>.</summary>
    public async Task<List<TransactionDto>> EveryAsync(Guid account, string payee, long amount, int count, int days, DateOnly last, Guid? category = null)
    {
        var rows = new List<TransactionDto>();
        for (var i = count - 1; i >= 0; i--)
        {
            rows.Add(await AddAsync(account, payee, amount, last.AddDays(-i * days), category));
        }

        return rows;
    }

    public Task<TransactionDto> AddAsync(Guid account, string payee, long amount, DateOnly date, Guid? category = null, TransactionStatus status = TransactionStatus.Cleared) =>
        Host.Transactions.SaveAsync(new SaveTransactionRequest(null, account, date, amount, payee, category, null, status), Ct);

    public async Task<RecurringItemDto> ItemAsync(string payee) =>
        (await Recurring.GetItemsAsync(new RecurringItemFilter([.. Enum.GetValues<RecurringStatus>()]), Ct)).Single(i => i.PayeeName == payee);
}
