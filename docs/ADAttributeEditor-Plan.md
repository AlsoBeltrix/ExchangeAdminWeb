# AD Attribute Editor And Protected Principal Plan

Status: Implemented (history). Evidence: `Services/ADAttributeEditorService.cs`,
`Services/ProtectedPrincipalService.cs`, and the `ADAttributeEditor` descriptor in
`Modules/ModuleCatalog.cs` are all present. Header added retroactively 2026-06-17 from
catalog/service presence (AC15 drift sweep), not a fresh line-by-line plan-vs-code audit.

## Purpose

Add an AD Attribute Editor module, but first extract protected-principal checks into shared infrastructure that future high-impact modules can reuse. The first release should prove the low-risk pattern for identity resolution, source-of-authority detection, allowlisted edits, diff preview, fail-closed protection checks, audit, and operation tracing.

This plan intentionally does not implement Emergency Disable or Test Account Creator. Those modules should build on the protected-principal service and the edit/diff/audit pattern established here.

## Goals

- Create one shared protected-principal service used by existing permission checks and new modules.
- Add an allowlist-driven AD Attribute Editor module.
- Keep all privileged credentials module-scoped through Delinea Secret Server.
- Fail closed when protected-principal config, module config, or credentials are unavailable.
- Preserve existing protected-user behavior during migration; the transition must never weaken current exclusions.
- Audit every attempted save with before/after values and result.
- Emit sanitized operation trace steps for lookup, protection checks, validation, write, audit, and notification.

## Non-Goals

- No bulk CSV editing in the first version.
- No generic unrestricted LDAP editor.
- No Emergency Disable workflow in this phase.
- No Test Account Creator workflow or persistent database in this phase.
- No direct editing of protected users, protected groups, break-glass accounts, privileged admin accounts, or blocked OUs.

## Phase 1: Shared Protected Principal Service

### Configuration

Add a central config fragment:

`config/protected-principals.json`

Proposed shape:

```json
{
  "ProtectedPrincipals": {
    "Users": [
      "ceo@analog.com",
      "breakglass-admin@analog.com"
    ],
    "Groups": [
      "ANALOG\\Domain Admins",
      "ANALOG\\Enterprise Admins",
      "ANALOG\\Exchange Organization Management"
    ],
    "OrganizationalUnits": [
      "OU=Tier0,DC=ad,DC=analog,DC=com"
    ],
    "SamAccountNamePatterns": [
      "adm-*",
      "svc-*"
    ]
  }
}
```

The service should also preserve backward compatibility with the current `MailboxPermissions:ExcludedUsers` module config during migration. Existing exclusions should be read as protected users until operators move them to the central protected-principals config.

Migration must be protection-preserving:

- If central config is absent, use legacy mailbox exclusions.
- If central config exists and is valid, use the union of central protected principals and legacy mailbox exclusions until a deliberate migration removes the legacy dependency.
- If central config exists and is corrupt, fail closed.
- Creating a central config file must never silently drop legacy protections.

### Directory Read Credential

Protected group and OU checks require directory reads beyond the current string/object-ID checks. Do not borrow a feature module's Delinea secret.

Use a dedicated core directory-read credential:

- Config key: `Security:ProtectedPrincipalDirectoryReadSecretId`
- Secret Server template fields: `Username`, `Password`, `Domain`
- Required only when protected group or OU rules are configured.
- Least privilege: read-only directory lookup and group expansion.
- The secret must not require checkout, approval, or any other interactive Secret Server workflow.

Implementation reference:

- Follow `GroupManagementService` for local RSAT Active Directory cmdlet usage with explicit `-Credential` from Delinea.
- Do not copy ambient app-pool identity patterns for AD reads/writes.
- Do not introduce a new AD remoting model for v1 unless local RSAT AD cmdlets are unavailable.

Failure semantics:

- If protected group/OU rules exist and the directory-read credential is missing, unavailable, or invalid, `ProtectedPrincipalService` returns `CheckFailed = true`.
- Existing mailbox/calendar permission operations will block while protection checks fail. This is intentional fail-closed behavior, but the UI/error message must identify protected-principal configuration as the operator action item.
- Cache config and expensive positive group-expansion inputs conservatively.
- Do not cache "not protected" decisions across writes. A user added to a protected group must be blocked on the next save attempt.
- Do not cache failures indefinitely. A short fixed TTL for directory-read credential failures is acceptable to prevent repeated vault/API storms while still allowing operators to repair config without restart.

### Service

Add `ProtectedPrincipalService`.

Responsibilities:

- Load and cache protected principal config.
- Fail closed if the config file exists but cannot be parsed.
- Resolve target identity through existing `IIdentityResolver` / Exchange lookup / AD lookup paths where appropriate.
- Check direct user matches by UPN, primary SMTP, alias, object ID, DN, and sAMAccountName where available.
- Check transitive group membership for configured protected groups. Direct membership is not enough; nested membership must protect the target.
- Check DN ancestry against blocked OUs.
- Check configured username patterns.
- Return structured results:

```csharp
public sealed record ProtectedPrincipalResult(
    bool IsProtected,
    bool CheckFailed,
    string Reason,
    string[] MatchedRules);
```

Rules:

- `CheckFailed = true` must block the operation.
- `IsProtected = true` must block the operation.
- All callers display a generic operator-safe message and log detailed rule names only to audit/trace.
- Ambiguous identity resolution must fail closed.
- Protected checks must bind to a resolved immutable identity snapshot, not just the typed search string.
- Prefer exact `-Identity` lookups where possible.
- For any AD `-Filter` or `-LDAPFilter`, escape LDAP filter special characters (`\`, `*`, `(`, `)`, and NUL). Quote escaping alone is not sufficient.

Suggested identity snapshot:

```csharp
public sealed record ResolvedDirectoryPrincipal(
    string Source,
    string DisplayName,
    string UserPrincipalName,
    string? SamAccountName,
    string? PrimarySmtpAddress,
    string? DistinguishedName,
    string? ObjectGuid,
    string? EntraObjectId);
```

The same snapshot used for protection evaluation must be used for the write target. Do not re-resolve a free-form identity string at save time and then write to a different object.

### Admin UI

Add management for protected principals in the admin area.

Preferred first version:

- Add a new `Protected Principals` section under Admin Settings or a system module config page.
- Global admins only.
- Edit users, groups, OUs, and patterns as separate lists.
- Write atomically to `config/protected-principals.json`.
- Audit changes with diff, not full raw config blobs.

Do not bury this under one feature module. This is cross-cutting safety infrastructure.

### Migration From Current PermissionValidator

Current `PermissionValidator` reads mailbox-permission exclusions from module config. Refactor it to call `ProtectedPrincipalService` first.

Compatibility behavior:

- If central protected-principals config does not exist, keep reading current Mailbox Permissions excluded users.
- If central config exists and is valid, combine it with current Mailbox Permissions excluded users until a separate migration removes the legacy list.
- If central config exists and is corrupt, fail closed.

### Tests

Add unit tests for:

- Missing config with legacy fallback.
- Valid central config.
- Corrupt central config fails closed.
- Direct UPN/email/user matches.
- Group protected match.
- Nested/transitive group protected match.
- OU protected match.
- Pattern protected match.
- Ambiguous identity resolution fails closed.
- Directory-read credential missing while group/OU rules exist fails closed.
- Central config plus legacy exclusions uses the union, not replacement.
- No match allows operation.
- Audit diff excludes secrets and raw config blobs.

## Phase 2: AD Attribute Editor Module

### Catalog Descriptor

Add a module:

- Id: `ADAttributeEditor`
- DisplayName: `AD Attribute Editor`
- Route: `ad-attribute-editor` in the catalog descriptor; the Razor page uses `@page "/ad-attribute-editor"`.
- Category: `Directory & Groups`
- EnabledByDefault: `false`
- Main permission: `ADAttributeEditor`, fail closed
- Config fields:
  - `DelineaSecretId` required: AD credential for directory reads/writes
  - `DefaultSearchBase` optional

The module must not use Group Management, DHCP, or any other module's credentials.

### Attribute Allowlist

Use an allowlist. Do not expose arbitrary LDAP attribute editing.

Store the rich allowlist in a dedicated fragment, not as JSON stuffed into a string-valued module config field:

`config/ad-editable-attributes.json`

```json
{
  "Attributes": [
    {
      "Name": "extensionAttribute1",
      "Label": "Extension Attribute 1",
      "Type": "String",
      "Required": false,
      "AllowClear": true,
      "MaxLength": 1024
    },
    {
      "Name": "employeeType",
      "Label": "Employee Type",
      "Type": "Choice",
      "Choices": [ "Employee", "Contractor", "Test" ],
      "Required": false,
      "AllowClear": true
    }
  ]
}
```

The fragment is edited through an admin-only UI and written atomically. If the file exists but cannot be parsed, the editor fails closed and exposes no editable attributes.

Metadata:

- `Name`: LDAP attribute name.
- `Label`: display label.
- `Type`: v1 supports `String` and `Choice`.
- `Choices`: required for `Choice`.
- `Required`: reject blank values.
- `AllowClear`: permit clearing an existing value.
- `MaxLength`: server-side string length limit.
- `Pattern`: optional server-side regex validation.

Reject contradictory config at load. For example, `Required = true` with `AllowClear = true` is invalid unless the UI and server define an explicit precedence; v1 should fail closed on contradiction.

V1 explicitly supports single-value attributes only. Multi-value attributes such as `proxyAddresses` are out of scope unless a later version defines add/remove/replace semantics.

### Hard Denylist

The allowlist is operator-managed, so the code must also enforce a hard denylist that cannot be overridden by config.

Never-editable attributes include at minimum:

- `userAccountControl`
- `pwdLastSet`
- `unicodePwd`
- `userPassword`
- `lockoutTime`
- `accountExpires`
- `memberOf`
- `primaryGroupID`
- `adminCount`
- `lastLogon*`
- `badPwdCount`
- `objectSid`
- `objectGUID`
- `distinguishedName`
- `msDS-*`
- `servicePrincipalName`
- `altSecurityIdentities`
- `nTSecurityDescriptor`

The service must reject these attributes both when loading the allowlist and again in `SaveAsync`. This prevents the editor from becoming an account-disable, password, SPN, or group-membership tool that bypasses Emergency Disable guardrails.

Denylist matching rules:

- Case-insensitive.
- Prefix/wildcard-aware.
- Applied to the canonical LDAP attribute name after normalization.
- Enforced both when loading `config/ad-editable-attributes.json` and immediately before mutation in `SaveAsync`.

Examples:

- `UserAccountControl` matches `userAccountControl`.
- `lastLogonTimestamp` matches `lastLogon*`.
- `msDS-KeyCredentialLink` matches `msDS-*`.

### Read Flow

The page should:

1. Accept a user identifier: UPN, email, sAMAccountName, employee ID, or alias.
2. Resolve identity.
3. Determine source of authority from directory object presence, not mailbox location:
   - On-prem AD user exists: edit on-prem AD attributes.
   - Cloud-only Entra user exists: display as unsupported/read-only in v1 unless cloud editing is explicitly added.
   - Synced cloud user: edit on-prem AD attributes; do not edit read-only Graph-sourced on-prem attributes in Graph.
4. Load allowed attributes.
5. Run protected-principal check.
6. Display current values, source, and protection status.

For hybrid/synced users, extension attributes should be edited on-prem because Graph exposes on-premises extension attributes as read-only for synced users.

Do not use `GetMailboxLocationAsync` as the routing decision for this module. It is mailbox-centric and can return `Unknown` for valid non-mailbox AD objects. Reuse identity-resolution infrastructure where helpful, but route based on directory object discovery (`Get-ADUser`/Graph user), not mailbox presence.

### Write Flow

The page should:

1. Require successful lookup first.
2. Re-check `ADAttributeEditor` authorization immediately before write.
3. Re-read the target object by bound object GUID/DN and fail closed if it no longer matches the lookup snapshot.
4. Re-run protected-principal check immediately before write against the bound object snapshot.
5. Validate all proposed values server-side against allowlist metadata and hard denylist.
6. Re-read current attribute values immediately before write and compute the real pre-image.
7. Show a diff preview before save.
8. Write only changed attributes.
9. Audit before/after values for changed attributes using the actual pre-write values from step 6, not stale preview values.
10. Emit operation trace steps.
11. Send admin notification for failures and protected-target block attempts. Routine successful edits should be audit-only unless a later product decision enables success notifications.

### UI

Layout:

- Search panel at top.
- Identity summary below search.
- Attribute editor grid below summary.
- Diff preview side panel or modal before save.

Editor grid:

- Attribute label.
- LDAP attribute name.
- Current value.
- New value.
- Validation status.
- Reset/revert control per changed row.

States:

- Not searched.
- Loading.
- Not found.
- Protected target blocked.
- Source unsupported.
- Editable.
- Save pending confirmation.
- Save result.

### Backend Service

Add `ADAttributeEditorService`.

Dependencies:

- `ModuleCredentialService`
- `DelineaService` only through module credential path where possible
- `ProtectedPrincipalService`
- `OperationTraceService`
- `AuditService`
- `EmailService`
- existing identity resolver services

Methods:

```csharp
Task<AttributeLookupResult> LookupAsync(string identity);
Task<AttributeSavePreview> PreviewAsync(ResolvedDirectoryPrincipal target, Dictionary<string, string?> proposedValues);
Task<AttributeSaveResult> SaveAsync(ResolvedDirectoryPrincipal target, Dictionary<string, string?> proposedValues, string performedBy, string ip);
```

Writes:

- On-prem AD: use Delinea-backed `PSCredential` and AD cmdlets.
- Graph/cloud-only: out of scope for v1. Display cloud-only users as read-only with an explanation.

Recommended v1 scope: on-prem AD attributes only. Add cloud-only Graph editing later after the on-prem workflow is stable.

`DefaultSearchBase` is an enforced editable-population boundary for v1, not just a search hint. If configured, lookup and write re-read must ensure the target DN is under that search base before attributes are displayed or mutated.

### Cache Invalidation

Both new config fragments require immediate cache invalidation:

- `config/protected-principals.json`
- `config/ad-editable-attributes.json`

When the admin UI saves either file, the corresponding service cache must be cleared in-process. Do not require app-pool restart for newly protected principals or attribute allowlist changes to take effect.

### Phase 2 Tests

Add tests for:

- Hard denylist exact, case-insensitive, and wildcard/prefix matches.
- Denylist enforced at allowlist-load and save-time.
- LDAP filter escaping for `\`, `*`, `(`, `)`, and NUL.
- Attribute not in allowlist rejected server-side.
- Contradictory allowlist config rejected fail-closed.
- Bound-object re-read mismatch fails closed.
- Actual pre-image audit uses save-time value, not stale preview value.
- Mailbox-less on-prem AD user routes as editable on-prem.
- Cloud-only user is read-only/unsupported in v1.
- `DefaultSearchBase` blocks objects outside the configured boundary.
- Config save invalidates protected-principal and attribute-allowlist caches immediately.

### Audit And Trace

Audit event:

- Action: `ADAttributeEditor_Update`
- Target: resolved identity
- Result
- Changed attribute names
- Old/new values for changed non-secret attributes
- Ticket: optional unless product policy changes

Trace steps:

- `LookupStarted`
- `IdentityResolved`
- `ProtectedPrincipalChecked`
- `AttributesLoaded`
- `ValidationCompleted`
- `WriteStarted`
- `AttributeWriteCompleted`
- `AuditWritten`
- `NotificationSent`
- `OperationCompleted`

No raw PowerShell output, raw exception messages, secrets, access tokens, or full object dumps in trace details.

## Deploy And Config Considerations

- Ensure `config/protected-principals.json` is preserved by `deploy.ps1`, `publish_to_prod`, and dev-to-prod promotion scripts.
- Ensure `config/ad-editable-attributes.json` is preserved by `deploy.ps1`, `publish_to_prod`, and dev-to-prod promotion scripts.
- Grant app pool Modify ACL to the config directory, not broad write access to the publish root.
- New module defaults disabled and fail-closed.
- New module requires explicit section access groups and module-specific Delinea secret before use.

## Review Checklist

- Does the protected-principal service fail closed in every corrupt/unavailable path?
- Are legacy mailbox exclusions unioned or migrated safely without weakening protection?
- Is transitive group membership implemented for protected groups?
- Is the directory-read credential decision explicit and least-privileged?
- Does the implementation follow the explicit-credential `GroupManagementService` AD cmdlet pattern rather than ambient app-pool AD access?
- Are protected users/groups/OUs centralized rather than duplicated?
- Does the Attribute Editor only expose allowlisted attributes?
- Does the code-level hard denylist block security-sensitive attributes regardless of allowlist config?
- Is hard-denylist matching case-insensitive and wildcard/prefix-aware?
- Are AD filters safely escaped beyond quote escaping?
- Does routing use directory object presence rather than mailbox location?
- Are writes bound to the resolved object GUID/DN rather than a re-resolved string?
- Does save re-read the real pre-image immediately before writing?
- Are multi-value attributes explicitly excluded in v1?
- Are Boolean/Date LDAP mappings deferred rather than guessed?
- Does `DefaultSearchBase` enforce an editable boundary?
- Do protected-principal and attribute-allowlist saves invalidate runtime caches immediately?
- Are all writes module-credential scoped?
- Does every write re-check authorization immediately before mutation?
- Does every write re-check protected-principal status immediately before mutation?
- Are audit records useful without leaking secrets?
- Are operation traces useful without raw exception/API/PowerShell payloads?
- Are deploy/promotion scripts preserving the new protected-principals config?
