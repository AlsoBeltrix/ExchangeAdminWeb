# Section-Level Permissions Plan

## Goal

Assign different AD groups granular access to individual sections and operations within the application. Each section is independently gated. Migration has sub-permissions for read-only checks vs. write operations.

## Current State

- One flat `Security:AllowedGroups` array gates access to the entire application
- A single `GroupPolicy` (fallback policy) checks membership in any of those groups
- All pages use `@attribute [Authorize(Policy = "GroupPolicy")]`
- NavMenu and Home cards show all links unconditionally
- Out of Office does not enforce the same protected-user restrictions as Mailbox/Calendar Permissions

## Proposed Configuration

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
| `MigrationCheck`     | Single and bulk eligibility checks (read-only)                       |
| `MigrationCreate`    | Create new migration batches                                         |
| `MigrationManage`    | Start, stop, complete, remove, pause, resume batches; complete user, approve skipped items, clear user, clear completed |
| `DelegationReport`   | View delegation assignments for a mailbox                            |
| `MessageTrace`       | Run message trace queries                                            |
| `RecipientLookup`    | Look up mailbox details, sizes, archive status                       |
| `OutOfOffice`        | View and set out-of-office auto-replies                              |

### Migration Page Behavior

The Migration page is one Razor component with multiple tabs. Access is layered:

- `MigrationCheck` grants access to the page and the eligibility check tabs
- `MigrationCreate` additionally shows the "Create Batch" tab (requires `MigrationCheck` implicitly — you can't create a batch you can't check)
- `MigrationManage` additionally shows batch action buttons (Start, Stop, Complete, Remove, Pause, Resume, Clear Completed) and per-user actions (Complete User, Approve Skipped Items, Clear User)
- Viewing batch status (the list of current batches), user search, and migration reports come with any migration access (`MigrationCheck` is sufficient)

A user with only `MigrationCheck` sees: eligibility tabs + read-only batch status list + user search + reports (no action buttons).

### Out of Office — Protected-User Enforcement

OOF set/clear already validates protected users via `ValidateTargetMailboxAsync` in `SetOof`. OOF status reads (checking current OOF state) are allowed for protected users — this is read-only and non-destructive.

The same user-protection rules that prevent modifying the CEO's mailbox permissions also prevent changing their OOF settings. This is already implemented and tested.

## Behavior Rules

- `AllowedGroups` remains the **base gate** — user must be in at least one to reach the app at all
- `SectionAccess` controls per-section visibility and access
- **Fail-closed**: if a section key is missing or has an empty group list, **no one** gets access to that section
- Startup logs a warning for each unconfigured section: `"SectionAccess:{Section} is empty — access denied until configured"`
- Home page cards are **hidden** for sections the user cannot access
- NavMenu links are **hidden** for sections the user cannot access
- Direct URL navigation to a denied section shows a "You do not have access to this feature" page

## Implementation Approach

### 1. Configuration Model

```csharp
public class SectionAccessOptions
{
    public Dictionary<string, string[]> SectionAccess { get; set; } = new();
}
```

Bound from `Security:SectionAccess` at startup. Startup validates that all expected keys are present and logs warnings for any missing.

### 2. Authorization Policies (Base + Section composition)

Every section policy **must** include both the base `AllowedGroups` requirement AND the section group requirement. This prevents a user who is in a section group but NOT in `AllowedGroups` from bypassing the base gate:

```csharp
var sectionAccess = builder.Configuration
    .GetSection("Security:SectionAccess")
    .Get<Dictionary<string, string[]>>() ?? new();

string[] GroupsFor(string section)
{
    if (sectionAccess.TryGetValue(section, out var groups) && groups.Length > 0)
        return groups;
    Log.Warning("SectionAccess:{Section} is empty — access denied until configured", section);
    return Array.Empty<string>();
}

// Each section policy requires BOTH base app access AND section access
options.AddPolicy("MailboxPermissions", policy => policy
    .RequireAuthenticatedUser()
    .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
    .AddRequirements(new GroupAuthorizationRequirement(GroupsFor("MailboxPermissions"))));

options.AddPolicy("CalendarPermissions", policy => policy
    .RequireAuthenticatedUser()
    .AddRequirements(new GroupAuthorizationRequirement(allowedGroups))
    .AddRequirements(new GroupAuthorizationRequirement(GroupsFor("CalendarPermissions"))));

// ... same pattern for all sections
```

The fallback policy (`GroupPolicy`) remains as the base gate for the Home page and any future pages that don't have section-specific policies.

`GroupAuthorizationHandler` needs no changes — it already handles empty group lists by denying, and ASP.NET Core evaluates all requirements (both must pass).

### 3. Page-Level Authorization

Each page references its section policy:

```razor
@attribute [Authorize(Policy = "MailboxPermissions")]    @* MailboxPermissions.razor *@
@attribute [Authorize(Policy = "CalendarPermissions")]   @* CalendarPermissions.razor *@
@attribute [Authorize(Policy = "MigrationCheck")]        @* Migration.razor (base access) *@
@attribute [Authorize(Policy = "DelegationReport")]      @* DelegationReport.razor *@
@attribute [Authorize(Policy = "MessageTrace")]          @* MessageTrace.razor *@
@attribute [Authorize(Policy = "RecipientLookup")]       @* RecipientLookup.razor *@
@attribute [Authorize(Policy = "OutOfOffice")]           @* OutOfOffice.razor *@
```

### 4. Migration Page — In-Page Policy Checks

Migration.razor uses `IAuthorizationService` to check sub-permissions at render time:

```razor
@code {
    private bool canCreate;
    private bool canManage;

    protected override async Task OnInitializedAsync()
    {
        var authState = await AuthStateProvider.GetAuthenticationStateAsync();
        var user = authState.User;
        canCreate = (await AuthorizationService.AuthorizeAsync(user, "MigrationCreate")).Succeeded;
        canManage = (await AuthorizationService.AuthorizeAsync(user, "MigrationManage")).Succeeded;
    }
}
```

- Create Batch tab/button: rendered only if `canCreate`
- Batch action buttons (Start/Stop/Complete/Remove/Clear): rendered only if `canManage`
- Server-side methods also check before executing (defense in depth — don't rely solely on UI hiding)

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

### 7. Section Denied — Blazor Routing

Blazor Server uses `AuthorizeRouteView` in `Routes.razor`. When a page's `[Authorize(Policy = "...")]` fails, the existing `<NotAuthorized>` block already fires and redirects to `/access-denied`. No middleware redirect is needed — the Blazor authorization pipeline handles it.

**Change needed:** The existing `AccessDenied.razor` says "You do not have permission to access this application." For section denials (user is authenticated and in `AllowedGroups` but not in the section group), the message should distinguish between "no app access" and "no feature access." Two approaches:

**Option A (preferred):** Pass a query parameter `?reason=section` when the denial comes from a section policy vs. the base policy. `Routes.razor` already navigates to `/access-denied` — add `?reason=section` there. The AccessDenied page shows a different message based on the query param:
- No param / `reason=app`: "You do not have permission to access this application."
- `reason=section`: "You do not have access to this feature. Contact your Exchange administrator to request access."

**Option B:** A separate `SectionDenied.razor` at `/section-denied`. Each section page's authorization check would redirect to this instead. Downside: duplicated page, harder to keep in sync.

Since the `<NotAuthorized>` block in `Routes.razor` fires for ALL policy failures (base or section), and we can't easily distinguish which policy failed at that point, the simplest approach is: update the AccessDenied page message to be generic enough for both cases ("You do not have permission to access this page"). The logged-in user display and AD group note remain. No routing change needed.

### 8. Server-Side Action Checks (Defense in Depth)

UI hiding (`canCreate`/`canManage` booleans) prevents rendering buttons, but a crafted SignalR message could still invoke the method. Every mutating action must re-check the policy server-side before executing.

**Pattern:** At the top of each protected method, call `AuthorizationService.AuthorizeAsync` and return early if denied:

```csharp
private async Task CreateSingleMigrationBatch()
{
    var authState = await AuthStateProvider.GetAuthenticationStateAsync();
    if (!(await AuthorizationService.AuthorizeAsync(authState.User, "MigrationCreate")).Succeeded)
        return;
    // ... existing logic
}
```

**Methods requiring `MigrationCreate` check:**
- `CreateSingleMigrationBatch()`
- `CreateMigrationBatch()`

**Methods requiring `MigrationManage` check:**
- `ExecuteBatchAction(...)` — gates Start, Stop, Complete, Remove, Pause, Resume
- `ExecuteUserAction(...)` — gates Complete User, Approve Skipped Items, Clear User
- `ClearCompletedBatches(...)`

**Methods with no additional check needed (read-only, gated by page-level `MigrationCheck`):**
- `LoadMigrationStatus()`
- `ToggleBatchDetails(...)`
- `RefreshBatchUsers(...)`
- `SearchUser()`
- `LoadUserReport(...)`

### 9. Audit Logging

Log section denials:
```
[WRN] User ANALOG\jsmith denied access to section MigrationManage — not in groups: ANALOG\ExchangeWebAdmins
```

The existing per-action audit log continues recording what actions were performed. Section-level denials are infrastructure/security log entries, not audit CSV rows.

## Files to Modify

| File | Change |
|------|--------|
| `appsettings.json` | Add `SectionAccess` under `Security` |
| `appsettings.json.sample` | Same |
| `Program.cs` | Register per-section policies |
| `Components/Pages/Home.razor` | Wrap cards in `<AuthorizeView>` |
| `Components/Pages/MailboxPermissions.razor` | Change policy to `"MailboxPermissions"` |
| `Components/Pages/CalendarPermissions.razor` | Change policy to `"CalendarPermissions"` |
| `Components/Pages/Migration.razor` | Base policy `"MigrationCheck"`; in-page checks for Create/Manage |
| `Components/Pages/DelegationReport.razor` | Change policy to `"DelegationReport"` |
| `Components/Pages/MessageTrace.razor` | Change policy to `"MessageTrace"` |
| `Components/Pages/RecipientLookup.razor` | Change policy to `"RecipientLookup"` |
| `Components/Pages/OutOfOffice.razor` | Change policy to `"OutOfOffice"` |
| `Components/Layout/NavMenu.razor` | Wrap links in `<AuthorizeView Policy="...">` |
| `Components/Pages/SectionDenied.razor` (new) | "No access to this feature" page |
| `README.md` | Document `SectionAccess` configuration |

## Validation at Startup

On startup, log warnings for:
- Any section key missing from `SectionAccess` (lists expected keys)
- Any section key present but with empty group list
- Both cases result in that section being denied to all users

## Example Access Matrix

| Group                    | Mailbox | Calendar | MigCheck | MigCreate | MigManage | Delegation | MsgTrace | Recipient | OOF |
|--------------------------|---------|----------|----------|-----------|-----------|------------|----------|-----------|-----|
| ANALOG\ExchangeWebAdmins | X       | X        | X        | X         | X         | X          | X        | X         | X   |
| ANALOG\iam               | X       | X        | X        |           |           | X          | X        | X         | X   |
| ANALOG\MigrationTeam     |         |          | X        | X         |           |            |          |           |     |
| ANALOG\Helpdesk          |         |          |          |           |           | X          | X        | X         |     |
