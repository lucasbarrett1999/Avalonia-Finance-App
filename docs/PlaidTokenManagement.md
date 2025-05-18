# Plaid Token Management

This document explains how Plaid access tokens are managed, stored, and rotated in MyApp.

## Token Storage Architecture

The system implements a secure architecture for storing and managing Plaid access tokens:

1. **Encryption Layer**
   - Access tokens are encrypted using AES-256 before storage
   - Each token is encrypted with a unique encryption key and initialization vector
   - Encryption keys are stored in configuration, never in the database

2. **Database Storage**
   - Encrypted tokens are stored in the `PlaidItems` table
   - The table includes metadata about each Plaid connection
   - The schema includes fields for error tracking, refresh scheduling, and status

3. **Token Manager**
   - The `PlaidTokenManager` service provides a clean API for token operations
   - All token access is logged for audit purposes
   - Rate limiting and error handling are built into the service

## Security Measures

The following security measures are implemented:

1. **Encryption at Rest**
   - All tokens are encrypted before storage using AES-256
   - The encryption key and IV are stored in application configuration, not in the database
   - For production, encryption keys should be stored in a secure vault or environment variables

2. **Access Control**
   - All token access is logged with timestamp, operation, and caller information
   - Token usage is tracked for audit and debugging purposes
   - Token access is controlled through service interfaces

3. **Error Handling**
   - Error states are tracked in the database
   - Failed tokens are marked for rotation or refresh
   - Comprehensive logging of error conditions

## Token Lifecycle

Tokens go through the following lifecycle:

1. **Creation**
   - Created through Plaid Link flow
   - Public token exchanged for access token
   - Access token encrypted and stored

2. **Usage**
   - Access tokens retrieved and decrypted only when needed
   - Access is logged in audit trail
   - Last accessed time tracked for analytics

3. **Refresh**
   - Tokens are refreshed on a configurable schedule
   - Refresh interval is environment-specific
   - Item metadata is updated during refresh

4. **Rotation**
   - Tokens can be rotated if compromised
   - Error handling supports automatic token rotation
   - Token rotation maintains the same item_id

5. **Removal**
   - Soft deletion preserves audit trail
   - Hard deletion only in special circumstances

## Audit Logging

All token operations are logged with:

1. Timestamp (UTC)
2. Operation type (ACCESS, MANAGEMENT, ERROR)
3. Item identifier
4. Action details
5. Error information (if applicable)

Logs are stored in the `logs/plaid_audit.log` file for review.

## Token Error Handling

The system handles token errors as follows:

1. Error details are stored in the database with the token
2. Errors are categorized by error code
3. Some errors trigger automatic token refresh or rotation
4. Recurring errors trigger notification and manual intervention

## Configuration

Token management is configured through:

1. `appsettings.json` - Base configuration
2. Environment-specific settings (Development, Staging, Production)
3. Feature flags for controlling refresh behavior
4. Encryption key and IV settings

See the [Plaid Configuration Guide](PlaidConfiguration.md) for more details.

## Best Practices

When working with tokens:

1. Always use `PlaidTokenManager` for token operations, never direct database access
2. Don't log access tokens, even in debug mode
3. Keep encryption keys secure and rotate them periodically
4. Monitor the audit log for suspicious activity
5. Set appropriate refresh intervals based on usage patterns