namespace Keel.Domain.Import;

/// <summary>
/// The data tables behind <see cref="PayeeNormalizer"/> (PRD 6.5): what US banks and card
/// processors add to a merchant descriptor that is not part of the merchant's name. Every entry
/// has a unit test in <c>PayeeNormalizerTests</c>.
/// </summary>
/// <remarks>
/// Normalized payees feed <see cref="ImportFingerprint"/>, which is stored with each imported
/// transaction. Changing these tables changes future fingerprints, so bump
/// <see cref="PayeeNormalizer.Version"/> whenever an entry is added, removed or edited.
/// All patterns run on ASCII-folded, upper-case text.
/// </remarks>
public static class PayeeNoiseTable
{
    /// <summary>
    /// Merchant rewrites, applied first and in order: (regular expression, replacement).
    /// They collapse the many descriptor spellings of one merchant to a single name.
    /// </summary>
    public static IReadOnlyList<(string Pattern, string Replacement)> Rewrites { get; } =
    [
        // Amazon: "AMZN Mktp US*2K4AB1CD2", "AMAZON MKTPL*RT4Y1", "Amazon.com*MK1AB2", "AMZN.COM/BILL".
        (@"\bAMZN\s*MKTP(?:LACE)?\b(?:\s+[A-Z]{2}\b)?(?:\s*\*\s*\S*)?", "AMAZON"),
        (@"\bAMAZON\s*(?:MKTPL|MKTPLACE|MARKETPLACE)\b(?:\s+[A-Z]{2}\b)?(?:\s*\*\s*\S*)?", "AMAZON"),
        (@"\bAMAZON\.COM\b(?:\s*\*\s*\S*)?", "AMAZON"),
        (@"\bAMZN\.COM/BILL\b", "AMAZON"),
        (@"\b(?:AMZN|AMAZON)\s+DIGITAL\b(?:\s*\*\s*\S*)?", "AMAZON DIGITAL"),
        (@"\b(?:AMZN|AMAZON)\s+PRIME\b(?:\s*\*\s*\S*)?", "AMAZON PRIME"),
        (@"\bPRIME\s+VIDEO\b(?:\s*\*\s*\S*)?", "PRIME VIDEO"),
        (@"\bAMZN\b", "AMAZON"),

        // Other merchants whose descriptors vary.
        (@"\bAPPLE\.COM/BILL\b", "APPLE"),
        (@"\bWM\s+SUPERCENTER\b", "WALMART"),
        (@"\bWAL-MART\b", "WALMART"),
        (@"\bWALMART\.COM\b", "WALMART"),
        (@"\bSQSP\s*\*\s*\S*", "SQUARESPACE"),
        (@"\bMSFT\s*\*\s*\S*", "MICROSOFT"),
        (@"\bCOSTCO\s+WHSE\b", "COSTCO"),
    ];

    /// <summary>
    /// Payment-processor and aggregator prefixes that end in <c>*</c> (for example
    /// <c>SQ *BLUE BOTTLE</c>, <c>TST* JOES PIZZA</c>, <c>PAYPAL *NETFLIX</c>). Removed only at the
    /// start of the descriptor, repeatedly, together with the asterisk.
    /// </summary>
    public static IReadOnlyList<string> ProcessorPrefixes { get; } =
    [
        "SQ",         // Square
        "TST",        // Toast
        "PAYPAL",     // PayPal
        "PP",         // PayPal (older descriptors)
        "SP",         // Shopify Payments
        "DD",         // DoorDash
        "IN",         // Intuit QuickBooks Payments
        "FS",         // FastSpring
        "EB",         // Eventbrite
        "WPY",        // WePay
        "WL",         // Valve / Steam wallet
        "DNH",        // GoDaddy
        "HLU",        // Hulu
        "IZ",         // iZettle
        "SUMUP",      // SumUp
        "PADDLE.NET", // Paddle
        "2CO.COM",    // 2Checkout
        "2CO",        // 2Checkout
    ];

    /// <summary>
    /// Multi-word channel phrases banks put around the merchant name. Removed anywhere as whole
    /// words, longest first.
    /// </summary>
    public static IReadOnlyList<string> NoisePhrases { get; } =
    [
        "RECURRING PAYMENT AUTHORIZED ON",
        "PURCHASE AUTHORIZED ON",
        "PURCHASE RETURN AUTHORIZED ON",
        "AUTHORIZED ON",
        "DEBIT CARD PURCHASE",
        "VISA CHECK CARD",
        "CHECK CARD PURCHASE",
        "CHECK CARD",
        "DEBIT CARD",
        "VISA DEBIT",
        "VISA PURCHASE",
        "MASTERCARD DEBIT",
        "MASTERCARD PURCHASE",
        "CARD PURCHASE",
        "POINT OF SALE",
        "ELECTRONIC PURCHASE",
        "RECURRING CHARGE",
    ];

    /// <summary>Single words that are channel noise wherever they appear.</summary>
    public static IReadOnlyList<string> NoiseTokens { get; } =
    [
        "POS",
        "DEBIT",
        "PURCHASE",
        "SQ",
        "TST",
        "CHECKCARD",
        "CHKCARD",
        "DBT",
        "CRD",
        "PIN",
        "NON-PIN",
        "DDA",
        "PUR",
        "ACH",
        "APLPAY",
        "GGLPAY",
        "RECURRING",
        "PREAUTHORIZED",
        "PRE-AUTHORIZED",
    ];

    /// <summary>
    /// Reference-number and card-number patterns removed anywhere (after rewrites): masked card
    /// numbers, <c>CARD 1234</c>, short dates, ACH company ids, reference and confirmation
    /// numbers, store numbers and web-address decoration.
    /// </summary>
    public static IReadOnlyList<string> RemovalPatterns { get; } =
    [
        @"\bCARD\s+(?:ENDING\s+(?:IN\s+)?)?(?:X+|\*+)?\d{4}\b",
        @"(?:\bX{2,}|\*{2,})\d{2,}\b",
        @"\b\d{1,2}/\d{1,2}(?:/\d{2,4})?\b",
        @"\b(?:PPD|CCD|WEB|TEL|CTX|IAT|ARC|BOC|POP|RCK)\s+ID:?\s*\S+",
        @"\bID:\s*\S+",
        @"\bREF(?:ERENCE)?\s*(?:#|NO\b\.?|NUM(?:BER)?\b)?\s*:?\s*[A-Z0-9-]*\d[A-Z0-9-]*",
        @"\bCONF(?:IRMATION)?\s*#\s*\S+",
        @"\bTRACE\s*#?\s*\d+",
        @"#\s*\d+\b",
        @"\bWWW\.",
        @"\.(?:COM|NET|ORG|IO|CO)\b",
        @"/BILL\b",
    ];
}
