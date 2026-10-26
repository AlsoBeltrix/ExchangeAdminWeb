# cons-4: an out-of-order module load can restore an older module after a newer route has won

**Severity**: MEDIUM - a wrong privileged module's configuration can be displayed and edited after rapid navigation or a
slow authorization read. `Components/Pages/ModuleConfig.razor` about 1106 to 1141: start loading module B, route to
module C while B is still awaiting its authorization, let C finish, then let B resume; `module = next` and
`LoadState()` apply B's state although the route is C. `loadedModuleId` is set earlier in the bookkeeping, and
nothing compares the request with the latest one immediately before the state is applied. aud-a67 (9) and (10) closed
the double-load and the OU-answer cases; this ordering is a third.
**Status**: Merged to master `4afa745d`; codex settings-batch-9 raised cons-6 (the A-B-A order defeats an identity-only check); closes when it lands
**Branch**: `fix/a64-a67` (from `eb017102`)
**Commit**: see below
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 620e9d5a head f08a664a (merged fe67668d, with d0fc4c70); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat f08a664a)

## Evidence

Codex settings-batch-8 finding 1 at `fe67668d`. The existing test mutates `ModuleId` during one load but never starts
and completes the newer load, so it passes while this order stays untested.

## Approach (to be written by the implementing agent before code)

- A load generation, or a comparison of the current `ModuleId` with the one this load requested, immediately before
  `module` is assigned, before `LoadState()`, before any navigation and before the final messages; a stale load is
  abandoned silently. Test: hold B, open and complete C, then release B and assert C's state survives and B's load wrote
  nothing. Mutation: the comparison removed, which must fail that test.


### Implementation notes (fix/a64-a67, 2026-10-26), at `f08a664a`

`OnParametersSetAsync` takes its module into `requested` before its first await (aud-a67 item 9) and sets
`loadedModuleId` to it straight away - but `loadedModuleId` says which load claimed the page, not which module the
router wants now, and nothing compared the two again. So of two loads in flight the one that RESUMED LAST won: open B,
route to C while B waits for its authorization read, let C finish, then let B resume, and B's `module = next` and
`LoadState()` landed over C.

`StillRequested(requested)` compares the live `ModuleId` with the module this load is for, and the load returns
silently when they differ. It is checked after each of the two awaits that can span a route change - the authentication
state and the authorization re-check - which between them cover the descriptor branch, both navigations
(config-only, access-denied), the state application and the final messages.

Three decisions, each forced by a mutant rather than assumed:
- A third check immediately before `module = next;`, which the record asks for in those words, was written and then
  removed: no await separates it from the check after the authorization read, so removing it changed nothing and no
  test could fail for it. What the record is really asking for - that the route cannot move between the last check and
  the state - is now a source guard,
  `TheLoad_ChecksTheRouteHasNotMovedOn_ImmediatelyBeforeItAppliesTheModule`, which fails if the check goes or if an
  `await` is added after it. That is durable where a redundant line is not.
- **This supersedes the outcome aud-a67 item 9's case asserted.** That case held one await, moved the route, and
  asserted the load still completed for the module it was asked for. Under cons-4 it must now apply nothing, because
  the route has moved past it; the case asserts that, and its two rows (one per await) still distinguish the two
  windows.
- Item 9's own mutants (I1, I2 - reading the parameter again instead of the captured local) no longer change any
  outcome, because this abort hides them: any disagreement ends the load. Its mechanism is pinned in the source
  instead, by `TheLoad_ReadsItsModuleParameterOnce_AndNeverAgainAfterAnAwait`, and **both mutants were re-run here and
  fail it** (the rows below).

## Files changed

- `Components/Pages/ModuleConfig.razor`; `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs`.

## Guard proof

Driver and filter as aud-a67's (session working directory `a64a67-work/mutate.sh`; the settings-pages filter, 511
tests). `ModuleConfig.razor` MD5 `7cb956834e82675bb23422e738298544`, matched after every row; 511/511 green after each
restore. N2 is the page as merged at `fe67668d`, so its failures are the finding's reproduction.

| Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|
| N1 the check after the authorization read removed | `b07ac770dd00d3ae43d5c9d8fbe0bd4b` | 3 of 511: `AnOlderLoadThatResumesAfterANewerOneFinished_AppliesNothing` (the older load applied its module over the newer one), `ARouteChangeDuringALoad_...(held: "the authorization re-check")`, and the source guard `TheLoad_ChecksTheRouteHasNotMovedOn_ImmediatelyBeforeItAppliesTheModule` (an `await` now sits between the last check and the assignment) |
| N2 both checks removed - the page as merged | `fcaf73643fcfa38b42cf72111610415e` | 4 of 511: the three above plus `ARouteChangeDuringALoad_...(held: "the authentication state")`; the source guard fails with "nothing checks the route before the module is applied" |
| aud-a67 I1 re-run (`Catalog.GetById(ModuleId)`) | `05ab1531296152a7f7d8c9fc93bd3f53` | 1 of 511: `TheLoad_ReadsItsModuleParameterOnce_AndNeverAgainAfterAnAwait` - "reads ModuleId again after capturing it: var next = Catalog.GetById(ModuleId);" |
| aud-a67 I2 re-run (`IsModuleAdmin(ModuleId, user)`) | `fb2027fc93111d9ec20159acb748ca23` | 1 of 511: the same guard - "...: if (!isGlobalAdmin && !ModuleAdmin.IsModuleAdmin(ModuleId, user))" |

## Known gaps

- The case drives the orderings on the harness's dispatcher with a held authorization stand-in; no browser, no live
  circuit, no directory. A real router raises `OnParametersSetAsync` for each route change, which is what the case does.
- A load abandoned this way leaves `loadedModuleId` at the module the winning load set, which is what the page shows.
  It writes nothing and says nothing, by design - the operator sees the module they asked for, with no notice that an
  earlier one was dropped.
- Nothing bounds how many loads can be in flight; each costs its own authorization reads and ends silently.

## Branch-tip gates - 2026-10-26 (`fix/a64-a67`, the cons-2/3/4/5 round)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 19 warnings - the same count as the fork, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 6,745 passed / 0 failed / 0 skipped, 4m40s. The
  previous tip of this branch was 6,742; these four records add 3 cases net (cons-4 adds three and retargets one,
  cons-5 extends an existing theory rather than adding cases, cons-2 and cons-3 add none).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- Not run: `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps` - no PowerShell file changed. No directory, Exchange
  Online, Graph, DHCP, IIS, Delinea or SMTP call; nothing deployed.

## Reviewer comments (supervisor, settings-batch-9)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base f08a664a head 554ad4da (merged 4afa745d); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat 554ad4da). One candidate, admitted as cons-6 (against cons-4). Codex judged the agent's three recorded calls and upheld all three: no await, StateHasChanged or yielding call sits between the last StillRequested and the assignment, and `ModuleAdmin.IsModuleAdmin` is synchronous, so the third check really was redundant and the source guard stands in for it; aud-a67 (9) is adequately superseded and its source guard is not tautological for the two named mutants; cons-2's ceiling is a broad sanity bound that does not bite under its own defect, and the sibling coverage it leans on is real (the held-lock failure case and the structural timeout assertion both catch removal of `DefaultTimeout`). cons-5's refusals return before any write, audit or dirty clearing, and Exchange Online Config's fields really are that page's working state. Artefacts: `.agents/review/settings-batch-9.result.txt`, `.stream.log`, `prompt-settings-batch-9.md`.
