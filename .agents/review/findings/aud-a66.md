# aud-a66: Module Configuration can apply one module's admin-group or section-access diff to another module when the operator opens a second module during the directory lookup

**Severity**: HIGH - a wrong authorization write (the module admin groups decide who may administer a
module). `SaveModuleAdminsAsync` reads `module.Id` and `moduleAdminGroupsBaseline` AFTER
`await ConfirmNewGroupGrantsAsync` (the directory lookup); the page instance is reused across
`/module-config/{ModuleId}` routes, so opening another module's configuration during the lookup
(NavMenu) applies module A's group diff, computed against module B's baseline, to module B through
`ApplyAdminChanges(module.Id, ...)`. `SaveSectionAccessAsync` has the same window. aud-a41's in-flight
save wait closes the Save-button path as a side effect, but the directory-lookup window is not closed and
no test pins it (the harness has no directory stand-in). Read from the code, not run.
**Status**: Fixed on branch, with the folded-in MEDIUM; closed on guard proof; not merged; codex review pending (approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule)
**Branch**: `worktree-agent-a047027df146de3ac`
**Commit**: `0267d70d` (plan `a8647062`; comments `653a800a`)
**Reviewer**: implementer (surfaced by the aud-a41/a40/a42 agent, 2026-10-23, candidate 1 of its hand-back)

## Evidence

At `86df002a`: `Components/Pages/ModuleConfig.razor` `SaveModuleAdminsAsync` (around 1548-1574 at
`4830230a`) and `SaveSectionAccessAsync` (around 1400-1423), the awaits before `module.Id` and the
baselines are read; `Components/Layout/NavMenu.razor` 66/118 (the route change); the aud-a41 in-flight wait.

## Approach (to be written by the implementing agent before code)

- Every save captures the module id, the baseline and the edited values into locals BEFORE its first await
  (the aud-a41 rule, applied to the directory lookup too); after the await, if the page's current module is
  no longer the captured one, the save REFUSES ("the page moved to another module while the lookup ran;
  nothing was written") with one refusal record; otherwise it writes the captured diff to the captured
  module and sets the baseline only if the page is still on that module.
- The route change itself waits for an in-flight save or lookup (the aud-a41 wait extended to the lookup),
  so the window is closed from both sides.
- Tests through `SettingsPageHarness` with a directory stand-in that holds the lookup while the route
  changes: nothing written to module B; module A's save refused or completed against A only. Mutation at the
  call site: `module.Id` read after the await again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"), within the 2026-10-23 stopping rule (HIGH; the folded-in item
MEDIUM); admitted at `bba6bb2b`, the folded-in item at `3fd9f0d0`. Branch `worktree-agent-a047027df146de3ac` from `ae112c83`;
master moved to `be41a71f` meanwhile and none of its commits touches a file below except this record, whose master text this
one starts from. One fix commit `fix(module-config): ... (aud-a66)`, then a comments commit. Behaviour: Module Configuration,
the shared settings page (base app version; the supervisor owns the bump).

Evidence re-read at `ae112c83` (its line numbers). The page instance is reused across `/module-config/{ModuleId}` and
`OnParametersSetAsync` (948-999) loads the next module into it. aud-a41's wait (952-957) covers one save:
`SaveCurrentTabAsync` (866-879) keeps only the latest in `saveInFlight`, so a second save started before the first finishes
(a double click before the disabled Save renders - aud-a67 item 4 - or a direct event) takes its place; the wait is checked
once, so a save started while a route change waits is not waited for; and it has no bound (the folded-in item). Every save
reads `module.Id`, its baseline, its edits and the page's load flags after `await Task.Yield()` and the authorization awaits;
the access and admins saves read `policyAliases`/`accessBaseline` (1438-1442) and `moduleAdminGroupsBaseline`/`module.Id`
(1595-1597) after `ConfirmNewGroupGrantsAsync`, and `ApplyAdminChanges(moduleId, ...)` takes the id read there. After its
write each save re-baselines and recounts the page's state and sets its corrupt flags; the config save masks secrets and
names keys in its record by the page's descriptor (`MaskIfSecret`, `IsSecretField`, `ConfigKeyName`); the allowlist save
re-checks authorization with `IsModuleAdmin(module!.Id, ...)` after its awaits (1996). And `OnParametersSetAsync` assigns
the next module (968) before an await (983) and its state after it (`LoadState`, 992), so a save started between them pairs
one module's id with the other's state. The windows: (a) a second save in flight, (b) a save started during the wait, (c) a
save started while a load awaits, and, once the wait is bounded, (d) a save still running when the bound runs out.

1. **Every save takes what it needs before its first await** - SaveConfigAsync, SaveModuleEnablementAsync,
   SaveSectionAccessAsync, SaveModuleAdminsAsync, SaveAllowlistAsync: the descriptor shown and its id, the page's load
   number, its record section, what it writes (the edits; the snapshot the directory confirms), what it diffs against (the
   baselines), the load flags that qualify them (`moduleConfigCorrupt`, `enablementStoreCorrupt`, `sectionAccessCorrupt`,
   `moduleAdminsUnreadable`), the pending sections its records name, and the allowlist's rows, version and count. Every later
   step uses these: the authorization re-check (`IsModuleAdmin(moduleId, ...)`), the store checks, the grants the lookup
   confirms, the write, and its records (secrets masked and keys named by the captured descriptor).
2. **A save whose page moved refuses.** Immediately before its store write - after the directory confirmation in the access
   and admins saves - a save whose page no longer shows the module it took writes nothing and leaves one refusal record:
   `LogSettingsSaveRefused` with `refusedAt: PageMoved` and `mutationAttempted: false` under its own section (the allowlist
   save through `LogAllowlistSaveRefused`, the only record path its page uses). Message: "Not saved: the page moved to another
   module while this save waited, so nothing was written. Open the module again and save.", named as in item 3. The check is
   the module id: the write is the captured diff, decided row by row under the write lock, so whether it may go ahead depends
   on its target, not on whether the page re-read that module.
3. **A save's page state lands only on the load it began on.** `LoadState` is the one place the page's module and state are
   replaced, and it counts the load. After its write a save changes page state - baselines, counts, corrupt flags, the
   allowlist's version and banner, `saving`, the status save's reload - only while the page still holds that load. Its
   records are written either way. On another load, its message is prefixed with its module's display name, so it cannot be
   read as being about the module shown.
4. **The route change waits for every save, for a bounded time.** `SaveCurrentTabAsync` registers each save with its load
   and module name until it completes (`savesInFlight`, replacing `saveInFlight`). `OnParametersSetAsync` waits for every
   unfinished save of the load it is about to replace, looking again after each finishes (so a save started meanwhile is
   waited for too), for at most `saveWaitBound`: `SqliteConnectionFactory.BusyTimeout` plus 5 s, 10 s - the store's one busy
   wait (measured up to 5.97 s, aud-a41) with room for the rest of a save. A directory confirmation can take longer (up to
   30 s per new grant on the runspace lock). When the bound runs out the next module loads and shows a notice naming the
   unfinished save's module; that save then refuses (item 2) or, if it was writing, lands on its own module without touching
   the page (item 3), and a later route change does not wait for it again: it belongs to an earlier load. `LoadState` clears
   `saving`, so the new module's Save is offered.
5. **A module is shown together with its state.** `OnParametersSetAsync` takes the next module into a local and assigns it
   with `LoadState`, after its awaits; until then the page keeps the previous module and that module's state, so a save
   started while a load awaits takes one module's id with that module's state, and item 2 refuses it if the load lands first.
6. **A directory stand-in.** New `Services/IGroupGrantDirectory.cs` (`IsAvailable`, `ValidateGroupSid`), implemented by
   `ADDirectorySearchService` and registered in `Program.cs` as that singleton - `IOperatorDirectory`'s shape, the service
   being sealed. The page injects it; its calls are unchanged, so the `ADSearch.ValidateGroupSid(` anchors in
   `GroupGrantValidationTests` and `ProgressRegistry` stand. `SettingsPageHarness` gives the page a stand-in (`Directory`)
   that answers each SID from the test, records it and can hold a lookup until released; nothing reaches a directory.
7. **Registries and tripwires.** ClickGateRegistry: ModuleConfig.razor NotYetConverted ("tier 2, not approved"), no line
   keys; the ClickGate tests run anyway. ProgressRegistry: no handler added; `OnParametersSetAsync` keeps its exemption (the
   wait is bounded and opens no activity). Source tripwires re-anchored with their intent unchanged where a captured local
   replaces a field (`GroupGrantValidationTests`' admins-save anchors). `SettingsSaveAuditTests`'
   `EveryRefusalAndCatchInASettingsSave_WritesItsRecord` covers the new refusal returns.
8. **Not changed.** The stores and services, the seam aside; aud-a67's held items, (1) `dirty` surviving a move and (4) no
   re-entrancy guard among them: this fix adds no guard, it makes a second save harmless to a route change.

Tests, new `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs` through `SettingsPageHarness` and its stand-in, with a short
`saveWaitBound` where the bound is the subject:
1. An admins save's lookup held while another module is opened: the load waits; released, the grant lands on the first
   module, and the second is untouched and loads its own state.
2. Two admins saves, the second finishing while the first's lookup is held: the route change waits for the first too;
   nothing reaches the second module. (Expected to fail at `ae112c83`: the executed reproduction.)
3. A save started while a route change waits (held in its write): the route change waits for it as well.
4. An admins save whose lookup outlasts the bound: the next module loads after the bound with the notice; released, the save
   refuses - one PageMoved record, nothing written to either module, the next module's lists and baseline untouched, the
   message naming the first module.
5. The same for the access save.
6. A save that never completes (its write held to the end of the test): the next module loads after the bound with the
   notice and its Save offered; a second route change does not wait for it.
7. An admins write that outlasts the bound, then released: it lands on its own module with one success record; the next
   module's lists and baseline stay its own; the message names the first module.
8. A save started while a route change's load awaits (the route's authorization held): it takes the module still shown and
   that module's state, writes there, and nothing reaches the module being loaded.
9. The config, status and allowlist saves whose page moves during their authorization re-check: each refuses with one
   PageMoved record and writes nothing.
10. The bound is the busy timeout plus 5 s; `Program.cs` registers the seam as the directory singleton.

Mutations at the call site: (1) SaveModuleAdminsAsync: `module.Id` read after the lookup again (the captured id taken from
the page after `ConfirmNewGroupGrantsAsync`) - test 4 fails (the second module's admins written); (2) OnParametersSetAsync:
the bound removed - test 6 fails (the route change never completes). Extras: (E1) only the latest save waited for (aud-a41's
single field) - test 2; (E2) the wait not looked at again after a save finishes - test 3; (E3) SaveSectionAccessAsync's moved
refusal removed - test 5; (E4) SaveModuleAdminsAsync's baseline reset with no load check - test 7; (E5) the next module
assigned before the load's await again - test 8; (E6) `LoadState` no longer clears `saving` - test 6.

## Executed reproduction - 2026-10-23

The fix's seam, harness stand-in and tests in place, and `Components/Pages/ModuleConfig.razor` as it was at `ae112c83`
swapped in (`git show`, MD5 `FCAED5839DBD656C6A389FE2176E7A66`) with one line changed - its `@inject` taking the new seam,
which the harness's stand-in needs (MD5 `50DD3DCAF4ABF037C6497E806217A3CA`; a diff against the base shows that line only);
build 0 errors. Of the four cases that need neither the bound nor the new refusal, three failed and one passed:

- `ASecondSaveThatFinishesFirst_DoesNotLetAnotherModuleLoadUnderTheFirst`: the second module's stored admin groups, sorted,
  began `S-1-5-21-1000-2000-3000-6001` - the first module's loaded group - where only `...-6003`, the second module's own,
  was expected. The first save's lookup was released after the route change had loaded the second module (the wait had
  followed only the second save, finished), and it applied the first module's list, diffed against the second module's
  baseline, to the second module: an authorization write on the wrong module.
- `ASaveStartedWhileAnotherModuleLoads_SavesTheModuleStillShown_AndNothingReachesTheOther`: the second module's stored
  groups, sorted, began `...-6002` - the group added on the first module's page - where only `...-6003` was expected.
- `ASaveStartedWhileAnotherModuleWaitsToLoad_IsWaitedForAsWell`: "the page loaded another module while a save started
  during its wait still waited for the directory".
- `AnotherModuleOpenedWhileAnAdminsSaveAsksTheDirectory_LoadsOnlyOnceTheSaveIsDone` passed: aud-a41's wait already covered
  a single save's lookup, as the evidence says.

During the run the second case's two store assertions were put in this order - the other module first - so its failure
names where the write went. The cases on the bound and the refusal need fields the base page has not got and were not run
against it. The fixed page was restored from its copy to its MD5 before the swap, touched and rebuilt.

## Fix

`Components/Pages/ModuleConfig.razor`:
- Each of the five saves (SaveConfigAsync, SaveModuleEnablementAsync, SaveSectionAccessAsync, SaveModuleAdminsAsync,
  SaveAllowlistAsync) takes, before its first await, the descriptor shown (`saved`) and its id, the page's load number
  (`started`), its record section, what it writes and what it diffs against - the changed keys with their loaded and
  wanted values; the toggle and its baseline; the aliases, snapshot, baseline, picked display names and pending sections;
  the admins snapshot and baseline; the allowlist's rows, version and count - and the load flags that qualify them. Every
  later step uses these: `IsModuleAdmin(moduleId, ...)`, `IsModuleCorrupt(moduleId)`, the grants the lookup confirms, the
  write, and its records (section, pending sections, `MaskIfSecret(saved, ...)`, `IsSecretField(saved, ...)`,
  `ConfigKeyName(saved, ...)`).
- Immediately before the write - after the directory confirmation in the access and admins saves - `if (!Shows(moduleId))`
  refuses with `PageMovedRefusal` and one record (`LogSettingsSaveRefused`, `refusedAt: PageMoved`, `mutationAttempted:
  false`; the allowlist through `LogAllowlistSaveRefused`); nothing is written.
- After the write, page state - baselines and counts, `moduleConfigCorrupt`, `enablementStoreCorrupt`,
  `sectionAccessCorrupt`, `moduleAdminsUnreadable`, the allowlist's version, banner and count, the status save's render and
  reload, `saving` - changes only `if (Holds(started))`; otherwise the save's message is prefixed with its module's display
  name (`OnAnotherModule`). Its records are written either way.
- `SaveCurrentTabAsync` registers each save in `savesInFlight` with its load and module name until it completes (the
  single `saveInFlight` is gone). `OnParametersSetAsync` waits through `WaitForThisLoadsSavesAsync` for the unfinished
  saves of the current load, looking again after each one ends, for at most `saveWaitBound`
  (`SqliteConnectionFactory.BusyTimeout` + 5 s); past it the next module loads and `UnfinishedSaveNotice` names the module
  of the saves still running.
- `OnParametersSetAsync` takes the next module into a local and assigns it with `LoadState`; `LoadState` counts the load
  and clears `saving`.
- The page injects `IGroupGrantDirectory`; its calls are unchanged.

`Services/IGroupGrantDirectory.cs` (new: `IsAvailable`, `ValidateGroupSid`); `Services/ADDirectorySearchService.cs`
implements it; `Program.cs` registers it as the `ADDirectorySearchService` singleton.

Tests: new `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs` (13 cases, the plan's ten; test 9 is a theory over the three
saves). `SettingsPageHarness` gains `GroupDirectory` - a stand-in that answers, records and holds lookups, given to the page
as its `ADSearch` - and releases its holds when disposed. `GroupGrantValidationTests.TheModuleAdminSave_...` re-anchored to
the captured baseline (`var baseline = moduleAdminGroupsBaseline.ToArray();`, then `snapshot.Except(baseline,` and
`baseline.Except(snapshot,`), its intent unchanged.

Departures from the plan, inside its scope:

1. **Test 3 holds the second save at its lookup, not its write.** The first draft held it as its write opened and
   deadlocked for 30 s: `ModuleAdminService.ApplyAdminChanges` takes the service's `_writeLock` (`Services/ModuleAdminService.cs`
   164) around the store write, so the held second write kept the first save from writing at all.
2. **The notice's seconds are formatted with the invariant culture**, so the text does not depend on the host's.

Caller-visible change, by design: on Module Configuration, opening another module while a save runs waits for that save -
and for every other save started on the module shown, including one started during the wait - for at most 10 seconds; past
that the other module opens with a notice naming the module whose save had not finished. A save whose page moved before it
wrote refuses ("Not saved: the page moved to another module while this save waited, so nothing was written. Open the
module again and save."), recorded as a refusal (`PageMoved`); one that finishes after the page moved changes nothing on the
page and says which module it was for. The page's Save is offered on the module it opened.

## Guard proof

Session-local driver `a66-mutate.ps1` in the scratchpad (never committed): the fix staged; the page copied and the copy
checked equal to the index blob (`git hash-object`); per row, MD5; the mutation by exact text (each edit must match once, and
the mutant must differ); touch; `dotnet build` of the test project; `dotnet test --no-build` filtered to 22 classes - the new
one, every settings-page suite, the allowlist suites, and the source scans that read this page (`GroupGrantValidationTests`,
`AdminPageDirtyStateTests`, `PageAuthorizationRecheckTests`, `ProtectedPrincipalServicerAdminUiTests`, `ProgressRegistryTests`,
`ClickGateTests`, `ClickGateStuckFlagTests`, `ModuleCatalogTests`): 702 tests; the page restored from the staged copy; touch;
MD5 re-confirmed; build; the same filter. Every mutant built with 0 errors, the page came back to its MD5 after every row, and
the restored tree ran 702/702 after each. All rows mutate `Components/Pages/ModuleConfig.razor`, this commit's bytes.

| # | Mutation (call site) | MD5 before | MD5 mutant | MD5 after | Named test failing |
|---|---|---|---|---|---|
| 1 | `SaveModuleAdminsAsync`: `module.Id` read after the lookup again (`moduleId = module!.Id;` after `ConfirmNewGroupGrantsAsync`'s refusal, before the moved check) | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `47A5DC3759F47658C34FA92B019D32F6` | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `AnAdminsSaveWhoseLookupOutlastsTheWait_Refuses_AndWritesToNeitherModule` (the second module's stored admins, sorted, began `...-6002`, the first module's added group; only `...-6003` expected) |
| 2 | `WaitForThisLoadsSavesAsync` (the route change's wait): the bound removed - the time left a day, not `saveWaitBound` less the time waited | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `290D83901C7E7872B34F079BB004A765` | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `ASaveThatNeverFinishes_HoldsTheNextModuleOnlyForTheBound_AndOnlyOnce` (the route change timed out after 10 s); also `AnAdminsWriteThatOutlastsTheWait_...`, `AnAdminsSaveWhoseLookupOutlastsTheWait_...`, `AnAccessSaveWhoseLookupOutlastsTheWait_...` and the three `ASaveWhosePageMovesDuringItsAuthorizationRecheck_...` cases, each waiting for a held save until its own timeout - 7 |
| E0 | extra, `OnParametersSetAsync`: no wait at all | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `0340245C91B626F6B2308A4EFED5B924` | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `AnotherModuleOpenedWhileAnAdminsSaveAsksTheDirectory_...` ("the page loaded another module while a save on this one waited for the directory"), `ASecondSaveThatFinishesFirst_...`, `ASaveStartedWhileAnotherModuleWaitsToLoad_...`, aud-a41's `SettingsSaveOffCircuitTests.ModuleConfig_AnotherModuleOpenedWhileASaveWaits_LoadsOnlyOnceTheSaveIsDone`, and the notice cases `AnAdminsSaveWhoseLookupOutlastsTheWait_...`, `ASaveThatNeverFinishes_...` ("after 0 ms, inside the wait") - 6 |
| E1 | extra, `SaveCurrentTabAsync`: only the latest save kept (`savesInFlight.Clear()` before the add - aud-a41's single field) | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `1C3A37DF8C813F85D4A4CC441712A274` | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `ASecondSaveThatFinishesFirst_DoesNotLetAnotherModuleLoadUnderTheFirst` ("the page loaded another module while the first of two saves still waited for the directory"; the store assertions above it held: the moved refusal stopped that save's write) |
| E2 | extra, `WaitForThisLoadsSavesAsync`: one pass (`if`, not `while`) | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `6D62077FCC862B36A9E2DA56268FE89C` | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `ASaveStartedWhileAnotherModuleWaitsToLoad_IsWaitedForAsWell` ("... a save started during its wait still waited for the directory"); `AnAdminsSaveWhoseLookupOutlastsTheWait_...` and `ASaveThatNeverFinishes_...` (one pass reports nothing unfinished, so no notice) - 3 |
| E3 | extra, `SaveSectionAccessAsync`: the moved refusal removed (`if (false)`) | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `7EC1AEFCD0A5486AEC7B00B9999116EB` | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `AnAccessSaveWhoseLookupOutlastsTheWait_Refuses_AndWritesToNeitherModule` (the first module's alias stored with the added group after the page had moved) |
| E4 | extra, `SaveModuleAdminsAsync`: the baseline reset with no load check (`if (true)`) | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `EB8428D5C4CCD3C0C64812C7BE55B83C` | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `AnAdminsWriteThatOutlastsTheWait_LandsOnItsOwnModule_AndLeavesTheNextOnesStateAlone` (the second module's baseline became the first module's snapshot, `...-6001` first; `...-6003` expected) |
| E5 | extra, `OnParametersSetAsync`: the next module assigned before the load's authorization await again (`module = next;` right after `Catalog.GetById`) | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `EF53B5620930CAFD29756B08760EB70A` | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `ASaveStartedWhileAnotherModuleLoads_SavesTheModuleStillShown_AndNothingReachesTheOther` (the second module's stored admins, sorted, began `...-6002`, the group added on the first module's page) |
| E6 | extra, `LoadState`: `saving` no longer cleared | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `FCD06182F3C15E2C4C3377647CA99BF9` | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `ASaveThatNeverFinishes_...` ("the next module's Save is still withheld by the unfinished save") |
| E7 | extra, `SaveConfigAsync`, `SaveModuleEnablementAsync`, `SaveAllowlistAsync`: the moved refusal removed from all three (`if (false)`) | `7C0C74BA6A1B2C5EC6A48007537AEB72` | `7CF490E4D2D73BAE5F0782F7F4128935` | `7C0C74BA6A1B2C5EC6A48007537AEB72` | the three `ASaveWhosePageMovesDuringItsAuthorizationRecheck_Refuses_AndWritesNothing` cases: `Configuration` (`DelineaSecretId` `4242` stored), `Module Status` (`MailboxPermissions` stored `false`), `Editable Attributes` (`["department"]` stored) |

Two rows were run twice. In the first pass E1's edit and E5's edit were built wrongly by the driver - PowerShell joined
`'a', 'b' + $nl + 'c'` as an array, so E1 replaced the add with `Clear()` and registered no save (the E0 result again, 6
failures), and E5 replaced its line with itself (mutant MD5 equal to the page's; 702 passed). The edits were parenthesised, the
driver now refuses a mutant that changes nothing, and both rows were run again; the table holds the second pass. The code
comments are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-23 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors; 19 warnings, all pre-existing, none in a touched
  file. The first run had a 20th, CS8631 in this commit's new test (a `string?` element in one assertion), fixed before the
  commit; the page the mutation round read was not touched by it.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 6,421
  passed / 0 skipped / 0 failed, 6,421 total, 3m27s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

## Known gaps

- Which windows production can reach: a second save in flight (a double click before the disabled Save renders, or a direct
  event), a save started while a route change waits, and a save that outlasts the bound - a directory confirmation past 10 s
  (up to 30 s per new grant on the runspace lock) or a store that does not answer. A save started while a load awaits (test 8)
  needs an await in the load that yields; today both complete synchronously (the circuit's stored authentication state, and
  the page's own note that `GroupAuthorizationHandler` is synchronous end to end) - *assumption from reading, not measured*;
  the harness holds them to drive it.
- A save that outlasts the bound keeps running - its worker thread and store connection until the store answers; the page
  cannot cancel it, as the stores take no cancellation. A later route change does not wait for it again.
- No catalog field is marked `IsSecret` today, so no case drives the config save's masking by its captured descriptor; it is
  by construction (`MaskIfSecret(saved, ...)`).
- The access save's concurrent-change message names aliases through `AliasName`, which reads the page's descriptor: after a
  move it falls back to the alias (wording only; the record's section and aliases are the captured ones).
- aud-a67 item 1 is unchanged: `dirty` survives a move, so the next module's save bar can show the previous module's counts.
  aud-a67 item 4 called a second save in flight cosmetic; this record's reproduction shows it let a route change forget the
  first save, which then wrote to the wrong module. That write is closed here; the re-entrancy guard stays held there.
- The page ran on the framework's dispatcher through the harness; no browser or live circuit ran. No directory, Exchange
  Online, Graph, DHCP, IIS, Delinea or SMTP: the directory is the harness's stand-in, and no case names a SID with a stored
  display name, so no SID is translated.

## Left for a follow-up (file owned by another agent)

None: no change was needed in a file another agent owns.

## Branch-tip gates - 2026-10-23 (`653a800a`: the fix and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 19 warnings, all pre-existing, none in a touched
  file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 6,421
  passed / 0 skipped / 0 failed, 6,421 total, 3m31s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- The comments commit `653a800a` changes comment lines only, in the one file it touches: `Components/Pages/ModuleConfig.razor`,
  whole-line comments and blank lines stripped, hashes the same at `0267d70d` and `653a800a` (MD5
  `6C84BAAF19B51AC62EC9B57008FC4878`, 1,818 lines); the 702-test filter of the guard proof ran green on it.

## Guard proof at admission

Pending.

## Folded in: the in-flight save wait on Module Configuration is unbounded (codex, settings-batch-6, 2026-10-23) - MEDIUM

`Components/Pages/ModuleConfig.razor` around line 953: `OnParametersSetAsync` awaits the aud-a41 in-flight save
with no timeout; a save task that never completes (a hung store) blocks every later module navigation on that
page instance until a new circuit. Bound the wait (the store's busy timeout plus a margin) and, when it runs
out, show a stuck-save notice and refuse the navigation's save bookkeeping rather than hang; the module-local
captured state this finding introduces is what makes a bounded wait safe (a late completion applies UI state
only if the page still shows that module). Mutation at the call site: the bound removed.
