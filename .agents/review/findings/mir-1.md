# mir-1: a batch-user load that lost the race renders one batch opened with another's mailboxes

**Severity**: HIGH - the mailbox rows carry per-mailbox actions (`ExecuteUserAction`), so an
operator can act on batch B's mailboxes while the page says batch A is open. It is silent: no
error, no spinner, and the audit records the mailbox actually touched, which is the wrong one for
the batch the operator believes they are in.
**Status**: Open
**Branch**: - (direct-to-main)
**Commit**: -

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

Not yet implemented. The shape the reviewer proposes, and the one this file already uses for the
same class of problem: a monotonic generation counter, bumped whenever the open batch changes or
collapses. Each load captures the value it started under and discards its result - and declines to
clear `loadingBatchUsers` - if the generation has moved on. `reportGeneration`
(`Migration.razor:1932`) is the precedent; it guards the report against exactly this shape, and
the rows it describes were left unguarded.

Open question for implementation, not yet settled: whether the row guard is a second counter or
the existing `reportGeneration`. They are bumped by overlapping but not identical sets of events,
so reusing one for both needs proving rather than assuming.

## Files changed

Declared before repair: `Components/Pages/Migration.razor`,
`ExchangeAdminWeb.Tests/MigrationStatusPageTests.cs`, `Modules/ModuleCatalog.cs` (module version),
`ExchangeAdminWeb.Tests/ClickGateRegistry.cs` (line-count fingerprint).

## Guard proof

Pending. The tripwire must fail without the guard and pass with it. Note the honest limit already
recorded for S1: no test in this repo renders a Blazor component, so a source-anchored guard is
what is available here and it must be anchored on the await sites themselves, not on the file.

## Coder dispute

None. Independently confirmed by reading both sites and walking the interleaving; the reviewer's
description of the race is accurate and its reachability through browser Back is what makes it
real rather than theoretical.

## Known gaps

`RefreshBatchUsers` and `ExecuteUserAction` contain the same unguarded
`ReplaceBatchUsers(await ...)` shape. Neither is reachable from the URL, so neither is part of the
predicted failure, but a generation guard that covers only the two cited sites leaves the same
trap for the next slice to fall into.

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

Pending step: the owner's go to implement. No code is written.
