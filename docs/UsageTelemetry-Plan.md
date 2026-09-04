# Anonymous usage telemetry

Status: Draft 2026-09-04, codex openreview over `2938db7..4af217e` returned
`acceptable_with_changes` (ute-1..3 plus one further material change, section 10), all
admitted and folded in. AWAITING AN OWNER GO to implement. The one owner decision (D1,
event rows) is RULED (`.agents/decisions.md` 2026-09-04); no owner decision is
outstanding.
Owner: Michael
Last verified against code: `2938db7` / 2026-09-04
Versions: base app `2.18.0` -> `2.19.0` in S1 (new database, shared service, layout
component, circuit handler, audit hook); `AdminEventLog` `1.1.0` -> `1.2.0` in S5
(Usage view + kill-switch config field).
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

Owner request 2026-09-04: *"is it possible to add some telemetry to this so I can see
how people are using it? the event log shows actual changes made, but things like
theme, modules opened and not used, etc. would be useful. it can be anonymous and
lightweight."* Ruling on the offered fork (daily counters vs anonymous event rows):
*"event rows"*.

The Constitution requires a written plan: persistence/storage change (a new database)
and a change to the audit path (a hook in `AuditService`).

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
- **Storage is its own database, `config/exchangeadmin-usage.db` (ute-1).** The first
  draft put the table in the config database; `tools/promote-dev-to-prod.ps1:400-408`
  replaces prod's config DB WHOLESALE with dev's on every promotion, so prod telemetry
  would have been overwritten with dev's each time. The jobs database
  (`exchangeadmin-jobs.db`, `Program.cs:63-70`) is the precedent: same deploy-excluded
  `config/` folder, its own `SqliteConnectionFactory`, not promoted, not in
  `SqliteConfigBackup.psm1`'s scope (which names `exchangeadmin.db` only). Telemetry is
  disposable, so no backup is added.
- **Actions are captured at the ONE common audit write, `AuditService.WriteAuditEvent`
  (ute-2),** which every public audit method ends in - not at `LogModuleAction` alone.
  The first draft claimed every module passes through `LogModuleAction`; it does not
  (`LogMailboxPermission`, `LogCalendarPermission`, `LogMigrationCheck/Batch/Action`,
  `LogLookupAction`, `LogMfaResetAction`, `LogConferenceRoomAction`, `LogADAttributeEdit`,
  `LogSettingsChange` each hard-code their own `category`). The hook reads `category`,
  `action` and `result` off the event dictionary the method already built. Reporting
  maps `category` to a module id: a catalog id verbatim, else a small static table for
  the legacy categories the Event Log dropdown already enumerates
  (`AdminEventLog.razor:47-64`: `MailboxPermission` -> `MailboxPermissions`,
  `CalendarPermission` -> `CalendarPermissions`, `MigrationCheck` / `MigrationBatch` /
  `MigrationAction` -> `Migration`, `Lookup` -> reported as "Lookup", `AdminSettings` ->
  reported as "Admin Settings"), else reported under "Other" with the category shown.
- **Action rows DO carry the throwaway session id when the action came from a browser
  circuit (ute-3).** `CircuitHandler.CreateInboundActivityHandler` (in the framework
  since .NET 8) wraps every inbound circuit activity - event handlers, JS interop
  callbacks - in the same async flow, so a scoped handler can set
  `UsageSession.Current` (a static `AsyncLocal<string?>`) around each one, and the
  singleton `UsageTelemetryService` reads it. Actions raised outside a circuit
  (background jobs, startup) have no session and stay null - the only nullable case.
  The first draft declared the id unreachable from the singleton and then still promised
  per-visit "opened and left"; the two could not both hold. With the id on the row,
  `SessionSummary` is exact, not a time-window guess.
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

- AC1: `UsageEventRepository` owns `config/exchangeadmin-usage.db` through its own
  `SqliteConnectionFactory` (never the config store's) and creates its schema
  idempotently on first use (`CREATE TABLE IF NOT EXISTS usage_event` with `id INTEGER
  PRIMARY KEY`, `ts TEXT NOT NULL` ISO-8601 UTC, `session TEXT COLLATE NOCASE` nullable,
  `kind TEXT NOT NULL COLLATE NOCASE`, `module TEXT COLLATE NOCASE` nullable, `detail
  TEXT` nullable, `success INTEGER` nullable; indexes on `ts` and `(module, kind)`).
  The table has no user, ip, target or ticket column (a tripwire test asserts the
  column list). `ConfigStoreMigrator` is untouched.
- AC2: `UsageEventRepository` offers `Insert(UsageEvent)`, `PruneOlderThan(DateTime
  cutoffUtc)` returning the deleted count, and three aggregates over a UTC range:
  `ModuleSummary(from, to)` -> per module `{ Opens, Actions, FailedActions,
  DistinctSessions }`; `ThemeSummary(from, to)` -> per theme id the count of distinct
  sessions whose latest `theme` row in range has that id; `SessionSummary(from, to)` ->
  `{ Sessions, AvgModulesPerSession, SessionsWithNoAction }` where a session "acted" if
  any `action` row carries its session id (exact join on the column, ute-3).
- AC3: `UsageTelemetryService` (singleton) exposes `RecordOpen(session, module,
  route)`, `RecordTheme(session, themeId)`, `RecordSessionStart(session)`,
  `RecordAction(category, action, success)`. Each checks the kill switch, then queues
  one `Insert` on `Task.Run`, catches every exception and logs a warning. No method
  returns a Task the caller must await; none throws. `RecordAction` takes the session
  from `UsageSession.Current` (null outside a circuit) and maps `category` to the
  stored `module` through `UsageTelemetryService.ModuleOf(category)` (section 1
  table; pure, tested).
- AC4: `UsageSession` is `AddScoped`; its `Id` is `Guid.NewGuid().ToString("N")`
  minted at construction; it holds nothing else and is never written anywhere but the
  `session` column. `UsageSession.Current` is a static `AsyncLocal<string?>`.
  `UsageSessionCircuitHandler : CircuitHandler` (scoped, registered like
  `ClientInfoCircuitHandler` at `Program.cs:207`) overrides
  `CreateInboundActivityHandler` to set `Current.Value = session.Id` for the duration of
  each inbound activity and restore the prior value after (try/finally).
- AC5: `AuditService.WriteAuditEvent` - the common path of EVERY public audit method -
  calls `_usage?.RecordAction(category, action, success)` as its LAST statement, after
  `_log.Write(evt)` and the trace step, reading the three values off `evt`; a telemetry
  fault cannot reach the audit write because `RecordAction` never throws (AC3) and runs
  after it. The constructor takes `UsageTelemetryService? usage = null` so every
  existing construction compiles and a null sink is a no-op.
- AC6: A `UsageTracker` component (`@rendermode InteractiveServer`, rendered once in
  `MainLayout.razor` beside `ThemePicker`) records `session` and the first `open` in
  `OnAfterRenderAsync(firstRender)` - NOT `OnInitialized`, which runs once more during
  prerender and would double-count (material change 4) - and subscribes to
  `NavigationManager.LocationChanged` there too, recording `open` on every change. Route
  -> module id through `ModuleCatalog.GetByRoute` after the same route derivation
  `ModuleVersion.razor:18-22` uses; a route with no module records `open` with `module
  = null` and `detail = route` only for the well-known non-module routes (`""` home,
  `access-denied`), and nothing otherwise. It reads the theme once via
  `JS.InvokeAsync<string>("getTheme")` in the same first-render pass (the
  `ThemePicker.razor:28-45` shape, since localStorage is unreachable during prerender)
  and records `theme`. It renders no markup and unsubscribes in `Dispose`.
- AC7: `ThemePicker` records `theme` on every change, through the same service.
- AC8: The startup block in `Program.cs` calls `PruneOlderThan(UtcNow - 90 days)` once,
  beside the jobs-DB prune, logging the count; a failure (including a missing or
  unwritable usage DB) logs and does not stop startup (the
  `MessageTraceExportStore.PruneExpired` posture).
- AC9: `AdminEventLog.razor` gains an "Events | Usage" toggle (C# state, no Bootstrap
  JS). The Usage view reuses the page's date range and shows: a module table (Module
  display name, Opens, Actions, Failed, Sessions, Actions per open; plus "Lookup",
  "Admin Settings" and "Other" rows for actions with no module), a theme table (Theme
  name via `UiThemeCatalog.Resolve`, Sessions), and a one-line session summary
  (sessions, average modules per session, sessions with no action). Modules with zero
  rows in range are listed with zeros (from `ModuleCatalog.GetAll()`) so "never opened"
  is visible. Same `EventLog` policy gates it; no new permission.
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
| Usage DB missing, locked or unwritable at first use | `CREATE TABLE IF NOT EXISTS` runs inside the first insert's try/catch; failure logged | Nothing | No telemetry until the next attempt; startup unaffected (the usage DB is never on the fail-fast path) |
| Telemetry insert (any kind) | Caught inside the `Task.Run`, warning logged | Nothing | One row missing; the triggering operation unaffected |
| `RecordAction` throws synchronously (should be impossible - guarded) | AC5 places the call LAST in `WriteAuditEvent`, after the file write; a throw would surface to the page like any audit exception does today | Existing page error handling | Audit row already written |
| Inbound-activity handler cannot set the session (no circuit: background job, startup) | `UsageSession.Current` stays null | Nothing | Action row with `session = NULL`, counted per module, not per visit |
| Prod promotion | Not involved: `exchangeadmin-usage.db` is not copied by `Copy-SqliteConfigDb` and `config/` is excluded from robocopy | Nothing | Each environment keeps its own telemetry |
| Kill switch config unreadable | `ModuleConfigService` fail-closed semantics apply: treat as disabled, log once | Nothing | No rows written until config reads again |
| `getTheme` JS interop fails (prerender, disconnected) | Caught; no `theme` row | Nothing | Theme unknown for that session |
| Route has no module | Only home / access-denied are recorded (module null); anything else ignored | Nothing | No stray rows |
| Prune at startup fails | Logged, startup continues | Nothing | Old rows linger until the next start |
| Usage view query fails | Error alert in the view (the page's existing alert shape) | Alert | Read-only, nothing changes |
| Rollback | Revert the commits; `exchangeadmin-usage.db` stays on disk, unread, harmless - no migrator version to refuse | Nothing | Delete the file by hand if wanted |

## 5. Rollback / blast radius

Revert any or all commits freely. The usage database is a separate file that nothing
else opens, has no version cursor, and is not backed up or promoted; a reverted build
simply stops writing to it. (The first draft put the table in the config database
behind a migrator step, which would have made a revert refuse to start - ute-1
removed that hazard along with the promotion one.)

Blast radius: one new database nobody else reads, one optional constructor parameter
on `AuditService` (null-safe), one `AsyncLocal` read in a singleton, one scoped circuit
handler, one invisible layout component, one config field, one bullet on the Home
notice, one view on the Event Log page. No authorization decision changes. The
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

### New: `config/exchangeadmin-usage.db` (own factory, idempotent schema)

`Program.cs` builds a second `SqliteConnectionFactory` at
`Path.Combine(ContentRootPath, "config", "exchangeadmin-usage.db")` exactly as the
jobs DB does at `:68-70`, and hands it to `UsageEventRepository`. The repository runs
this once per process (lazy, under a lock, inside the first operation's try/catch):

```
CREATE TABLE IF NOT EXISTS usage_event (
    id      INTEGER PRIMARY KEY,
    ts      TEXT    NOT NULL,
    session TEXT    COLLATE NOCASE,
    kind    TEXT    NOT NULL COLLATE NOCASE,
    module  TEXT    COLLATE NOCASE,
    detail  TEXT,
    success INTEGER
);
CREATE INDEX IF NOT EXISTS ix_usage_event_ts ON usage_event(ts);
CREATE INDEX IF NOT EXISTS ix_usage_event_module_kind ON usage_event(module, kind);
```

Kinds: `session` (detail null), `open` (detail = route), `theme` (detail = theme id),
`action` (detail = action name, success 0/1, session = the circuit's id or null).

### New: `Services/Storage/UsageEventRepository.cs`

```
public sealed record UsageEvent(DateTime TsUtc, string? Session, string Kind, string? Module, string? Detail, bool? Success);
public sealed record ModuleUsage(string Module, int Opens, int Actions, int FailedActions, int DistinctSessions);
public sealed record ThemeUsage(string ThemeId, int Sessions);
public sealed record SessionUsage(int Sessions, double AvgModulesPerSession, int SessionsWithNoAction);

public sealed class UsageEventRepository(SqliteConnectionFactory usageDb)
{
    public void Insert(UsageEvent e);
    public int PruneOlderThan(DateTime cutoffUtc);
    public IReadOnlyList<ModuleUsage> ModuleSummary(DateTime fromUtc, DateTime toUtc);
    public IReadOnlyList<ThemeUsage> ThemeSummary(DateTime fromUtc, DateTime toUtc);
    public SessionUsage SessionSummary(DateTime fromUtc, DateTime toUtc);
}
```

All SQL parameterised; `ts` stored as `DateTime.UtcNow.ToString("O")` and compared
as text (ISO-8601 sorts lexically); connections opened per operation through
`usageDb.Open()` and disposed. Registered `AddSingleton`. Tests build one from a
temp-path factory (`new SqliteConnectionFactory(Path.Combine(dir, "exchangeadmin-usage.db"))`);
no `TestConfigStore` change.

### New: `Services/UsageSession.cs`, `Services/UsageSessionCircuitHandler.cs`, `Services/UsageTelemetryService.cs`

```
public sealed class UsageSession                       // AddScoped
{
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public static readonly AsyncLocal<string?> Current = new();   // set per inbound circuit activity
}

public sealed class UsageSessionCircuitHandler(UsageSession session) : CircuitHandler   // AddScoped<CircuitHandler, ...>
{
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(Func<CircuitInboundActivityContext, Task> next)
        => async context =>
        {
            var prior = UsageSession.Current.Value;
            UsageSession.Current.Value = session.Id;
            try { await next(context); }
            finally { UsageSession.Current.Value = prior; }
        };
}

public sealed class UsageTelemetryService(UsageEventRepository repo, ModuleConfigService config, ModuleCatalog catalog, ILogger<UsageTelemetryService> log)
{
    public const int RetentionDays = 90;
    internal const string ConfigModuleId = "AdminEventLog";
    internal const string ConfigKey = "UsageTelemetryEnabled";
    public bool Enabled();                        // reads the Boolean field; unreadable -> false, logged once
    public void RecordSessionStart(string session);
    public void RecordOpen(string session, string? module, string? route);
    public void RecordTheme(string session, string themeId);
    public void RecordAction(string category, string action, bool success);   // session = UsageSession.Current.Value
    internal static string? ModuleOf(string category, ModuleCatalog catalog);  // pure: catalog id | legacy map | null
    internal virtual void Enqueue(UsageEvent e);  // Task.Run + try/catch + LogWarning; TEST SEAM
}
```

`Enabled()` reads the field the way `BitLockerRecoveryService` reads `ValidateTickets`
(the `ITicketValidator` plan's switch) - copy that call shape at implementation.
`ModuleOf`: `catalog.GetById(category)?.Id`, else the legacy table in section 1, else
null (stored `module = NULL`, `detail = action`, reported under "Other" with the
category kept in a second detail-free way: the Usage view groups NULL-module actions
by their original category, which the repository returns from a `category` column
added for exactly this - see below).

Schema note for the above: `usage_event` gains `category TEXT COLLATE NOCASE` nullable
(the raw audit category on `action` rows) so unmapped categories can still be
reported by name. It is an audit category string, never operator input.

### Change: `AuditService`

Constructor gains `UsageTelemetryService? usage = null`. `WriteAuditEvent(evt)` -
the private method every public audit method ends in (`AuditService.cs:384-422`) -
gains one final statement after `_log.Write(evt)` and the `AuditWritten` trace step:

```
_usage?.RecordAction(
    evt.GetValueOrDefault("category")?.ToString() ?? "",
    evt.GetValueOrDefault("action")?.ToString() ?? "",
    string.Equals(evt.GetValueOrDefault("result")?.ToString(), "Success", StringComparison.Ordinal));
```

Nothing else in the class changes. Every audit method - `LogMailboxPermission`,
`LogCalendarPermission`, `LogMigrationCheck`, `LogMigrationBatch`, `LogMigrationAction`,
`LogModuleAction`, `LogLookupAction`, `LogMfaResetAction`, the ConferenceRooms and
ADAttributeEditor writers, `LogSettingsChange` - therefore counts (ute-2).

### New: `Components/Layout/UsageTracker.razor`

`@rendermode InteractiveServer`, `@implements IDisposable`, injects `NavigationManager`,
`ModuleCatalog`, `UsageSession`, `UsageTelemetryService`, `IJSRuntime`. Renders
nothing. Everything happens in `OnAfterRenderAsync(firstRender)`, which runs only in
the interactive circuit, never during prerender (material change 4): `RecordSessionStart`,
record the current route as `open`, subscribe `LocationChanged`, then try `getTheme`
and `RecordTheme`. `LocationChanged` handler records `open`. `Dispose`: unsubscribe.
Route -> module: `Navigation.ToBaseRelativePath(uri)`, cut at `?`, trim `/`,
`Catalog.GetByRoute(route)?.Id`; null module recorded only for `""` and
`access-denied`. Placed in `MainLayout.razor` inside `<Authorized>` next to
`ThemePicker` so anonymous circuits (the sign-in bounce) record nothing.

### Change: `ThemePicker.razor`

After `setTheme` succeeds: `Usage.RecordTheme(Session.Id, currentId)`.

### Change: `Program.cs`

Build the usage-DB `SqliteConnectionFactory` beside the jobs one (`:68-70`); register
`UsageEventRepository`, `UsageTelemetryService` (singletons), `UsageSession` (scoped)
and `AddScoped<CircuitHandler, UsageSessionCircuitHandler>()` beside
`ClientInfoCircuitHandler` (`:207`). In the startup block beside the jobs prune
(`:271-272`): `var pruned = usageRepo.PruneOlderThan(DateTime.UtcNow.AddDays(-UsageTelemetryService.RetentionDays)); Log.Information(...)`
inside its own try/catch that logs and continues.

### Deploy scripts

No change needed: `config/` is already excluded from robocopy mirroring in
`deploy.ps1` and `promote-dev-to-prod.ps1` (`/XD logs config`), and
`Copy-SqliteConfigDb` copies `exchangeadmin.db` by name. S6 adds one sentence to
`.agents/repo-guidance.md` Architectural Invariant 3 naming `exchangeadmin-usage.db`
as a third runtime file in `config/` that is neither backed up nor promoted, so the
next deploy change does not "fix" its absence from the backup.

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

**S1 - usage-DB factory + `UsageEventRepository` (idempotent schema) + base app bump
`2.18.0` -> `2.19.0`.** Serves AC1, AC2, base half of AC11.

**S2 - `UsageSession`, `UsageSessionCircuitHandler`, `UsageTelemetryService`
(`ModuleOf`, `Enqueue`), `AuditService.WriteAuditEvent` hook, DI.** Serves AC3, AC4,
AC5, AC10.

**S3 - `UsageTracker` component in `MainLayout`, `ThemePicker` hook.** Serves AC6, AC7.

**S4 - startup prune + `UsageTelemetryEnabled` config field + Home page disclosure.**
Serves AC8, the switch half of AC10, and AC13. The disclosure ships in the same slice
as the switch it depends on. (The field ships before the view; the module bump waits
for S5, which lands the operator-visible change - if S5 is ever deployed separately,
bump here.)

**S5 - Usage view on the Event Log page, `AdminEventLog` `1.1.0` -> `1.2.0`.** Serves
AC9, module half of AC11.

**S6 - README paragraph, repo-guidance invariant 3 sentence, plan status, state.**
Serves AC12.

## 8. Test plan

`ExchangeAdminWeb.Tests/UsageEventRepositoryTests.cs` (S1, real temp SQLite through a
temp-path `SqliteConnectionFactory`):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC1 | `Schema_HasNoIdentityColumns` | `PRAGMA table_info(usage_event)` yields exactly id, ts, session, kind, module, category, detail, success | Add a `user` column; FAIL |
| AC1 | `Schema_IsCreatedLazily_AndIdempotent` | Two repositories on the same file both work; no error on the second create | n/a (shape) |
| AC1 | `ConfigStoreMigrator_IsUntouched` | `TargetVersion` is still 6 | Append a step; FAIL |
| AC2 | `Insert_ThenModuleSummary_CountsOpensActionsFailedSessions` | 3 opens / 2 sessions, 2 actions (1 failed) -> one row {3,2,1,2} | Miscount any; FAIL |
| AC2 | `ModuleSummary_RespectsRange` | A row outside [from,to) is not counted | Drop the WHERE; FAIL |
| AC2 | `ThemeSummary_UsesLatestThemePerSession` | Session A: light then oled -> counts once under oled | Count all rows; FAIL |
| AC2 | `SessionSummary_FlagsSessionsWithNoAction_ByExactSessionJoin` | Session A opens and acts; session B opens only; an action with session NULL in the same module and minute does NOT make B "acted" | Join by time window; FAIL |
| AC2 | `PruneOlderThan_DeletesOnlyOlderRows` | Returns 2, leaves the newer 1 | Remove the cutoff; FAIL |

`ExchangeAdminWeb.Tests/UsageTelemetryServiceTests.cs` (S2, S4; `Enqueue` seam
captures events synchronously):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC3 | `RecordOpen_QueuesOneOpenRow_WithSessionAndRoute` | Kind `open`, module, detail = route, success null | Swap fields; FAIL |
| AC3/AC4 | `RecordAction_CarriesTheAmbientSession` | With `UsageSession.Current.Value = "abc"` set in the test flow -> row session "abc"; with it null -> null | Ignore the ambient; FAIL |
| AC3 | `ModuleOf_MapsCatalogIds_LegacyCategories_AndUnknown` | `GroupManagement` -> itself; `MailboxPermission` -> `MailboxPermissions`; `MigrationBatch` -> `Migration`; `Bogus` -> null | Drop the legacy table; FAIL |
| AC3 | `RecordAction_KeepsTheRawCategory` | Unknown category -> module null, category stored | Drop the column; FAIL |
| AC3 | `Enqueue_SwallowsRepositoryExceptions` | Repository throws -> no exception escapes, warning logged | Rethrow; FAIL |
| AC4 | `CircuitHandler_SetsAndRestoresCurrent` | Invoking the wrapped activity sees `Current == session.Id`; afterwards the prior value is back | Drop the finally; FAIL |
| AC10 | `Disabled_RecordsNothing` | Kill switch false -> `Enqueue` never called | Ignore the switch; FAIL |
| AC10 | `UnreadableSwitch_IsDisabled` | Config throws -> nothing recorded | Default to true; FAIL |
| AC5 | `AuditService_EveryPublicAuditMethod_CallsRecordAction` (behavioral: fake sink) | Calling each public `Log*` method once yields one `action` row each with that method's category | Hook only `LogModuleAction`; FAIL |
| AC5 | `AuditService_NullSink_IsNoOp` | Existing constructor shape still works | n/a |
| AC5 | `AuditService_HookIsLastInWriteAuditEvent` (source guard) | `_usage?.RecordAction(` is the last statement of `WriteAuditEvent`, after `_log.Write(evt)` | Move it above; FAIL |

`ExchangeAdminWeb.Tests/UsageTrackerWiringTests.cs` (S3, S5, source guards, no bUnit):

| AC | Test | What it proves | Non-vacuity |
|---|---|---|---|
| AC6 | `UsageTracker_RecordsOnLocationChanged_AndDisposes` | `LocationChanged +=` and `-=`, `GetByRoute(`, `RecordSessionStart(`, `RecordOpen(` present | Remove any; FAIL |
| AC6 | `UsageTracker_RecordsOnlyAfterFirstInteractiveRender` | `RecordSessionStart(` and the subscription sit inside `OnAfterRenderAsync` under `if (firstRender)`; the file has no `OnInitialized` override | Move to OnInitialized; FAIL |
| AC6 | `UsageTracker_RendersInsideAuthorized` | `MainLayout.razor` has `<UsageTracker />` after `<Authorized>` and before `</Authorized>` | Move it; FAIL |
| AC6 | `RouteToModule_MatchesModuleVersionDerivation` (pure) | The extracted `internal static string RouteOf(string baseRelative)` strips query and slashes like `ModuleVersion.razor` | Change; FAIL |
| AC7 | `ThemePicker_RecordsThemeAfterSet` | `RecordTheme(` follows `setTheme` | Remove; FAIL |
| AC13 | `Home_DisclosesTelemetry_OnlyWhenEnabled` | `Home.razor` has `Anonymous usage data is collected` inside an `@if (Usage.Enabled())` block, mentions `RetentionDays`, and the phrase appears exactly once | Unconditional or hard-coded 90; FAIL |
| AC9 | `BuildModuleRows_FillsZerosForUnopenedModules` (pure) | 27 catalog modules, 2 with data -> 27 rows, zeros elsewhere, ratio computed | Drop the join; FAIL |
| AC11 | (existing) `ModuleCatalogTests` untouched | No permission/alias change | n/a |

Manual checks after deploy:

1. Open three modules, act in one, switch theme once. Event Log -> Usage for today
   shows the three opens, one action, the theme under its new id, one session; the
   action row's `session` equals the open rows' (query the DB). Act in Mailbox
   Permissions specifically: its action counts (the legacy `MailboxPermission` category
   maps, ute-2). Reload the page: still ONE `session` row for the new circuit, not two
   (prerender, material change 4).
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

- 2026-09-04: openreview codex (`@azure-openai-eus2-global/gpt-5.5-dzs` @ xhigh,
  grade fallback; codex-cli 0.152.1, `codex exec -s read-only`) over
  `2938db7..4af217e`: verdict `acceptable_with_changes`, capability_ok true, both
  SHAs echoed. The reviewer's own approach matched the plan's shape (event rows,
  short-lived session id, small service, layout and theme instrumentation, a usage
  view under Event Log, disclosure, retention, focused tests) and named two
  load-bearing choices that would have produced misleading telemetry. Four material
  changes, three findings, all admitted and folded in:
  **ute-1 (HIGH)** - the table was planned for the config database, which
  `promote-dev-to-prod.ps1` replaces wholesale with dev's on every promotion; now its
  own `config/exchangeadmin-usage.db` beside the jobs DB, never promoted or backed up,
  no migrator step (which also removed the revert-refuses-to-start hazard the first
  draft had documented as a rollback rule). `.agents/decisions.md` entry corrected.
  **ute-2 (HIGH)** - only `LogModuleAction` was hooked while MailboxPermissions,
  ConferenceRooms, Migration and others audit through their own methods; now the hook
  is the last statement of the common `WriteAuditEvent`, with a category-to-module
  map for the legacy categories and a `category` column so unmapped ones still report.
  **ute-3 (MEDIUM)** - the plan declared action rows session-less and still promised
  per-visit "opened and left"; now a scoped `CircuitHandler.CreateInboundActivityHandler`
  sets an `AsyncLocal` session for every inbound circuit activity, so page-originated
  actions carry the id and the session join is exact; only background actions stay null.
  **Material change 4 (no finding record - a plan-text correction)** - the tracker
  recorded in `OnInitialized`, which prerender runs once more; now everything happens
  in `OnAfterRenderAsync(firstRender)`, pinned by a source guard and manual check 1.
  Records: `.agents/review/findings/ute-{1,2,3}.md`; envelope
  `.agents/review/ute.result.json` (gitignored scratch).
