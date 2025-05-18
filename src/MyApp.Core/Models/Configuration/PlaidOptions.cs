namespace MyApp.Core.Models.Configuration
{
    public class PlaidOptions
    {
        public const string Plaid = "Plaid"; // Section name in appsettings.json

        // Basic authentication and environment settings
        public string ClientId { get; set; } = string.Empty;
        public string Secret { get; set; } = string.Empty;
        public string Environment { get; set; } = string.Empty; // e.g., "sandbox", "development", "production"
        
        // Additional configuration
        public string ApiVersion { get; set; } = "2020-09-14"; // Plaid API version
        public string ClientName { get; set; } = "MyApp Personal Finance"; // Client name for Plaid Link
        
        // Feature flags
        public PlaidFeatureFlags Features { get; set; } = new PlaidFeatureFlags();
    }

    public class PlaidFeatureFlags
    {
        public bool EnableDirectDeposit { get; set; } = false;
        public bool EnableAccountLinking { get; set; } = true;
        public bool EnableTransactionSync { get; set; } = true;
        public bool EnableRefreshInterval { get; set; } = true;
        public int RefreshIntervalHours { get; set; } = 6;
    }
} 