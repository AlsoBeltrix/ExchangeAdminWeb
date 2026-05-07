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
| `MigrationManage`    | Start, stop, complete, remove batches; clear completed               |
| `DelegationReport`   | View delegation assignments for a mailbox                            |
| `MessageTrace`       | Run message trace queries                                            |
| `RecipientLookup`    | Look up mailbox details, sizes, archive status                       |
| `OutOfOffice`        | View and set out-of-office auto-replies                              |

### Migration Page Behavior

The Migration page is one Razor component with multiple tabs. Access is layered:

- `MigrationCheck` grants access to the page and the eligibility check tabs
- `MigrationCreate` additionally shows the "Create Batch" tab (requires `MigrationCheck` implicitly — you can't create a batch you can't check)
- `MigrationManage` additionally shows batch action buttons (Start, Stop, Complete, Remove, Clear Completed)
- Viewing batch status (the list of current batches) comes with any migration access (`MigrationCheck` is sufficient)

A user with only `MigrationCheck` sees: eligibility tabs + read-only batch status list (no action buttons).

### Out of Office — Protected-User Enforcement

Out of Office must enforce the same `ExcludedUsers` / protected-user restrictions as Mailbox and Calendar Permissions. This is already partially implemented (the `ValidateTargetMailboxAsync` call exists in `SetOof`), but the plan formalizes it as a requirement: **the same user-protection rules that prevent modifying the CEO's mailbox permissions also prevent changing their OOF settings.**

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

### 2. Authorization Policies

Register a named policy per section key in `Program.cs`:

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

options.AddPolicy("MailboxPermissions", policy => policy
    .RequireAuthenticatedUser()
    .AddRequirements(new GroupAuthorizationRequirement(GroupsFor("MailboxPermissions"))));

options.AddPolicy("CalendarPermissions", policy => ...);
options.AddPolicy("MigrationCheck", policy => ...);
options.AddPolicy("MigrationCreate", policy => ...);
options.AddPolicy("MigrationManage", policy => ...);
options.AddPolicy("DelegationReport", policy => ...);
options.AddPolicy("MessageTrace", policy => ...);
options.AddPolicy("RecipientLookup", policy => ...);
options.AddPolicy("OutOfOffice", policy => ...);
```

`GroupAuthorizationHandler` needs no changes — it already handles empty group lists by denying.

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

### 7. Access Denied Page

New `SectionDenied.razor` (or extend existing `/access-denied`):

```
You do not have access to this feature.
Contact your Exchange administrator to request access.
```

Configure the authorization middleware to redirect to this page on section policy failures (distinct from the global "not in AllowedGroups" denial).

### 8. Audit Logging

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
