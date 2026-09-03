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
/// whenever ANY of the search's Graph responses carried @odata.nextLink (T1) - the shared client
/// cannot follow an absolute nextLink, so a capped result must never render as a complete one.
/// SearchedCount is the number of distinct devices the search actually inspected, which matters
/// when nothing matches: "no match in the first N devices," never "no such device."
/// </summary>
/// <param name="FilterIgnoredCount">
/// How many devices Graph returned that did NOT match the search term and were therefore hidden
/// (T2 Revision 2026-09-03). Normally zero. A non-zero value means an endpoint honoured the
/// request and ignored its $filter, which the page states as a warning rather than rendering the
/// rows as matches - a filter that failed must not read as a benign result.
/// </param>
public sealed record IntuneDeviceSearchResult(
    IReadOnlyList<IntuneDevice> Devices,
    bool Truncated,
    int SearchedCount,
    int FilterIgnoredCount = 0);

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
    Wipe,

    /// <summary>
    /// Remove the device's Entra ID device object, standalone from the detail panel. Its OWN tier,
    /// IntuneDevicesEntraDelete (S5 / D3): it acts on a different object through a DIRECTORY scope
    /// (Device.ReadWrite.All), the widest grant in the module, so an operator entitled to wipe a
    /// phone is not automatically entitled to remove directory records. Also offered as a checkbox
    /// beside the three Intune actions, which runs the same removal as a separately reported and
    /// separately audited second step.
    /// </summary>
    EntraDelete
}

/// <summary>
/// Why the device's primary user was, or was not, emailed about an action
/// (docs/IntuneDeviceManagement-Plan.md S6 / D2). Every not-sent case carries its own member,
/// because "was the user told?" is an audit question and a silent no is indistinguishable from a
/// failure. The four not-sent reasons are exactly D2's list: config default, operator unticked, no
/// address on the device, suppressed app-wide.
/// </summary>
public enum IntuneUserNotificationOutcome
{
    /// <summary>Every condition met: the mail is to be sent.</summary>
    Send,

    /// <summary>The module's config default for this action is not to email, and the operator did
    /// not ask for one.</summary>
    NotRequestedByDefault,

    /// <summary>The config default for this action IS to email, and the operator cleared the box -
    /// the lost-or-stolen case the per-action checkbox exists for.</summary>
    NotRequestedByOperator,

    /// <summary>The operator asked, but affected-user mail is disabled for the whole deployment
    /// (EmailService's _notifyUsers gate), which outranks anything this module sets. Must be said on
    /// screen as well as audited, or the checkbox is decorative.</summary>
    SuppressedAppWide,

    /// <summary>The operator asked, but the device has no primary user address to send to (a shared,
    /// kiosk or Autopilot pre-provisioned device).</summary>
    NoAddress
}

/// <summary>
/// The notification decision and the reason for it, in words fit for both the screen and the audit
/// record. Pure, so the whole matrix is testable without a mail server (plan Test plan).
/// </summary>
public sealed record IntuneUserNotificationDecision(IntuneUserNotificationOutcome Outcome, string Reason)
{
    public bool ShouldSend => Outcome == IntuneUserNotificationOutcome.Send;
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
