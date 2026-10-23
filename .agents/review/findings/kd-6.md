# kd-6: BlockedSenderService.UnblockSenderAsync takes no CancellationToken and has no timeout

**Severity**: LOW - a hang risk. `Services/BlockedSenderService.cs` `UnblockSenderAsync` (about 48) runs the unblock with no cancellation and no bound, so a stuck Exchange Online call holds the circuit's operation indefinitely.
**Status**: Verified on branch; awaiting merge and codex review
**Branch**: `fix/kd-misc` (worktree `D:\source\ExchangeAdminWeb-wt\kd-misc`, from master `eb017102`)
**Commit**: fix `b5673b23`; module version in `d79e1b79`
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. Trigger: the EXO call stalls. Predicted failure: the operation never completes or fails; the activity shows progress forever.

## Approach

Re-read at `eb017102`; every line below is that commit's.

- `Services/BlockedSenderService.cs:48-63` - `public Task<PermissionResult> UnblockSenderAsync(string senderAddress, string reason)`
  returns `RunAsync((ps, tracker) => { ...; Invoke(ps, tracker); }, () => ($"Unblocked sender {address}.", null), allowRetry: true)`:
  no token parameter and no bound. `ExchangeServiceBase.RunAsync` (`Services/ExchangeServiceBase.cs:49-107`) hands the
  delegate to `ExoConnectionPool.RunWithRetryAsync`, whose borrow waits up to two minutes for a slot and whose run is a
  synchronous `Invoke` inside `Task.Run` - neither takes a token, so nothing bounds a cmdlet that never answers.
- `Components/Pages/BlockedSenders.razor:456` - `opResult = await BlockedSenderSvc.UnblockSenderAsync(address, unblockReason);`
  inside `ConfirmUnblock`, after `isLoading = true` and the activity's `Progress.Begin` (`:344`); the catch at `:459-464`
  files any throw as "Unblock failed", and the audit at `:481-488` files `opResult.Success`. A stall there leaves
  `isLoading` true and the activity open for as long as Exchange Online does not answer.
- There is no module-wide timeout shape in this codebase to copy for an Exchange Online write (the waits that exist
  are the pool's two-minute slot wait, its ninety-second connect gate and the on-premises throttle's two minutes), so
  the shape is defined here and kept inside the module.

What changes:

1. **The caller's token and a bound.** `UnblockSenderAsync(senderAddress, reason, CancellationToken cancellationToken = default)`
   waits on the pooled call with `WaitAsync(UnblockBound, cancellationToken)`. `UnblockTimeout` is five minutes: longer
   than the pool's own admission can legitimately take (two minutes for a slot, then the ninety-second connect gate)
   plus an allowance for one cmdlet, so it fires on a stall, not on a busy pool. `UnblockBound` (internal, init) is the
   test seam.
2. **What the caller is told depends on whether the command was sent** (the aud-a73 and aud-a76 rules), settled once by
   a module-scoped `UnblockSubmission` compare-and-swap: the pooled delegate claims "sent" BEFORE it builds the command
   and refuses (throws, building nothing, so the borrowed pipeline is left clean) if the caller has already given up;
   the caller claims "abandoned" when it stops waiting. Abandoned first: a failed result, "nothing was changed and
   <address> is still blocked", and the call never sends afterwards. Sent first: the outcome is unknown, so the service
   throws `BlockedSenderUnblockUnconfirmedException` (a `TimeoutException`; message "...whether <address> was unblocked
   could not be confirmed. Refresh the list..."). A pool retry of a call that sent stays sent. A run that finishes in
   the same instant the wait gives up keeps its own outcome. The abandoned call still ends on its own; what it ended with
   is logged and its exception observed.
3. **The page passes its token and files an unconfirmed unblock Undetermined.** `ConfirmUnblock` passes
   `activity.CancellationToken` (the navigation guard's OK or the circuit closing). A dedicated catch for
   `BlockedSenderUnblockUnconfirmedException` shows the service's sentence (not "Unblock failed: ...") and files the
   `UnblockSender` row Undetermined: `LogModuleAction` has no explicit result parameter (aud-a73 gave one to the two
   permission methods only) and `AuditService.MergeExtra` is documented as merged last so a caller can set a computed
   field, so the row's extra carries `result: "Undetermined"`, its error kept. No shared code changes. An Undetermined
   unblock of a serviced protected principal keeps the servicer note (the pgwt-7 rule: the note must reach the audit
   whatever the outcome; a write that timed out after it was sent is its own example). `isLoading` is lowered and the
   activity completed on the same straight-line path as before (no finally; `ConfirmUnblock_HasNoFinally`).

Declared files: `Services/BlockedSenderService.cs`, `Components/Pages/BlockedSenders.razor`,
`ExchangeAdminWeb.Tests/MigrationStubExchange.cs` (an `UnblockStalls` switch on the Remove-BlockedSenderAddress stub and
a `beforeConnect` hook on `Pool`), `ExchangeAdminWeb.Tests/BlockedSenderUnblockBoundTests.cs` (new), this record. The
module version bump is the branch's version commit.

Mutation: the bound removed.

## Files changed

- `Services/BlockedSenderService.cs` - `UnblockTimeout` (five minutes) and the `UnblockBound` seam; `UnblockSenderAsync`
  takes `CancellationToken cancellationToken = default` and waits with `WaitAsync(UnblockBound, cancellationToken)`; the
  `UnblockSubmission` handshake (claimed by the pooled delegate before it builds the command, by the caller when it stops
  waiting); a run that finished as the wait gave up keeps its own outcome; the abandoned run is observed and logged;
  `BlockedSenderUnblockUnconfirmedException` (public, sealed, a `TimeoutException`, carries the address).
- `Components/Pages/BlockedSenders.razor` - `ConfirmUnblock` passes `activity.CancellationToken`, catches the unconfirmed
  exception before the general catch (the service's sentence, not "Unblock failed: ..."), keeps the servicer note for an
  unknown outcome, and files the row Undetermined through `FiledUndetermined` (new private helper). The literal
  `extra: serviced` the `PageAuthorizationRecheckTests` shape guard pins is kept. Not line-keyed (`NotYetConverted`).
- `ExchangeAdminWeb.Tests/MigrationStubExchange.cs` - the `UnblockStalls` switch on the shared Remove-BlockedSenderAddress
  stub and `Pool(..., Action? beforeConnect = null)`. Shared test infrastructure only; no Migration behaviour or test
  changes.
- `ExchangeAdminWeb.Tests/BlockedSenderUnblockBoundTests.cs` - new.
- `ExchangeAdminWeb.Tests/ExchangeServiceBaseRunFailureTests.cs` - its four `UnblockSenderAsync` calls pass
  `TestContext.Current.CancellationToken`: the method now takes an optional token, so xUnit1051 flags a call that omits
  it, and `dotnet format --verify-no-changes` (the CI gate) exits 2 on that unfixable diagnostic. No behaviour change.
- This record.

## Guard proof

`BlockedSenderUnblockBoundTests`, on the real service and pool over the stub cmdlets (no Exchange); the stalled call
borrows a runspace the pool already connected (a warm-up unblock first), so it is sent at once and then stalls:

- `AStalledUnblock_StopsWaitingAtTheBound` - the call returns inside a 30-second watchdog with a 3-second bound while the
  stub still holds it; sent, so `BlockedSenderUnblockUnconfirmedException` with a `TimeoutException` cause. (On a host too
  loaded to send it inside the bound it would be abandoned unsent instead, and the test then requires "nothing was
  changed" and no send ever; the K4 run below shows the sent branch is the one taken here.)
- `ACallerWhoCancelsAfterTheUnblockWasSent_StopsWaiting_AndItsOutcomeIsUnconfirmed` - cancelled once the stub has the
  call; unconfirmed, cause an `OperationCanceledException`.
- `ACallAbandonedBeforeTheUnblockWasSent_SaysNothingChanged_AndNeverSendsIt` - the borrow held in the pool's connect;
  cancelled; "nothing was changed"; the connect then completes and the abandoned call, observed to end (its log line),
  never reaches the stub.
- `AnUnblockThatAnswersInTime_IsUnchanged`, `TheBoundOutlastsThePoolsOwnAdmission`.
- `Page_AStalledUnblockTheOperatorWalksAwayFrom_EndsTheOperation_AndIsFiledUndetermined` - the compiled page's
  `ConfirmUnblock` driven by reflection with the real protection gate over empty stores; the activity cancelled once the
  stub has the call (the navigation guard's `CancelAll`); the bound set past the test, so only the page's token can end
  the wait. One `UnblockSender` row filed **Undetermined** with the service's sentence; `isLoading` false; the banner says
  the outcome could not be confirmed; no activity left open.

Mutations, each by exact replacement, md5-confirmed, built, run against `FullyQualifiedName~BlockedSenderUnblockBoundTests`
(6), restored byte-exact with a fresh mtime, rebuilt, rerun (6 passed every time):

| # | mutation | with it |
| --- | --- | --- |
| K1 | **the bound removed** (the record's): `run.WaitAsync(UnblockBound, cancellationToken)` -> `run.WaitAsync(cancellationToken)` | 1 failed: the stalled call held past the 30 s watchdog |
| K2 | the caller's token ignored: `-> run.WaitAsync(UnblockBound)` | 3 failed: cancel-after-send, abandon-before-send, the page |
| K3 | the handshake's send claim removed: `if (!submission.TrySend())` -> `if (false)` | 4 failed: the abandoned call sends; sent calls reported unsent |
| K4 | a sent call reported unsent: `if (submission.TryAbandon())` -> `if (true)` | 4 failed, the stall test among them (so it takes the sent branch) |
| K5 | the page files no Undetermined: `if (outcomeUnknown)` (before `FiledUndetermined`) -> `if (false)` | 1 failed: the page row reads Failed |
| K6 | the page passes no token: `UnblockSenderAsync(address, unblockReason, activity.CancellationToken)` -> `(address, unblockReason)` | 1 failed: the page held past the watchdog |

Targeted run before the mutations: BlockedSender, ExchangeServiceBaseRunFailure, ClickGateStuckFlag,
PageAuthorizationRecheck, ProgressRegistry and Migration filters, 857 passed. Two test-only edits followed: the format
gate required the test token on the flagged calls (above), and the stall test was made to read the call's answer
before holding it to the stub's log (it had checked the log first, which could race the stub's own line on a loaded
host). K1 and K4 were run again on the final test file: K1 1 failed (the stalled call, 30 s), K4 4 failed; 6 passed
restored each time (md5 `02df31b9` before and after). Full suite on this slice: 6719 passed / 0 failed (before the
second test-only edit; the class was rerun after it, 6 + 5 neighbours passed).

## Coder dispute

None.

## Known gaps

- Stopping the wait does not stop the pipeline: an abandoned call keeps its pooled runspace until Exchange Online answers
  or the session dies (a stall that never ends holds one slot, as it already did; the page no longer waits on it). A
  pipeline stop was not added: it cannot be verified without a live tenant.
- A cancellation after the command was sent files Undetermined even when the call would have answered a moment later;
  the later outcome is logged, not audited.
- Out of scope, found while here (candidate, not fixed): a FAILED serviced unblock still drops the servicer note
  (`Components/Pages/BlockedSenders.razor`, the `serviced` condition: `opResult.Success || outcomeUnknown`), against
  pgwt-7's "every outcome" rule.
- The timeout value is this module's own; there was no shared EXO write timeout to reuse.
- No live check: nothing ran against Exchange Online.

## Reviewer comments

None yet (codex review pending, dispatched by the supervising session).

## Closeout

Pending: merge into master, codex review, branch deletion. Branch-tip gates at `d79e1b79` (the branch's last code
commit): Release build 0 errors / 19 warnings (all pre-existing, none in a changed file);
`dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4` 6720 passed / 0 failed / 0 skipped;
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`, `git diff --check HEAD` and
`tools/Test-AsciiOnly.ps1` clean. PSScriptAnalyzer and Pester not run: no PowerShell file changed.
