using System.Text;
using System.Text.Unicode;

namespace Keel.Infrastructure.Import;

/// <summary>Decoded file text and the encoding that produced it.</summary>
/// <param name="Text">Decoded text without a byte-order mark.</param>
/// <param name="Encoding">Encoding used.</param>
/// <param name="UsedFallback">True when a declared or forced UTF-8 encoding did not fit and
/// Windows-1252 was used instead.</param>
internal readonly record struct DecodedText(string Text, Encoding Encoding, bool UsedFallback);

/// <summary>
/// Encoding sniffing for import files: byte-order marks (UTF-8, UTF-16 LE/BE), BOM-less UTF-16,
/// strict UTF-8, and Windows-1252 (or a declared single-byte code page) as the fallback.
/// </summary>
internal static class TextDecoder
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Windows-1252, the usual encoding of US bank exports that are not UTF-8.</summary>
    public static Encoding Windows1252 { get; } = CodePagesEncodingProvider.Instance.GetEncoding(1252)!;

    /// <summary>Decodes <paramref name="bytes"/>.</summary>
    /// <param name="bytes">Whole file.</param>
    /// <param name="forcedEncodingName">A web name that overrides detection, or null.</param>
    /// <param name="singleByteFallback">Code page for non-UTF-8 bytes; Windows-1252 when null.</param>
    public static DecodedText Decode(ReadOnlySpan<byte> bytes, string? forcedEncodingName = null, Encoding? singleByteFallback = null)
    {
        if (forcedEncodingName is not null && Resolve(forcedEncodingName) is { } forced)
        {
            var bom = BomLength(bytes, forced);
            if (forced is UTF8Encoding && !Utf8.IsValid(bytes[bom..]))
            {
                return new DecodedText(Windows1252.GetString(bytes[bom..]), Windows1252, UsedFallback: true);
            }

            return new DecodedText(forced.GetString(bytes[bom..]), forced, UsedFallback: false);
        }

        var detected = Detect(bytes, out var bomLength, singleByteFallback);
        return new DecodedText(detected.GetString(bytes[bomLength..]), detected, UsedFallback: false);
    }

    /// <summary>Detects the encoding of <paramref name="bytes"/> (see the type remarks).</summary>
    public static Encoding Detect(ReadOnlySpan<byte> bytes, out int bomLength, Encoding? singleByteFallback = null)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            bomLength = 3;
            return StrictUtf8;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            bomLength = 2;
            return Encoding.Unicode;
        }

        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            bomLength = 2;
            return Encoding.BigEndianUnicode;
        }

        bomLength = 0;
        if (LooksLikeUtf16(bytes, out var bigEndian))
        {
            return bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode;
        }

        return Utf8.IsValid(bytes) ? StrictUtf8 : singleByteFallback ?? Windows1252;
    }

    /// <summary>Resolves a web name or a common alias (<c>1252</c>, <c>USASCII</c>, <c>8859-1</c>) to an encoding.</summary>
    public static Encoding? Resolve(string name)
    {
        var key = name.Trim().ToUpperInvariant().Replace("_", "-", StringComparison.Ordinal);
        switch (key)
        {
            case "UTF-8" or "UTF8":
                return StrictUtf8;
            case "UTF-16" or "UTF-16LE" or "UNICODE":
                return Encoding.Unicode;
            case "UTF-16BE":
                return Encoding.BigEndianUnicode;
            case "1252" or "WINDOWS-1252" or "CP1252" or "NONE" or "USASCII" or "US-ASCII" or "ASCII":
                return Windows1252; // a superset of ASCII; banks that say ASCII often send 1252
            case "8859-1" or "ISO-8859-1" or "LATIN1" or "LATIN-1":
                return Encoding.Latin1;
        }

        try
        {
            return CodePagesEncodingProvider.Instance.GetEncoding(key) ?? Encoding.GetEncoding(key);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>Decodes a short head of a file for sniffing; never throws.</summary>
    public static string DecodeHead(ReadOnlySpan<byte> head)
    {
        var encoding = Detect(head, out var bom);
        return encoding is UTF8Encoding
            ? Encoding.UTF8.GetString(head[bom..]) // lenient: the head may end mid-character
            : encoding.GetString(head[bom..]);
    }

    private static int BomLength(ReadOnlySpan<byte> bytes, Encoding encoding)
    {
        var preamble = encoding.Preamble;
        return preamble.Length > 0 && bytes.StartsWith(preamble) ? preamble.Length : 0;
    }

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> bytes, out bool bigEndian)
    {
        bigEndian = false;
        var sample = bytes[..(Math.Min(bytes.Length, 4096) & ~1)];
        if (sample.Length < 4)
        {
            return false;
        }

        int evenZeros = 0, oddZeros = 0;
        for (var i = 0; i < sample.Length; i += 2)
        {
            evenZeros += sample[i] == 0 ? 1 : 0;
            oddZeros += sample[i + 1] == 0 ? 1 : 0;
        }

        var pairs = sample.Length / 2;
        if (oddZeros * 10 > pairs * 4 && evenZeros * 20 < pairs)
        {
            return true;
        }

        if (evenZeros * 10 > pairs * 4 && oddZeros * 20 < pairs)
        {
            bigEndian = true;
            return true;
        }

        return false;
    }
}
