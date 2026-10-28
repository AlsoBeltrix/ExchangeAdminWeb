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
**Status**: Verified on branch; awaiting merge and codex review (item 12 not fixed - see the section at the end)
**Branch**: `fix/a64-a67` (from `eb017102`)
**Commit**: `2c52053f` (1), `5d89d3a3` (2), `0fc3dad5` (3), `b1661786` (4) and (13) with `51b2f7fa`, `44d99f3e` (5), `41d3320f` (6), `3cb50d2c` (7), `492725b5` (8), `16392c2a` (9), `ba77f21f` (10), `0a10b484` (11), `7af08914` (14)
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
- (14): `Components/Pages/ModuleConfig.razor`; `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs`.
- (12): nothing changed; see the section at the end.
- (11): `Components/Pages/ModuleConfig.razor`; `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs`,
  `ExchangeAdminWeb.Tests/GroupGrantValidationTests.cs` (one anchor re-anchored, intent unchanged).
- (10): `Components/Pages/ModuleConfig.razor`, `Services/IOrganizationalUnitDirectory.cs` (new),
  `Services/ADOrganizationalUnitService.cs`, `Program.cs`; `ExchangeAdminWeb.Tests/SettingsPageHarness.cs` (a stand-in
  that answers and can hold an enumeration), `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs`.
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

**(9) was superseded on 2026-10-26 by cons-4**, which is on this branch: a load whose module the route has moved past
now applies nothing, so this item's case asserts that outcome instead of the load completing, and its two mutants are
re-run against the source guard cons-4 added. The mechanism below - one read of the parameter, before the first await -
is unchanged and is what cons-4's comparison is made against. See `.agents/review/findings/cons-4.md`.

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

## Guard proof, continued: the items the sections above describe

These rows belong to the Guard proof section and are written here, after the held lists they close, because each item
reads beside the text that raised it. Same driver and same filter.

(10). The OU browser's enumeration is a directory read on a worker, and every line after it applied to whatever module
the page was showing when the answer arrived: the entries, the filtered list, the availability flag, the minimum depth,
and - from `ToggleOUBrowser` - the browser opening at all. The browser renders only on AD Attribute Editor and writes
nothing, so the cost was one module's organizational units shown in another module's browser. The aud-a66 rule applies:
the answer lands only on the load that asked for it (`Holds`), in `LoadOUsAsync`, `ToggleOUBrowser` and `RefreshOUs`.

Two things were decided while doing it, both deliberate:
- `ouLoading` is NOT guarded, and the first draft guarded it. That left the spinner raised on the next module's page -
  a page whose own load lowers it only when it has a browser (AD Attribute Editor) - which is worse than the defect.
  The flag belongs to the enumeration, which has stopped whichever module is shown, so it is cleared either way. The
  case asserts it.
- The page's OU service is now behind `IOrganizationalUnitDirectory`, implemented by `ADOrganizationalUnitService` and
  registered as that same singleton: exactly what aud-a66 did with `IGroupGrantDirectory`, and for the same reason -
  the service is sealed, and the case has to hold an enumeration while the route changes without reaching a directory.
  The page's calls are unchanged, so `ProgressRegistry`'s `OUService.GetOrganizationalUnits` pin stands.

Filter as above, 506 tests. `ModuleConfig.razor` MD5 `de06d5ce8ba6229609b258c9501df7df`, matched after both rows;
506/506 green after each restore.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| J1 `LoadOUsAsync` applies its answer whatever the page shows (the `Holds` return removed) | `671812eed488cd0b836a6e56fb7d83c5` | 1 of 506: `AnOuEnumerationThatOutlastsARouteChange_LeavesTheNextModulesBrowserAlone` - the next module's `ouAllEntries` holding the first module's two OUs |
| J2 `ToggleOUBrowser` opens the browser whatever the page shows (its `Holds` return removed) | `5b94da38f195255bbb6df42cbe790aa6` | 1 of 506: the same case - "the browser opened over the module the page had moved to" |

(11). A save named what it refused from the page's own labels: the alias through `PermissionLabelFor`, which reads the
page's descriptor, and the group through `DisplayNameFor`, which reads the page's display names. Both are replaced by
the next module's load, so a save whose directory lookup outlasted the route change's bound reached the admin - and
the audit record - naming a bare SID, which is the one part of that refusal they can act on. Each save now takes the
display names with everything else before its first await, and names its aliases from the descriptor it captured; the
grant confirmation takes the label function from its caller.

Two things this turned up, both handled rather than worked around:
- The first draft added `AliasName` and `PermissionLabelFor` overloads, which `ProgressRegistryTests` refuses on a
  covered page - that registry is keyed by method name, so an overload cannot be described by it. The descriptor-taking
  ones are `AliasNameOf` and `PermissionLabelOf`.
- `GroupGrantValidationTests.TheAccessSave_ConfirmsNewGrantsBeforeItPersists_AndPersistsTheSnapshot` anchors on the
  confirmation's call text, which now carries the label argument. Re-anchored to `ConfirmNewGroupGrantsAsync(newGrants,`
  with its intent unchanged - the access save confirms before it writes - as aud-a66 re-anchored its neighbours.

The case drives the window past `saveWaitBound`, which is where the labels are actually stale: inside the bound the
route change waits, so the message is built before anything is replaced. It reads the AUDIT record rather than the
banner, because the load clears `statusMessage` as it runs - the recorded refusal is what survives, and it carries the
same text.

Filter as above, 507 tests. `ModuleConfig.razor` MD5 `e6c9db8dd26a9f766557429697338206`, matched after the row;
507/507 green after the restore.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| K1 the access save names its refusals by the page's current display names again (the captured map dropped, `DisplayNameFor` passed) | `8272b5b6c3754b5b5c4c2401536eb9b9` | 1 of 507: `AnAccessSaveRefusedAfterAMove_StillNamesTheGroupItRefused` - the recorded refusal naming the bare SID where the group's name was expected |

A first version of this case was vacuous and the mutant passed: it moved the page inside the bound, so the route
change waited for the save and the message was built while the page still held its own labels.

(14). The route change waits up to `saveWaitBound` - ten seconds - for the saves on the module shown, and showed
nothing for any of it: the operator clicked another module and the page sat on the one they left, with no sign that it
had heard them. It now names what it is waiting for and what it is waiting to open, rendered before the await so the
message is out while the wait runs, and that message is replaced either by the next module's page or by
`UnfinishedSaveNotice` when the bound runs out first.

Filter as above, 508 tests. `ModuleConfig.razor` MD5 `e125bacb33213c64e443bbe7b279f5aa`, matched after the row;
508/508 green after the restore.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| L1 the waiting notice removed (the page silent through the wait again) | `2f737be2374c091b09b24a70cb99c48f` | 1 of 508: `AnotherModuleOpenedWhileASaveRuns_SaysWhatItIsWaitingFor_UntilItOpens` - nothing shown where the wait was expected to be named |

### (12) is not fixed, and why - for the owner

The item is a statement of fact rather than a defect with a fix inside this record's scope: a save that outlasts the
bound keeps its worker thread and its store connection until the store answers, because the stores take no
cancellation. Closing it means threading cancellation through `IConfigStore` and every repository on it, and cancelling
a synchronous SQLite statement needs `SqliteCommand.Cancel()` from another thread - a persistence change, which the
Constitution's Planning Rules put behind a written plan, and well outside what this record describes. This agent did
not start it.

What is true today, after this record: the resource is bounded, not unbounded. Item 4 refuses a second save from the
button while one is in flight, so a load has at most one; `LoadState` clears `saving`, so a route change past the bound
can start one more; and each ends when its store answers - the store's own busy wait is five seconds (item 2 and
aud-a41), so the thread is held for a hung store, not for a contended one. Nothing is written by the late save that
should not be (aud-a66 closed that, and items 9, 10 and 11 close what it still said on the next module's page).

Named for the owner as the one thing in this record left undone.

## Known gaps

- **(12) is not fixed.** Its own section above says why and what is true instead. It is the one item in this record
  left for the owner.
- (2)'s measurement is of `db check` as a process, against a database another process holds exclusively, on this host.
  The remaining wait - about 8 s rather than the 5 s `docs/ConfigDbToolInApp-Plan.md` names - is structural: the open
  and the statement each get one wait, because `busy_timeout` is a PRAGMA and cannot be set until the connection is
  open. That plan is marked `Implemented` and describes the shipped tool, so its "5 s" is now imprecise; this record
  does not edit it, and whoever next touches that plan should either correct the number or point it here.
- (2) is not measured against the shared config database, a second instance, or the ops scripts that call the CLI
  (`tools/SqliteConfigBackup.psm1`): no deploy, promote or cutover script was run, and no PowerShell file changed, so
  the PowerShell gates were not run either.
- (2)'s BitLocker half is a real archive file in a temp folder with a real held lock; no live archive, and no live
  directory.
- (3): the pool drain is awaited on a worker. The pool itself is unchanged, so a drain that never returns still holds
  that worker - the same shape as (12), and bounded by the same thing: the pool's own disconnects.
- (4): the guard is on the Save button, so a save started by any other path is unaffected by design - that is what
  keeps aud-a66's defence meaningful, and its mutants were re-run here to prove it. The two aud-a66 cases that start a
  second save now call `StartSaveAsync` directly; in production that window is reached through a route change past the
  bound rather than a double click.
- (8): the three exemptions are matched by code text - the catch variable's name, and the call each must make. A new
  catch that happens to name its variable `auditEx` is exempted on that name alone, though it must then log and record
  nothing. The scan reads `ProgressScan.CodeView`, which blanks string literals, so no exemption can key on wording.
- (9), (10), (11): each case drives the window on the harness's dispatcher with a held stand-in. No browser, no live
  circuit, and no directory - the OU browser's seam (`IOrganizationalUnitDirectory`) exists so that stays true.
- (11) is proved through the audit record rather than the banner, because the route change clears `statusMessage` as
  it loads. The banner carries the same text when the save finishes first.
- (14)'s notice is not shown when the wait is instant, which is every route change with no save running - the common
  case. Nothing times how long it is visible.
- No settings page was rendered in a browser, and no manual operator check was run for any item here.

## Branch-tip gates - 2026-10-26 (`fix/a64-a67`, from `eb017102`)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 19 warnings - master's own count at this fork, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 6,742 passed / 0 failed / 0 skipped, 4m06s. The
  base at `eb017102` was 6,693, so these two records add 49 cases.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- Not run: `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps`. No `.ps1`, `.psm1` or deploy script changed on this
  branch.
- No directory, Exchange Online, Graph, DHCP, IIS, Delinea or SMTP call, and nothing was deployed. The one process run
  outside the test host is the Release build's own `ExchangeAdminWeb.exe db check` against a temp fixture, for the
  measurement in aud-a67 item 2.

## Reviewer comments (supervisor, settings-batch-8)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 620e9d5a head f08a664a (merged fe67668d, with d0fc4c70); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat f08a664a). Two candidates, admitted as cons-4 (MEDIUM, a stale module load landing after a newer route) and cons-5 (LOW, a dropped second save). Codex found nothing in the log-folder start-up construction and recovery, the telemetry streak reset, the bulk-job range defaults, the Config DB and BitLocker busy-timeout surfacing, or the Exchange Online drain's failure handling. Artefacts: `.agents/review/settings-batch-8.result.txt`, `.stream.log`, `prompt-settings-batch-8.md`.

## (12), under docs/CancellationDepth-Plan.md Part B (2026-10-28)

The plan (approved 2026-10-28) took the item on as its Part B: a save the page has stopped waiting for is cancelled, and a
cancelled write means nothing written. Each slice's section follows; kd-58's record holds Part A.

### B1, the store (implementer, 2026-10-28; lines at this commit)

Read first, at `da9e0a0b`: `IConfigStore.Write` took no token, and `SqliteConfigStore.Write` opened its connection, began
`BEGIN IMMEDIATE`, ran the callback, bumped the change token and committed with nothing between them that a cancel could
reach.

- **The interface** (`Services/Storage/IConfigStore.cs`): two token-taking writes as default members (55, 62), each looking at
  the token once and then calling the token-less write - so the test stand-ins that implement the interface compile
  unchanged (the local precedent is `IBulkJobProcessor.OnJobCompletedAsync`).
- **The store** (`Services/Storage/SqliteConfigStore.cs`): implements both itself (40, 72); the token-less forms pass none. It
  looks at four points and nowhere else (plan 4.4): before the connection opens (C1, 43), before `BEGIN IMMEDIATE` (C2, 45),
  as it returns (C3, 53 - after any busy wait, so the callback never runs) and after the callback and the change-token bump,
  before the commit (C4, 57). A throw at C3 or C4 leaves the `using` with the transaction uncommitted, and its disposal rolls
  it back - the callback's rows and the bump together - so nothing is half-written and the other instance sees nothing.
  After `Commit()` nothing looks: a cancel there is not honoured, the write returns its result, and the store alone decides
  whether a write landed. An internal seam runs after the commit, for the tests (`AfterCommit`, 59, 67; null in the app).
- **Not reached by any token**, as the plan says (4.4, 4.7): the busy wait inside `BEGIN IMMEDIATE` (five seconds) and a call
  blocked inside the database file. A cancel during the wait is seen at C3 when the wait ends; a lock never freed fails busy,
  and busy is what the write reports.
- **The analyzer** (plan section 13, assumption 7, now counted): the overloads made xUnit1051 flag thirteen existing test
  calls on the concrete store - `SqliteConfigStoreTests` (6), `SqliteBusyWaitTests` (2), `ADAttributeEditorServiceTests` (3),
  `AllowlistBannerTests`, `SectionAccessServiceTests`. Each now passes `TestContext.Current.CancellationToken`, which no test
  cancels: no behaviour changes, and the build's warnings stay the baseline 19.
- No caller in the app passes a token yet: that is B2 and B3.

**What the operator sees**: nothing yet - no save hands the store a token until B2.

#### Guard proof - B1

New `ExchangeAdminWeb.Tests/SqliteConfigStoreCancellationTests.cs`, 8 cases, a real store over a temporary database, each
outcome read through a connection of its own, a held lock an unpooled connection's `BEGIN IMMEDIATE`:

- `AWriteWhoseTokenIsAlreadyCancelled_WritesNothing_AndWaitsForNoLock` (both writes): no callback, no row, no token moved, and
  no wait for the held lock.
- `AWriteCancelledDuringTheBusyWait_TheLockThenFreed_RunsNoCallback_AndWritesNothing`: cancelled a second into the wait, the
  lock then freed - a cancellation, the callback never run.
- `AWriteCancelledInsideItsCallback_RollsBackWhole` (both writes): two rows inserted, then cancelled - no row, no token moved.
- `AWriteCancelledAfterItsCommit_ReturnsItsResult_ItsRowsThere`: through the seam - the result returned, the row and the token
  there.
- `AWriteCancelledDuringABusyWaitThatNeverEnds_FailsBusy_NotAsACancel`: `ConfigStoreBusy.IsBusy`, nothing written.
- `TheDefaultRefusesACancelledToken_AndTheAppsStoreImplementsBothTokenWritesItself`: a token-less stand-in's default refuses
  before writing; the interface map shows `SqliteConfigStore` implementing both token writes.

The two busy-wait cases wait by time - a second, inside the five-second wait - for the write to meet the lock; there is no
seam to say it has. Fixed-file MD5s: `Services/Storage/SqliteConfigStore.cs` `05CC9DD2DB266CBE918FA44ED83EF4C2`,
`Services/Storage/IConfigStore.cs` `1ED00B9065378656A6F6067E8D2734CF`. kd-58's harness and method; filter
`FullyQualifiedName~SqliteConfigStoreCancellation`, 8 tests, 8/8 with the fix; every mutant compiled; every restore byte-exact.
C1 and C2 cannot be told apart by a test - nothing observable lies between them - so they are mutated together.

| # | Mutation | Mutant MD5 | Failed (of 8) |
|---|---|---|---|
| B1a | the looks before the open and before BEGIN (C1, C2) removed | `131DC640754860A05CFBDAB5B2A24019` | 2: AWriteWhoseTokenIsAlreadyCancelled_WritesNothing_AndWaitsForNoLock(True); AWriteWhoseTokenIsAlreadyCancelled_WritesNothing_AndWaitsForNoLock(False) |
| B1b | the look as BEGIN returns (C3) removed | `7EFF0F2BAADDABB9F0C231F4E74B0701` | 1: AWriteCancelledDuringTheBusyWait_TheLockThenFreed_RunsNoCallback_AndWritesNothing |
| B1c | the look before the commit (C4) removed | `06AC91F24CCA2915093099898525A45D` | 2: AWriteCancelledInsideItsCallback_RollsBackWhole(False); AWriteCancelledInsideItsCallback_RollsBackWhole(True) |
| B1d | a look added after the commit | `6C534002EDB7A36A3055845A487F6426` | 1: AWriteCancelledAfterItsCommit_ReturnsItsResult_ItsRowsThere |
| B1e | the interface's default write, returning a value, looks at nothing | `AB640659AB93DDFB3094D639B78A285C` | 1: TheDefaultRefusesACancelledToken_AndTheAppsStoreImplementsBothTokenWritesItself |
| B1f | the interface's default write, returning none, looks at nothing | `CFC2E01C2B5FF3B63B5C6FE0A58972F2` | 1: TheDefaultRefusesACancelledToken_AndTheAppsStoreImplementsBothTokenWritesItself |
| B1g | the store's no-result write drops the token | `44AC039D96282B491392AC22AC8FED7B` | 3: AWriteCancelledInsideItsCallback_RollsBackWhole(False); AWriteWhoseTokenIsAlreadyCancelled_WritesNothing_AndWaitsForNoLock(False); AWriteCancelledDuringTheBusyWait_TheLockThenFreed_RunsNoCallback_AndWritesNothing |

Every mutant failed on its own behaviour, never on a count pin.

#### Gates - B1

On this commit's tree, each run alone (2026-10-28): `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental` exit 0,
0 errors, 19 warnings - the baseline set, no xUnit1051 among them; `dotnet test ExchangeAdminWeb.slnx --
xUnit.MaxParallelThreads=4` 8,157 passed / 0 failed / 0 skipped (A10's 8,149 and these 8); `dotnet format
ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0; after `git add -A`, `git diff --check HEAD` exit 0 and
`pwsh -NoProfile -File tools/Test-AsciiOnly.ps1` passed. No PowerShell file changed; nothing deployed; no manual check run.

**Versions**: none. The store is shared - the base app version, by the supervisor's ruling, is 2.36.0's, not yet deployed (the
plan's section 7).

### B2, the four Module Configuration saves (implementer, 2026-10-28; lines at this commit)

Read first, at `f129e5c0`: a route change that ran out of `saveWaitBound` loaded the next module and showed
`UnfinishedSaveNotice` ("if it had not written yet it refuses"), but a save already inside its store call ran on and committed
whenever the store answered - nothing it held could be cancelled.

- **The page's token** (`Components/Pages/ModuleConfig.razor`): `StartSaveAsync` makes one `CancellationTokenSource` per save
  (907) and hands its token to the status, access, configuration and admins saves (910-914); `savesInFlight` carries it (936).
  `WaitForThisLoadsSavesAsync` cancels the source of every save it gives up on, as the bound runs out and before it returns
  their names (994) - the one moment it is cancelled. Not the operator's token: these saves run under no activity
  (`ProgressRegistry` exempts them), and nothing files it as a cancel. Not disposed: it owns no timer.
- **The path** (plan 4.3): each save passes it to its service (1494, 1642, 1808, 2004) -
  `ModuleEnablementService.SetModulesEnabled` (250), `SectionAccessService.SaveEditedAliases` (270),
  `ModuleConfigService.SaveChangedValues` (153, through `DecideChangedValues`; the rolled-back `CompareChangedValues` passes
  none) and `ModuleAdminService.ApplyAdminChanges` (161) - and each service to its repository (274, 333, 223, 168), whose
  write hands it to the store (B1): `ModuleEnablementRepository.SetModules` (124), `SectionAccessRepository.ReplaceAliases`
  (192), `ModuleConfigRepository.UpdateModuleIf` (147), `ModuleAdminRepository.ApplyChanges` (126). Each takes an optional last
  token; every other caller passes none and is unchanged. Nothing after a commit takes it - not the access save's display
  names (`SaveGroupDisplayNames`), the success records or the re-baselines. A service waiting on its own write lock is not
  interrupted there; it meets C1 once it holds the lock. The callbacks' bare catches wrap only reads that take no token, so no
  cancellation is read as an unreadable store.
- **The catch** (plan 4.5, 4.6): each of the four saves has `catch (OperationCanceledException) when
  (pageGaveUp.IsCancellationRequested)` ahead of its general catch (1554, 1689, 1879, 2041). It files
  `Audit.LogSettingsSaveRefused(..., "PageMoved", PageMovedRefusal)` - the record aud-a66 files for the same move before the
  write: result Failed, `refusedAt` PageMoved, `mutationAttempted` false, true by construction since the store rolled back -
  and the save's `finally` names its module over the message, as for every save that ends on another load. A cancellation of
  any other token is not taken by it and stays the save's failure; a busy store the page gave up on during the wait is the
  busy refusal, since the wait takes no token. No new words, record, field or value.
- **Once given up, always refused** (plan 4.2's chosen consequence): a save the page gave up on refuses even if the page
  shows its module again before the save reaches its write - the store looks at the token, not at the page.
- **The tripwire** (`SettingsSaveAuditTests`, aud-a67 (8)): it learned the new catch - every cancellation catch in a settings
  save must be exactly that filter and record through `Audit.LogSettingsSaveRefused(` with `"PageMoved"` and
  `PageMovedRefusal`, read from the page as written since the code view blanks literals; a save that takes the page's token
  must have one - and that `StartSaveAsync` catches nothing (`TheModuleConfigSaveHandOff_CatchesNothing`).
- **The test stand-in** (`AttributeEditorHarness.SwitchableStore`): implements the token writes, forwarding the token to the
  real store so its looks are the ones the saves get; holds the next write inside its transaction (`InsideWrite`, after
  `BEGIN IMMEDIATE`, before the callback) or after its commit (`AfterWrite`); and its token-less writes are its token writes
  with none, so every write meets those holds. `SettingsPageHarness.RunAsync` gives a trailing optional parameter its default,
  so the tests that run a save directly run it as before, with no token. Three source scans name the saves' new signatures and
  calls (`AdminPageDirtyStateTests`, `GroupGrantValidationTests`), and sixteen test calls to the four services now pass
  `TestContext.Current.CancellationToken` (xUnit1051; no test cancels it).

**What the operator sees**: after "A save on X had not finished after 10 seconds ..." - the notice, unchanged - a save that
had not committed ends with "X: Not saved: the page moved to another module while this save waited, so nothing was written.
Open the module again and save.", and nothing is stored. Before this, the same save committed and ended "X: Module admins
saved." (or its tab's success) after the notice had said it would refuse. A save that had committed is reported saved, as
before.

#### Guard proof - B2

In `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs`, through `SettingsPageHarness`, each save with one edit on the first
module (status toggled, a group added to its access and, picked from the directory with its name, a config value set, a group
added to its admins), the route change moving to the second module with a 0.3 s bound:

- **The main case** - `ASaveInsideItsWriteWhenTheRouteChangeGivesUp_WritesNothing_AndIsRefusedAsPageMoved` (4 saves): the
  write held inside its transaction past the bound; the next module opens with the notice; released - nothing stored, the
  change token unmoved, no display name stored, the next module's state its own, one record (Failed, `refusedAt` PageMoved,
  `mutationAttempted` false) and the page showing "X: Not saved: ...".
- **The converted case** - `AnAdminsWriteThatOutlastsTheWait_WritesNothing_AndLeavesTheNextOnesStateAlone`, until B2
  `..._LandsOnItsOwnModule_...`: the admins write held as it opens, before the store (C1) - it used to land after the notice;
  now nothing is written and the move's refusal is recorded.
- **The controls** (4 saves each): `ASaveInsideItsWriteReleasedInsideTheBound_SavesAsBefore` - saved, one success record, the
  route change loading after; `ASaveCommittedBeforeTheRouteChangeGivesUp_IsReportedSaved_WithItsSuccessRecord` - held after
  its commit past the bound: stored, "X: <its success>", one success record, the access save's display name stored, the next
  module's state its own; `ASaveWaitingForABusyStoreWhenTheRouteChangeGivesUp_IsTheBusyRefusal` - another connection holds
  the lock, the page gives up a second into the busy wait, the wait runs out: the busy refusal (`refusedAt` StoreBusy), not a
  page move, nothing stored. The busy cases wait the store's real five seconds, by design.
- **What the token does not replace** - `ASaveTheRouteChangeNeverWaitedFor_IsRefusedByTheMovedCheck_ItsTokenLive` (access,
  admins): a save started while the route change waits at its authorization read is never waited for or given up on, so its
  token stays live; the page moves while it asks the directory, and aud-a66's check before the write refuses it. Added in
  this slice because the first round's re-run of aud-a66's E3 survived (below).
- `SettingsSaveAuditTests`: the tripwire's new rules, and `TheModuleConfigSaveHandOff_CatchesNothing`.

**Reproduction**: with the page as committed before B2 (`Components/Pages/ModuleConfig.razor` at `f129e5c0`, MD5
`50AF05492C3336456AEE927FD30DEE4E`) and everything else as here, `SettingsModuleMoveTests` failed 5 of 43 - the main case for
all four saves and the converted case, each with the late write stored - and passed the controls; the page was restored
byte-exact (`2B49AB173F0FC6B2BFB96862D0AE2F36`).

kd-58's harness and method; the filter `FullyQualifiedName~Settings|...~GroupGrantValidationTests|...~AdminPageDirtyStateTests|
...~SqliteConfigStoreCancellationTests|...~Allowlist|...~PageAuthorizationRecheckTests`, 427 tests (429 with the
never-waited case), green on the fix before and after every mutant; every mutant compiled and every restore was byte-exact.
Fixed-file MD5s: `Components/Pages/ModuleConfig.razor` `2B49AB173F0FC6B2BFB96862D0AE2F36`, `Services/ModuleEnablementService.cs`
`C66030D30CE12ED4F6C54693C170A635`, `Services/SectionAccessService.cs` `8717DBC928E9CA6C1F9CAE2DFB7375CA`,
`Services/ModuleConfigService.cs` `70CAC7F774DD2ECE01E5895A12A28091`, `Services/ModuleAdminService.cs`
`73481DE043A97677166288CFFF45C6D7`, `Services/Storage/ModuleEnablementRepository.cs` `04C330EE4E1CB7C8C1444AEF717857A2`,
`Services/Storage/SectionAccessRepository.cs` `D4114FFFF5A5F5AF322C8587A5DB67DE`, `Services/Storage/ModuleConfigRepository.cs`
`F811D0AAC9FBF91BA6B179A52A5FB29C`, `Services/Storage/ModuleAdminRepository.cs` `1C23B3F56368503AE0C44354FE5700A6`,
`Components/Pages/AdminSettings.razor` `F5A6704E45BBB2651BA473D23061D961`, `Components/Pages/ExchangeOnlineConfig.razor`
`0279099DB992F685E5D313C17E5E5EEF`. In the table, "main", "converted", "committed", "busy" and "never waited" are the cases
above, and "tripwire" is `EveryRefusalAndCatchInASettingsSave_WritesItsRecord` for the save named.

| # | Mutation | Mutant MD5 | Failed (of 427) |
|---|---|---|---|
| B2a | the give-up cancel removed: the route change gives up on a save and cancels nothing | `4DDDCF45E865495F30DC4DAA96E3E61A` | 5: main (all four saves); converted - each late write stored |
| B2b | the status save does not pass its token to the service | `F36C4CBD2B11A218000CFBE0F9DA4A83` | 1: main (Module Status) - the toggle stored |
| B2c | the access save does not pass its token to the service | `BE027545FEE3FB3D6E89165B48E13D35` | 2: main (Section Access); `GroupGrantValidationTests.TheAccessSave_ConfirmsNewGrantsBeforeItPersists_AndPersistsTheSnapshot` (its call pin) |
| B2d | the configuration save does not pass its token to the service | `4261BA9014110595E9B95A764EFA2C35` | 1: main (Configuration) - the value stored |
| B2e | the admins save does not pass its token to the service | `3563E249BDDFE54FB00B1F53BADFC0D3` | 3: main (Module Admins); converted; `GroupGrantValidationTests.TheModuleAdminSave_ConfirmsNewGrantsBeforeItPersists_AndPersistsTheSnapshot` (its call pin) |
| B2f | the status save's cancel catch removed | `B8B09BB8928D6C21A9883B6E0D0F5BCB` | 2: main (Module Status) - filed as a failure, no `refusedAt`; tripwire (status) - "takes the page's token and has no catch for it" |
| B2g | the access save's cancel catch removed | `DF53BB2044371D64AEE36DA2FF62B2EE` | 2: main (Section Access), as B2f; tripwire (access) |
| B2h | the configuration save's cancel catch removed | `D172C08A9F8603A91DF734F9D3D6A873` | 2: main (Configuration), as B2f; tripwire (configuration) |
| B2i | the admins save's cancel catch removed | `0A7E8F0EF2133B641E38554066E3E31B` | 3: main (Module Admins), as B2f; converted; tripwire (admins) |
| B2j | `StartSaveAsync` hands the status save no token | `62AFF38C0C09B4AC9020584B3731C6CF` | 1: main (Module Status) |
| B2k | `StartSaveAsync` hands the access save no token | `B86D1FDBFFAA43DBAA618514A3BBAD44` | 1: main (Section Access) |
| B2l | `StartSaveAsync` hands the configuration save no token | `2B3B9D232AB7B1B34A73B7F58C98466D` | 1: main (Configuration) |
| B2m | `StartSaveAsync` hands the admins save no token | `EEBECF40468B308917DED315279B343B` | 2: main (Module Admins); converted |
| B2n | the configuration save's cancel catch takes any cancellation (its filter dropped) | `5DAC444A26FF1822FDCA9F8F1609BE47` | 1: tripwire (configuration) - the filter is not the page token's. No behaviour case can tell: these saves take no other token |
| B2o | the status save's cancel catch takes any exception once the page gave up | `7521B756070148DEAB6C3E8A6875C642` | 2: busy (Module Status) - `refusedAt` PageMoved where StoreBusy was expected; tripwire (status) |
| B2p | the enablement service does not pass the token to its repository | `80D6FA5C0B109E3CAD0F356272190FC8` | 1: main (Module Status) |
| B2q | the access service does not pass the token to its repository | `65172238850DD7642F84DCFFD1710FE7` | 1: main (Section Access) |
| B2r | the config service does not pass the token to its repository | `AE09C4D06A954C85A3C3ED77F829D995` | 1: main (Configuration) |
| B2s | the admin service does not pass the token to its repository | `7D2B114BD6BBE1C50ADECB74A255B1D4` | 2: main (Module Admins); converted |
| B2t | the enablement repository does not pass the token to the store | `1441D65727152A2A7AC2A9AF81354190` | 1: main (Module Status) - the toggle stored |
| B2u | the access repository does not pass the token to the store | `C0E505F87808E2442590CF5D4402A13E` | 1: main (Section Access) |
| B2v | the config repository does not pass the token to the store | `13A66380FFD884FA8A5D70BE203B3394` | 1: main (Configuration) |
| B2w | the admin repository does not pass the token to the store | `8511F3F1BB39095C92CB21CAADF607D0` | 2: main (Module Admins); converted |
| E1 | aud-a66 E1 re-run: only the latest save kept (`savesInFlight.Clear()` before the add) | `08CD1B46DBA648FB43555E825695D183` | 1: `ASecondSaveThatFinishesFirst_DoesNotLetAnotherModuleLoadUnderTheFirst` - "the page loaded another module while the first of two saves still waited for the directory" |
| E2 | aud-a66 E2 re-run: the route change's wait looks once (`if`, not `while`) | `68EA5AF3D2B4B390ECBEA9D2C43D2EB9` | 12: main and committed (all four saves); converted; `AnAdminsSaveWhoseLookupOutlastsTheWait_...`, `ASaveStartedWhileAnotherModuleWaitsToLoad_...`, `ASaveThatNeverFinishes_...` |
| E3 | aud-a66 E3 re-run: the access save's moved refusal removed (`if (false)`) | `5158ABC055B4D883A683306DDC0A2007` | **0 - survived** (first round, below) |
| E4 | aud-a66 E4 re-run: the admins save's baseline reset with no load check (`if (true)`) | `9A055E879AFB39B08BC8A08B6B803EEB` | 1: committed (Module Admins) - the second module's baseline became the first's snapshot. Until B2 the converted case caught it; that write no longer lands |
| D1 | aud-a67 D1 re-run: `ModuleConfig.SaveCurrentTabAsync`'s guard removed (`if (false)`) | `E71A5F52E985127F93D07703C3B59A2D` | 1: `SettingsSaveOffCircuitTests.ASecondSavePressedWhileTheFirstRuns_IsRefused_AndTheStoreSeesOneWrite(Module Configuration)` |
| D2 | aud-a67 D2 re-run: `AdminSettings.SaveCurrentTabAsync`'s guard removed (`if (false)`) | `23E21999D40E808FDD8447FB13C48E51` | 1: the same case (Admin Settings) |
| D3 | aud-a67 D3 re-run: `ExchangeOnlineConfig.SaveExoConfig`'s guard removed (`if (false)`) | `76A0AC3511E2218C18ACE4E2640E471C` | 1: the same case (Exchange Online Config) |
| E3r | E3 again, with the never-waited case | `5158ABC055B4D883A683306DDC0A2007` | 1 of 429: never waited (Section Access) - the first module's access stored after the page moved |
| E3s | its twin: the admins save's moved refusal removed (`if (false)`) | `F2AC9E8E8D6D29B98D41CF97AA3C8B85` | 1 of 429: never waited (Module Admins) - the first module's admins stored after the page moved |

**E3's first round.** It survived because every moved case in this file was one the route change had given up on: with the
access save's moved check removed, the save went on to the store with its token already cancelled, met C1 and filed the same
PageMoved refusal, so nothing changed. The token now covers every save the route change gives up on, but not one it never
waited for - a save started while the route change waits at its authorization read, after its wait for this load's saves -
and there the moved check is the only refusal. The never-waited case pins it, and E3 and its admins twin fail on it.

#### Gates - B2

On this commit's tree, each run alone (2026-10-28): `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental` exit 0,
0 errors, 19 warnings - the baseline set, no xUnit1051 among them; `dotnet test ExchangeAdminWeb.slnx --
xUnit.MaxParallelThreads=4` 8,176 passed / 0 failed / 0 skipped (B1's 8,157 and these 19); `dotnet format
ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0; after `git add -A`, `git diff --check HEAD` exit 0 and
`pwsh -NoProfile -File tools/Test-AsciiOnly.ps1` passed. No PowerShell file changed; nothing deployed; no manual check run -
no settings page was rendered in a browser.

**Versions**: none. The Module Configuration page and the four settings services are app-wide; the base version is 2.36.0's,
not yet deployed (the supervisor's ruling; the plan's section 7).

### B3, the attribute allowlist save (implementer, 2026-10-28; lines at this commit)

Read first, at `d2ef22bf`: the allowlist save is the fifth save on Module Configuration, and its records are its service's
(aud-a28l). `ADAttributeEditorService.SaveAllowlistAndAudit` decides inside one store write, records every outcome and
returns it without throwing; the page records only its own authorization re-check and the move before the write (aud-a66).
It took no token, so a save the route change had given up on still committed whenever the store answered.

- **The token's path** (plan 4.3): `StartSaveAsync` hands the allowlist save the page's token (`ModuleConfig.razor` 913), the
  save passes it to `SaveAllowlistAndAudit` (2470; the save's signature 2414), the service to
  `AttributeEditorRepository.ReplaceAllowlist` (`ADAttributeEditorService.cs` 449, 468), and the repository to the store's
  write (`AttributeEditorRepository.cs` 109, 129; B1). Optional last parameters; every other caller passes none. Nothing after
  the commit takes it - not the cache invalidation, not the success record.
- **The service's own refusal** (plan 4.6): `SaveAllowlistAndAudit` catches a cancellation of its own token ahead of the catch
  that records a failure (470) and records `LogAllowlistSaveRefused(performedBy, ip, "PageMoved",
  SettingsSaveAudit.PageMovedRefusal)` - result Failed, section `ADAttributeEditor:Allowlist`, `refusedAt` PageMoved,
  `mutationAttempted` false - and returns it as a refusal. The page writes nothing for it: it shows the result it is given,
  named for its module, as for every outcome that ends on another load. The words moved to
  `SettingsSaveAudit.PageMovedRefusal` (`Services/SettingsSaveAudit.cs` 65) so the page's saves and the service say the same;
  the page's constant is now that one (`ModuleConfig.razor` 965). A busy store the page gave up on during the wait is still
  the service's busy refusal.
- **The tripwire and a pin**: `SettingsSaveAuditTests` holds that the allowlist save catches no cancellation - its records are
  the service's; `AllowlistSaveAuditTests.ASaveThePageGaveUpOn_IsTheServicesOwnPageMovedRefusal_ForItsOwnTokenOnly` holds the
  service's catch to its own token, ahead of the failure's, filing PageMoved in the shared words - the shape no case can tell
  from a catch that takes any cancellation. Two source scans name the save's new signature (`AllowlistConcurrentSaveTests`,
  `AllowlistSaveAuditTests`), and twelve test calls to `SaveAllowlistAndAudit` pass `TestContext.Current.CancellationToken`
  (xUnit1051).

**What the operator sees**: as B2 - after the notice, an allowlist save that had not committed ends "AD Attribute Editor: Not
saved: the page moved to another module while this save waited, so nothing was written. Open the module again and save.",
and the allowlist is as it was. Before this it committed and ended "AD Attribute Editor: The attribute allowlist was saved."

#### Guard proof - B3

`SettingsModuleMoveTests`' B2 cases take the allowlist save as their fifth: on the AD Attribute Editor, a row added
(`department`, Department), the same four cases - inside its write past the bound (nothing stored, one record, written by the
service, "AD Attribute Editor: Not saved: ..."), released inside the bound (saved), committed before the give-up (reported
saved, its success record), and busy (the busy refusal) - with the pin above. **Reproduction**: with the page as committed
before B3 (MD5 `2B49AB173F0FC6B2BFB96862D0AE2F36`, B2's) and everything else as here, the allowlist's main case failed - the
row stored after the page moved - beside five source scans that read the save by its new signature; the page was restored
byte-exact (`A1308F2059D9C7B4550A4D24DC89CF05`).

kd-58's harness and method; the B2 filter with `|FullyQualifiedName~ADAttributeEditor`, 587 tests, green on the fix; every
mutant compiled; every restore byte-exact. Fixed-file MD5s: `Services/ADAttributeEditorService.cs`
`A8A1C731737E04441939BC90EBC65DA6`, `Services/Storage/AttributeEditorRepository.cs` `ED58692CECD66B855883199803ADCF28`,
`Components/Pages/ModuleConfig.razor` `A1308F2059D9C7B4550A4D24DC89CF05`. "Main" is
`ASaveInsideItsWriteWhenTheRouteChangeGivesUp_WritesNothing_AndIsRefusedAsPageMoved(Editable Attributes)`, "the pin" the
`AllowlistSaveAuditTests` case above.

| # | Mutation | Mutant MD5 | Failed (of 587) |
|---|---|---|---|
| B3a | the service's cancel branch removed | `F52D4280F89DFA44286008B0087E08C7` | 2: main - a Failed record with no `refusedAt`, the plan's named mutation; the pin |
| B3b | the allowlist save does not pass its token to the service | `2765A888D48C0EE6865BE793DC244C95` | 1: main - the row stored |
| B3c | `StartSaveAsync` hands the allowlist save no token | `1FD9F63C546603466FB016A50482452F` | 1: main |
| B3d | the service does not pass the token to its repository | `4B4F39F7E3E8C2BD7C2C746B80BE3712` | 2: main - the row stored; the pin |
| B3e | the repository does not pass the token to the store | `DB3644B986E19BD09CED6910CFC4A634` | 1: main |
| B3f | the service's cancel catch takes any cancellation (its filter dropped) | `12C217ED8B92C0CE5439BCAB00549E8B` | 1: the pin - no behaviour case can tell, as for B2n |
| B3g | the page catches the allowlist save's cancellation itself, recording it a second time | `50DCD1A4B5EA1924EFFE411BEF328E10` | 1: the tripwire (allowlist) - "the page catches none" |

#### Gates - B3

On this commit's tree, each run alone (2026-10-28): `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental` exit 0,
0 errors, 19 warnings - the baseline set, no xUnit1051 among them; `dotnet test ExchangeAdminWeb.slnx --
xUnit.MaxParallelThreads=4` 8,181 passed / 0 failed / 0 skipped (B2's 8,176 and these 5); `dotnet format
ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0; after `git add -A`, `git diff --check HEAD` exit 0 and
`pwsh -NoProfile -File tools/Test-AsciiOnly.ps1` passed. No PowerShell file changed; nothing deployed; no manual check run.

**Versions**: AD Attribute Editor 1.7.4 -> 1.7.5 (`Modules/ModuleCatalog.cs` 1683). No base bump: 2.36.0, not yet deployed,
absorbs the shared text in `SettingsSaveAudit` and the Module Configuration page's part (the plan's section 7).
