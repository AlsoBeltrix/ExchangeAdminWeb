# Service Health Plan

Status: Implemented
Owner: Michael
Last verified against code: 7ba6b25 (2026-09-08)

## 1. Goal  [YOU]

"create a new module that does what D:\source\servicehealthmonitor does. we'll
migrate the client secret into Secret Server when ready."

Scope answered by Michael when asked whether the module should also notify:
**Dashboard only.** A faithful port of what actually runs today - a read-only page
showing Microsoft 365 service status and open incidents, fetched on demand with a
short cache. Smallest surface, no background service, no new alerting path.

Audience, stated by Michael 2026-09-08: L1 and L2 support use this to get service
health status, and what they see is often forwarded to executives. It has to be
comprehensible at a glance. That sets the bar for the incident text - Microsoft's
own formatting is part of what makes an update readable, and flattening it is not
acceptable.

## 2. Non-goals  [YOU]

- No email, Teams-webhook or file-share alerting. The old `config.json` declares
  `Notifications` and `Settings` blocks, but nothing in `app.py` reads them - it is
  dead config describing intent that was never shipped. Not ported.
- No background/scheduled polling service. Data is fetched when an operator opens
  or refreshes the page.
- No incident history store, no `HistoryFile`, no retention policy.
- No `MonitoredServices` allow-list. The source app ignores it and shows every
  service the tenant returns.
- No write operations of any kind. The module never mutates anything.
- No reuse of the standalone app's DPAPI credential file, and no shelling out to
  PowerShell for a secret. The app registration is reused; the credential store is
  not. Credentials come from Secret Server only.
- The standalone Flask app is not decommissioned by this work. That is a separate
  owner decision after the module is live.

## 3. Acceptance criteria  [YOU approve; model proposed]

- AC1: An operator with the `ServiceHealth` permission can open `/service-health`
  and see every Microsoft 365 service the tenant reports, each with its current
  status and a friendly display name.
- AC2: The same page lists every currently unresolved service incident, newest
  first, showing at minimum title, affected service, classification, status and
  start time.
- AC3: Summary counts are shown: total services, healthy services, active
  incidents, and services currently degraded.
- AC4: An operator without the permission is sent to `access-denied`; the page
  never renders its data first.
- AC5: With no Delinea secret ID configured, the page loads and says the module is
  not configured. It does not render an empty dashboard that looks healthy.
- AC6: When the Graph call fails, the page shows an error. It never renders
  "0 incidents" or "all healthy" off a failed request.
- AC7: Repeat page loads inside the cache window do not re-hit Graph; a refresh
  action forces a fresh fetch.
- AC8: Every dashboard load writes an audit event naming the operator, their IP
  and the action.
- AC9: The client secret is read from a module-specific Delinea secret at call
  time. It is never in `appsettings.json`, module config, source, tests or logs.
- AC10: The module is disabled by default and appears in the sidebar under
  Infrastructure only when enabled and permitted.
- AC11: Expanding an incident shows its running update timeline - every post
  Microsoft has published against it, newest first, each with its timestamp and
  text. An incident with no posts yet says so rather than rendering an empty box.
- AC12: Incident text keeps Microsoft's formatting - paragraphs, line breaks, bold,
  lists and links render as formatting, not as literal tags and not as a single
  block of grey text. It is readable at a glance and fit to forward as-is.
- AC13: The rendered incident HTML is sanitized against an allow-list before it
  reaches the browser. Script, style, iframe, object, embed, event-handler
  attributes and non-http(s) URL schemes never survive into the page.

## 4. Failure behavior  [YOU own]

| Step / dependency | If it fails | The user sees | System state afterward |
|---|---|---|---|
| Delinea secret ID not set in Module Config | Fail closed before any network call | "Service Health is not configured. Set Graph App Delinea Secret ID in Module Config." | Nothing fetched, nothing cached |
| Delinea unreachable, or secret ID wrong / not visible to the SDK client | `InvalidOperationException` from the service | Error banner naming the secret ID and telling them to check Secret Server | Nothing cached; next load retries |
| Secret exists but a field (Tenant ID / Application ID / Client Secret) is blank | Fail closed | "Graph API credentials incomplete in Secret Server." | Nothing cached |
| Token request rejected (bad secret, expired secret, app disabled) | Graph call returns non-success | Error banner with the HTTP status | Nothing cached; no partial dashboard |
| `healthOverviews` call fails | Throw - never treated as "no services" | Error banner with the HTTP status | Nothing cached |
| `issues` call fails after services succeeded | Throw - never treated as "no incidents" | Error banner with the HTTP status | Nothing cached; the whole load fails rather than showing a false all-clear |
| Graph app missing `ServiceHealth.Read.All` consent | 403 from Graph | Error banner with the status; text points at the app registration | Nothing cached |
| Audit write fails | Logged separately; the page still renders | Nothing - the dashboard works | Data shown, audit gap logged to the app log |
| Sanitizer strips something Microsoft legitimately sent | Formatting or a link is lost from that post | Slightly plainer text; the substance survives | No security impact; allow-list widened if it recurs |
| A Microsoft post carries hostile or malformed markup | Allow-list drops it | Clean formatted text | Nothing executes in the browser; the module never trusts the payload |
| Cache holds stale data during an outage | Data up to 10 minutes old | Last-updated timestamp on the page; refresh forces a fetch | Cache replaced on successful refresh |

## 5. Rollback / blast radius  [YOU own]

- Blast radius is one new page and one new service. Nothing existing changes
  behavior. The module is `EnabledByDefault = false`, so until an admin enables it
  and grants the permission, no operator sees it at all.
- The module performs zero writes - to Microsoft 365, to AD, or to any local store
  other than the audit log. There is no state to unwind.
- Rollback is a git revert of the slice plus a redeploy. Because the config
  database is shared, a leftover `ServiceHealth` config row would remain after a
  revert; it is inert (no code reads it) and harmless.
- The Graph app registration is the standalone app's existing one, reused. It
  carries only `ServiceHealth.Read.All`, which cannot read mail, files, users or
  directory objects. Compromise of that secret exposes tenant service-status data
  only.
- While the Flask app is still running it keeps reading its own DPAPI credential
  file. Rotating the client secret in Secret Server breaks the Flask app until
  that file is rewritten. Accepted: the Flask app is scheduled for retirement
  after the module is live.
- One new third-party dependency, the `HtmlSanitizer` package 9.2.1039 (namespace `Ganss.Xss`). It is a parsing/sanitizing library
  with no network or filesystem access, used only on strings this module already
  received from Graph. Reverting the module reverts the reference.
- No base app version bump (Constitution, Deployment And Versioning: adding a new
  module is not a shared-infrastructure change). Only the new module's own
  `Version = "1.0.0"` is set.

## 6. Design sketch  [MODEL]

### Runtime moving parts, and what each one can break

1. **`Modules/ModuleCatalog.cs` entry** - declares the module. Break it and the
   page has no policy, no sidebar entry, or a duplicate route; the catalog
   tripwire tests catch all three.
2. **`Services/ServiceHealthService.cs`** (new, singleton) - owns the Delinea
   lookup, the two Graph calls and the cache. This is where a failure turns into
   either an honest error or a dangerous false all-clear.
3. **`Components/Pages/ServiceHealth.razor`** (new) - authorization gate, layout,
   refresh, audit call. Break the gate and an unpermitted operator sees data.
4. **The Graph app registration** (owner-side, outside this repo) - the standalone
   app's existing registration, already consented for `ServiceHealth.Read.All`.
   Nothing else in ExchangeAdminWeb uses it, so this is not cross-module credential
   reuse: the module replaces the Flask app rather than joining it. What is new is
   the Delinea record holding that app's `Tenant ID` / `Application ID` /
   `Client Secret`; today the secret lives in a DPAPI file on ASHBEXUTIL1. A
   missing or revoked consent is a 403 the page reports.

### Descriptor (goes in `ModuleCatalog.RegisterAll()`)

```csharp
new()
{
    Id = "ServiceHealth",
    DisplayName = "Service Health",
    Description = "Microsoft 365 service status and open service incidents, read from the Microsoft Graph service announcements API.",
    Route = "service-health",
    IconCss = "bi bi-gear-fill-nav-menu",
    Category = "Infrastructure",
    SortOrder = 830,
    EnabledByDefault = false,
    IsSystemModule = false,
    Version = "1.0.0",
    MainPermission = new(
        "Access",
        "ServiceHealth",
        "Open the module and view current Microsoft 365 service status and the tenant's open service incidents. Read-only - grants no ability to change anything.",
        FailClosed: true),
    ConfigFields = [
        new("GraphDelineaSecretId", "Graph App Delinea Secret ID", "Secret Server secret containing Tenant ID, Application ID, and Client Secret fields (requires ServiceHealth.Read.All)")
    ]
},
```

Placement rationale: `Infrastructure` currently ends at `IntuneDevices` SortOrder
820 (`Modules/ModuleCatalog.cs`); 830 is the next free slot. `IconCss` reuses
`bi bi-gear-fill-nav-menu`, already used by `DhcpAuthorization`,
`BitLockerRecovery` and `IntuneDevices`, so it is proven present in host CSS. No
`DependsOn` - this is Graph-backed, not Exchange-backed.

### Reference module

`Services/NamedLocationsService.cs` and `Components/Pages/NamedLocations.razor` are
the pattern being followed: same `GraphDelineaSecretId` config field name, same
`GetGraphClientAsync()` construction, same `IsAvailable` property, same
first-page-failure rule (`NamedLocationsService.cs:61-64` - a failed first page
throws rather than rendering as "none exist"), same page auth gate
(`NamedLocations.razor:239-258`).

### Service shape

```
GetGraphClientAsync()   -> identical to NamedLocationsService.cs:20-38, keyed on "ServiceHealth"
IsAvailable             -> identical to NamedLocationsService.cs:40-47
GetStatusAsync(bool forceRefresh)
    - lock-guarded 600-second cache (matches CACHE_TTL_SECONDS in the source app)
    - GET /admin/serviceAnnouncement/healthOverviews
    - GET /admin/serviceAnnouncement/issues?$filter=isResolved eq false
    - either call failing on its first page throws
    - decorate services with friendly display names (the source app's map, ported verbatim)
    - map each issue's service name back to a service id (the source app's second map, ported verbatim)
    - sort issues by startDateTime descending
    - returns ServiceHealthStatus { Services, Issues, LastUpdated, TotalServices, ActiveIssues }
```

The two hardcoded name maps are ported as written in
`D:\source\servicehealthmonitor\Production\app.py`. They are cosmetic: an
unmapped service falls back to its raw Graph id rather than being hidden.

The source app's one-shot 401-retry loop is not ported. `GraphTokenClient` already
owns token acquisition and lifetime, so re-implementing a retry against a token
this module does not manage would duplicate host behavior.

Incident **posts** (the update timeline) come back inline on the list call - no
per-issue fan-out. Evidence: `app.py:277` logs `has posts:` directly off the
`/issues` response, and `templates/dashboard.html:270-273` renders `issue.posts`
from that same payload. The `$expand=posts` comment at `app.py:250` means expand is
not supported on v1.0, not that posts are missing. The separate
`GET /issues/{id}/posts` call in `Get-ServiceHealthIssueDetails.ps1` is one way to
fetch them, not the only one.

Each post carries `createdDateTime`, `postType` and a `description` object of
`{ contentType, content }`. **The content is HTML from Microsoft**, and it is
rendered as formatted HTML - the audience requirement in section 1 rules out
flattening it to text. Posts are shown newest first alongside each incident's
`impactDescription`, which is HTML on the same terms.

This is the module's one genuinely risky surface, so it is handled explicitly:

- **Sanitize server-side, allow-list only.** Add the `HtmlSanitizer` 9.2.1039
  package (the maintained successor to the OWASP-derived .NET sanitizer). Hand-
  rolling an HTML sanitizer is the classic way to ship an XSS hole; the
  Constitution's "prefer established local patterns" applies doubly to security
  primitives. Owner ruling 2026-09-08: try the package first; if it proves
  unsuitable, fall back to a hand-written allow-list sanitizer with the same AC13
  vector tests as the gate.
- **Allowed:** `p br b strong i em u ul ol li a span div h1-h6 table thead tbody tr
  th td code pre blockquote`. Allowed attributes: `href title colspan rowspan`.
  Allowed URL schemes: `http`, `https`. Everything else - including every `on*`
  handler, `style`, `script`, `iframe`, `object`, `embed`, and `javascript:` /
  `data:` URLs - is stripped.
- **Links get `target="_blank" rel="noopener noreferrer"`** added post-sanitize, so
  a Microsoft KB link does not navigate the admin console away.
- **`MarkupString` is used only on sanitizer output**, never on a raw Graph string.
  Today `MarkupString` appears in this repo only against app-authored literals
  (`AdminSettings.razor:751-754`, `Migration.razor:1800-1804`); this module is the
  first to render third-party content, which is why the boundary is written down
  here rather than left to the reader.
- Sanitizing happens in the service, not the page, so the page cannot be edited
  later into rendering an unsanitized field.

### Notifications

None. Constitution, Notifications: a read module is non-alerting unless it is
purpose-built for security response; this is a status dashboard, and audit is
sufficient. This matches the RiskyUsers precedent (`.agents/decisions.md`,
2026-06-30: reads are audited, never alert-emailed).

### Protected principals / ticket number

Neither applies. The module mutates nothing, so there is no write target to gate
and no change to justify with a ticket.

### DI

`builder.Services.AddSingleton<ServiceHealthService>()` in `Program.cs` - singleton
so the cache is process-wide, matching the source app's module-level cache.

## 7. Task breakdown  [MODEL]

1. Catalog tests first, in `ExchangeAdminWeb.Tests/`: module count increases, route
   `service-health` unique, page route exists, page policy matches the descriptor's
   main permission, config field declared. (AC1, AC4, AC10)
2. Add the descriptor to `ModuleCatalog.RegisterAll()`. (AC1, AC10)
3. Add the `HtmlSanitizer` package reference and configure the allow-list sanitizer.
   (AC12, AC13)
4. `Services/ServiceHealthService.cs`: Delinea/Graph client construction,
   `IsAvailable`, the two Graph calls, post/timeline parsing, HTML sanitizing, name
   maps, cache, models. (AC1, AC2, AC3, AC5, AC6, AC7, AC9, AC11, AC12, AC13)
5. Register the service in `Program.cs`. (AC1)
6. `Components/Pages/ServiceHealth.razor`: auth gate, not-configured banner, error
   banner, summary cards, service grid, expandable incident list with its formatted
   update timeline, refresh, audit call, and the required `<ModuleVersion />` in the
   `<h1>`. (AC1, AC2, AC3, AC4, AC5, AC6, AC7, AC8, AC11, AC12)
7. Service unit tests in `ExchangeAdminWeb.Tests/`, including the sanitizer vector
   cases. (AC5, AC6, AC7, AC11, AC12, AC13)
8. Gates: `dotnet build ExchangeAdminWeb.slnx -c Release`,
   `dotnet test ExchangeAdminWeb.slnx`,
   `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`,
   `git diff --check HEAD`.
9. Paperwork in the same motion: `.agents/state.md`, `.agents/token-log.md`, plan
   sections 9 and 10, plan Status to Implemented.

## 8. Test plan  [MODEL writes; YOU check the mapping]

| AC | Test |
|---|---|
| AC1 | Catalog test: `ServiceHealth` present, route `service-health`, module count increased. Service test: a stubbed `healthOverviews` payload yields the expected service list with display names applied. |
| AC2 | Service test: a stubbed `issues` payload yields unresolved incidents sorted by `startDateTime` descending. |
| AC3 | Service test: summary counts computed correctly from a mixed healthy/degraded payload. |
| AC4 | Catalog test: page policy matches the descriptor main policy alias, and the descriptor's main permission is `FailClosed: true`. Manual: unpermitted operator is redirected to `access-denied`. |
| AC5 | Service test: `IsAvailable` false when the config value is missing, blank, non-numeric or `<= 0`; `GetStatusAsync` throws rather than returning an empty result. |
| AC6 | Service test: a non-success status on `healthOverviews` throws; a non-success status on `issues` throws. Neither returns an empty collection. |
| AC7 | Service test: two calls inside the TTL make one Graph round trip; `forceRefresh: true` makes a second. |
| AC8 | Manual: open the page, confirm the audit entry with actor, IP and action. |
| AC9 | Grep gate: no tenant id, client id or secret literal in the diff. Service test: the secret is read through `DelineaService`, not from configuration. |
| AC10 | Catalog test: `EnabledByDefault == false`, `Category == "Infrastructure"`. |
| AC11 | Service test: a stubbed issue carrying posts yields them parsed and ordered newest first; an issue with no `posts` property yields an empty timeline rather than throwing. |
| AC12 | Service test: sanitized output of a representative Microsoft post keeps `<p>`, `<br>`, `<strong>`, `<ul>/<li>` and a `https://` link. Manual: expand a live incident and confirm it reads like the Microsoft status page, not like a wall of text. |
| AC13 | Service test, one case per vector: `<script>`, `onerror=`/`onclick=`, `<iframe>`, `<object>`, `<embed>`, `style=`, `javascript:` href and `data:` href are all absent from the sanitized output. Test that an `<a href="https://...">` survives with `rel="noopener noreferrer"` and `target="_blank"`. |

Every AC in section 3 appears above.

## 9. Traceability check  [MODEL fills when iteration ends]

Implemented 2026-09-08. Where each AC landed:

| AC | Landed in | Covered by |
|---|---|---|
| AC1 | `Modules/ModuleCatalog.cs` (descriptor), `Services/ServiceHealthService.cs` (`FetchServicesAsync`, `ServiceDisplayNames`) | `ModuleCatalogTests.Catalog_HasServiceHealthModule`, `ServiceHealthServiceTests.GetStatus_MapsKnownServiceIdsToFriendlyNames`, `..._UnknownServiceIdFallsBackToGraphNameThenId` |
| AC2 | `ServiceHealthService.FetchIssuesAsync` (unresolved filter, descending sort) | `..._RequestsOnlyUnresolvedIssues`, `..._SortsIssuesNewestFirst` |
| AC3 | `ServiceHealthStatus` computed counts | `..._CountsSummariseServicesAndIssues`, `..._OnlyServiceOperationalCountsAsHealthy` |
| AC4 | `Components/Pages/ServiceHealth.razor` `@attribute [Authorize]` + `OnInitializedAsync` redirect | `ModuleCatalogTests.Catalog_ServiceHealth_MainPermissionIsFailClosed`, `ServiceHealthPageTests.Page_IsGatedByTheCatalogPolicyAndRedirectsWhenDenied`. Redirect itself is manual - NOT RUN. |
| AC5 | `ServiceHealthService.IsAvailable`, not-configured banner on the page | `..._IsAvailable_IsFalseWithoutAConfiguredSecretId`, `ServiceHealthPageTests.Page_ShowsTheNotConfiguredBannerRatherThanAnEmptyBoard` |
| AC6 | Both fetch methods throw on a non-success status; the page renders an error alert instead of a board | `..._ThrowsWhenHealthOverviewsFails`, `..._ThrowsWhenIssuesFails`, `..._FailedFetchIsNotCachedAsSuccess` |
| AC7 | `GetStatusAsync` TTL + `SemaphoreSlim` double-check | `..._SecondCallInsideTtlDoesNotHitGraph`, `..._ForceRefreshBypassesTheCache` |
| AC8 | `LoadAsync` audits both paths via `AuditService.LogLookupAction` | `ServiceHealthPageTests.Page_AuditsBothTheSuccessAndFailurePaths`. Live audit entry is manual - NOT RUN. |
| AC9 | `GetGraphClientAsync` reads the secret from `DelineaService` at call time | No credential literal in the diff (checked); no secret is logged. Delinea record does not exist yet - see below. |
| AC10 | Descriptor `EnabledByDefault = false`, `Category = "Infrastructure"` | `ModuleCatalogTests.Catalog_HasServiceHealthModule` |
| AC11 | `ParseIncident` post parsing and descending sort; expandable timeline on the page | `..._ReadsPostTimelineFromTheIssuesResponse`, `..._IssueWithoutPostsHasEmptyTimeline`, `..._ParseIncident_ToleratesAMissingDescriptionObject` |
| AC12 | `HtmlSanitizer` allow-list keeps Microsoft's formatting; page renders via `MarkupString` | `..._Sanitize_PreservesMicrosoftFormatting`, `..._Sanitize_AddsSafeTargetAndRelToLinks`. "Reads like the Microsoft status page" is manual - NOT RUN. |
| AC13 | `ServiceHealthService.Sanitize`, called in the service so no raw field can reach the page | `..._Sanitize_StripsHostileMarkup` (12 vectors), `..._GetStatus_SanitizesBothImpactDescriptionAndPosts`, `ServiceHealthPageTests.Page_MarkupStringIsOnlyEverUsedOnSanitizedFields` |

Deviations from the plan as written:

- The package id is `HtmlSanitizer` (9.2.1039); `Ganss.Xss` is its namespace. Section 5,
  6 and 7 corrected. The package was suitable, so the hand-rolled fallback the owner
  authorised was not needed.
- Section 7 ordered the catalog tests first; in practice the service was written first
  and the catalog tests immediately after. No scope change.
- `ServiceHealthPageTests` was added beyond the section 7 list. Source-text guards only,
  no bUnit; it exists to pin the `MarkupString` boundary so a later edit cannot render an
  unsanitized field.

Non-vacuity proof (repo rule): `Sanitize` was reverted to a pass-through, 14 of the 40
service tests failed, the sanitizer was restored (and the file touched, to defeat the
MSBuild timestamp skip), and all 51 pass again.

Verification run: `dotnet build ExchangeAdminWeb.slnx -c Release` succeeded; full
`dotnet test ExchangeAdminWeb.slnx` green; `dotnet format --verify-no-changes` clean;
`git diff --check HEAD` clean; no non-ASCII in any touched `.cs`.

Still required before the module can be switched on in an environment:

1. The Delinea record holding Tenant ID / Application ID / Client Secret for the existing
   app registration `e4fa5e51-d226-4a02-9a8b-d8de27133cdb` (tenant
   `eaa689b4-8f87-40e0-9c6f-7228de4d754a`), then its Secret ID entered in Module Config.
2. Enable the module and grant the `ServiceHealth` section access group.

## 10. Review log  [MODEL appends each round]

**2026-09-08 - implementation, self-reported. No independent review obtained.**

Landed in one slice (new module, so one commit). Files: `Services/ServiceHealthService.cs`,
`Components/Pages/ServiceHealth.razor`, `ExchangeAdminWeb.Tests/ServiceHealthServiceTests.cs`,
`ExchangeAdminWeb.Tests/ServiceHealthPageTests.cs` (new); `Modules/ModuleCatalog.cs`,
`Program.cs`, `ExchangeAdminWeb.csproj`, `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs`
(modified).

Gates: Release build 0 errors; full `dotnet test ExchangeAdminWeb.slnx` 2440 passed, 0
failed, 3 skipped (from 2389); `dotnet format --verify-no-changes` clean;
`git diff --check HEAD` clean; no non-ASCII in any touched `.cs`.

Non-vacuity: replacing `Sanitizer.Sanitize(html)` with `html` failed 14 of the 40 service
tests then in the file; restored (and the file touched, to defeat the MSBuild timestamp
skip) and the suite returned green.

Known limits of this coverage, stated rather than implied:
- `ServiceHealthPageTests` are source-text tripwires, not behaviour. There is no bUnit
  harness in this repo, so nothing here renders the page or observes a branch. The
  `MarkupString` guard is the one that matters and it reads text, not output.
- No test exercises the real Delinea or Graph path; `GetGraphClientAsync` is only reached
  through the DI constructor, which no test uses.
- The 12 hostile-markup vectors are a sample, not a proof. The security property rests on
  `HtmlSanitizer`'s allow-list, which is why the allow-lists are cleared before being
  populated rather than added to the package defaults.
