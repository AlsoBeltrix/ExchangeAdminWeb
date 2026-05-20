# Admin Module Specification

Version: 1.0 (based on ExchangeAdminWeb v1.1.x implementation)

## Overview

An admin module is a self-contained feature section registered in the `ModuleCatalog`. Each module declares its metadata, permissions, and route. The catalog drives navigation, home page cards, admin settings, authorization policies, and enablement controls.

## Module Descriptor

Every module is defined by an `AdminModuleDescriptor` record:

```csharp
new AdminModuleDescriptor
{
    Id = "MyModule",                    // Stable identifier, PascalCase
    DisplayName = "My Module",          // Human-readable name for nav/home
    Description = "What this module does.", // Home page card text
    Route = "my-module",                // URL path (no leading slash)
    IconCss = "bi bi-icon-nav-menu",    // CSS class for nav icon
    SortOrder = 800,                    // Position in nav/home (lower = higher)
    EnabledByDefault = true,            // Enabled on fresh installs
    IsSystemModule = false,             // true = cannot be disabled
    MainPermission = new ModulePermission("Access", "MyModule"),
    GranularPermissions = [             // Optional sub-permissions
        new ModulePermission("Admin", "MyModuleAdmin", FailClosed: true)
    ]
}
```

## Permission Model

### ModulePermission

```csharp
public sealed record ModulePermission(string Name, string PolicyAlias, bool FailClosed = false);
```

- **Name**: Logical permission name (e.g., "Access", "OnPrem", "Create", "Manage")
- **PolicyAlias**: The string used in `[Authorize(Policy = "...")]` and `config/sectionaccess.json`
- **FailClosed**: If true, this permission denies all users when no section access is configured (instead of falling back to AllowedGroups)

### Authorization Layering

**Main permission policy** (generated automatically):
1. RequireAuthenticatedUser
2. User must be in `Security:AllowedGroups` (base gate)
3. Module must be enabled
4. User must be in the dynamic section groups for `PolicyAlias`

**Granular permission policy** (generated automatically):
1. RequireAuthenticatedUser
2. User must be in `Security:AllowedGroups` (base gate)
3. Module must be enabled
4. User must be in the parent module's main permission groups
5. User must be in the dynamic section groups for the granular `PolicyAlias`

**System module policy** (AdminSettings, AdminEventLog):
1. RequireAuthenticatedUser
2. User must be in `Security:AdminGroups` (no AllowedGroups base gate)
3. Always enabled (cannot be disabled)

## Naming Conventions

| Element | Convention | Example |
|---------|-----------|---------|
| Module Id | PascalCase, no spaces | `MailboxPermissions` |
| Route | kebab-case | `mailbox-permissions` |
| Main PolicyAlias | Same as Id or legacy name | `MailboxPermissions`, `MigrationCheck` |
| Granular PolicyAlias | ParentId + PermissionName | `MailboxPermissionsOnPrem`, `MigrationCreate` |
| Icon CSS | `bi bi-{name}-nav-menu` | `bi bi-person-fill-nav-menu` |

## Configuration

### Section Access (permissions)

File: `config/sectionaccess.json`

```json
{
  "Security": {
    "SectionAccess": {
      "MyModule": ["DOMAIN\\MyModuleUsers"],
      "MyModuleAdmin": ["DOMAIN\\MyModuleAdmins"]
    }
  }
}
```

Managed via the Admin Settings UI. Each PolicyAlias appears as a row.

### Module Enablement

File: `config/modules-enabled.json`

```json
{
  "MyModule": true
}
```

- Absent file: all modules use `EnabledByDefault`
- Corrupt file: all non-system modules disabled (fail-closed)
- System modules always enabled regardless of file content
- Managed via Admin Settings toggle switches

## Registration

Add the descriptor to `Modules/ModuleCatalog.cs` in `RegisterAll()`:

```csharp
new()
{
    Id = "MyModule",
    // ... all fields
}
```

The catalog validates at startup:
- No duplicate module IDs
- No duplicate routes
- No duplicate policy aliases (except system modules sharing a policy)

## Page Authorization

Each module page must:

1. Use the policy attribute: `@attribute [Authorize(Policy = "MyModule")]`
2. Re-check authorization in `OnInitializedAsync` (Blazor Server defensive pattern):

```razor
var authResult = await AuthorizationService.AuthorizeAsync(user, "MyModule");
if (!authResult.Succeeded)
{
    Navigation.NavigateTo("access-denied", forceLoad: true);
    return;
}
```

3. For mutating actions requiring granular permissions, re-check immediately before execution:

```csharp
var auth = await AuthorizationService.AuthorizeAsync(user, "MyModuleAdmin");
if (!auth.Succeeded) { /* deny */ }
```

## Audit Requirements

All mutating actions must call `AuditService` with:
- User identity
- Client IP address
- Action name (verb + target)
- Category (module name)
- Result (Success/Failed)
- Relevant context (target, error detail)

## Service Pattern

Module-specific business logic goes in a dedicated service class:

```csharp
public class MyModuleService : ExchangeServiceBase
{
    public MyModuleService(ExoConnectionPool exoPool, DelineaService delineaService, ILogger<MyModuleService> logger)
        : base(exoPool, delineaService, logger) { }

    public async Task<SomeResult> DoSomethingAsync(string target)
    {
        return await RunPooledQueryAsync(ps =>
        {
            // PowerShell commands here
        });
    }
}
```

Shared infrastructure in `ExchangeServiceBase`:
- `RunAsync` — write operations (borrow pool, execute, return)
- `RunPooledQueryAsync` — read operations
- `ThrottledAsync` — on-prem throttle (2 concurrent)
- `Invoke` / `InvokeOptional` — PowerShell invocation wrappers
- `ConnectOnPrem` — Kerberos PSSession via Delinea credentials
- `ValidateMailbox` / `ValidateRecipient` — EXO identity validation

## UI Rendering

Modules are rendered automatically by the catalog in:
- **NavMenu**: sorted by `SortOrder`, filtered by enablement + authorization
- **Home page**: cards with `DisplayName`, `Description`, link to `Route`
- **Admin Settings**: section access rows for all `PolicyAlias` values

System modules are grouped separately with warning styling.

## Deployment

New modules require an application publish and app pool restart. Module enablement (on/off) is controlled at runtime via the admin UI without restart.

The deploy script (`deploy.ps1`):
- Preserves `config/sectionaccess.json` and `config/modules-enabled.json` across upgrades
- Creates the `config/` directory with write ACL for the app pool identity
- Sets IIS auth (Windows Auth, NTLM only, kernel mode off)

## Adding a New Module — Checklist

1. Add descriptor to `ModuleCatalog.RegisterAll()`
2. Create the page `.razor` file with `[Authorize(Policy = "...")]`
3. Create the service class (if needed) inheriting `ExchangeServiceBase`
4. Add audit logging for all mutating actions
5. Test: build passes, existing tests pass
6. Deploy to dev, verify:
   - Module appears in nav/home when authorized
   - Module hidden when disabled
   - Direct URL denied when disabled
   - Section access configurable in Admin Settings
   - Audit entries created for actions
7. Deploy to prod
