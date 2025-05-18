using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MyApp.Core.Interfaces;

namespace MyApp.Infrastructure.Services
{
    /// <summary>
    /// Implementation of IAuditLogger that logs to a file
    /// </summary>
    public class FileAuditLogger : IAuditLogger
    {
        private readonly ILogger<FileAuditLogger> _logger;
        private readonly string _logFilePath;
        private static readonly object _lockObject = new object();

        public FileAuditLogger(ILogger<FileAuditLogger> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            
            // Create logs directory if it doesn't exist
            string logDirectory = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "logs");
            Directory.CreateDirectory(logDirectory);
            
            // Set log file path
            _logFilePath = Path.Combine(logDirectory, "plaid_audit.log");
            
            _logger.LogInformation("Audit logging to: {LogFilePath}", _logFilePath);
        }

        /// <summary>
        /// Logs a token access event
        /// </summary>
        public Task LogTokenAccessAsync(string itemId, string operation)
        {
            string message = $"{DateTime.UtcNow:u} | ACCESS | Item: {itemId} | Operation: {operation}";
            return LogMessageAsync(message);
        }

        /// <summary>
        /// Logs a token management event
        /// </summary>
        public Task LogTokenManagementAsync(string itemId, string operation)
        {
            string message = $"{DateTime.UtcNow:u} | MANAGEMENT | Item: {itemId} | Operation: {operation}";
            return LogMessageAsync(message);
        }

        /// <summary>
        /// Logs a token error event
        /// </summary>
        public Task LogTokenErrorAsync(string itemId, string errorCode, string errorMessage)
        {
            string message = $"{DateTime.UtcNow:u} | ERROR | Item: {itemId} | Code: {errorCode} | Message: {errorMessage}";
            return LogMessageAsync(message);
        }

        /// <summary>
        /// Writes a message to the audit log file
        /// </summary>
        private Task LogMessageAsync(string message)
        {
            try
            {
                // Use a lock to prevent concurrent file access issues
                lock (_lockObject)
                {
                    // Append to the log file
                    File.AppendAllText(_logFilePath, message + Environment.NewLine);
                }
                
                return Task.CompletedTask;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing to audit log");
                return Task.CompletedTask; // Don't throw exceptions for logging failures
            }
        }
    }
}