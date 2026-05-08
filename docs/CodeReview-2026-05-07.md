# Code Review - 2026-05-07

Review target: `3f269c5` (`Consolidate duplicate notices; fix historical search subject/date validation`).

## Scope

I reviewed the current tracked codebase: host configuration, authorization, middleware, models, services, Razor components, tests, docs, deployment script, config sample, and example PowerShell scripts. Generated output, vendored/minified Bootstrap, binary images, and ignored local secrets were not treated as source.

ServiceNow ticket validation is explicitly out of scope. I did not count "ticket is not validated against ServiceNow" as a defect. I only reviewed local ticket capture/audit behavior and the operational risk of stale ServiceNow wiring.

## Verification

- Latest commit `3f269c5`: `dotnet build ExchangeAdminWeb.csproj --no-restore` passed with 0 warnings and 0 errors.
- Latest commit `3f269c5`: `dotnet test ExchangeAdminWeb.Tests\ExchangeAdminWeb.Tests.csproj --no-restore` passed: 85 tests.
- Latest commit `3f269c5`: `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` passed.
- Prior package checks found no vulnerable packages for app or tests. The latest commit did not change package references.
- Prior package checks found no deprecated packages for app or tests. The latest commit did not change package references.
- Outdated app packages remain: `Microsoft.AspNetCore.Authentication.Negotiate` 8.0.26 -> 10.0.7, `Microsoft.PowerShell.SDK` 7.4.15 -> 7.6.1, `Serilog.AspNetCore` 8.0.3 -> 10.0.0, `Serilog.Enrichers.Environment` 2.3.0 -> 3.0.1, `Serilog.Sinks.File` 5.0.0 -> 7.0.0.
- Outdated test packages remain: `coverlet.collector` 6.0.4 -> 10.0.0, `Microsoft.NET.Test.Sdk` 17.14.1 -> 18.5.1.

## Addendum - Dark Mode Persistence

I missed the dark-mode persistence bug in the original review. Before the latest remediation, the toggle stored the selected theme in `localStorage`, but the page-level script only applied that value on the initial document load. Section navigation is done with Blazor `NavLink`/enhanced navigation, so moving between sections can patch server-rendered markup without re-running the initial theme script. The result is that `localStorage` can still contain `dark` while the document loses the `html.dark` class and renders the next section in light mode.

Commits `b9aa35d` and `7a94a35` appear to remediate this by extracting `applyTheme()` in `Components/App.razor`, running it on initial load, `blazor:enhancedload`, and `pageshow`, and reapplying the stored theme if Blazor patches the document element class (`Components/App.razor:13`-`Components/App.razor:36`). `7a94a35` also fixed dark-mode striped table readability through Bootstrap table variables (`wwwroot/app.css:88`-`wwwroot/app.css:101`). `ThemeToggle` still toggles `html.dark` and writes the selected value to `localStorage` (`Components/Layout/ThemeToggle.razor:4`-`Components/Layout/ThemeToggle.razor:22`). This should preserve dark mode when moving between sections through the sidebar.

Residual risk: there is no browser/integration test for this behavior. Add a UI test that toggles dark mode, navigates through at least two sections, and asserts both `document.documentElement.classList.contains("dark")` and the toggle state remain consistent.

## Addendum - Message Trace Historical Search

Commit `7c4baee` added automatic historical search submission for Message Trace ranges over 10 days, removed admin notification email from lookup operations, and added CSV export for real-time trace results. The cmdlet choice is directionally correct: Microsoft documents `Get-MessageTrace` for recent trace data and `Start-HistoricalSearch` for older message data. Commits `509f3d5` and `3f269c5` addressed several review findings in this area, but there are still residual gaps.

References:
- https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/get-messagetrace?view=exchange-ps
- https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/start-historicalsearch?view=exchange-ps

### Medium - Historical Message Trace notification address is only partially resolved

`509f3d5` added UPN as a fallback after email claims (`Components/Pages/MessageTrace.razor:164`-`Components/Pages/MessageTrace.razor:167`), so this is better than the original email-claim-only version. If neither email nor UPN is present, `RunHistoricalSearch` still refuses to submit the search (`Components/Pages/MessageTrace.razor:232`-`Components/Pages/MessageTrace.razor:239`). Microsoft also requires `NotifyAddress` to be an internal recipient in an accepted domain, and this path does not validate that the resolved claim is a valid notification recipient before submitting it.

Impact: historical search can still fail for users whose Windows-auth claims do not include mail/UPN, or for environments where the UPN is not routable/accepted for Exchange notifications.

Recommendation: resolve the notification SMTP address explicitly from Exchange/AD using the current Windows identity, or validate a user-supplied/internal address before submission.

### Remediated - Historical Message Trace subject field is disabled in historical mode

`509f3d5` added visible copy that subject filtering is not available for historical searches. `3f269c5` now disables the `Subject Contains` input whenever `IsHistoricalRange` is true and changes the placeholder to explain that subject filtering is unavailable (`Components/Pages/MessageTrace.razor:43`-`Components/Pages/MessageTrace.razor:45`). Historical submission still sends only sender, recipient, start date, end date, notify address, and title (`Components/Pages/MessageTrace.razor:246`-`Components/Pages/MessageTrace.razor:250`; `Services/ExchangeService.cs:1444`-`Services/ExchangeService.cs:1455`).

Residual risk: if a user enters a subject filter in real-time mode and then expands the date range into historical mode, the disabled input can still display the stale subject value. The search correctly ignores it, but the UI can still be slightly misleading unless the value is cleared on mode change or the disabled state is visually explicit enough.

Recommendation: no blocking fix is required. Add a component/UI test for this branch, and consider clearing `subjectFilter` when switching into historical mode.

### Medium - Historical date validation only partially rejects future end-date submissions

`509f3d5` fixed the 90-day off-by-one by changing the local limit to `> 89` days before the `endDate.AddDays(1)` submission. `3f269c5` added an explicit `end > DateTime.Today` rejection (`Components/Pages/MessageTrace.razor:174`-`Components/Pages/MessageTrace.razor:183`). That prevents users from selecting tomorrow or later.

The remaining issue is that submission still passes `endDate.AddDays(1)` to `StartHistoricalSearchAsync` (`Components/Pages/MessageTrace.razor:246`-`Components/Pages/MessageTrace.razor:250`). If the selected end date is today, the actual submitted exclusive end timestamp is tomorrow at midnight. That can still be a future `EndDate`, and Microsoft documents historical searches as covering message data aged between roughly 1-4 hours and 90 days old.

Impact: Exchange can still reject a locally accepted historical search when the inclusive date picker value is today, even though the selected date itself is not in the future.

Recommendation: validate the actual submitted `[startDate, endExclusive)` range. For date-only UI values, clamp the submitted end to the current time or reject historical searches whose inclusive end date is today until the data is old enough for historical search.

### Low - Historical search quota and async lifecycle are not surfaced

Microsoft documents tenant limits for historical searches, including a daily submission quota and a per-file row limit. The UI automatically switches to historical search for ranges over 10 days (`Components/Pages/MessageTrace.razor:198`-`Components/Pages/MessageTrace.razor:200`) and only tells the user that results will be emailed later (`Components/Pages/MessageTrace.razor:59`-`Components/Pages/MessageTrace.razor:65`). It does not warn that submissions consume quota, provide a job ID, or offer a way to inspect existing historical jobs.

Impact: users can burn tenant historical-search quota without realizing it, and support has little in-app context if a submitted search is delayed or never arrives.

Recommendation: show the returned job ID when available, add copy about async processing/quota, and consider a minimal historical-job status view or link to the Exchange admin workflow.

### Remediated - Message Trace CSV formula hardening

`509f3d5` added formula-prefix hardening for exported Message Trace CSV fields that start with `=`, `+`, `-`, `@`, tab, CR, or LF (`Components/Pages/MessageTrace.razor:306`-`Components/Pages/MessageTrace.razor:313`). This matches the audit/bulk CSV mitigation pattern.

Residual risk: there are still no tests covering Message Trace CSV export shape or formula hardening.

## Addendum - Per-Section Audit Notices

Commit `928aa85` adds contextual audit notices to Mailbox, Calendar, Migration, Out of Office, Delegation Report, Message Trace, and Recipient Lookup pages. Commit `5591951` corrects the most important wording issues: Mailbox/Calendar stopped unconditionally saying affected users "will" receive notifications, and Migration now says "Migration actions" rather than "All migration operations." Commit `3f269c5` removes the duplicate Mailbox/Calendar notice blocks. The lookup/search notices align with the current code: Delegation Report, Message Trace, Historical Search, Recipient Lookup, and OOF status checks write lookup audit entries.

### Remediated - Duplicate Mailbox/Calendar audit notices were removed

`3f269c5` consolidates Mailbox and Calendar down to one audit notice each (`Components/Pages/MailboxPermissions.razor:20`-`Components/Pages/MailboxPermissions.razor:21`, `Components/Pages/CalendarPermissions.razor:20`-`Components/Pages/CalendarPermissions.razor:21`). The duplicate-copy issue from `928aa85` is closed.

### Low - Mailbox/Calendar notification sentence is still broad on bulk tabs

The remaining Mailbox and Calendar notices conditionally append "Affected users will receive email notifications when permissions are granted or removed" whenever `Email:NotifyUsersOnPermissionGrant` is true (`Components/Pages/MailboxPermissions.razor:20`-`Components/Pages/MailboxPermissions.razor:21`, `Components/Pages/CalendarPermissions.razor:20`-`Components/Pages/CalendarPermissions.razor:21`). That is accurate for successful single operations because those paths call `SendUserNotificationAsync` and `SendOwnerNotificationAsync` (`Components/Pages/MailboxPermissions.razor:332`-`Components/Pages/MailboxPermissions.razor:339`, `Components/Pages/CalendarPermissions.razor:330`-`Components/Pages/CalendarPermissions.razor:338`). It is still too broad for bulk operations: the bulk paths send only the admin summary notification and do not notify every affected user/owner (`Components/Pages/MailboxPermissions.razor:397`-`Components/Pages/MailboxPermissions.razor:400`, `Components/Pages/CalendarPermissions.razor:396`-`Components/Pages/CalendarPermissions.razor:399`).

Impact: when notification config is enabled, users can reasonably infer that bulk permission grants/removals send per-user notifications even though the code does not do that.

Recommendation: either make the notification sentence tab-aware, or change it to "For single permission changes, affected users will receive email notifications..." when the setting is enabled.

### Remediated - Migration notice no longer overstates all operations

`5591951` changes the Migration banner to "Migration actions are logged for audit purposes, including eligibility checks, batch creation, and status changes" (`Components/Pages/Migration.razor:19`). This is materially better than the prior "All migration operations" wording because it no longer implies that read-only status/report views are also audited.

Residual risk: the earlier audit-boundary gap remains: migration status loading, batch detail expansion, user search, and migration report retrieval are still not written to the audit CSV.

Recommendation: either audit the read-only migration views or keep documentation explicit that only eligibility checks and mutating migration actions are audited.

## Remediation Status Notes

The detailed findings below preserve the original review context. Current status after commits `b9aa35d`, `7a94a35`, `509f3d5`, `928aa85`, `5591951`, and `3f269c5`:

- Remediated: `Complete-MigrationUser` was replaced with `Set-MigrationUser -CompleteAfter`; OOF protected-user denial audit is guarded; audit/bulk/Message Trace CSV formula hardening is present; PathBase normalization was improved; audit column docs include `IPAddress`; the Home card no longer claims Delegation Report includes Send on Behalf; dark mode persistence and dark table readability are improved; duplicate Mailbox/Calendar audit notices were removed; the Migration audit banner no longer overstates audit behavior; historical Message Trace disables subject input in historical mode.
- Partially remediated: historical Message Trace has UPN fallback, selected future end-date rejection, and 90-day off-by-one handling, but still lacks robust notification-address resolution, validation/clamping of the actual submitted end timestamp, quota/job lifecycle surfacing, and parameter-level tests. `OnPremTargetDatabases` is now split into a string array, but the sample/default still uses `DAG2019` and there is still no validation that values are mailbox database identities. Protected-user ObjectIds are cached after initialization, but group expansion remains direct-only and self-grant checks still perform live identity lookups. Per-section audit notices exist, but Mailbox/Calendar notification wording is still too broad on bulk tabs when user notifications are enabled.
- Still open: IIS app pool identity setup, operation-level PowerShell timeout/cancellation, email notification failure semantics, section-level permissions implementation, section-permission plan inconsistencies, migration read audit boundary, direct/nested protected group coverage, client IP forwarding/cache cleanup, and ServiceNow dead/stale wiring.

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
- No tests cover Message Trace historical-search branching, notification-address resolution, actual submitted end-date clamping/rejection, quota/job messaging, or `Start-HistoricalSearch` parameter emission.
- No tests cover Message Trace CSV export escaping, formula hardening, or downloaded CSV shape.
- No component/UI tests cover the audit notices, including bulk-vs-single notification wording and the historical subject disabled state.

## Summary

The app builds cleanly and the existing helper tests are passing. The duplicate Mailbox/Calendar notices are removed, but the remaining conditional notification sentence is still too broad on bulk tabs. Historical Message Trace is improved, with the subject field disabled in historical mode and selected future dates rejected, but it still needs stronger notification-address resolution, validation or clamping of the actual submitted end timestamp, quota/job messaging, and parameter-level tests. The biggest remaining platform risks are IIS identity setup, on-prem target database assumptions, and lack of true PowerShell operation timeouts.
