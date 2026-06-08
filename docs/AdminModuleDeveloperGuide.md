# ExchangeAdminWeb Module Developer Guide

Version: 1.3
Host baseline: ExchangeAdminWeb 2.3.0
Last verified against code: commit 6e2fbb6 (2026-06-05)

## Purpose

This guide explains how to design, build, review, and integrate a new
administration module for ExchangeAdminWeb.

It is written for a developer who is new to this codebase but is expected to
produce production-quality code that fits the existing security, configuration,
authorization, auditing, and deployment model.

ExchangeAdminWeb is currently a **compiled modular application**. Modules are
not loaded dynamically from uploaded ZIP files or dropped assemblies. A new
module is added by contributing source files, a module descriptor, service
registration, tests, and documentation to the main application. Runtime
enablement, permissions, and module configuration are then managed through the
admin UI.

## Core Concepts

An ExchangeAdminWeb module is a self-contained administration feature with:

- A stable module ID.
- A route and Razor page.
- A module descriptor in the catalog.
- One main access policy and optional granular policies.
- Optional module-specific configuration fields.
- Optional module-specific privileged credentials from Delinea Secret Server.
- A dedicated service class for business logic.
- Business audit records for user-visible actions.
- Operation trace records for multi-step backend diagnostics.
- Tests covering catalog integration, security behavior, parsing, and failure
  modes.

The module catalog is the source of truth for module metadata. It drives:

- Authorization policy registration.
- Sidebar navigation.
- Home page cards.
- Module enable/disable controls.
- Per-module section access UI.
- Per-module configuration UI.

The catalog does not replace the need for a Razor page, a service registration,
and tests. Those are still source-level integration points.

## Host API Boundary

Host services are dependencies. A module consumes them through constructor or
Razor injection; it does not reimplement them.

If a service signature is not documented here, inspect the host API before
coding inside the main repository. For isolated authoring without repository
access, stop and report the missing API contract instead of inventing a
production stub.

Production module code must use the host types exactly as provided. Do not
change constructor shapes, add methods to host types, or compile against local
stand-ins. In particular:

- `GraphTokenClient` is constructed from tenant ID, client ID, client secret,
  and an `HttpClient`.
- `OperationTraceService.Step(...)` is called on the service, not on the
  `OperationScope`.
- `ClientInfoService` is the source for the UI/audit IP address.
- Authorization policies are generated from the real `ModuleCatalog`; do not
  add always-allow policies for a module.

## Required Files

A typical module adds or changes these files:

```text
Modules/ModuleCatalog.cs
Components/Pages/<ModuleName>.razor
Services/<ModuleName>Service.cs
Models/<ModuleName>/*.cs               optional
Program.cs                             DI registration
ExchangeAdminWeb.Tests/*               unit/catalog tests
docs/*                                 module notes when needed
tools/deploy-pipeline.ps1              only if new preserved config files exist
tools/promote-dev-to-prod.ps1          only if new preserved config files exist
```

For small modules, models may live in the service file if they are private to
that implementation. For larger modules, create a dedicated model folder.

## Deliverable Boundary

This guide describes a source contribution to the existing ExchangeAdminWeb
host, not a standalone application.

Do not create, copy, or submit:

- A new `ExchangeAdminWeb.csproj`.
- A new production `Program.cs`.
- A replacement `Modules/ModuleCatalog.cs`.
- `bin/`, `obj/`, or other build output.
- A fake host application.
- Mock authentication or always-allow authorization policies.
- Production implementations of host services such as `GraphTokenClient`,
  `AuditService`, `OperationTraceService`, `ModuleConfigService`,
  `ModuleCredentialService`, `ProtectedPrincipalService`, or
  `ClientInfoService`.
- A `HostStubs.cs` or equivalent file under production source folders.

When building outside the main repository, produce integration-ready source
files and explicit patch instructions. If test doubles are needed for isolated
unit tests, keep them under the test project namespace/folder and name them as
test fakes. Test fakes must never be referenced by production module code.

The correct output shape is:

```text
src/
  Components/Pages/<ModuleName>.razor
  Services/<ModuleName>Service.cs
  Models/<ModuleName>/*.cs
integration/
  ModuleCatalog.snippet.cs
  Program.snippet.cs
tests/
  <ModuleName>Tests.cs
docs/
  <ModuleName>.md
```

For work directly inside ExchangeAdminWeb, apply those snippets to the real
files instead of creating a parallel host.

## Package Validator

Isolated module authors must run the package validator before handing the
module back for integration review:

```powershell
.\tools\validate-module-package.ps1 `
    -PackagePath D:\source\isolated_module_test\MyModule `
    -HostPath D:\source\ExchangeAdminWeb
```

The validator is read-only. It does not install the module and does not modify
the host repository. It checks the package shape and the most common host
compatibility failures:

- Missing `src/`, `integration/`, `tests/`, or `docs/`.
- Missing `integration/ModuleCatalog.snippet.cs` or
  `integration/Program.snippet.cs`.
- Standalone host files such as root `Program.cs`, `*.csproj`, `bin/`, or
  `obj/`.
- Production host stubs or fake host services.
- Dependencies on fake-only APIs that do not exist in the real host.
- Always-allow authorization policies.
- Hardcoded audit IP addresses.
- Mismatched descriptor route and Razor `@page`.
- Mismatched descriptor main policy and Razor `[Authorize]`.
- Missing `@rendermode`, auth, or `ClientInfoService` page wiring.
- Graph endpoints that are literal strings and do not start with `/`.
- Sidebar icon classes that do not exist in the host CSS.
- Raw stack traces or empty catch blocks in production source.

Warnings are advisory by default. To make warnings fail the validation:

```powershell
.\tools\validate-module-package.ps1 `
    -PackagePath D:\source\isolated_module_test\MyModule `
    -HostPath D:\source\ExchangeAdminWeb `
    -TreatWarningsAsErrors
```

Passing the validator does not prove business correctness. It only proves that
the package is shaped like an ExchangeAdminWeb module and avoids known
integration traps. A module still needs human code review and, after the
snippets are applied to a branch, a real host build/test run.

## Naming Standards

| Item | Rule | Example |
| --- | --- | --- |
| Module ID | PascalCase, stable, no spaces | `NamedLocations` |
| Route | kebab-case, no leading slash | `named-locations` |
| Page file | PascalCase module name | `NamedLocations.razor` |
| Service class | `<ModuleId>Service` | `NamedLocationsService` |
| Main policy alias | Usually same as module ID | `NamedLocations` |
| Granular policy alias | Module ID plus capability | `ADAttributeEditorLevel2` |
| Audit category | Module ID | `NamedLocations` |
| Config namespace | Module ID | `NamedLocations:DelineaSecretId` |

Do not rename an existing module ID or policy alias without a migration plan.
These values are persisted in config files and operator procedures.

## Module Descriptor

Every module is registered in `Modules/ModuleCatalog.cs` by adding an
`AdminModuleDescriptor` to `RegisterAll()`.

Current descriptor shape:

```csharp
public sealed record AdminModuleDescriptor
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }
    public required string Description { get; init; }
    public required string Route { get; init; }
    public required string IconCss { get; init; }
    public string Category { get; init; } = "Other";
    public required int SortOrder { get; init; }
    public required bool EnabledByDefault { get; init; }
    public required bool IsSystemModule { get; init; }
    public string Version { get; init; } = "1.0.0";
    public required ModulePermission MainPermission { get; init; }
    public string? DependsOn { get; init; }
    public bool IsConfigOnly { get; init; }
    public IReadOnlyList<ModulePermission> GranularPermissions { get; init; } = [];
    public IReadOnlyList<ModuleConfigField> ConfigFields { get; init; } = [];
}
```

Current permission shape:

```csharp
public sealed record ModulePermission(
    string Name,
    string PolicyAlias,
    bool FailClosed = false);
```

Current config field shape:

```csharp
public sealed record ModuleConfigField(
    string Key,
    string Label,
    string Description,
    bool Required = true,
    bool IsSecret = false,
    string DefaultValue = "");
```

Example descriptor:

```csharp
new()
{
    Id = "NamedLocations",
    DisplayName = "Named Locations",
    Description = "Manage Entra ID Conditional Access named locations.",
    Route = "named-locations",
    IconCss = "bi bi-geo-alt-fill-nav-menu",
    Category = "Identity & Access",
    SortOrder = 790,
    EnabledByDefault = false,
    IsSystemModule = false,
    Version = "1.0.0",
    MainPermission = new("Access", "NamedLocations", FailClosed: true),
    ConfigFields = [
        new(
            "DelineaSecretId",
            "Graph App Delinea Secret ID",
            "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields")
    ]
}
```

### Descriptor Rules

- New optional modules should normally use `EnabledByDefault = false`.
- Modules that mutate data or use privileged backends must use
  `FailClosed: true` on the main permission.
- Granular permissions should also be fail-closed when they grant privileged
  sub-capabilities.
- `IsSystemModule = true` is reserved for core host functions.
- `Version` is the module's version, not the application version. Increment it
  whenever the module behavior or config contract changes.
- `ConfigFields` are string-valued. Do not stuff complex structured config into
  a single field unless the module explicitly parses and validates it
  fail-closed. Prefer a dedicated config fragment for complex data.
- `DependsOn` optionally names another module ID this module requires. The
  catalog rejects self-dependencies, unknown IDs, and dependency cycles at
  startup. Enablement cascades at runtime:
  `ModuleEnablementService.IsModuleEnabled` reports a module disabled when any
  module in its `DependsOn` chain is disabled, and Admin Settings shows such
  modules as parent-disabled. Exchange-backed modules declare
  `DependsOn = "ExchangeOnline"`.
- `IsConfigOnly` marks a module that exists to hold shared configuration
  consumed by dependent modules (current example: `ExchangeOnline`).
  Config-only modules are excluded from home page cards, the operational
  sidebar, the enablement toggle list, and configurable policy aliases. Their
  page is reached through config navigation, and its Razor `[Authorize]`
  policy is `AdminSettings`, not the module's main permission alias.

## Categories And Navigation

Use one of the established categories:

- `Exchange`
- `Directory & Groups`
- `Identity & Access`
- `Infrastructure`
- `Administration`
- `Other`

The sidebar and home page are catalog-driven. A module appears when:

1. The module is enabled.
2. The user is authorized for the module's main policy.
3. The route points to a real Razor page.

If the module needs a new icon, add the CSS class and visual asset following the
existing sidebar icon pattern. Reuse an existing icon class when possible.

## Authorization Model

The catalog generates policies at startup.

Main module policy:

1. User must be authenticated.
2. Module must be enabled.
3. User must belong to the configured section access groups for the main policy
   alias.

Granular policy:

1. User must be authenticated.
2. Module must be enabled.
3. User must satisfy the module's main policy.
4. User must belong to the configured section access groups for the granular
   policy alias.

`Security:AllowedGroups` is only a backward-compatibility fallback when no
section access configuration exists and the permission is not fail-closed. Once
section access exists, the section's groups are authoritative. There is no
separate base gate.

### Page-Level Authorization

Every module page must include:

```razor
@page "/my-module"
@attribute [Authorize(Policy = "MyModule")]
@rendermode InteractiveServer

@inject AuthenticationStateProvider AuthStateProvider
@inject IAuthorizationService AuthorizationService
@inject ClientInfoService ClientInfo
@inject NavigationManager Navigation
```

The page must also explicitly check authorization during initialization:

```csharp
var authState = await AuthStateProvider.GetAuthenticationStateAsync();
var user = authState.User;
currentUser = user.Identity?.Name ?? "Unknown";
clientIpAddress = ClientInfo.IpAddress != "Unknown"
    ? ClientInfo.IpAddress
    : ClientInfo.GetIpForUser(currentUser);

var authResult = await AuthorizationService.AuthorizeAsync(user, "MyModule");
if (!authResult.Succeeded)
{
    Navigation.NavigateTo("access-denied", forceLoad: true);
    return;
}
```

Every mutating action must re-check authorization immediately before the write.
If the service owns the backend write, the service must perform this re-check
with the policy for that write. Page-side checks are useful for UX and early
denial, but they do not replace the service-side check.

Do not rely on:

- Route authorization alone.
- The initial `OnInitializedAsync` check.
- Hidden UI.
- Disabled buttons.

Example:

```csharp
var authState = await AuthStateProvider.GetAuthenticationStateAsync();
var auth = await AuthorizationService.AuthorizeAsync(authState.User, "MyModuleManage");
if (!auth.Succeeded)
{
    AuditDeniedAttempt(...);
    return;
}
```

Use granular policies for sub-actions when needed. Examples:

- `MigrationCreate`
- `MigrationManage`
- `MailboxPermissionsOnPrem`
- `ADAttributeEditorLevel1`
- `ADAttributeEditorLevel2`
- `ADAttributeEditorLevel3`

## Module Enablement

Module enablement is stored in:

```text
config/modules-enabled.json
```

Behavior:

- Missing file: modules use descriptor `EnabledByDefault`.
- Corrupt file: non-system modules are disabled fail-closed.
- System modules remain enabled.

Enablement is runtime-configurable from the admin UI. It does not require an app
pool restart.

## Section Access

Section access is stored in:

```text
config/sectionaccess.json
```

Shape:

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

Operators manage these groups on the module config page. Every policy alias in
the module descriptor appears in that UI.

Fail-closed permissions deny access when no explicit group is configured.

## Module Configuration

Simple module configuration is stored in per-module files:

```text
config/module-config-{ModuleId}.json
```

Each module has its own configuration file, keyed by module ID (for example,
`config/module-config-Migration.json`). Access it through `ModuleConfigService`:

```csharp
public string? GetValue(string moduleId, string key);
public Dictionary<string, string> GetModuleConfig(string moduleId);
public bool IsModuleConfigured(string moduleId);
public void SaveModuleConfig(string moduleId, Dictionary<string, string> values);
public bool HasConfigFile { get; }
public bool IsCorrupt { get; }
public event Action<string>? ConfigSaved;
```

Rules:

- Treat `IsCorrupt` as fail-closed for privileged operations. For a specific
  module's state, prefer the per-module overloads `IsModuleCorrupt(moduleId)`
  and `HasModuleConfigFile(moduleId)`; the parameterless `IsCorrupt` and
  `HasConfigFile` are aggregate views across all modules.
- Required config missing means the operation must not proceed.
- Do not store secrets in module config.
- For values that need immediate runtime effect, read from `ModuleConfigService`
  at operation time or subscribe to `ConfigSaved` and invalidate caches.
- Complex config should live in a dedicated file under `config/` with atomic
  write and parse validation.

If a module adds a dedicated config file, update deploy/promotion scripts so the
file is preserved and merged or copied intentionally.

## Credential Isolation

Every module that needs privileged credentials must use a module-specific
Delinea Secret Server record.

Use `ModuleCredentialService` for username/password/domain credentials:

```csharp
public Task<(string username, string password, string domain)?>
    GetCredentialsAsync(string moduleId, string purpose);
```

Example:

```csharp
var creds = await _moduleCredentials.GetCredentialsAsync("DhcpAuthorization", "DHCP authorize");
if (creds is null)
{
    return OperationResult.Fail("Module credentials are not configured or unavailable.");
}
```

Credential rules:

- Declare the credential config field in the module descriptor. Use
  `GraphDelineaSecretId` for modules that consume Graph API credentials and
  `DelineaSecretId` for modules that consume AD or Exchange credentials.
- Never use a global Delinea secret for module work.
- Never borrow another module's secret.
- Never fall back to app pool identity for privileged operations.
- Never store credentials in `appsettings.json`, module config, source, tests,
  logs, or deployment scripts.
- Missing credentials must fail closed.
- Secret Server records must be directly readable by the Delinea API bootstrap
  credential. They must not require checkout, approval, or user interaction.

### Graph Credentials

Graph modules must use a module-specific Delinea secret containing:

- `Tenant ID`
- `Application ID`
- `Client Secret`

Graph helper:

```csharp
public sealed class GraphTokenClient
{
    public bool IsConfigured { get; }
    public GraphTokenClient(string tenantId, string clientId, string clientSecret, HttpClient httpClient);
    public Task<JsonDocument?> GetAsync(string endpoint);
    public Task<JsonDocument?> PostAsync(string endpoint, object? body = null);
    public Task<bool> PostNoContentAsync(string endpoint, object? body = null);
    public Task<bool> PatchAsync(string endpoint, object body);
    public Task<bool> DeleteAsync(string endpoint);
}
```

Required construction pattern:

```csharp
private async Task<GraphTokenClient> GetGraphClientAsync()
{
    var secretIdValue = _moduleConfig.GetValue("MyModule", "GraphDelineaSecretId")
                     ?? _moduleConfig.GetValue("MyModule", "DelineaSecretId");
    if (!int.TryParse(secretIdValue, out var secretId) || secretId <= 0)
        throw new InvalidOperationException("MyModule is not configured. Set Graph App Delinea Secret ID in Module Config.");

    var fields = await _delineaService.GetSecretFieldsAsync(secretId);
    if (fields is null)
        throw new InvalidOperationException("Cannot retrieve MyModule Graph app secret from Secret Server.");

    var tenantId = fields.GetValueOrDefault("Tenant ID") ?? "";
    var clientId = fields.GetValueOrDefault("Application ID") ?? "";
    var clientSecret = fields.GetValueOrDefault("Client Secret") ?? "";

    if (string.IsNullOrWhiteSpace(tenantId) ||
        string.IsNullOrWhiteSpace(clientId) ||
        string.IsNullOrWhiteSpace(clientSecret))
        throw new InvalidOperationException("Graph API credentials incomplete in Secret Server.");

    return new GraphTokenClient(
        tenantId,
        clientId,
        clientSecret,
        _httpClientFactory.CreateClient("MicrosoftGraph"));
}
```

Rules:

- Each module should have its own Entra app registration with least-privilege
  permissions.
- Reusing an app registration is allowed only when the operator explicitly
  configures the same secret ID on each module.
- Do not fall back to another module's Graph config.
- Do not cache unconfigured clients.
- Do not log raw token endpoint bodies or client secrets.
- Graph endpoints passed to `GraphTokenClient` must start with `/`, for example
  `/identity/conditionalAccess/namedLocations`.
- OData filter values must be escaped and the final query string must be
  URI-encoded.
- Follow `@odata.nextLink` for list operations that can return more than one
  page.

## Protected Principals

Any module that modifies a user, mailbox, group membership, identity state,
access state, password, token/session state, or directory attribute must check
protected principals before writing.

Relevant types:

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

public sealed record ProtectedPrincipalResult(
    bool IsProtected,
    bool CheckFailed,
    string Reason,
    string[] MatchedRules);

public Task<ProtectedPrincipalResult> CheckAsync(ResolvedDirectoryPrincipal target);
```

Required pattern for identity writes:

1. Resolve the user or object.
2. Bind the operation to immutable identity data, such as DN plus ObjectGUID or
   Graph object ID.
3. Check `ProtectedPrincipalService.CheckAsync`.
4. If `CheckFailed`, stop and report a fail-closed error.
5. If `IsProtected`, stop and audit the blocked attempt.
6. Re-check authorization immediately before the write.
7. Re-read the object by the bound immutable identity.
8. Verify the immutable ID still matches.
9. Apply the write.

Ambiguous lookup results must fail closed.

## Exchange PowerShell Modules

Exchange-backed modules should use a dedicated service class. For Exchange
Online or on-prem Exchange operations, inherit from `ExchangeServiceBase` when
appropriate.

Important helpers:

```csharp
protected Task<PermissionResult> RunAsync(
    Action<PowerShell, ConnectionErrorTracker> operation,
    Func<(string message, string? detail)>? successFormatter = null);

protected Task<T> RunPooledQueryAsync<T>(
    Func<PowerShell, ConnectionErrorTracker, T> query);

protected static Collection<PSObject> Invoke(
    PowerShell ps,
    ConnectionErrorTracker tracker);

protected static Collection<PSObject> InvokeOptional(
    PowerShell ps,
    ConnectionErrorTracker tracker);
```

Rules:

- Use `RunPooledQueryAsync` for Exchange Online reads.
- Use `RunAsync` for Exchange Online writes.
- Pass the `ConnectionErrorTracker` to `Invoke`/`InvokeOptional`.
- Do not reintroduce thread-static connection-error tracking.
- For on-prem operations, use the module's Delinea credential path.
- Add timeouts/retry behavior when opening on-prem sessions.
- Audit partial success accurately when multiple backend steps are required.

## Active Directory Modules

AD modules should follow the explicit credential pattern already used by
module-scoped AD services:

- Import the `ActiveDirectory` module inside the PowerShell runspace.
- Construct a `PSCredential` from the module's Delinea secret.
- Pass `-Credential` to AD cmdlets.
- Prefer `-Identity` for bound-object operations.
- Escape LDAP filter values when a filter is required.
- Treat multiple matches as ambiguous and fail closed.

AD write rules:

- Re-read by DN or object GUID immediately before write.
- Verify ObjectGUID if available.
- Use one atomic cmdlet call where possible.
- If multiple calls are unavoidable, audit/report partial completion.
- Never write security-sensitive attributes through a generic editor unless a
  dedicated reviewed module owns that behavior.

## Ticket Handling

Mutating modules should require a ticket number unless explicitly approved as an
audit-only exception.

UI rules:

- Required ticket inputs must use `@bind:event="oninput"` if button enabled
  state depends on the value.
- Capture the ticket into a local variable before confirmation/write.
- Use the captured ticket for execution and audit.
- Do not execute against mutable form fields after a confirmation dialog.

ServiceNow validation is not part of the default module contract. Do not add it
unless the module request explicitly requires it.

UI labels should say `Ticket Number` unless ServiceNow validation is explicitly
part of the request. Do not label the field `ServiceNow` just because tickets
are collected for audit.

## Auditing

Use `AuditService` for business audit records. The audit log is intended to
answer: who did what, to which target, when, from where, with what result, and
under which ticket.

Generic module audit API:

```csharp
public void LogModuleAction(
    string performedBy,
    string ipAddress,
    string action,
    string category,
    string target,
    bool success,
    string ticketNumber = "",
    string? errorDetail = null,
    Dictionary<string, object?>? extra = null);
```

Lookup audit API:

```csharp
public void LogLookupAction(
    string performedBy,
    string ipAddress,
    string action,
    string target,
    bool success,
    string? errorDetail = null,
    string ticketNumber = "");
```

Audit rules:

- Audit all mutating successes.
- Audit mutating failures.
- Audit authorization denials for mutating actions.
- Audit protected-principal blocks.
- Include previous and new values for data changes when safe.
- Do not include secrets, tokens, raw auth responses, or raw PowerShell output.
- Catch and log audit failures separately; an audit write failure must not
  change the result of an already committed backend write.

## Operation Tracing

Use `OperationTraceService` for backend diagnostic transcripts. Trace records
are separate from the business audit log.

API:

```csharp
public OperationScope BeginOperation(
    string module,
    string action,
    string actor,
    string ipAddress,
    string? target = null,
    string? ticket = null,
    IReadOnlyDictionary<string, object?>? details = null);

public void Step(
    string stage,
    string result = "Success",
    string? backend = null,
    string? command = null,
    string? target = null,
    IReadOnlyDictionary<string, object?>? details = null,
    Exception? exception = null);

public sealed class OperationScope : IDisposable
{
    public string OperationId { get; }
    public void Complete(bool success, string? message = null, Exception? exception = null);
}
```

`OperationScope` only owns operation lifetime and completion. It does not have a
`Step` method. Always call `_operationTrace.Step(...)` on the injected
`OperationTraceService`.

Pattern:

```csharp
using var scope = _operationTrace.BeginOperation(
    "MyModule",
    "UpdateThing",
    currentUser,
    clientIpAddress,
    target: targetId,
    ticket: ticket);

try
{
    _operationTrace.Step("ValidateInput");
    _operationTrace.Step("CredentialLookup", backend: "Delinea");
    _operationTrace.Step("BackendWrite", backend: "ActiveDirectory", command: "Set-ADUser", target: targetId);
    scope.Complete(true);
}
catch (Exception ex)
{
    _operationTrace.Step("BackendWrite", "Failed", backend: "ActiveDirectory", exception: ex);
    scope.Complete(false, "Update failed", ex);
    throw;
}
```

Trace rules:

- Trace backend milestones, not routine UI state.
- Keep trace details sanitized.
- Never log passwords, tokens, client secrets, raw API payloads, raw auth
  responses, or raw PowerShell streams.
- Prefer command name, backend, target, counts, duration, and sanitized result.

## Notifications

Use `EmailService` only when the module's workflow warrants operator or user
notification.

Rules:

- Notification failure must not change the backend operation result.
- Catch notification exceptions and log them.
- Avoid alert fatigue. Routine reads usually should not email anyone.
- Security-response modules may notify on every success/failure.

## File Uploads

If the module accepts uploaded files:

- Set an explicit max upload size.
- Prefer streaming and in-memory parsing.
- Use CsvHelper for CSV files with quotes, commas, or newlines.
- If a library requires a temp file, use a unique file in the OS temp directory.
- Delete temp files in `finally`.
- Log delete failures.
- Do not store uploads in the publish folder, config folder, audit folder, or
  source tree.
- Validate headers exactly and report missing/ambiguous headers clearly.
- Enforce row limits for bulk operations.

## UI Standards

ExchangeAdminWeb is an internal operations tool. UI should be compact, direct,
and task-focused.

Rules:

- Build the real workflow as the first screen.
- Do not create landing/marketing pages.
- Use existing Bootstrap styling and page patterns.
- Keep panels compact.
- Keep long result tables scrollable.
- Use sticky action panels for long workflows.
- Avoid explanatory architecture text in the UI.
- Use clear success/failure banners.
- Show partial success explicitly.
- Keep buttons disabled until required fields are present, but still validate
  server-side.
- Avoid putting destructive actions directly in dense tables when a confirmation
  edit/detail view is more appropriate.
- Destructive operations must require an explicit confirmation state. For
  high-impact actions, use typed confirmation.
- Never show raw stack traces, raw backend payloads, auth response bodies, or
  unsanitized exception details in the UI.

## Backend Result Design

Services should return typed result objects rather than raw strings when the
operation has more than success/failure.

Good result objects usually include:

- `Success`
- `Message`
- `Error`
- `Target`
- `ChangedCount`
- `SkippedCount`
- `PerRowResults`
- `OldValues`
- `NewValues`

Result objects returned to the UI should contain operator-safe messages.
Detailed exceptions belong in server logs or operation trace records after
sanitization. Do not put `Exception.StackTrace` into a UI-bound result.

Bulk modules should track per-row statuses:

- Pending
- Resolved
- Skipped
- Protected
- CheckFailed
- Updated
- Failed

Do not collapse partial success into a generic failure message.

## Dependency Injection

Register module services in `Program.cs`.

Default choices:

- `AddScoped<TService>()` for services that use PowerShell runspaces, UI-circuit
  state, or per-request behavior.
- `AddSingleton<TService>()` only for stateless, thread-safe services.

Add only the services the module actually uses. Do not keep scaffolding services
unreferenced after integration.

## Tests

Every module should include tests appropriate to its risk.

Minimum catalog tests:

- Module count increases as expected.
- Route is unique.
- Page route exists.
- Page policy matches descriptor main policy.
- Configurable policy aliases include main and granular aliases.
- The module does not add a standalone host, fake auth policy, or production
  host-service stub.

Security tests for mutating modules:

- Module disabled -> denied.
- No section access for fail-closed module -> denied.
- Missing required config -> no backend write.
- Corrupt config -> no backend write.
- Missing credentials -> no backend write.
- Authorization rechecked immediately before mutation.
- Protected principal -> no backend write.
- Ambiguous identity -> no backend write.
- Bound-object mismatch -> no backend write.

Parser/upload tests:

- Missing required headers.
- Header casing.
- Quoted commas/newlines.
- Empty file.
- Too many rows.
- Duplicate rows.
- Invalid values.

Audit/trace tests:

- Success audit contains action, category, target, ticket, result.
- Failure audit contains error.
- Denied action is audited.
- Audit failure does not change operation result.
- Trace details do not include sensitive keys.

Run before submitting:

```powershell
.\tools\validate-module-package.ps1 -PackagePath D:\source\isolated_module_test\MyModule -HostPath D:\source\ExchangeAdminWeb
dotnet build ExchangeAdminWeb.csproj --no-restore
dotnet test ExchangeAdminWeb.Tests\ExchangeAdminWeb.Tests.csproj --no-restore
dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore
git diff --check HEAD
```

## Deployment Considerations

New source modules require publishing the application and restarting the app
pool.

Runtime changes that do not require restart:

- Module enable/disable toggles.
- Section access group changes.
- Simple module config changes, if the service reads config at operation time or
  invalidates caches on save.

Deployment scripts preserve these config files:

- `config/sectionaccess.json`
- `config/module-config-{ModuleId}.json` (one file per module)
- `config/modules-enabled.json`
- `config/protected-principals.json`
- `config/ad-editable-attributes.json`
- `config/ad-editable-attributes-legend.json`

If a module introduces a new durable config or state file, update deployment and
promotion scripts intentionally. State files must not be wiped by publish or
promotion.

Do not store durable operational state only inside the publish folder unless the
deploy scripts explicitly preserve it.

## Documentation Required For Each Module

Each module should document:

- Purpose and intended operators.
- Required permissions/groups.
- Required module config fields.
- Required Delinea secret template and fields.
- Required Graph app permissions, if any.
- Required AD/Exchange permissions, if any.
- Whether the module mutates data.
- Protected-principal behavior.
- Audit actions emitted.
- Operation trace behavior.
- Manual dev validation steps.
- Rollback or remediation steps for failed operations.

## Review Checklist

Before a module is accepted, review these points:

- Descriptor route matches Razor `@page`.
- Descriptor policy matches Razor `[Authorize]`.
- Module is disabled by default if privileged or new.
- Main permission is fail-closed if privileged.
- Granular permissions are fail-closed where needed.
- Mutating operations re-check auth immediately before write.
- Tickets are required for mutating actions unless explicitly exempted.
- Ticket-bound buttons use `@bind:event="oninput"`.
- Module credentials are isolated through module config.
- Missing/corrupt config fails closed.
- Protected principals are enforced for identity-impacting writes.
- AD/Graph/Exchange writes are bound to immutable object identity where possible.
- Partial success is represented accurately.
- Audit records exist for success, failure, denied, and protected paths.
- Audit/email failures do not mask backend results.
- Operation traces are sanitized.
- Uploaded files are size-limited and cleaned up.
- Tests cover the high-risk branches.
- Isolated packages pass `tools\validate-module-package.ps1`.
- Deploy scripts preserve any new durable config/state.

## Common Mistakes To Avoid

- Building a page that is visible only because the nav hides unauthorized links.
  Direct routes must be denied too.
- Using another module's Delinea secret.
- Adding a global credential config.
- Falling back to app pool identity for privileged operations.
- Adding ServiceNow validation when it is not required. ServiceNow integration
  is a planned feature, gated by ServiceNow API access and dormant when
  `ServiceNow:Enabled=false`.
- Logging raw token failures, raw API payloads, passwords, or secrets.
- Re-resolving a free-form identity string at write time instead of writing to a
  bound object.
- Treating a multi-step mutation as all-or-nothing when earlier steps already
  committed.
- Marking a module complete with no tests for failure paths.
- Forgetting to increment module version after behavior changes.
- Adding new config/state files without updating deployment preservation.

## Definition Of Done

A module is done when:

- It has a descriptor, page, service, and DI registration.
- It follows the module credential and authorization model.
- It fails closed for missing config, missing credentials, corrupt config, and
  denied authorization.
- It audits and traces appropriately.
- It has focused tests for success and failure behavior.
- It is documented for operators and maintainers.
- Build, tests, format, and diff checks pass.
- Isolated package validation passes before integration review.
- It has been deployed to dev and validated with realistic operator workflows.
