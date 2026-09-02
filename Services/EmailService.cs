using System.Net;
using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace ExchangeAdminWeb.Services;

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
        _smtpPort = int.Parse(config["Email:SmtpPort"] ?? "25");
        _smtpUser = config["Email:SmtpUsername"] ?? "";
        _smtpPass = config["Email:SmtpPassword"] ?? "";
        _smtpUseSsl = bool.Parse(config["Email:SmtpUseSsl"] ?? "false");
        _fromAddress = config["Email:FromAddress"] ?? "noreply@example.com";
        _fromName = config["Email:FromName"] ?? "Exchange Admin";
        _adminEmail = config["Email:AdminNotificationEmail"] ?? "";
        _notifyUsers = bool.Parse(config["Email:NotifyUsersOnPermissionGrant"] ?? "false");
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

        var adminEmails = _adminEmail.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrWhiteSpace(e));

        foreach (var email in adminEmails)
        {
            await SendEmailAsync(email, subject, body);
        }
    }

    public async Task SendUserNotificationAsync(
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

    public async Task SendOwnerNotificationAsync(
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

    public async Task SendOofNotificationAsync(string targetEmail, string performedBy, string action)
    {
        if (!_notifyUsers)
            return;

        var h = (string s) => WebUtility.HtmlEncode(s ?? "");
        var isEnabled = action != "disabled";
        var headerText = isEnabled ? "Auto-Reply Enabled" : "Auto-Reply Disabled";
        var headerColor = isEnabled ? "#0078d4" : "#28a745";

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
            <p>Your mailbox auto-reply (Out of Office) has been <strong>{h(action)}</strong> by an administrator.</p>
            <p><strong>Changed by:</strong> {h(performedBy)}<br>
               <strong>Date:</strong> {DateTime.Now:MMMM dd, yyyy 'at' h:mm tt}</p>
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

        await SendEmailAsync(targetEmail, $"Your Auto-Reply Has Been {(isEnabled ? "Enabled" : "Disabled")}", body);
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
        await client.DisconnectAsync(true);

        _logger.LogInformation("Email sent to {To}: {Subject}", to, subject);
    }
}
