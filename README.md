# ExchangeAdminWeb

ASP.NET Core 10 Blazor Server application for Exchange Online administration, mailbox/calendar permissions, group management, MFA operations, conference room setup, named locations, and Active Directory tasks through a self-service web interface. Provides 22 modules (21 operational + 1 config-only) covering EXO, Graph API, and on-prem AD operations.

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

### Exchange Migration (`/migration`)

Check migration eligibility and create move batches between Exchange Online and on-premises Exchange.

- Validates current mailbox location before eligibility decisions
- Checks on-prem mailbox and archive size before cloud migrations
- Move-back batches pass all configured databases from `OnPremTargetDatabases` in the Migration module config to Exchange for distribution
- Built-in move-back database defaults match the approved 2019 database list; no DAG lookup or space balancing is performed
- Section access keys: `MigrationCheck`, `MigrationCreate`, `MigrationManage`

### Message Analysis (`/message-analysis`)

Analyze message headers and search mail flow across Exchange Online and on-premises transport logs.

- Realtime trace queries both Exchange Online message trace and on-premises message tracking logs
- Optional ticket number is recorded in audit logs only; it does not call ServiceNow
- Historical-search reports are delivered only to the authenticated user's email address
- Header Analysis is the primary workflow and supports pasted headers plus `.eml` and `.msg` uploads
- Extracts Message-ID, sender, recipient, subject, routing hops, authentication details, spam/filtering clues, delivery-failure evidence, and all parsed header values
- Header analysis can populate or immediately run a trace using the parsed Message-ID and date window
- Historical Exchange Online searches are still submitted as background jobs for ranges beyond the realtime window
- Section access key: `MessageTrace`
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

- **Module Status** — enable/disable the module (global admin only)
- **Section Access** — which AD groups can use the module (global admin only)
- **Configuration** — module-specific settings (config fields)
- **Module Admins** — AD groups that can configure this module without global admin

Changes are persisted to `config/modules-enabled.json`, `config/sectionaccess.json`, and `config/module-config-{ModuleId}.json` and take effect immediately.
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

### M365 Group Management (`/m365-group-management`)

Create, modify, and delete Microsoft 365 groups via Graph API.

- Full lifecycle management for M365 (Unified) groups
- **Requires:** Graph app registration with `Group.ReadWrite.All` permission
- Section access key: `M365GroupManagement`

### Comms-10k (`/comms-10k`)

Dedicated bulk member replacement for broadcast distribution lists.

- CSV upload of new member list
- Resolves all entries before applying changes
- Confirmation step showing add/remove diff
- Atomic replacement via `Set-ADGroup -Replace` (full member swap in one AD operation)
- Uses Delinea credentials for Active Directory operations
- Section access key: `Comms10k`

### AD Attribute Editor (`/ad-attribute-editor`)

View and edit allowlisted Active Directory attributes for on-premises user accounts.

- Tiered access control with three granular permission levels (Level1, Level2, Level3)
- Configurable attribute allowlist via `config/ad-editable-attributes.json`
- Optional search base boundary restrictions
- Uses Delinea credentials for Active Directory operations
- Section access keys: `ADAttributeEditor`, `ADAttributeEditorLevel1`, `ADAttributeEditorLevel2`, `ADAttributeEditorLevel3`

### Licensing Updates (`/licensing-updates`)

Bulk update Exchange licensing SKU assignments (extensionAttribute11) via CSV upload.

- CSV upload with validation against allowed license types
- Configurable allowed license values (default: E5, EOP2+SOP2, F3, F3+EOP1)
- Uses Delinea credentials for Active Directory operations
- Section access key: `LicensingUpdates`

### Conference Rooms (`/conference-rooms`)

Room mailbox metadata configuration and booking policy management.

- Set room properties: city, building, capacity, floor label, timezone
- Apply booking policy templates (the `RoomType` enum in `Models/ConferenceRoomModels.cs`):
  - **Standard** — Open auto-booking (AllBookInPolicy), 180-day window
  - **Video** — Like Standard plus a video-room additional response
  - **Restricted** — Delegate/group-gated booking (BookInPolicy), 180-day window, site-aware
  - **Exception** — Restricted to an exception group, 1080-day window
  - **CEO** — CEO-group only; always clears existing calendar permissions, 1080-day window
  - **Executive** — Exec VP/admin groups, 1080-day window
- CSV bulk upload for multi-room setup
- Room list management (add/remove rooms from room lists)
- Section access key: `ConferenceRooms`

### Emergency Disable (`/emergency-disable`)

Rapidly disable a compromised user account across on-prem AD and Entra ID with session revocation.

- Disables the AD account and revokes Entra ID sessions in a single workflow
- Security team notification on every disable action
- **Requires:** AD Delinea secret for account disable and Graph Delinea secret for session revocation
- Section access key: `EmergencyDisable`

### DHCP Authorization (`/dhcp-authorization`)

Authorize or deauthorize DHCP servers in Active Directory.

- Authorize a server by IP/hostname to allow DHCP service in the domain
- Deauthorize a server to revoke DHCP serving rights
- Confirmation dialog (high-privilege operation)
- **Requires:** Module-specific Delinea secret (Enterprise Admin credentials)
- Section access key: `DhcpAuthorization`

### Named Locations (`/named-locations`)

Manage Entra ID Conditional Access named locations via Microsoft Graph API.

- Create, edit, and delete IP range locations (IPv4/IPv6 CIDR)
- Create, edit, and delete country/region locations
- Mark locations as trusted
- Full pagination support for large tenants
- **Requires:** Graph app registration with `Policy.ReadWrite.ConditionalAccess` permission
- Section access key: `NamedLocations`

### Account Lockout Remediation (`/account-lockout-remediation`)

Identify the source machine of an account lockout and log the affected accounts off the
implicated or scoped domain computers. Disabled by default; both permissions fail-closed.

- Discover lockout sources from Security 4740 events on the PDC Emulator (or named DCs)
- Log selected accounts off the implicated source machines
- Scoped sweep: log accounts off every machine in an OU/computer list (with a
  `MaxSweepTargets` cap and a WinRM fan-out throttle)
- Dry-run by default; execution requires a ticket number and typed "LOG OFF" confirmation
- Targets are re-resolved through Protected Principals with an immutable-GUID re-check
  immediately before logoff; all paths audited and traced
- Granular `Logoff` permission gates execution separately from `Access`
- **Requires:** module-scoped AD credential (`DelineaSecretId`) with rights to read lockout
  events, query sessions, and log off sessions; WinRM to target machines
- Section access keys: `AccountLockoutRemediation`, `AccountLockoutRemediationLogoff`

### Security & Compliance
- **Windows Authentication** — Seamless SSO with Active Directory
- **Group-based Authorization** — Restrict access to specific AD groups per module
- **Self-grant Prevention** — Users cannot grant themselves permissions
- **Protected User Lists** — Block modifications to C-suite/executive mailboxes (with AD group expansion)
- **Audit Logging** — JSONL format logs with full operation details (always active)
- **Extended Diagnostic Logging** — Configurable log level (None/Error/Warning/Info/Debug) with separate log file, viewable in Event Log page
- **Email Notifications** — Admin notifications on all operations, optional user notifications
- **Fail-closed Design** — Corrupt config or credential failures block operations rather than bypassing security

## Requirements

### Server Prerequisites
- **Windows Server** 2016+ (IIS with Windows Authentication)
- **.NET 10 SDK** and **ASP.NET Core 10 Runtime (Windows Hosting Bundle)**
- **PowerShell 7.4+**
- **ExchangeOnlineManagement PowerShell Module** (v3.7.0+ required for Get-MessageTraceV2)
- **SQLite command-line tools (`sqlite3.exe` on PATH)** — required by the deploy/promote
  scripts to make a verified, consistent online backup of the runtime config database
  (`config/exchangeadmin.db`) before each deploy. Install with `winget install SQLite.SQLite`.
  The deploy/promote scripts fail fast if `sqlite3` is not on PATH rather than fall back to an
  unverified file copy. (The app itself bundles its own SQLite engine and does not need this;
  it is a *deployment-host* dependency only.)

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
    "LogRoot": "D:\\Logs\\ExchangeAdminWeb",
    "RotationPeriod": "daily",
    "MaxFileMB": 50,
    "MaxFilesPerPeriod": 5
  },
  "OperationTrace": {
    "Enabled": true
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
    "AdminGroups": [
      "DOMAIN\\Exchange-Admins"
    ]
  }
}
```

**Important:** `appsettings.json` is excluded from git for security. Never commit production credentials.

**Required:** `Audit:LogRoot` must be set to an absolute path **outside the deploy folder**
(the app deploys to `D:\inetpub\<app name>`, which is the content root; logs must not live
under it or under `wwwroot`). There is no baked-in default: **the app will not start** if
`Audit:LogRoot` is unset or blank, so audit logs are never silently misplaced. Each
environment sets its own value.

### 4. Deploy to IIS

**Generic / new environments — use the standalone product installer** in **elevated
PowerShell**. It is environment-neutral, prompts for environment-specific values, and
preserves existing `appsettings.json` and `config/` fragments on update:

```powershell
.\tools\Install-ExchangeAdminWeb.ps1            # interactive fresh install or update
.\tools\Install-ExchangeAdminWeb.ps1 -PlanOnly  # dry run, no changes
```

**ADI environment only — `deploy.ps1`** is the ADI-specific deployment helper (it targets
ADI aliases/app pools and the `ExchangeAdminWebDev` dev path). Do not use it in other
environments; it is intentionally separate from the standalone installer.

```powershell
.\deploy.ps1
```

The deploy/install scripts will:
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
- **Bulk CSV Optimization:** Bulk operations borrow a single pooled session for the entire batch (not per-row), reducing 50-row CSV processing from minutes to seconds.
- **On-Prem Throttle:** On-premises Exchange operations limited to 2 concurrent PSSession connections with retry (3 attempts, exponential backoff).
- **Blazor Server:** Interactive Server rendering mode with per-circuit state.
- **Audit + Operation Trace:** Append-only JSONL files rotated daily/weekly/monthly; every audit transaction includes a correlated `operationId` plus structured `operation.*` records for SIEM ingestion.
- **Extended Logging:** Separate JSONL diagnostic log with runtime-configurable level (Admin Settings). Viewable from Event Log page.
- **Credential Management:** Delinea Secret Server SDK auth via Windows PasswordVault. Per-module secrets isolated in Secret Server.
- **Thread Safety:** PermissionValidator uses immutable collections with atomic swap; SectionAccessService uses in-memory cache invalidated on save.

## Configuration Details

### Group-Based Authorization

Each module is gated by its own section access groups. `Security:AllowedGroups` is only a backward-compatibility fallback for modules that are not fail-closed when no `config/sectionaccess.json` or legacy `Security:SectionAccess` block exists:

```json
"AllowedGroups": [
  "DOMAIN\\IT-Helpdesk",
  "DOMAIN\\Exchange-Admins",
  "DOMAIN\\Migration-Team"
]
```

- Groups are checked using Windows role claims
- Both simple names (`IT-Helpdesk`) and domain-qualified names (`DOMAIN\IT-Helpdesk`) are supported
- Once section access is configured, the section's groups are the gate; users do not also need to be in `AllowedGroups`
- Fail-closed modules deny access until explicit section access groups are configured

### Admin Groups

Members of `Security:AdminGroups` can access Admin Settings. Admin Event Log is controlled by its own `EventLog` section access policy:

```json
"AdminGroups": ["DOMAIN\\Exchange-Admins"]
```

- Empty or missing = Admin Settings is inaccessible to everyone (fail-closed).

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
  "NamedLocations": ["DOMAIN\\Exchange-Admins"],
  "GroupManagement": ["DOMAIN\\Exchange-Admins"],
  "GroupManagementOnPrem": ["DOMAIN\\Exchange-Admins"],
  "Comms10k": ["DOMAIN\\Exchange-Admins"],
  "ConferenceRooms": ["DOMAIN\\Exchange-Admins"],
  "DhcpAuthorization": ["DOMAIN\\Exchange-Admins"],
  "EventLog": ["DOMAIN\\Exchange-Admins"]
}
```

- **Fail-closed:** when section access is configured, missing or empty section keys deny access to that feature for all users
- **On-prem sections** (`MailboxPermissionsOnPrem`, `CalendarPermissionsOnPrem`) are always fail-closed even when no `SectionAccess` configuration exists at all
- **Migration hierarchy:** `MigrationCreate` requires MigrationCheck groups AND MigrationCreate groups; `MigrationManage` requires MigrationCheck groups AND MigrationManage groups
- NavMenu links and Home page cards are hidden for unauthorized sections
- Section access is managed per-module via `/module-config/{ModuleId}` (collapsible tree in sidebar)

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

All operations are logged as JSON Lines (.jsonl). Business audit records and diagnostic operation trace records are separate files:

**Audit location:** `<Audit:LogRoot>\ExchangeAdminWeb\exchangeadmin_YYYYMMDD.jsonl`

**Trace location:** `<Audit:LogRoot>\ExchangeAdminWeb\exchangeadmin_YYYYMMDD_trace.jsonl`

(`Audit:LogRoot` is a required setting; see the configuration section above.)

**Common audit fields:** `eventType`, `operationId`, `ts`, `user`, `ip`, `action`, `category`, `result`, `ticket`

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

**Operation trace records:** Each audit transaction writes structured trace events with the same `operationId`:
- `operation.start` — transaction accepted by the logging pipeline
- `operation.step` — significant step such as `AuditWritten`, plus shared backend steps where available
- `operation.complete` — final transaction result and elapsed duration

Trace fields are SIEM-friendly: `eventType`, `operationId`, `parentOperationId`, `module`, `action`, `stage`, `backend`, `command`, `target`, `ticket`, `result`, `durationMs`, `details`, and `errorType`. Raw exception messages are not written to trace records. Secret-like detail keys (`password`, `secret`, `token`, `apiKey`, `clientSecret`) are masked as `***`. Shared backend services also emit standalone operation trace records when no operation scope is active, so vault and EXO connection failures are still visible in the trace stream. Disable trace records with `OperationTrace:Enabled = false` if needed; audit records remain active.

**Rotation:**
- `daily` (default): New file each day
- `weekly`: New file each week
- `monthly`: New file each month
- `Audit:MaxFileMB` and `Audit:MaxFilesPerPeriod` cap both audit and trace file growth

**Extended diagnostics:** Admin Settings can enable a separate UI-viewable diagnostic stream for troubleshooting Delinea/on-prem connectivity. It defaults to off, writes to `exchangeadmin_YYYYMMDD_extended.jsonl`, rotates at 10 MB by default, and keeps 5 files per day including the active file. Tune with `ExtendedLog:MaxFileMB` and `ExtendedLog:MaxFilesPerDay` if needed. The queue is bounded and drops oldest entries under sustained bursts, so this is best-effort diagnostics, not an audit trail. Keep Admin Event Log access tightly scoped.

### Delinea Secret Server

The app uses one shared Delinea API bootstrap credential, then each module retrieves its own privileged secret by module config. Do not put on-prem Exchange, AD, DHCP, or Graph application credentials in global appsettings.

```json
"Delinea": {
  "SecretServerUrl": "https://secretserver.yourcompany.com/secretserver",
  "CredentialTarget": "Delinea_Client"
}
```

| Key | Purpose |
|-----|---------|
| `SecretServerUrl` | Base URL of Delinea/Thycotic Secret Server |
| `CredentialTarget` | Windows Credential Manager entry name storing Delinea API client credentials |

Modules that need privileged on-prem or AD access expose their own `DelineaSecretId` field on that module's config page. Those secrets must contain fields named `Username`, `Password`, and `Domain`. Graph-backed modules use their own Delinea secret containing `Tenant ID`, `Application ID`, and `Client Secret`.

Module secrets must be directly readable by the Delinea API bootstrap credential and must not require checkout, approval, or another interactive Secret Server workflow. The web app cannot complete a checkout prompt during background API calls.

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
helpdesk@company.com,john.doe@company.com,True,True,True
finance@company.com,jane.smith@company.com,True,False,False
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
├── Modules/           # Descriptor-based module system (the architecture)
│   ├── ModuleCatalog.cs        # All module descriptors (nav, auth, enablement)
│   └── AdminModuleDescriptor.cs # Descriptor record contract
├── Components/         # Blazor components
│   ├── Pages/         # One Razor page per module (MailboxPermissions, ConferenceRooms, ...)
│   └── Layout/        # Layout components
├── Authorization/     # Policy handlers and authorization wiring
├── Middleware/        # Request pipeline middleware
├── Models/            # Data models and enums
├── Services/          # Per-feature services (one per module + shared infra)
│   ├── AuditService.cs          # JSONL business audit records
│   ├── OperationTraceService.cs # Correlated operation trace records
│   ├── JsonlLogService.cs       # Shared JSONL writer
│   ├── ExoConnectionPool.cs     # Pooled Exchange Online connections
│   ├── EmailService.cs          # SMTP notifications
│   └── PermissionValidator.cs   # Security validation
├── config/            # Runtime config fragments (preserved across deploys)
├── tools/             # Standalone installer + ops scripts (Install-ExchangeAdminWeb.ps1)
├── tests/ps/          # Pester tests for PowerShell
├── docs/              # ProjectConstitution, specs, plans
├── .agents/           # Agent guidance (state.md, decisions.md, repo-map.json)
├── ExchangeAdminWeb.Tests/  # xUnit test project
├── Program.cs         # Application entry point
├── web.config         # IIS configuration
└── deploy.ps1         # ADI-specific deploy helper (generic install: tools/Install-ExchangeAdminWeb.ps1)
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
