namespace ExchangeAdminWeb.Models;

/// <summary>
/// A Microsoft Graph managedDevice, narrowed to the fields the module's read UI uses
/// (docs/IntuneDeviceManagement-Plan.md, Verified API surface). Deliberately has no property to
/// hold activationLockBypassCode - the exclusion is enforced at two independent points, the
/// request $select (T4b) and the absence of a landing spot here, so a $select regression or a
/// tenant that returns it anyway still cannot reach the page, an audit record, a notification or
/// a log line.
/// </summary>
public sealed class IntuneDevice
{
    public string Id { get; set; } = "";
    public string DeviceName { get; set; } = "";
    public string ManagedDeviceName { get; set; } = "";
    public string UserPrincipalName { get; set; } = "";
    public string UserDisplayName { get; set; } = "";
    public string UserId { get; set; } = "";
    public string OperatingSystem { get; set; } = "";
    public string OsVersion { get; set; } = "";
    public string Manufacturer { get; set; } = "";
    public string Model { get; set; } = "";
    public string SerialNumber { get; set; } = "";
    public string Imei { get; set; } = "";
    public string Meid { get; set; } = "";
    public string WiFiMacAddress { get; set; } = "";
    public string EthernetMacAddress { get; set; } = "";
    public DateTimeOffset? EnrolledDateTime { get; set; }
    public DateTimeOffset? LastSyncDateTime { get; set; }
    public string ComplianceState { get; set; } = "";
    public string ManagementAgent { get; set; } = "";
    public string ManagedDeviceOwnerType { get; set; } = "";
    public string DeviceEnrollmentType { get; set; } = "";
    public string DeviceRegistrationState { get; set; } = "";
    public bool IsEncrypted { get; set; }
    public bool IsSupervised { get; set; }
    public bool JailBroken { get; set; }
    public string AzureADDeviceId { get; set; } = "";
    public bool AzureADRegistered { get; set; }
    public long TotalStorageSpaceInBytes { get; set; }
    public long FreeStorageSpaceInBytes { get; set; }
    public string Notes { get; set; } = "";
}

/// <summary>
/// Result of a bounded device search (docs/IntuneDeviceManagement-Plan.md S1). Truncated is set
/// whenever the Graph response carried @odata.nextLink (T1) - the shared client cannot follow an
/// absolute nextLink, so a capped result must never render as a complete one. SearchedCount is
/// the number of devices the page actually inspected, which matters when a client-side filter
/// (T2 fallback) matches nothing: "no match in the first N devices," never "no such device."
/// </summary>
public sealed record IntuneDeviceSearchResult(
    IReadOnlyList<IntuneDevice> Devices,
    bool Truncated,
    int SearchedCount);

/// <summary>Outcome of a single device action. SafeError carries only the sanitized Graph
/// error.code/message (never a token or raw body) so a refusal can be shown and audited.</summary>
public sealed record IntuneDeviceActionResult(bool Success, string Message, string? SafeError);

/// <summary>
/// One destructive action against one device. Each member carries its own audit action name and its
/// own granular policy (D1's two tiers), so the page never has a generic "device action" path that
/// could file a wipe under a delete's permission or a delete's audit name.
/// </summary>
public enum IntuneDeviceAction
{
    /// <summary>Delete the Intune management record. Tier 1, IntuneDevicesDelete (S3).</summary>
    Delete,

    /// <summary>Remove company data from the device. Tier 2, IntuneDevicesPrivileged (S4).</summary>
    Retire,

    /// <summary>Factory reset the device. Tier 2, IntuneDevicesPrivileged (S4).</summary>
    Wipe
}

/// <summary>
/// Every parameter Graph's managedDevice wipe action accepts, as an operator choice (D2: "anything
/// that can be an option should be an option"). The defaults are the full-reset reading of the
/// button's own label.
/// </summary>
/// <remarks>
/// KeepUserData and KeepEnrollmentData are ALWAYS serialized at their chosen values, so the reset
/// semantics are never left to a Graph default - the surviving half of idm-2. The other three are
/// sent only when set, because macOsUnlockCode: "" or an obliteration behaviour on a Windows device
/// is meaningless. MacOsUnlockCode is an operator-supplied device secret: it is displayed back once
/// after a successful queue and recorded in the audit as (set)/(not set), never by value (T4b).
/// </remarks>
public sealed record IntuneWipeOptions(
    bool KeepUserData = false,
    bool KeepEnrollmentData = false,
    string? MacOsUnlockCode = null,
    string? ObliterationBehavior = null,
    bool PersistEsimDataPlan = false);
