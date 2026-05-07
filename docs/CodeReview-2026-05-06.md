# ExchangeAdminWeb Code Review - 2026-05-06

## Scope Reviewed

Reviewed the current working tree for first-party source and operational assets:

- Application startup/configuration: `Program.cs`, `ExchangeAdminWeb.csproj`, `web.config`, `appsettings*.json`, launch settings.
- Authorization, middleware, models, and services under `Authorization/`, `Middleware/`, `Models/`, and `Services/`.
- Blazor components and pages under `Components/`, including the full `Migration.razor` and `ExchangeService.cs`.
- Tests under `ExchangeAdminWeb.Tests/`.
- First-party CSS, deployment scripts, example scripts, README/security/architecture docs, `.gitignore`, `.gitattributes`, and `.claude` local files.

Not manually reviewed as source: generated `bin/` and `obj/`, binary images/docx files, Word lock files, and vendored minified Bootstrap CSS.

Explicitly out of scope per project direction: ServiceNow integration behavior and ticket validation against ServiceNow. This review still covers local ticket capture/audit handling where it affects non-ServiceNow application behavior.

## Verification

Commands run:

- `dotnet build ExchangeAdminWeb.csproj --no-restore` - passed, 0 warnings, 0 errors.
- `dotnet test ExchangeAdminWeb.Tests\ExchangeAdminWeb.Tests.csproj --no-restore` - passed, 84 tests.
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` - passed.
- `dotnet list ExchangeAdminWeb.csproj package --vulnerable --include-transitive` - no vulnerable packages.
- `dotnet list ExchangeAdminWeb.Tests\ExchangeAdminWeb.Tests.csproj package --vulnerable --include-transitive` - no vulnerable packages.
- `dotnet list ExchangeAdminWeb.csproj package --deprecated` - no deprecated packages.
- `dotnet list ExchangeAdminWeb.Tests\ExchangeAdminWeb.Tests.csproj package --deprecated` - no deprecated packages.
- `dotnet list package --outdated` - app packages outdated: `Microsoft.AspNetCore.Authentication.Negotiate` 8.0.26 -> 10.0.7, `Microsoft.PowerShell.SDK` 7.4.15 -> 7.6.1, `Serilog.AspNetCore` 8.0.3 -> 10.0.0, `Serilog.Enrichers.Environment` 2.3.0 -> 3.0.1, `Serilog.Sinks.File` 5.0.0 -> 7.0.0.
- `dotnet list ExchangeAdminWeb.Tests\ExchangeAdminWeb.Tests.csproj package --outdated` - test packages outdated: `coverlet.collector` 6.0.4 -> 10.0.0, `Microsoft.NET.Test.Sdk` 17.14.1 -> 18.5.1.

## Findings

### High - Audit coverage does not match the documented "every operation" guarantee

The architecture doc says every operation, including read-only lookups and write actions, is recorded (`docs/Architecture Design Document.md:122`). The implementation has gaps:

- OOF protected-user validation failure returns without audit (`Components/Pages/OutOfOffice.razor:259`).
- Bulk migration eligibility sends an admin email but writes no audit log (`Components/Pages/Migration.razor:823`).
- Single migration eligibility exceptions are shown in the UI but are not audited or notified (`Components/Pages/Migration.razor:736`).
- Some notification failures are swallowed silently (`Components/Pages/MessageTrace.razor:183`, `Components/Pages/OutOfOffice.razor:284`, `Components/Pages/Migration.razor:1052`).

Impact: denied attempts, validation failures, and some lookup/migration events can be missing from the compliance trail. For an admin delegation tool, failed attempts are often as important as successful changes.

Recommendation: log an audit record at the boundary for every submitted action, including validation failures and caught exceptions. Treat email as a secondary notification channel, not the system of record.

### High - Deployment ACLs grant a hard-coded account that the script does not configure as the app pool identity

`deploy.ps1` creates or reconfigures the app pool and sets only runtime/load profile/start mode (`deploy.ps1:57`, `deploy.ps1:65`). It does not set the app pool identity to `ANALOG\SVC_SCRIPTADM`, but grants certificate private-key access and log-folder access to that hard-coded account (`deploy.ps1:127`, `deploy.ps1:147`). README describes ApplicationPoolIdentity as the default (`README.md:49`).

Impact: a fresh deployment can publish successfully but fail at runtime when Exchange certificate auth or audit logging needs private key/log-folder access. Changing `-AppPoolName` also does not change the ACL target.

Recommendation: either set the app pool identity explicitly and securely in the script, or grant ACLs to `IIS AppPool\<AppPoolName>` / a parameterized service account. Avoid hard-coded domain identities in reusable deployment automation.

### Medium - Authorization behavior and documentation disagree for empty `AllowedGroups`

Runtime behavior is fail-closed: startup warns that empty `AllowedGroups` denies all access (`Program.cs:26`), and the authorization handler fails the requirement when no groups are configured (`Authorization/GroupAuthorizationHandler.cs:40`). Documentation says the opposite in two places: empty list means all authenticated users are allowed (`NOTIFICATIONS_SECURITY.md:63`, `README.md:175`).

Impact: operators following the docs will misdiagnose access failures, and administrators could make the wrong security assumption during incident response or deployment.

Recommendation: update the docs to match fail-closed behavior. Keep fail-closed as the default.

### Medium - Identity matching is heuristic and can both over-block and under-block self-grant/protected-user checks

`PermissionValidator.IdentitiesMatch` compares extracted local names and dot-stripped names only (`Services/PermissionValidator.cs:73`). Tests intentionally treat `jsmith@analog.com` and `jsmith@other.com` as the same user (`ExchangeAdminWeb.Tests/IdentityMatchTests.cs:15`), while `DOMAIN\jsmith` and `john.smith@analog.com` do not match (`ExchangeAdminWeb.Tests/IdentityMatchTests.cs:14`).

Impact: an operator can be blocked from acting on a different person with the same local part in another domain, while a real self-grant can slip through if the Windows samAccountName does not match the email local part. The same risk applies to protected-user exclusions.

Recommendation: resolve entered identities through Exchange/AD to stable identifiers before enforcement, then compare object IDs/SIDs/ExternalDirectoryObjectIds rather than string variants.

### Medium - PowerShell operations have no cancellation, throttling, or concurrency guard

Exchange operations create a new runspace inside `Task.Run` per request (`Services/ExchangeService.cs:1922`) and many lookup/status paths do the same (`Services/ExchangeService.cs:811`, `Services/ExchangeService.cs:900`, `Services/ExchangeService.cs:1342`, `Services/ExchangeService.cs:1438`, `Services/ExchangeService.cs:1588`). There is no cancellation token, timeout, bounded queue, or semaphore.

Impact: slow Exchange Online/on-prem calls can tie up server threads and Blazor circuits. Multiple users or repeated clicks can create many concurrent PowerShell sessions, increasing the chance of EXO throttling and app pool resource exhaustion.

Recommendation: introduce a bounded Exchange operation executor with concurrency limits, timeout/cancellation support, and consistent logging around duration/failures.

### Medium - User-visible errors leak raw backend exception messages

Several pages surface `ex.Message` or PowerShell error text directly to the UI (`Components/Pages/Migration.razor:743`, `Components/Pages/RecipientLookup.razor:161`, `Components/Pages/MessageTrace.razor:187`, `Components/Pages/OutOfOffice.razor:310`). `ExchangeService.RunAsync` returns raw PowerShell errors as `PermissionResult.Message` (`Services/ExchangeService.cs:1952`).

Impact: users can see internal hostnames, certificate subject details, server paths, module errors, or secret-management failures. This is useful for admins but weakens separation between operator UI and operational internals.

Recommendation: return friendly, stable error codes/messages to the UI and keep full exception details in Serilog/audit logs available to maintainers.

### Medium - Notification delivery failures are not treated as control failures

`EmailService.SendEmailAsync` logs and swallows all SMTP failures (`Services/EmailService.cs:322`). Multiple callers also swallow email failures (`Components/Pages/MessageTrace.razor:183`, `Components/Pages/OutOfOffice.razor:295`, `Components/Pages/Migration.razor:1101`).

Impact: the UI can report success even when admin or end-user notifications did not send. If notifications are a security/compliance control, silent failure creates false assurance.

Recommendation: return a notification result to callers, audit notification failures, and show a non-blocking warning when an Exchange action succeeded but notification delivery failed.

### Medium - On-prem database selection can be very expensive

`GetOnPremDatabasesAsync` sorts mounted databases by executing `Get-Mailbox -ResultSize Unlimited` per database inside a remote script block (`Services/ExchangeService.cs:1827`).

Impact: in a large Exchange environment, creating a migration batch can trigger broad mailbox enumeration across every mounted database. That can be slow, increase load on on-prem Exchange, and delay UI responses.

Recommendation: use mailbox database statistics or a precomputed placement policy instead of enumerating all mailboxes per database on demand.

### Medium - Client IP capture can be stale or misleading

`ClientInfoMiddleware` records `RemoteIpAddress` only (`Middleware/ClientInfoMiddleware.cs:15`) and stores it in a static dictionary keyed by username (`Services/ClientInfoService.cs:8`). Cache entries expire only when read and are never removed.

Impact: behind IIS proxies/load balancers, the logged IP can be the proxy rather than the operator. Concurrent sessions from the same username can overwrite each other, and the static dictionary can grow for every distinct user over the process lifetime.

Recommendation: configure forwarded headers if applicable, store per-circuit/per-connection context rather than a static username cache, and evict expired entries.

### Medium - Message trace date validation allows an effective 11-day query

Validation permits exactly a 10-day difference (`Components/Pages/MessageTrace.razor:146`; test at `ExchangeAdminWeb.Tests/LookupValidationTests.cs:42`), but the query sends `endDate.AddDays(1)` to include the end date (`Components/Pages/MessageTrace.razor:170`).

Impact: selecting a 10-day UI range can send an 11-day backend range, which conflicts with the UI text and can trip Exchange Online trace limits.

Recommendation: validate the effective backend range, or change the UI rule to a 9-day difference when using inclusive end dates.

### Low - Target framework and README are out of sync

The app and tests target `net10.0-windows10.0.17763.0` (`ExchangeAdminWeb.csproj:4`, `ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj:4`), while README still describes the app and prerequisites as ASP.NET Core/.NET 8 (`README.md:3`, `README.md:31`, `README.md:327`).

Impact: new deployments can install the wrong hosting bundle/runtime.

Recommendation: update README prerequisites and install commands to .NET 10, or retarget the projects to the documented runtime if .NET 8 is intended.

### Low - Path base is hard-coded in both runtime and markup

The app hard-codes `/ExchangeAdminWeb` in middleware and markup (`Program.cs:67`, `Components/App.razor:7`). README local development says to use `http://localhost:5226` (`README.md:302`).

Impact: local root-hosted dev and alternate IIS aliases are brittle, and deployments under a different path require code edits.

Recommendation: move the path base/base href to configuration or document that all environments must use `/ExchangeAdminWeb`.

### Low - Documentation image links cannot currently resolve from tracked files

The architecture doc links several images under `docs/images/` (`docs/Architecture Design Document.md:51`, `docs/Architecture Design Document.md:225`), but `docs/images` is empty and `.gitignore` ignores all PNG files (`.gitignore:47`).

Impact: published architecture documentation will render broken diagrams/screenshots unless the images are supplied out of band.

Recommendation: allow `docs/images/*.png` in `.gitignore` or switch the doc to text-only diagrams until images are added.

## Positive Observations

- Build, tests, and format verification are clean.
- The core permission flows use PowerShell SDK parameter binding rather than string-built command execution.
- Audit CSV output and bulk report CSV output include formula-injection mitigation.
- Protected-user validation is intentionally fail-closed when expansion cannot be initialized.
- The test suite covers important pure logic: identity normalization, audit writing, CSV report generation, migration-user matching, date/schedule validation, and Exchange size parsing.

## Remediation Status (2026-05-07)

| Finding | Status | Notes |
|---------|--------|-------|
| Audit coverage gaps | **Remediated** | OOF denial, single/bulk eligibility exceptions audited; empty catch blocks replaced with `Logger.LogWarning` |
| Deploy ACL identity | **Remediated** | `deploy.ps1` now sets app pool identity explicitly via parameterized `$ServiceAccount` |
| AllowedGroups docs | **Remediated** | README and NOTIFICATIONS_SECURITY updated to reflect fail-closed behavior |
| Identity matching | **Remediated** | `IIdentityResolver` added; `ValidateSelfGrantAsync` resolves via `Get-Recipient`/`ExternalDirectoryObjectId`; falls back to string heuristic on failure |
| PowerShell concurrency | **Partially remediated** | `SemaphoreSlim(5,5)` for cloud, `(2,2)` for on-prem bounds concurrent sessions; 2-min timeout on semaphore acquisition. No per-operation CancellationToken or PowerShell timeout once running. |
| Raw error messages | **Not remediated** | Accepted risk — audience is IT support staff |
| Notification failure visibility | **Remediated** | All catch blocks now log warnings; notifications do not block operation success |
| On-prem DB enumeration | **Remediated** | Removed; uses configured `Migration:OnPremTargetDatabases` value |
| Client IP capture | **No change needed** | Verified functional via production audit CSVs; IIS proxy/forwarded-headers not applicable in current deployment |
| Message trace date range | **Remediated** | Validation threshold changed from `> 10` to `> 9` to account for inclusive end-date +1 |
| .NET version in README | **Remediated** | All references updated to .NET 10 |
| Path base hard-coding | **Remediated** | Configurable via `Application:PathBase` in appsettings.json |
| .gitignore blocking docs/images | **Remediated** | Added `!docs/images/*.png` exception |

### Remaining open items

- **PowerShell operation timeout/cancellation**: Once a runspace operation starts, there is no timeout or cancellation mechanism. A hung Exchange call will hold the semaphore slot until the 2-minute acquisition timeout starts blocking other requests. Adding `CancellationToken` support requires reworking the `Task.Run` + runspace pattern (PowerShell SDK does not natively support cooperative cancellation).
- **ServiceNow integration**: Registered and called from permission pages but functionally disabled via config. Out of scope per project direction but still appears as active code to reviewers.
