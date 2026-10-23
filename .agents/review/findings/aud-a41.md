# aud-a41: The settings store's busy wait is about 35 seconds, not the documented 5, and the settings handlers block their circuit for the whole wait

**Severity**: LOW/MEDIUM - operator experience and a wrong documented fact. `SqliteConnectionFactory`
sets `busy_timeout=5000` but no `Default Timeout`, so Microsoft.Data.Sqlite's 30-second command timeout
wraps the 5-second busy wait and a held write lock makes a save wait about 35 seconds (measured by the
aud-a28v agent: 35.2 s) before it throws SQLITE_BUSY. The factory's doc comment and the aud-a28r record
say 5 seconds. The settings handlers call the store synchronously after `await Task.Yield()`, so the
Blazor circuit is blocked for the whole wait. The aud-a28v test holds a real lock and therefore adds about
35 seconds to the suite.
**Status**: Complete; merged `86df002a`; codex settings-batch-6 raised one MEDIUM (folded into aud-a66) and two LOWs (held in aud-a67)
**Branch**: `worktree-agent-a885c578973c1cad3`
**Commit**: `bcef5fec` (plan `6db3a417`; comments `f219f2e8`)
**Reviewer**: implementer (surfaced by the aud-a28v/a26s/a28w agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At `a583777d`: `Services/Storage/SqliteConnectionFactory.cs` lines 46-50 (no Default Timeout), line 77
(`busy_timeout=5000`), doc comment lines 54-56; `.agents/review/findings/aud-a28r.md` lines 52 and 168;
`Components/Pages/AdminSettings.razor` around line 1091 (the synchronous store call after `Task.Yield`);
`ExchangeAdminWeb.Tests/SettingsStoreBusyTests.cs` (the 35 s wait).

## Approach (to be written by the implementing agent before code)

- One wait, documented and enforced: set the connection's `Default Timeout` so the total busy wait equals
  the documented value (5 s; or document a deliberately chosen value and make both agree); a test pins the
  measured wait to within a tolerance of the documented one.
- The settings handlers run the store call off the circuit (`Task.Run` or the store's async surface) so
  the page stays responsive and the aud-a28v message appears when the wait ends.
- Correct the factory doc comment and the aud-a28r record's two lines; the aud-a28v busy test shrinks to
  the documented wait.
- Mutations at the call site: the `Default Timeout` removed (the pin fails); the synchronous call restored.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `6972a5c5`. One commit `fix(storage): a held write lock
is waited out once, for the documented 5 seconds, and the settings saves wait for the store off the circuit (aud-a41)`, first on
this branch (then aud-a40, aud-a42). Behaviour: the shared connection factory and the three settings pages (base app version),
and Exchange Online Config (the Exchange Online module's version); the supervisor owns both bumps.

Evidence re-read at `4830230a`, and measured rather than assumed - a throwaway console probe in the scratchpad (never
committed) against Microsoft.Data.Sqlite 10.0.7 / SQLite 3.49.1, the factory's PRAGMA batch applied as the factory applies it,
the lock held by a second unpooled connection's `BEGIN IMMEDIATE`:

- today's factory (no `Default Timeout`): `BEGIN IMMEDIATE` failed SQLITE_BUSY (code 5, extended 5) after 34.16 s (aud-a28v
  measured 35.2 s; aud-a33's mutation run 34 s);
- `Default Timeout` 5, and 1, each with `busy_timeout=5000`: 5.56 to 5.58 s, four runs each - one native busy wait, nothing added;
- the process timer at 1 ms (`timeBeginPeriod(1)`): the native wait itself 5.07 to 5.97 s, `Default Timeout` 5 and 1 alike;
- a 20-million-row recursive CTE (4.83 s) completed under `Default Timeout` 1: the provider's command timeout bounds its busy
  retry, not a statement's run time;
- a lock released after 3 s: the waiting `BEGIN IMMEDIATE` began at 3.07 s (`Default Timeout` 1 or 5); held 4.8 s: began at 5.00 s;
- a factory-style open and read while another connection holds the write lock: 5 ms, reading the committed state (a WAL reader
  never waits for a writer).

So: SQLite's busy handler waits `busy_timeout` (5 s) inside each step; Microsoft.Data.Sqlite re-steps a busy statement after
150 ms while its command timeout (`Default Timeout`, 30 s when unset) has not passed; six rounds make about 35 s.

1. **One wait (`SqliteConnectionFactory`).** A public `BusyTimeout` of 5 s; the PRAGMA is built from it (`busy_timeout=5000`, the
   value unchanged); the connection string sets `Default Timeout` to 1 second - the provider's retry ceiling, below the busy wait,
   so once SQLite's own wait has failed the provider never starts another. Not 5: the native wait's nominal total is exactly
   `busy_timeout`, so with a ceiling equal to it, whether a second 5-second wait starts would rest on Windows' sleeps overshooting
   (they did, every run - 5.07 s at the least - but that is the platform timer, not a guarantee). A busy result SQLite returns
   without waiting is retried for up to 1 s, a retry can still wait once, so the worst case is about 6 s, where a ceiling of 5
   allows 10 or more. The documented wait is 5 s (5.6 s measured on this host: the Windows sleep granularity).
   **No recorded reason for another value** was found. aud-a33's known gap (a long migration could outlast the wait) and the other
   two databases this factory serves were checked: migration steps are DDL, the SID migration's directory lookups run outside the
   lock (aud-a33b), the jobs repository's transactions are single statements, and the usage database's writes are single
   statements - nothing holds a write lock for more than milliseconds. The config, jobs and usage databases and both migrators get
   the documented wait. Architectural Invariant 2: dev and prod wait for each other's writes 5 s, not 35; a settings save meeting
   the other instance's lock for longer is refused (aud-a40 makes every save say so), and a start-up migration meeting the other
   instance's step waits up to 5 s for a step that takes milliseconds.
2. **Off the circuit.** Every settings save's store write - the calls that take the write lock and so can wait - runs through
   `await Task.Run(...)`, so the circuit is free while it waits and the save's message appears when the wait ends: Admin Settings
   `SetModulesEnabled` (SaveEnablement), `ExtendedLog.SetLevel` (SaveLogLevel), `CompareDirectoryReadSecretId`, `ReplaceConfig` and
   `SaveDirectoryReadSecretId` (SaveProtectedPrincipals; `SaveDirectoryReadSecret` becomes async); Exchange Online Config
   `SetModulesEnabled` (SaveModuleStatus) and `SaveChangedValues` (SaveExoConfig); Module Configuration `SaveChangedValues`
   (SaveConfigAsync), `SetModulesEnabled` (SaveModuleEnablementAsync), `SaveEditedAliases` and `SaveGroupDisplayNames`
   (SaveSectionAccessAsync), `ApplyAdminChanges` (SaveModuleAdminsAsync), `SaveAllowlistAndAudit` (SaveAllowlistAsync - the call
   only; `ADAttributeEditorService` is not touched). The reads before them (the corrupt re-checks) stay: measured, a read does not
   wait for a held write lock. The lambdas capture locals only. `ExoPool.DrainPool` is not a store wait and stays (Known gaps).
3. **A save writes what the page held when its first store call began, and its bookkeeping follows what it wrote.** A free
   circuit lets the admin edit while a save waits. So each save takes what it writes, and what its bookkeeping needs, from the page
   before its first store call; afterwards the page's baseline becomes what was saved and the unsaved count is recounted against it
   - the Section Access and Module Admins saves' rule ("the baseline is what was SAVED, so a row added during the lookup still
   counts as an unsaved edit"), already used where they await the directory. An edit made during the wait stays on the page,
   counted unsaved, for the next save. SaveEnablement and SaveModuleEnablementAsync: the baseline takes the toggles written, then the
   count (the reload after them then warns about an edit made during the save, rather than dropping it). SaveLogLevel: the level is
   a local, for the record and the message. SaveProtectedPrincipals: the rules and the secret are taken before the compare (the
   rules' config moves above it); the already-applied record names the entries of what was written; the lists take the rows held
   under the lock (aud-a26n) only when no edit landed during the save; the rules' baseline is what the store now holds; the secret's
   field takes the stored value only while it still holds what was saved; the section is recounted. SaveModuleStatus: the baseline
   takes the toggle written. SaveConfigAsync: the baseline takes the values written. SaveAllowlistAsync (a tally, not a diff): the
   count keeps the edits made during the save. SaveExoConfig, SaveSectionAccessAsync and SaveModuleAdminsAsync already work this way.
   No re-entrancy guard: a second save started while one waits (a double click before the disabled Save renders) is decided by the
   stores under their locks, as two tabs' saves are.
4. **Docs.** The factory's doc comments say what the wait is and why the provider's timeout is set; `ConfigStoreBusy`'s remarks lose
   "about 35 seconds"; the aud-a28v comment in SaveProtectedPrincipals; `SettingsStoreBusyTests`' remarks; aud-a28r.md lines 52 and
   168 corrected in place.
5. **Registries.** ClickGateRegistry: the three pages NotYetConverted, no line keys; nothing re-anchors (the ClickGate tests run
   anyway). ProgressRegistry: SaveExoConfig's pinned calls stay inside its activity; the exempt SaveCurrentTabAsync entries are still
   config-store writes.

Tests:
- New `ExchangeAdminWeb.Tests/SqliteBusyWaitTests.cs`: a write through `SqliteConfigStore` meeting a write lock another connection
  holds fails SQLITE_BUSY after the documented wait - at least 4.5 s and under 9 s (a second wait would pass 10; today's is 34); a
  factory connection runs with `busy_timeout` equal to `BusyTimeout` and a command timeout below it, read from the opened connection
  (deterministic); a lock released after 1 s lets the write through; a statement running past the command timeout completes.
- `SettingsStoreBusyTests`: the aud-a28v case now waits the documented wait, asserted under 9 s.
- New `ExchangeAdminWeb.Tests/SettingsSaveOffCircuitTests.cs` through `SettingsPageHarness`: each save's write held at its store's
  `BeforeWrite` (no lock, no wait); while held, an edit on the page's dispatcher completes at once (the circuit is free); released,
  the save completes, the store holds only what it wrote, and the edit is still on the page and counted unsaved where the page
  counts. One case per save above, the protected-principals save twice (its compare, its rules' write). The harness gains an action
  on the dispatcher, `RunAsync` with arguments, and the allowlist's repository behind a switchable store.

Mutations at the call site: (1) the `Default Timeout` removed - the pin fails; (2) SaveProtectedPrincipals' `ReplaceConfig` back on
the circuit (called inline) - its off-circuit case fails; extra (E1) `Default Timeout` equal to the busy wait (5) - the structural
pin fails; (E2) SaveProtectedPrincipals' rules baseline from the page's lists again - the late edit is counted saved; (E3)
SaveConfigAsync's write back on the circuit; (E4) SaveConfigAsync's baseline from the page's state again.

## Fix

`Services/Storage/SqliteConnectionFactory.cs`: `BusyTimeout`, 5 seconds; the PRAGMA's `busy_timeout` is built from it (5000, as
before) and each connection's `DefaultTimeout` - the provider's command timeout, 30 when unset - is set to the same 5 seconds, on
the connection rather than in the connection string. The doc comments say what the one wait is and why both settings make it.

`Services/Storage/ConfigStoreBusy.cs`: the remarks give the wait as `BusyTimeout`, 5 seconds (about 35 before).

`Components/Pages/AdminSettings.razor`: SaveEnablement, SaveLogLevel and SaveProtectedPrincipals (its compare, `ReplaceConfig`, and
the secret's write in `SaveDirectoryReadSecretAsync`, formerly `SaveDirectoryReadSecret`) run each store write through
`await Task.Run(...)`. SaveEnablement takes its toggles before the try and uses them for the write, every record
(`EnablementSections(changes)` in place of `PendingEnablementSections()`) and the baseline, then recounts. SaveLogLevel's level is a
local. SaveProtectedPrincipals takes the rules' config, the loaded secret and the rules' version before the compare; the
already-applied record names the entries of what was written (`ChangedRuleEntries(newConfig)`); the lists take the rows held under
the lock only while `ListsHold(newConfig)`; the rules' baseline becomes what the store holds (`SetRulesBaseline`); the section is
recounted after the secret; the secret's baseline becomes the stored value, and its field only while it still holds what was saved.
`SnapshotProtectedBaseline` had no caller left and is removed.

`Components/Pages/ExchangeOnlineConfig.razor`: SaveModuleStatus (the toggle a local, the baseline from it) and SaveExoConfig (the
write's arguments built on the circuit) off the circuit.

`Components/Pages/ModuleConfig.razor`: SaveConfigAsync (the baseline takes each written value, then the count), SaveModuleEnablementAsync
(the baseline from the toggle written, then the count), SaveSectionAccessAsync (both writes), SaveModuleAdminsAsync and
SaveAllowlistAsync (the tally keeps the edits made during the save) off the circuit. `SaveCurrentTabAsync` keeps the save it started
in `saveInFlight`, and `OnParametersSetAsync` waits for it before it loads another module.

Tests: new `ExchangeAdminWeb.Tests/SqliteBusyWaitTests.cs` (5 cases) and `ExchangeAdminWeb.Tests/SettingsSaveOffCircuitTests.cs` (12);
`SettingsStoreBusyTests`' page case asserts its wait; `SettingsPageHarness` gains `RunAsync` with arguments, `OnDispatcherAsync` and
the allowlist's store behind a switch (`AllowlistStore`). Three source tripwires re-anchored, their intent unchanged:
`AdminPageDirtyStateTests` (the two reload saves now settle the count by a recount, still before the render and the reload),
`GroupGrantValidationTests` (the persist calls sit inside `Task.Run`; `moduleId`), `AllowlistConcurrentSaveTests` (the loaded version
passed through a local).

Departures from the plan, inside its scope:

1. **`Default Timeout` is 5, not 1.** Measured after the plan, with the same probe: a new database file's switch to WAL fails at
   once while another connection holds a lock - SQLite applies no `busy_timeout` to it, set before it or after - so the provider's
   retry is the only wait that statement has. At `Default Timeout` 1 the open failed after 1.10 s, where the unset 30-second retry
   outlasted a 2-second lock. At 5 every busy path waits the documented 5 seconds: a held lock is SQLite's one wait (never under
   5.07 s here, so the provider finds its 5 seconds passed and starts no second wait), and a statement SQLite fails without waiting is
   retried for 5. The plan's reason against 5 - a native wait finishing under 5.000 s would start a second one - did not happen in 12
   runs at either timer resolution; the pin's bound (under 9 s) fails if it ever does. E1 below is the plan's value.
2. **Module Configuration finishes a save before it shows another module.** The page is reused when its route moves to another
   module (`/module-config/{ModuleId}`, and NavMenu links each one), so with the write off the circuit the admin can open another
   module while a save waits; that save's bookkeeping - baseline, count, banner flags, and the secret masking in its success record,
   which reads the page's descriptor - would have landed on the other module's state. One wait in `OnParametersSetAsync` covers every
   save on the page. It also closes the same window the access and admins saves already had while their directory confirmation runs
   (raised in the hand-back).
3. **The off-circuit cases start each save from another thread** (found in the round, below).
4. **The command timeout is set on each connection, not in the connection string.** The first gate run put it in the string and
   failed 207 tests, all in `Comms10kRefusalTests`: its fixture clears the file's pool by rebuilding the plain connection string,
   which no longer matched the factory's pool, so the pooled handles kept its temp folder from being deleted. No production code
   clears a pool; `AttributeEditorHarness` and `SettingsPageHarness` clear it the same way, silently (their deletes are best
   effort). On the connection, the pool's key is the plain string again. Pinned by
   `AFactorysPooledConnections_AreReleasedByClearingThePoolOfThePlainConnectionString` (E7 below).

Caller-visible change, by design: a write that meets a lock held elsewhere waits 5 seconds and fails busy, where it waited about 35 -
on every database the factory opens (config, jobs, usage) and in both migrators; dev and prod wait for each other 5 seconds. The
three settings pages stay usable while a save waits for the store, and an edit made then stays on the page, counted unsaved, for the
next save. Module Configuration finishes a save before it shows another module.

## Guard proof

Session-local driver `a885-mutate.ps1` in the scratchpad (a name of its own; never committed): the fix staged; MD5; mutate by exact
text (one match required); touch; `dotnet build` of the test project; `dotnet test --no-build` filtered to 41 classes
(`a885-filter-a41.txt`: the new and changed classes, every settings-page suite, the store, migrator, repository and service suites over
the factory, the fixtures that clear the factory's pool, and the source scans that read these pages - 1317 tests); `git checkout --`
from the index; touch; MD5 re-confirmed; rebuild; the same filter. Every mutant built with 0 errors, every file came back to its MD5,
and the restored tree ran 1317/1317 after each row. All nine rows ran on this commit's tree, in one pass.

| # | Mutation (call site) | MD5 before | MD5 mutant | MD5 after | Named test failing |
|---|---|---|---|---|---|
| 1 | `SqliteConnectionFactory.Open`: the connection's `DefaultTimeout` no longer set | `42ED78D643DA6FE413171BB262FE7DFB` | `C2595784CA4C5BD2492540B81F093A3D` | `42ED78D643DA6FE413171BB262FE7DFB` | `SqliteBusyWaitTests.AWriteThatMeetsAHeldLock_FailsBusy_AfterTheDocumentedWait` ("the write waited 34.87 s: more than one busy wait of 5 s"), `AFactoryConnection_WaitsTheBusyTimeout_AndTheProviderRetriesNoLonger` (5 expected, 30), `SettingsStoreBusyTests.AdminSettings_ASecretIdCompareThatFindsTheStoreBusy_...` ("the save waited 35.08 s") |
| 2 | `AdminSettings.SaveProtectedPrincipals`: `ReplaceConfig` back on the circuit (called inline) | `9D5372C65B32823CC132CB438C7615EB` | `082BE935373903849E45F72C702BDED2` | `9D5372C65B32823CC132CB438C7615EB` | `SettingsSaveOffCircuitTests.AdminSettings_AProtectedSaveWhoseRulesWriteWaits_LeavesTheCircuitFree_AndARuleAddedMeanwhileStaysUnsaved` ("an edit on the page waited for the save's store write: the write held the circuit") |
| E1 | extra, `SqliteConnectionFactory.Open`: `DefaultTimeout` 1, below the busy wait (the plan's value) | `42ED78D643DA6FE413171BB262FE7DFB` | `9185B5DEFDD02F757878649879269531` | `42ED78D643DA6FE413171BB262FE7DFB` | `AFactoryConnection_...` (5 expected, 1), `ANewDatabaseFilesSwitchToWal_WaitsForALockAsLongAsAnyOtherStatement` (SQLITE_BUSY out of the open) |
| E2 | extra, `AdminSettings.SaveProtectedPrincipals`: the rules' baseline from the page's lists again (`SnapshotProtectedRulesBaseline()`) | `9D5372C65B32823CC132CB438C7615EB` | `7E9DD9193EDC38C692432EC5867584A8` | `9D5372C65B32823CC132CB438C7615EB` | `AdminSettings_AProtectedSaveWhoseRulesWriteWaits_...` (baseline holds the late rule; `["svc-*"]` expected - the late rule counted as saved) |
| E3 | extra, `ModuleConfig.SaveConfigAsync`: the write back on the circuit | `C1F8471E9E381946DE1C84CCEE924158` | `C1CA91B6629B629A3668544BD335679F` | `C1F8471E9E381946DE1C84CCEE924158` | `ModuleConfig_AConfigSave_LeavesTheCircuitFree_AndAValueTypedMeanwhileStaysUnsaved`, `ModuleConfig_AnotherModuleOpenedWhileASaveWaits_LoadsOnlyOnceTheSaveIsDone` (the edit waited) |
| E4 | extra, `ModuleConfig.SaveConfigAsync`: the baseline from the page's state again (`ClearSection`, whole copy) | `C1F8471E9E381946DE1C84CCEE924158` | `EC75CBB0EC9BAFF4FE98A25C6F3C62CF` | `C1F8471E9E381946DE1C84CCEE924158` | `ModuleConfig_AConfigSave_...` (count 0, 1 expected) |
| E5 | extra, `ModuleConfig.OnParametersSetAsync`: the wait for the save in flight removed | `C1F8471E9E381946DE1C84CCEE924158` | `C92C379A6232E4F05D9C74F16A030BC3` | `C1F8471E9E381946DE1C84CCEE924158` | `ModuleConfig_AnotherModuleOpenedWhileASaveWaits_...` ("the page loaded another module while a save on this one waited") |
| E6 | extra, `ExchangeOnlineConfig.SaveExoConfig`: the write back on the circuit | `E74CD16F58599AB1D16B3AD8B1FF9BE6` | `34F80A782909BDD922E695C54506C162` | `E74CD16F58599AB1D16B3AD8B1FF9BE6` | `ExchangeOnlineConfig_AConnectionSave_LeavesTheCircuitFree_AndAFieldTypedMeanwhileIsNotTakenAsSaved` (the edit waited) |
| E7 | extra, `SqliteConnectionFactory`: `Default Timeout` back in the connection string (the first gate run's shape) | `42ED78D643DA6FE413171BB262FE7DFB` | `E783D259A1543AE67B831A8FD8175BB0` | `42ED78D643DA6FE413171BB262FE7DFB` | `SqliteBusyWaitTests.AFactorysPooledConnections_AreReleasedByClearingThePoolOfThePlainConnectionString`, and `Comms10kRefusalTests` (206 cases: its fixture could not delete its folder) - 207 in all |

**Mutation 2 survived the first draft of the off-circuit cases**: all 1040 then passed against it, that case in 30 s. A throwaway probe
in the test project (deleted) traced why: the harness's renderer dispatcher ran the save, and the continuation it posted to itself at
`await Task.Yield()`, on the thread that invoked it, until the save truly yielded - so with the write inline the held write blocked the
test's own thread, the hook timed out after 30 s, and the edit then ran unopposed. The cases now start each save from another thread
(`Task.Run(save)`), so only the dispatcher is held. The MD5s are this commit's bytes; the code comments are written after the round, in
their own commit.

## Gates - 2026-10-23 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 5,922 passed /
  3 skipped (the three known AD skips) / 0 failed, 5,925 total, 5m47s. No hang. The first run of this gate, with the command timeout
  in the connection string, failed 207 (departure 4).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

The suite's wall time did not drop with the busy case: 5m47s here against 5m16s at the base's branch-tip gate. The aud-a28v case
itself now takes about 5.6 s where it took 35 (mutation 1 shows both); the suite's wall time is set by other classes and is not
comparable run to run on this host, which other agents share.

No directory, Exchange Online, Graph, DHCP, IIS, Delinea or SMTP: the harness leaves the directory services unset, and no case adds
a group grant or a rule that is looked up. The pages were not rendered in a browser. No coder self-review verdict is claimed.

## Known gaps

- Measured on this host, at the default and at a 1 ms timer resolution; nothing ran against the shared database or a second
  instance - two connections on one temp file stand in for the two instances.
- A host whose sleeps returned early could see SQLite's wait finish under 5.000 s, and the provider would then start a second wait
  (about 10 s). Not observed in 12 runs; `AWriteThatMeetsAHeldLock_...` fails if it happens.
- `ExoPool.DrainPool` still runs on the circuit after a connection save: Exchange Online round trips, not a store wait.
- An edit made during one of the two enablement saves is kept and counted, so the reload that follows them asks whether to leave
  with an unsaved change - by design, but new.
- No re-entrancy guard: a second save started while one waits (a double click before the disabled Save renders) is decided by the
  stores under their locks, as two tabs' saves are; the first to finish clears `saving`.
- A failed access save's record lists the access sections pending when it failed (`PendingAccessSections()` in its catch), which can
  now include an edit made while the save waited.
- The pages ran on the framework's dispatcher through the harness; no browser or live circuit ran.

## Branch-tip gates - 2026-10-23 (`f219f2e8`: the three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 5,933 passed /
  3 skipped (the three known AD skips) / 0 failed, 5,936 total, 5m46s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- The comments commit `f219f2e8` changes comment lines only: the code lines of all 19 source files the fixes touch, whole-line comments
  and blank lines stripped, hash the same at `086501ad` and `f219f2e8`.