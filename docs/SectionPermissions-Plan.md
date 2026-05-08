# Section-Level Permissions Plan

## Goal

Assign different AD groups granular access to individual sections and operations within the application. Each section is independently gated. Migration has sub-permissions for read-only checks vs. write operations.

## Current State

- One flat `Security:AllowedGroups` array gates access to the entire application
- A single `GroupPolicy` (fallback policy) checks membership in any of those groups
- All pages use `@attribute [Authorize(Policy = "GroupPolicy")]`
- Every page also explicitly calls `AuthorizationService.AuthorizeAsync(user, "GroupPolicy")` in `OnInitializedAsync` as defense-in-depth
- NavMenu and Home cards show all links unconditionally
- Out of Office already enforces protected-user restrictions on set/clear via `ValidateTargetMailboxAsync`

## Proposed Configuration

In `appsettings.json.sample` (and production `appsettings.json` at deploy time):

```json
"Security": {
  "AllowedGroups": ["ANALOG\\ExchangeWebAdmins", "ANALOG\\iam"],
  "PreventSelfGrant": true,
  "ExcludedUsers": ["VRStaff@analog.com", "CLD_LIC_MS_BOD", "vincent.roche@analog.com"],
  "SectionAccess": {
    "MailboxPermissions": ["ANALOG\\ExchangeWebAdmins", "ANALOG\\iam"],
    "CalendarPermissions": ["ANALOG\\ExchangeWebAdmins", "ANALOG\\iam"],
    "MigrationCheck": ["ANALOG\\ExchangeWebAdmins", "ANALOG\\MigrationTeam", "ANALOG\\iam"],
    "MigrationCreate": ["ANALOG\\ExchangeWebAdmins", "ANALOG\\MigrationTeam"],
    "MigrationManage": ["ANALOG\\ExchangeWebAdmins"],
    "DelegationReport": ["ANALOG\\ExchangeWebAdmins", "ANALOG\\iam", "ANALOG\\Helpdesk"],
    "MessageTrace": ["ANALOG\\ExchangeWebAdmins", "ANALOG\\iam", "ANALOG\\Helpdesk"],
    "RecipientLookup": ["ANALOG\\ExchangeWebAdmins", "ANALOG\\iam", "ANALOG\\Helpdesk"],
    "OutOfOffice": ["ANALOG\\ExchangeWebAdmins", "ANALOG\\iam"]
  }
}
```

## Section and Permission Breakdown

| Section Key          | What It Gates                                                        |
|---------------------|----------------------------------------------------------------------|
| `MailboxPermissions` | Full page: grant/revoke Full Access, Send As; single + bulk CSV      |
| `CalendarPermissions`| Full page: set/remove calendar sharing; single + bulk CSV            |
| `MigrationCheck`     | Page access + single/bulk eligibility checks (read-only) + batch status view + user search + reports |
| `MigrationCreate`    | "Create Migration Batch" buttons inside eligibility results panels   |
| `MigrationManage`    | Batch actions (Start, Stop, Complete, Remove, Pause, Resume, Clear Completed) + per-user actions (Complete User, Approve Skipped Items, Clear User) |
| `DelegationReport`   | View delegation assignments for a mailbox                            |
| `MessageTrace`       | Run message trace queries                                            |
| `RecipientLookup`    | Look up mailbox details, sizes, archive status                       |
| `OutOfOffice`        | View and set out-of-office auto-replies                              |

### Migration Page Behavior

The Migration page is one Razor component. Access is layered:

- `MigrationCheck` grants access to the page, eligibility check panels, read-only batch status list, user search, and migration reports
- `MigrationCreate` additionally renders the "Create Migration Batch" buttons that appear inside the single/bulk eligibility result panels after a check passes
- `MigrationManage` additionally renders batch action buttons (Start, Stop, Complete, Remove, Pause, Resume, Clear Completed) and per-user action buttons (Complete User, Approve Skipped Items, Clear User)

A user with only `MigrationCheck` sees: eligibility panels + read-only batch status + user search + reports — no create or action buttons.

### Migration Sub-Permission Hierarchy

`MigrationCreate` and `MigrationManage` policies are composed of three AND requirements: base groups + MigrationCheck groups + operation groups. ASP.NET Core requires all requirements to succeed, so:

- `MigrationCheck`: base groups AND MigrationCheck groups
- `MigrationCreate`: base groups AND MigrationCheck groups AND MigrationCreate groups
- `MigrationManage`: base groups AND MigrationCheck groups AND MigrationManage groups

A user must be in all three group sets to pass the operation policy. This means a MigrationCheck-only user cannot pass `MigrationCreate` (they fail the MigrationCreate group requirement). The operation policy alone is sufficient for server-side enforcement — no need to check two policies at each callsite.

## Behavior Rules

- `AllowedGroups` remains the **base gate** — user must be in at least one to reach the app at all
- `SectionAccess` controls per-section visibility and access
- **Fail-closed**: if a section key is missing or has an empty group list, **no one** gets access to that section
- Startup logs a warning for each unconfigured section: `"SectionAccess:{Section} is empty — access denied until configured"`
- Home page cards are **hidden** for sections the user cannot access
- NavMenu links are **hidden** for sections the user cannot access
- Direct URL navigation to a denied section shows a "You do not have access to this feature" page (the existing `AccessDenied.razor` with updated wording)

## Implementation Approach

### 1. GroupAuthorizationRequirement — Add Section Name

Add a `SectionName` property so the handler can produce meaningful denial logs:

```csharp
public class GroupAuthorizationRequirement : IAuthorizationRequirement
{
    public string[] AllowedGroups { get; }
    public string SectionName { get; }

    public GroupAuthorizationRequirement(string[] allowedGroups, string sectionName = "Application")
    {
        AllowedGroups = allowedGroups;
        SectionName = sectionName;
    }
}
```

Update `GroupAuthorizationHandler` denial logging:

```csharp
// Empty-list case:
_logger.LogError("SectionAccess:{Section} has no groups configured — denying all access", requirement.SectionName);
context.Fail(new AuthorizationFailureReason(this, $"No groups configured for {requirement.SectionName}. Contact your administrator."));

// User not in group case:
_logger.LogWarning("User {User} denied access to {Section} — not in groups: {Groups}",
    userName, requirement.SectionName, string.Join(", ", requirement.AllowedGroups));
```

The existing base-gate requirement uses `SectionName = "Application"` (default). Section requirements use their section key.

### 2. Policy Registration (Program.cs)

```csharp
var sectionAccess = builder.Configuration
    .GetSection("Security:SectionAccess")
    .Get<Dictionary<string, string[]>>() ?? new();

var expectedSections = new[] {
    "MailboxPermissions", "CalendarPermissions", "MigrationCheck",
    "MigrationCreate", "MigrationManage", "DelegationReport",
    "MessageTrace", "RecipientLookup", "OutOfOffice"
};

string[] GroupsFor(string section)
{
    if (sectionAccess.TryGetValue(section, out var groups) && groups.Length > 0)
        return groups;
    Log.Warning("SectionAccess:{Section} is empty — access denied until configured", section);
    return Array.Empty<string>();
}

foreach (var missing in expectedSections.Where(s => !sectionAccess.ContainsKey(s)))
    Log.Warning("SectionAccess:{Section} is not configured — access denied until configured", missing);

// Base gate (unchanged — used by Home and as fallback)
var groupPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
    .Build();
options.AddPolicy("GroupPolicy", groupPolicy);
options.FallbackPolicy = groupPolicy;

// Section policies: base gate + section gate
var migrationSubPolicies = new[] { "MigrationCreate", "MigrationManage" };
foreach (var section in expectedSections.Except(migrationSubPolicies))
{
    var sectionGroups = GroupsFor(section);
    options.AddPolicy(section, policy => policy
        .RequireAuthenticatedUser()
        .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
        .AddRequirements(new GroupAuthorizationRequirement(sectionGroups, section)));
}

// Migration sub-permission hierarchy: three separate AND requirements
// User must be in base groups AND MigrationCheck groups AND operation groups
options.AddPolicy("MigrationCreate", policy => policy
    .RequireAuthenticatedUser()
    .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
    .AddRequirements(new GroupAuthorizationRequirement(GroupsFor("MigrationCheck"), "MigrationCheck"))
    .AddRequirements(new GroupAuthorizationRequirement(GroupsFor("MigrationCreate"), "MigrationCreate")));

options.AddPolicy("MigrationManage", policy => policy
    .RequireAuthenticatedUser()
    .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
    .AddRequirements(new GroupAuthorizationRequirement(GroupsFor("MigrationCheck"), "MigrationCheck"))
    .AddRequirements(new GroupAuthorizationRequirement(GroupsFor("MigrationManage"), "MigrationManage")));
```

ASP.NET Core evaluates all requirements as AND — every requirement must succeed. This means a user needs membership in base groups AND MigrationCheck groups AND the operation-specific group to pass. A MigrationCheck-only user cannot pass `MigrationCreate` because they fail the MigrationCreate requirement.

### 3. Page-Level Authorization — Both Attribute and Explicit Check

Each page changes **both** the `@attribute` and the explicit `AuthorizationService.AuthorizeAsync` check in `OnInitializedAsync`:

**Before (all pages):**
```razor
@attribute [Authorize(Policy = "GroupPolicy")]

// In OnInitializedAsync:
var authResult = await AuthorizationService.AuthorizeAsync(user, "GroupPolicy");
```

**After (example: MailboxPermissions):**
```razor
@attribute [Authorize(Policy = "MailboxPermissions")]

// In OnInitializedAsync:
var authResult = await AuthorizationService.AuthorizeAsync(user, "MailboxPermissions");
```

Full mapping:

| Page | Policy |
|------|--------|
| `Home.razor` | `"GroupPolicy"` (unchanged — base gate only) |
| `MailboxPermissions.razor` | `"MailboxPermissions"` |
| `CalendarPermissions.razor` | `"CalendarPermissions"` |
| `Migration.razor` | `"MigrationCheck"` |
| `DelegationReport.razor` | `"DelegationReport"` |
| `MessageTrace.razor` | `"MessageTrace"` |
| `RecipientLookup.razor` | `"RecipientLookup"` |
| `OutOfOffice.razor` | `"OutOfOffice"` |

### 4. Migration Page — In-Page Sub-Permission Checks

In `OnInitializedAsync`, after the base page-level check:

```csharp
canCreate = (await AuthorizationService.AuthorizeAsync(user, "MigrationCreate")).Succeeded;
canManage = (await AuthorizationService.AuthorizeAsync(user, "MigrationManage")).Succeeded;
```

**UI gating:** Hide creation and management controls:
- "Create Migration Batch" buttons (lines ~133, ~281): wrap in `@if (canCreate) { ... }`
- Batch action buttons (Start, Stop, Complete, Remove, etc.): wrap in `@if (canManage) { ... }`
- Per-user action buttons (Complete User, Approve Skipped, Clear User): wrap in `@if (canManage) { ... }`

**Server-side enforcement (defense in depth):** Every mutating method re-checks the policy before executing:

```csharp
private async Task CreateSingleMigrationBatch()
{
    var authState = await AuthStateProvider.GetAuthenticationStateAsync();
    if (!(await AuthorizationService.AuthorizeAsync(authState.User, "MigrationCreate")).Succeeded)
        return;
    // ... existing logic
}
```

Methods requiring `MigrationCreate`:
- `CreateSingleMigrationBatch()`
- `CreateMigrationBatch()`

Methods requiring `MigrationManage`:
- `ExecuteBatchAction(...)` — gates Start, Stop, Complete, Remove, Pause, Resume
- `ExecuteUserAction(...)` — gates Complete User, Approve Skipped Items, Clear User
- `ClearCompletedBatches(...)`

Methods with no additional check (read-only, gated by page-level `MigrationCheck`):
- `LoadMigrationStatus()`
- `ToggleBatchDetails(...)`
- `RefreshBatchUsers(...)`
- `SearchUser()`
- `LoadUserReport(...)`

### 5. NavMenu Visibility

```razor
<AuthorizeView Policy="MailboxPermissions">
    <Authorized>
        <div class="nav-item px-3">
            <NavLink class="nav-link" href="mailbox-permissions">Mailbox Permissions</NavLink>
        </div>
    </Authorized>
</AuthorizeView>

<AuthorizeView Policy="CalendarPermissions">
    <Authorized>
        ...
    </Authorized>
</AuthorizeView>

<AuthorizeView Policy="MigrationCheck">
    <Authorized>
        <div class="nav-item px-3">
            <NavLink class="nav-link" href="migration">Migration</NavLink>
        </div>
    </Authorized>
</AuthorizeView>

@* etc. for each section *@
```

### 6. Home Page Cards

Same pattern — each card wrapped in `<AuthorizeView Policy="...">`.

### 7. Access Denied Page

Use the existing `AccessDenied.razor` — no new `SectionDenied.razor`. Update the message to be generic enough for both cases:

> "You do not have permission to access this page. If you believe you should have access, contact your Exchange administrator."

Navigation to access-denied uses relative path (`"access-denied"`, not `"/access-denied"`) to preserve `/ExchangeAdminWeb` path-base hosting.

### 8. Audit/Logging

Section denials are logged by `GroupAuthorizationHandler` using the `SectionName` property:

```
[WRN] User ANALOG\jsmith denied access to MigrationManage — not in groups: ANALOG\ExchangeWebAdmins
```

These are Serilog structured log entries, not audit CSV rows. The audit CSV continues recording executed actions only.

## Files to Modify

| File | Change |
|------|--------|
| `appsettings.json.sample` | Add `SectionAccess` under `Security` |
| `Authorization/GroupAuthorizationHandler.cs` | Add `SectionName` to requirement; update handler log messages |
| `Program.cs` | Register per-section policies with startup validation |
| `Components/Layout/NavMenu.razor` | Wrap links in `<AuthorizeView Policy="...">` |
| `Components/Pages/Home.razor` | Wrap cards in `<AuthorizeView Policy="...">`; keep explicit check as `"GroupPolicy"` |
| `Components/Pages/MailboxPermissions.razor` | Change attribute + explicit check to `"MailboxPermissions"` |
| `Components/Pages/CalendarPermissions.razor` | Change attribute + explicit check to `"CalendarPermissions"` |
| `Components/Pages/Migration.razor` | Base policy `"MigrationCheck"`; add `canCreate`/`canManage`; gate UI + server-side |
| `Components/Pages/DelegationReport.razor` | Change attribute + explicit check to `"DelegationReport"` |
| `Components/Pages/MessageTrace.razor` | Change attribute + explicit check to `"MessageTrace"` |
| `Components/Pages/RecipientLookup.razor` | Change attribute + explicit check to `"RecipientLookup"` |
| `Components/Pages/OutOfOffice.razor` | Change attribute + explicit check to `"OutOfOffice"` |
| `Components/Pages/AccessDenied.razor` | Update message wording |
| `README.md` | Document `SectionAccess` configuration |

**Deployment note:** Production `appsettings.json` at `D:\inetpub\ExchangeAdminWeb` must receive the new `Security:SectionAccess` block before deploy. The sample file is the tracked template; the production file is not in source control.

## Validation at Startup

On startup, log warnings for:
- Any expected section key missing from `SectionAccess`
- Any section key present but with empty group list
- Both cases result in that section being denied to all users (fail-closed)

## Example Access Matrix

| Group                    | Mailbox | Calendar | MigCheck | MigCreate | MigManage | Delegation | MsgTrace | Recipient | OOF |
|--------------------------|---------|----------|----------|-----------|-----------|------------|----------|-----------|-----|
| ANALOG\ExchangeWebAdmins | X       | X        | X        | X         | X         | X          | X        | X         | X   |
| ANALOG\iam               | X       | X        | X        |           |           | X          | X        | X         | X   |
| ANALOG\MigrationTeam     |         |          | X        | X         |           |            |          |           |     |
| ANALOG\Helpdesk          |         |          |          |           |           | X          | X        | X         |     |

## Implementation Order

1. `GroupAuthorizationHandler.cs` — add `SectionName`, update logging
2. `Program.cs` — register section policies with hierarchy and startup validation
3. `appsettings.json.sample` — add `SectionAccess` block
4. Each page component — change attribute + explicit check
5. `Migration.razor` — add `canCreate`/`canManage` + gate UI + server-side checks
6. `NavMenu.razor` — wrap links
7. `Home.razor` — wrap cards
8. `AccessDenied.razor` — update message
9. `README.md` — document configuration
10. Build + test + manual verify nav/home hiding + verify denial redirect
