using System;
using System.Threading.Tasks;

namespace MyApp.Core.Interfaces
{
    /// <summary>
    /// Service for logging audit events
    /// </summary>
    public interface IAuditLogger
    {
        /// <summary>
        /// Logs a token access event
        /// </summary>
        Task LogTokenAccessAsync(string itemId, string operation);
        
        /// <summary>
        /// Logs a token management event (creation, rotation, deletion)
        /// </summary>
        Task LogTokenManagementAsync(string itemId, string operation);
        
        /// <summary>
        /// Logs a token error event
        /// </summary>
        Task LogTokenErrorAsync(string itemId, string errorCode, string errorMessage);
    }
}