# aud-a45: ConfigChangeWatcher.LogOnce calls the reader's logger unguarded, so a throwing logger reaches every cache-hit reader

**Severity**: MEDIUM - the aud-a28q/a28z class in shared storage code. `Services/Storage/ConfigChangeWatcher.cs`
`LogOnce` reports an unreadable change token through the caller's logger with no guard; if the provider
throws, `HasChangedSince` throws into every reader that hits its cache: `ADAttributeEditorService` (two
sites), `PermissionValidator`, `ProtectedPrincipalService`, `UsageTelemetryService`. aud-a28z guarded
`ExtendedLogService`'s own call; one guard in `LogOnce` covers all of them.
**Status**: In progress - plan below, approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a42d5cbfa41a7a483` (from `2df48c36`)
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28z/a26u/cdt-41 agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `36da9789`: `Services/Storage/ConfigChangeWatcher.cs` around line 120 (`LogOnce`); callers at
`Services/ADAttributeEditorService.cs` 216 and 754, `Services/PermissionValidator.cs` 639,
`Services/ProtectedPrincipalService.cs` 373, `Services/UsageTelemetryService.cs` 79-110 (tip line numbers of
the a28z branch).

## Approach (to be written by the implementing agent before code)

- `LogOnce` wraps its logger call in the aud-a28u nested guard; `HasChangedSince` never throws for a logging
  failure (an unreadable token still reads as "changed", fail closed on caching - keep that).
- Test: the aud-a28q throwing-logger theory extended to `ConfigChangeWatcher`; a reader with a throwing
  logger and an unreadable token still answers. Mutation at the call site: the guard removed.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the aud-a28z/a26u/cdt-41 hand-back (`6b71adcc`).
One commit `fix(config-store): ... (aud-a45)`, second of three on branch `worktree-agent-a42d5cbfa41a7a483` (after
aud-a47, before aud-a46). Shared infrastructure - the watcher every caching config reader consults - so a base app
version change; the supervisor owns the bump. Evidence re-read at `2df48c36`:

1. `Services/Storage/ConfigChangeWatcher.cs` `LogOnce` (111-121) sets `_failureLogged` under the lock (113-118), then
   calls `_logger?.LogWarning(...)` (120) with no guard. It runs from `CurrentToken`'s catch (68-72), before that
   method's `return Unknown`, and from `HasChangedSince`'s catch (96-100), inside its `lock (_lock)` and before its
   `return false`. A provider that throws therefore takes its exception out of both public methods.
2. The readers, by name only - their files are other agents'. Each consults the watcher on every cache hit and reads
   the token on every load, each passing its own logger to the watcher, so the provider that throws is the reader's
   own:
   - `UsageTelemetryService.Enabled` (105-127: the hit 108-111, the token 119). It is called from `Record` (235) and
     from `Components/Pages/Home.razor` 43 in render.
   - `PermissionValidator.CacheIsFresh` (663-666) and `EnsureInitializedAsync` (682).
   - `ProtectedPrincipalService` (370-374, 380).
   - `ADAttributeEditorService.GetAllowlist` (213-222) and `GetLegend` (752-761).
   - `ExtendedLogService` guards its own: `MinimumLevel` 87-97 since aud-a28z, and `LoadLevel`'s `catch { }` 354.
3. The record's Approach says an unreadable token "still reads as changed". Read from the file, the watcher's contract
   is narrower, and it is kept exactly:
   - A check whose read fails answers false: serve the cache, the reader's TTL bounding staleness (75-82, 96-100).
   - The token a failed `CurrentToken` returns, `Unknown`, reads as changed against every later successful read
     (24-29, 103-107). So a reader that loaded while the token was unreadable reloads at the first check that
     reaches the store.
   - Nothing here changes either answer; only the throw goes.

**Rule.** Reporting an unreadable change token never fails the call that met it, whatever the logging provider does:
`CurrentToken` answers `Unknown`, and `HasChangedSince` answers false (or true, against a token loaded under `Unknown`),
exactly as with a provider that works.

1. `LogOnce`: the logger call inside a nested `try { ... } catch (Exception) { }` (the aud-a28u shape: a provider that
   throws has nowhere to report its own failure). The flag stays set when the report fails: one attempt per failure
   streak, as now, so a provider that keeps throwing costs a throw at most once per streak, not once per check.
2. Comments after the mutation round, checked code-identical.

Tests, `ExchangeAdminWeb.Tests/ConfigChangeWatcherTests.cs`, with a capturing logger that throws once it has
recorded, as aud-a28u's:
- `TheWatcher_GivenALoggerThatThrows_StillAnswers`, by cause:
  - `CurrentToken`'s read fails: `Unknown`, the report attempted.
  - `HasChangedSince`'s read fails: false.
  - A read fails after a success: false, a second report attempted.
  - A value loaded under `Unknown`, the store readable again: true, a changed answer.
  - No throw in any case.
- `AReader_GivenALoggerThatThrows_AndAnUnreadableToken_StillAnswers`: `UsageTelemetryService.Enabled`, the reader on
  the audit path and the Home page, over a real config store whose change token the test can make unreadable.
  - On the cache hit, the clock past the throttle: it answers its cached value.
  - On the load, a fresh service: it answers the store's value.
  - No throw in either case.

Mutation at the call site (md5 before and after, the named failing test, restored, md5 matched, touched, rebuilt,
green): M1 the guard removed (the bare `_logger?.LogWarning(...)` again).

Files: `Services/Storage/ConfigChangeWatcher.cs`, `ExchangeAdminWeb.Tests/ConfigChangeWatcherTests.cs`, this record.

## Guard proof

Pending.
