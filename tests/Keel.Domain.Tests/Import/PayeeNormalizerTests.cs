using Keel.Domain.Import;

namespace Keel.Domain.Tests.Import;

public class PayeeNormalizerTests
{
    [Theory]
    [InlineData("  Trader   Joe's  #552 ", "TRADER JOES")]
    [InlineData("Café Crème", "CAFE CREME")]
    [InlineData("Straße Bäckerei Øst", "STRASSE BACKEREI OST")]
    [InlineData("Joe’s – Diner", "JOES DINER")]
    [InlineData("POS DEBIT 1234 STARBUCKS #5678 SEATTLE", "STARBUCKS SEATTLE")]
    [InlineData("SQ *BLUE BOTTLE COFFEE", "BLUE BOTTLE COFFEE")]
    [InlineData("TST* JOE'S PIZZA", "JOES PIZZA")]
    [InlineData("PAYPAL *NETFLIX", "NETFLIX")]
    [InlineData("SQ *PAYPAL *ETSY", "ETSY")]
    [InlineData("AMZN Mktp US*2K4AB1CD2", "AMAZON")]
    [InlineData("SHELL OIL 57444212500", "SHELL OIL")]
    [InlineData("NETFLIX 123", "NETFLIX 123")]
    [InlineData("PURCHASE AUTHORIZED ON 01/05 STARBUCKS STORE 01234 SEATTLE WA S466005123456789 CARD 1234", "STARBUCKS STORE SEATTLE WA")]
    [InlineData("CHECKCARD 0105 SHELL OIL 12345678", "SHELL OIL")]
    [InlineData("HLU*HULU 12345-U", "HULU")]
    [InlineData("DD *DOORDASH CHIPOTLE", "DOORDASH CHIPOTLE")]
    [InlineData("GOOGLE *YouTube", "GOOGLE YOUTUBE")]
    [InlineData("ACH DEBIT NETFLIX", "NETFLIX")]
    [InlineData("VISA DEBIT STARBUCKS XXXXXX1234", "STARBUCKS")]
    [InlineData("APLPAY STARBUCKS", "STARBUCKS")]
    [InlineData("7-ELEVEN 12345", "7-ELEVEN")]
    [InlineData("H&M 0123", "H&M")]
    [InlineData("PAYPAL TRANSFER", "PAYPAL TRANSFER")]
    [InlineData("Zelle payment to JOHN CONF# ab12cd", "ZELLE PAYMENT TO JOHN")]
    [InlineData("starbucks", "STARBUCKS")]
    public void Normalizes_descriptors(string raw, string expected)
    {
        PayeeNormalizer.Normalize(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Empty_input_yields_empty(string? raw)
    {
        PayeeNormalizer.Normalize(raw).ShouldBe(string.Empty);
    }

    [Theory]
    [InlineData("POS DEBIT 12345", "POS DEBIT 12345")]
    [InlineData("  debit   purchase ", "DEBIT PURCHASE")]
    public void All_noise_falls_back_to_the_collapsed_descriptor(string raw, string expected)
    {
        PayeeNormalizer.Normalize(raw).ShouldBe(expected);
    }

    [Theory]
    [InlineData("AMZN Mktp US*2K4AB1CD2", "AMAZON")]
    [InlineData("AMZN MKTP US", "AMAZON")]
    [InlineData("AMAZON MKTPL*RT4Y12AB3", "AMAZON")]
    [InlineData("Amazon Marketplace", "AMAZON")]
    [InlineData("Amazon.com*MK1AB2CD3", "AMAZON")]
    [InlineData("AMZN.COM/BILL WA", "AMAZON WA")]
    [InlineData("AMZN Digital*1A2B3C4D5", "AMAZON DIGITAL")]
    [InlineData("Amazon Prime*2B3C4D5E6", "AMAZON PRIME")]
    [InlineData("Prime Video*3C4D5E6F7", "PRIME VIDEO")]
    [InlineData("AMZN RETAIL", "AMAZON RETAIL")]
    [InlineData("APPLE.COM/BILL 866-712-7753", "APPLE")]
    [InlineData("WM SUPERCENTER #1234", "WALMART")]
    [InlineData("WAL-MART #1234", "WALMART")]
    [InlineData("WALMART.COM 8009666546", "WALMART")]
    [InlineData("SQSP* INV12345678", "SQUARESPACE")]
    [InlineData("MSFT * E0300ABCDE", "MICROSOFT")]
    [InlineData("COSTCO WHSE #0123", "COSTCO")]
    public void Applies_every_merchant_rewrite(string raw, string expected)
    {
        PayeeNormalizer.Normalize(raw).ShouldBe(expected);
    }

    [Fact]
    public void Every_rewrite_rule_has_a_test_case()
    {
        // Keep Applies_every_merchant_rewrite in step with the table.
        PayeeNoiseTable.Rewrites.Count.ShouldBe(15);
    }

    public static TheoryData<string> ProcessorPrefixes() => new(PayeeNoiseTable.ProcessorPrefixes);

    [Theory]
    [MemberData(nameof(ProcessorPrefixes))]
    public void Removes_every_processor_prefix(string prefix)
    {
        PayeeNormalizer.Normalize($"{prefix} *ACME HARDWARE").ShouldBe("ACME HARDWARE");
        PayeeNormalizer.Normalize($"{prefix}*ACME HARDWARE").ShouldBe("ACME HARDWARE");
        PayeeNormalizer.Normalize($"{prefix.ToLowerInvariant()}* Acme Hardware").ShouldBe("ACME HARDWARE");
    }

    public static TheoryData<string> NoiseTokens() => new(PayeeNoiseTable.NoiseTokens);

    [Theory]
    [MemberData(nameof(NoiseTokens))]
    public void Removes_every_noise_token_anywhere(string token)
    {
        PayeeNormalizer.Normalize($"{token} ACME HARDWARE").ShouldBe("ACME HARDWARE");
        PayeeNormalizer.Normalize($"ACME {token} HARDWARE").ShouldBe("ACME HARDWARE");
        PayeeNormalizer.Normalize($"ACME HARDWARE {token}").ShouldBe("ACME HARDWARE");
    }

    public static TheoryData<string> NoisePhrases() => new(PayeeNoiseTable.NoisePhrases);

    [Theory]
    [MemberData(nameof(NoisePhrases))]
    public void Removes_every_noise_phrase(string phrase)
    {
        PayeeNormalizer.Normalize($"{phrase} ACME HARDWARE").ShouldBe("ACME HARDWARE");
        PayeeNormalizer.Normalize($"ACME HARDWARE {phrase.ToLowerInvariant()}").ShouldBe("ACME HARDWARE");
    }

    [Theory]
    [InlineData("ACME HARDWARE CARD 1234", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE CARD ENDING IN XXXX1234", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE XXXXXXXXXXXX1234", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE ****1234", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE 01/05", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE 1/5/26", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE PPD ID: 1234567890", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE WEB ID: 9876543210", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE ID: XYZ123", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE REF #A12B", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE REF NO. 12-34", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE CONF# AB12CD", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE TRACE #021000021", "ACME HARDWARE")]
    [InlineData("ACME HARDWARE # 12", "ACME HARDWARE")]
    [InlineData("WWW.ACMEHARDWARE.COM", "ACMEHARDWARE")]
    [InlineData("ACMEHARDWARE.NET", "ACMEHARDWARE")]
    [InlineData("ACMEHARDWARE.COM/BILL", "ACMEHARDWARE")]
    public void Applies_every_removal_pattern(string raw, string expected)
    {
        PayeeNoiseTable.RemovalPatterns.Count.ShouldBe(12);
        PayeeNormalizer.Normalize(raw).ShouldBe(expected);
    }

    [Fact]
    public void Is_idempotent_on_its_own_output()
    {
        string[] samples =
        [
            "POS DEBIT 1234 STARBUCKS #5678 SEATTLE", "SQ *BLUE BOTTLE", "AMZN Mktp US*2K4AB1CD2",
            "Café Crème", "PURCHASE AUTHORIZED ON 01/05 SHELL OIL 12345678 CARD 9999",
        ];
        foreach (var s in samples)
        {
            var once = PayeeNormalizer.Normalize(s);
            PayeeNormalizer.Normalize(once).ShouldBe(once);
        }
    }

    [Fact]
    public void Output_is_upper_case_ascii_with_single_spaces()
    {
        var result = PayeeNormalizer.Normalize("  Ünïcödé\tMërchant  «Deluxe»  ");
        result.ShouldBe("UNICODE MERCHANT DELUXE");
        result.All(c => c < 128).ShouldBeTrue();
    }
}
