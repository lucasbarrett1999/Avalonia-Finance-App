using System;

namespace MyApp.Core.Entities
{
    /// <summary>
    /// Represents a Plaid Item - a connection to a financial institution
    /// </summary>
    public class PlaidItem
    {
        /// <summary>
        /// Primary key for the item in our database
        /// </summary>
        public int Id { get; set; }

        /// <summary>
        /// Plaid's item_id for this connection
        /// </summary>
        public string ItemId { get; set; } = string.Empty;
        
        /// <summary>
        /// Encrypted access token for this item
        /// </summary>
        public string AccessToken { get; set; } = string.Empty;
        
        /// <summary>
        /// ID of the financial institution
        /// </summary>
        public string? InstitutionId { get; set; }
        
        /// <summary>
        /// Name of the financial institution
        /// </summary>
        public string? InstitutionName { get; set; }
        
        /// <summary>
        /// When the item was first created
        /// </summary>
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// When the item or its access token was last updated
        /// </summary>
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        /// <summary>
        /// When the item data was last accessed from Plaid
        /// </summary>
        public DateTime? LastAccessedAt { get; set; }
        
        /// <summary>
        /// When the item should be refreshed next
        /// </summary>
        public DateTime? NextRefreshScheduledAt { get; set; }
        
        /// <summary>
        /// If the item has an error condition
        /// </summary>
        public bool HasError { get; set; }
        
        /// <summary>
        /// Error code if the item has an error
        /// </summary>
        public string? ErrorCode { get; set; }
        
        /// <summary>
        /// Error message if the item has an error
        /// </summary>
        public string? ErrorMessage { get; set; }
        
        /// <summary>
        /// Available products for this item (comma-separated string)
        /// </summary>
        public string? AvailableProducts { get; set; }

        /// <summary>
        /// Whether this item is active (false if deleted)
        /// </summary>
        public bool IsActive { get; set; } = true;
        
        // Navigation property for accounts
        public System.Collections.Generic.ICollection<Account> Accounts { get; set; } = 
            new System.Collections.Generic.List<Account>();
    }
}