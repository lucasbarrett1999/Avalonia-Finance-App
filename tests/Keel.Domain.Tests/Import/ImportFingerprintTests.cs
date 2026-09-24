using Keel.Domain.Import;

namespace Keel.Domain.Tests.Import;

public class ImportFingerprintTests
{
    private static readonly Guid Account = Guid.Parse("0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b");

    [Fact]
    public void Is_sha256_hex_of_the_pipe_joined_fields()
    {
        // printf '%s' '0190a1b2-c3d4-7e5f-8a9b-0c1d2e3f4a5b|2026-01-05|-1234|STARBUCKS' | sha256sum
        ImportFingerprint.Compute(Account, new DateOnly(2026, 1, 5), -1234, "STARBUCKS")
            .ShouldBe("3bf4c607f0b58d9b71b96e0ccf4c7dc7c128fd8d1035fa6a6faad49b37b46e0a");
    }

    [Fact]
    public void Differs_when_any_field_differs()
    {
        var baseline = ImportFingerprint.Compute(Account, new DateOnly(2026, 1, 5), -1234, "STARBUCKS");
        ImportFingerprint.Compute(Guid.Empty, new DateOnly(2026, 1, 5), -1234, "STARBUCKS").ShouldNotBe(baseline);
        ImportFingerprint.Compute(Account, new DateOnly(2026, 1, 6), -1234, "STARBUCKS").ShouldNotBe(baseline);
        ImportFingerprint.Compute(Account, new DateOnly(2026, 1, 5), 1234, "STARBUCKS").ShouldNotBe(baseline);
        ImportFingerprint.Compute(Account, new DateOnly(2026, 1, 5), -1234, "STARBUCKS COFFEE").ShouldNotBe(baseline);
    }

    [Fact]
    public void Is_64_lower_case_hex_characters()
    {
        var fp = ImportFingerprint.Compute(Account, DateOnly.MinValue, long.MinValue, "");
        fp.Length.ShouldBe(64);
        fp.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')).ShouldBeTrue();
    }
}
