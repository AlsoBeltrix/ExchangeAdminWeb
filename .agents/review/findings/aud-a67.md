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
- (9): `Components/Pages/ModuleConfig.razor`; `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs`.
- (8): `ExchangeAdminWeb.Tests/SettingsSaveAuditTests.cs`.
- (7): `ExchangeAdminWeb.Tests/SqliteBusyWaitTests.cs`, `ExchangeAdminWeb.Tests/SettingsStoreBusyTests.cs`.
- (6): `Services/ADAttributeEditorService.cs`, `Modules/ModuleCatalog.cs` (AD Attribute Editor 1.7.2 -> 1.7.3);
  `ExchangeAdminWeb.Tests/SettingsStoreBusyTests.cs`.
- (5): `ExchangeAdminWeb.Tests/SettingsSaveAuditTests.cs`.
- (4), with (13): `Components/Pages/ModuleConfig.razor`, `Components/Pages/AdminSettings.razor`,
  `Components/Pages/ExchangeOnlineConfig.razor`; `ExchangeAdminWeb.Tests/SettingsSaveOffCircuitTests.cs`,
  `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs` (two aud-a66 cases start their second save through the new seam).
- (3): `Components/Pages/ExchangeOnlineConfig.razor`, `Modules/ModuleCatalog.cs` (Exchange Online 1.0.4 -> 1.0.5);
  `ExchangeAdminWeb.Tests/SettingsSaveOffCircuitTests.cs`, `ExchangeAdminWeb.Tests/AttributeEditorHarness.cs` (a
  `BeforeRead` hook beside the store stand-in's `BeforeWrite`, so a test can hold a read).
- (2): `Services/Storage/ConfigDbMaintenance.cs`, `Services/BitLockerRecoveryService.cs`, `Modules/ModuleCatalog.cs`
  (BitLocker Recovery 1.2.2 -> 1.2.3); `ExchangeAdminWeb.Tests/ConfigDbMaintenanceTests.cs`,
  `ExchangeAdminWeb.Tests/BitLockerRecoveryTests.cs`.

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

### The measured wait (2)

Measured, not inferred, on this host on 2026-10-26, against the Release build of the app
(`bin/Release/.../ExchangeAdminWeb.exe db check <path>`) as a process - the way `tools/SqliteConfigBackup.psm1` runs it.

The fixture is a database another **process** holds so that no other connection may even read it: a `sqlite3` shell with
`PRAGMA cache_size=1`, `BEGIN IMMEDIATE` and an `UPDATE` over 20,000 rows, so SQLite spills the page cache to the file
and takes an EXCLUSIVE lock for the rest of the transaction. This matters and was found by measuring: a plain
`BEGIN IMMEDIATE` takes RESERVED, which a reader passes - the first two attempts at this measurement returned `ok` in
0.3 s because nothing was actually blocking the read.

| | run 1 | run 2 | run 3 |
|---|---|---|---|
| before the fix (`busy_timeout=5000`, no command timeout) | 30.5 s | 30.6 s | - |
| after (both from `SqliteConnectionFactory.BusyTimeout`) | 9.4 s | 7.8 s | 7.8 s |

Both exited 2 with `error: SQLite Error 5: 'database is locked'.`

So the docs' 5 s was wrong by about six times, and is now wrong by about one and a half. The remainder is structural and
is left as it is: the connection's open and the statement after it are two phases, and each gets one wait. The busy
timeout is a PRAGMA, so it can only be set once the connection is open; during the open itself SQLite's own wait is 0
and only the provider's retry applies, bounded by the command timeout - about 5 s - and the `integrity_check` then waits
its own 5 s. A caller that met a held database therefore waits up to about twice the one wait, not six times it.
`docs/ConfigDbToolInApp-Plan.md`'s "5 s" is an `Implemented` plan's description of the shipped tool rather than a
contract; this record does not edit it (see Known gaps).

(2). Filter `FullyQualifiedName~ConfigDbMaintenanceTests|FullyQualifiedName~ConfigDbCliTests|FullyQualifiedName~SqliteBusyWaitTests|FullyQualifiedName~BitLockerRecovery|FullyQualifiedName~ModuleCatalogTests`
(161 tests). `ConfigDbMaintenance.cs` MD5 `3c572241700e6932af484a85d44fa247`, `BitLockerRecoveryService.cs`
`514bd4790bfd897cf152e53bdc5f777c`, each matched after its row; 161/161 green after each restore. Each mutant is that
connection as it was at `2c52053f`, so each failure is the defect's reproduction - and the in-process number matches the
process measurement above.

| Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|
| B1 `ConfigDbMaintenance.OpenReadOnly`'s command timeout removed | `e0c3f4304fe3b7431439e77a66c29103` | 2 of 161: `OpenReadOnly_TakesTheOneBusyWait_AsBothItsBusyTimeoutAndItsCommandTimeout` (`DefaultTimeout` 30 where 5 was expected) and `Check_OfADatabaseHeldByAnotherConnection_GivesUp_WithoutTheProvidersThirtySecondRetry` ("the check waited 30.3 s") |
| B2 `BitLockerRecoveryService`'s archive command timeout removed | `defb8e71c7c2246457a743e40aab7625` | 1 of 161: `BitLockerRecoveryTests.A_held_archive_is_reported_without_the_providers_thirty_second_retry` ("the search waited 30.3 s") |

(3). Filter `FullyQualifiedName~SettingsSaveOffCircuitTests|FullyQualifiedName~SettingsStoreTextTests|FullyQualifiedName~SettingsSaveAuditTests|FullyQualifiedName~ProgressRegistryTests|FullyQualifiedName~ClickGate|FullyQualifiedName~ModuleCatalogTests|FullyQualifiedName~ExoConnectionPool`
(551 tests). `ExchangeOnlineConfig.razor` MD5 `c4375890674270d8d7b2e046a2cf5457`, matched after the row; 551/551 green
after the restore. The mutant is the call as it was at `5d89d3a3`, so its failure is the reproduction. The existing
drain tests were the constraint and all pass unchanged: `ProgressRegistry` pins `ExoPool.DrainPool(`, which the awaited
call still carries, and `SettingsStoreTextTests.ExchangeOnlineConfig_AFailingDrainAfterASavedConnection_...` still gets
its own warning and its own audit action. A wider run over every settings-page and pool suite was green at 843/843.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| C1 `SaveExoConfig`'s drain back on the circuit (`ExoPool.DrainPool();`) | `7dcc7c11d68cb12021be7f280f332a48` | 1 of 551: `SettingsSaveOffCircuitTests.ExchangeOnlineConfig_AConnectionSavesPoolDrain_LeavesTheCircuitFree` - "an edit on the page waited for the pool drain: the drain held the circuit" |

### (4), and what it must not weaken (13)

The guard is at the event, not inside each save: `ModuleConfig.SaveCurrentTabAsync`, `AdminSettings.SaveCurrentTabAsync`
and Exchange Online Config's two handlers (that page wires its buttons straight at them) return at once while `saving`.
Every save already set `saving` before its first await, so the second click of a double click reads it.

Item (13) is the reason it is placed there. aud-a66 (HIGH) defends a save that is ALREADY in flight when a route change
happens, and two of its cases start a second save deliberately; a guard inside the registration would have made that
scenario unreachable and taken the bite out of aud-a66's E1 and E2 mutants. So `SaveCurrentTabAsync` keeps the guard and
hands off to a new internal `StartSaveAsync`, which is the registration and the await exactly as they were. aud-a66's two
cases start their second save through `StartSaveAsync` and are otherwise untouched. That window is still reachable in
production, which is why the defence is still worth testing: a route change that outlasts its 10 s bound loads the next
module and `LoadState` clears `saving`, so a new save can begin while the older one is still running.

Proof that nothing was weakened: aud-a66's E1 and E2 mutants were re-run on this tree, and both still fail their own
cases (the last two rows below).

The commit that landed this (`b1661786`) was made with `dotnet format` failing: its new case's `switch` blocks were
indented one level short, and the gate's result was read after the commit rather than before it. `51b2f7fa` is the
whitespace fix, the formatter's own, no code line changed; the gate, the build and the case pass on it. The two
commits are one sub-item. This agent does not amend without the owner's authority, so the mistake stands in the
history with its correction behind it.

(4). Filter as above, 497 tests. `ModuleConfig.razor` MD5 `583eb30f590dff56f2996d1205ddf022`, `AdminSettings.razor`
`f43b9b413ebb98947002eb0c6176137f`, `ExchangeOnlineConfig.razor` `25138a2776cb02b2b1baaca8889bd9ba`, each matched after
its rows; 497/497 green after every restore. D1 to D3 are each page as it was at `0fc3dad5`.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| D1 `ModuleConfig.SaveCurrentTabAsync`'s guard removed | `91be35984e8927082e84057c983d2168` | 1 of 497: `ASecondSavePressedWhileTheFirstRuns_IsRefused_AndTheStoreSeesOneWrite(page: "Module Configuration")` - two writes where one was expected |
| D2 `AdminSettings.SaveCurrentTabAsync`'s guard removed | `3247bb53c501e83d72096d6ae844483c` | 1 of 497: the same case for "Admin Settings" - two writes |
| D3 `ExchangeOnlineConfig.SaveExoConfig`'s guard removed | `95d77fa2f2c7242d654271fb632a1ed1` | 1 of 497: the same case for "Exchange Online Config" - two writes |
| aud-a66 E1 re-run (`savesInFlight.Clear()` before the add) | `d4d0a3efc8218684d687086fb63277c1` | 1 of 497: `SettingsModuleMoveTests.ASecondSaveThatFinishesFirst_DoesNotLetAnotherModuleLoadUnderTheFirst` - "the page loaded another module while the first of two saves still waited for the directory" |
| aud-a66 E2 re-run (the route change's wait looks once, `if` not `while`) | `18bc49f26d57dbeac9d869f8d3271220` | 3 of 497: `ASaveStartedWhileAnotherModuleWaitsToLoad_IsWaitedForAsWell`, `AnAdminsSaveWhoseLookupOutlastsTheWait_...` and `ASaveThatNeverFinishes_...` (no notice) |

(5). The comment said `SetLevel` "logs and swallows" a store failure, so only the authorization call could reach the log
level save's catch. It has rethrown the store's exception since aud-a28k, which is what lets a level that did not
persist be reported as a failure rather than a success. The comment is corrected and the case is now a theory that
drives both ways in - the authorization store, and the store the level is written to. Filter
`FullyQualifiedName~SettingsSaveAuditTests|FullyQualifiedName~ExtendedLogServiceTests|FullyQualifiedName~SettingsStoreBusyTests`
(95 tests); `ExtendedLogService.cs` MD5 `71edf225678dd7c9b8818b7e3803f335`, matched after the row; 95/95 green after the
restore.

| Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|
| E5 `SetLevel` swallows the store's failure again (its `throw;` removed) - the world the stale comment described | `45b89105881f8e27ca84f067c8f70a23` | 3 of 95: the new row `AdminSettings_ALogLevelSaveThatThrows_IsRecordedAsAFailure(fails: "the settings store the level is written to")` (recorded Success where Failed was expected), `ExtendedLogServiceTests.SetLevel_WhenTheStoreRefusesIt_AndTheLoggerThrows_ThrowsTheStoresFailure`, and `SettingsStoreBusyTests.AdminSettings_ALogLevelSaveThatFindsTheStoreBusy_...` |

(6). The exact change aud-a40's "Left for a follow-up" section specified, in the file it could not touch. Filter
`FullyQualifiedName~SettingsStoreBusyTests|FullyQualifiedName~AttributeEditor|FullyQualifiedName~Allowlist|FullyQualifiedName~ModuleCatalogTests`
(312 tests). `ADAttributeEditorService.cs` MD5 `3f99e333f43b35c941c71efd058573ee`, matched after the row; 312/312 green
after the restore. The mutant is the catch as it was at `44d99f3e`, so its failure is the reproduction.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| F1 `SaveAllowlistAndAudit`'s busy branch removed | `3e80d5e67efb1fd328fc0054253ef5ac` | 1 of 312: `SettingsStoreBusyTests.ModuleConfig_AnAllowlistSaveThatFindsTheStoreBusy_IsRefusedAsBusy_AndWritesNothing` - the record's `refusedAt` missing where `StoreBusy` was expected (and the admin shown "Failed to save: SQLite Error 5") |

(7). Confirmed by the supervisor, not speculative: these two ceilings failed repeatedly on this host over the weekend,
with several agents building and testing at once. Both were one second under two busy waits - 9 s - which is a timing
rather than a signal, since a 5-second wait plus scheduling can cross it with nothing wrong. Each is now 25 s, which
still fails the thing the assertion exists for (the provider's retry loop, measured at 30 to 35 s) and nothing else;
the structural assertions that cannot drift with load are kept and are named in the comment as what actually pins the
wait's shape - `AFactoryConnection_WaitsTheBusyTimeout_AndTheProviderRetriesNoLonger` reads back `busy_timeout` and
`DefaultTimeout`.

Proof the loosened ceilings still bite: the aud-a41 defect itself was put back - `SqliteConnectionFactory.Open`'s
`DefaultTimeout` removed - and both still fail, at 34.4 s. Filter
`FullyQualifiedName~SqliteBusyWaitTests|FullyQualifiedName~SettingsStoreBusyTests` (25 tests);
`SqliteConnectionFactory.cs` MD5 `42ed78d643da6fe413171bb262fe7dfb`, matched after the row; 25/25 green after the
restore.

| Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|
| G1 `SqliteConnectionFactory.Open`'s `DefaultTimeout` removed (the aud-a41 defect back) | `c2595784ca4c5bd2492540b81f093a3d` | 3 of 25: `SqliteBusyWaitTests.AWriteThatMeetsAHeldLock_FailsBusy_AfterTheDocumentedWait` ("the write waited 34.38 s"), `SettingsStoreBusyTests.AdminSettings_ASecretIdCompareThatFindsTheStoreBusy_...` ("the save waited 34.39 s"), and the structural `AFactoryConnection_WaitsTheBusyTimeout_AndTheProviderRetriesNoLonger` (`DefaultTimeout` 30 where 5 was expected), which fails in 11 ms whatever the load |

(8). The tripwire listed nine handlers in `InlineData` and looked at the FIRST catch of each, so a save added to one of
these pages - or renamed - was simply absent from it, and a second or nested catch escaped it. It now discovers every
`Task Save*(` declaration in the three pages' source and checks every `catch (Exception ...)` in each: 13 handlers
instead of 9, and every catch instead of one. Three exemptions are named with their reason, and each is held to what
the exemption is for rather than waved through:

- a catch named `auditEx` - the audit write that follows a completed save, which must not make it look failed
  (Constitution, Auditing And Tracing): it must log and record nothing;
- `catch (Exception drainEx)` in `SaveExoConfig` - a failed pool drain is not a failed save, so it has its own audit
  action (aud-a26k): it must use `Audit.LogConnectionPoolDrainFailed(`;
- `SaveAllowlistAsync` - its records are the attribute-editor service's (aud-a28l and item 6 above), refusals included.

The marker for the first is the variable name, not the log line: these scans read `ProgressScan.CodeView`, which blanks
string literals. Two of the three exemptions were found BY the widened guard, not assumed: the drain catch and the
allowlist save's refusal returns both failed it on the first run, and each was read and admitted on its own reasons.
A save with neither try nor catch - `SaveCurrentTabAsync` since item 4, and `StartSaveAsync` - is required to have no
catch and to write no record by hand.

Filter `FullyQualifiedName~SettingsSaveAuditTests` (52 cases, up from 48). `AdminSettings.razor` MD5
`f43b9b413ebb98947002eb0c6176137f`, `ExchangeOnlineConfig.razor` `25138a2776cb02b2b1baaca8889bd9ba`, each matched after
its row; 52/52 green after each restore. Both mutants are defects the OLD tripwire would have passed, which is the
point of the item.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| H1 `SaveDirectoryReadSecretAsync` (a handler the old list did not name) writes its failure by hand | `92816b21219cb34b9e7b85750a3d75d9` | 1 of 52: `EveryRefusalAndCatchInASettingsSave_WritesItsRecord(AdminSettings.razor, SaveDirectoryReadSecretAsync)` - no `Audit.LogSettingsSaveException(` in its catch |
| H2 `SaveExoConfig`'s drain catch (a SECOND catch in a handler the old list did name) stops using its own audit action | `5b0323e9c65007b16d8726ab763c02d5` | 1 of 52: the same case for `SaveExoConfig` - no `Audit.LogConnectionPoolDrainFailed(` in the drain's catch |

(9). `OnParametersSetAsync` read the `ModuleId` parameter after its awaits, twice: the descriptor
(`Catalog.GetById(ModuleId)`) after the authentication state, and the delegated-admin grant
(`ModuleAdmin.IsModuleAdmin(ModuleId, user)`) after the authorization re-check. The router sets that parameter again on
the next route change, so a second change inside either window made the load fetch the newer module's descriptor, or
decide this page with another module's grant - and the route change that asked for the newer module then found nothing
to do, so one route change's work was done twice and the other's not at all. The module is taken into `requested`
before the first await and used for all of it, the early-return compare included.

Filter as above, 505 tests. `ModuleConfig.razor` MD5 `c9fa8768373c056bed7ed97b1164a3f0`, matched after both rows;
505/505 green after each restore. Each mutant restores one of the two reads as it was at `492725b5`.

A first attempt at this case was vacuous and is worth recording: it held the authorization re-check only, which sits
AFTER the descriptor read, and it left the operator a global admin, so the grant check never ran. Both mutants passed
under it. The case is a theory over the two windows now - the authentication state for the descriptor, the
authorization re-check for the grant, with the operator a module admin of the first module only - and each row fails
its own mutant.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| I1 the descriptor read from the parameter again (`Catalog.GetById(ModuleId)`) | `3c5b2a2d9171647d68edb5dc6e53a189` | 1 of 505: `ARouteChangeDuringALoad_LeavesTheLoadOnTheModuleItWasAskedFor(held: "the authentication state")` - the third module shown where the second was asked for |
| I2 the delegated-admin check read from the parameter again (`IsModuleAdmin(ModuleId, user)`) | `f0c0d8e3d50e22707a2598d74a454e9b` | 1 of 505: the same case for "the authorization re-check" - the grant checked for the module the page had moved to, so the load it was asked for was refused |

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
