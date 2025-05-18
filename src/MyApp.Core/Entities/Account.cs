using System;
using System.Collections.Generic;

namespace MyApp.Core.Entities
{
    public class Account
    {
        public int Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string Type { get; set; } = string.Empty; // e.g., Checking, Savings, Credit Card
        public decimal Balance { get; set; }
        public string? BankName { get; set; } // From Plaid or manually entered
        public string? PlaidAccountId { get; set; } // Identifier from Plaid
        public string? PlaidItemId { get; set; } // Identifier for the Plaid Item (connection)

        // Navigation property for related transactions
        public ICollection<Transaction> Transactions { get; set; } = new List<Transaction>();
    }
} 