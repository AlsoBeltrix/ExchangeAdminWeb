# Future Modules Plan

Status: Implemented (history). Evidence: the modules this backlog proposed now exist in
`Modules/ModuleCatalog.cs` — Licensing Updates (`LicensingUpdates`), Emergency Disable
(`EmergencyDisable`), Test Account Creator (`TestAccountPool`), and the Recipient
Permissions Report enhancement (`RecipientLookup`/`DelegationReport`). Header added
retroactively 2026-06-17 from catalog presence (AC15 drift sweep), not a fresh
plan-vs-code audit. Note: `TestAccountPool` is queued for removal (owner direction; see
`.agents/state.md`) — this plan is history and is not the authority for that decision.

## Overview

This plan describes modules that should be built on the infrastructure established by the AD Attribute Editor implementation (v2.1.x). All future modules should reuse:

- **ProtectedPrincipalService** — centralized fail-closed protection checks
- **ModuleCredentialService** — Delinea-backed module-scoped credentials
- **OperationTraceService** — structured operation step logging
- **AuditService** — JSONL audit with operation ID correlation
- **ModuleCatalog** — self-describing module registration with dynamic authorization

The AD Attribute Editor established the intended pattern for: identity resolution, source-of-authority detection, allowlisted edits, diff preview, fail-closed protection checks, audit, and operation tracing. That implementation underwent external review and several correctness fixes were applied (partial-write atomicity, audit completeness, group matching precision, full identity resolution for protection checks). Future modules should follow the corrected pattern, not copy pre-fix code.

---

## Shared Infrastructure Checklist

Before starting any module below, verify:

- [x] ProtectedPrincipalService operational with central config
- [x] ModuleCredentialService working with Delinea
- [x] OperationTraceService emitting structured steps
- [x] AuditService logging with operation ID correlation
- [x] ModuleCatalog accepting new module registrations
- [x] Deploy scripts preserving config files
- [x] AD Attribute Editor atomic single-call writes (post-review fix)
- [x] Full audit on both success and failure paths (post-review fix)
- [x] Precise DN-based group matching with fail-closed on expansion errors (post-review fix)
- [x] Full identity resolution in PermissionValidator when Group/OU rules exist (post-review fix)
- [ ] Admin UI for protected principals management (build before Emergency Disable)
- [ ] End-to-end integration test against a real AD environment

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
  - `AllowedLicenseTypes` (optional): comma-separated valid license values; defaults to `E5,EOP2+SOP2,F3,F3+EOP1`

**Hard-coded target attribute:** The module writes exclusively to `extensionAttribute11`. This is NOT configurable. A configurable attribute name would bypass the hard-denylist/allowlist model and turn a license SKU module into an arbitrary AD editor. If a different attribute is needed in the future, it requires a code change with review, not a config toggle.

**Ticket handling:** Ticket number is mandatory for audit trail. Tickets are recorded in the audit log but NOT validated against ServiceNow. This is an audit-only field — no external API call.

**Key Differences From Standalone:**

| Aspect | Standalone | Module |
|--------|-----------|--------|
| Auth | Hardcoded group | Dynamic section access via ModuleCatalog |
| Credentials | App pool ambient | Delinea-backed module credential |
| Logging | Separate Serilog sink | ExchangeAdminWeb AuditService |
| UI | Vanilla HTML/JS SPA | Blazor Server component |
| Protection | None | ProtectedPrincipalService check before write |
| Preview | None | Bound-object preview with ticket requirement |

**Service: `LicensingUpdatesService`**

```csharp
Task<LicensePreviewResult> PreviewCsvAsync(
    Stream csvStream, string fileName, string licenseType,
    string performedBy, string ip);

Task<LicenseBulkResult> ApplyCsvAsync(
    LicensePreviewResult preview, string ticket,
    string performedBy, string ip);
```

**Bound-object semantics (required):**

The preview and apply phases MUST use bound-object identity:

1. `PreviewCsvAsync` resolves each CSV row to a full `ResolvedDirectoryPrincipal` (DN + ObjectGUID)
2. The preview result contains the bound identity for each row
3. `ApplyCsvAsync` re-reads each object by ObjectGUID/DN immediately before write
4. If the re-read object doesn't match the preview snapshot (renamed, moved, deleted), that row fails closed
5. ProtectedPrincipalService.CheckAsync runs at apply-time, not just preview-time

This prevents the case where a CSV identity resolves to user A at preview but user B at apply due to rename/replication.

**Responsibilities per row:**
- Resolve identity via Get-ADUser with LDAP filter (escaped input)
- Bind to DN + ObjectGUID
- Run ProtectedPrincipalService.CheckAsync — skip protected users with per-row error
- Read current extensionAttribute11 value
- At apply: re-read by DN, re-check protection, write new value if changed
- Record per-row result (success, skipped-protected, not-found, unchanged, error)
- Audit the batch operation with summary counts and per-row detail

**UI: `Components/Pages/LicensingUpdates.razor`**

Layout:
- License type selector (dropdown of configured types)
- CSV file upload with drag-drop
- Ticket number (mandatory, audit-only, no ServiceNow validation)
- "Preview" button → resolves users, shows parsed user list with current vs proposed values
- "Apply" button → executes writes with bound-object re-read, shows results table
- Results table: User, Status, Previous Value, New Value, Error

States:
- Not started
- File selected / parsed (preview with bound identities)
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
- Reversibility requires a durable pre-action snapshot (see below)

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

**Ticket handling:** Ticket number is mandatory. Recorded in audit log. No ServiceNow validation — audit-only field.

### Durable Pre-Action Snapshot (Required)

Before mutating any state, the service MUST capture and persist a snapshot:

```json
{
  "operationId": "...",
  "timestamp": "2026-05-29T14:30:00Z",
  "actor": "admin",
  "ticket": "INC1234567",
  "target": {
    "userPrincipalName": "user@contoso.com",
    "distinguishedName": "CN=User,OU=...",
    "objectGuid": "..."
  },
  "preState": {
    "adAccountEnabled": true,
    "adPasswordLastSet": "2026-04-01T...",
    "entraAccountEnabled": true,
    "entraSignInSessionsValid": true,
    "conditionalAccessExclusions": ["group-id-1", "group-id-2"]
  },
  "actions": [
    { "step": "DisableAD", "result": "Success" },
    { "step": "ResetPassword", "result": "Success" },
    ...
  ]
}
```

This snapshot is written to the audit log root BEFORE mutations begin and updated as each step completes. It enables:
- Accurate re-enable workflow (Phase 2) that restores exact prior state
- Forensic trail of what was changed and when
- Recovery from partial-completion scenarios

### Actions Performed

1. Resolve identity (full DN + ObjectGUID binding)
2. Protected principal check (fail-closed)
3. **Capture durable pre-action snapshot**
4. Disable AD account (`Set-ADUser -Enabled $false`)
5. Reset password to random (prevent credential reuse)
6. Revoke Entra ID sessions (Graph: `revokeSignInSessions`)
7. Disable Entra ID account (Graph: `PATCH /users/{id}` → `accountEnabled: false`)
8. Remove from conditional access exclusion groups (optional, record which groups)
9. Update snapshot with per-step results
10. Audit all steps with operation trace
11. Notify security team immediately
12. Display summary with per-step status

### Re-Enable Workflow (Phase 2)

- Reads the durable snapshot from the disable operation
- Restores exact prior state (not blindly enabling — if account was already disabled before emergency disable, it stays disabled)
- Requires separate authorization check
- Requires ticket
- Cannot re-enable if snapshot is missing or corrupt (fail closed)

### Dependencies

- ProtectedPrincipalService (from Phase 1)
- ModuleCredentialService
- GraphTokenClient (existing pattern from MfaReset/NamedLocations)
- On-prem AD cmdlets (existing pattern from GroupManagement)

---

## Module 3: Test Account Creator

### Purpose

Provision standardized test accounts in AD with predefined attributes, group memberships, and licensing. Ensures test accounts are distinguishable from production accounts and have a defined lifecycle including cleanup.

### Design Principles

- All test accounts created in a dedicated OU (configurable)
- Naming convention enforced (e.g., `test-{purpose}-{date}`)
- Expiration date set at creation (configurable max lifetime)
- Test accounts marked with a sentinel attribute (`extensionAttribute15 = "TESTACCOUNT"`) for identification
- Operations constrained to TargetOU and sentinel-marked objects only — any operation resolving outside that boundary fails closed
- AD is the source of truth for account state
- Automated cleanup deletes expired accounts (not just disables)

### Boundary Enforcement

Test account operations MUST be constrained to:
1. Objects whose DN is under the configured `TargetOU`
2. Objects marked with the sentinel attribute value

Any operation that resolves an identity outside these boundaries fails closed. This is NOT a bypass of protected-principal checks — it is an additional boundary constraint. ProtectedPrincipalService still runs on all targets. If an operator accidentally configures a TargetOU that overlaps with a protected OU, the protected-principal check blocks the operation.

### Catalog Descriptor

- Id: `TestAccountCreator`
- DisplayName: `Test Account Creator`
- Route: `test-account-creator`
- Category: `Identity & Access`
- SortOrder: `760`
- EnabledByDefault: `false`
- MainPermission: `TestAccountCreator`, FailClosed: `true`
- ConfigFields:
  - `DelineaSecretId`: AD credential with user creation/deletion permissions in target OU
  - `TargetOU`: DN of the OU where test accounts are created
  - `MaxLifetimeDays`: maximum account expiration (default 90)
  - `NamingPrefix`: prefix for test account names (default `test-`)
  - `SentinelAttribute`: attribute name for marking test accounts (default `extensionAttribute15`)
  - `SentinelValue`: value written to sentinel attribute (default `TESTACCOUNT`)
  - `DefaultLicenseType`: license to assign (default `F3`)
  - `DefaultGroups`: comma-separated groups to add test accounts to
  - `CleanupGraceDays`: days after expiration before deletion (default 7)

### Operations

- **Create:** Generate account in target OU with expiration, set sentinel attribute, assign groups, set license
- **List:** Show existing test accounts (query TargetOU with sentinel filter)
- **Extend:** Push expiration date forward (within max lifetime from original creation)
- **Delete:** Immediately remove a test account (must be in TargetOU + have sentinel)
- **Cleanup:** Scheduled/on-demand deletion of accounts expired beyond grace period

### Cleanup Mechanism

Cleanup is triggered two ways:
1. **On-demand:** Admin clicks "Clean Up Expired" button on the list view
2. **On list load:** The list view shows expired accounts with a visual indicator and "days overdue" count

Cleanup performs:
1. Query TargetOU for accounts with sentinel attribute where `accountExpires` < (now - CleanupGraceDays)
2. For each expired account: verify sentinel + TargetOU boundary, disable if still enabled, delete AD object
3. Audit each deletion with the original creation metadata if available

Cleanup does NOT run on a background timer in the app pool. It is operator-initiated. If scheduled cleanup is needed, it should be a separate scheduled task outside the web app.

### Recovery Story

- Failed delete: account remains in AD with sentinel marking. Next cleanup attempt retries.
- Partially created account (create failed mid-way): sentinel attribute is set first so it's identifiable. List view shows it; admin can delete manually.
- Orphaned accounts (app decommissioned): queryable by sentinel attribute via standard AD tools.

### Audit

All operations audited with: actor, ticket, target DN, operation type, sentinel verification result, outcome.

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
| 1 | Licensing Updates | Low | Low | Replacing existing standalone app; proven functionality; single hard-coded attribute write |
| 2 | Emergency Disable | Medium | Medium | Security-critical; requires careful testing; elevated privilege; snapshot complexity |
| 3 | Test Account Creator | Medium | Low | Quality-of-life; no production impact; contained to test OU; needs cleanup mechanism |
| 4 | Recipient Report Enhancement | Low | Low | Incremental improvement to existing module |

---

## Credential Isolation Rules

Each module MUST use its own Delinea secret. Never share credentials across modules:

| Module | Purpose | Minimum AD Permissions |
|--------|---------|----------------------|
| ADAttributeEditor | Read/write allowlisted attrs | Write on allowlisted attributes |
| LicensingUpdates | Write extensionAttribute11 | Write on extensionAttribute11 only |
| EmergencyDisable | Disable accounts, reset passwords | Account Operators or delegated disable + password reset |
| TestAccountCreator | Create/delete in test OU | Create/delete child objects in target OU only |
| GroupManagement | Group membership changes | Write membership on target groups |
| DhcpAuthorization | DHCP server objects | Enterprise Admin (existing) |

The ProtectedPrincipalService uses a separate read-only directory credential (`Security:ProtectedPrincipalDirectoryReadSecretId`) that must never be reused for writes.

---

## Cross-Cutting Rules

These rules apply to ALL future modules:

1. **Ticket numbers are audit-only.** No ServiceNow API validation. The field is mandatory for traceability but the value is not verified against an external system.

2. **Bound-object semantics for all writes.** Any module that mutates AD must resolve to DN + ObjectGUID at lookup/preview time, then re-read by that identity immediately before write. Free-form re-resolution at write time is prohibited.

3. **Single atomic write where possible.** If a module needs to set multiple attributes, use one `Set-ADUser` call with combined parameters. If truly separate calls are required (different cmdlets), audit each step independently and report partial completion accurately.

4. **Always audit regardless of outcome.** Success, failure, and partial completion must all produce an audit record. Never return early from a write path without auditing.

5. **Protected principal checks run at write time, not just preview time.** A user can become protected between preview and save. The check at save is the authoritative one.

6. **Fail closed on resolution ambiguity.** If an identity resolves to multiple objects, or cannot be resolved when protection rules require it, block the operation.
