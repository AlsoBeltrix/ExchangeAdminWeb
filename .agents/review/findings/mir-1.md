# mir-1: a batch-user load that lost the race renders one batch opened with another's mailboxes

**Severity**: HIGH - the mailbox rows carry per-mailbox actions (`ExecuteUserAction`), so an
operator can act on batch B's mailboxes while the page says batch A is open. It is silent: no
error, no spinner, and the audit records the mailbox actually touched, which is the wrong one for
the batch the operator believes they are in.
**Status**: Verified
**Branch**: - (direct-to-main)
**Commit**: `1e8a9e6`

## Evidence

`Components/Pages/Migration.razor:1017` (`SyncOpenBatchFromUrl`) and `:1472`
(`ToggleBatchDetails`) both do:

```csharp
ReplaceBatchUsers(await MigrationSvc.GetMigrationBatchUsersAsync(<batch>));
```

Neither checks that `<batch>` is still the open batch when the await returns, and
`ReplaceBatchUsers` assigns unconditionally.

Triggering condition - two batch-user loads overlapping:

1. Open batch A. It loads.
2. Open batch B. `OpenBatch(B)` sets `expandedBatch = B` and pushes `?batch=B`;
   `loadingBatchUsers = B`; the Exchange call starts.
3. Before it returns, press **browser Back**. The URL returns to `?batch=A`,
   `SyncOpenBatchFromUrl` sees `A != B`, sets `expandedBatch = A`, and starts a second Exchange
   call for A.
4. B's call - the older one - returns last and calls `ReplaceBatchUsers(B's users)`.

Predicted observable failure: the batch list shows **A** expanded, and the mailbox rows under it
are **B's**. The per-mailbox action buttons in those rows act on B's mailboxes. Separately, B's
`finally` clears `loadingBatchUsers` while A's load is still running, so `IsBusy` goes false and
the whole page reports itself idle mid-load.

**This is a regression S1 introduced, not a pre-existing hole.** Before S1 the only entry points
to a batch-user load were `ToggleBatchDetails`, `RefreshBatchUsers` and `SearchUser`, every one of
them behind a control carrying `disabled="@IsBusy"`, so a second load could not be started while
one was in flight. S1 made the URL an entry point, and the browser's Back button is not a control
this page can disable.

## Approach

`batchUsersGeneration` counts changes of open batch. Both writers of `expandedBatch` - `OpenBatch`
outbound and `SyncOpenBatchFromUrl` inbound - bump it. Each load captures it before its await, and
`ReplaceBatchUsers` refuses any result whose capture has been overtaken.

**The guard is inside `ReplaceBatchUsers`, not at the two cited call sites.** That is the one place
rows may be assigned, so all six load paths are covered by construction. Only two are reachable
from browser Back today; the other four are not, and that is exactly the kind of fact that stops
being true one slice later.

**Resolved: a SECOND counter, not `reportGeneration`.** They are bumped by overlapping but not
identical events - `ReplaceBatchUsers` bumps the report generation without changing the open batch
- so sharing one would make every row refresh look like a batch change and discard rows that are
perfectly current.

**The in-flight flag is keyed on the batch name, not the generation.** Its `finally` clears
`loadingBatchUsers` only when this load still owns it, so a superseded load no longer un-busies the
page while its successor runs. Keying it on the generation instead looked tidier and was wrong: on
the collapse path the superseding act starts no load at all, so nothing would ever clear the flag
and the page would stay busy for the life of the circuit. A stuck flag is the worse failure, and
the repo already has a test class for it.

Residual, stated rather than hidden: A -> B -> Back(A) -> Forward(B) leaves the first B load
owning the name, so it clears the flag while the second B load runs. The rows are still correct -
the generation discards the stale one - and the page merely un-busies a moment early.

## Files changed

Declared before repair: `Components/Pages/Migration.razor`,
`ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs`, `Modules/ModuleCatalog.cs` (module version),
`ExchangeAdminWeb.Tests/ClickGateRegistry.cs` (line-count fingerprint).

## Guard proof

Three tests in `ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs`, each proved to bite by
mutating the fix and watching the named test fail, then restoring:

1. `ARowLoadThatLostTheRaceIsDiscardedRatherThanPublished` - guard removed from
   `ReplaceBatchUsers`. FAILED.
2. `EveryChangeOfTheOpenBatchBumpsTheRowGeneration` - the bump removed from `OpenBatch`. FAILED.
3. `EveryRowLoadCapturesTheGenerationBeforeItsAwait` - the capture in `RefreshBatchUsers` moved to
   AFTER the await. FAILED. **This is the test worth having:** that mutation compiles, reads almost
   identically to the correct code, and compares the counter with itself, so the guard can never
   fire and every other test still passes.

**One probe is not counted and is recorded rather than quietly redone.** The first attempt at (3)
moved the capture inside the `try` while the `catch` still referenced it - `error CS0103` - so it
proved nothing about the test. It was rewritten to compile and then bit.

Honest limit, unchanged from S1: no test in this repo renders a Blazor component, so none of these
exercises the race. They are source-anchored tripwires on the await sites themselves, and the
failure they describe can only be seen in a browser.

Gates: build Release 0 errors, `dotnet test` 3072 passed / 0 failed / 3 skipped (+3, exactly the
new tests), format exit 0, `git diff --check HEAD` exit 0, ASCII scan clean. Pester and
PSScriptAnalyzer not run - no PowerShell changed.

## Coder dispute

None. Independently confirmed by reading both sites and walking the interleaving; the reviewer's
description of the race is accurate and its reachability through browser Back is what makes it
real rather than theoretical.

## Known gaps

Closed by putting the guard in `ReplaceBatchUsers`: `RefreshBatchUsers`, `ExecuteUserAction` and
both `SearchUser` branches carried the same unguarded shape and are now covered, though none is
reachable from the URL today.

Still open, and not this finding: the enhanced-navigation question S1 recorded. If the interactive
component is torn down on each `NavigateTo` rather than preserved, `SyncOpenBatchFromUrl` never
races anything because there is no surviving instance to race in - the fix is still correct, but
the defect it closes would have been unreachable. That needs a browser either way.

## Reviewer comments

Round 1 - `openreview`, unprimed approach review over `69980f1..f43fc11`.

Verdict: **Acceptable with changes.** The approach - one route, URL-owned open batch, one outbound
writer and one inbound sync, plus the `LoadMigrationStatus`/`LoadBatchList` split - was endorsed as
what the reviewer would itself have built. One required change: the stale async-load guard above.

Capability proof: read `AGENTS.md` (succeeded); ran `git status --short --branch` (succeeded);
also ran `dotnet test ExchangeAdminWeb.slnx` - 3069 passed, 0 failed, 3 skipped.

```text
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  grade: fallback, accepted by the owner naming the pair at dispatch
  harness: codex-cli 0.154.0
  pins: 69980f151fee046bf716de6062b051b70ba5d668..f43fc1121dae530d7d95529c3983ef0c2244486d
  dispatched: 2026-09-25 UTC
```

Resolved identity is the dispatched slug; the Portkey gateway does not echo a provider-resolved
model id, so this records what was sent rather than what the provider chose.

## Closeout

Fixed on `master` in `1e8a9e6`, record in the commit that follows it. Per repo policy
(`.agents/repo-guidance.md`, "Earned Practices") a reviewer verification round is CRITICAL-only
and needs its own owner go; this is HIGH, so it closes on the coder-side guard proof above.

Pending step: push. Push policy is ask, and these commits are unpushed.
