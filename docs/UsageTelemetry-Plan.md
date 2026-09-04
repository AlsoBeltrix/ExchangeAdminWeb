# Anonymous usage telemetry

Status: Draft 2026-09-04, awaiting codex openreview, then an owner go per slice. The
one owner decision (D1, event rows) is RULED (`.agents/decisions.md` 2026-09-04); no
owner decision is outstanding.
Owner: Michael
Last verified against code: `2938db7` / 2026-09-04
Versions: base app `2.18.0` -> `2.19.0` in S1 (new table, migrator step, shared
service, layout component, audit hook); `AdminEventLog` `1.1.0` -> `1.2.0` in S5
(Usage view + kill-switch config field).
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

Owner request 2026-09-04: *"is it possible to add some telemetry to this so I can see
how people are using it? the event log shows actual changes made, but things like
theme, modules opened and not used, etc. would be useful. it can be anonymous and
lightweight."* Ruling on the offered fork (daily counters vs anonymous event rows):
*"event rows"*.

The Constitution requires a written plan: persistence/storage change (new table and
migration step) and a change to the audit path (a hook in `AuditService`).

## 1. Owner decisions

**D1 (RULED 2026-09-04): anonymous event rows with a throwaway session id.** Each row
records what happened and in which module, never who. A random session id is minted
when the browser circuit opens and dies with it, so visits can be analysed
("opened three modules, acted in one"). Long sequences could in theory hint at who was
working when; the owner accepted that. Canonical text in `.agents/decisions.md`.

Settled by the ruling, existing code, or repo policy - not open:

- **No identity fields exist in the table.** Not user, not IP, not target, not ticket.
  The audit log (`AuditService`, JSONL files) stays the record of who did what. A code
  review of this plan should confirm no column or `detail` value can carry an identity.
- **Fire-and-forget.** A telemetry write runs off the caller's path (`Task.Run`), is
  wrapped in try/catch, logs its own failure, and can never change, delay, or mask the
  operation that triggered it (Constitution, Auditing: audit failure must not mask a
  completed operation - the same rule, one layer down).
- **No hosted service.** Retention runs in the one-shot startup pass, the owner's
  standing ruling for retention work (`Program.cs:265-281`, 2026-08-04;
  `Services/Jobs/BulkJobService.cs:9`).
- **Retention 90 days**, a code constant like `MessageTraceExportStore.RetentionDays`
  (`Services/MessageTraceExportStore.cs:38`), not a config key.
- **Kill switch:** one Boolean Module Config field on `AdminEventLog`,
  `UsageTelemetryEnabled`, default true (`ConfigFieldType.Boolean`, renders as a
  checkbox per `docs/BooleanConfigControls-Plan.md`). Off means no rows are written; the
  Usage view still shows what exists.
- **Action rows carry no session id.** Actions are captured at the one choke point every
  module already passes through, `AuditService.LogModuleAction` (a singleton, called
  from 27 modules' pages). A per-circuit id is not reachable from a singleton without
  threading a new parameter through every page. Declined as disproportionate: the
  question "opened but not used" is answered per module and per day by opens vs
  actions, and per visit by the session's open sequence. If the per-visit join ever
  matters, that is a later plan touching every page.
- **The `detail` column is an enum-like short string** (a route, a theme id, an
  action name), never free text from the operator.
- **The Home page notice states that telemetry is active (owner, 2026-09-04: "that
  notice will need to clearly state that telemetry is active ... whatever the industry
  standard is").** The existing "Important Notice" (`Components/Pages/Home.razor:34-43`)
  gains a third bullet, rendered only while the kill switch is on so the notice is never
  false: **"Anonymous usage data is collected:** which pages you open, which actions you
  run, and the theme you use are recorded to improve the portal. This data contains no
  username, IP address, or details of the items you act on, and is kept for 90 days."
  The disclosure names what, why, what is excluded, and how long - the four elements a
  usage-analytics notice conventionally carries.

## 2. Non-goals

- Anything a browser does that the server does not see (scroll, hover, time on page).
- Per-user or per-IP reporting, opt-in/opt-out per user, or a privacy notice: the
  store holds no identity, so there is nothing to opt out of.
- Exporting telemetry, charts, or a dashboard beyond one table view; a CSV can follow
  the `CsvExport` pattern later if wanted.
- Recording targets, tickets, outcomes' text, error messages, or search terms.
- Telemetry on the JSONL audit files, on operation traces, or on the jobs database.
- A background service or timer of any kind.
- Changing `AuditService`'s own write, files, or event shape.

## 3. Acceptance criteria

- AC1: Migration step v7 creates `usage_event` (`id INTEGER PRIMARY KEY`, `ts TEXT NOT
  NULL` ISO-8601 UTC, `session TEXT COLLATE NOCASE` nullable, `kind TEXT NOT NULL
  COLLATE NOCASE`, `module TEXT COLLATE NOCASE` nullable, `detail TEXT` nullable,
  `success INTEGER` nullable) plus indexes on `ts` and `(module, kind)`. The table has
  no user, ip, target or ticket column (a tripwire test asserts the column list).
- AC2: `UsageEventRepository` (pattern `AppSettingRepository`) offers `Insert(UsageEvent)`,
  `PruneOlderThan(DateTime cutoffUtc)` returning the deleted count, and three
  aggregates over a UTC range: `ModuleSummary(from, to)` -> per module `{ Opens,
  Actions, FailedActions, DistinctSessions }`; `ThemeSummary(from, to)` -> per theme id
  the count of distinct sessions whose latest `theme` row in range has that id;
  `SessionSummary(from, to)` -> `{ Sessions, AvgModulesPerSession, SessionsWithNoAction }`
  where a session "acted" if any `action` row for a module it opened exists within the
  session's open window (first to last open + 30 minutes; the per-module, per-day
  approximation the settled note above accepts).
- AC3: `UsageTelemetryService` (singleton) exposes `RecordOpen(session, module,
  route)`, `RecordTheme(session, themeId)`, `RecordSessionStart(session)`,
  `RecordAction(module, action, success)`. Each checks the kill switch, then queues one
  `Insert` on `Task.Run`, catches every exception and logs a warning. No method
  returns a Task the caller must await; none throws.
- AC4: `UsageSession` is `AddScoped`; its `Id` is `Guid.NewGuid().ToString("N")`
  minted at construction. It holds nothing else. It is never written anywhere but the
  `session` column.
- AC5: `AuditService.LogModuleAction` calls `RecordAction(category, action, success)`
  after `WriteAuditEvent` and outside its own try flow so a telemetry fault can never
  reach the audit path; the constructor takes `UsageTelemetryService? usage = null` so
  every existing construction compiles and a null sink is a no-op.
- AC6: A `UsageTracker` component (`@rendermode InteractiveServer`, rendered once in
  `MainLayout.razor` beside `ThemePicker`) records `session` once per circuit, records
  `open` for the current route on first render and on every `NavigationManager.
  LocationChanged`, mapping route -> module id through `ModuleCatalog.GetByRoute`
  after the same route derivation `ModuleVersion.razor:18-22` uses; a route with no
  module records `open` with `module = null` and `detail = route` only for the
  well-known non-module routes (`""` home, `access-denied`), and nothing otherwise.
  It reads the theme once via `JS.InvokeAsync<string>("getTheme")` in
  `OnAfterRenderAsync(firstRender)` (the `ThemePicker.razor:28-45` shape, since
  localStorage is unreachable during prerender) and records `theme`. It renders no
  markup and unsubscribes in `Dispose`.
- AC7: `ThemePicker` records `theme` on every change, through the same service.
- AC8: The startup block in `Program.cs` calls `PruneOlderThan(UtcNow - 90 days)` once,
  after `migrator.Migrate()`, logging the count; a failure logs and does not stop
  startup (the `MessageTraceExportStore.PruneExpired` posture).
- AC9: `AdminEventLog.razor` gains an "Events | Usage" toggle (C# state, no Bootstrap
  JS). The Usage view reuses the page's date range and shows: a module table (Module
  display name, Opens, Actions, Failed, Sessions, Actions per open), a theme table
  (Theme name via `UiThemeCatalog.Resolve`, Sessions), and a one-line session summary.
  Modules with zero rows in range are listed with zeros (from `ModuleCatalog.GetAll()`)
  so "never opened" is visible. Same `EventLog` policy gates it; no new permission.
- AC10: `UsageTelemetryEnabled` off -> no `Insert` is reached (behavioral test on the
  service with a fake repository); the Usage view still renders existing rows.
- AC11: Versions per the header; `ModuleCatalogTests` alias/permission counts
  untouched (a config field is not a permission).
- AC12: README gains a "Usage telemetry" paragraph under the Event Log section stating
  exactly what is and is not recorded.
- AC13: `Home.razor`'s Important Notice renders the "Anonymous usage data is collected"
  bullet (wording in section 1) if and only if `UsageTelemetryService.Enabled()` is
  true, so the notice and the behaviour cannot disagree. The retention figure in the
  bullet is read from `UsageTelemetryService.RetentionDays`, not typed twice.

## 4. Failure behavior

| Step / dependency | If it fails | The operator sees | System state afterward |
|---|---|---|---|
| Migration v7 at startup | Startup fails fast, as every migration does today (`Program.cs:218-230`) | App does not start; Serilog names the step | DB unchanged (each step is its own transaction) |
| Telemetry insert (any kind) | Caught inside the `Task.Run`, warning logged | Nothing | One row missing; the triggering operation unaffected |
| `RecordAction` throws synchronously (should be impossible - guarded) | AC5 places the call after `WriteAuditEvent`; a throw would surface to the page like any audit exception does today | Existing page error handling | Audit row already written |
| Kill switch config unreadable | `ModuleConfigService` fail-closed semantics apply: treat as disabled, log once | Nothing | No rows written until config reads again |
| `getTheme` JS interop fails (prerender, disconnected) | Caught; no `theme` row | Nothing | Theme unknown for that session |
| Route has no module | Only home / access-denied are recorded (module null); anything else ignored | Nothing | No stray rows |
| Prune at startup fails | Logged, startup continues | Nothing | Old rows linger until the next start |
| Usage view query fails | Error alert in the view (the page's existing alert shape) | Alert | Read-only, nothing changes |
| **Rollback after v7 applied** | `ConfigStoreMigrator.Migrate` THROWS when `user_version` exceeds the build's `Migrations.Length` (`ConfigStoreMigrator.cs:163-169`), so a build that removes the v7 step will not start | App down until fixed | See section 5 - the migration step must survive any revert, or the DB must be restored from the pre-deploy backup |

## 5. Rollback / blast radius

Revert S2-S6 freely. **Do not revert the S1 migration step** once any deploy has run
it: the migrator refuses a database newer than the build, so the reverted app would
fail to start. Two safe rollbacks: (a) revert everything except the v7 array element
(an unused table is harmless), or (b) restore `config/exchangeadmin.db` from the
verified backup `deploy-pipeline.ps1` takes before every deploy (and lose config
changes made since). Say which in the revert commit.

Blast radius otherwise: one new table nobody else reads, one optional constructor
parameter on `AuditService` (null-safe), one invisible layout component, one config
field, one view on the Event Log page. No authorization decision changes. The
telemetry sink is null in every existing test, so the audit suites run unchanged.

## 6. Design sketch

### Current code (read at `2938db7` via survey; spot-verify at implementation)

- Store: `Services/Storage/IConfigStore.cs:20-40` (`Read`, `Write` with transaction),
  `SqliteConfigStore.cs`, migrator `ConfigStoreMigrator.cs:23-142` (ordered `string[]
  Migrations`, append = new version; last step v6 at `:139-141`), pattern repository
  `AppSettingRepository.cs:13-67`, registrations `Program.cs:50-60`, test helper
  `ExchangeAdminWeb.Tests/TestConfigStore.cs` (`Create(dir)` migrates a temp DB).
- Theme: browser localStorage key `theme`; JS `getTheme()`/`setTheme(id)`
  (`Components/App.razor:90-100`); `UiThemeCatalog.Resolve` (`Services/UiTheme.cs:97`);
  `ThemePicker.razor:28-52`; nothing server-side knows the theme today.
- Event Log page: no tabs; filter row `:34-94`, date range `startDate`/`endDate`
  (`:350-351`), `LoadEvents` reads JSONL files (`:397-531`); catalog entry
  `ModuleCatalog.cs:849-871`, `Version = "1.1.0"` at `:860`, policy `EventLog`.
- Navigation: `LocationChanged` is used nowhere; `MainLayout.razor` has no
  `@rendermode` (renders `ThemePicker`, which is interactive, at `:12`);
  `ModuleCatalog.GetByRoute` (`:34`); route derivation `ModuleVersion.razor:18-22`.
- Circuit: `ClientInfoService` is `AddScoped` (`Program.cs:203`) and populated by
  `ClientInfoCircuitHandler` (`:207`) - the per-circuit lifetime `UsageSession` uses.
- Audit: `AuditService.LogModuleAction` (`Services/AuditService.cs:191-217`) writes
  JSONL via `WriteAuditEvent`; `category` is the caller-supplied module-ish string;
  registered `AddSingleton` (`Program.cs:183`).
- Retention precedent: one-shot startup block `Program.cs:265-291`.

### New: migration v7 (append to `Migrations`)

```
CREATE TABLE usage_event (
    id      INTEGER PRIMARY KEY,
    ts      TEXT    NOT NULL,
    session TEXT    COLLATE NOCASE,
    kind    TEXT    NOT NULL COLLATE NOCASE,
    module  TEXT    COLLATE NOCASE,
    detail  TEXT,
    success INTEGER
);
CREATE INDEX ix_usage_event_ts ON usage_event(ts);
CREATE INDEX ix_usage_event_module_kind ON usage_event(module, kind);
```

Kinds: `session` (detail null), `open` (detail = route), `theme` (detail = theme id),
`action` (detail = action name, success 0/1, session null).

### New: `Services/Storage/UsageEventRepository.cs`

```
public sealed record UsageEvent(DateTime TsUtc, string? Session, string Kind, string? Module, string? Detail, bool? Success);
public sealed record ModuleUsage(string Module, int Opens, int Actions, int FailedActions, int DistinctSessions);
public sealed record ThemeUsage(string ThemeId, int Sessions);
public sealed record SessionUsage(int Sessions, double AvgModulesPerSession, int SessionsWithNoAction);

public sealed class UsageEventRepository(IConfigStore store)
{
    public void Insert(UsageEvent e);
    public int PruneOlderThan(DateTime cutoffUtc);
    public IReadOnlyList<ModuleUsage> ModuleSummary(DateTime fromUtc, DateTime toUtc);
    public IReadOnlyList<ThemeUsage> ThemeSummary(DateTime fromUtc, DateTime toUtc);
    public SessionUsage SessionSummary(DateTime fromUtc, DateTime toUtc);
}
```

All SQL parameterised; `ts` stored as `DateTime.UtcNow.ToString("O")` and compared
as text (ISO-8601 sorts lexically). Registered `AddSingleton` beside the other
repositories. `TestConfigStore.CreateUsageEvents(dir)` added.

### New: `Services/UsageSession.cs` and `Services/UsageTelemetryService.cs`

```
public sealed class UsageSession { public string Id { get; } = Guid.NewGuid().ToString("N"); }   // AddScoped

public sealed class UsageTelemetryService(UsageEventRepository repo, ModuleConfigService config, ILogger<UsageTelemetryService> log)
{
    internal const string ConfigModuleId = "AdminEventLog";
    internal const string ConfigKey = "UsageTelemetryEnabled";
    internal bool Enabled();                      // reads the Boolean field; unreadable -> false, logged once
    public void RecordSessionStart(string session);
    public void RecordOpen(string session, string? module, string? route);
    public void RecordTheme(string session, string themeId);
    public void RecordAction(string? module, string action, bool success);
    internal virtual void Enqueue(UsageEvent e);  // Task.Run + try/catch + LogWarning; TEST SEAM
}
```

`Enabled()` reads the field the way `BitLockerRecoveryService` reads `ValidateTickets`
(the `ITicketValidator` plan's switch) - copy that call shape at implementation.

### Change: `AuditService`

Constructor gains `UsageTelemetryService? usage = null`; `LogModuleAction` ends with
`_usage?.RecordAction(category, action, success);` after `WriteAuditEvent(evt)`.
Nothing else in the class changes. (`LogPermissionChange`, `LogMigrationAction`,
`LogLookup` are not hooked: the module-action path is the one every module page uses;
the others are legacy shapes - record this scope in the README paragraph.)

### New: `Components/Layout/UsageTracker.razor`

`@rendermode InteractiveServer`, `@implements IDisposable`, injects `NavigationManager`,
`ModuleCatalog`, `UsageSession`, `UsageTelemetryService`, `IJSRuntime`. Renders
nothing. `OnInitialized`: `RecordSessionStart`, record the current route, subscribe
`LocationChanged`. `OnAfterRenderAsync(firstRender)`: try `getTheme`, `RecordTheme`.
`Dispose`: unsubscribe. Route -> module: `Navigation.ToBaseRelativePath(uri)`, cut at
`?`, trim `/`, `Catalog.GetByRoute(route)?.Id`; null module recorded only for `""`
and `access-denied`. Placed in `MainLayout.razor` inside `<Authorized>` next to
`ThemePicker` so anonymous circuits (the sign-in bounce) record nothing.

### Change: `ThemePicker.razor`

After `setTheme` succeeds: `Usage.RecordTheme(Session.Id, currentId)`.

### Change: `Program.cs`

Register `UsageEventRepository`, `UsageTelemetryService` (singletons) and
`UsageSession` (scoped). In the startup block after `migrator.Migrate()`:
`var pruned = usageRepo.PruneOlderThan(DateTime.UtcNow.AddDays(-UsageTelemetryService.RetentionDays)); Log.Information(...)`
inside its own try/catch that logs and continues.

### Change: `ModuleCatalog.cs` (`AdminEventLog`)

`ConfigFields` gains `new("UsageTelemetryEnabled", "Record anonymous usage", "Records
which modules are opened and which actions run, with no user, IP or target. Off stops
new rows and hides the Home page disclosure; the Usage view keeps showing existing
rows.", Required: false, DefaultValue: "true", FieldType: ConfigFieldType.Boolean)` -
the `PreventSelfGrant` shape at `ModuleCatalog.cs:161`. Version `1.1.0` -> `1.2.0` (S5).

### Change: `Components/Pages/Home.razor`

Inject `UsageTelemetryService`; inside the existing `<ul>` at `:36-42`, after the
notification bullet:

```
@if (Usage.Enabled())
{
    <li><strong>Anonymous usage data is collected:</strong> which pages you open, which actions you run, and the theme you use are recorded to improve the portal. This data contains no username, IP address, or details of the items you act on, and is kept for @UsageTelemetryService.RetentionDays days.</li>
}
```

The `Enabled()` read is the same one the recorder uses, so the two cannot drift.

### Change: `AdminEventLog.razor`

State `bool showUsage`; a two-button toggle above the filter row. When `showUsage`:
hide the events table and undo panel, show the date range only, and render the three
Usage tables from `UsageEventRepository` (loaded in `LoadUsage()` on toggle and on
date change). `internal static` projector `BuildModuleRows(IReadOnlyList<ModuleUsage>,
IReadOnlyList<AdminModuleDescriptor>)` fills zero rows and computes actions-per-open,
so it is unit-testable.

## 7. Task breakdown

One commit per slice; S2 depends on S1, S3 and S5 on S2, S4 on S1; S6 last.

**S1 - migration v7 + `UsageEventRepository` + `TestConfigStore.CreateUsageEvents` +
base app bump `2.18.0` -> `2.19.0`.** Serves AC1, AC2, base half of AC11.

**S2 - `UsageSession`, `UsageTelemetryService`, `AuditService` hook, DI.** Serves
AC3, AC4, AC5, AC10.

**S3 - `UsageTracker` component in `MainLayout`, `ThemePicker` hook.** Serves AC6, AC7.

**S4 - startup prune + `UsageTelemetryEnabled` config field + Home page disclosure.**
Serves AC8, the switch half of AC10, and AC13. The disclosure ships in the same slice
as the switch it depends on. (The field ships before the view; the module bump waits
for S5, which lands the operator-visible change - if S5 is ever deployed separately,
bump here.)

**S5 - Usage view on the Event Log page, `AdminEventLog` `1.1.0` -> `1.2.0`.** Serves
AC9, module half of AC11.

**S6 - README paragraph, plan status, state.** Serves AC12.

## 8. Test plan

`ExchangeAdminWeb.Tests/UsageEventRepositoryTests.cs` (S1, real temp SQLite via
`TestConfigStore`):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC1 | `Schema_HasNoIdentityColumns` | `PRAGMA table_info(usage_event)` yields exactly id, ts, session, kind, module, detail, success | Add a `user` column; FAIL |
| AC1 | `Migrate_ReachesVersion7_AndIsIdempotent` | Fresh DB migrates to `TargetVersion` = 7, second run no-ops | n/a (shape) |
| AC2 | `Insert_ThenModuleSummary_CountsOpensActionsFailedSessions` | 3 opens / 2 sessions, 2 actions (1 failed) -> one row {3,2,1,2} | Miscount any; FAIL |
| AC2 | `ModuleSummary_RespectsRange` | A row outside [from,to) is not counted | Drop the WHERE; FAIL |
| AC2 | `ThemeSummary_UsesLatestThemePerSession` | Session A: light then oled -> counts once under oled | Count all rows; FAIL |
| AC2 | `SessionSummary_FlagsSessionsWithNoAction` | Session with opens and no action counted in `SessionsWithNoAction` | Invert; FAIL |
| AC2 | `PruneOlderThan_DeletesOnlyOlderRows` | Returns 2, leaves the newer 1 | Remove the cutoff; FAIL |

`ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs` (S2, S4; `Enqueue` seam
captures events synchronously):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC3 | `RecordOpen_QueuesOneOpenRow_WithSessionAndRoute` | Kind `open`, module, detail = route, success null | Swap fields; FAIL |
| AC3 | `RecordAction_QueuesActionRow_WithoutSession` | Kind `action`, session null, success carried | Put a session in; FAIL |
| AC3 | `Enqueue_SwallowsRepositoryExceptions` | Repository throws -> no exception escapes, warning logged | Rethrow; FAIL |
| AC10 | `Disabled_RecordsNothing` | Kill switch false -> `Enqueue` never called | Ignore the switch; FAIL |
| AC10 | `UnreadableSwitch_IsDisabled` | Config throws -> nothing recorded | Default to true; FAIL |
| AC5 | `AuditService_LogModuleAction_CallsRecordAction` (behavioral: fake sink) | One `action` row with category, action, success | Remove the hook; FAIL |
| AC5 | `AuditService_NullSink_IsNoOp` | Existing constructor shape still works | n/a |
| AC5 | `AuditService_HookFollowsTheAuditWrite` (source guard) | `_usage?.RecordAction(` appears after `WriteAuditEvent(evt)` in the method body | Move it; FAIL |

`ExchangeAdminWeb.Tests/UsageTrackerWiringTests.cs` (S3, S5, source guards, no bUnit):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC6 | `UsageTracker_RecordsOnLocationChanged_AndDisposes` | `LocationChanged +=` and `-=`, `GetByRoute(`, `RecordSessionStart(`, `RecordOpen(` present | Remove any; FAIL |
| AC6 | `UsageTracker_RendersInsideAuthorized` | `MainLayout.razor` has `<UsageTracker />` after `<Authorized>` and before `</Authorized>` | Move it; FAIL |
| AC6 | `RouteToModule_MatchesModuleVersionDerivation` (pure) | The extracted `internal static string RouteOf(string baseRelative)` strips query and slashes like `ModuleVersion.razor` | Change; FAIL |
| AC7 | `ThemePicker_RecordsThemeAfterSet` | `RecordTheme(` follows `setTheme` | Remove; FAIL |
| AC13 | `Home_DisclosesTelemetry_OnlyWhenEnabled` | `Home.razor` has `Anonymous usage data is collected` inside an `@if (Usage.Enabled())` block, mentions `RetentionDays`, and the phrase appears exactly once | Unconditional or hard-coded 90; FAIL |
| AC9 | `BuildModuleRows_FillsZerosForUnopenedModules` (pure) | 27 catalog modules, 2 with data -> 27 rows, zeros elsewhere, ratio computed | Drop the join; FAIL |
| AC11 | (existing) `ModuleCatalogTests` untouched | No permission/alias change | n/a |

Manual checks after deploy:

1. Open three modules, act in one, switch theme once. Event Log -> Usage for today
   shows the three opens, one action, the theme under its new id, one session.
2. Inspect the DB: `SELECT DISTINCT session, kind, module, detail FROM usage_event` -
   no value is a user name, an email, an IP, or a target.
3. Turn `UsageTelemetryEnabled` off in Module Config, open two modules: no new rows;
   the Usage view still shows the earlier ones.
4. Restart the app after inserting a row dated 91 days ago: the startup log names one
   pruned row.
5. Sign-in bounce (`access-denied`) as a user without access: no `session` row for that
   circuit (the tracker sits inside `<Authorized>`).
6. Home page shows the "Anonymous usage data is collected" bullet with "90 days"; after
   check 3 (switch off) it is gone; switch on brings it back.

Verification commands (from `.agents/repo-guidance.md`):

```
dotnet build ExchangeAdminWeb.slnx -c Release
dotnet test ExchangeAdminWeb.slnx
dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore
git diff --check HEAD
```

Non-vacuity: revert the named target of each new test, confirm FAIL, restore, confirm
PASS.

## 9. Traceability check

Completed in S6.

## 10. Review log

(Filled by the openreview dispatch.)
