using System.Globalization;
using System.Text;

namespace Keel.Infrastructure.Import.Ofx;

/// <summary>An OFX element: an aggregate with children, or a leaf with a value.</summary>
internal sealed class OfxElement(string name, int line)
{
    public string Name { get; } = name;

    public int Line { get; } = line;

    public string? Value { get; set; }

    public List<OfxElement> Children { get; } = [];

    /// <summary>Value of the first direct child leaf with this name.</summary>
    public string? Leaf(string childName) =>
        Children.FirstOrDefault(c => c.Value is not null && c.Name == childName)?.Value;

    /// <summary>First direct child with this name.</summary>
    public OfxElement? Child(string childName) => Children.FirstOrDefault(c => c.Name == childName);

    /// <summary>All descendants (depth first, document order) with one of these names.</summary>
    public IEnumerable<OfxElement> Descendants(params string[] names)
    {
        foreach (var child in Children)
        {
            if (names.Contains(child.Name))
            {
                yield return child;
            }

            foreach (var nested in child.Descendants(names))
            {
                yield return nested;
            }
        }
    }
}

/// <summary>A parsed OFX file: header fields and the element tree under a synthetic root.</summary>
internal sealed record OfxDocument(IReadOnlyDictionary<string, string> Header, OfxElement Root, bool IsXml, IReadOnlyList<string> Problems);

/// <summary>
/// A tolerant reader for both OFX 1.x (SGML: leaf tags are not closed) and OFX 2.x (XML).
/// It survives what banks actually send: missing aggregate end tags, a missing <c>&lt;/OFX&gt;</c>,
/// CRLF or LF, empty SGML leaves, stray end tags and unescaped ampersands.
/// </summary>
internal static class OfxReader
{
    // Aggregates in the banking and credit-card parts of the OFX spec. Unknown tags without a
    // value and without an immediate end tag are treated as empty leaves, which keeps a bad
    // empty SGML leaf from swallowing its siblings.
    private static readonly HashSet<string> KnownAggregates = new(StringComparer.Ordinal)
    {
        "OFX", "SIGNONMSGSRSV1", "SONRS", "STATUS", "FI", "BANKMSGSRSV1", "CREDITCARDMSGSRSV1",
        "STMTTRNRS", "STMTRS", "CCSTMTTRNRS", "CCSTMTRS", "BANKACCTFROM", "CCACCTFROM", "BANKACCTTO",
        "CCACCTTO", "BANKTRANLIST", "STMTTRN", "CCSTMTTRN", "LEDGERBAL", "AVAILBAL", "PAYEE", "CURRENCY",
        "ORIGCURRENCY", "BALLIST", "BAL", "MKTGINFO", "INVSTMTMSGSRSV1", "INVSTMTTRNRS", "INVSTMTRS",
    };

    /// <summary>Reads the header and element tree from decoded text.</summary>
    public static OfxDocument Read(string text)
    {
        var problems = new List<string>();
        var start = text.IndexOf("<OFX", StringComparison.OrdinalIgnoreCase);
        var isXml = text.TrimStart().StartsWith("<?xml", StringComparison.OrdinalIgnoreCase)
            || text.Contains("<?OFX", StringComparison.OrdinalIgnoreCase);
        var header = ReadHeader(start < 0 ? text : text[..start], isXml);
        var root = new OfxElement("#root", 1);
        if (start < 0)
        {
            problems.Add("No <OFX> element was found.");
            return new OfxDocument(header, root, isXml, problems);
        }

        var lines = new LineIndex(text);
        var stack = new List<OfxElement> { root };
        var i = start;
        while (i < text.Length)
        {
            var open = text.IndexOf('<', i);
            if (open < 0)
            {
                break;
            }

            if (string.CompareOrdinal(text, open, "<!--", 0, 4) == 0)
            {
                var endComment = text.IndexOf("-->", open + 4, StringComparison.Ordinal);
                i = endComment < 0 ? text.Length : endComment + 3;
                continue;
            }

            var close = text.IndexOf('>', open + 1);
            if (close < 0)
            {
                problems.Add("The file ends inside a tag.");
                break;
            }

            var tag = text[(open + 1)..close].Trim();
            i = close + 1;
            if (tag.Length == 0 || tag[0] is '?' or '!')
            {
                continue;
            }

            if (tag[0] == '/')
            {
                var endName = NameOf(tag[1..]);
                var index = stack.FindLastIndex(e => e.Name == endName);
                if (index > 0)
                {
                    stack.RemoveRange(index, stack.Count - index);
                }

                continue;
            }

            var selfClosing = tag.EndsWith('/');
            var name = NameOf(selfClosing ? tag[..^1] : tag);
            var element = new OfxElement(name, lines.LineOf(open));
            if (selfClosing)
            {
                element.Value = string.Empty;
                stack[^1].Children.Add(element);
                continue;
            }

            var nextOpen = text.IndexOf('<', i);
            var rawValue = nextOpen < 0 ? text[i..] : text[i..nextOpen];
            var value = rawValue.Trim();
            var afterEnd = 0;
            var closesImmediately = nextOpen >= 0 && IsEndTag(text, nextOpen, name, out afterEnd);
            if (value.Length > 0 || closesImmediately || !KnownAggregates.Contains(name))
            {
                element.Value = DecodeEntities(value);
                stack[^1].Children.Add(element);
                i = closesImmediately ? afterEnd : (nextOpen < 0 ? text.Length : nextOpen);
                continue;
            }

            // An aggregate. OFX aggregates never nest in themselves, so a repeated open tag
            // closes the previous one (a bank that forgot </STMTTRN>).
            var sameIndex = stack.FindLastIndex(e => e.Name == name);
            if (sameIndex > 0)
            {
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"<{name}> was not closed."));
                stack.RemoveRange(sameIndex, stack.Count - sameIndex);
            }

            stack[^1].Children.Add(element);
            stack.Add(element);
        }

        // A missing </OFX> alone is common and harmless; anything still open inside it is not.
        if (stack.Count > 2)
        {
            problems.Add(string.Create(CultureInfo.InvariantCulture, $"The file ends before </{stack[^1].Name}>."));
        }

        return new OfxDocument(header, root, isXml, problems);
    }

    private static string NameOf(string tag)
    {
        var end = 0;
        while (end < tag.Length && !char.IsWhiteSpace(tag[end]))
        {
            end++;
        }

        return tag[..end].ToUpperInvariant();
    }

    private static bool IsEndTag(string text, int at, string name, out int afterEnd)
    {
        afterEnd = at;
        if (at + 1 >= text.Length || text[at + 1] != '/')
        {
            return false;
        }

        var close = text.IndexOf('>', at);
        if (close < 0 || NameOf(text[(at + 2)..close].Trim()) != name)
        {
            return false;
        }

        afterEnd = close + 1;
        return true;
    }

    private static Dictionary<string, string> ReadHeader(string prefix, bool isXml)
    {
        var header = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (isXml)
        {
            // <?xml version="1.0" encoding="UTF-8"?> and <?OFX OFXHEADER="200" VERSION="211" ...?>
            foreach (var pi in prefix.Split("<?", StringSplitOptions.RemoveEmptyEntries))
            {
                foreach (var part in pi.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    var eq = part.IndexOf('=');
                    if (eq > 0)
                    {
                        header.TryAdd(part[..eq].Trim().ToUpperInvariant(), part[(eq + 1)..].Trim().TrimEnd('?', '>').Trim('"', '\''));
                    }
                }
            }

            return header;
        }

        foreach (var line in prefix.Split('\n'))
        {
            var colon = line.IndexOf(':');
            if (colon > 0)
            {
                header.TryAdd(line[..colon].Trim().ToUpperInvariant(), line[(colon + 1)..].Trim());
            }
        }

        return header;
    }

    /// <summary>Decodes the XML/SGML character entities banks use; stray <c>&amp;</c> is kept as is.</summary>
    public static string DecodeEntities(string value)
    {
        if (!value.Contains('&'))
        {
            return value;
        }

        var sb = new StringBuilder(value.Length);
        var i = 0;
        while (i < value.Length)
        {
            var semicolon = value[i] == '&' ? value.IndexOf(';', i) : -1;
            if (semicolon > i && semicolon - i <= 10 && TryEntity(value[(i + 1)..semicolon], out var decoded))
            {
                sb.Append(decoded);
                i = semicolon + 1;
            }
            else
            {
                sb.Append(value[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    private static bool TryEntity(string entity, out string decoded)
    {
        decoded = entity switch
        {
            "amp" => "&",
            "lt" => "<",
            "gt" => ">",
            "quot" => "\"",
            "apos" => "'",
            "nbsp" => " ",
            _ => string.Empty,
        };
        if (decoded.Length > 0)
        {
            return true;
        }

        if (entity.StartsWith('#'))
        {
            var isHex = entity.Length > 1 && entity[1] is 'x' or 'X';
            var digits = isHex ? entity[2..] : entity[1..];
            if (int.TryParse(digits, isHex ? NumberStyles.HexNumber : NumberStyles.None, CultureInfo.InvariantCulture, out var code)
                && code is > 0 and <= 0x10FFFF && (code < 0xD800 || code > 0xDFFF))
            {
                decoded = char.ConvertFromUtf32(code);
                return true;
            }
        }

        return false;
    }

    /// <summary>Maps character offsets to one-based line numbers.</summary>
    private sealed class LineIndex
    {
        private readonly List<int> _starts = [0];

        public LineIndex(string text)
        {
            for (var i = 0; i < text.Length; i++)
            {
                if (text[i] == '\n')
                {
                    _starts.Add(i + 1);
                }
            }
        }

        public int LineOf(int offset)
        {
            var index = _starts.BinarySearch(offset);
            return (index >= 0 ? index : ~index - 1) + 1;
        }
    }
}
