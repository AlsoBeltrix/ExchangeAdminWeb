# ExchangeAdminWeb Design Document

**PROJECT ARCHITECT: Michael Coelho — Corp-IS**

Analog Devices Confidential Information. All rights reserved.

---

## Project Background

- Helpdesk and L2 support staff require the ability to perform Exchange administration tasks — mailbox permission grants, migrations, out-of-office management — that are restricted by security policy from being performed directly.
- ExchangeAdminWeb provides a gated, governed interface that allows authorized operators to perform these restricted operations within enforced guardrails: protected-user exclusions, self-grant prevention, mandatory ticket capture, comprehensive audit logging, and admin/end-user notifications.
- The application acts as a controlled delegation layer — operators never receive direct Exchange admin credentials. All Exchange operations execute server-side through a certificate-authenticated app registration, and on-premises operations use credentials retrieved at runtime from a secret vault.
- This document reflects the current implementation baseline as of May 6, 2026.

---

## Design Considerations

- System connects to both Exchange Online (EXO) and on-premises Exchange (hybrid environment).
- Exchange Online access uses the EXO PowerShell module with app-only certificate authentication — operators never handle Exchange credentials.
- On-premises Exchange access uses Kerberos authentication with credentials retrieved from Delinea Secret Server at runtime; credentials are never stored on disk or exposed to operators.
- IIS hosting with Windows Authentication provides zero-friction SSO for domain-joined helpdesk users.
- Blazor Server (interactive) was chosen over client-side to keep Exchange credentials and PowerShell execution server-side only — nothing sensitive reaches the browser.
- .NET 10 target framework for long-term support and performance.
- Action tools require ticket numbers for audit correlation.
- Exchange PowerShell operations run in isolated runspaces and disconnect Exchange Online / on-premises PSSessions in `finally` blocks to prevent session leaks.

---

## System Design – Technology Stack

| Component | Technology |
|---|---|
| Framework | .NET 10, Blazor Server (InteractiveServer) |
| Hosting | IIS (in-process, Windows Auth, No Managed Code app pool) |
| Exchange Online | EXO PowerShell via System.Management.Automation |
| On-Prem Exchange | Remote PowerShell (Kerberos) via PSSession |
| Authentication | Windows Negotiate (AD/Kerberos SSO) |
| Authorization | AD Group membership policy (Security:AllowedGroups) |
| Protected-User Enforcement | Configurable exclusion list with AD group expansion, 30-minute refresh, fail-closed |
| Audit Log | CSV on dedicated volume (daily rotation, formula-injection sanitized) |
| Email Notifications | MailKit → configured SMTP relay |
| Credential Management | Delinea Secret Server REST API (runtime retrieval only) |
| Certificate Auth | Azure App Registration with locally-installed X.509 certificate |

---

## Deployment Diagram

![Deployment Diagram](images/deployment-diagram.png)

*Place network/architecture diagram here (Visio, draw.io, or similar)*

```
                          ┌─────────────────────┐
                          │   Helpdesk / L2      │
                          │   (Domain-joined)    │
                          └──────────┬───────────┘
                                     │ HTTPS + Windows Auth (Kerberos)
                                     ▼
                          ┌─────────────────────┐
                          │ IIS / .NET 10       │
                          │ Blazor Server       │
                          │ (all operations     │
                          │  execute here)      │
                          └──┬────┬────┬────┬───┘
                             │    │    │    │
          ┌──────────────────┘    │    │    └──────────────────┐
          ▼                       ▼    ▼                       ▼
┌─────────────────┐   ┌────────────┐  ┌──────────────┐  ┌──────────────┐
│ Exchange Online │   │ On-Prem    │  │ Delinea      │  │ SMTP Relay   │
│ (PowerShell)    │   │ Exchange   │  │ Secret Server│  │              │
│                 │   │            │  │              │  │              │
│ Cert Auth via   │   │ Kerberos   │  │ REST API     │  │ Notifications│
│ App Reg         │   │ PSSession  │  │ (creds at    │  │ (admin +     │
│                 │   │            │  │  runtime)    │  │  end-user)   │
└─────────────────┘   └────────────┘  └──────────────┘  └──────────────┘
          │
          ▼
┌─────────────────┐
│ Microsoft       │
│ Entra ID        │
│ App             │
│ Registration    │
└─────────────────┘
```

---

## Access Control & Authorization

### User Roles

| Role | Who | What They Can Do |
|---|---|---|
| Authorized Operator | Members of configured AD groups (e.g., `ExchangeWebAdmins`, `iam`) | Execute all application features |
| Protected Users | Executives, service accounts, members of configured protected groups | Cannot be targeted by any write operation — enforced server-side |
| End Users (mailbox owners) | Any mailbox targeted by an operation | Receive email notifications; no application access |

### Authorization Enforcement

- **AD group gating:** Every page enforces group membership policy. Unauthorized users are redirected to an access-denied page.
- **Self-grant prevention:** Operators cannot modify their own mailbox permissions. Identity comparison is dot-insensitive and matches across DOMAIN\samAccountName and email formats.
- **Protected-user enforcement:** A configurable list of users and AD groups whose members cannot be targeted by write operations. Groups are expanded and cached with a 30-minute refresh cycle. If the protected list cannot be loaded, validation **fails closed** — all write operations are blocked until it recovers.
- **Protected-user scope:** Enforced on mailbox permission changes, calendar permission changes, and Out of Office Set/Clear actions.
- **No credential exposure:** Operators authenticate via Windows SSO only. They never see or handle Exchange admin credentials, certificate keys, or Secret Server tokens.

### Authentication Flow

1. User accesses application → IIS negotiates Kerberos ticket.
2. Application verifies user is a member of at least one configured AllowedGroup.
3. If authorized → render page. If not → redirect to /access-denied.
4. Exchange operations use app-only certificate auth (EXO) or vault-retrieved service credentials (on-prem). The operator's identity is recorded for audit but is not used for Exchange authentication.

---

## Audit Logging

### What Is Logged

Every operation — both read-only lookups and write actions — is recorded to a daily-rotating CSV file on a dedicated volume.

| Field | Description |
|---|---|
| Timestamp | UTC time of the operation |
| Operator | Windows identity (DOMAIN\username) of the authenticated user |
| IP Address | Client IP of the operator |
| Action | Operation type (e.g., AddFullAccess, SetOutOfOffice, RecipientLookup) |
| Target | Mailbox or recipient being acted upon |
| Affected User | User being granted/revoked access (for permission operations) |
| Ticket Number | ServiceNow or equivalent ticket reference (required for write actions) |
| Success | Boolean result |
| Error Detail | Error message if the operation failed |

### Security Properties

- CSV fields are sanitized against formula injection (leading `=`, `+`, `-`, `@`, tab, and CR characters are prefixed with an apostrophe).
- Audit logging failures do not silently swallow — they surface to the operator.
- Log volume is on a separate drive from the application to prevent disk-fill attacks from affecting application availability.

---

## Notifications

### Admin Notifications

Email notifications are sent to a configured admin distribution list for:

| Trigger | Purpose |
|---|---|
| Permission add/remove (mailbox, calendar) | Security awareness — sensitive access changes |
| Migration operations (create, complete, stop, resume, remove, clear) | Operational awareness |
| Out of Office set/schedule/clear | Awareness of changes to user mailboxes |
| Message trace searches | Audit-only (no admin email) |

### End-User Notifications

Mailbox owners receive email notification when:

- Permissions are granted or revoked on their mailbox
- Their Out of Office is changed by an operator

This ensures affected users are aware of changes made to their mailbox by helpdesk/L2 staff.

---

## Secrets Management

| Secret | Storage | Access Method |
|---|---|---|
| EXO Certificate (private key) | LocalMachine\My certificate store | Certificate subject lookup at runtime |
| On-Prem Exchange credentials | Delinea Secret Server | REST API call at runtime — never cached to disk |
| App Registration identifiers | appsettings.json | Non-sensitive identifiers only (AppId, Org) |

- No credentials are stored in source control, environment variables, or application configuration files.
- On-prem credentials are retrieved fresh from the vault for each operation requiring on-prem connectivity.

---

## Input Validation & Injection Protection

- All Exchange cmdlet parameters are passed via PowerShell SDK parameter binding (not string interpolation) — immune to command injection.
- CSV audit output sanitizes formula-injection characters.
- Blazor Server renders all user-provided content with automatic HTML encoding — no raw HTML output.
- Out of Office message display strips HTML tags before rendering (defense-in-depth against stored XSS in Exchange OOF messages).
- Message trace uses real-time `Get-MessageTrace` for ranges up to 10 days (results in-page, capped at 1,000) and `Start-HistoricalSearch` for ranges over 10 days (results emailed to user, up to 90 days).
- Exchange mailbox size parsing uses culture-invariant numeric parsing so server locale cannot distort values.

---

## Network Security

| Source | Destination | Port/Protocol | Purpose |
|---|---|---|---|
| Application server | Exchange Online PowerShell endpoint | 443/HTTPS | EXO PowerShell |
| Application server | On-premises Exchange endpoint | HTTP/HTTPS per config | On-prem Exchange PowerShell |
| Application server | Delinea Secret Server | 443/HTTPS | Credential vault API |
| Application server | SMTP relay | 25/SMTP or configured port | Notifications |
| Helpdesk/L2 users | Application server | 443/HTTPS | Web application |

---

## Application Features

### Write Operations (ticket required, audited, notified)

| Feature | Description | Protected-User Enforced |
|---|---|---|
| Mailbox Permissions | Grant/revoke Full Access, Send As | Yes |
| Calendar Permissions | Grant/revoke calendar folder access | Yes |
| Migration Eligibility Check | Validates mailbox readiness for cloud migration | No |
| Migration Batch Management | Create, complete, stop, resume, remove batches | No |
| Out of Office Set/Schedule/Clear | Manage auto-reply for a target mailbox | Yes |

### Read-Only Operations (no ticket required, audited)

| Feature | Description | Admin Notification |
|---|---|---|
| Delegation Report | Full Access, Send As, Calendar permissions for a mailbox | No |
| Message Trace | Sender/recipient message delivery status lookup | No |
| Recipient Lookup | Type, location (cloud/on-prem), size, archive, forwarding | No |
| Out of Office Check | Current auto-reply status and message text | No |

### Application Screenshots

![Mailbox Permissions](images/mailbox-permissions.png)
![Migration Management](images/migration-management.png)
![Delegation Report](images/delegation-report.png)
![Message Trace](images/message-trace.png)
![Recipient Lookup](images/recipient-lookup.png)
![Out of Office](images/out-of-office.png)

---

## Integration Points

| System | Direction | Method | Purpose |
|---|---|---|---|
| Exchange Online | Bidirectional | EXO PowerShell (cert auth) | All cloud mailbox operations |
| On-Prem Exchange | Read + Write | Remote PowerShell (Kerberos via vault creds) | Migration, mailbox stats, hybrid operations |
| Active Directory | Read | PowerShell AD module / Exchange group expansion | Protected-user group expansion |
| Delinea Secret Server | Read | REST API (HTTPS) | On-prem credential retrieval |
| SMTP relay | Outbound | SMTP | Admin + end-user notifications |

---

## Data Classification

- **No mailbox content** is read, stored, or transmitted by the application. Only permission metadata, migration status, recipient properties, and message trace metadata are processed.
- Out of Office message text is read/displayed transiently and submitted to Exchange for Set/Schedule actions; it is not persisted.
- All read-only lookup results are transient (displayed only, never persisted).
- Audit CSV files contain operational metadata only.

| Attribute | Value |
|---|---|
| Regulatory | Not SOX, PCI, ITAR, HIPAA, or FDA regulated |
| Data Classification | Internal operational metadata |
| Business Continuity | Low — manual PowerShell scripts remain available as fallback |
| Tier | Tier 3 (internal productivity tool) |

---

## Backup and Data Retention

- **Audit logs:** Daily CSV rotation on dedicated volume. Retained per corporate records policy.
- **Application binaries:** Deployed from source control; recoverable via redeploy.
- **Certificate (CN=EXO-Automation):** Managed via corporate PKI; renewal tracked separately.
- **No database:** Application is stateless beyond audit CSV files.
- **Disaster Recovery:** Redeploy from source control + restore audit logs from backup. Recovery time < 1 hour.

---

## Vulnerability Management

- Application runs on IIS with No Managed Code app pool (ASP.NET Core in-process model).
- Server patching follows corporate standards.
- NuGet dependencies monitored for vulnerabilities via `dotnet list --vulnerable`.
- No external JavaScript dependencies (Blazor Server renders entirely server-side).

---

## Test & Verification Baseline

Automated test coverage includes:
- Permission validation (self-grant prevention, protected-user enforcement)
- Identity normalization (dot-insensitive, cross-format matching)
- Protected-user cache refresh and fail-closed behavior
- CSV formula injection sanitization
- Lookup date/schedule validation
- Exchange size parsing (all EXO output formats, non-US culture regression)

Verification baseline: `dotnet build`, `dotnet test`, `dotnet format --verify-no-changes`.

---

## Out of Scope

- Mailbox content access or eDiscovery
- Distribution group management (future phase)
- Direct Graph API operations
- ServiceNow ticket validation (future phase — ticket capture is mandatory, validation is not)
- Multi-tenant support

---

## Required Documentation

| Document | Status |
|---|---|
| Architecture Diagram | Included above |
| Technology Stack | Included above |
| Data Flow / Integration Points | Included above |
| Ports and Protocols | Included above |
| Access Provisioning (Roles) | Included above |
| Vulnerability Scanning | Corporate standard (Qualys) |
| Configuration Scanning | N/A (no custom OS hardening) |
| Log Forwarding to SIEM | Not currently configured — audit CSV only |
| Patching Schedule | Corporate standard |
| Server Hardening | IIS lockdown (Windows Auth only, no anonymous) |
