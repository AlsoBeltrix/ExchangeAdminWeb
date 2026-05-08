# Code Review - 2026-05-08

## Scope

Reviewed all first-party source, Razor components, services, models, tests, configuration samples, IIS deployment script, example PowerShell scripts, and project documentation in the repository. Generated build output under `bin/` and `obj/` was excluded. `ServiceNowService` and ServiceNow ticket-validation behavior are out of scope per request. The findings below are grounded in the current application, deployment, configuration, and documentation files; legacy/example scripts were not treated as authoritative behavior for the current app.

No application code was changed. This file is the only intended output from the review. Existing local change `docs/CodeReview-2026-05-07.md` was not touched.

## Verification

- `dotnet build ExchangeAdminWeb.csproj --no-restore` - passed, 0 warnings, 0 errors.
- `dotnet test ExchangeAdminWeb.Tests\ExchangeAdminWeb.Tests.csproj --no-restore` - passed, 85 tests.
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` - passed.
- `dotnet list ... package --vulnerable --include-transitive` - no vulnerable packages reported for app or tests.
- `dotnet list ... package --deprecated` - no deprecated packages reported for app or tests.
- `dotnet list ... package --outdated` - several packages are behind current major versions; see the package-version finding below.

## Findings

### High - New IIS deployments using the default domain service account can fail to start

`deploy.ps1` defaults `ServiceAccount` to `ANALOG\SVC_SCRIPTADM`, configures the app pool with `processModel.identityType = 3` (`SpecificUser`), and sets only `processModel.userName` (`deploy.ps1:3`-`deploy.ps1:10`, `deploy.ps1:66`-`deploy.ps1:71`). The script never accepts a password/credential parameter and never sets `processModel.password`. Microsoft documents `SpecificUser` as a custom identity configured with `userName` and `password`; the `password` attribute is necessary when `identityType` is `SpecificUser`. As written, a new app pool using the default domain service-account value is likely to fail at start unless the pool already has retained credentials or the configured account is intentionally a managed service account.

Recommendation: make the script explicitly support either a gMSA mode or a credential prompt/parameter and set `processModel.password` for specific-user accounts.

### High - The app and deployment path do not enforce HTTPS

The runtime pipeline does not call `UseHttpsRedirection()` or HSTS (`Program.cs:74`-`Program.cs:81`), `web.config` does not enforce HTTPS (`web.config:1`-`web.config:18`), and the deployment script prints an `http://localhost` URL (`deploy.ps1:162`-`deploy.ps1:165`). Microsoft documents `UseHttpsRedirection()` and `UseHsts()` as the ASP.NET Core mechanisms for redirecting HTTP and applying HSTS outside development. This app carries Windows-authenticated administrative workflows, ticket numbers, recipient data, audit metadata, and SignalR circuit traffic. If the parent IIS site already enforces HTTPS, runtime exposure may be controlled there, but the current app/deploy path does not validate or enforce that requirement and leaves an easy misconfiguration path.

Recommendation: enforce HTTPS at the IIS site or app level, add HSTS/redirects where appropriate, and make the deployment script fail or warn if the parent site has no HTTPS binding.

### High - Move-back migration batches pass a DAG name where `TargetDatabases` expects database identities

`ExchangeService` reads `Migration:OnPremTargetDatabases`, splits the configured string, and passes those values directly to `New-MigrationBatch -TargetDatabases` for `ToOnPrem` batches (`Services/ExchangeService.cs:44`-`Services/ExchangeService.cs:45`, `Services/ExchangeService.cs:709`, `Services/ExchangeService.cs:729`-`Services/ExchangeService.cs:736`). The tracked sample config sets this value to `"DAG2019"` (`appsettings.json.sample:48`), and `DAG2019` is the Database Availability Group name. Microsoft documents `TargetDatabases` as the identity of the database to move mailboxes to, accepting values such as database name, DN, or GUID; when multiple database identities are provided, the migration service selects one database from the list.

As implemented, move-back batch creation is likely to fail because `TargetDatabases` receives a DAG name instead of mailbox database identities. This is a current-code configuration/parameter mismatch. For the stated operational requirement of more than 50 databases in `DAG2019` with Exchange choosing the target database, the app should resolve the DAG to eligible mailbox database names and pass the full list of database identities to `TargetDatabases`. That matches Microsoft's documented behavior: when multiple database identities are provided, the migration service selects one database from the list.

Recommendation: rename the configuration to represent a DAG, resolve it with on-premises Exchange data (`Get-DatabaseAvailabilityGroup` for the DAG and `Get-MailboxDatabase`/database properties to enumerate mailbox database identities), filter out recovery or provisioning-excluded databases as appropriate for the environment, and pass the resulting database-name array to `New-MigrationBatch -TargetDatabases`. If the setting remains `OnPremTargetDatabases`, require actual database identities and validate that configured values are not DAG names.

### Medium - Single protected-target denials are not audited for mailbox/calendar permission pages

Bulk operations audit protected-target denials (`Services/ExchangeService.cs:238`-`Services/ExchangeService.cs:245`, `Services/ExchangeService.cs:364`-`Services/ExchangeService.cs:370`), and Out of Office denials are audited (`Components/Pages/OutOfOffice.razor:261`-`Components/Pages/OutOfOffice.razor:269`). Single mailbox and calendar operations return immediately after `ValidateTargetMailboxAsync` without writing an audit entry (`Components/Pages/MailboxPermissions.razor:267`-`Components/Pages/MailboxPermissions.razor:273`, `Components/Pages/CalendarPermissions.razor:263`-`Components/Pages/CalendarPermissions.razor:269`).

That leaves attempted changes against protected users absent from the compliance audit trail.

Recommendation: log denied single-operation attempts before returning, matching the bulk and OOF paths.

### Medium - Historical message trace omits the selected end date

Realtime message trace expands the selected end date with `endDate.AddDays(1)` so the whole selected end day is included (`Components/Pages/MessageTrace.razor:220`-`Components/Pages/MessageTrace.razor:224`). Historical search passes `endDate` directly (`Components/Pages/MessageTrace.razor:249`-`Components/Pages/MessageTrace.razor:253`). Because date inputs bind to midnight, historical ranges end at `00:00` on the chosen end date and omit that day.

Recommendation: use the same end-date inclusion rule for historical searches, unless the UI is changed to collect explicit times.

### Medium - Access-denied redirects can break when hosted under `/ExchangeAdminWeb`

The app supports a path base and emits `<base href="/ExchangeAdminWeb/">` (`Program.cs:68`-`Program.cs:72`, `Components/App.razor:8`), but authorization failures navigate to the root-relative URI `"/access-denied"` (`Components/Routes.razor:7`-`Components/Routes.razor:10`, `Components/AuthorizationCheck.razor:18`-`Components/AuthorizationCheck.razor:30`, and page-level redirects such as `Components/Pages/Home.razor:136`-`Components/Pages/Home.razor:140`). Under an IIS virtual application, a root-relative URI resolves to the parent site root, not `/ExchangeAdminWeb/access-denied`.

Recommendation: navigate relative to the app base (`"access-denied"`) or build the URL through `NavigationManager.ToAbsoluteUri`/configured path-base helpers consistently.

### Medium - Audit IP attribution is fragile and can be wrong

`ClientInfoMiddleware` stores `RemoteIpAddress` in a static cache keyed only by username (`Middleware/ClientInfoMiddleware.cs:20`-`Middleware/ClientInfoMiddleware.cs:35`, `Services/ClientInfoService.cs:11`-`Services/ClientInfoService.cs:36`). There is no forwarded-header handling, no cleanup of expired cache entries, and a later request for the same username from a different network can overwrite the value used by subsequent page/circuit initialization for that account. Microsoft documents that forwarded headers must be explicitly enabled and restricted to trusted proxies/networks; the current app does neither. In proxy/load-balancer deployments, audits can record the proxy address instead of the real client IP. In multi-session scenarios for the same account, audits can use the most recently cached IP rather than the IP for the active operator session.

Recommendation: use forwarded headers only from trusted proxies, keep client info in circuit-scoped state where possible, and purge or timestamp-remove cache entries.

### Medium - Migration batch status counts may render as zero

`GetMigrationBatchesAsync` reads `SyncedItemCount`, `FinalizedItemCount`, and `FailedItemCount` from `Get-MigrationBatch` results and defaults missing values to zero (`Services/ExchangeService.cs:823`-`Services/ExchangeService.cs:828`). Microsoft documents migration-batch count fields as `Total`, `Synced`, `Finalized`, and `Failed` in the Exchange admin center, and the published `MigrationBatch` API properties include `TotalCount`, `SyncedCount`, `FinalizedCount`, and `FailedCount`. The current code already reads `TotalCount`, but the other three names use `*ItemCount`; if those properties are absent on the live `Get-MigrationBatch` objects, the dashboard silently shows zero synced/finalized/failed counts and can mislead operators.

Recommendation: map the batch counters to `SyncedCount`, `FinalizedCount`, and `FailedCount` unless the live tenant proves different names. Keep a diagnostic check such as `Get-MigrationBatch | Format-List *Count*` during rollout and add a parser test around the mapping.

### Low - Message trace admin notification is documented but not implemented

The architecture document says message trace searches produce admin notification (`docs/Architecture Design Document.md:146`-`docs/Architecture Design Document.md:156`, `docs/Architecture Design Document.md:216`-`docs/Architecture Design Document.md:223`). The page only writes the audit CSV for realtime and historical searches (`Components/Pages/MessageTrace.razor:226`-`Components/Pages/MessageTrace.razor:228`, `Components/Pages/MessageTrace.razor:264`).

Recommendation: either send the documented admin notification or update the documentation to say message trace is audit-only.

### Low - NuGet package versions are skewed behind the .NET 10 target

The app and tests target `net10.0-windows10.0.17763.0` (`ExchangeAdminWeb.csproj:4`, `ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj:4`), but package metadata from NuGet shows several top-level packages behind current major versions: `Microsoft.AspNetCore.Authentication.Negotiate` 8.0.26 -> 10.0.7, `Microsoft.PowerShell.SDK` 7.4.15 -> 7.6.1, `Serilog.AspNetCore` 8.0.3 -> 10.0.0, `Serilog.Enrichers.Environment` 2.3.0 -> 3.0.1, `Serilog.Sinks.File` 5.0.0 -> 7.0.0, `coverlet.collector` 6.0.4 -> 10.0.0, and `Microsoft.NET.Test.Sdk` 17.14.1 -> 18.5.1 (`ExchangeAdminWeb.csproj:13`-`ExchangeAdminWeb.csproj:17`, `ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj:11`-`ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj:12`). Vulnerability and deprecation checks are clean, so this is compatibility/support hygiene rather than an urgent security issue.

Recommendation: plan a controlled package alignment pass, especially for ASP.NET Core authentication packages, with regression testing under IIS Windows Authentication.

## Test Gaps

- No automated coverage for Blazor action flows around protected-target denials, audit logging, and notification side effects.
- No test for path-base hosting or access-denied navigation under `/ExchangeAdminWeb`.
- No test for historical message trace date inclusivity.
- No test seam around migration batch/user PowerShell result mapping, including `TargetDatabases` input validation and batch count property names.
- No deployment validation test or dry-run mode for `deploy.ps1`.

## Notes

- ServiceNow-specific behavior was intentionally not reviewed.
- `appsettings.json` and `appsettings.Development.json` are ignored and not tracked; `appsettings.json.sample` is the tracked configuration template.
- The code consistently uses PowerShell parameter binding rather than interpolated command strings for Exchange Online operations, which is the right direction for command-injection resistance.

## External References Checked

- Microsoft Learn, IIS `<processModel>`: `SpecificUser` is configured with `userName` and `password`; `password` is necessary for `SpecificUser`. <https://learn.microsoft.com/en-us/iis/configuration/system.applicationhost/applicationpools/add/processmodel>
- Microsoft Learn, ASP.NET Core HTTPS/HSTS: `UseHttpsRedirection()` and `UseHsts()` are the documented ASP.NET Core mechanisms. <https://learn.microsoft.com/en-us/aspnet/core/security/enforcing-ssl>
- Microsoft Learn, `New-MigrationBatch -TargetDatabases`: accepts database identities such as name, DN, or GUID; multiple values let the migration service select one. <https://learn.microsoft.com/en-us/powershell/module/exchangepowershell/new-migrationbatch?view=exchange-ps#-target-databases>
- Microsoft Learn, `Get-MailboxDatabase` and `Get-DatabaseAvailabilityGroup`: on-premises cmdlets for retrieving mailbox database objects and DAG/member information. <https://learn.microsoft.com/en-us/powershell/module/exchange/get-mailboxdatabase?view=exchange-ps> and <https://learn.microsoft.com/en-us/powershell/module/exchange/get-databaseavailabilitygroup?view=exchange-ps>
- Microsoft Learn, ASP.NET Core forwarded headers: forwarded headers must be enabled and restricted to trusted proxies/networks. <https://learn.microsoft.com/en-us/aspnet/core/host-and-deploy/proxy-load-balancer>
- Microsoft Learn, Exchange migration batch counts and `MigrationBatch` API properties: current EAC columns are Total/Synced/Finalized/Failed and API properties include `TotalCount`, `SyncedCount`, `FinalizedCount`, and `FailedCount`. <https://learn.microsoft.com/en-us/exchange/mailbox-migration/manage-migration-batches> and <https://learn.microsoft.com/en-us/previous-versions/office/exchange-server-api/jj936443(v=exchg.150)>
