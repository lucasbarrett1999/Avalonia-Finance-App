using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MyApp.Core.Models.Configuration;

namespace MyApp.Infrastructure.Services
{
    /// <summary>
    /// Validates Plaid configuration settings to ensure all required values are present
    /// </summary>
    public class PlaidConfigurationValidator
    {
        private readonly ILogger<PlaidConfigurationValidator> _logger;
        private readonly PlaidOptions _options;

        public PlaidConfigurationValidator(
            IOptions<PlaidOptions> options,
            ILogger<PlaidConfigurationValidator> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        }

        /// <summary>
        /// Validates the Plaid configuration and returns a list of any validation errors
        /// </summary>
        /// <returns>A list of validation error messages, or an empty list if valid</returns>
        public List<string> Validate()
        {
            var errors = new List<string>();

            // Validate required fields
            if (string.IsNullOrWhiteSpace(_options.ClientId))
            {
                errors.Add("Plaid:ClientId is required");
                _logger.LogError("Plaid:ClientId is missing in configuration");
            }

            if (string.IsNullOrWhiteSpace(_options.Secret))
            {
                errors.Add("Plaid:Secret is required");
                _logger.LogError("Plaid:Secret is missing in configuration");
            }

            if (string.IsNullOrWhiteSpace(_options.Environment))
            {
                errors.Add("Plaid:Environment is required");
                _logger.LogError("Plaid:Environment is missing in configuration");
            }
            else
            {
                // Validate environment value
                var env = _options.Environment.ToLowerInvariant();
                if (env != "sandbox" && env != "development" && env != "production")
                {
                    errors.Add($"Plaid:Environment value '{_options.Environment}' is invalid. Must be 'sandbox', 'development', or 'production'");
                    _logger.LogError("Plaid:Environment value '{Environment}' is invalid. Must be 'sandbox', 'development', or 'production'", _options.Environment);
                }
            }

            // Validate feature flag configurations when enabled
            if (_options.Features.EnableRefreshInterval && _options.Features.RefreshIntervalHours <= 0)
            {
                errors.Add("Plaid:Features:RefreshIntervalHours must be greater than 0 when EnableRefreshInterval is true");
                _logger.LogError("Plaid:Features:RefreshIntervalHours is invalid. Value must be greater than 0");
            }

            // Validate Client Name for Plaid Link
            if (string.IsNullOrWhiteSpace(_options.ClientName))
            {
                errors.Add("Plaid:ClientName is required for Plaid Link");
                _logger.LogWarning("Plaid:ClientName is missing, will use default value");
            }

            if (errors.Count > 0)
            {
                _logger.LogWarning("Plaid configuration validation found {Count} errors", errors.Count);
            }
            else
            {
                _logger.LogInformation("Plaid configuration validation passed");
            }

            return errors;
        }

        /// <summary>
        /// Validates the configuration and throws an exception if invalid
        /// </summary>
        /// <exception cref="InvalidOperationException">Thrown when the configuration is invalid</exception>
        public void ValidateAndThrow()
        {
            var errors = Validate();
            if (errors.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Plaid configuration is invalid. Errors: {string.Join(", ", errors)}");
            }
        }
    }
}