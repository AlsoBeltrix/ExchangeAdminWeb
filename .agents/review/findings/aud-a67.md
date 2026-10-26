# aud-a67: Settings pages and stores - unsaved counts carry across modules, the db CLI and the BitLocker archive wait 35 s not 5, the pool drain holds the circuit, no re-entrancy guard, a stale test comment, and the allowlist save's generic busy message

**Severity**: LOW - recorded under the 2026-10-23 stopping rule, not dispatched. (1) Module Configuration's
`LoadState` never clears `dirty`; only `ReloadAsync` does, so the previous module's unsaved counts survive a
move to another module on the same page instance. (2) `ConfigDbMaintenance` sets `busy_timeout=5000` with no
command timeout, so the `db` CLI waits about 35 s, not the 5 s `docs/ConfigDbToolInApp-Plan.md` documents;
`BitLockerRecoveryService`'s read-only archive connection has the same shape (inferred from the mechanism,
not measured). (3) `ExoPool.DrainPool()` in `SaveExoConfig` still runs on the circuit: up to five synchronous
disconnects. (4) No settings page has a re-entrancy guard: a second save started before the disabled Save
button renders runs alongside the first, whose `finally` clears `saving` early (cosmetic; the stores decide
under their locks). (5) `SettingsSaveAuditTests` 444-445 says `SetLevel` "swallows" a store failure; it has
rethrown since aud-a28k. (6) `ADAttributeEditorService.SaveAllowlistAndAudit` (464-472) still reports a busy
store as "Failed to save: SQLite Error 5" (the aud-a40 class inside the service; the exact change is in
aud-a40.md's follow-up section).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; held by the 2026-10-23 stopping rule (LOW) for after the first dev deploy
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a41/a40/a42 agent, 2026-10-23, candidates 2-6 and its follow-up)

## Evidence

At `86df002a`: `Components/Pages/ModuleConfig.razor` `LoadState` / `ReloadAsync`; `Services/Storage/ConfigDbMaintenance.cs`
175-183; `Services/BitLockerRecoveryService.cs` 243-257; `Components/Pages/ExchangeOnlineConfig.razor`
`SaveExoConfig` (the drain); the three pages' `saving` flags; `ExchangeAdminWeb.Tests/SettingsSaveAuditTests.cs`
444-445; `Services/ADAttributeEditorService.cs` 464-472.

## Approach (to be written by the implementing agent before code)

- `LoadState` resets `dirty`; the two other connections take `SqliteConnectionFactory.BusyTimeout` as their
  command timeout (measure the CLI wait); the drain runs off the circuit; each save sets `saving` before
  its first await and refuses a second entry; the comment corrected; the allowlist save uses
  `SettingsSaveAudit.LogSettingsSaveException`.
- Mutations at the call site: `dirty` left; the CLI timeout removed; the drain back on the circuit; the
  allowlist's generic message again.

### Implementation notes (fix/a64-a67, 2026-10-23)

Line numbers are at the commit each item names; `Components/Pages/ModuleConfig.razor` and the other settings pages are
NotYetConverted in `ClickGateRegistry` (no line keys), and `ProgressRegistry` pins call text, so a moved line moves no key.

- (1) At `620e9d5a`: `Components/Pages/ModuleConfig.razor` `LoadState` (1100-1169) replaces the page's module state and,
  since aud-a66, counts the load and clears `saving` (1105-1107), but never clears `dirty`; only `ReloadAsync` (1005) and
  the saves do. So a move to another module on the same page instance kept the previous module's unsaved counts: the
  save bar and the tab dots showed them over the next module, and `UnsavedChangesGuard` kept warning about edits that
  were not on the page. `LoadState` clears `dirty` beside `saving`: a fresh load holds no unsaved edit - every baseline it
  sets is the state it has just read - so clean is the right count. Test: `SettingsModuleMoveTests` +1 - an admins edit on one module, then a move: the
  next module's page is clean. Mutation: the clear removed. App-wide (Module Configuration): no module bump.
- (2) At `620e9d5a`: `Services/Storage/ConfigDbMaintenance.cs` `OpenReadOnly` (175-189), behind every `db` verb that reads
  a database (`backup`'s source, `check`, `active-jobs`, the backup copy's integrity check), sets `PRAGMA
  busy_timeout=5000` (182) and leaves the connection's `DefaultTimeout` at Microsoft.Data.Sqlite's 30 s, so a statement
  that met a lock was run again, each run another 5 s busy wait, until 30 s had passed - the aud-a41 shape the factory
  fixed, here outside the factory by design (it must not switch the file to WAL). `Services/BitLockerRecoveryService.cs`
  243-257, the read-only archive connection, has the same shape. Each now sets `DefaultTimeout` to
  `SqliteConnectionFactory.BusyTimeout` and its busy timeout from the same constant: one 5 s wait, then SQLITE_BUSY.
  `DefaultTimeout` bounds only the provider's busy retry, not how long a statement runs (the factory's measured remark),
  so a long `VACUUM INTO` is not cut short. Tests: `ConfigDbMaintenanceTests` - `OpenReadOnly` (made internal) has both
  settings, and the CLI's `check` of a database another connection holds exclusively fails after one wait, not the retry
  loop; `BitLockerRecoveryTests` - a search of an archive held exclusively says the archive could not be read, after one
  wait. Measurement: the built app's `db check` as a process against a fixture a `sqlite3` shell holds exclusively, before
  and after. Mutations: each `DefaultTimeout` removed. Versions: `ConfigDbMaintenance` app-wide; BitLocker Recovery's
  module version.

## Files changed

- (1): `Components/Pages/ModuleConfig.razor`; `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs`.

## Guard proof

Driver (session working directory `a64a67-work/mutate.sh`, never committed): the file's MD5; one exact-text replacement
that must match once and must change the bytes; a Debug build of the test project, 0 errors required; the filter; the
exact bytes restored with a fresh mtime and the MD5 re-checked; a rebuild; the filter again, green.

The settings-pages filter, used by every row below unless a row says otherwise:
`FullyQualifiedName~SettingsModuleMoveTests|FullyQualifiedName~SettingsSaveOffCircuitTests|FullyQualifiedName~SettingsStoreBusyTests|FullyQualifiedName~SettingsSaveAuditTests|FullyQualifiedName~AdminPageDirtyStateTests|FullyQualifiedName~GroupGrantValidationTests|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistryTests`.

(1). 493 tests. `ModuleConfig.razor` MD5 `c87557331b0ba5bff0463afe49cd9dfb`, matched after the row; 493/493 green after
the restore. The mutant is the page as it was at `36245893`, so its failure is the reproduction.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| A1 `LoadState`'s `dirty.Clear()` removed | `47dbb2d07ccaa6a4bd3d368138e58cc3` | 1 of 493: `SettingsModuleMoveTests.AnotherModuleOpenedAfterAnEdit_LoadsClean_WithNoCountFromTheModuleLeft` - "the next module's page still counts unsaved edits: 1 unsaved change in Module Admins" |

## Also held (codex, settings-batch-6, 2026-10-23) - LOW

(7) `ExchangeAdminWeb.Tests/SqliteBusyWaitTests.cs` 58 and `SettingsStoreBusyTests.cs` 128: the `< 9 s` wall-clock
ceiling can false-fail on a slow runner; keep the structural assertions (`busy_timeout`, `DefaultTimeout`) and
loosen the elapsed check to "not the 30 s retry loop". (8) `SettingsSaveAuditTests.cs` 644: the helper tripwire
enumerates catches by `InlineData`, so a new or nested settings-save catch escapes it; discover the catches from
the page source and require the helper for every non-allowlisted one.

## Also held (aud-a66 agent, 2026-10-23) - LOW

(9) `ModuleConfig.razor` `OnParametersSetAsync` reads the `ModuleId` parameter after its awaits; a second route
change during a load's await makes the first call load the newer module too (a double load, consistent, no
write). (10) After a move, the OU browser's load completion sets browser state on the next page (renders only
on AD Attribute Editor; writes nothing). (11) On a moved page the access save's concurrent-change and
directory-refusal messages fall back to alias or SID labels. (12) A save that outlasts the 10 s bound keeps
its worker thread and store connection until the store answers (the stores take no cancellation). (13) Item
(4) above is corrected: a second save in flight combined with a route change caused the aud-a66 HIGH; the
write is now closed there, the re-entrancy guard itself stays held.

## Also held (codex, settings-batch-7, 2026-10-23) - LOW

(14) `ModuleConfig.razor` around 1045: during the bounded route-change save drain no waiting state is shown
for up to 10 s; show "Waiting for <module>'s save before opening <next module>" before the await, clear it on
completion or replace it with the timeout notice.
