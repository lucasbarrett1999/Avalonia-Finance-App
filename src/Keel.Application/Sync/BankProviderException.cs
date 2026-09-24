using Keel.Domain;

namespace Keel.Application.Sync;

/// <summary>
/// A provider call failed. <see cref="Status"/> is the connection health it implies (for example
/// Plaid <c>ITEM_LOGIN_REQUIRED</c> is <see cref="SyncStatus.NeedsReauth"/>) and <see cref="Code"/>
/// the provider's error code; neither the message nor the code ever contains a token or secret.
/// </summary>
public sealed class BankProviderException : Exception
{
    /// <summary>Creates the exception.</summary>
    public BankProviderException(SyncStatus status, string code, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Status = status;
        Code = code;
    }

    /// <summary>Creates the exception.</summary>
    public BankProviderException()
        : this(SyncStatus.Error, BankErrorCodes.Unknown, "The bank data provider failed.")
    {
    }

    /// <summary>Creates the exception.</summary>
    public BankProviderException(string message)
        : this(SyncStatus.Error, BankErrorCodes.Unknown, message)
    {
    }

    /// <summary>Creates the exception.</summary>
    public BankProviderException(string message, Exception innerException)
        : this(SyncStatus.Error, BankErrorCodes.Unknown, message, innerException)
    {
    }

    /// <summary>Connection health implied by the failure.</summary>
    public SyncStatus Status { get; }

    /// <summary>Provider error code (e.g. <c>ITEM_LOGIN_REQUIRED</c>) or one of <see cref="BankErrorCodes"/>.</summary>
    public string Code { get; }
}

/// <summary>Keel's own error codes for provider failures that have no provider code.</summary>
public static class BankErrorCodes
{
    /// <summary>Unclassified failure.</summary>
    public const string Unknown = "UNKNOWN";

    /// <summary>No client id or secret entered in Settings → Connections.</summary>
    public const string MissingCredentials = "KEEL_MISSING_CREDENTIALS";

    /// <summary>The connection's access token is not in the secret store (e.g. another machine).</summary>
    public const string MissingAccessToken = "KEEL_MISSING_ACCESS_TOKEN";

    /// <summary>The network or the provider could not be reached.</summary>
    public const string Network = "KEEL_NETWORK";

    /// <summary>The link session expired or timed out before the user finished.</summary>
    public const string LinkExpired = "KEEL_LINK_EXPIRED";

    /// <summary>The user closed the link flow without linking.</summary>
    public const string LinkExited = "KEEL_LINK_EXITED";

    /// <summary>The bank requires the user to sign in again (Plaid <c>ITEM_LOGIN_REQUIRED</c>).</summary>
    public const string LoginRequired = "ITEM_LOGIN_REQUIRED";

    /// <summary>A SimpleFIN setup token was malformed or already claimed.</summary>
    public const string InvalidSetupToken = "KEEL_INVALID_SETUP_TOKEN";
}
