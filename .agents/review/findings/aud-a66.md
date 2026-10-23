# aud-a66: Module Configuration can apply one module's admin-group or section-access diff to another module when the operator opens a second module during the directory lookup

**Severity**: HIGH - a wrong authorization write (the module admin groups decide who may administer a
module). `SaveModuleAdminsAsync` reads `module.Id` and `moduleAdminGroupsBaseline` AFTER
`await ConfirmNewGroupGrantsAsync` (the directory lookup); the page instance is reused across
`/module-config/{ModuleId}` routes, so opening another module's configuration during the lookup
(NavMenu) applies module A's group diff, computed against module B's baseline, to module B through
`ApplyAdminChanges(module.Id, ...)`. `SaveSectionAccessAsync` has the same window. aud-a41's in-flight
save wait closes the Save-button path as a side effect, but the directory-lookup window is not closed and
no test pins it (the harness has no directory stand-in). Read from the code, not run.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (HIGH); not started
**Branch**: -
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

## Guard proof

Pending.

## Folded in: the in-flight save wait on Module Configuration is unbounded (codex, settings-batch-6, 2026-10-23) - MEDIUM

`Components/Pages/ModuleConfig.razor` around line 953: `OnParametersSetAsync` awaits the aud-a41 in-flight save
with no timeout; a save task that never completes (a hung store) blocks every later module navigation on that
page instance until a new circuit. Bound the wait (the store's busy timeout plus a margin) and, when it runs
out, show a stuck-save notice and refuse the navigation's save bookkeeping rather than hang; the module-local
captured state this finding introduces is what makes a bounded wait safe (a late completion applies UI state
only if the page still shows that module). Mutation at the call site: the bound removed.
