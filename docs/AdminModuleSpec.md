# Admin Module Specification

Version: 1.1 (based on ExchangeAdminWeb v1.5.4 implementation)

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
2. Module must be enabled
3. User must be in the dynamic section groups for `PolicyAlias`

**Granular permission policy** (generated automatically):
1. RequireAuthenticatedUser
2. Module must be enabled
3. User must be in the parent module's main permission groups
4. User must be in the dynamic section groups for the granular `PolicyAlias`

`Security:AllowedGroups` is only a backward-compatibility fallback when no section access configuration exists and the permission is not marked `FailClosed`. Once section access is configured, the section's groups are authoritative; there is no separate base gate.

**System module policy** (AdminSettings):
1. RequireAuthenticatedUser
2. User must be in `Security:AdminGroups`
3. Always enabled (cannot be disabled)

Admin Event Log is not an AdminGroups system module. It uses the `EventLog` section policy and is fail-closed until explicitly granted.

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

Managed via per-module config pages (`/module-config/{ModuleId}`). Each PolicyAlias appears under Section Access.

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
- Managed via Admin Settings toggle switches or the module's own config page

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

## Credential Isolation

**Each module that requires privileged credentials MUST have its own Delinea Secret Server secret.** Modules must never share credentials with other modules or with the application core.

### Rules

1. Declare a `DelineaSecretId` config field in the module descriptor. The operator sets the secret ID via the module's config page.
2. Retrieve credentials through `ModuleCredentialService.GetCredentialsAsync(moduleId, purpose)` or call `DelineaService.GetCredentialsBySecretIdAsync(secretId)` only after reading the secret ID from the same module's config. Never use a global or another module's credential.
3. If the secret ID is not configured or credentials are unavailable, **fail closed** — return an error, do not fall back to the app pool identity or another module's secret.
4. The Delinea API bootstrap credential (PasswordVault target `Delinea_Client`) is shared infrastructure. Individual module secrets retrieved through it are isolated.
5. Module secrets must be directly readable by the Delinea API bootstrap credential and must not require checkout, approval, or other interactive workflows. Noninteractive module calls cannot complete a Secret Server checkout flow.

### For Graph API modules

1. Declare a module-specific `DelineaSecretId` config field that points to a Secret Server record containing the Graph app fields.
2. The Graph app secret must contain `Tenant ID`, `Application ID`, and `Client Secret` fields.
3. Each module uses its own Entra app registration with minimal permissions.
4. Create a `GraphTokenClient` from the module's own Delinea secret on each operation. Do not fall back to another module's config or credential.
5. Reusing an app registration across modules is allowed ONLY if the operator explicitly enters the same Secret ID in each module's config.

### Example (DHCP Authorization)

```csharp
ConfigFields = [
    new("DelineaSecretId", "Delinea Secret ID", "Secret Server ID for Enterprise Admin credential")
]
```

```csharp
var creds = await _moduleCredentials.GetCredentialsAsync("DhcpAuthorization", "DHCP authorization");
if (creds is null) return Fail("Credentials unavailable.");
```

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

## Audit And Operation Trace Requirements

All mutating actions must call `AuditService` with:
- User identity
- Client IP address
- Action name (verb + target)
- Category (module name)
- Result (Success/Failed)
- Relevant context (target, error detail)

`AuditService` writes the business audit record to the audit JSONL file. Correlated operation trace records (`operation.start`, `operation.step`, `operation.complete`) use the same `operationId` but are written to the separate trace JSONL file. Module code that needs a multi-step transaction transcript should begin an `OperationTraceService` scope before the first backend call, then write sanitized `Step(...)` records for important milestones such as authorization checks, vault credential retrieval, Graph/Exchange/AD writes, notifications, or cleanup. Shared backend services emit standalone trace records when no operation scope is active. Never place secrets, tokens, raw exception messages, raw PowerShell output, or raw API payloads in trace details.

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
- **Module Config pages**: section access + config fields per module

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
   - Section access configurable on module config page
   - Audit entries created for actions
7. Deploy to prod
