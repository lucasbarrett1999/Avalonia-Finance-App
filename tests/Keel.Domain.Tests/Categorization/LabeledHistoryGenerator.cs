using System.Globalization;
using Keel.Domain.Categorization;
using Keel.Domain.Entities;
using Keel.Domain.Rules;

namespace Keel.Domain.Tests.Categorization;

/// <summary>
/// Deterministic, realistic labeled history for the learner accuracy fixture (PRD 12 M4, 13):
/// 30 categories, 64 payees with noisy bank-descriptor variants, amount and weekday patterns,
/// three accounts, payees split across two categories by amount, account or chance, refunds,
/// and 1% inconsistent labels. Also compiled into Keel.Benchmarks.
/// </summary>
public static class LabeledHistoryGenerator
{
    public static readonly Guid Checking = new("00000000-0000-7000-8000-0000000ac001");
    public static readonly Guid Visa = new("00000000-0000-7000-8000-0000000ac002");
    public static readonly Guid Amex = new("00000000-0000-7000-8000-0000000ac003");

    private static readonly Guid[] AccountIds = [Checking, Visa, Amex];

    public static readonly IReadOnlyList<string> CategoryNames =
    [
        "Groceries", "Dining Out", "Coffee", "Fast Food", "Gas", "Public Transit", "Rideshare", "Parking",
        "Rent", "Electric", "Internet", "Phone", "Streaming", "Music", "Software", "Gym", "Pharmacy",
        "Doctor", "Clothing", "Home Improvement", "Household", "Electronics", "Books", "Pets", "Gifts",
        "Travel", "Insurance", "Charity", "Entertainment", "Ready to Assign",
    ];

    private static readonly string[] Cities = ["BROOKLYN NY", "NEW YORK NY", "JERSEY CITY NJ", "HOBOKEN NJ"];

    /// <summary>Category ids by name (Ready to Assign is the system category).</summary>
    public static IReadOnlyDictionary<string, Guid> Categories { get; } = CategoryNames
        .Select((name, i) => (name, id: name == "Ready to Assign"
            ? SystemIds.ReadyToAssignCategory
            : new Guid("00000000-0000-7000-8000-00000000" + (0xca00 + i).ToString("x4", CultureInfo.InvariantCulture))))
        .ToDictionary(x => x.name, x => x.id);

    /// <summary>The category catalog (Ready to Assign restricted).</summary>
    public static IReadOnlyList<LearnerCategory> Catalog { get; } = CategoryNames
        .Select(n => new LearnerCategory(Categories[n], n, n == "Ready to Assign"))
        .ToList();

    /// <summary>Names of the payees whose history is split across two categories.</summary>
    public static IReadOnlyList<string> SplitPayees => Payees.Where(p => p.Rule is not Single).Select(p => p.Name).ToList();

    /// <summary>Number of payee definitions.</summary>
    public static int PayeeCount => Payees.Count;

    private static readonly List<PayeeSpec> Payees = BuildPayees();

    /// <summary>
    /// Generates <paramref name="months"/> months of approved history starting 2024-01, in date
    /// order. <paramref name="extraPayees"/> adds synthetic single-category merchants (for scale tests).
    /// </summary>
    public static IReadOnlyList<LabeledExample> Generate(int months = 24, int seed = 42, int extraPayees = 0)
    {
        var rng = new Random(seed);
        var payees = new List<PayeeSpec>(Payees);
        payees.AddRange(ExtraPayees(extraPayees, seed));
        var start = new DateOnly(2024, 1, 1);
        var rows = new List<(DateOnly Date, int Order, LabeledExample Example)>();
        var order = 0;
        for (var m = 0; m < months; m++)
        {
            var monthStart = start.AddMonths(m);
            var days = DateTime.DaysInMonth(monthStart.Year, monthStart.Month);
            foreach (var payee in payees)
            {
                var count = Poisson(rng, payee.PerMonth);
                for (var k = 0; k < count; k++)
                {
                    var date = PickDate(rng, monthStart, days, payee);
                    var account = PickAccount(rng, payee.AccountWeights);
                    var amount = PickAmount(rng, payee);
                    var refund = payee.RefundRate > 0 && rng.NextDouble() < payee.RefundRate;
                    var signed = payee.Inflow || refund ? amount : -amount;
                    var category = payee.Rule.Pick(rng, amount, Array.IndexOf(AccountIds, account));
                    if (rng.NextDouble() < 0.01)
                    {
                        category = CategoryNames[rng.Next(CategoryNames.Count - 1)]; // an inconsistent label
                    }

                    var descriptor = Descriptor(rng, payee, date);
                    var snapshot = new TransactionSnapshot
                    {
                        Id = new Guid(order, 0, 0, new byte[8]),
                        AccountId = account,
                        Date = date,
                        Amount = signed,
                        PayeeRaw = descriptor,
                        Payee = descriptor,
                        Source = TransactionSource.File,
                        IsApproved = true,
                        CategoryId = Categories[category],
                    };
                    rows.Add((date, order++, new LabeledExample(snapshot, Categories[category], category)));
                }
            }
        }

        return rows.OrderBy(r => r.Date).ThenBy(r => r.Order).Select(r => r.Example).ToList();
    }

    private static List<PayeeSpec> BuildPayees()
    {
        const int Chk = 0, Vis = 1, Amx = 2;
        double[] Card(int preferred) => preferred switch { Vis => [0.1, 0.8, 0.1], Amx => [0.1, 0.1, 0.8], _ => [0.8, 0.1, 0.1] };
        int[] weekend = [5, 6, 0];
        int[] weekdays = [1, 2, 3, 4, 5];
        string[] store = ["{N}", "{N} #{S}", "POS DEBIT {N} {S4}", "{N} {CITY}", "PURCHASE AUTHORIZED ON {MD} {N} {CITY} S4{D12} CARD {D4}", "CHECKCARD {MMDD} {N} {D8}", "{n}"];
        string[] square = ["SQ *{N}", "SQ *{N} {CITY}", "TST* {N} {S4}", "{N}", "{n}"];
        string[] online = ["{N}", "{N}.COM", "{N} {D10}", "PAYPAL *{N}", "{n}"];
        string[] ach = ["{N} PPD ID: {D10}", "ACH DEBIT {N}", "{N} WEB ID: {D10}", "{N} AUTOPAY {D8}"];

        return
        [
            // Groceries
            new("TRADER JOE'S", 5, 5500, 0.45, Card(Vis), new Single("Groceries"), store, weekend),
            new("WHOLE FOODS MARKET", 3, 7200, 0.5, Card(Vis), new Single("Groceries"), store, weekend),
            new("KEY FOOD", 2, 3100, 0.5, Card(Chk), new Single("Groceries"), store),
            new("FAIRWAY MARKET", 1.5, 6400, 0.5, Card(Amx), new Single("Groceries"), store, weekend),
            new("COSTCO WHSE", 1.2, 18000, 0.4, [0.5, 0.5, 0], new ByAccount(["Groceries", "Household", "Household"], 0.9), store, weekend),
            new("WALMART", 1, 6500, 0.6, Card(Vis), new ByChance("Groceries", "Household", 0.6), store),
            new("INSTACART", 0.6, 9500, 0.3, Card(Amx), new Single("Groceries"), online),
            // Dining, coffee, fast food
            new("JOE'S PIZZA", 2, 2400, 0.4, Card(Vis), new Single("Dining Out"), square, weekend),
            new("CAFE MOGADOR", 1, 7800, 0.35, Card(Amx), new Single("Dining Out"), square, weekend),
            new("SHAKE SHACK", 1.5, 2300, 0.3, Card(Vis), new Single("Fast Food"), store),
            new("CHIPOTLE", 2.5, 1450, 0.25, Card(Vis), new Single("Fast Food"), store, weekdays),
            new("UBER EATS", 2, 3400, 0.4, Card(Amx), new Single("Dining Out"), online, weekend),
            new("DOORDASH", 1, 4100, 0.4, Card(Amx), new Single("Dining Out"), ["DD *DOORDASH {N2}", "DOORDASH*{N2}", "DOORDASH {N2}"], weekend),
            new("STARBUCKS", 8, 650, 0.3, Card(Vis), new Single("Coffee"), store, weekdays),
            new("BLUE BOTTLE COFFEE", 4, 580, 0.25, Card(Vis), new Single("Coffee"), square, weekdays),
            new("DUNKIN", 2, 420, 0.3, Card(Chk), new Single("Coffee"), store, weekdays),
            new("MCDONALD'S", 1.5, 1100, 0.3, Card(Chk), new Single("Fast Food"), store),
            new("SWEETGREEN", 3, 1650, 0.2, Card(Vis), new Single("Dining Out"), store, weekdays),
            // Transport
            new("SHELL OIL", 2.5, 4800, 0.3, Card(Vis), new ByAmount(1500, "Fast Food", "Gas", 0.92), store),
            new("EXXONMOBIL", 1.5, 5200, 0.25, Card(Vis), new Single("Gas"), store),
            new("MTA NYCT PAYGO", 10, 290, 0.05, Card(Chk), new Single("Public Transit"), ["{N} {CITY}", "{N}", "MTA*NYCT PAYGO"], weekdays),
            new("NJ TRANSIT", 2, 1100, 0.3, Card(Chk), new Single("Public Transit"), store),
            new("UBER", 3, 2300, 0.5, Card(Amx), new Single("Rideshare"), ["UBER *TRIP {D4}", "UBER TRIP {S4}", "UBER *TRIP HELP.UBER.COM"]),
            new("LYFT", 1.5, 1900, 0.5, Card(Amx), new Single("Rideshare"), ["LYFT *RIDE {DAY} {D4}", "LYFT RIDE {S4}", "LYFT *RIDE"]),
            new("ICON PARKING", 1, 3500, 0.4, Card(Vis), new Single("Parking"), store),
            new("PARKMOBILE", 1.5, 800, 0.4, Card(Vis), new Single("Parking"), online),
            // Housing and bills
            new("ACME PROPERTY MGMT", 1, 240000, 0, [1, 0, 0], new Single("Rent"), ach, FixedAmount: true),
            new("CON EDISON", 1, 11500, 0.3, [1, 0, 0], new Single("Electric"), ach),
            new("SPECTRUM", 1, 8999, 0, Card(Vis), new Single("Internet"), ach, FixedAmount: true),
            new("VERIZON WIRELESS", 1, 7500, 0.05, Card(Vis), new Single("Phone"), ach),
            new("GEICO", 1, 14200, 0, [1, 0, 0], new Single("Insurance"), ach, FixedAmount: true),
            new("LEMONADE INSURANCE", 1, 1500, 0, Card(Vis), new Single("Insurance"), online, FixedAmount: true),
            // Subscriptions
            new("NETFLIX", 1, 1549, 0, Card(Amx), new Single("Streaming"), online, FixedAmount: true),
            new("HULU", 1, 1799, 0, Card(Amx), new Single("Streaming"), ["HLU*HULU {D5}-U", "HULU {D10}", "HULU.COM/BILL"], FixedAmount: true),
            new("DISNEY PLUS", 1, 1399, 0, Card(Amx), new Single("Streaming"), online, FixedAmount: true),
            new("SPOTIFY", 1, 1199, 0, Card(Amx), new Single("Music"), online, FixedAmount: true),
            new("GITHUB", 1, 400, 0, Card(Vis), new Single("Software"), online, FixedAmount: true),
            new("DROPBOX", 1, 1199, 0, Card(Vis), new Single("Software"), online, FixedAmount: true),
            new("APPLE", 2, 999, 1.2, Card(Vis), new ByAmount(5000, "Software", "Electronics", 0.9), ["APPLE.COM/BILL", "APPLE STORE #R{S3}", "APPLE.COM/US {D10}"]),
            new("EQUINOX", 1, 26000, 0, Card(Amx), new Single("Gym"), ach, FixedAmount: true),
            new("CLASSPASS", 0.6, 4900, 0.2, Card(Amx), new Single("Gym"), online),
            // Health
            new("CVS PHARMACY", 2, 2300, 0.6, Card(Vis), new ByChance("Pharmacy", "Household", 0.7), store),
            new("DUANE READE", 1, 1600, 0.5, Card(Chk), new Single("Pharmacy"), store),
            new("ONE MEDICAL", 0.3, 20000, 0.5, Card(Vis), new Single("Doctor"), online),
            new("ZOCDOC NYU LANGONE", 0.3, 4000, 0.3, Card(Chk), new Single("Doctor"), ["NYU LANGONE {D8}", "NYU LANGONE HLTH {S4}", "NYU LANGONE"]),
            // Shopping
            new("AMAZON", 5, 3200, 0.9, Card(Amx), new ByAmount(6000, "Household", "Electronics", 0.85), ["AMZN Mktp US*{R}", "AMAZON.COM*{R}", "Amazon.com*{R}", "AMZN MKTP US {R}"], RefundRate: 0.05),
            new("TARGET", 1.5, 4800, 0.6, Card(Vis), new ByChance("Household", "Clothing", 0.65), store, weekend),
            new("UNIQLO", 0.5, 7500, 0.5, Card(Amx), new Single("Clothing"), store, weekend, RefundRate: 0.1),
            new("ZARA", 0.4, 8900, 0.5, Card(Amx), new Single("Clothing"), store, weekend, RefundRate: 0.1),
            new("HOME DEPOT", 0.6, 6800, 0.8, Card(Vis), new Single("Home Improvement"), store, weekend),
            new("BEST BUY", 0.3, 22000, 0.8, Card(Vis), new Single("Electronics"), store, RefundRate: 0.1),
            new("BARNES & NOBLE", 0.5, 2800, 0.4, Card(Vis), new Single("Books"), store, weekend),
            new("KINDLE", 1, 999, 0.4, Card(Amx), new ByChance("Books", "Entertainment", 0.85), ["Kindle Svcs*{R}", "KINDLE UNLTD*{R}", "AMAZON KINDLE {D8}"]),
            new("PETCO", 0.8, 5400, 0.4, Card(Vis), new Single("Pets"), store),
            new("CHEWY", 1, 6200, 0.3, Card(Amx), new Single("Pets"), online),
            new("ETSY", 0.4, 3600, 0.6, Card(Vis), new ByChance("Gifts", "Household", 0.7), online),
            new("1-800-FLOWERS", 0.2, 7400, 0.3, Card(Vis), new Single("Gifts"), online),
            // Travel and fun
            new("DELTA AIR LINES", 0.25, 38000, 0.5, Card(Amx), new Single("Travel"), ["DELTA AIR {D10}", "DELTA AIR LINES ATLANTA", "DELTA {D10}"], RefundRate: 0.05),
            new("AIRBNB", 0.2, 52000, 0.5, Card(Amx), new Single("Travel"), ["AIRBNB * {R}", "AIRBNB {D10}", "AIRBNB HMQ{R}"]),
            new("MARRIOTT", 0.15, 41000, 0.4, Card(Amx), new Single("Travel"), store),
            new("AMC THEATRES", 0.8, 3200, 0.3, Card(Vis), new Single("Entertainment"), store, weekend),
            new("TICKETMASTER", 0.3, 16000, 0.6, Card(Amx), new Single("Entertainment"), online),
            new("RED CROSS", 0.3, 5000, 0.5, [1, 0, 0], new Single("Charity"), online),
            // Income
            new("ACME CORP PAYROLL", 2.17, 412000, 0.02, [1, 0, 0], new Single("Ready to Assign"), ["{N} PPD ID: {D10}", "ACH CREDIT {N}", "{N} DIR DEP"], Inflow: true),
        ];
    }

    private static IEnumerable<PayeeSpec> ExtraPayees(int count, int seed)
    {
        var rng = new Random(seed + 1);
        string[] syllables = ["KA", "LO", "MIR", "TE", "ZAN", "BRO", "VEL", "NOR", "QUI", "PAX", "DEL", "RIO", "SUN", "OAK", "FOX", "LUM"];
        string[] kinds = ["MARKET", "CAFE", "SHOP", "STUDIO", "GRILL", "GOODS", "SUPPLY", "BAR", "DELI", "OUTLET"];
        for (var i = 0; i < count; i++)
        {
            var name = syllables[rng.Next(syllables.Length)] + syllables[rng.Next(syllables.Length)] + i.ToString("x", CultureInfo.InvariantCulture).ToUpperInvariant()
                + " " + kinds[rng.Next(kinds.Length)];
            var category = CategoryNames[rng.Next(CategoryNames.Count - 1)];
            yield return new PayeeSpec(name, 0.5 + (rng.NextDouble() * 2), 500 + rng.Next(20000), 0.4, [0.3, 0.4, 0.3], new Single(category), ["{N}", "{N} #{S}", "SQ *{N}", "{N} {CITY}"]);
        }
    }

    private static string Descriptor(Random rng, PayeeSpec payee, DateOnly date)
    {
        var template = payee.Templates[rng.Next(payee.Templates.Length)];
        string Digits(int n)
        {
            var chars = new char[n];
            for (var i = 0; i < n; i++)
            {
                chars[i] = (char)('0' + rng.Next(10));
            }

            return new string(chars);
        }

        string Ref()
        {
            const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ0123456789";
            var chars = new char[9];
            for (var i = 0; i < chars.Length; i++)
            {
                chars[i] = alphabet[rng.Next(alphabet.Length)];
            }

            return new string(chars);
        }

        var parts = payee.Name.Split(' ');
        var n2 = parts.Length > 1 ? string.Join(' ', parts.Skip(1)) : payee.Name;
        var titled = CultureInfo.InvariantCulture.TextInfo.ToTitleCase(payee.Name.ToLowerInvariant());
        return template
            .Replace("{N}", payee.Name, StringComparison.Ordinal)
            .Replace("{n}", titled, StringComparison.Ordinal)
            .Replace("{N2}", n2 == payee.Name ? "ORDER" : n2, StringComparison.Ordinal)
            .Replace("{S}", (100 + (payee.Name.Length * 37 % 800) + rng.Next(3)).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{S3}", (100 + rng.Next(900)).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{S4}", Digits(4), StringComparison.Ordinal)
            .Replace("{CITY}", Cities[rng.Next(10) < 7 ? 0 : rng.Next(Cities.Length)], StringComparison.Ordinal)
            .Replace("{MD}", date.ToString("MM/dd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{MMDD}", date.ToString("MMdd", CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{DAY}", date.ToString("ddd", CultureInfo.InvariantCulture).ToUpperInvariant(), StringComparison.Ordinal)
            .Replace("{D12}", Digits(12), StringComparison.Ordinal)
            .Replace("{D10}", Digits(10), StringComparison.Ordinal)
            .Replace("{D8}", Digits(8), StringComparison.Ordinal)
            .Replace("{D5}", Digits(5), StringComparison.Ordinal)
            .Replace("{D4}", Digits(4), StringComparison.Ordinal)
            .Replace("{R}", Ref(), StringComparison.Ordinal);
    }

    private static int Poisson(Random rng, double mean)
    {
        var limit = Math.Exp(-mean);
        var k = 0;
        var p = rng.NextDouble();
        while (p > limit)
        {
            k++;
            p *= rng.NextDouble();
        }

        return k;
    }

    private static DateOnly PickDate(Random rng, DateOnly monthStart, int days, PayeeSpec payee)
    {
        if (payee.FixedAmount || payee.Inflow)
        {
            // Bills and pay land on nearly the same day each month.
            var day = Math.Min(days, 1 + ((payee.Name.Length * 7) % 27) + rng.Next(3));
            return monthStart.AddDays(day - 1);
        }

        for (var attempt = 0; attempt < 4; attempt++)
        {
            var date = monthStart.AddDays(rng.Next(days));
            if (payee.Weekdays is null || payee.Weekdays.Contains((int)date.DayOfWeek))
            {
                return date;
            }
        }

        return monthStart.AddDays(rng.Next(days));
    }

    private static Guid PickAccount(Random rng, double[] weights)
    {
        var x = rng.NextDouble() * weights.Sum();
        for (var i = 0; i < weights.Length; i++)
        {
            x -= weights[i];
            if (x < 0)
            {
                return AccountIds[i];
            }
        }

        return AccountIds[Array.FindLastIndex(weights, w => w > 0)];
    }

    private static long PickAmount(Random rng, PayeeSpec payee)
    {
        if (payee.FixedAmount || payee.Spread == 0)
        {
            return payee.MedianCents;
        }

        // Log-normal around the median (Box-Muller).
        var u1 = 1.0 - rng.NextDouble();
        var u2 = rng.NextDouble();
        var z = Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
        return Math.Max(50, (long)Math.Round(payee.MedianCents * Math.Exp(payee.Spread * z)));
    }

    private sealed record PayeeSpec(
        string Name,
        double PerMonth,
        long MedianCents,
        double Spread,
        double[] AccountWeights,
        CategoryRule Rule,
        string[] Templates,
        int[]? Weekdays = null,
        bool FixedAmount = false,
        bool Inflow = false,
        double RefundRate = 0);

    private abstract record CategoryRule
    {
        public abstract string Pick(Random rng, long amount, int account);
    }

    private sealed record Single(string Category) : CategoryRule
    {
        public override string Pick(Random rng, long amount, int account) => Category;
    }

    private sealed record ByAmount(long ThresholdCents, string Below, string Above, double Purity) : CategoryRule
    {
        public override string Pick(Random rng, long amount, int account)
        {
            var main = amount < ThresholdCents ? Below : Above;
            var other = amount < ThresholdCents ? Above : Below;
            return rng.NextDouble() < Purity ? main : other;
        }
    }

    private sealed record ByAccount(string[] PerAccount, double Purity) : CategoryRule
    {
        public override string Pick(Random rng, long amount, int account)
        {
            var main = PerAccount[account];
            return rng.NextDouble() < Purity ? main : PerAccount.First(c => c != main);
        }
    }

    private sealed record ByChance(string First, string Second, double FirstShare) : CategoryRule
    {
        public override string Pick(Random rng, long amount, int account) => rng.NextDouble() < FirstShare ? First : Second;
    }
}
