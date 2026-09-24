using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Numerics;
using Keel.Domain.Import;
using Keel.Domain.Rules;

namespace Keel.Domain.Categorization;

/// <summary>
/// The learner's features of one transaction (ADR 0021): the exact normalized payee, its words,
/// a log-scale amount bucket, the account, the day of week and the direction.
/// </summary>
internal sealed record LearnerFeatures(string Payee, string[] Tokens, int AmountBucket, Guid AccountId, int Weekday, int Direction)
{
    // Words that carry no merchant identity. Everything else PayeeNormalizer already removed.
    private static readonly FrozenSet<string> StopWords = new[] { "THE", "AND", "OF", "FOR", "TO", "AT", "INC", "LLC", "LTD", "CO", "CORP", "COMPANY", "COM" }.ToFrozenSet(StringComparer.Ordinal);

    public static LearnerFeatures Extract(TransactionSnapshot snapshot, Dictionary<string, string>? normalizeCache = null)
    {
        var text = snapshot.EffectivePayee ?? string.Empty;
        string payee;
        if (normalizeCache is null)
        {
            payee = PayeeNormalizer.Normalize(text);
        }
        else if (!normalizeCache.TryGetValue(text, out payee!))
        {
            payee = PayeeNormalizer.Normalize(text);
            normalizeCache[text] = payee;
        }

        return new LearnerFeatures(
            payee,
            Tokenize(payee),
            AmountBucketOf(snapshot.Amount),
            snapshot.AccountId,
            (int)snapshot.Date.DayOfWeek,
            Math.Sign(snapshot.Amount));
    }

    /// <summary>Distinct payee words of at least two characters containing a letter, minus stop words, in ordinal order.</summary>
    public static string[] Tokenize(string normalizedPayee)
    {
        if (normalizedPayee.Length == 0)
        {
            return [];
        }

        var tokens = normalizedPayee
            .Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => t.Length >= 2 && t.Any(char.IsAsciiLetter) && !StopWords.Contains(t))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        Array.Sort(tokens, StringComparer.Ordinal);
        return tokens;
    }

    /// <summary>
    /// Log-scale bucket of the magnitude: 0 for zero, otherwise 1 + floor(log2(|minor units|)).
    /// Each bucket spans a doubling ($10.24 to $20.47 is one bucket). Integer-only, so identical on
    /// every platform.
    /// </summary>
    public static int AmountBucketOf(long amount)
    {
        if (amount == 0)
        {
            return 0;
        }

        var magnitude = amount == long.MinValue ? (ulong)long.MaxValue + 1 : (ulong)Math.Abs(amount);
        return 1 + BitOperations.Log2(magnitude);
    }
}

/// <summary>Immutable counts per (feature value, category).</summary>
internal sealed class CountTable<TKey>
    where TKey : notnull
{
    private static readonly ImmutableDictionary<Guid, int> EmptyRow = ImmutableDictionary<Guid, int>.Empty;

    public CountTable(ImmutableDictionary<TKey, ImmutableDictionary<Guid, int>> rows)
    {
        Rows = rows;
    }

    public ImmutableDictionary<TKey, ImmutableDictionary<Guid, int>> Rows { get; }

    /// <summary>Number of distinct feature values seen.</summary>
    public int Count => Rows.Count;

    public static CountTable<TKey> Build(Dictionary<TKey, Dictionary<Guid, int>> rows, IEqualityComparer<TKey>? comparer = null)
    {
        var builder = ImmutableDictionary.CreateBuilder<TKey, ImmutableDictionary<Guid, int>>(comparer);
        foreach (var (key, row) in rows)
        {
            builder[key] = row.ToImmutableDictionary();
        }

        return new CountTable<TKey>(builder.ToImmutable());
    }

    public ImmutableDictionary<Guid, int>? Row(TKey key) => Rows.TryGetValue(key, out var row) ? row : null;

    /// <summary>Adds <paramref name="delta"/> to one cell; a cell or row that reaches zero is removed.</summary>
    public CountTable<TKey> Add(TKey key, Guid category, int delta)
    {
        var row = Rows.TryGetValue(key, out var existing) ? existing : EmptyRow;
        var value = row.GetValueOrDefault(category) + delta;
        if (value < 0)
        {
            throw new InvalidOperationException("The example is not part of the model.");
        }

        row = value == 0 ? row.Remove(category) : row.SetItem(category, value);
        return new CountTable<TKey>(row.IsEmpty ? Rows.Remove(key) : Rows.SetItem(key, row));
    }
}

/// <summary>Mutable counterpart of <see cref="CountTable{TKey}"/> for training.</summary>
internal sealed class CountTableBuilder<TKey>(IEqualityComparer<TKey>? comparer = null)
    where TKey : notnull
{
    private readonly Dictionary<TKey, Dictionary<Guid, int>> _rows = new(comparer);

    public void Add(TKey key, Guid category)
    {
        if (!_rows.TryGetValue(key, out var row))
        {
            row = [];
            _rows[key] = row;
        }

        row[category] = row.GetValueOrDefault(category) + 1;
    }

    public CountTable<TKey> Build() => CountTable<TKey>.Build(_rows, _rows.Comparer);
}
