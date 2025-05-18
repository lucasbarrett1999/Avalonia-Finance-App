using System;

namespace MyApp.Core.Entities
{
    public enum TransactionType
    {
        Income,
        Expense,
        Transfer
    }

    public class Transaction
    {
        public int Id { get; set; }
        public DateTime Date { get; set; }
        public string Payee { get; set; } = string.Empty;
        public string? Description { get; set; }
        public decimal Amount { get; set; }
        public TransactionType Type { get; set; }
        public bool IsCleared { get; set; }

        // Foreign Key for Account
        public int AccountId { get; set; }
        public Account? Account { get; set; }

        // Foreign Key for Category (optional)
        public int? CategoryId { get; set; }
        public Category? Category { get; set; }
        
        public string? PlaidTransactionId { get; set; } // Identifier from Plaid
    }
} 