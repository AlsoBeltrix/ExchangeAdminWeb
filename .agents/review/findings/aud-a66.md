# aud-a66: Module Configuration can apply one module's admin-group or section-access diff to another module when the operator opens a second module during the directory lookup

**Severity**: HIGH - a wrong authorization write (the module admin groups decide who may administer a
module). `SaveModuleAdminsAsync` reads `module.Id` and `moduleAdminGroupsBaseline` AFTER
`await ConfirmNewGroupGrantsAsync` (the directory lookup); the page instance is reused across
`/module-config/{ModuleId}` routes, so opening another module's configuration during the lookup
(NavMenu) applies module A's group diff, computed against module B's baseline, to module B through
`ApplyAdminChanges(module.Id, ...)`. `SaveSectionAccessAsync` has the same window. aud-a41's in-flight
save wait closes the Save-button path as a side effect, but the directory-lookup window is not closed and
no test pins it (the harness has no directory stand-in). Read from the code, not run.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (HIGH; the folded-in item MEDIUM); in progress
**Branch**: `worktree-agent-a047027df146de3ac`
**Commit**: -
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

## Guard proof

Pending.

## Folded in: the in-flight save wait on Module Configuration is unbounded (codex, settings-batch-6, 2026-10-23) - MEDIUM

`Components/Pages/ModuleConfig.razor` around line 953: `OnParametersSetAsync` awaits the aud-a41 in-flight save
with no timeout; a save task that never completes (a hung store) blocks every later module navigation on that
page instance until a new circuit. Bound the wait (the store's busy timeout plus a margin) and, when it runs
out, show a stuck-save notice and refuse the navigation's save bookkeeping rather than hang; the module-local
captured state this finding introduces is what makes a bounded wait safe (a late completion applies UI state
only if the page still shows that module). Mutation at the call site: the bound removed.
