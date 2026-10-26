# cons-6: routing A then B then back to A lets the older A load overwrite the newer A page

**Severity**: MEDIUM - administrator configuration edits disappear with no save, refusal or unsaved-changes prompt.
`Components/Pages/ModuleConfig.razor` about 1088: `StillRequested(requested)` compares the current `ModuleId`
with the captured one, so after A, B, A the FIRST A load passes the check although it is not the latest route. It
reaches `module = next; LoadState();`, reloads from the store, clears `dirty`, increments `load` and drops the
edits made on the newer A page. cons-4 closed the A-then-B case; identity alone cannot close this one.
**Status**: Verified on branch; awaiting merge and codex review
**Branch**: `fix/a64-a67` (from `eb017102`)
**Commit**: see below
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base f08a664a head 554ad4da (merged 4afa745d); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat 554ad4da)

## Evidence

Codex settings-batch-9, its single finding, at `4afa745d`.

## Approach (to be written by the implementing agent before code)

- A monotonically increasing route or load generation, or cancellation of the previous load; both the same module id AND
  the latest generation are required before navigation, before the state is applied and before the final status. Test
  the A, B, A order with an edit on the newer page and assert the edit survives; mutation: the generation comparison
  removed, which must drop the edit again. Keep cons-4's source guard honest - it pins that a check exists and that no
  await follows it, so it must still hold with the generation added.


### Implementation notes (fix/a64-a67, 2026-10-26), at `554ad4da`

cons-4 made a load check that the route still asks for its module. That closes A then B, where the module differs,
and nothing else: after A, B, A the older A load's module IS the route again, so it passed, reached
`module = next; LoadState();`, read the store afresh and dropped whatever had been typed on the newer A page.

`routeGeneration` counts the route changes this page takes up. Each load keeps the number it was given, and
`StillRequested(requested, generation)` requires both - the same module AND the latest number - so only the newest
load may apply anything. Both existing call sites take it, which is every point after an await: the two navigations
(config-only, access-denied), the state, and the final status, none of which has an await between it and the last
check.

Three decisions worth stating:
- **The counter is incremented after the no-op return, not at the top.** A parameter set for the module already shown
  returns before it, so a re-render of the same route cannot invalidate the load in flight for that route - which
  would abandon it with nothing left to finish the page.
- **A generation, not cancellation.** The alternative in the record is cancelling the previous load; the stores take
  no cancellation (aud-a67 item 12, still the owner's), so a cancelled load would go on running and still have to be
  told not to apply itself. The number does that directly and adds no new state to unwind.
- **cons-4's source guard still holds.** It pins that a `StillRequested` check sits before the assignment with no
  `await` after it; it is re-anchored from the whole call text to `if (!StillRequested(` so it asks about WHERE the
  check is rather than what it compares, and it passes unchanged otherwise.

**cons-7 refined this on 2026-10-26**, on this branch. The save drain is an await as well, and nothing checked the
route after it: a check sits there now, ahead of the teardown. And `loadedModuleId` is set only when the page settles
and cleared when a load starts replacing it, so the first decision above holds for a settled page only - a parameter
set that arrives during a load, for the same module included, takes a number of its own, and that load finishes the
page. See `.agents/review/findings/cons-7.md`.

## Files changed

- `Components/Pages/ModuleConfig.razor`; `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs`.

## Guard proof

Driver and filter as aud-a67's (the settings-pages filter, 512 tests). `ModuleConfig.razor` MD5
`157cda2288fc62ba07aeda3b8cff52cc`, matched after the row; 512/512 green after the restore. The mutant is the check as
it was at `554ad4da` - identity alone - so its failure is the finding's reproduction.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| R1 the generation comparison removed from `StillRequested` | `b63f227d5b8e56ebc818dfccc9fe8f7a` | 1 of 512: `AnOlderLoadOfTheSameModule_DoesNotOverwriteTheNewerPage_NorDropItsEdits` - the page's admin groups back to the stored one, the edit gone |

The case is the record's order exactly: A held at its authorization, B, A again to completion, an admin group added on
that page, then the first A released. It asserts the edit is still there, still unsaved, and still unwritten to the
store.

## Known gaps

- The counter is a page field, so it orders the loads of one page instance - which is the whole scope of the defect.
  It is never persisted and means nothing across circuits.
- An abandoned load still runs to its end, doing its authorization reads and then returning; nothing cancels it, and
  nothing bounds how many can be in flight. That is aud-a67 item 12's territory and is unchanged here.
- An `int` is used rather than a wrapping-safe counter: it would take about two billion route changes on one page
  instance to overflow, and the page is rebuilt on every circuit.
- Driven on the harness's dispatcher with a held authorization stand-in; no browser, no live circuit, no directory.

## Branch-tip gates - 2026-10-26 (`fix/a64-a67`, the cons-6 round)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 19 warnings - the fork's count, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 6,746 passed / 0 failed / 0 skipped, 6m38s. The
  previous tip was 6,745; this record adds the one case.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- Not run: `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps` - no PowerShell file changed. No directory, Exchange
  Online, Graph, DHCP, IIS, Delinea or SMTP call; nothing deployed.
