# Email Notifications & Security Features

## Email Notifications

### Admin Notifications
**Always sent** to the configured admin email address for every permission change operation.

Includes:
- **Action performed** (Add/Remove FullAccess, SendAs, Calendar permissions)
- **Status** (SUCCESS or FAILED)
- **Target mailbox** being modified
- **Affected user** receiving/losing access
- **Permission type** granted or revoked
- **Who performed the action** (Windows username)
- **IP address** of the person who triggered the change
- **Timestamp** (UTC)
- **Error details** (if the operation failed)

### User Notifications
**Configurable** — only sent when adding/granting permissions (not when removing).

Toggle in appsettings.json:
```json
"Email": {
  "NotifyUsersOnPermissionGrant": false  // Set to true to enable
}
```

When enabled, users receive a friendly email notification when they are granted access to someone's mailbox or calendar, with:
- What mailbox they now have access to
- What type of permission was granted
- Who granted the access
- A security notice: "If you were unaware of this change, contact the IT Service Desk"

## Security Features

### Access Control (Group-Based Authorization)

Restrict application access to specific Active Directory groups:

```json
"Security": {
  "AllowedGroups": [
    "IT-Helpdesk",
    "Exchange-Admins",
    "Service Desk Tier 2"
  ]
}
```

**How It Works:**
- Users must be authenticated via Windows Authentication (automatic SSO)
- After authentication, the app checks if the user is a member of any allowed group
- Group membership is checked using Windows/AD role claims
- If the user is not in any allowed group, they are redirected to an Access Denied page

**Supported Group Formats:**
- Simple group name: `"IT-Helpdesk"`
- Domain-qualified: `"ANALOG\\IT-Helpdesk"`
- Both formats are checked automatically

**Behavior:**
- If `AllowedGroups` is **empty or not configured**, all authenticated users are allowed (WARNING: not recommended for production)
- If `AllowedGroups` contains values, only members of those groups can access the application
- Non-members see a friendly Access Denied page with their username and contact information
- Authorization failures are logged for security auditing

**Testing:**
1. Add your group names to appsettings.json
2. Ensure users are members of at least one listed group in Active Directory
3. Users not in any group will be denied access after Windows Authentication succeeds
4. Check application logs for authorization decisions

### Ticket Number Requirement

All permission operations require a valid ticket number for audit trail purposes:
- Single operations: Ticket number field is **required** in the UI
- Bulk operations: Single ticket number applies to all rows in the CSV
- Ticket numbers are logged in all audit entries
- Ticket numbers appear in admin notification emails

### Self-Grant Prevention

Prevent users from granting permissions to themselves:

```json
"Security": {
  "PreventSelfGrant": true
}
```

**Behavior:**
- When enabled (default), users cannot grant themselves permissions to any mailbox or calendar
- Only applies to **Add/Set** operations, not Remove operations
- Validation matches usernames intelligently (strips domain/email format)
- Error message: "Access denied: You cannot grant permissions to yourself."
- Applies to both single operations and bulk CSV uploads
- Can be disabled by setting to `false` if your workflow requires self-grants

### Excluded Users Protection

Prevent modifications to C-suite or other sensitive mailboxes by configuring excluded users **and groups** in appsettings.json:

```json
"Security": {
  "ExcludedUsers": [
    "C-Suite",
    "Board of Directors",
    "Executives",
    "ceo@analog.com",
    "cfo@analog.com"
  ]
}
```

**Group Expansion (Automatic):**
- The app automatically detects if an entry is a **distribution group** or **mail-enabled security group**
- All group members are expanded and added to the exclusion list at startup
- Groups are expanded using the same Exchange Online connection as normal operations
- If group expansion fails (permissions, connectivity), the app logs a warning and continues with individual entries only

**Example:**
```json
"ExcludedUsers": ["C-Suite"]
```
At startup, the app:
1. Connects to Exchange Online
2. Queries `Get-Recipient -Identity "C-Suite"`
3. If it's a group, runs `Get-DistributionGroupMember -Identity "C-Suite"`
4. Adds all members (SMTP, UPN, SamAccountName) to the exclusion list
5. Any modifications to those members' mailboxes are blocked

**Behavior:**
- Any attempt to modify permissions on an excluded mailbox is immediately rejected
- Error message: "Access denied: [user] is protected and cannot be modified through this interface"
- Works with all identity formats: SMTP, UPN, or SamAccountName
- Applies to both single operations and bulk CSV uploads
- Failed attempts are logged in the audit log

**Identity Matching:**
The validator matches excluded users intelligently:
- `ceo@analog.com` matches `CEO@analog.com`, `ceo`, `ANALOG\ceo`, or `ceo@analog.onmicrosoft.com`
- Case-insensitive matching
- Extracts username from domain\user or user@domain formats

**Performance:**
- Group expansion happens **once** at application startup (first validation call)
- Expanded member list is cached in memory for the lifetime of the app
- No performance impact on permission operations after initialization

## SMTP Configuration

Configure email delivery in appsettings.json:

```json
"Email": {
  "SmtpHost": "smtp.analog.com",
  "SmtpPort": 25,
  "SmtpUsername": "",           // Leave empty for anonymous SMTP
  "SmtpPassword": "",           // Leave empty for anonymous SMTP
  "SmtpUseSsl": false,          // true for port 465/587 with SSL
  "FromAddress": "exchangeadmin@analog.com",
  "FromName": "Exchange Admin",
  "AdminNotificationEmail": "itadmin@analog.com",
  "NotifyUsersOnPermissionGrant": false
}
```

**Typical Configurations:**

**Internal SMTP Relay (No Auth):**
```json
"SmtpHost": "mail.analog.com",
"SmtpPort": 25,
"SmtpUsername": "",
"SmtpPassword": "",
"SmtpUseSsl": false
```

**External SMTP with Auth (Office 365):**
```json
"SmtpHost": "smtp.office365.com",
"SmtpPort": 587,
"SmtpUsername": "exchangeadmin@analog.com",
"SmtpPassword": "YourPassword",
"SmtpUseSsl": true
```

## Audit Logging

All operations are logged to a shared application folder with configurable rotation:

**Location:** `E:\WWWOutput\ExchangeAdminWeb\`

**Rotation:** Configurable in appsettings.json (`Audit:RotationPeriod`):
- **daily** (default): `exchangeadmin_20260429.csv`
- **weekly**: `exchangeadmin_2026W18.csv`
- **monthly**: `exchangeadmin_202604.csv`

**Format:** CSV (comma-separated values) with one entry per line, suitable for Excel or log analysis tools.

**Configuration:**
```json
"Audit": {
  "LogRoot": "E:\\WWWOutput",
  "RotationPeriod": "daily"
}
```

**CSV Columns:**
- TimestampUtc
- User
- TicketNumber
- Action
- TargetMailbox
- AffectedUser
- PermissionType
- AutoMapping (blank if not applicable)
- AccessRight (for calendar permissions, blank otherwise)
- Result (SUCCESS or FAILED)
- Error (error details if FAILED, blank otherwise)

Example log entry:
```csv
TimestampUtc,User,TicketNumber,Action,TargetMailbox,AffectedUser,PermissionType,AutoMapping,AccessRight,Result,Error
2026-04-29T14:23:45.1234567Z,jsmith,INC0001234,AddFullAccess+SendAs,helpdesk@analog.com,jdoe@analog.com,FullAccess+SendAs,True,,SUCCESS,
```

## Testing Email Configuration

To verify email is working, perform a test permission grant operation and check:
1. Admin receives notification email
2. If `NotifyUsersOnPermissionGrant` is `true`, user receives notification
3. Check application logs (`logs/app-.log`) for any SMTP errors

If emails are not being sent:
- Check application logs for errors
- Verify SMTP host/port is accessible from the web server
- Test SMTP credentials if using authenticated SMTP
- Ensure firewall allows outbound SMTP traffic
