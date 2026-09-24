using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Keel.Domain.Import;

/// <summary>
/// The exact-duplicate fingerprint of PRD 6.5 step 3:
/// <c>SHA-256(AccountId | Date | Amount | NormalizedPayee)</c> as lower-case hex.
/// </summary>
public static class ImportFingerprint
{
    /// <summary>
    /// Computes the fingerprint. The hashed text is
    /// <c>{accountId:D}|{date:yyyy-MM-dd}|{amount}|{normalizedPayee}</c> in UTF-8, with the
    /// amount in minor units and invariant-culture digits.
    /// </summary>
    /// <param name="accountId">Account the transaction belongs to.</param>
    /// <param name="date">Ledger date.</param>
    /// <param name="amount">Signed amount in minor units.</param>
    /// <param name="normalizedPayee">Output of <see cref="PayeeNormalizer.Normalize"/>.</param>
    /// <returns>64 lower-case hexadecimal characters.</returns>
    public static string Compute(Guid accountId, DateOnly date, long amount, string normalizedPayee)
    {
        ArgumentNullException.ThrowIfNull(normalizedPayee);
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{accountId:D}|{date:yyyy-MM-dd}|{amount}|{normalizedPayee}");
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }
}
