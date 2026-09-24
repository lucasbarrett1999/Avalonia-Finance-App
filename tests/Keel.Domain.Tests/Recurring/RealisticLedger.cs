using Keel.Domain.Recurring;

namespace Keel.Domain.Tests.Recurring;

/// <summary>
/// A realistic 15-month ledger ending 2026-09-24, with raw bank descriptors: a biweekly paycheck,
/// rent on the 1st, Netflix with a price increase, quarterly insurance, an annual domain renewal,
/// utility bills with variable amounts, a cancelled gym, a streaming subscription on checking, and
/// irregular coffee and grocery spending that must not be detected.
/// </summary>
public static class RealisticLedger
{
    public static readonly DateOnly AsOf = new(2026, 9, 24);
    public static readonly Guid Checking = RecurringSeries.Account(1);
    public static readonly Guid Visa = RecurringSeries.Account(2);
    public static readonly Guid Savings = RecurringSeries.Account(3);

    public const string PaycheckRaw = "ACME CORP PAYROLL PPD ID: 1234567890";
    public const string RentRaw = "LANDLORD LLC RENT";
    public const string NetflixRaw = "NETFLIX.COM 866-579-7172 CA";
    public const string InsuranceRaw = "STATE FARM INSURANCE";
    public const string DomainRaw = "NAMECHEAP.COM";
    public const string UtilityRaw = "PACIFIC GAS AND ELECTRIC";
    public const string GymRaw = "PLANET FITNESS CLUB FEES";
    public const string SpotifyRaw = "Spotify USA";
    public const string CoffeeRaw = "SQ *BLUE BOTTLE COFFEE";
    public const string GroceryRaw = "TRADER JOE'S #552";

    public static string Name(Guid account) =>
        account == Checking ? "Checking" : account == Visa ? "Visa" : account == Savings ? "Savings" : account.ToString();

    public static IReadOnlyList<RecurringTransaction> Transactions() => Build().Transactions;

    /// <summary>The ledger and the cadence each descriptor should be detected with (null: not detected).</summary>
    public static (IReadOnlyList<RecurringTransaction> Transactions, IReadOnlyList<(string Raw, Guid Account, RecurrenceCadence? Cadence)> Labels) Build()
    {
        var list = new List<RecurringTransaction>();
        var n = 0;
        void Add(Guid account, DateOnly date, long amount, string raw) =>
            list.Add(RecurringTransaction.FromRaw(RecurringSeries.Id(5, n++), account, date, amount, raw));

        // Biweekly paycheck on Fridays.
        for (var d = new DateOnly(2025, 7, 11); d <= AsOf; d = d.AddDays(14))
        {
            Add(Checking, d, 245_000, PaycheckRaw);
        }

        // Rent on the 1st; March 2026 paid on Friday, February 27.
        for (var m = new DateOnly(2025, 7, 1); m <= AsOf; m = m.AddMonths(1))
        {
            Add(Checking, m == new DateOnly(2026, 3, 1) ? new DateOnly(2026, 2, 27) : m, -185_000, RentRaw);
        }

        // Netflix on the 12th on the card: $15.49, then $17.99 from May 2026.
        for (var m = new DateOnly(2025, 7, 12); m <= AsOf; m = m.AddMonths(1))
        {
            Add(Visa, m, m < new DateOnly(2026, 5, 1) ? -1_549 : -1_799, NetflixRaw);
        }

        // Quarterly insurance.
        foreach (var d in new[] { new DateOnly(2025, 7, 15), new DateOnly(2025, 10, 15), new DateOnly(2026, 1, 15), new DateOnly(2026, 4, 15), new DateOnly(2026, 7, 15) })
        {
            Add(Checking, d, -31_240, InsuranceRaw);
        }

        // Annual domain renewal, a day later the second year.
        Add(Visa, new DateOnly(2025, 8, 3), -1_498, DomainRaw);
        Add(Visa, new DateOnly(2026, 8, 4), -1_498, DomainRaw);

        // Utility bills around the 20th, seasonal amounts.
        var utility = new (int Y, int M, int D, long Amount)[]
        {
            (2025, 7, 21, -18_450), (2025, 8, 19, -20_110), (2025, 9, 22, -16_020), (2025, 10, 20, -11_240),
            (2025, 11, 18, -9_870), (2025, 12, 22, -13_560), (2026, 1, 20, -15_930), (2026, 2, 19, -14_470),
            (2026, 3, 23, -11_020), (2026, 4, 20, -8_590), (2026, 5, 19, -9_140), (2026, 6, 22, -14_380),
            (2026, 7, 20, -19_760), (2026, 8, 21, -20_930), (2026, 9, 21, -17_310),
        };
        foreach (var (y, mo, d, amount) in utility)
        {
            Add(Checking, new DateOnly(y, mo, d), amount, UtilityRaw);
        }

        // Gym, cancelled after February 2026.
        for (var m = new DateOnly(2025, 7, 5); m <= new DateOnly(2026, 2, 5); m = m.AddMonths(1))
        {
            Add(Checking, m, -2_499, GymRaw);
        }

        // Streaming on checking on the 28th.
        for (var m = new DateOnly(2025, 7, 28); m <= AsOf; m = m.AddMonths(1))
        {
            Add(Checking, m, -1_199, SpotifyRaw);
        }

        // Irregular coffee: 1 to 6 days apart, $4.50 to $7.25.
        var random = new Random(42);
        for (var d = new DateOnly(2025, 6, 26); d <= AsOf; d = d.AddDays(random.Next(1, 7)))
        {
            Add(Visa, d, -(450 + (25 * random.Next(0, 12))), CoffeeRaw);
        }

        // Irregular groceries: 2, 3, 4, 10 or 11 days apart.
        var gaps = new[] { 2, 3, 4, 10, 11 };
        for (var d = new DateOnly(2025, 6, 27); d <= AsOf; d = d.AddDays(gaps[random.Next(gaps.Length)]))
        {
            Add(Checking, d, -(4_000 + (100 * random.Next(0, 80))), GroceryRaw);
        }

        IReadOnlyList<(string, Guid, RecurrenceCadence?)> labels =
        [
            (PaycheckRaw, Checking, RecurrenceCadence.Biweekly),
            (RentRaw, Checking, RecurrenceCadence.Monthly),
            (NetflixRaw, Visa, RecurrenceCadence.Monthly),
            (InsuranceRaw, Checking, RecurrenceCadence.Quarterly),
            (DomainRaw, Visa, RecurrenceCadence.Yearly),
            (UtilityRaw, Checking, RecurrenceCadence.Monthly),
            (GymRaw, Checking, RecurrenceCadence.Monthly),
            (SpotifyRaw, Checking, RecurrenceCadence.Monthly),
            (CoffeeRaw, Visa, null),
            (GroceryRaw, Checking, null),
        ];
        return (list, labels);
    }
}
