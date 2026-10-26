using System.Net;
using ExchangeAdminWeb.Models;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// What became of one notification dispatch: how many recipients it was addressed to, and how
/// many accepted it.
/// </summary>
/// <remarks>
/// Audit finding A27. The caller needs this to tell the truth about it. Zero requested is a
/// different event from zero delivered - "nobody is configured to hear about this" versus
/// "the people configured to hear about it did not".
/// </remarks>
public sealed record NotificationDispatch(int Requested, int Delivered, string? FirstError)
{
    public static readonly NotificationDispatch NoRecipient = new(0, 0, null);

    public bool AnyDelivered => Delivered > 0;

    public bool AllDelivered => Requested > 0 && Delivered == Requested;
}

public class EmailService
{
    private readonly IConfiguration _config;
    private readonly ILogger<EmailService> _logger;
    private readonly string _smtpHost;
    private readonly int _smtpPort;
    private readonly string _smtpUser;
    private readonly string _smtpPass;
    private readonly bool _smtpUseSsl;
    private readonly string _fromAddress;
    private readonly string _fromName;
    private readonly string _adminEmail;
    private readonly bool _notifyUsers;
    private readonly string _appName;

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
        _smtpHost = config["Email:SmtpHost"] ?? "localhost";
        // aud-a62. int.Parse threw on a port that is not a number, or is blank, and with it this singleton. Such a value is
        // the default, 25, and is named once; a wrong port fails at connect time, as a wrong number always has.
        _smtpPort = ConfigValue.ReadInt(config, "Email:SmtpPort", whenBlank: 25, logger);
        _smtpUser = config["Email:SmtpUsername"] ?? "";
        _smtpPass = config["Email:SmtpPassword"] ?? "";
        // aud-a58. bool.Parse threw on a value that is neither true nor false, and this singleton, and every page that sends
        // mail, failed with it. A mistyped TLS switch is read as on, so mail never goes out in clear text by a typo; the
        // user-notification switch keeps its default, as the Home page reads it.
        _smtpUseSsl = ConfigBoolean.Read(config, "Email:SmtpUseSsl", whenBlank: false, whenNotBoolean: true, logger);
        _fromAddress = config["Email:FromAddress"] ?? "noreply@example.com";
        _fromName = config["Email:FromName"] ?? "Exchange Admin";
        _adminEmail = config["Email:AdminNotificationEmail"] ?? "";
        _notifyUsers = ConfigBoolean.Read(config, "Email:NotifyUsersOnPermissionGrant", whenBlank: false, whenNotBoolean: false, logger);
        _appName = config["Application:Name"] ?? "Exchange Admin";
    }

    public virtual async Task SendAdminNotificationAsync(
        string performedBy,
        string ipAddress,
        string action,
        string targetMailbox,
        string affectedUser,
        string permissionType,
        bool success,
        string ticketNumber,
        string? errorDetail = null)
    {
        if (string.IsNullOrWhiteSpace(_adminEmail))
        {
            _logger.LogWarning("Admin notification email not configured, skipping notification");
            return;
        }

        var isMigration = action.Contains("Migration", StringComparison.OrdinalIgnoreCase);
        var headerText = isMigration
            ? (success ? "✓" : "✗") + " Migration Notification"
            : (success ? "✓" : "✗") + " Permission Change Notification";

        var subject = $"[Exchange Admin] {action} - {(success ? "SUCCESS" : "FAILED")} - Ticket #{ticketNumber}";
        var h = (string s) => WebUtility.HtmlEncode(s ?? "");
        var body = $@"<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; }}
        .header {{ background: {(success ? "#28a745" : "#dc3545")}; color: white; padding: 10px; }}
        .content {{ padding: 20px; }}
        table {{ border-collapse: collapse; width: 100%; }}
        td {{ padding: 8px; border-bottom: 1px solid #ddd; }}
        td:first-child {{ font-weight: bold; width: 150px; }}
    </style>
</head>
<body>
    <div class=""header"">
        <h2>{h(headerText)}</h2>
    </div>
    <div class=""content"">
        <table>
            <tr><td>Ticket Number</td><td><strong>{h(ticketNumber)}</strong></td></tr>
            <tr><td>Action</td><td>{h(action)}</td></tr>
            <tr><td>Status</td><td>{(success ? "SUCCESS" : "FAILED")}</td></tr>
            <tr><td>Target Mailbox</td><td>{h(targetMailbox)}</td></tr>
            <tr><td>Affected User</td><td>{h(affectedUser)}</td></tr>
            <tr><td>Permission Type</td><td>{h(permissionType)}</td></tr>
            <tr><td>Performed By</td><td>{h(performedBy)}</td></tr>
            <tr><td>IP Address</td><td>{h(ipAddress)}</td></tr>
            <tr><td>Timestamp (UTC)</td><td>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</td></tr>
            {(string.IsNullOrWhiteSpace(errorDetail) ? "" : $"<tr><td>Error</td><td style=\"color: red;\">{h(errorDetail)}</td></tr>")}
        </table>
    </div>
</body>
</html>";

        // Support multiple admin emails (comma-separated)
        var adminEmails = _adminEmail.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrWhiteSpace(e));

        foreach (var email in adminEmails)
        {
            await SendEmailAsync(email, subject, body);
        }
    }

    public virtual async Task SendAdminNotificationAsync(
        string performedBy,
        string ipAddress,
        string action,
        bool success,
        string ticketNumber,
        IReadOnlyDictionary<string, string> details,
        string? errorDetail = null)
    {
        if (string.IsNullOrWhiteSpace(_adminEmail))
        {
            _logger.LogWarning("Admin notification email not configured, skipping notification");
            return;
        }

        await SendModuleNotificationAsync(
            _adminEmail, performedBy, ipAddress, action, success, ticketNumber, details, errorDetail);
    }

    /// <summary>
    /// Sends the admin-notification mail to an EXPLICIT recipient list and reports what
    /// happened to it.
    /// </summary>
    /// <remarks>
    /// Audit finding A27. Emergency Disable reads its own NotifySecurityTeam address and then
    /// called the admin overload, so the configured address worked as an on/off switch and
    /// the mail itself went to the global admin list. A module that owns a recipient needs to
    /// be able to name it.
    ///
    /// It returns a <see cref="NotificationDispatch"/> rather than void because the caller has
    /// a claim to make: the Emergency Disable page told the operator "Security team notified"
    /// unconditionally. Per-recipient failures are aggregated, not collapsed into one bool -
    /// Known Failure Class 2.
    /// </remarks>
    public virtual async Task<NotificationDispatch> SendModuleNotificationAsync(
        string recipients,
        string performedBy,
        string ipAddress,
        string action,
        bool success,
        string ticketNumber,
        IReadOnlyDictionary<string, string> details,
        string? errorDetail = null)
    {
        var addresses = (recipients ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToList();

        if (addresses.Count == 0)
            return NotificationDispatch.NoRecipient;

        var subject = $"[Admin] {action} - {(success ? "SUCCESS" : "FAILED")} - Ticket #{ticketNumber}";
        var h = (string s) => WebUtility.HtmlEncode(s ?? "");

        var detailRows = string.Join("\n", details.Select(kv =>
            $"<tr><td>{h(kv.Key)}</td><td>{h(kv.Value)}</td></tr>"));

        var body = $@"<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; }}
        .header {{ background: {(success ? "#28a745" : "#dc3545")}; color: white; padding: 10px; }}
        .content {{ padding: 20px; }}
        table {{ border-collapse: collapse; width: 100%; }}
        td {{ padding: 8px; border-bottom: 1px solid #ddd; }}
        td:first-child {{ font-weight: bold; width: 150px; }}
    </style>
</head>
<body>
    <div class=""header"">
        <h2>{(success ? "✓" : "✗")} {h(action)}</h2>
    </div>
    <div class=""content"">
        <table>
            <tr><td>Ticket Number</td><td><strong>{h(ticketNumber)}</strong></td></tr>
            <tr><td>Action</td><td>{h(action)}</td></tr>
            <tr><td>Status</td><td>{(success ? "SUCCESS" : "FAILED")}</td></tr>
            {detailRows}
            <tr><td>Performed By</td><td>{h(performedBy)}</td></tr>
            <tr><td>IP Address</td><td>{h(ipAddress)}</td></tr>
            <tr><td>Timestamp (UTC)</td><td>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</td></tr>
            {(string.IsNullOrWhiteSpace(errorDetail) ? "" : $"<tr><td>Error</td><td style=\"color: red;\">{h(errorDetail)}</td></tr>")}
        </table>
    </div>
</body>
</html>";

        var delivered = 0;
        string? firstError = null;

        foreach (var email in addresses)
        {
            try
            {
                await SendEmailOrThrowAsync(email, subject, body);
                delivered++;
            }
            catch (Exception ex)
            {
                firstError ??= ex.Message;
                _logger.LogError(ex, "Failed to send {Action} notification to {To}: {Message}", action, email, ex.Message);
            }
        }

        return new NotificationDispatch(addresses.Count, delivered, firstError);
    }

    public virtual async Task SendUserNotificationAsync(
        string userEmail,
        string targetMailbox,
        string performedBy,
        string permissionType,
        bool isGrant = true)
    {
        if (!_notifyUsers)
        {
            _logger.LogDebug("User notifications disabled, skipping notification to {Email}", userEmail);
            return;
        }

        var subject = isGrant ? "Mailbox Access Granted" : "Mailbox Access Removed";
        var actionWord = isGrant ? "granted" : "removed";
        var headerText = isGrant ? "📬 Mailbox Access Granted" : "📪 Mailbox Access Removed";
        var headerColor = isGrant ? "#0078d4" : "#dc3545";

        var h = (string s) => WebUtility.HtmlEncode(s ?? "");
        var body = $@"<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background: {headerColor}; color: white; padding: 20px; border-radius: 5px 5px 0 0; }}
        .content {{ background: #f9f9f9; padding: 20px; border: 1px solid #ddd; border-top: none; }}
        .footer {{ background: #f0f0f0; padding: 15px; border-radius: 0 0 5px 5px; font-size: 12px; color: #666; }}
        .warning {{ background: #fff3cd; border: 1px solid #ffc107; padding: 10px; margin: 15px 0; border-radius: 3px; }}
        .details {{ background: white; padding: 10px; margin: 10px 0; border-left: 3px solid {headerColor}; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h2>{h(headerText)}</h2>
        </div>
        <div class=""content"">
            <p>Hello,</p>
            <p>Your access to the following mailbox has been {actionWord}:</p>
            <div class=""details"">
                <strong>Mailbox:</strong> {h(targetMailbox)}<br>
                <strong>Permission:</strong> {h(permissionType)}<br>
                <strong>{(isGrant ? "Granted" : "Removed")} by:</strong> {h(performedBy)}<br>
                <strong>Date:</strong> {DateTime.Now:MMMM dd, yyyy 'at' h:mm tt}
            </div>
            {(isGrant && permissionType.Contains("FullAccess") ? "<p>This mailbox may automatically appear in your Outlook if AutoMapping is enabled.</p>" : "")}
            {(!isGrant && permissionType.Contains("FullAccess") ? "<p>If the mailbox was previously auto-mapped to your Outlook, it may take up to 24 hours to disappear, or you may need to restart Outlook.</p>" : "")}
            <div class=""warning"">
                <strong>⚠️ Important:</strong> If you were unaware of this change or did not request this {(isGrant ? "access" : "removal")}, please contact the IT Service Desk immediately.
            </div>
        </div>
        <div class=""footer"">
            <p>This is an automated notification from Exchange Admin. Please do not reply to this email.</p>
            <p>© {DateTime.Now.Year} Your organization. All rights reserved.</p>
        </div>
    </div>
</body>
</html>";

        await SendEmailAsync(userEmail, subject, body);
    }

    /// <summary>
    /// kd-44. Tells the user whose access a change was to alter that it was attempted and that whether it took effect could not
    /// be confirmed - neutral: what may have changed and whom to contact, never "granted" or "removed". Sent where the grant
    /// or removal notice would have been, when the change's outcome is unknown (Constitution, Notifications: a change to a
    /// user's access notifies them, and this one may have made it). Gated by user notifications, as the grant notice is.
    /// </summary>
    public virtual async Task SendUnconfirmedUserNotificationAsync(
        string userEmail,
        string targetMailbox,
        string performedBy,
        string permissionType,
        bool isGrant = true)
    {
        if (!_notifyUsers)
        {
            _logger.LogDebug("User notifications disabled, skipping notification to {Email}", userEmail);
            return;
        }

        await SendEmailAsync(userEmail, UnconfirmedSubject,
            BuildUnconfirmedChangeBody(toOwner: false, targetMailbox, userEmail, permissionType, isGrant, performedBy));
    }

    /// <summary>
    /// kd-44. <see cref="SendUnconfirmedUserNotificationAsync"/> for the owner of the mailbox or calendar the change was on.
    /// </summary>
    public virtual async Task SendUnconfirmedOwnerNotificationAsync(
        string ownerEmail,
        string grantedUser,
        string performedBy,
        string permissionType,
        bool isGrant = true)
    {
        if (!_notifyUsers)
        {
            _logger.LogDebug("User notifications disabled, skipping notification to {Email}", ownerEmail);
            return;
        }

        await SendEmailAsync(ownerEmail, UnconfirmedSubject,
            BuildUnconfirmedChangeBody(toOwner: true, ownerEmail, grantedUser, permissionType, isGrant, performedBy));
    }

    /// <summary>kd-44. The subject of both notices of a change whose outcome could not be confirmed.</summary>
    internal const string UnconfirmedSubject = "Access Change Could Not Be Confirmed";

    /// <summary>
    /// kd-44. The body of both notices: that a change to <paramref name="trustee"/>'s access to <paramref name="resource"/> was
    /// attempted and could not be confirmed, what was attempted and by whom, and whom to contact. Built apart from the send so it
    /// can be read without SMTP (the <see cref="BuildCloudPasswordResetBody"/> shape).
    /// </summary>
    internal static string BuildUnconfirmedChangeBody(bool toOwner, string resource, string trustee, string permissionType, bool isGrant, string performedBy)
    {
        var h = (string s) => WebUtility.HtmlEncode(s ?? "");
        var resourceType = permissionType.Contains("Calendar") ? "calendar" : "mailbox"; // as the owner notice reads it
        var sentence = toOwner
            ? $"A change to the access the following user has to your {resourceType} was attempted, and whether it took effect could not be confirmed. Their access may or may not have changed."
            : $"A change to your access to the following {resourceType} was attempted, and whether it took effect could not be confirmed. Your access may or may not have changed.";
        var subjectLine = toOwner
            ? $"<strong>User:</strong> {h(trustee)}<br>"
            : $"<strong>Mailbox:</strong> {h(resource)}<br>";
        return $@"<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background: #6c757d; color: white; padding: 20px; border-radius: 5px 5px 0 0; }}
        .content {{ background: #f9f9f9; padding: 20px; border: 1px solid #ddd; border-top: none; }}
        .footer {{ background: #f0f0f0; padding: 15px; border-radius: 0 0 5px 5px; font-size: 12px; color: #666; }}
        .warning {{ background: #fff3cd; border: 1px solid #ffc107; padding: 10px; margin: 15px 0; border-radius: 3px; }}
        .details {{ background: white; padding: 10px; margin: 10px 0; border-left: 3px solid #6c757d; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h2>{h(UnconfirmedSubject)}</h2>
        </div>
        <div class=""content"">
            <p>Hello,</p>
            <p>{sentence}</p>
            <div class=""details"">
                {subjectLine}
                <strong>Permission:</strong> {h(permissionType)}<br>
                <strong>Change attempted:</strong> {(isGrant ? "grant" : "removal")}<br>
                <strong>Attempted by:</strong> {h(performedBy)}<br>
                <strong>Date:</strong> {DateTime.Now:MMMM dd, yyyy 'at' h:mm tt}
            </div>
            <div class=""warning"">
                <strong>What to do:</strong> To find out whether the access changed, or if you did not expect this change, please contact the IT Service Desk.
            </div>
        </div>
        <div class=""footer"">
            <p>This is an automated notification from Exchange Admin. Please do not reply to this email.</p>
        </div>
    </div>
</body>
</html>";
    }

    public virtual async Task SendOwnerNotificationAsync(
        string ownerEmail,
        string grantedUser,
        string performedBy,
        string permissionType,
        bool isGrant = true)
    {
        if (!_notifyUsers)
        {
            _logger.LogDebug("User notifications disabled, skipping notification to {Email}", ownerEmail);
            return;
        }

        var isCalendar = permissionType.Contains("Calendar");
        var resourceType = isCalendar ? "calendar" : "mailbox";

        var subject = isGrant
            ? $"Access to Your {(isCalendar ? "Calendar" : "Mailbox")} Has Been Granted"
            : $"Access to Your {(isCalendar ? "Calendar" : "Mailbox")} Has Been Removed";
        var actionWord = isGrant ? "granted access to" : "removed access from";
        var headerText = isGrant
            ? (isCalendar ? "📅 Calendar Access Granted" : "🔑 Mailbox Access Granted")
            : (isCalendar ? "🔒 Calendar Access Removed" : "🔒 Mailbox Access Removed");
        var headerColor = isGrant ? "#ffc107" : "#28a745";

        var permissionDetails = "";
        if (isGrant)
        {
            if (permissionType.Contains("FullAccess"))
                permissionDetails = "<p><strong>Full Access</strong> allows the user to read, send, and manage all items in your mailbox.</p>";
            else if (permissionType.Contains("SendAs"))
                permissionDetails = "<p><strong>Send As</strong> allows the user to send email as if it came from you.</p>";
            else if (permissionType.Contains("Editor"))
                permissionDetails = "<p><strong>Editor</strong> allows the user to read and modify items in your calendar.</p>";
            else if (permissionType.Contains("Reviewer"))
                permissionDetails = "<p><strong>Reviewer</strong> allows the user to read items in your calendar.</p>";
        }

        var h = (string s) => WebUtility.HtmlEncode(s ?? "");
        var body = $@"<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background: {headerColor}; color: white; padding: 20px; border-radius: 5px 5px 0 0; }}
        .content {{ background: #f9f9f9; padding: 20px; border: 1px solid #ddd; border-top: none; }}
        .footer {{ background: #f0f0f0; padding: 15px; border-radius: 0 0 5px 5px; font-size: 12px; color: #666; }}
        .warning {{ background: #fff3cd; border: 1px solid #ffc107; padding: 10px; margin: 15px 0; border-radius: 3px; }}
        .details {{ background: white; padding: 10px; margin: 10px 0; border-left: 3px solid {headerColor}; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h2>{h(headerText)}</h2>
        </div>
        <div class=""content"">
            <p>Hello,</p>
            <p>The following user has been {actionWord} your {resourceType}:</p>
            <div class=""details"">
                <strong>User:</strong> {h(grantedUser)}<br>
                <strong>Permission:</strong> {h(permissionType)}<br>
                <strong>{(isGrant ? "Granted" : "Removed")} by:</strong> {h(performedBy)}<br>
                <strong>Date:</strong> {DateTime.Now:MMMM dd, yyyy 'at' h:mm tt}
            </div>
            {permissionDetails}
            <div class=""warning"">
                <strong>⚠️ Security Notice:</strong> If you did not authorize this change or have concerns about this access, please contact the IT Service Desk immediately.
            </div>
        </div>
        <div class=""footer"">
            <p>This is an automated notification from Exchange Admin. Please do not reply to this email.</p>
            <p>© {DateTime.Now.Year} Your organization. All rights reserved.</p>
        </div>
    </div>
</body>
</html>";

        await SendEmailAsync(ownerEmail, subject, body);
    }

    /// <summary>
    /// Tells the mailbox owner what an administrator did to their auto-reply: enabled, scheduled
    /// (with its window) or disabled. Gated by the same <c>NotifyUsersOnPermissionGrant</c> switch
    /// as the other affected-user notifications, and virtual so it is test-seamable like them.
    /// </summary>
    /// <remarks>
    /// Audit finding aud-a7d. This used to take a two-way word, "enabled" or "disabled", so a
    /// Scheduled write told the owner their auto-reply "has been enabled" and named no window. It
    /// takes the request that was written instead - the caller's captured request (aud-a7b), never
    /// a re-read of the form.
    /// </remarks>
    public virtual async Task SendOofNotificationAsync(string targetEmail, string performedBy, OutOfOfficeRequest request)
    {
        if (!_notifyUsers)
            return;

        var (subject, body) = BuildOofNotification(performedBy, request, DateTimeOffset.UtcNow);
        await SendEmailAsync(targetEmail, subject, body);
    }

    /// <summary>
    /// The affected-user auto-reply notification for one written request. Extracted from the send
    /// so it is assertable without SMTP. Every caller-supplied value is HTML-encoded.
    /// </summary>
    internal static (string Subject, string Body) BuildOofNotification(string performedBy, OutOfOfficeRequest request, DateTimeOffset changedAt)
    {
        var h = (string s) => WebUtility.HtmlEncode(s ?? "");
        var (headerText, action, subjectWord, headerColor) = request.State switch
        {
            "Disabled" => ("Auto-Reply Disabled", "disabled", "Disabled", "#28a745"),
            "Scheduled" => ("Auto-Reply Scheduled", "scheduled", "Scheduled", "#0078d4"),
            _ => ("Auto-Reply Enabled", "enabled", "Enabled", "#0078d4"),
        };
        var window = request.State == "Scheduled" && request.StartTime is { } start && request.EndTime is { } end
            ? $@"
            <p><strong>From:</strong> {h(When(start, request.ScheduleZone))}<br>
               <strong>Until:</strong> {h(When(end, request.ScheduleZone))}</p>"
            : "";

        // aud-a7e. Each time with the zone it was entered in and that zone's offset at that time, so the
        // owner - who may be in another zone - can tell which moment is meant. The zone's name comes from
        // the same helper the page's label uses. A Scheduled write without a zone is refused before it
        // is made, so the first arm is not expected to run; it says so rather than drop the zone silently.
        static string When(DateTime wallClock, TimeZoneInfo? zone) => zone is null
            ? $"{wallClock:MMMM dd, yyyy 'at' h:mm tt} (time zone not recorded)"
            : $"{wallClock:MMMM dd, yyyy 'at' h:mm tt} ({OutOfOfficeRequest.ZoneName(zone)}, {OutOfOfficeRequest.OffsetAt(wallClock, zone)})";

        // aud-a7f. The change's instant in UTC, labelled UTC; it used to be this server's clock with no
        // zone. UTC rather than the operator's zone: the reader is the mailbox owner, who may be anywhere
        // and has no tie to the operator's browser, and the request carries a zone only when Scheduled.
        static string Changed(DateTimeOffset at) => $"{at.UtcDateTime:MMMM dd, yyyy 'at' h:mm tt} UTC";

        var body = $@"<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background: {headerColor}; color: white; padding: 20px; border-radius: 5px 5px 0 0; }}
        .content {{ background: #f9f9f9; padding: 20px; border: 1px solid #ddd; border-top: none; }}
        .footer {{ background: #f0f0f0; padding: 15px; border-radius: 0 0 5px 5px; font-size: 12px; color: #666; }}
        .warning {{ background: #fff3cd; border: 1px solid #ffc107; padding: 10px; margin: 15px 0; border-radius: 3px; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h2>{h(headerText)}</h2>
        </div>
        <div class=""content"">
            <p>Hello,</p>
            <p>Your mailbox auto-reply (Out of Office) has been <strong>{h(action)}</strong> by an administrator.</p>{window}
            <p><strong>Changed by:</strong> {h(performedBy)}<br>
               <strong>Date:</strong> {h(Changed(changedAt))}</p>
            <div class=""warning"">
                <strong>Important:</strong> If you were unaware of this change or did not request it, please contact the IT Service Desk immediately.
            </div>
        </div>
        <div class=""footer"">
            <p>This is an automated notification from Exchange Admin. Please do not reply to this email.</p>
        </div>
    </div>
</body>
</html>";

        return ($"Your Auto-Reply Has Been {subjectWord}", body);
    }

    /// <summary>
    /// Notifies a user that they were added to or removed from an on-premises security group
    /// (self-service group management, plan docs/SelfServiceGroupManagement-Plan.md AC10; Constitution
    /// "Notifications" - a change to a user's access must additionally notify the affected user). Gated
    /// by the same <c>NotifyUsersOnPermissionGrant</c> switch as the other affected-user notifications,
    /// and virtual so it is test-seamable like the admin-notification overloads.
    /// </summary>
    /// <param name="userEmail">The affected member's primary SMTP address.</param>
    /// <param name="groupName">The security group's display name.</param>
    /// <param name="performedBy">The self-service owner who made the change.</param>
    /// <param name="isAdd">True when the user was added; false when removed.</param>
    public virtual async Task SendGroupMembershipUserNotificationAsync(
        string userEmail,
        string groupName,
        string performedBy,
        bool isAdd)
    {
        if (!_notifyUsers)
        {
            _logger.LogDebug("User notifications disabled, skipping group-membership notification to {Email}", userEmail);
            return;
        }

        var subject = isAdd
            ? "You Have Been Added to a Security Group"
            : "You Have Been Removed from a Security Group";
        var headerText = isAdd ? "Security Group Membership Added" : "Security Group Membership Removed";
        var actionWord = isAdd ? "added to" : "removed from";
        var headerColor = isAdd ? "#0078d4" : "#28a745";

        var h = (string s) => WebUtility.HtmlEncode(s ?? "");
        var body = $@"<!DOCTYPE html>
<html>
<head>
    <style>
        body {{ font-family: Arial, sans-serif; color: #333; }}
        .container {{ max-width: 600px; margin: 0 auto; padding: 20px; }}
        .header {{ background: {headerColor}; color: white; padding: 20px; border-radius: 5px 5px 0 0; }}
        .content {{ background: #f9f9f9; padding: 20px; border: 1px solid #ddd; border-top: none; }}
        .footer {{ background: #f0f0f0; padding: 15px; border-radius: 0 0 5px 5px; font-size: 12px; color: #666; }}
        .warning {{ background: #fff3cd; border: 1px solid #ffc107; padding: 10px; margin: 15px 0; border-radius: 3px; }}
        .details {{ background: white; padding: 10px; margin: 10px 0; border-left: 3px solid {headerColor}; }}
    </style>
</head>
<body>
    <div class=""container"">
        <div class=""header"">
            <h2>{h(headerText)}</h2>
        </div>
        <div class=""content"">
            <p>Hello,</p>
            <p>Your membership of the following security group has been {actionWord}:</p>
            <div class=""details"">
                <strong>Group:</strong> {h(groupName)}<br>
                <strong>{(isAdd ? "Added" : "Removed")} by:</strong> {h(performedBy)}<br>
                <strong>Date:</strong> {DateTime.Now:MMMM dd, yyyy 'at' h:mm tt}
            </div>
            <div class=""warning"">
                <strong>Important:</strong> Security group membership can change your access to systems and resources. If you were unaware of this change or did not request it, please contact the IT Service Desk immediately.
            </div>
        </div>
        <div class=""footer"">
            <p>This is an automated notification from Exchange Admin. Please do not reply to this email.</p>
        </div>
    </div>
</body>
</html>";

        await SendEmailAsync(userEmail, subject, body);
    }

    /// <summary>
    /// Whether affected-user mail is enabled for this DEPLOYMENT
    /// (Email:NotifyUsersOnPermissionGrant). This switch outranks anything a module asks for, so a
    /// caller that offers the user-notification choice must be able to say on screen that a ticked
    /// box will send nothing - otherwise the control is decorative
    /// (docs/IntuneDeviceManagement-Plan.md D2 / S6, the idm-3 class).
    /// </summary>
    public virtual bool UserNotificationsEnabled => _notifyUsers;

    /// <summary>
    /// Tells a device's primary user that an administrator acted on their device
    /// (docs/IntuneDeviceManagement-Plan.md S6 / D2). Its own method rather than an overload of
    /// SendUserNotificationAsync or SendGroupMembershipUserNotificationAsync, whose subjects and
    /// bodies are mailbox- and group-specific.
    /// </summary>
    /// <remarks>
    /// RETURNS WHETHER IT ACTUALLY SENT, so the caller records a suppressed send instead of assuming
    /// one happened: false when the app-wide _notifyUsers gate is off, and false when the SMTP send
    /// failed. "Was the user told?" is an audit question, and a silent no is indistinguishable from
    /// a failure.
    ///
    /// The body names the device, the action and the ticket, and NOTHING else - deliberately no
    /// operator name and no action parameters. On a wipe it may reach a mailbox the user can now only
    /// open elsewhere, and on a lost or stolen device whoever holds it may read it.
    /// </remarks>
    public virtual async Task<bool> SendDeviceActionUserNotificationAsync(
        string userEmail,
        string deviceName,
        string actionLabel,
        string ticketNumber)
    {
        if (!_notifyUsers)
        {
            _logger.LogDebug("User notifications disabled, skipping device-action notification to {Email}", userEmail);
            return false;
        }

        var subject = $"[Exchange Admin] Device action: {actionLabel} - {deviceName}";
        var body = BuildDeviceActionUserBody(deviceName, actionLabel, ticketNumber);

        try
        {
            // SendEmailOrThrowAsync rather than the swallowing SendEmailAsync: a caller that must
            // report whether the mail went cannot be handed a true it did not earn.
            await SendEmailOrThrowAsync(userEmail, subject, body);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send device-action notification to {Email}: {Message}", userEmail, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The affected-user body for a device action. Extracted from the send so it is assertable
    /// without SMTP. Device, action, ticket - nothing else (D2), every value HTML-encoded.
    /// </summary>
    internal static string BuildDeviceActionUserBody(string deviceName, string actionLabel, string ticketNumber)
    {
        var h = (string s) => WebUtility.HtmlEncode(s ?? "");
        return $@"<html>
<body style=""font-family: Segoe UI, Arial, sans-serif; color: #222;"">
    <div style=""max-width: 640px; margin: 0 auto;"">
        <h2>Device action notice</h2>
        <p>An administrator has carried out the following action on a device registered to you:</p>
        <table style=""border-collapse: collapse;"" cellpadding=""6"">
            <tr><td><strong>Device</strong></td><td>{h(deviceName)}</td></tr>
            <tr><td><strong>Action</strong></td><td>{h(actionLabel)}</td></tr>
            <tr><td><strong>Ticket</strong></td><td>{h(ticketNumber)}</td></tr>
        </table>
        <p>If you were not expecting this, contact the IT Service Desk and quote the ticket number.</p>
        <p>This is an automated notification from Exchange Admin. Please do not reply to this email.</p>
    </div>
</body>
</html>";
    }

    /// <summary>
    /// Sends a reset Entra ID cloud-only account password to the account's owner
    /// (docs/CloudPasswordReset-Plan.md, "Delivery, and the ordering trap that comes with it").
    /// </summary>
    /// <remarks>
    /// Its own method rather than an overload: every other user-facing mail here is mailbox-,
    /// group- or device-specific, and none of them carries a credential.
    ///
    /// RETURNS WHETHER IT ACTUALLY SENT. The caller has already changed the password by the time
    /// this runs and cannot undo it, so "was the owner told?" decides whether the reset is
    /// reported as complete or as changed-but-undelivered. A silent false is indistinguishable
    /// from a failure and both must be recorded, never assumed.
    ///
    /// <paramref name="forceChangeAtNextSignIn"/> exists so the closing line matches what was
    /// actually done. It must never promise a prompt that will not appear: an owner expecting one
    /// and not getting it raises a ticket, and an owner not expecting one who meets it on a client
    /// that cannot service the change is locked out.
    ///
    /// The body names the account, the ticket and the password. It does NOT name the operator.
    /// </remarks>
    public virtual async Task<bool> SendCloudPasswordResetAsync(
        string ownerEmail,
        string targetUpn,
        string password,
        string ticketNumber,
        bool forceChangeAtNextSignIn)
    {
        if (!_notifyUsers)
        {
            // No address and no target in this line: the send did not happen, and the interesting
            // fact is the switch, not who it would have gone to.
            _logger.LogWarning("User notifications are disabled; a cloud password reset mail was NOT sent");
            return false;
        }

        var subject = $"[Exchange Admin] Password reset: {targetUpn}";
        var body = BuildCloudPasswordResetBody(targetUpn, password, ticketNumber, forceChangeAtNextSignIn);

        try
        {
            // SendEmailOrThrowAsync, not the swallowing SendEmailAsync: a caller that must report
            // whether a credential reached its owner cannot be handed a true it did not earn.
            await SendEmailOrThrowAsync(ownerEmail, subject, body);
            _logger.LogInformation("Cloud password reset mail accepted by the mail server for {TargetUpn}", targetUpn);
            return true;
        }
        catch (Exception ex)
        {
            // ex.Message only. The body held a password and must never reach a log through an
            // exception's ToString().
            _logger.LogError("Failed to send the cloud password reset mail for {TargetUpn}: {Message}", targetUpn, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// The owner's mail body for a cloud password reset. Extracted from the send so it is
    /// assertable without SMTP.
    /// </summary>
    /// <remarks>
    /// The closing line is conditional on <paramref name="forceChangeAtNextSignIn"/> and is the
    /// reason this takes the flag at all. Every value is HTML-encoded, the password included: a
    /// generated password contains characters from the separator set and an unencoded one could
    /// break the markup around it.
    /// </remarks>
    internal static string BuildCloudPasswordResetBody(
        string targetUpn,
        string password,
        string ticketNumber,
        bool forceChangeAtNextSignIn)
    {
        var h = (string s) => WebUtility.HtmlEncode(s ?? "");

        var closing = forceChangeAtNextSignIn
            ? "You will be asked to set a new password the first time you sign in with this one."
            : "This password will keep working until you change it. Please change it when convenient.";

        return $@"<html>
<body style=""font-family: Segoe UI, Arial, sans-serif; color: #222;"">
    <div style=""max-width: 640px; margin: 0 auto;"">
        <h2>Password reset</h2>
        <p>The password for the account below has been reset by an administrator.</p>
        <table style=""border-collapse: collapse;"" cellpadding=""6"">
            <tr><td><strong>Account</strong></td><td>{h(targetUpn)}</td></tr>
            <tr><td><strong>New password</strong></td><td><code>{h(password)}</code></td></tr>
            <tr><td><strong>Ticket</strong></td><td>{h(ticketNumber)}</td></tr>
        </table>
        <p>{h(closing)}</p>
        <p>If you were not expecting this, contact the IT Service Desk immediately and quote the ticket number.</p>
        <p>This is an automated notification from Exchange Admin. Please do not reply to this email.</p>
    </div>
</body>
</html>";
    }

    /// <summary>
    /// The path of the Downloadable Reports page, relative to the app's base URL. Must stay in step
    /// with the @page directive on Components/Pages/MessageTraceReports.razor.
    /// </summary>
    internal const string MessageTraceReportsPath = "message-analysis/reports";

    /// <summary>
    /// Notifies that a Message Analysis detail export is ready, with a LINK to the Downloadable
    /// Reports page. The export itself is deliberately NOT attached
    /// (docs/MessageTraceDownloadLink-Plan.md): the data stays behind the app's login gate, so an
    /// arbitrary notification recipient can never receive message content, only a pointer that
    /// demands authentication and MessageTrace access.
    ///
    /// Recipients come from the caller (the operator's submitted set, D4) and are used as given.
    /// The configured admin address is NOT merged in: admins are never recipients of trace data.
    /// Fail-soft: a send failure is logged, never thrown into the bulk-job result.
    /// </summary>
    public virtual async Task SendMessageTraceResultAsync(
        IReadOnlyList<string> recipients,
        int messageCount,
        string ticket,
        string performedBy,
        DateTime expiresAtUtc)
    {
        var to = NormalizeRecipients(recipients);
        if (to.Count == 0)
        {
            _logger.LogWarning("Message Analysis detail export has no recipient; skipping send");
            return;
        }

        var subject = $"[Exchange Admin] Message Analysis detail export ready - {messageCount} message(s) - Ticket #{ticket}";
        var body = BuildMessageTraceReadyBody(ResolveReportsUrl(), _appName, messageCount, ticket,
            performedBy, expiresAtUtc, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        foreach (var recipient in to)
        {
            await SendEmailAsync(recipient, subject, body);
        }
    }

    /// <summary>
    /// Notifies that a Message Analysis detail export could NOT be stored, so there is nothing to
    /// download and the trace must be re-run (openreview F1).
    ///
    /// This exists because removing the attachment made the saved file the sole delivery. Without
    /// this branch a disk-full or permissions failure would send a "ready" mail pointing at a file
    /// that never existed, which the reports page would render as Expired - indistinguishable from
    /// ordinary retention, leading the operator to conclude they simply waited too long.
    /// </summary>
    public virtual async Task SendMessageTraceFailureAsync(
        IReadOnlyList<string> recipients,
        int messageCount,
        string ticket,
        string performedBy)
    {
        var to = NormalizeRecipients(recipients);
        if (to.Count == 0)
        {
            _logger.LogWarning("Message Analysis detail export failure notice has no recipient; skipping send");
            return;
        }

        var subject = $"[Exchange Admin] Message Analysis detail export FAILED - {messageCount} message(s) - Ticket #{ticket}";
        var body = BuildMessageTraceFailureBody(messageCount, ticket, performedBy,
            DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        foreach (var recipient in to)
        {
            await SendEmailAsync(recipient, subject, body);
        }
    }

    /// <summary>
    /// Trims, drops blanks, and de-duplicates case-insensitively. Unlike the resolver this replaced,
    /// it adds nothing: the admin address is never merged into a trace-export recipient set, and a
    /// caller that supplies nothing gets no send rather than a silent fallback to some other mailbox.
    /// </summary>
    internal static IReadOnlyList<string> NormalizeRecipients(IEnumerable<string?>? recipients)
    {
        if (recipients is null)
            return [];

        return recipients
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .Select(e => e!.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Splits an operator-typed recipient box into addresses and reports the malformed ones.
    ///
    /// Comma or semicolon separated; the accepted set is exactly what
    /// <see cref="NormalizeRecipients"/> would keep, so what the page validates is what the send
    /// uses. A cleared box is valid and yields no addresses (plan D4: the pre-fill is a default, not
    /// a floor). Format only - no domain allow-listing, because the mail carries a login-gated link
    /// rather than the data (plan D4, "no domain allow-listing").
    /// </summary>
    internal static IReadOnlyList<string> ParseRecipientInput(string? input, out IReadOnlyList<string> invalid)
    {
        var bad = new List<string>();
        invalid = bad;

        if (string.IsNullOrWhiteSpace(input))
            return [];

        var candidates = input.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries);
        var good = new List<string>(candidates.Length);
        foreach (var candidate in candidates)
        {
            var trimmed = candidate.Trim();
            if (trimmed.Length == 0)
                continue;
            if (IsPlausibleAddress(trimmed))
                good.Add(trimmed);
            else
                bad.Add(trimmed);
        }

        return NormalizeRecipients(good);
    }

    /// <summary>
    /// A deliberately loose shape check: one at-sign, something either side, a dot in the domain, no
    /// whitespace. Rejecting valid-but-unusual addresses would block an operator from a delivery
    /// they are entitled to, and the address is only ever used as an SMTP recipient here.
    /// </summary>
    private static bool IsPlausibleAddress(string value)
    {
        if (value.Any(char.IsWhiteSpace))
            return false;

        var at = value.IndexOf('@');
        if (at <= 0 || at != value.LastIndexOf('@') || at == value.Length - 1)
            return false;

        var domain = value[(at + 1)..];
        var dot = domain.IndexOf('.');
        return dot > 0 && dot < domain.Length - 1;
    }

    /// <summary>
    /// The absolute URL of the Downloadable Reports page, or null when it cannot be built.
    ///
    /// Returns null - never a relative path - when Application:PublicBaseUrl is unset or is not an
    /// absolute URI (openreview F3). An email client has no origin to resolve a relative path
    /// against, so a bare "/message-analysis/reports" href renders as a dead link, which is worse
    /// than no link: it reads as a broken app and gives the operator nothing to act on. The caller
    /// falls back to prose. Deliberately does NOT guess a scheme and host, and deliberately does not
    /// fail the send - the job has already completed by this point and a mail-formatting problem
    /// must never change a job result.
    /// </summary>
    internal string? ResolveReportsUrl()
    {
        var baseUrl = _config["Application:PublicBaseUrl"];
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            _logger.LogWarning(
                "Application:PublicBaseUrl is not configured; the Message Analysis export email will describe the reports page in prose instead of linking to it");
            return null;
        }

        baseUrl = baseUrl.Trim();
        if (!Uri.TryCreate(baseUrl, UriKind.Absolute, out var parsed))
        {
            _logger.LogWarning(
                "Application:PublicBaseUrl '{BaseUrl}' is not an absolute URL; omitting the link from the Message Analysis export email rather than emitting one an email client cannot resolve",
                baseUrl);
            return null;
        }

        return $"{parsed.ToString().TrimEnd('/')}/{MessageTraceReportsPath}";
    }

    /// <summary>
    /// The ready-and-linked body. Extracted from the send so it is assertable without SMTP.
    /// Every interpolated value is HTML-encoded.
    /// </summary>
    internal static string BuildMessageTraceReadyBody(
        string? reportsUrl,
        string appName,
        int messageCount,
        string ticket,
        string performedBy,
        DateTime expiresAtUtc,
        string generatedAt)
    {
        var h = (string s) => WebUtility.HtmlEncode(s ?? "");
        var where = reportsUrl is null
            ? $@"<p>Sign in to {h(appName)} and open the <strong>Downloadable Reports</strong> page
           (Message Analysis) to download it.</p>"
            : $@"<p><a href=""{h(reportsUrl)}"">Open the Downloadable Reports page</a> to download it.</p>";

        return $@"<html>
<body style=""font-family: Segoe UI, Arial, sans-serif; color: #222;"">
    <div style=""max-width: 640px; margin: 0 auto;"">
        <h2>Message Analysis - Delivery Detail Export</h2>
        <p>The delivery-detail export you requested is ready.</p>
        {where}
        <p>You will be asked for a ticket number when you download it; the ticket is recorded with
           the download for audit. Downloading requires your Windows sign-in and Message Analysis
           access - the export is not attached to this email.</p>
        <table style=""border-collapse: collapse;"" cellpadding=""6"">
            <tr><td><strong>Messages</strong></td><td>{messageCount}</td></tr>
            <tr><td><strong>Ticket</strong></td><td>{h(ticket)}</td></tr>
            <tr><td><strong>Requested by</strong></td><td>{h(performedBy)}</td></tr>
            <tr><td><strong>Generated</strong></td><td>{h(generatedAt)}</td></tr>
            <tr><td><strong>Available until</strong></td><td>{h(expiresAtUtc.ToString("yyyy-MM-dd"))} (UTC)</td></tr>
        </table>
        <p>This is an automated notification from Exchange Admin. Please do not reply to this email.</p>
    </div>
</body>
</html>";
    }

    /// <summary>The save-failed body: no link, because there is nothing to download.</summary>
    internal static string BuildMessageTraceFailureBody(
        int messageCount,
        string ticket,
        string performedBy,
        string generatedAt)
    {
        var h = (string s) => WebUtility.HtmlEncode(s ?? "");

        return $@"<html>
<body style=""font-family: Segoe UI, Arial, sans-serif; color: #222;"">
    <div style=""max-width: 640px; margin: 0 auto;"">
        <h2>Message Analysis - Delivery Detail Export Failed</h2>
        <p>The delivery-detail export you requested could <strong>not be stored</strong>, so there is
           nothing to download. Please re-run the trace. If it fails again, contact your
           administrator - the server may be out of disk space or unable to write to the export
           directory.</p>
        <table style=""border-collapse: collapse;"" cellpadding=""6"">
            <tr><td><strong>Messages</strong></td><td>{messageCount}</td></tr>
            <tr><td><strong>Ticket</strong></td><td>{h(ticket)}</td></tr>
            <tr><td><strong>Requested by</strong></td><td>{h(performedBy)}</td></tr>
            <tr><td><strong>Attempted</strong></td><td>{h(generatedAt)}</td></tr>
        </table>
        <p>This is an automated notification from Exchange Admin. Please do not reply to this email.</p>
    </div>
</body>
</html>";
    }

    private async Task SendEmailAsync(
        string to,
        string subject,
        string htmlBody,
        byte[]? attachmentBytes = null,
        string? attachmentFileName = null)
    {
        try
        {
            await SendEmailOrThrowAsync(to, subject, htmlBody, attachmentBytes, attachmentFileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}: {Message}", to, ex.Message);
        }
    }

    private async Task SendEmailOrThrowAsync(
        string to,
        string subject,
        string htmlBody,
        byte[]? attachmentBytes = null,
        string? attachmentFileName = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_fromName, _fromAddress));
        message.To.Add(MailboxAddress.Parse(to));
        message.Subject = subject;

        var builder = new BodyBuilder { HtmlBody = htmlBody };
        if (attachmentBytes is not null && !string.IsNullOrWhiteSpace(attachmentFileName))
            builder.Attachments.Add(attachmentFileName, attachmentBytes);
        message.Body = builder.ToMessageBody();

        using var client = new SmtpClient();
        var secureOptions = _smtpUseSsl
            ? SecureSocketOptions.SslOnConnect
            : SecureSocketOptions.None;
        await client.ConnectAsync(_smtpHost, _smtpPort, secureOptions);

        if (!string.IsNullOrWhiteSpace(_smtpUser))
            await client.AuthenticateAsync(_smtpUser, _smtpPass);

        await client.SendAsync(message);

        // THE HANDOFF IS COMPLETE HERE. Everything below is cleanup, and a failure in cleanup
        // must not be reported as a failure to send.
        //
        // DisconnectAsync sends QUIT and waits for the server, which can throw on a dropped
        // connection AFTER the message has been accepted. This method's contract is "throws if
        // the mail was not handed over", and callers act on that: the cloud password reset
        // discards a live credential and audits a delivery failure when this throws. A failed
        // QUIT would have made it discard a password the owner had already received (review
        // finding cpr-5, HIGH).
        //
        // Absorbed rather than rethrown for every caller, not just that one: no caller of this
        // method wants a successful send reported as a failure because the socket closed badly.
        try
        {
            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning("SMTP disconnect after a successful send to {To} failed: {Message}", to, ex.Message);
        }

        _logger.LogInformation("Email sent to {To}: {Subject}", to, subject);
    }
}
