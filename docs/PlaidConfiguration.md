# Plaid Configuration Guide

This document explains how to configure and use the Plaid integration in MyApp.

## Environment Configuration

MyApp supports different environment configurations for Plaid:

- **Development** - Uses Plaid Sandbox environment (fake data)
- **Staging** - Uses Plaid Development environment (real data with test credentials)
- **Production** - Uses Plaid Production environment (real data with production credentials)

### Setting the Environment

You can set the application environment using the `ASPNETCORE_ENVIRONMENT` environment variable:

```bash
# On macOS/Linux
export ASPNETCORE_ENVIRONMENT=Development
dotnet run

# On Windows PowerShell
$env:ASPNETCORE_ENVIRONMENT="Development"
dotnet run

# Using our helper script
./scripts/set-environment.sh Development
```

## Configuration Files

The Plaid configuration is stored in appsettings.json files:

- `appsettings.json` - Base configuration with sandbox values
- `appsettings.Development.json` - Development environment (sandbox)
- `appsettings.Staging.json` - Staging environment (development)
- `appsettings.Production.json` - Production environment

### Configuration Structure

```json
{
  "Plaid": {
    "ClientId": "YOUR_PLAID_CLIENT_ID",
    "Secret": "YOUR_PLAID_SECRET",
    "Environment": "sandbox|development|production",
    "ApiVersion": "2020-09-14",
    "ClientName": "MyApp Personal Finance",
    "Features": {
      "EnableDirectDeposit": false,
      "EnableAccountLinking": true,
      "EnableTransactionSync": true,
      "EnableRefreshInterval": true,
      "RefreshIntervalHours": 6
    }
  }
}
```

## Security Best Practices

1. **Never commit real API credentials to source control**
   - Use `dotnet user-secrets` for development
   - Use environment variables or a secure vault in production

2. **Setting up user secrets for development**:
   ```bash
   dotnet user-secrets set "Plaid:ClientId" "your-plaid-client-id"
   dotnet user-secrets set "Plaid:Secret" "your-plaid-secret"
   ```

3. **Setting up environment variables for production**:
   ```bash
   export Plaid__ClientId="your-plaid-client-id"
   export Plaid__Secret="your-plaid-secret"
   ```

## Feature Flags

The Plaid integration includes feature flags to enable/disable specific functionality:

- **EnableDirectDeposit** - Enable direct deposit switching features
- **EnableAccountLinking** - Enable account linking functionality
- **EnableTransactionSync** - Enable transaction synchronization
- **EnableRefreshInterval** - Enable automatic data refresh at intervals
- **RefreshIntervalHours** - Number of hours between automatic refreshes

## Migration Plan

When upgrading to a new Plaid API version:

1. Update `ApiVersion` in the configuration
2. Test thoroughly in Development environment
3. Update to Staging for integration testing
4. Roll out to Production after validation

## Troubleshooting

If you encounter issues with the Plaid integration:

1. Verify your environment is set correctly
2. Check configuration values in the appropriate appsettings file
3. Look for validation warnings in the application logs
4. Ensure your Plaid API keys are set correctly for the environment
5. Check that feature flags are configured appropriately