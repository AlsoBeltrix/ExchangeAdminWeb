# Defect review request: Module Configuration saves capture their module and baseline before any await and refuse when the page has moved; the route change waits for every unfinished save of its load within a 10 s bound (aud-a66, with its folded-in MEDIUM)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `9bce6e97` or later.
- The change is the branch merged at `579c0875`: `a8647062` (plan), `0267d70d` (fix, tests, reproduction, guard proof),
  `653a800a` (comments only), `90835fd3` (record), forked from `ae112c83`. Review `git diff ae112c83 90835fd3`, and read
  the COMBINED `Components/Pages/ModuleConfig.razor` (`OnParametersSetAsync`: module and state assigned together after
  the awaits; the wait over every unfinished save of the current load, the 10 s bound, the notice; `SaveCurrentTabAsync`
  registering saves with the load number; each of the five saves: the captured module, id, load number, values and
  baselines, the "page moved" refusal right before the write, state changes only when the load still holds),
  `Services/IGroupGrantDirectory.cs` (new), `Services/ADDirectorySearchService.cs` (the interface), `Program.cs` (the
  registration), `ExchangeAdminWeb.Tests/SettingsPageHarness.cs` (`GroupDirectory`), `SettingsModuleMoveTests.cs`,
  `GroupGrantValidationTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a66.md` (its executed reproduction at `ae112c83`; the folded-in MEDIUM: the
  unbounded wait); parents `aud-a41.md`, `aud-a40.md`, `aud-a28r.md`. ALREADY admitted and out of scope: `aud-a67`
  (`dirty` carried across modules; the db CLI wait; the drain on the circuit; the re-entrancy guard; the stale comment;
  the allowlist busy message; the two test-guard LOWs; `ModuleId` read after the awaits causing a double load; the OU
  browser state after a move; label fallbacks on a moved page; a save past the bound keeping its thread). Do not
  re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3);
  `docs/ProjectConstitution.md` (authorization; every refused write audited).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 579c0875`. Report whether both succeeded.

## Mandate

Hunt for observable defects. The "page moved" check runs "right before the write" - between that check and the
write there is no await? (trace each of the five saves; an await between the check and the write reopens the window);
the captured values - the admins save diffs the captured edited list against the captured baseline: an edit made
AFTER the capture, while the lookup ran, is it kept as unsaved (aud-a41's recount rule) or lost; the refusal record
`PageMoved` with `mutationAttempted: false` - the allowlist save through `LogAllowlistSaveRefused`: same fields; the
10 s bound and the notice "naming the module whose save had not finished, and its Save is offered" - the operator can
then save module B while module A's save is still running against the store: two writes to different modules, fine;
but can module A's late completion then apply ITS state changes to the page (the record says only if the load still
holds - verify the load number comparison is on every state write including `saving`); `OnParametersSetAsync` sets
module and state together after its awaits - a render between the old module and the new state (a flash of module
B's values under module A's heading, or the reverse) - cosmetic unless a Save is clickable in that instant (name it if
so); the `IGroupGrantDirectory` seam registered as the `ADDirectorySearchService` singleton - lifetime unchanged, no
second instance; the route-change wait is in `OnParametersSetAsync`, which Blazor awaits before rendering - a 10 s
wait blocks the page render for 10 s with no feedback: is a "waiting for the previous save" state shown during it.
Tests passing for the wrong reason: the harness `GroupDirectory` holding a lookup - does it hold on the same await the
production code awaits; the 702-test filter - does it include the ClickGate and ProgressRegistry pins (the page is
NotYetConverted).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
