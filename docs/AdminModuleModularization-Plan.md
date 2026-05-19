# Admin Module Modularization Plan

## Goal

Modularize ExchangeAdminWeb so administration sections are described by a consistent module contract, can be enabled or disabled from the admin page, and can declare their own permissions. Existing sections should be adapted to the new format, and the project should publish a spec for future administration modules.

This plan assumes a compiled-module system first. Blazor routes and components are compiled, so the practical first phase is startup discovery of modules that ship with the app, plus runtime enablement, visibility, and authorization controls. True external assembly loading can be evaluated later if there is a concrete operational need.

## Current State

The app is currently hard-wired around:

- Static page components under `Components/Pages`.
- Static authorization policy names in `Program.cs`.
- Static nav links in `Components/Layout/NavMenu.razor`.
- Static home cards in `Components/Pages/Home.razor`.
- A fixed `sections` array in `Components/Pages/AdminSettings.razor`.
- A single section permission store at `config/sectionaccess.json`.
- A large shared `ExchangeService` interface and implementation that serves all sections.

The modular design should replace these fixed lists with a module catalog and module-aware configuration while preserving current fail-closed authorization behavior.

## Module Contract

Create a module descriptor contract similar to:

```csharp
public sealed record AdminModuleDescriptor(
    string Id,
    string DisplayName,
    string Description,
    string Route,
    string IconCss,
    int SortOrder,
    bool EnabledByDefault,
    bool IsSystemModule,
    ModulePermission MainPermission,
    IReadOnlyList<ModulePermission> GranularPermissions);
```

Each module must define:

- Stable `Id`, for example `MailboxPermissions`, `Migration`, or `MessageTrace`.
- Human-readable display name and description.
- Primary route.
- Nav/home icon metadata.
- Sort order.
- Whether the module is enabled by default.
- Whether the module is a system module that cannot be disabled.
- One main permission.
- Optional granular permissions.
- Optional module-specific services.
- Optional module-specific config namespace under `Modules:<ModuleId>`.

Example permissions:

- `Migration.Access`
- `Migration.Create`
- `Migration.Manage`
- `MailboxPermissions.Access`

## Configuration Shape

Replace the current section-only fragment with a module-aware fragment at `config/modules.json`:

```json
{
  "Modules": {
    "MailboxPermissions": {
      "Enabled": true,
      "Permissions": {
        "Access": ["DOMAIN\\Exchange-Admins"]
      }
    },
    "Migration": {
      "Enabled": true,
      "Permissions": {
        "Access": ["DOMAIN\\Migration-Team"],
        "Create": ["DOMAIN\\Migration-Team"],
        "Manage": ["DOMAIN\\Exchange-Admins"]
      }
    }
  }
}
```

Behavior:

- If `config/modules.json` exists, it is authoritative for module enablement and module permissions.
- If it is corrupt, module authorization fails closed.
- If a module is missing from the fragment, use the module descriptor's `EnabledByDefault` for enablement and the base `Security:AllowedGroups` fallback for permissions during migration only.
- Once the module config is saved from the admin UI, write all known modules and permissions explicitly.
- Preserve a one-time compatibility path from `Security:SectionAccess` and `config/sectionaccess.json`.

`Security:AllowedGroups` remains the base application gate.

`Security:AdminGroups` remains the admin settings gate and is not editable from the UI.

## Credential And Identity Guidelines

Any module that requires privileged on-premises credentials must use Delinea Secret Server. The tool already has Delinea API access, so a prerequisite for adding that module is creating the required Secret Server entry and documenting the expected secret fields. Module code must not store privileged account passwords in `appsettings.json`, module config files, source code, deploy scripts, or Windows Credential Manager unless the credential is only the Delinea API bootstrap credential already used by the application.

For Azure or Entra-backed capabilities, each module must declare its own app configuration requirements. Prefer least-privilege, module-scoped app registrations or credentials. If an existing app registration is reused, that reuse must be explicit in the module spec and must not share an existing secret across modules. Reused app registrations need separate module-specific credentials, such as a new client secret or preferably a certificate/federated credential when supported, so access can be rotated or revoked for one module without affecting unrelated modules.

Module specs must include:

- Required Secret Server secret IDs and field names.
- Required on-prem account privileges.
- Required Entra app registration permissions.
- Required app credential type, storage location, and rotation expectation.
- Whether an app registration can be reused, and what isolation is still required.

## Authorization Design

Register module policies dynamically from the module catalog instead of hard-coding policies in `Program.cs`.

Rules:

- `GroupPolicy` remains the base app gate from `Security:AllowedGroups`.
- `AdminSettings` remains controlled by static `Security:AdminGroups`.
- Regular module access requires:
  - Authenticated user.
  - User in `Security:AllowedGroups`.
  - Module enabled.
  - User in the module's main permission groups.
- Granular module permission requires:
  - Authenticated user.
  - User in `Security:AllowedGroups`.
  - Module enabled.
  - User in the module's main permission groups.
  - User in the granular permission groups.
- Disabled module means:
  - Hidden from nav.
  - Hidden from home.
  - Direct route denied.
  - Server-side mutating operations denied.
- Empty permission group list denies all users for that permission.

Introduce:

- `ModuleAuthorizationRequirement`
- `ModuleAuthorizationHandler`
- `ModuleConfigurationService`
- `ModuleCatalog`

The current `GroupAuthorizationHandler` can either be refactored into the module handler or retained for `GroupPolicy` and `AdminSettings`.

## Admin Page Changes

Refactor `AdminSettings.razor` from a fixed section table into a module manager.

The page should show:

- Read-only base access groups from `Security:AllowedGroups`.
- Read-only admin groups from `Security:AdminGroups`.
- Module enable/disable controls.
- System modules marked read-only.
- Each module's main permission row.
- Granular permissions nested under the module's main permission.
- Warning badges for groups that are not in `Security:AllowedGroups`.
- Save-all behavior with atomic write to `config/modules.json`.
- Audit entries for module enablement changes.
- Audit entries for module permission changes.

Do not allow disabling `AdminSettings`. Treat `AdminEventLog` as either a system admin module or an admin child module controlled by `AdminSettings`, but do not make it possible to lock administrators out of recovery.

## Existing Section Mapping

Adapt existing sections as modules:

| Current Section | Module Id | Main Permission | Granular Permissions |
| --- | --- | --- | --- |
| Mailbox Permissions | `MailboxPermissions` | `Access` | None |
| Calendar Permissions | `CalendarPermissions` | `Access` | None |
| Exchange Migration | `Migration` | `Access` | `Create`, `Manage` |
| Delegation Report | `DelegationReport` | `Access` | None |
| Message Trace | `MessageTrace` | `Access` | None |
| Recipient Lookup | `RecipientLookup` | `Access` | None |
| Out of Office | `OutOfOffice` | `Access` | None |
| Admin Settings | `AdminSettings` | `Access` | System/admin only |
| Admin Event Log | `AdminEventLog` | `Access` | System/admin only |

Compatibility aliases:

- `MigrationCheck` maps to `Migration.Access`.
- `MigrationCreate` maps to `Migration.Create`.
- `MigrationManage` maps to `Migration.Manage`.

These aliases should remain during the transition so existing page attributes and tests can be migrated incrementally.

## Code Structure

Introduce a `Modules` folder:

```text
Modules/
  ModuleCatalog.cs
  AdminModuleDescriptor.cs
  ModulePermission.cs
  MailboxPermissions/
    MailboxPermissionsModule.cs
    Pages/
    Services/
  CalendarPermissions/
    CalendarPermissionsModule.cs
    Pages/
    Services/
  Migration/
    MigrationModule.cs
    Pages/
    Services/
```

Each module should own:

- Descriptor.
- Page components.
- Module-specific service interfaces.
- Module-specific service implementations when applicable.
- Tests for authorization and module behavior.

Shared infrastructure remains outside modules:

- `ExoConnectionPool`
- On-prem Exchange connection helpers.
- `PermissionValidator`
- `AuditService`
- `EmailService`
- `DelineaService`
- `ClientInfoService`

## Program Startup Changes

Change `Program.cs` to:

- Register shared infrastructure.
- Register module catalog.
- Let modules register their services.
- Generate module authorization policies from descriptors.
- Preserve `GroupPolicy` and `AdminSettings`.
- Validate duplicate module IDs, duplicate routes, and duplicate policy names at startup.
- Warn if configured module IDs are unknown.
- Fail closed if module config is corrupt.

The initial module catalog can be static compiled registration, for example:

```csharp
builder.Services.AddAdminModules(modules =>
{
    modules.Add<MailboxPermissionsModule>();
    modules.Add<CalendarPermissionsModule>();
    modules.Add<MigrationModule>();
});
```

## Navigation And Home

Refactor `NavMenu.razor` and `Home.razor` to render from the module catalog:

- Sort by descriptor `SortOrder`.
- Skip disabled modules.
- Wrap each item in an authorization check for the module main policy.
- Keep system/admin modules grouped separately.
- Avoid duplicating policy names and routes in markup.

This removes the current hard-coded list of feature links and cards.

## Page Authorization Pattern

Each module page should use a standard page-level authorization pattern:

- `@attribute [Authorize(Policy = ModulePolicies.SomePolicy)]` where possible.
- Explicit `AuthorizationService.AuthorizeAsync` in `OnInitializedAsync`, matching current defensive pattern.
- For mutating actions, re-check the required granular policy immediately before executing.

Migration should continue to gate:

- Eligibility checks on `Migration.Access`.
- Batch creation on `Migration.Create`.
- Batch management actions on `Migration.Manage`.

## Service Refactor

Do not keep growing `ExchangeService` as modules are added.

Split module services over time:

- `IMailboxPermissionService`
- `ICalendarPermissionService`
- `IMigrationService`
- `IDelegationReportService`
- `IMessageTraceService`
- `IRecipientLookupService`
- `IOutOfOfficeService`

Staging approach:

1. Keep `ExchangeService` as the backing implementation while module catalog and authorization are introduced.
2. Extract one service at a time into module-specific services.
3. Keep shared Exchange connection code in reusable infrastructure.
4. Shrink `IExchangeService` until it can be removed or reduced to shared helper behavior.

## Published Module Spec

Create `docs/AdminModuleSpec.md` after the implementation pattern is settled.

The spec should document:

- Required descriptor fields.
- Permission naming rules.
- Route naming rules.
- Config namespace rules.
- Credential and identity prerequisites.
- Dependency injection registration pattern.
- Required authorization checks.
- Required audit behavior for mutating actions.
- Required module tests.
- UI expectations for nav/home/admin settings.
- How to add granular permissions.
- How to add module-specific configuration.
- Deployment note: new compiled modules require app publish/restart; admin enablement controls runtime availability.

Include a sample module skeleton.

## Implementation Phases

1. Add module descriptors, module catalog, and module config service with no UI behavior change.
2. Generate authorization policies from descriptors while preserving current policy aliases.
3. Convert `NavMenu.razor` and `Home.razor` to catalog-driven rendering.
4. Replace `AdminSettings.razor` fixed section table with module enablement and nested permission editing.
5. Move current pages into module folders and attach descriptors.
6. Split `ExchangeService` into module-specific services.
7. Publish `docs/AdminModuleSpec.md` and a sample skeleton module.
8. Remove legacy `SectionAccess` after migration has been proven.

## Verification Plan

Add or update tests for:

- Module catalog rejects duplicate IDs.
- Module catalog rejects duplicate routes.
- Disabled module is hidden from nav and home.
- Disabled module direct route is denied.
- Enabled module with empty permission groups fails closed.
- Missing module config uses the intended migration fallback.
- Corrupt module config fails closed.
- AdminSettings cannot be disabled.
- Migration `Create` and `Manage` require both `Migration.Access` and their granular permission.
- Legacy `sectionaccess.json` migrates correctly.
- Admin page writes all modules and permissions explicitly.
- Audit logs record module enablement and permission edits.

Manual verification:

- Toggle a non-system module off and confirm nav/home/direct route behavior.
- Toggle it back on and confirm access returns for authorized users.
- Remove all groups from a module permission and confirm deny-all behavior.
- Configure a group outside `AllowedGroups` and confirm warning badge behavior.
- Confirm existing app deploy still preserves module config under `config/`.

## Non-Goals For First Phase

- Runtime loading of arbitrary external assemblies.
- Installing modules from the admin UI.
- Editing `Security:AllowedGroups` or `Security:AdminGroups` from the UI.
- Per-user permissions outside Windows group membership.
- Replacing the existing audit system.
