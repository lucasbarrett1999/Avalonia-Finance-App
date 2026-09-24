namespace Keel.Domain.Entities;

/// <summary>
/// Stable identifiers for rows every budget file contains. They are seeded by the initial
/// migration so that system rows can be found without relying on (renameable) names.
/// </summary>
public static class SystemIds
{
    /// <summary>The default <see cref="Profile"/> (household features are P2).</summary>
    public static readonly Guid DefaultProfile = new("00000000-0000-7000-8000-000000000001");

    /// <summary>The system "Inflow" <see cref="CategoryGroup"/>.</summary>
    public static readonly Guid InflowGroup = new("00000000-0000-7000-8000-000000000010");

    /// <summary>The system "Credit Card Payments" <see cref="CategoryGroup"/>.</summary>
    public static readonly Guid CreditCardPaymentsGroup = new("00000000-0000-7000-8000-000000000011");

    /// <summary>The system "Ready to Assign" <see cref="Category"/> inside the Inflow group.</summary>
    public static readonly Guid ReadyToAssignCategory = new("00000000-0000-7000-8000-000000000020");
}
