# cons-7: a duplicate same-target load can strand the module page with no live load left to apply

**Severity**: MEDIUM - the configuration page can be left blank and stuck after rapid or duplicate routing during the
save-drain window, and with `authChecked` false the unsaved-changes guard is not rendered.
`Components/Pages/ModuleConfig.razor` about 1147: while module A has a save in flight, routing to B increments
`routeGeneration` and the first B load waits in `WaitForThisLoadsSavesAsync()`. If the same B parameters arrive
again before that wait ends, a second B load takes a newer generation. The older B continuation resumes first, sets
`loadedModuleId = requested`, clears `authChecked` and `statusMessage`, then fails `StillRequested` on its stale
generation. The newer B load then resumes, sees `requested == loadedModuleId` and returns at the no-op. Nothing
reaches `module = next; LoadState()`. This is precisely the "a live load abandoned with nothing to finish the page"
case cons-6's counter placement was meant to avoid.
**Status**: Merged to master at `0769536e` (2026-10-26); awaiting the single final codex-commercial review
**Branch**: `fix/a64-a67` (from `eb017102`)
**Commit**: the `fix(cons-7)` commit on `fix/a64-a67`, the first after `bcaabba7`
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 554ad4da head bcaabba7 (merged d28c0d25); 2026-10-26; capability proof succeeded (AGENTS.md read, git show --stat bcaabba7)

## Evidence

Codex settings-batch-10, its single finding, at `d28c0d25`.

## Approach (to be written by the implementing agent before code)

- Do not let a stale or merely claimed load satisfy the no-op: either delay `loadedModuleId = requested` until the load
  is still current and about to apply state, or record the generation that claimed and applied it and let the
  same-module return fire only for the current generation or for page state already applied. Test the exact order - a
  save in flight on A, route to B, the same B parameters again before the drain ends, the older B resuming first - and
  assert the page shows B; mutation: the claim moved back, which must strand the page again.

### As implemented (fix/a64-a67, 2026-10-26), written before the code

Quotes are `Components/Pages/ModuleConfig.razor` at `bcaabba7` (MD5 `157cda2288fc62ba07aeda3b8cff52cc`):

- 1119 `if (ModuleId == loadedModuleId) return;` - the no-op at the top.
- 1131 `var generation = ++routeGeneration;` - every load past it takes the next number (cons-6).
- 1146 `var unfinished = await WaitForThisLoadsSavesAsync();` - the save drain, up to `saveWaitBound`.
- 1147 `if (requested == loadedModuleId) return;` - the same-module return after the drain.
- 1149 `loadedModuleId = requested;`, 1151 `authChecked = false;`, 1152 `statusMessage = null;` - the claim and the
  teardown, both before this load has checked whether it is still wanted.
- 1154 `var authState = await AuthStateProvider.GetAuthenticationStateAsync();`, then 1159
  `if (!StillRequested(requested, generation)) return;` - the first such check.
- 1196 `module = next;`, 1197 `LoadState();`, 1210 `authChecked = true;` - the only place the module is applied, and
  1169 `authChecked = true;` for a module that does not exist.
- 24 `@if (!authChecked)` renders nothing at all, so 46 `<UnsavedChangesGuard HasUnsavedChanges="@dirty.IsDirty"` is
  not rendered either.

`loadedModuleId` is set at 1149, when a load has merely got through the drain, and read at 1119 and 1147 as "this
module is on screen". Three orders strand the page from that, the first being the finding's:

1. B and B again, both in the drain behind a save on A. The older resumes first: 1149 claims B, 1151 clears
   `authChecked`, 1159 fails on its generation. The newer resumes: 1147 returns. Nothing reaches 1196.
2. Back to the module shown, inside the same window. A shown with a save in flight, route to B (in the drain), route
   back to A: 1119 returns, rightly - A is on screen. Then B resumes: 1149, 1151, and 1159 fails because the route is
   A. The route is A and the page is blank.
3. A claim outliving its load. A load for X gets through the drain - on its bound, the save still running - and
   claims at 1149, then waits at 1154 or at the authorization read; the route moves to Y, whose load waits in the
   drain for the same save; the route comes back to X and 1119 returns on the stale claim. X's load fails its check
   on generation, Y's on identity, and nothing applies X.

The change, in two parts:

- (a) A load the route has moved past ends at the drain, before it touches anything: `StillRequested` immediately
  after 1146, ahead of the teardown. Closes 1 and 2.
- (b) `loadedModuleId` is the module the page has SETTLED on. It is set only where `authChecked = true` is - the
  not-found branch and the end of the apply - and it is set to null where a load begins replacing the page, beside
  1151. So 1119 returns only for a page that is settled on the module the route asks for; a parameter set that
  arrives while a load is in flight, the same module included, takes a newer number and that load finishes the page.
  Closes 1 and 3.
- 1147 cannot fire any more and is removed: with (a), a load still current after the drain finds `loadedModuleId` as
  it was when that load passed 1119, because only the current load sets it. The comments that describe
  `loadedModuleId` as set early (1081, and `routeGeneration`'s at 1100-1102) are corrected.
- The cons-4 source guard still holds: the last `if (!StillRequested(` before `module = next;` is still the one after
  the authorization read, with no await after it. The cons-6 generation is unchanged.

**The previous agent's uncommitted draft** (stopped mid-slice) had (a), and moved the claim to the apply and the
not-found branch, but did not clear `loadedModuleId` where the page is torn down. Under it `loadedModuleId` keeps
naming the module shown after another module's load has cleared `authChecked`: route from A to B, hold B at its
authorization read, route back to A - 1119 returns because A is still "loaded", B fails its check, and the page is
blank. `bcaabba7` does not have that one (B's early claim sends the return to A through a load of its own), so the
draft swapped one strand for another and was not the finished fix. It is kept and completed rather than redone: its
(a), its two claims and its case stay; the null at the teardown is added, the dead return removed, the comments
corrected, and three cases added.

Cases, in `SettingsModuleMoveTests`: T1 order 1, exactly as the record asks (the draft's case); T2 order 2; T3 order 3;
T4 the draft's regression. Mutations: R0 the page as at `bcaabba7` (T1, T2 and T3 must fail); M1 the claim moved back
to the teardown as at `bcaabba7` - the record's own mutation; M2 (a) removed; M3 the null at the teardown removed,
which is the draft. Each of M1 to M3 must fail its own case. **M1 cannot strand T1's order on its own**: (a) closes
order 1 by itself, so for T1 only R0 strands it, and the claim's placement is proven by T3, the one order where nothing
else saves the page.

## Files changed

- `Components/Pages/ModuleConfig.razor`; `ExchangeAdminWeb.Tests/SettingsModuleMoveTests.cs` (T1 kept from the draft,
  with a remark on its order; T2 to T4 added); a pointer each in `.agents/review/findings/cons-4.md` and `cons-6.md`,
  whose notes describe `loadedModuleId` as it was.
- Versions: none. Module Configuration is an app-wide admin page with no catalog entry (aud-a67: "App-wide (Module
  Configuration): no module bump"), cons-4 and cons-6 moved no version either, and the base bump owed at close
  (`.agents/state.md`) is not this slice's.

## Guard proof

Driver `mutate.sh` in this session's scratchpad: each mutant replaces the page, then
`dotnet test ExchangeAdminWeb.slnx --filter "FullyQualifiedName~SettingsModuleMoveTests"` (27 tests), then the fix is
copied back byte-exact with a fresh mtime and its MD5 checked. `ModuleConfig.razor` MD5
`50af05492c3336456aee927fd30dee4e`, matched after every row; 27/27 green after the last restore. R0 is the page as at
`bcaabba7`, so its failures are the finding's reproduction; M1 to M3 are each the fix with one part taken out.

| Mutation | Mutant MD5 | Named tests failing |
|---|---|---|
| R0 the page as at `bcaabba7` | `157cda2288fc62ba07aeda3b8cff52cc` | 3 of 27: T1 `ASecondLoadOfTheSameModuleDuringTheSaveDrain_StillLeavesThePageShowingIt` (the record's order: `MailboxPermissions` still shown where `CalendarPermissions` was routed to), T3 `AReturnToAModuleWhoseLoadWasOvertaken_IsALoadOfItsOwn` (the same), T2 `ARouteBackToTheModuleShown_DuringTheSaveDrain_LeavesThatModuleOnThePage` ("the page was left torn down") |
| M1 the claim moved back - `loadedModuleId = requested;` at the teardown as at `bcaabba7`, the two settle-point claims removed | `37a0cdffccbe2570da8c84c74d4237a3` | 1 of 27: T3 - `MailboxPermissions` still shown where `CalendarPermissions` was routed back to |
| M2 the check as the load leaves the drain removed | `8a12c4ad19df58e71d5d67f7af197ae3` | 1 of 27: T2 - "the page was left torn down" |
| M3 `loadedModuleId = null;` at the teardown removed - the inherited draft's design | `1cd835f7a718d1686322d23e7defe908` | 1 of 27: T4 `AReturnToTheModuleShown_AfterAnotherModulesLoadClearedThePage_LoadsItAgain` - "the page was left torn down" |

- **T1 survives M1 and M2 alike**, as the Approach predicted: each part of the fix closes the record's order on its
  own, so T1 fails only with both gone (R0). The record's mutation - the claim moved back, which must strand the page
  again - strands T3's order, the one where the claim's placement alone decides; that is row M1.
- T1's order, the older load resuming first, is the dispatcher's and not the case's: both drain waits are
  continuations of the one save, registered oldest first. R0 failing T1 proves the order happened - the other way
  round, the page as at `bcaabba7` applies B.
- The inherited draft was also run as it stood, before any page edit of this slice (MD5
  `7ba6628a9283fbaddb5afbc38419f960`): 26 of 27, T4 the one failure. M3 is the same design on the final text.

## Known gaps

- A parameter set that arrives while a load is in flight - a re-render of the same route included - now starts a load
  of its own, where it used to return on the in-flight load's claim. The newer load finishes the page; the cost is one
  more authentication-state and authorization read.
- The waiting notice is not taken back by a load that ends at the drain. Route from A to B while A's save runs, then
  back to A: the page is A, settled, and until that save ends it still says "Waiting for A's save to finish before
  opening B". The save's own outcome replaces it when it ends (T2 asserts that). Cosmetic, writes nothing; not fixed.
- Order 3 and T4's order need the authentication-state or authorization read not to answer at once. The page's own
  note says `GroupAuthorizationHandler` is synchronous end to end, so (an assumption, not measured) both orders are
  narrow in production; they are held because the record treats every await as a window, as cons-4 does. Orders 1
  and 2 need only the save drain, which is real.
- Driven on the harness's dispatcher with held stand-ins for the directory and the authorization read; no browser, no
  live circuit, no directory. A real router raises `OnParametersSetAsync` for each route change and re-render, which
  is what the cases do.

## Branch-tip gates - 2026-10-26 (`fix/a64-a67`, the cons-7 round)

Run once, on the tree this commit holds and before it was made, so the numbers could be recorded in it: the page
(`50af05492c3336456aee927fd30dee4e`) and the test file (`6d4a811f1536393681f9826d103847e6`) are byte-identical to what
was built and run; only this section was written after.

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 19 warnings - the branch's count, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4`: 6,750 passed / 0 failed / 0 skipped, 4m02s. The
  previous tip was 6,746; this record adds the four cases.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `pwsh -NoProfile -File tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- Not run: `Invoke-ScriptAnalyzer` and `Invoke-Pester tests/ps` - no PowerShell file changed. No directory, Exchange
  Online, Graph, DHCP, IIS, Delinea or SMTP call; nothing deployed.
