# Code Review - 2026-05-07

Review target: `ce1d249` (`Add section-level permissions plan to docs/`).

## Scope

I reviewed the current tracked codebase: host configuration, authorization, middleware, models, services, Razor components, tests, docs, deployment script, config sample, and example PowerShell scripts. Generated output, vendored/minified Bootstrap, binary images, and ignored local secrets were not treated as source.

ServiceNow ticket validation is explicitly out of scope. I did not count "ticket is not validated against ServiceNow" as a defect. I only reviewed local ticket capture/audit behavior and the operational risk of stale ServiceNow wiring.

## Verification

- `dotnet build ExchangeAdminWeb.csproj --no-restore` passed with 0 warnings and 0 errors.
- `dotnet test ExchangeAdminWeb.Tests\ExchangeAdminWeb.Tests.csproj --no-restore` passed: 85 tests.
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` passed.
- `dotnet list ... package --vulnerable --include-transitive` found no vulnerable packages for app or tests.
- `dotnet list ... package --deprecated` found no deprecated packages for app or tests.
- Outdated app packages remain: `Microsoft.AspNetCore.Authentication.Negotiate` 8.0.26 -> 10.0.7, `Microsoft.PowerShell.SDK` 7.4.15 -> 7.6.1, `Serilog.AspNetCore` 8.0.3 -> 10.0.0, `Serilog.Enrichers.Environment` 2.3.0 -> 3.0.1, `Serilog.Sinks.File` 5.0.0 -> 7.0.0.
- Outdated test packages remain: `coverlet.collector` 6.0.4 -> 10.0.0, `Microsoft.NET.Test.Sdk` 17.14.1 -> 18.5.1.

## Addendum - Dark Mode Persistence

I missed the dark-mode persistence bug in the original review. Before the latest remediation, the toggle stored the selected theme in `localStorage`, but the page-level script only applied that value on the initial document load. Section navigation is done with Blazor `NavLink`/enhanced navigation, so moving between sections can patch server-rendered markup without re-running the initial theme script. The result is that `localStorage` can still contain `dark` while the document loses the `html.dark` class and renders the next section in light mode.

Current HEAD `b9aa35d` appears to remediate this by extracting `applyTheme()` in `Components/App.razor`, running it on initial load, and also running it on `blazor:enhancedload` (`Components/App.razor:13`-`Components/App.razor:24`). `ThemeToggle` still toggles `html.dark` and writes the selected value to `localStorage` (`Components/Layout/ThemeToggle.razor:4`-`Components/Layout/ThemeToggle.razor:22`). This should preserve dark mode when moving between sections through the sidebar.

Residual risk: there is no browser/integration test for this behavior. Add a UI test that toggles dark mode, navigates through at least two sections, and asserts both `document.documentElement.classList.contains("dark")` and the toggle state remain consistent.

## Findings

### High - Section-permission plan can bypass the base `AllowedGroups` gate

The plan says `AllowedGroups` remains the base gate (`docs/SectionPermissions-Plan.md:67`), then proposes changing pages from `GroupPolicy` to section policies (`docs/SectionPermissions-Plan.md:121`-`docs/SectionPermissions-Plan.md:132`). That does not automatically preserve the current base gate. In ASP.NET Core authorization, the fallback policy is not a second layer once an endpoint/component has explicit authorization metadata. If a page switches to `[Authorize(Policy = "MigrationCheck")]`, the section policy itself must also require base `AllowedGroups`; otherwise a user in `ANALOG\MigrationTeam` but not in the base app groups could reach the page if the section policy allows that group.

Impact: the plan's intended "base app access plus section access" can become "section access only" unless implemented carefully.

Recommendation: compose every section policy from two requirements: base `AllowedGroups` AND the section group list. Alternatively, create one explicit `BaseAndSectionAuthorizationRequirement`. Also validate config so `MigrationCreate`/`MigrationManage` cannot silently drift outside the intended base-gate model.

### Medium - Section plan has an Out-of-Office contradiction

The plan's Current State says "Out of Office does not enforce the same protected-user restrictions" (`docs/SectionPermissions-Plan.md:13`), but later says OOF is already partially wired because `ValidateTargetMailboxAsync` exists in `SetOof` (`docs/SectionPermissions-Plan.md:61`-`docs/SectionPermissions-Plan.md:63`). Runtime confirms the latter: `SetOof` validates the target before calling Exchange (`Components/Pages/OutOfOffice.razor:252`-`Components/Pages/OutOfOffice.razor:266`).

Impact: implementers can waste time adding a guard that already exists, while missing the real issue: the protected-user denial audit write is not guarded, and OOF status checks are still allowed for protected users.

Recommendation: update the plan to say "OOF set/clear already validates protected users; denial audit handling needs cleanup; decide whether OOF read/check should also be blocked for protected users."

### Medium - Migration permission tiers omit existing actions and sensitive reads

The plan defines migration tiers as Check, Create, and Manage (`docs/SectionPermissions-Plan.md:38`-`docs/SectionPermissions-Plan.md:48`) and says `MigrationCheck` includes read-only batch status (`docs/SectionPermissions-Plan.md:54`-`docs/SectionPermissions-Plan.md:59`). Current Migration UI has more than start/stop/remove: complete batch, complete user, approve skipped items, pause, resume, clear user, clear completed, user search, and report retrieval (`Components/Pages/Migration.razor:552`-`Components/Pages/Migration.razor:595`, `Components/Pages/Migration.razor:990`-`Components/Pages/Migration.razor:995`, `Components/Pages/Migration.razor:1292`-`Components/Pages/Migration.razor:1308`).

Impact: a user granted only "check" might also get broad visibility into every migration batch and detailed migration reports. Conversely, some management buttons may be forgotten when the `MigrationManage` gates are implemented.

Recommendation: explicitly list every migration action under `MigrationManage`, and decide whether batch status, user search, and migration reports belong under `MigrationCheck`, a separate `MigrationStatus`/`MigrationReport` permission, or `MigrationManage`.

### Medium - Section-denied routing is underspecified for Razor components

The plan says to configure authorization middleware to redirect section policy failures to a section-denied page (`docs/SectionPermissions-Plan.md:190`-`docs/SectionPermissions-Plan.md:199`). The current app handles route authorization through `AuthorizeRouteView` and a `NotAuthorized` fragment that navigates to `/access-denied` (`Components/Routes.razor:6`-`Components/Routes.razor:10`). Middleware alone will not distinguish "not in base app" from "in app, denied for this section" in that component-level route flow.

Impact: section failures may keep landing on the generic access denied page, or implementation may become inconsistent between direct URL navigation and in-app hidden links.

Recommendation: design the denial path in `Routes.razor`/component authorization explicitly. For example, authenticated-but-section-denied users can render a `SectionDenied` component, while unauthenticated/base-denied users go to the current access-denied path.

### Medium - Plan calls for server-side action checks but does not allocate them

The plan correctly says Migration server-side methods should check permissions and not rely only on hidden UI (`docs/SectionPermissions-Plan.md:154`-`docs/SectionPermissions-Plan.md:156`). The file list only calls out page/policy/nav/doc changes (`docs/SectionPermissions-Plan.md:210`-`docs/SectionPermissions-Plan.md:228`) and does not define a reusable authorization helper or tests for action handlers.

Impact: it is easy to implement card/link hiding and `[Authorize]` attributes while leaving existing event handlers callable inside an already-authorized Migration page.

Recommendation: add an implementation step for per-action `IAuthorizationService.AuthorizeAsync` checks inside `CreateSingleMigrationBatch`, `CreateMigrationBatch`, `ExecuteBatchAction`, `ExecuteUserAction`, and `ClearCompletedBatches`, with tests around denied action paths.

### High - Fresh deploy likely breaks the IIS app pool custom identity

`deploy.ps1` defaults `ServiceAccount` to `ANALOG\SVC_SCRIPTADM` and configures the app pool as a specific user (`deploy.ps1:7`, `deploy.ps1:69`, `deploy.ps1:70`), but it never sets `processModel.password` or documents a gMSA-specific setup path. A newly created IIS app pool with a custom identity normally cannot start without a password or valid gMSA configuration. The script then grants certificate and audit-log ACLs to that same account (`deploy.ps1:131`, `deploy.ps1:151`), so this is the intended runtime identity, not a harmless display value.

README still says the default is `ApplicationPoolIdentity` (`README.md:49`, `README.md:283`) and only shows setting a password as a troubleshooting step (`README.md:287`-`README.md:289`).

Impact: first-time deploys can publish successfully and then fail at app pool startup, or operators may manually change IIS identity and invalidate certificate/log ACL assumptions.

Recommendation: either keep `ApplicationPoolIdentity` as the default and grant ACLs to `IIS AppPool\<name>`, or make domain service account setup explicit with a secure password/gMSA path.

### High - Per-user migration completion calls a cmdlet that does not appear to exist

The Migration page renders a per-user `Complete` button for `Synced` users (`Components/Pages/Migration.razor:552`-`Components/Pages/Migration.razor:555`). That calls `CompleteUser` (`Components/Pages/Migration.razor:991`), which calls `ExchangeService.CompleteMigrationUserAsync`, which invokes `Complete-MigrationUser` (`Services/ExchangeService.cs:964`-`Services/ExchangeService.cs:973`).

Microsoft's Exchange migration docs expose `Set-MigrationUser -CompleteAfter` for user-level completion settings and `Complete-MigrationBatch` for batch finalization; I could not find an official `Complete-MigrationUser` cmdlet. The test suite does not validate emitted PowerShell command names, so this compiles and all tests pass while the UI action can still fail at runtime.

Impact: a likely operator action for synced migration users fails with a PowerShell command-not-found error.

Recommendation: replace with a supported flow, likely `Set-MigrationUser -CompleteAfter <time>` where per-user completion is intended, and add tests around migration cmdlet emission.

References:
- https://learn.microsoft.com/en-us/powershell/module/exchange/set-migrationuser?view=exchange-ps
- https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/complete-migrationbatch?view=exchange-ps

### High - To-on-prem migration target database handling is likely wrong

The sample config sets `Migration:OnPremTargetDatabases` to `DAG2019` (`appsettings.json.sample:48`). The service reads it as a single string (`Services/ExchangeService.cs:44`) and passes it as one `TargetDatabases` value (`Services/ExchangeService.cs:708`, `Services/ExchangeService.cs:732`). The legacy script resolved actual mailbox database names from the DAG first (`examplescripts/NewMigrationBatch.ps1:61`) and passed those names (`examplescripts/NewMigrationBatch.ps1:62`).

Microsoft documents `New-MigrationBatch -TargetDatabases` as mailbox database identities. If production follows the sample value, offboarding batch creation is likely to fail. If an operator supplies comma-separated DBs, the current code still passes one string element rather than multiple database identities.

Impact: `ToOnPrem` batch creation can fail or target an unintended database set.

Recommendation: make `OnPremTargetDatabases` a string array of real mailbox database identities, validate it before creating the batch, and support split/trim only as backward compatibility.

Reference:
- https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/new-migrationbatch?view=exchange-ps

### High - The two-minute timeout is only a queue timeout, not an operation timeout

The throttle waits up to two minutes to enter the semaphore (`Services/ExchangeService.cs:1845`, `Services/ExchangeService.cs:1951`). Once a slot is acquired, the PowerShell operation runs in `Task.Run` without cancellation or a wall-clock timeout (`Services/ExchangeService.cs:1850`, `Services/ExchangeService.cs:1955`). `PermissionValidator.TryExpandGroupAsync` also creates its own raw runspace outside the shared throttle (`Services/PermissionValidator.cs:200`-`Services/PermissionValidator.cs:204`).

Impact: a hung `Connect-ExchangeOnline`, `New-PSSession`, or Exchange cmdlet can hold a throttle slot and Blazor circuit indefinitely. The app can still get stuck even though the code appears to have a two-minute timeout.

Recommendation: add operation-level timeout/cancellation around PowerShell invocation and best-effort `PowerShell.Stop()`/runspace cleanup. Apply the same pattern to protected-user group expansion.

### Medium - Protected-user and self-grant validation can fan out into many EXO sessions

`IsUserExcludedAsync` resolves the target identity, then loops over every excluded identity and resolves each one (`Services/PermissionValidator.cs:47`-`Services/PermissionValidator.cs:53`). Each `ResolveToObjectIdAsync` creates a new runspace, connects to EXO, runs `Get-Recipient`, and disconnects (`Services/ExchangeService.cs:1903`-`Services/ExchangeService.cs:1939`). `ValidateSelfGrantAsync` adds two more identity resolutions per add/set operation (`Services/PermissionValidator.cs:118`, `Services/PermissionValidator.cs:119`).

Impact: one validation can become N+1 Exchange Online sessions, and bulk CSV multiplies that per row. This can consume the same throttle the write operations need.

Recommendation: cache resolved object IDs for excluded identities for the same refresh window, add a short-lived target/user resolution cache, and resolve batches in one runspace where possible.

### Medium - Protected group expansion is direct-only and documentation overstates it

Protected-user loading uses `Get-DistributionGroupMember` once for an excluded group (`Services/PermissionValidator.cs:231`-`Services/PermissionValidator.cs:247`). It does not recurse nested groups, does not handle dynamic membership, and has no tests around expansion behavior. README and security docs say groups are auto-expanded to members (`README.md:188`, `NOTIFICATIONS_SECURITY.md:116`-`NOTIFICATIONS_SECURITY.md:120`) without documenting these limits.

Impact: protected users in nested or dynamic groups can be missed.

Recommendation: implement recursive expansion with cycle protection, or document that only direct static group members are protected.

### Medium - Email notification failures do not affect audit/result semantics

`EmailService.SendEmailAsync` catches all exceptions and only logs them (`Services/EmailService.cs:296`-`Services/EmailService.cs:325`). Page-level `try/catch` blocks around notification sends generally cannot observe SMTP failures because the service swallows them. Docs say admin notifications are always sent (`README.md:208`, `NOTIFICATIONS_SECURITY.md:5`-`NOTIFICATIONS_SECURITY.md:17`).

Impact: an action can be shown and audited as successful even when required admin notification delivery failed. The only signal is in application logs.

Recommendation: return a notification result or throw for admin-notification failure, then audit or surface notification failure separately from the Exchange operation result.

### Medium - Section-level permissions are still only a plan

The section access work has not been implemented yet. Runtime still registers only `GroupPolicy` (`Program.cs:24`-`Program.cs:41`), every feature page still uses `Authorize(Policy = "GroupPolicy")`, and Home/Nav render direct links (`Components/Pages/Home.razor:41`-`Components/Pages/Home.razor:118`, `Components/Layout/NavMenu.razor:11`-`Components/Layout/NavMenu.razor:49`).

Impact: if section-level permissions are now a requirement, any user in the base allowed groups can still access every feature, including migration management and message trace.

Recommendation: keep the plan clearly labeled as future work until implemented, and enforce section checks in server-side action methods as well as UI visibility.

### Medium - Migration status/report reads are not audited despite broad audit claims

The architecture doc says every operation, including read-only lookups, is recorded (`docs/Architecture Design Document.md:120`-`docs/Architecture Design Document.md:123`). Migration status loading, batch detail expansion, user search, and migration report retrieval do not write audit entries (`Components/Pages/Migration.razor:919`, `Components/Pages/Migration.razor:943`, `Components/Pages/Migration.razor:1227`, `Components/Pages/Migration.razor:1292`).

Impact: operators can view migration batches, users, and reports without audit CSV records, contrary to the stated audit model.

Recommendation: define the intended audit boundary. If these reads are sensitive, log them. If not, narrow the docs.

### Medium - Delegation Report says "Send on Behalf" but does not collect it

The Home card says Delegation Report includes Send on Behalf permissions (`Components/Pages/Home.razor:78`-`Components/Pages/Home.razor:82`). The service retrieves Full Access, Send As, and Calendar permissions (`Services/ExchangeService.cs:1250`-`Services/ExchangeService.cs:1299`), but does not read mailbox `GrantSendOnBehalfTo`.

Impact: operators can trust the report as complete while a common delegation type is omitted.

Recommendation: add Send-on-Behalf retrieval to the model/UI, or remove the claim.

### Medium - Out-of-office protected-user denial can throw while auditing the denial

`SetOof` validates protected users before setting `isSetting = true` (`Components/Pages/OutOfOffice.razor:252`-`Components/Pages/OutOfOffice.razor:269`). If the target is protected, it immediately calls `Audit.LogMigrationAction` without a local guard (`Components/Pages/OutOfOffice.razor:260`-`Components/Pages/OutOfOffice.razor:266`). Later OOF audit writes are guarded (`Components/Pages/OutOfOffice.razor:280`-`Components/Pages/OutOfOffice.razor:290`).

Impact: if audit storage is unavailable, a protected-user denial can turn into an unhandled UI error rather than a controlled denial message.

Recommendation: guard this denial audit write consistently. The operation is already blocked; the user should still see the protected-user denial.

### Low - ServiceNow code is out of scope but still confusingly wired

`ServiceNowService` is registered (`Program.cs:50`-`Program.cs:60`) and Mailbox/Calendar pages call `ValidateTicketAsync` (`Components/Pages/MailboxPermissions.razor:264`, `Components/Pages/MailboxPermissions.razor:377`, `Components/Pages/CalendarPermissions.razor:260`, `Components/Pages/CalendarPermissions.razor:376`). When disabled, it intentionally returns valid (`Services/ServiceNowService.cs:35`-`Services/ServiceNowService.cs:42`). Migration and OOF only capture ticket text.

This is not a ServiceNow-validation defect. The risk is operational: if someone enables it, mailbox/calendar writes depend on unproven Basic-auth ServiceNow calls while migration/OOF behavior is unchanged.

Recommendation: quarantine this as future/dead code, add an explicit out-of-scope comment, or remove it until ServiceNow integration is actively designed and tested.

### Low - CSV formula-injection mitigation misses leading LF

Audit and bulk-report CSV escaping prefix fields that start with `=`, `+`, `-`, `@`, tab, or CR (`Services/AuditService.cs:231`-`Services/AuditService.cs:233`, `Models/BulkOperationResult.cs:32`-`Models/BulkOperationResult.cs:34`). They do not handle leading LF, even though fields containing LF are quoted.

Impact: narrow spreadsheet-hardening gap if attacker-controlled fields are opened in Excel.

Recommendation: handle `\n` with the same formula-prefix rules, or strip/control-prefix all leading ASCII control characters before formula checks.

### Low - PathBase is not normalized

`Program.cs` passes the raw configured value to `UsePathBase` (`Program.cs:68`, `Program.cs:69`), and `App.razor` appends `/` to the raw value for `<base>` (`Components/App.razor:8`). The sample `/ExchangeAdminWeb` works (`appsettings.json.sample:67`-`appsettings.json.sample:69`), but `/ExchangeAdminWeb/`, empty string, or `/` can produce inconsistent base URLs.

Recommendation: normalize once at startup and reuse the normalized value for middleware and base href.

### Low - Client IP capture is best-effort and cache has no cleanup

`ClientInfoMiddleware` stores `RemoteIpAddress` and user agent in a static dictionary keyed by username (`Middleware/ClientInfoMiddleware.cs:22`-`Middleware/ClientInfoMiddleware.cs:32`, `Services/ClientInfoService.cs:11`-`Services/ClientInfoService.cs:36`). There is no forwarded-header handling and expired entries are never removed.

Impact: audit IPs can be proxy/load-balancer IPs, and the cache can grow in long-lived processes.

Recommendation: configure forwarded headers if deployed behind a proxy, and remove stale cache entries.

### Low - Audit documentation still has the old column order

`AuditService` writes `IPAddress` in the header (`Services/AuditService.cs:11`), and tests assert `TimestampUtc,User,IPAddress,TicketNumber` (`ExchangeAdminWeb.Tests/AuditServiceTests.cs:55`-`ExchangeAdminWeb.Tests/AuditServiceTests.cs:58`). README and notification docs still show audit columns without `IPAddress` (`README.md:198`-`README.md:199`, `NOTIFICATIONS_SECURITY.md:210`-`NOTIFICATIONS_SECURITY.md:221`).

Impact: parsers or compliance steps based on docs can parse the wrong columns.

Recommendation: update docs to match the exact runtime header.

## Test Coverage Gaps

- No tests validate PowerShell cmdlet names or parameters emitted by `ExchangeService`.
- No tests cover `CreateMigrationBatchAsync` for `ToOnPrem` target database values.
- No tests cover `DelineaService`, `EmailService`, or IIS deploy behavior.
- No tests cover protected group expansion, nested groups, or identity resolution caching.
- No component/integration tests cover Blazor page action flows.
- No tests are planned yet for section-policy composition: base gate AND section gate, missing/empty section fail-closed behavior, direct URL denial, hidden Nav/Home links, and denied Migration sub-actions.
- No browser/UI test covers dark-mode persistence across Blazor enhanced navigation.

## Summary

The app builds cleanly and core helper tests are passing. The biggest remaining risks are runtime integration risks: IIS identity setup, Exchange migration cmdlet correctness, on-prem target database assumptions, and lack of true PowerShell operation timeouts. The prior review also overstated some remediation because it did not distinguish queue timeout from operation timeout, or configured database name from actual database discovery.
