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

    public EmailService(IConfiguration config, ILogger<EmailService> logger)
    {
        _config = config;
        _logger = logger;
        _smtpHost = config["Email:SmtpHost"] ?? "localhost";
        _smtpPort = int.Parse(config["Email:SmtpPort"] ?? "25");
        _smtpUser = config["Email:SmtpUsername"] ?? "";
        _smtpPass = config["Email:SmtpPassword"] ?? "";
        _smtpUseSsl = bool.Parse(config["Email:SmtpUseSsl"] ?? "false");
        _fromAddress = config["Email:FromAddress"] ?? "noreply@analog.com";
        _fromName = config["Email:FromName"] ?? "Exchange Admin";
        _adminEmail = config["Email:AdminNotificationEmail"] ?? "";
        _notifyUsers = bool.Parse(config["Email:NotifyUsersOnPermissionGrant"] ?? "false");
    }

    public async Task SendAdminNotificationAsync(
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
        <h2>{headerText}</h2>
    </div>
    <div class=""content"">
        <table>
            <tr><td>Ticket Number</td><td><strong>{ticketNumber}</strong></td></tr>
            <tr><td>Action</td><td>{action}</td></tr>
            <tr><td>Status</td><td>{(success ? "SUCCESS" : "FAILED")}</td></tr>
            <tr><td>Target Mailbox</td><td>{targetMailbox}</td></tr>
            <tr><td>Affected User</td><td>{affectedUser}</td></tr>
            <tr><td>Permission Type</td><td>{permissionType}</td></tr>
            <tr><td>Performed By</td><td>{performedBy}</td></tr>
            <tr><td>IP Address</td><td>{ipAddress}</td></tr>
            <tr><td>Timestamp (UTC)</td><td>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</td></tr>
            {(string.IsNullOrWhiteSpace(errorDetail) ? "" : $"<tr><td>Error</td><td style=\"color: red;\">{errorDetail}</td></tr>")}
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
            <h2>{headerText}</h2>
        </div>
        <div class=""content"">
            <p>Hello,</p>
            <p>Your access to the following mailbox has been {actionWord}:</p>
            <div class=""details"">
                <strong>Mailbox:</strong> {targetMailbox}<br>
                <strong>Permission:</strong> {permissionType}<br>
                <strong>{(isGrant ? "Granted" : "Removed")} by:</strong> {performedBy}<br>
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
            <p>© {DateTime.Now.Year} Analog Devices. All rights reserved.</p>
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
            <h2>{headerText}</h2>
        </div>
        <div class=""content"">
            <p>Hello,</p>
            <p>The following user has been {actionWord} your {resourceType}:</p>
            <div class=""details"">
                <strong>User:</strong> {grantedUser}<br>
                <strong>Permission:</strong> {permissionType}<br>
                <strong>{(isGrant ? "Granted" : "Removed")} by:</strong> {performedBy}<br>
                <strong>Date:</strong> {DateTime.Now:MMMM dd, yyyy 'at' h:mm tt}
            </div>
            {permissionDetails}
            <div class=""warning"">
                <strong>⚠️ Security Notice:</strong> If you did not authorize this change or have concerns about this access, please contact the IT Service Desk immediately.
            </div>
        </div>
        <div class=""footer"">
            <p>This is an automated notification from Exchange Admin. Please do not reply to this email.</p>
            <p>© {DateTime.Now.Year} Analog Devices. All rights reserved.</p>
        </div>
    </div>
</body>
</html>";

        await SendEmailAsync(ownerEmail, subject, body);
    }

    private async Task SendEmailAsync(string to, string subject, string htmlBody)
    {
        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(_fromName, _fromAddress));
            message.To.Add(MailboxAddress.Parse(to));
            message.Subject = subject;

            var builder = new BodyBuilder { HtmlBody = htmlBody };
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
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to send email to {To}: {Message}", to, ex.Message);
        }
    }
}
