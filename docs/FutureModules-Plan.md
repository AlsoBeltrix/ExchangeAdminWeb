# Future Modules Plan

## Overview

This plan describes modules that should be built on the infrastructure established by the AD Attribute Editor implementation (v2.1.0). All future modules should reuse:

- **ProtectedPrincipalService** — centralized fail-closed protection checks
- **ModuleCredentialService** — Delinea-backed module-scoped credentials
- **OperationTraceService** — structured operation step logging
- **AuditService** — JSONL audit with operation ID correlation
- **ModuleCatalog** — self-describing module registration with dynamic authorization

The AD Attribute Editor proved the pattern for: identity resolution, source-of-authority detection, allowlisted edits, diff preview, fail-closed protection checks, audit, and operation tracing. Future modules should follow this same pattern.

---

## Module 1: Licensing Updates

### Origin

Convert the standalone `D:\source\LicensingUpdates` application into an ExchangeAdminWeb module. The standalone app is a .NET 9 minimal API that bulk-updates `extensionAttribute11` (Exchange licensing SKU) via CSV upload.

### Current Standalone Behavior

- Accepts CSV file upload (first column = user identifier)
- Assigns one of four license types: E5, EOP2+SOP2, F3, F3+EOP1
- Writes `extensionAttribute11` on each user's AD object
- Uses app pool identity for AD writes (no explicit credential)
- Logs to Serilog file sink at `E:\WWWOutput\licensing\audit_.log`
- Saves uploaded CSVs to `E:\WWWOutput\licensing\`
- Authorization: single hardcoded AD group (`ANALOG\sanjay_reports`)

### Module Design

**Catalog Descriptor:**

- Id: `LicensingUpdates`
- DisplayName: `Licensing Updates`
- Route: `licensing-updates`
- Category: `Exchange`
- SortOrder: `450`
- EnabledByDefault: `false`
- MainPermission: `LicensingUpdates`, FailClosed: `true`
- ConfigFields:
  - `DelineaSecretId` (required): AD credential for extensionAttribute11 writes
  - `LicenseAttribute` (optional, default `extensionAttribute11`): target attribute name
  - `AllowedLicenseTypes` (optional): comma-separated valid license values; defaults to `E5,EOP2+SOP2,F3,F3+EOP1`

**Key Differences From Standalone:**

| Aspect | Standalone | Module |
|--------|-----------|--------|
| Auth | Hardcoded group | Dynamic section access via ModuleCatalog |
| Credentials | App pool ambient | Delinea-backed module credential |
| Logging | Separate Serilog sink | ExchangeAdminWeb AuditService |
| UI | Vanilla HTML/JS SPA | Blazor Server component |
| Protection | None | ProtectedPrincipalService check before write |
| Preview | None | Diff preview with ticket requirement |

**Service: `LicensingUpdatesService`**

```csharp
Task<LicenseBulkResult> ProcessCsvAsync(
    Stream csvStream, string fileName, string licenseType,
    string performedBy, string ip);
```

Responsibilities:
- Parse CSV (first column = identifier, skip header rows containing "user", "name", "email", "sam")
- Validate license type against configured allowed values
- For each user:
  - Resolve identity via Get-ADUser (use LDAP filter with escaped input)
  - Run ProtectedPrincipalService.CheckAsync — skip protected users with per-row error
  - Read current extensionAttribute11 value
  - Write new value if changed
  - Record per-row result (success, skipped-protected, not-found, error)
- Audit the batch operation with summary counts
- Return structured result with per-user detail

**UI: `Components/Pages/LicensingUpdates.razor`**

Layout:
- License type selector (dropdown of configured types)
- CSV file upload with drag-drop
- Ticket number (mandatory)
- "Preview" button → shows parsed user list with current values
- "Apply" button → executes writes, shows results table
- Results table: User, Status, Previous Value, New Value, Error

States:
- Not started
- File selected / parsed (preview)
- Processing (progress bar)
- Complete (results with export)

**Migration Path:**

1. Build the module in ExchangeAdminWeb
2. Verify with test CSV in dev
3. Once stable, decommission the standalone app in IIS
4. Redirect traffic or remove the old app pool

---

## Module 2: Emergency Disable

### Purpose

Rapidly disable a compromised or at-risk user account across on-prem AD and Entra ID. This is the high-urgency counterpart to the AD Attribute Editor's low-risk attribute editing.

### Design Principles

- Must complete in under 30 seconds for the critical path
- Fail-closed: if any step cannot confirm completion, flag for manual follow-up
- Protected principals block applies (cannot disable break-glass accounts)
- Requires separate, elevated Delinea credential (not shared with AD Attribute Editor)
- All actions reversible via a paired "Re-enable" workflow (Phase 2)

### Catalog Descriptor

- Id: `EmergencyDisable`
- DisplayName: `Emergency Disable`
- Route: `emergency-disable`
- Category: `Identity & Access`
- SortOrder: `740`
- EnabledByDefault: `false`
- MainPermission: `EmergencyDisable`, FailClosed: `true`
- ConfigFields:
  - `DelineaSecretId`: AD credential with account disable permissions
  - `GraphDelineaSecretId`: Graph credential with User.ReadWrite.All
  - `NotifySecurityTeam`: email address for immediate notification
  - `RequireTicket`: whether ticket is mandatory (default true)

### Actions Performed

1. Resolve identity (same resolver as AD Attribute Editor)
2. Protected principal check (fail-closed)
3. Disable AD account (`Set-ADUser -Enabled $false`)
4. Reset password to random (prevent credential reuse)
5. Revoke Entra ID sessions (Graph: `revokeSignInSessions`)
6. Disable Entra ID account (Graph: `PATCH /users/{id}` → `accountEnabled: false`)
7. Remove from all conditional access exclusion groups (optional)
8. Audit all steps with operation trace
9. Notify security team immediately
10. Display summary with per-step status

### Dependencies

- ProtectedPrincipalService (from Phase 1)
- ModuleCredentialService
- GraphTokenClient (existing pattern from MfaReset/NamedLocations)
- On-prem AD cmdlets (existing pattern from GroupManagement)

---

## Module 3: Test Account Creator

### Purpose

Provision standardized test accounts in AD with predefined attributes, group memberships, and licensing. Ensures test accounts are distinguishable from production accounts and automatically expire.

### Design Principles

- All test accounts created in a dedicated OU (configurable)
- Naming convention enforced (e.g., `test-{purpose}-{date}`)
- Automatic expiration date set at creation (configurable max lifetime)
- Test accounts marked with a sentinel attribute for easy identification
- Protected principal rules should NOT protect test accounts (separate OU)
- No persistent database — AD is the source of truth

### Catalog Descriptor

- Id: `TestAccountCreator`
- DisplayName: `Test Account Creator`
- Route: `test-account-creator`
- Category: `Identity & Access`
- SortOrder: `760`
- EnabledByDefault: `false`
- MainPermission: `TestAccountCreator`, FailClosed: `true`
- ConfigFields:
  - `DelineaSecretId`: AD credential with user creation permissions
  - `TargetOU`: DN of the OU where test accounts are created
  - `MaxLifetimeDays`: maximum account expiration (default 90)
  - `NamingPrefix`: prefix for test account names (default `test-`)
  - `DefaultLicenseType`: license to assign (default `F3`)
  - `DefaultGroups`: comma-separated groups to add test accounts to

### Operations

- Create: generate account in target OU with expiration, set attributes, assign groups
- List: show existing test accounts (filter by OU + naming prefix)
- Extend: push expiration date forward (within max lifetime)
- Delete: remove expired or no-longer-needed test accounts
- Audit all operations

---

## Module 4: Recipient Permissions Report (Enhancement)

### Purpose

Extend the existing DelegationReport module to support scheduled reports and CSV export with filtering by delegate, permission type, and date range.

This is an enhancement to an existing module, not a new module. Listed here because it depends on the same infrastructure patterns.

### Additions

- Configurable scheduled export (daily/weekly CSV to file share)
- Filter by: specific delegate, specific mailbox, permission type, date granted
- "Stale permissions" view: permissions granted > N days ago with no recent access
- Export to CSV button

---

## Implementation Priority

| Priority | Module | Effort | Risk | Justification |
|----------|--------|--------|------|---------------|
| 1 | Licensing Updates | Low | Low | Replacing existing standalone app; proven functionality; low-risk attribute write |
| 2 | Emergency Disable | Medium | Medium | Security-critical; requires careful testing; elevated privilege |
| 3 | Test Account Creator | Medium | Low | Quality-of-life; no production impact; contained to test OU |
| 4 | Recipient Report Enhancement | Low | Low | Incremental improvement to existing module |

---

## Shared Infrastructure Checklist

Before starting any module above, verify:

- [x] ProtectedPrincipalService operational with central config
- [x] ModuleCredentialService working with Delinea
- [x] OperationTraceService emitting structured steps
- [x] AuditService logging with operation ID correlation
- [x] ModuleCatalog accepting new module registrations
- [x] Deploy scripts preserving config files
- [x] AD Attribute Editor pattern proven end-to-end
- [ ] Admin UI for protected principals management (deferred from AD Attribute Editor Phase 1 — build before Emergency Disable)

---

## Credential Isolation Rules

Each module MUST use its own Delinea secret. Never share credentials across modules:

| Module | Purpose | Minimum AD Permissions |
|--------|---------|----------------------|
| ADAttributeEditor | Read/write allowlisted attrs | Write on allowlisted attributes |
| LicensingUpdates | Write extensionAttribute11 | Write on extensionAttribute11 |
| EmergencyDisable | Disable accounts, reset passwords | Account Operators or delegated disable + password reset |
| TestAccountCreator | Create/delete in test OU | Create/delete child objects in target OU |
| GroupManagement | Group membership changes | Write membership on target groups |
| DhcpAuthorization | DHCP server objects | Enterprise Admin (existing) |

The ProtectedPrincipalService uses a separate read-only directory credential (`Security:ProtectedPrincipalDirectoryReadSecretId`) that must never be reused for writes.
