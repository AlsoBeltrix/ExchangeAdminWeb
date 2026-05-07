# ExchangeAdminWeb

ASP.NET Core 10 Blazor Server application for managing Exchange Online mailbox and calendar permissions through a self-service web interface.

## Features

### Mailbox Permissions
- **Full Access** - Grant/revoke full mailbox access with optional AutoMapping
- **Send As** - Grant/revoke Send As permissions
- Single operations and bulk CSV upload support

### Calendar Permissions
- Set calendar sharing permissions (Owner, Editor, Reviewer, Limited Details, etc.)
- Remove calendar permissions
- Automatic calendar folder detection (supports international/localized folder names)
- Single operations and bulk CSV upload support

### Security & Compliance
- **Windows Authentication** - Seamless SSO with Active Directory
- **Group-based Authorization** - Restrict access to specific AD groups
- **Self-grant Prevention** - Users cannot grant themselves permissions
- **Protected User Lists** - Block modifications to C-suite/executive mailboxes (with AD group expansion)
- **Audit Logging** - CSV format logs with full operation details
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
  - Default: Uses ApplicationPoolIdentity (inherits from computer account)
  - Recommended: Configure app pool to run as domain service account with AD read access

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
    "ExcludedUsers": [
      "C-Suite",
      "ceo@yourcompany.com"
    ],
    "PreventSelfGrant": true,
    "AllowedGroups": [
      "DOMAIN\\IT-Helpdesk",
      "DOMAIN\\Exchange-Admins"
    ]
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

## Configuration Details

### Group-Based Authorization

Users must be members of at least one allowed AD group:

```json
"AllowedGroups": [
  "DOMAIN\\IT-Helpdesk",
  "DOMAIN\\Exchange-Admins"
]
```

- Groups are checked using Windows role claims
- Both simple names (`IT-Helpdesk`) and domain-qualified names (`DOMAIN\IT-Helpdesk`) are supported
- Empty list = all access denied (fail-closed). At least one group must be configured.

### Protected Users / Excluded Users

Prevent modifications to specific mailboxes:

```json
"ExcludedUsers": [
  "C-Suite",
  "vincent.roche@analog.com"
]
```

- Supports distribution groups (auto-expanded to members at startup)
- Supports individual users (SMTP, UPN, or SamAccountName)
- All formats are intelligently matched (case-insensitive)

### Audit Logging

All operations logged to CSV format:

**Location:** `E:\WWWOutput\ExchangeAdminWeb\exchangeadmin_YYYYMMDD.csv`

**Columns:**
- TimestampUtc, User, IPAddress, TicketNumber, Action, TargetMailbox, AffectedUser, PermissionType, AutoMapping, AccessRight, Result, Error

**Rotation:**
- `daily` (default): New file each day
- `weekly`: New file each week
- `monthly`: New file each month

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

See [CSV_FORMAT.md](CSV_FORMAT.md) for complete documentation.

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

See [NOTIFICATIONS_SECURITY.md](NOTIFICATIONS_SECURITY.md) for complete documentation on:
- Email notification configuration
- Group-based access control
- Self-grant prevention
- Protected user lists
- Audit logging format

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
- If using ApplicationPoolIdentity (default), ensure server computer account has domain access
- **To use domain service account:**
  ```powershell
  Import-Module WebAdministration
  Set-ItemProperty "IIS:\AppPools\ExchangeAdminWeb" -Name processModel.identityType -Value 3
  Set-ItemProperty "IIS:\AppPools\ExchangeAdminWeb" -Name processModel.userName -Value "DOMAIN\svc_exchangeadmin"
  Set-ItemProperty "IIS:\AppPools\ExchangeAdminWeb" -Name processModel.password -Value "password"
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
│   ├── AuditService.cs         # CSV audit logging
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

Internal use only - Analog Devices, Inc.

## Support

For issues or questions, contact the IT Service Desk or create an issue in the repository.
