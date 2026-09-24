using System.Globalization;

namespace Keel.Domain.Rules;

/// <summary>
/// Display names used when describing rules and traces. Unknown ids are shown as a short id so a
/// description never fails.
/// </summary>
public sealed class RuleNames
{
    private readonly IReadOnlyDictionary<Guid, string> _categories;
    private readonly IReadOnlyDictionary<Guid, string> _accounts;

    /// <summary>Creates a lookup.</summary>
    /// <param name="categories">Category names by id.</param>
    /// <param name="accounts">Account names by id.</param>
    /// <param name="currency">Currency used to format amounts.</param>
    public RuleNames(IReadOnlyDictionary<Guid, string>? categories = null, IReadOnlyDictionary<Guid, string>? accounts = null, string currency = Keel.Domain.Currency.Default)
    {
        _categories = categories ?? new Dictionary<Guid, string>();
        _accounts = accounts ?? new Dictionary<Guid, string>();
        Currency = currency;
    }

    /// <summary>A lookup with no names (ids are shown) and USD amounts.</summary>
    public static RuleNames Empty { get; } = new();

    /// <summary>Currency used to format amounts.</summary>
    public string Currency { get; }

    /// <summary>The category's name, or a short id.</summary>
    public string Category(Guid? id) => id is not { } value
        ? "no category"
        : _categories.TryGetValue(value, out var name) ? name : ShortId(value);

    /// <summary>The account's name, or a short id.</summary>
    public string Account(Guid id) => _accounts.TryGetValue(id, out var name) ? name : ShortId(id);

    /// <summary>Formats minor units as an invariant number, e.g. <c>12.50</c>.</summary>
    public string FormatAmount(long minorUnits) => new Money(minorUnits, Currency).FormatNumber(CultureInfo.InvariantCulture);

    internal static string DescribeText(TextOperator op, string value) => op switch
    {
        TextOperator.Contains => $"contains \"{value}\"",
        TextOperator.EqualTo => $"is \"{value}\"",
        TextOperator.StartsWith => $"starts with \"{value}\"",
        TextOperator.Regex => $"matches /{value}/",
        _ => value,
    };

    internal static string JoinOr(IEnumerable<string> items)
    {
        var list = items.ToList();
        return list.Count switch
        {
            0 => "(none)",
            1 => list[0],
            _ => string.Join(", ", list.Take(list.Count - 1)) + " or " + list[^1],
        };
    }

    private static string ShortId(Guid id) => "#" + id.ToString("N", CultureInfo.InvariantCulture)[..8];
}
