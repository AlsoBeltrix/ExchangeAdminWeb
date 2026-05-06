# ExchangeAdminWeb Design Document

**PROJECT ARCHITECT: Michael Coelho — Corp-IS**

Analog Devices Confidential Information. All rights reserved.

---

## Project Background

- ADI's Exchange administration workflows rely on manual PowerShell scripts run by helpdesk staff, creating audit gaps and operational risk from human error.
- ExchangeAdminWeb replaces these scripts with a governed web application providing mailbox permission management, migration operations, and lookup tools.
- The objective is to provide a secure, auditable, self-service interface for Exchange Online and hybrid on-premises administration that enforces policy controls (protected users, self-grant prevention, ticket tracking).
- The application connects to Exchange Online via an existing Azure App Registration using certificate-based authentication (no user credentials stored).

---

## Design Considerations

- System connects to both Exchange Online (EXO) and on-premises Exchange (hybrid environment).
- Exchange Online access uses the EXO PowerShell module with app-only certificate authentication — no interactive login required.
- On-premises Exchange access uses Kerberos authentication with credentials retrieved from Delinea Secret Server at runtime.
- IIS hosting with Windows Authentication provides zero-friction SSO for domain-joined helpdesk users.
- Blazor Server (interactive) was chosen over client-side to keep Exchange credentials and PowerShell execution server-side only.
- .NET 10 target framework for long-term support and performance.

---

## System Design – Deployment Overview

| Environment | Server | Specs | Role |
|---|---|---|---|
| PRD | ASHBEXUTIL1 | 4 CPU, 16 GB RAM, 100 GB HDD | IIS App Server |
| PRD | (Azure AD) | N/A | App Registration + Certificate |
| PRD | ASHBMBX8 | Existing | On-Prem Exchange endpoint |
| PRD | Secret Server | Existing | Credential vault (Delinea) |
| PRD | mailhost.analog.com | Existing | SMTP relay |

**Technology Stack:**

| Component | Technology |
|---|---|
| Framework | .NET 10, Blazor Server (InteractiveServer) |
| Hosting | IIS (in-process, Windows Auth) |
| Exchange Online | EXO PowerShell via System.Management.Automation |
| On-Prem Exchange | Remote PowerShell (Kerberos) via PSSession |
| Authentication | Windows Negotiate (AD/Kerberos SSO) |
| Authorization | AD Group membership (Security:AllowedGroups) |
| Audit Log | CSV on E:\WWWOutput (daily rotation) |
| Email Notifications | MailKit → mailhost.analog.com:25 (no TLS) |
| Credential Management | Delinea Secret Server API |

**URLs:**

| Environment | URL |
|---|---|
| Production | https://ashbexutil1/ExchangeAdminWeb |

---

## Deployment Diagram

![Deployment Diagram](images/deployment-diagram.png)

*Place network/architecture diagram here (Visio, draw.io, or similar)*

```
                          ┌─────────────────────┐
                          │   Helpdesk User      │
                          │   (Domain-joined)    │
                          └──────────┬───────────┘
                                     │ HTTPS + Windows Auth (Kerberos)
                                     ▼
                          ┌─────────────────────┐
                          │   IIS / .NET 10     │
                          │   ASHBEXUTIL1       │
                          │   (Blazor Server)   │
                          └──┬────┬────┬────┬───┘
                             │    │    │    │
          ┌──────────────────┘    │    │    └──────────────────┐
          ▼                       ▼    ▼                       ▼
┌─────────────────┐   ┌────────────┐  ┌──────────────┐  ┌──────────────┐
│ Exchange Online │   │ On-Prem    │  │ Delinea      │  │ SMTP Relay   │
│ (PowerShell)    │   │ Exchange   │  │ Secret Server│  │ mailhost     │
│                 │   │ ASHBMBX8   │  │              │  │              │
│ Cert Auth:      │   │ Kerberos   │  │ REST API     │  │ Port 25      │
│ CN=EXO-         │   │ via        │  │              │  │ No TLS       │
│ Automation      │   │ PSSession  │  │              │  │              │
└─────────────────┘   └────────────┘  └──────────────┘  └──────────────┘
          │
          ▼
┌─────────────────┐
│ Azure AD        │
│ App Reg:        │
│ 129fb786-...    │
│ analog.         │
│ onmicrosoft.com │
└─────────────────┘
```

---

## Security Considerations

### Users / Roles

| Role | Description | Access |
|---|---|---|
| Helpdesk Admin | Members of `ANALOG\ExchangeWebAdmins` or `ANALOG\iam` AD groups | Full application access |
| Protected Users | Configured in `Security:ExcludedUsers` (executives, service accounts) | Cannot be targeted by operations |
| End Users | Mailbox owners receiving notifications | No app access; email notifications only |

### Access Controls

- Windows Authentication (Negotiate/Kerberos) — no anonymous access permitted.
- Authorization enforced via AD group membership policy on every page.
- Self-grant prevention: operators cannot modify permissions on their own mailbox.
- Protected-user list refreshes from AD groups every 30 minutes.
- Identity comparison uses dot-insensitive matching across DOMAIN\sam and email formats.

### Authentication Flow

1. User accesses application → IIS negotiates Kerberos ticket.
2. Application verifies user is member of configured AllowedGroups.
3. If authorized → render page. If not → redirect to /access-denied ([AllowAnonymous]).
4. Exchange operations use app-only certificate auth (no user delegation).

### Secrets Management

| Secret | Storage | Access Method |
|---|---|---|
| EXO Certificate | LocalMachine\My certificate store | Certificate subject lookup |
| On-Prem Exchange credentials | Delinea Secret Server | REST API call at runtime |
| App Registration details | appsettings.json (AppId, Org) | Non-sensitive identifiers only |

### Data Classification

- No ITAR, PII, or regulated data processed by the application.
- Mailbox content is never read or stored — only permission metadata and migration status.
- Audit logs contain: operator identity, IP, timestamp, action, target, result.
- Message trace results are transient (displayed only, never persisted).

---

## Security Considerations (cont)

### Input Validation & Output Encoding

- All Exchange cmdlet parameters are passed via PowerShell SDK parameter binding (not string interpolation) — immune to injection.
- CSV audit output sanitizes formula-injection characters (=, +, -, @, tab, CR).
- Blazor Server renders all user-provided content with automatic HTML encoding.
- OOF message display strips HTML tags before rendering.

### Network Security

| Source | Destination | Port/Protocol | Purpose |
|---|---|---|---|
| ASHBEXUTIL1 | outlook.office365.com | 443/HTTPS | EXO PowerShell |
| ASHBEXUTIL1 | ASHBMBX8 | 80/HTTP | On-Prem Exchange PowerShell |
| ASHBEXUTIL1 | secretserver.ad.analog.com | 443/HTTPS | Delinea API |
| ASHBEXUTIL1 | mailhost.analog.com | 25/SMTP | Email notifications |
| Helpdesk users | ASHBEXUTIL1 | 443/HTTPS | Web application |

### Vulnerability Management

- Application runs on IIS with No Managed Code app pool (ASP.NET Core process model).
- Server patching follows corporate standards.
- NuGet dependencies monitored for vulnerabilities via `dotnet list --vulnerable`.
- No external JavaScript dependencies (Blazor Server, no client-side JS framework).

---

## Backup and Data Retention

- **Audit logs (E:\WWWOutput):** Daily CSV rotation. Retained per corporate records policy.
- **Application binaries (D:\inetpub\ExchangeAdminWeb):** Deployed from Git; no backup needed beyond source control.
- **Source code:** Hosted on internal Gitea (https://ashbexutil1/gitea/mcoelho/ExchangeAdminWeb).
- **Certificate (CN=EXO-Automation):** Managed via corporate PKI; renewal tracked separately.
- **No database:** Application is stateless beyond audit CSV files.
- **Disaster Recovery:** Redeploy from Git + restore audit logs from E: drive backup. Recovery time < 1 hour.

---

## Scope

### Application Features

| Feature | Type | Ticket Required | Admin Notification |
|---|---|---|---|
| Mailbox Permissions (Full Access, Send As) | Action | Yes | Yes |
| Calendar Permissions | Action | Yes | Yes |
| Migration Eligibility Check | Action | Yes | Yes |
| Migration Batch Creation/Management | Action | Yes | Yes |
| Out of Office Set/Clear | Action | Yes | Yes |
| Delegation Report | Read-only | No | No |
| Message Trace | Read-only | No | Yes |
| Recipient Lookup | Read-only | No | No |

### Integration Points

| System | Direction | Method | Purpose |
|---|---|---|---|
| Exchange Online | Bidirectional | EXO PowerShell (cert auth) | All cloud mailbox operations |
| On-Prem Exchange | Read + Write | Remote PowerShell (Kerberos) | Migration, size checks |
| Active Directory | Read | PowerShell AD module | Group membership, ITAR checks |
| Delinea Secret Server | Read | REST API | On-prem credentials |
| SMTP (mailhost) | Outbound | SMTP port 25 | Admin + user notifications |

### Application Screenshots

![Mailbox Permissions](images/mailbox-permissions.png)
![Migration Management](images/migration-management.png)
![Delegation Report](images/delegation-report.png)
![Message Trace](images/message-trace.png)
![Recipient Lookup](images/recipient-lookup.png)
![Out of Office](images/out-of-office.png)

### Out of Scope

- Mailbox content access or eDiscovery
- Distribution group management (future phase)
- Direct Graph API operations
- ServiceNow ticket validation (future phase)
- Multi-tenant support

---

## Business Purpose & Use Cases

### Business Purpose

- Replace manual PowerShell script execution with a governed, auditable web interface.
- Reduce operational errors (typos, wrong parameters, forgotten disconnects) in Exchange administration.
- Enforce policy controls that scripts cannot guarantee (protected users, self-grant prevention).
- Provide complete audit trail for compliance (who did what, when, from where, under which ticket).

### Use Cases

1. **Grant/revoke mailbox permissions** — Helpdesk receives ticket, adds Full Access or Send As, system logs and notifies.
2. **Migration management** — IT staff checks eligibility, creates batch, monitors status, completes/stops migrations.
3. **Troubleshooting** — Message trace for "did my email arrive?" questions; recipient lookup for mailbox location/type.
4. **Compliance** — Delegation report shows current state of all permissions on a mailbox for auditors.
5. **User management** — Set/clear OOF for terminated or on-leave employees.

### System Classification

| Attribute | Value |
|---|---|
| Replaces | Manual PowerShell scripts (no predecessor application) |
| Regulatory | Not SOX, PCI, ITAR, HIPAA, or FDA regulated |
| Data Classification | Internal (permission metadata only) |
| Business Continuity | Low — manual scripts remain available as fallback |
| Tier | Tier 3 (internal productivity tool) |

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
