using System.Globalization;
using Keel.Application.Security;
using Keel.Application.Sync;
using Keel.Desktop.Resources;
using Keel.Desktop.Services;
using Keel.Domain;

namespace Keel.Desktop.ViewModels.Sync;

/// <summary>How a connection's health dot is drawn (PRD 9.1): green, amber, red, or grey.</summary>
public enum HealthKind
{
    /// <summary>Not linked: no dot.</summary>
    None,

    /// <summary>Syncing normally (green).</summary>
    Ok,

    /// <summary>Needs the user, e.g. sign in again (amber).</summary>
    Attention,

    /// <summary>The last sync failed (red).</summary>
    Error,

    /// <summary>Sync is off (grey).</summary>
    Off,
}

/// <summary>Localized text for bank sync (all strings come from Strings.resx).</summary>
public static class SyncText
{
    /// <summary>Health dot kind of a status.</summary>
    public static HealthKind Health(SyncStatus? status) => status switch
    {
        null => HealthKind.None,
        SyncStatus.Ok => HealthKind.Ok,
        SyncStatus.NeedsReauth => HealthKind.Attention,
        SyncStatus.Error => HealthKind.Error,
        _ => HealthKind.Off,
    };

    /// <summary>Short status, e.g. "Connected" or "Sign-in needed".</summary>
    public static string Status(SyncStatus status) => Lookup("SyncStatus_" + status) ?? status.ToString();

    /// <summary>A user-facing explanation of a sync error code (never shows secrets; codes are Plaid's or Keel's).</summary>
    public static string Error(string? code)
    {
        if (string.IsNullOrEmpty(code))
        {
            return Strings.SyncError_Generic_NoCode;
        }

        return Lookup("SyncError_" + code) ?? LedgerText.Format(Strings.SyncError_Generic, code);
    }

    /// <summary><see cref="Error"/> as a sentence on its own: capitalized, ending with a period.</summary>
    public static string ErrorSentence(string? code)
    {
        var text = Error(code);
        if (text.Length == 0)
        {
            return text;
        }

        text = char.ToUpper(text[0], CultureInfo.CurrentCulture) + text[1..];
        return text.EndsWith('.') ? text : text + ".";
    }

    /// <summary>Provider display name.</summary>
    public static string Provider(SyncProvider provider) => Lookup("SyncProvider_" + provider) ?? provider.ToString();

    /// <summary>Provider display name by id.</summary>
    public static string Provider(string providerId) =>
        Provider(string.Equals(providerId, "simplefin", StringComparison.Ordinal) ? SyncProvider.SimpleFin : SyncProvider.Plaid);

    /// <summary>Secret store backend name, e.g. "GNOME Keyring / KWallet (Secret Service)".</summary>
    public static string Backend(SecretStoreBackend backend) => Lookup("SecretBackend_" + backend) ?? backend.ToString();

    /// <summary>"Never synced", "Synced just now", "Synced 5 min ago", "Synced at 9:41 AM", "Synced 9/20/2026".</summary>
    public static string LastSync(DateTime? utc, DateTimeOffset now)
    {
        if (utc is not { } at)
        {
            return Strings.Sync_NeverSynced;
        }

        var when = new DateTimeOffset(DateTime.SpecifyKind(at, DateTimeKind.Utc));
        var age = now - when;
        if (age < TimeSpan.FromMinutes(1))
        {
            return Strings.Sync_JustNow;
        }

        if (age < TimeSpan.FromHours(1))
        {
            return LedgerText.Format(Strings.Sync_MinutesAgo, (int)age.TotalMinutes);
        }

        var local = when.ToLocalTime();
        return local.Date == now.ToLocalTime().Date
            ? LedgerText.Format(Strings.Sync_AtTime, local.ToString("t", CultureInfo.CurrentCulture))
            : LedgerText.Format(Strings.Sync_OnDate, local.ToString("d", CultureInfo.CurrentCulture));
    }

    /// <summary>The status-strip line for a finished run.</summary>
    public static string Summary(int added, int updated, int removed) =>
        added + updated + removed == 0
            ? Strings.Sync_ResultNothingNew
            : LedgerText.Format(Strings.Sync_Result, added, updated, removed);

    private static string? Lookup(string key) => Strings.ResourceManager.GetString(key, Strings.Culture);
}
