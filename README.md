# ExchangeAdminWeb

ASP.NET Core 10 Blazor Server application for Exchange Online administration, mailbox/calendar permissions, group management, MFA operations, conference room setup, and Active Directory tasks through a self-service web interface. Currently provides 12 modules covering EXO, Graph API, and on-prem AD operations.

## Features

### Mailbox Permissions
- **Full Access** - Grant/revoke full mailbox access with optional AutoMapping
- **Send As** - Grant/revoke Send As permissions
- Single operations and bulk CSV upload support
- On-premises mailbox support for authorized power users (see below)

### Calendar Permissions
- Set calendar sharing permissions (Owner, Editor, Reviewer, Limited Details, etc.)
- Remove calendar permissions
- Automatic calendar folder detection (supports international/localized folder names)
- Single operations and bulk CSV upload support
- On-premises mailbox support for authorized power users (see below)

### On-Premises Permission Operations

When a target mailbox is detected as on-premises:

- **Regular users** see an escalation message directing them to contact the Exchange team.
- **Power users** (members of the `MailboxPermissionsOnPrem` or `CalendarPermissionsOnPrem` section-access groups) are prompted with a confirmation dialog and can proceed.
- Execution uses an on-prem PowerShell remoting session (`New-PSSession`) authenticated with credentials retrieved from Delinea Secret Server at runtime.
- Concurrency is throttled to 2 simultaneous on-prem operations.

### Admin Settings Page (`/admin-settings`)

Module enablement controls, gated by `Security:AdminGroups`.

### Per-Module Config Pages (`/module-config/{ModuleId}`)

Each module has its own config page (linked in the sidebar) with:

- **Section Access** — which AD groups can use the module (global admin only)
- **Configuration** — module-specific settings (config fields)
- **Module Admins** — AD groups that can configure this module without global admin

Changes are persisted to `config/sectionaccess.json` and `config/module-config.json` and take effect immediately.
Global admins see all module config links; module admins see only their delegated modules.

### Admin Event Log Page (`/admin-event-log`)

A read-only JSONL audit log viewer, also gated by `Security:AdminGroups`.

- Browse audit events by date (one file per rotation period).
- Filter by category, user, IP address, and result (Success/Failed).
- Displays full event detail in an expandable JSON view.

### MFA Reset (`/mfa-reset`)

Reset user MFA authentication methods via Microsoft Graph API.

- Lists all registered authentication methods for a user (phone, FIDO2, authenticator app, etc.)
- Confirmation step before deletion
- Deletes all non-password authentication methods in one operation
- **Requires:** Separate Graph app registration with `UserAuthenticationMethod.ReadWrite.All` application permission (per-module registration pattern)
- Section access key: `MfaReset`

### Group Management (`/group-management`)

Search and manage distribution lists, mail-enabled security groups, and Microsoft 365 groups.

- Three backends depending on group type:
  - **EXO** — Cloud-only distribution and mail-enabled security groups
  - **On-prem AD** — Synced groups modified via Delinea-authenticated AD operations
  - **Graph API** — Microsoft 365 (Unified) groups
- Add/remove members, view group details
- **Requires:** `GroupManagementOnPrem` section access for on-premises group modifications
- Section access key: `GroupManagement`

### Comms-10k (`/comms-10k`)

Dedicated bulk member replacement for broadcast distribution lists.

- CSV upload of new member list
- Resolves all entries before applying changes
- Confirmation step showing add/remove diff
- Atomic replacement via `Set-ADGroup -Replace` (full member swap in one AD operation)
- Uses Delinea credentials for Active Directory operations
- Section access key: `Comms10k`

### Conference Rooms (`/conference-rooms`)

Room mailbox metadata configuration and booking policy management.

- Set room properties: city, building, capacity, floor label, timezone
- Apply booking policy templates:
  - **Standard** — Default room booking behavior
  - **Workspace** — Hot-desk / workspace booking mode
  - **Restricted** — Delegate-approved booking only
- CSV bulk upload for multi-room setup
- Room list management (add/remove rooms from room lists)
- Section access key: `ConferenceRooms`

### DHCP Authorization (`/dhcp-authorization`)

Authorize or deauthorize DHCP servers in Active Directory.

- Authorize a server by IP/hostname to allow DHCP service in the domain
- Deauthorize a server to revoke DHCP serving rights
- Confirmation dialog (high-privilege operation)
- **Requires:** Module-specific Delinea secret (Enterprise Admin credentials)
- Section access key: `DhcpAuthorization`

### Security & Compliance
- **Windows Authentication** - Seamless SSO with Active Directory
- **Group-based Authorization** - Restrict access to specific AD groups
- **Self-grant Prevention** - Users cannot grant themselves permissions
- **Protected User Lists** - Block modifications to C-suite/executive mailboxes (with AD group expansion)
- **Audit Logging** - JSONL format logs with full operation details
- **Email Notifications** - Admin notifications on all operations, optional user notifications
- **Ticket Number Requirement** - All operations require a service ticket number

## Requirements

### Server Prerequisites
- **Windows Server** 2016+ (IIS with Windows Authentication)
- **.NET 10 SDK** and **ASP.NET Core 10 Runtime (Windows Hosting Bundle)**
- **PowerShell 7.4+**
- **ExchangeOnlineManagement PowerShell Module** (v3.0+)

### Exchange Online Prerequisites
- **Azure App Registration** with:
  - `Exchange.ManageAsApp` API permission (application permission, admin consented)
  - Certificate authentication configured
  - Service principal assigned **Exchange Administrator** role or scoped RBAC role
- **Certificate** installed in server's certificate store (`LocalMachine\My` or `CurrentUser\My`)
  - Must match `CertificateSubject` in appsettings.json
  - Private key must be accessible to IIS app pool identity

### Active Directory
- **AD Security Groups** for authorization (e.g., `IT-Helpdesk`, `Exchange-Admins`)
- Users must be authenticated via Windows Authentication (domain-joined)
- **ActiveDirectory PowerShell Module** (RSAT-AD-PowerShell) required for migration eligibility checks
- **IIS App Pool Identity** must have AD read permissions to query user group memberships
  - The deployment script requires `-ServiceAccount` on fresh install (prompts interactively if omitted); on upgrade it retains the existing app pool identity
  - Configure IIS manually if using ApplicationPoolIdentity or a gMSA instead

## Installation

### 1. Install Prerequisites

**Install .NET 10:**
```powershell
# Download and install .NET 10 SDK
# https://dotnet.microsoft.com/download/dotnet/10.0

# Download and install ASP.NET Core Windows Hosting Bundle
# https://dotnet.microsoft.com/download/dotnet/10.0
```

**Install IIS Features:**
```powershell
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WebServer -All
Enable-WindowsOptionalFeature -Online -FeatureName IIS-WindowsAuthentication -All
Enable-WindowsOptionalFeature -Online -FeatureName IIS-ASPNET45 -All
```

**Install PowerShell Modules:**
```powershell
# Exchange Online Management module
Install-Module -Name ExchangeOnlineManagement -Force -AllowClobber -Scope AllUsers

# Active Directory module (required for migration eligibility checks)
Install-WindowsFeature RSAT-AD-PowerShell
```

### 2. Configure Exchange Online App Registration

1. Create Azure App Registration with certificate authentication
2. Grant `Exchange.ManageAsApp` API permission (admin consent required)
3. Assign service principal **Exchange Administrator** role in Entra ID
4. Install certificate on web server

### 3. Configure Application Settings

Copy `appsettings.json.sample` to `appsettings.json` and configure:

```json
{
  "ExchangeOnline": {
    "AppId": "your-app-id-here",
    "Organization": "yourorg.onmicrosoft.com",
    "CertificateSubject": "CN=EXO-Automation"
  },
  "Audit": {
    "LogRoot": "E:\\WWWOutput",
    "RotationPeriod": "daily"
  },
  "Email": {
    "SmtpHost": "smtp.yourcompany.com",
    "SmtpPort": 25,
    "SmtpUseSsl": false,
    "FromAddress": "exchangeadmin@yourcompany.com",
    "FromName": "Exchange Admin",
    "AdminNotificationEmail": "itadmin@yourcompany.com",
    "NotifyUsersOnPermissionGrant": false
  },
  "Security": {
    "AllowedGroups": [
      "DOMAIN\\IT-Helpdesk",
      "DOMAIN\\Exchange-Admins",
      "DOMAIN\\Migration-Team"
    ],
    "SectionAccess": {
      "MailboxPermissions": ["DOMAIN\\Exchange-Admins"],
      "CalendarPermissions": ["DOMAIN\\Exchange-Admins"],
      "MigrationCheck": ["DOMAIN\\Exchange-Admins", "DOMAIN\\Migration-Team"],
      "MigrationCreate": ["DOMAIN\\Exchange-Admins", "DOMAIN\\Migration-Team"],
      "MigrationManage": ["DOMAIN\\Exchange-Admins"],
      "DelegationReport": ["DOMAIN\\Exchange-Admins", "DOMAIN\\IT-Helpdesk"],
      "MessageTrace": ["DOMAIN\\Exchange-Admins", "DOMAIN\\IT-Helpdesk"],
      "RecipientLookup": ["DOMAIN\\Exchange-Admins", "DOMAIN\\IT-Helpdesk"],
      "OutOfOffice": ["DOMAIN\\Exchange-Admins"],
      "MfaReset": ["DOMAIN\\Exchange-Admins"],
      "GroupManagement": ["DOMAIN\\Exchange-Admins"],
      "Comms10k": ["DOMAIN\\Exchange-Admins"],
      "ConferenceRooms": ["DOMAIN\\Exchange-Admins"],
      "DhcpAuthorization": ["DOMAIN\\Exchange-Admins"]
    }
  }
}
```

**Important:** `appsettings.json` is excluded from git for security. Never commit production credentials.

### 4. Deploy to IIS

Run the deployment script in **elevated PowerShell**:

```powershell
.\deploy.ps1
```

This script will:
- Stop the application pool
- Build and publish the application
- Create/configure the IIS application pool
- Create the web application under Default Web Site
- Enable Windows Authentication and disable Anonymous Authentication
- Grant app pool access to certificate private key
- Grant app pool write access to audit log folder
- Restart the application pool

**Default URL:** `http://yourserver/ExchangeAdminWeb`

### 5. Configure IIS Windows Authentication (if needed)

The deploy script configures Windows Authentication automatically. If authentication issues occur:

```powershell
# Ensure only NTLM provider is enabled
Import-Module WebAdministration
Clear-WebConfiguration -Filter '/system.webServer/security/authentication/windowsAuthentication/providers' -PSPath 'IIS:\' -Location 'Default Web Site/ExchangeAdminWeb'
Add-WebConfigurationProperty -Filter '/system.webServer/security/authentication/windowsAuthentication/providers' -Name '.' -Value @{value='NTLM'} -PSPath 'IIS:\' -Location 'Default Web Site/ExchangeAdminWeb'
Set-WebConfigurationProperty -Filter '/system.webServer/security/authentication/windowsAuthentication' -Name 'useAppPoolCredentials' -Value 'True' -PSPath 'IIS:\' -Location 'Default Web Site/ExchangeAdminWeb'
```

## Architecture

- **EXO Connection Pool:** 5 pooled PowerShell runspaces for Exchange Online operations; connections idle longer than 20 minutes are automatically recycled.
- **On-Prem Throttle:** On-premises Exchange operations are limited to 2 concurrent PSSession connections.
- **Blazor Server:** Interactive Server rendering mode with per-circuit state.
- **Audit:** Append-only JSONL files rotated daily/weekly/monthly; read by the admin event log viewer.

## Configuration Details

### Group-Based Authorization

Users must be members of at least one `AllowedGroups` entry to access the application at all:

```json
"AllowedGroups": [
  "DOMAIN\\IT-Helpdesk",
  "DOMAIN\\Exchange-Admins",
  "DOMAIN\\Migration-Team"
]
```

- Groups are checked using Windows role claims
- Both simple names (`IT-Helpdesk`) and domain-qualified names (`DOMAIN\IT-Helpdesk`) are supported
- Empty list = all access denied (fail-closed). At least one group must be configured.

### Admin Groups

Members of `Security:AdminGroups` can access the Admin Settings and Admin Event Log pages:

```json
"AdminGroups": ["DOMAIN\\Exchange-Admins"]
```

- Empty or missing = admin pages are inaccessible to everyone (fail-closed).

### Section-Level Access (SectionAccess)

Each application feature is independently gated by AD group membership via its section access groups, managed on each module's config page (`/module-config/{ModuleId}`). The config is stored in `config/sectionaccess.json`:

```json
"SectionAccess": {
  "MailboxPermissions": ["DOMAIN\\Exchange-Admins"],
  "CalendarPermissions": ["DOMAIN\\Exchange-Admins"],
  "MailboxPermissionsOnPrem": ["DOMAIN\\Exchange-Admins"],
  "CalendarPermissionsOnPrem": ["DOMAIN\\Exchange-Admins"],
  "MigrationCheck": ["DOMAIN\\Exchange-Admins", "DOMAIN\\Migration-Team"],
  "MigrationCreate": ["DOMAIN\\Exchange-Admins", "DOMAIN\\Migration-Team"],
  "MigrationManage": ["DOMAIN\\Exchange-Admins"],
  "DelegationReport": ["DOMAIN\\Exchange-Admins", "DOMAIN\\IT-Helpdesk"],
  "MessageTrace": ["DOMAIN\\Exchange-Admins", "DOMAIN\\IT-Helpdesk"],
  "RecipientLookup": ["DOMAIN\\Exchange-Admins", "DOMAIN\\IT-Helpdesk"],
  "OutOfOffice": ["DOMAIN\\Exchange-Admins"],
  "MfaReset": ["DOMAIN\\Exchange-Admins"],
  "GroupManagement": ["DOMAIN\\Exchange-Admins"],
  "GroupManagementOnPrem": ["DOMAIN\\Exchange-Admins"],
  "Comms10k": ["DOMAIN\\Exchange-Admins"],
  "ConferenceRooms": ["DOMAIN\\Exchange-Admins"],
  "DhcpAuthorization": ["DOMAIN\\Exchange-Admins"]
}
```

- **Fail-closed:** missing or empty section keys deny access to that feature for all users
- **On-prem sections** (`MailboxPermissionsOnPrem`, `CalendarPermissionsOnPrem`) are always fail-closed even when no `SectionAccess` configuration exists at all
- **Migration hierarchy:** `MigrationCreate` requires MigrationCheck groups AND MigrationCreate groups; `MigrationManage` requires MigrationCheck groups AND MigrationManage groups
- NavMenu links and Home page cards are hidden for unauthorized sections
- Section access is managed per-module via `/module-config/{ModuleId}` (not Admin Settings)
- Section access can be managed at runtime via the Admin Settings page, which writes to `config/sectionaccess.json` (takes precedence over `appsettings.json`)

### Protected Users / Excluded Users

Prevent modifications to specific mailboxes. Configure via **Module Config** page under **Mailbox Permissions** → `Excluded Users` field (comma-separated). Applies to both mailbox and calendar permission operations:

```
C-Suite, Board of Directors, ceo@example.com
```

- Supports distribution groups (auto-expanded to members on first use, cached 30 min)
- Supports individual users (SMTP, UPN, or SamAccountName)
- All formats are intelligently matched (case-insensitive)
- Falls back to `Security:ExcludedUsers` in appsettings.json if module config is not set
- Cache is invalidated immediately when module config is saved via UI

### Audit Logging

All operations logged as JSON Lines (.jsonl):

**Location:** `E:\WWWOutput\ExchangeAdminWeb\exchangeadmin_YYYYMMDD.jsonl`

**Common fields:** `ts`, `user`, `ip`, `action`, `category`, `result`, `ticket`

**Category-specific fields:**
- MailboxPermission: `target`, `affectedUser`, `permissionType`, `autoMapping`
- CalendarPermission: `target`, `affectedUser`, `accessRight`
- MigrationCheck: `target`, `status`, `reasons`
- MigrationBatch: `batchName`, `direction`, `userCount`, `autoStart`, `autoComplete`
- MigrationAction: `target`
- Lookup: `target`
- AdminSettings: `section`, `added`, `removed`
- MfaReset: `target`, `methodsRemoved`
- GroupManagement: `target`, `member`, `operation`, `backend`
- Comms10k: `target`, `membersAdded`, `membersRemoved`
- ConferenceRooms: `target`, `properties`, `policyTemplate`
- DhcpAuthorization: `target`, `operation`

Null fields are omitted. `error` appears only on failure.

**Rotation:**
- `daily` (default): New file each day
- `weekly`: New file each week
- `monthly`: New file each month

### Delinea Secret Server (On-Prem Credentials)

On-prem Exchange operations retrieve credentials from Delinea Secret Server:

```json
"Delinea": {
  "SecretServerUrl": "https://secretserver.yourcompany.com/secretserver",
  "ExchangeSecretId": 0,
  "CredentialTarget": "Delinea_Client"
}
```

| Key | Purpose |
|-----|---------|
| `SecretServerUrl` | Base URL of Delinea/Thycotic Secret Server |
| `ExchangeSecretId` | Secret ID containing on-prem Exchange service account (Username, Password, Domain fields) |
| `CredentialTarget` | Windows Credential Manager entry name storing Delinea API client credentials |

The secret must contain fields named `Username`, `Password`, and `Domain`.

### Application Settings

```json
"Application": {
  "PathBase": "/ExchangeAdminWeb",
  "ContactEmail": "exchangeadmin@yourcompany.com"
}
```

| Key | Purpose |
|-----|---------|
| `Application:PathBase` | IIS sub-application path |
| `Application:ContactEmail` | Displayed in the UI as the support contact (nav footer) |

### Email Notifications

**Admin Notifications (always sent):**
- Every permission operation (success or failure)
- Includes full details: who, what, when, IP address, ticket number

**User Notifications (optional):**
- Set `NotifyUsersOnPermissionGrant: true` to enable
- Sent when permissions are granted or removed
- Friendly email with mailbox details and security notice

## Bulk Operations

### CSV Format - Mailbox Permissions

**Add/Remove:**
```csv
Target,User,FullAccess,SendAs,AutoMapping
helpdesk@company.com,john.doe@company.com,Yes,Yes,Yes
finance@company.com,jane.smith@company.com,Yes,No,No
```

See [docs/CSV_FORMAT.md](docs/CSV_FORMAT.md) for complete documentation.

### CSV Format - Calendar Permissions

**Set:**
```csv
Target,User,AccessRight
exec@company.com,assistant@company.com,Editor
manager@company.com,team@company.com,Reviewer
```

**Remove:**
```csv
Target,User
exec@company.com,old-assistant@company.com
```

## Security Features

Configure email notifications, group-based access control, self-grant prevention, protected user lists, and audit logging in `appsettings.json`.

## Troubleshooting

### "401 Unauthorized" when accessing the app
- Verify Windows Authentication is enabled in IIS for the application
- Check user is member of an allowed AD group
- Verify `AllowedGroups` configuration in appsettings.json
- Check IIS logs: `C:\inetpub\logs\LogFiles\W3SVC1\`

### "Cannot connect to Exchange Online"
- Verify certificate is installed and accessible to app pool identity
- Check certificate subject matches `CertificateSubject` in appsettings.json
- Verify app registration has `Exchange.ManageAsApp` permission
- Verify service principal has Exchange Administrator role
- Check application logs: `D:\inetpub\ExchangeAdminWeb\logs\app-*.log`

### "User not found" errors
- Ensure ExchangeOnlineManagement module is installed
- Verify service principal permissions in Exchange Online
- Check user exists in Exchange Online (Get-Mailbox)

### Emails not sending
- Check SMTP host/port is accessible from web server
- Verify firewall allows outbound SMTP traffic
- Check application logs for SMTP errors
- Test SMTP credentials if using authenticated SMTP

### Migration eligibility checks show AD group warning
- Ensure ActiveDirectory PowerShell module is installed: `Install-WindowsFeature RSAT-AD-PowerShell`
- Verify IIS app pool identity has AD read permissions
- If using ApplicationPoolIdentity, ensure server computer account has domain access
- **To use domain service account:**
  ```powershell
  Import-Module WebAdministration
  Set-ItemProperty "IIS:\AppPools\ExchangeAdminWeb" -Name processModel.identityType -Value 3
  Set-ItemProperty "IIS:\AppPools\ExchangeAdminWeb" -Name processModel.userName -Value "DOMAIN\svc_exchangeadmin"
  Set-ItemProperty "IIS:\AppPools\ExchangeAdminWeb" -Name processModel.password -Value "<secure-password>"
  ```
- Check application logs for detailed AD errors: `D:\inetpub\ExchangeAdminWeb\logs\app-*.log`
- Test AD access: Run `Get-ADUser -Identity username` in PowerShell as the app pool identity

## Development

### Local Development

1. Clone repository
2. Copy `appsettings.json.sample` to `appsettings.json`
3. Configure settings (or use appsettings.Development.json)
4. Run: `dotnet run`
5. Access: `http://localhost:5226`

**Note:** Windows Authentication works automatically on domain-joined dev machines using IIS Express or Kestrel with `NegotiateDefaults`.

### Project Structure

```
ExchangeAdminWeb/
├── Components/         # Blazor components
│   ├── Pages/         # Razor pages (Home, MailboxPermissions, CalendarPermissions)
│   └── Layout/        # Layout components
├── Models/            # Data models and enums
├── Services/          # Business logic
│   ├── ExchangeService.cs      # PowerShell EXO operations
│   ├── AuditService.cs         # JSONL audit logging
│   ├── EmailService.cs         # SMTP notifications
│   └── PermissionValidator.cs  # Security validation
├── wwwroot/           # Static assets
├── Program.cs         # Application entry point
├── web.config         # IIS configuration
└── deploy.ps1         # Deployment script
```

### Tech Stack

- **ASP.NET Core 10** - Blazor Server with Interactive Server rendering
- **Authentication** - Windows Authentication (Negotiate/NTLM)
- **Authorization** - Role-based with AD groups
- **Exchange Operations** - PowerShell SDK with ExchangeOnlineManagement module
- **Logging** - Serilog (file + console)
- **Email** - MailKit/MimeKit
- **CSV** - CsvHelper

## License

For internal administrative use. Configure all environment-specific settings before deployment.

## Support

For issues or questions, contact the IT Service Desk or create an issue in the repository.
