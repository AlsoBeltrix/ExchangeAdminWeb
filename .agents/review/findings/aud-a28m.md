# aud-a28m: The event log's undo has two more unrecorded exits, a Cancel mid-undo makes the catch throw, and the repository's allowlist writer is still public

**Severity**: MEDIUM for the undo exits (the aud-a28g/a28l class on the authorization-adjacent undo
path), LOW for the rest. (1) `AdminEventLog.razor` `ExecuteUndo`'s "No undo handler available"
return and its catch (an exception thrown before `ExecuteUndoAsync`) write no record. (2) The undo
panel's Cancel/close buttons stay active during an undo; closing mid-run nulls `undoTargetEvent`,
and the catch's `LogError(..., undoTargetEvent.OperationId)` then throws out of the handler - the
operator sees a circuit error instead of the undo's outcome. (3) `AttributeEditorRepository.SaveAllowlist`
is still public with only test callers; the aud-a28l tripwire refuses a production caller, but the
surface stays.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-a762d0851374626dc`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28l/a26g/a26i agent, 2026-10-22, candidates 1 to 4 of its hand-back)

## Evidence

At `f0cccea8`, `Components/Pages/AdminEventLog.razor` `ExecuteUndo`: the no-handler return; the
catch's log line reading `undoTargetEvent.OperationId`; the panel's Cancel/close handlers not gated
on an undo in progress. `Services/Storage/AttributeEditorRepository.cs` `SaveAllowlist` (public).

## Approach (to be written by the implementing agent before code)

- Record both exits: the no-handler return through the page's own refusal helper (no module to ask,
  so a generic settings/undo refusal record via `AuditService`, `refusedAt: NoHandler`,
  `mutationAttempted: false`, `originalOperationId` the event's); the catch through a failure record
  with the exception message. Exactly one record per attempt.
- Capture the target event into a local at the top of the handler and use the local everywhere
  (including the catch); disable Cancel/close while an undo runs. A Cancel during an undo can no
  longer null what the catch reads.
- Make the repository writer internal (or private to the audited path), moving tests to the audited
  save or to an internal seam; keep the tripwire.
- Tests through the aud-a28l `EventLogUndoRefusalTests` pattern: no handler leaves one refusal
  record; an exception before `ExecuteUndoAsync` leaves one failure record; a Cancel raced into the
  handler leaves the outcome recorded and no exception escapes; the repository writer is not public.
  `ClickGateRegistry`: the page is `NotYetConverted`; confirm. Mutations at the call site: drop the
  no-handler record; drop the catch record; read the field again in the catch.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28l/a26g/a26i hand-back
(`8f3aa99d`). One commit `fix(event-log): ... (aud-a28m)`, first of two on this branch. Behaviour: Admin
Event Log (the page); the AD Attribute Editor's repository changes visibility only. The supervisor owns the
version bumps. Evidence re-read at `762c774c`, `Components/Pages/AdminEventLog.razor`: ExecuteUndo 957-1054 -
the no-handler return 975-979 sets undoError and returns with no record; the catch 1043-1047 logs
`undoTargetEvent.OperationId`; after its awaits the handler reads undoTargetEvent (998, 1002), undoTicket (998,
1018) and undoPreview (1023-1024) again. The panel's three close paths - the backdrop 341, the X 345, Cancel
422 - call CloseUndoPanel 948-955, which nulls undoPreview and undoTargetEvent and clears undoTicket without
looking at undoExecuting. `Services/Storage/AttributeEditorRepository.cs` SaveAllowlist 56-71, public: no
production caller (the aud-a28l source tripwire); test callers AttributeEditorHarness 59,
ADAttributeEditorPatternTests 29, ADAttributeEditorServiceTests 40 and 479, AllowlistBannerTests 56 and 81.

1. **Both exits are recorded.** New page helper `LogUndoNotRun(target, userName, clientIp, ticket, refusedAt,
   error)`: `Audit.LogModuleAction(userName, clientIp, "AdminEventLog_Undo", "AdminEventLog", <the event's
   FullTarget, or "(none)">, false, ticket, error, extra)` with `originalOperationId` (the event's
   OperationId, null when blank), `mutationAttempted: false` and, when given, `refusedAt` - inside a catch that
   logs an audit failure, so it never throws. Filed under the event log's own catalog id because there may be
   no module to file it under; the category filter (58-76) gains `AdminEventLog` so the rows can be filtered
   to. `@inject AuditService Audit` (the ADAttributeEditor and AdminSettings precedent).
   - No handler: `LogUndoNotRun(..., "NoHandler", undoError)` directly above the return. So that it names the
     operator, the auth-state read and `userName` move above FindHandler (same values; FindHandler is pure).
   - The catch: `LogUndoNotRun(..., userName ?? "unknown", ..., refusedAt: null, undoError)` only while the
     module has not been called - a local `moduleCalled`, set on the line before `handler.ExecuteUndoAsync`.
     Exactly one record per attempt: ExecuteUndoAsync records every exit it reaches, a throw included
     (aud-a28d; its own catch), and an exception after it - the trailing LoadEvents - follows a success the
     module has already recorded. `mutationAttempted: false` because nothing was handed to the module; no
     `refusedAt`, because it is a failure, not a refusal. `error` is what the operator is shown.
   Failed, so neither marks the edit reversed (aud-a26c), and no module claims category AdminEventLog, so
   neither offers an Undo of its own. Not announced: the page announces nothing (aud-a28d's known gap).
2. **The handler acts on what it was confirmed for, and the panel stays open while it runs.** At the top of
   ExecuteUndo, before any await: `target = undoTargetEvent`, `preview = undoPreview`, `ticket =
   undoTicket.Trim()` and `clientIp = ClientInfo.IpAddress` into locals; the guard tests them, and every later
   read - FindHandler, LogUndoRefused, the fields copy, ExecuteUndoAsync, the reversed-set add, the catch's log
   line and record - uses them. The ticket is captured for the reason the event is: the box stays editable
   while the undo runs, and the old close cleared it. CloseUndoPanel returns at once while `undoExecuting`
   (the backdrop cannot be disabled, and a click the browser sent before the re-render still arrives); the X
   and Cancel buttons render `disabled="@undoExecuting"`. undoExecuting is lowered in ExecuteUndo's finally,
   so the panel closes again once the undo ends. Not a ClickGate conversion: the page stays NotYetConverted
   and gains no ActionsDisabled.
3. **SaveAllowlist is internal**: the tests' seed through InternalsVisibleTo, with no production caller. The
   tests stay on it as the internal seam; the aud-a28l tripwires are unchanged.
4. **Registries.** ClickGateRegistry: AdminEventLog.razor is NotYetConverted ("tier 4, not approved") with no
   line-keyed entry - confirmed at `762c774c`; nothing re-anchors. ProgressRegistry: ExecuteUndo's pin
   (`handler.ExecuteUndoAsync`) unchanged and no Complete added before it; CloseUndoPanel stays synchronous;
   LogUndoNotRun is not a handler.

Tests, through the aud-a28l EventLogUndoRefusalTests pattern (its authorization substitute can also hold a
policy's answer or fault it; the page gets the harness's `Audit`): (a) no handler (an empty UndoRegistry) -
one record: AdminEventLog_Undo, Failed, `refusedAt` NoHandler, `mutationAttempted` false, `error` the message
shown, the event's `originalOperationId` and target, the operator's user, ip and ticket; no directory
session, nothing written, no notice, the edit not reversed; (b) an exception before ExecuteUndoAsync - the
page re-check or the level check faulted - one failure record (no `refusedAt`, `mutationAttempted` false,
`error` the message shown) and nothing else; (c) Cancel raced in - the page re-check held, CloseUndoPanel
called, the check then faulted - the panel not closed, one failure record, the outcome shown, no exception
escapes, and Cancel closes the panel once the undo has ended; (d) the panel's fields cleared mid-run as the
old Cancel cleared them - no exception escapes and the record names the event and ticket the undo was
confirmed for; (e) the X and Cancel buttons carry `disabled="@undoExecuting"`; (f)
AllowlistConcurrentSaveTests: the repository's SaveAllowlist is internal, not public. Conditions asserted,
not wording.

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched, touched,
rebuilt, green): M1 the no-handler record dropped; M2 the catch's record dropped; M3 the catch's log line
reading `undoTargetEvent.OperationId` again, as at `762c774c`; extras E1 CloseUndoPanel's undoExecuting guard
dropped, E2 SaveAllowlist public again.

Files: `Components/Pages/AdminEventLog.razor`, `Services/Storage/AttributeEditorRepository.cs`,
`ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`, `ExchangeAdminWeb.Tests/AllowlistConcurrentSaveTests.cs`,
this record.

## Fix

`Components/Pages/AdminEventLog.razor`:

- ExecuteUndo reads what it acts on into locals before its first await - the event (`target`), the preview,
  the trimmed ticket and the client IP - and every later read uses them: FindHandler, aud-a28l's
  LogUndoRefused, the fields copy, ExecuteUndoAsync, the in-session reversed add, the catch's log line and its
  record. The auth state and `userName` are read first in the try, above FindHandler.
- The no-handler return calls `LogUndoNotRun(target, userName, clientIp, ticket, "NoHandler", undoError)`
  first. The catch calls `LogUndoNotRun(target, userName ?? "unknown", clientIp, ticket, null, undoError)`
  while `moduleCalled` is false; it is set on the line before `handler.ExecuteUndoAsync`.
- New `LogUndoNotRun`: `Audit.LogModuleAction(userName, clientIp, "AdminEventLog_Undo", "AdminEventLog",
  <the event's FullTarget, or "(none)">, false, ticket, error, extra)` with `originalOperationId` (null when
  the event has none), `mutationAttempted: false` and, when given, `refusedAt`; inside a catch that logs an
  audit failure. `@inject AuditService Audit`.
- CloseUndoPanel returns while `undoExecuting`; the X and Cancel buttons render `disabled="@undoExecuting"`.
- The category filter offers `AdminEventLog`.
- 1484 lines before, 1528 after. NotYetConverted in ClickGateRegistry, so no line key moves.

`Services/Storage/AttributeEditorRepository.cs`: `SaveAllowlist` is `internal`.

Tests: `EventLogUndoRefusalTests` - six new cases: `AnUndoNoModuleClaims_LeavesOneRefusalRecord`,
`AnUndoThatThrowsBeforeItsModuleIsCalled_LeavesOneFailureRecord` (the page re-check faulted; the level check
faulted), `ACancelWhileTheUndoRuns_IsRefused_AndTheUndosOutcomeIsShownAndRecorded`,
`ThePanelClearedWhileTheUndoRuns_CannotTurnItsOutcomeIntoAnException` and
`TheUndoPanelsCloseButtons_AreDisabledWhileAnUndoRuns`; its authorization substitute can hold or fault a
policy's answer, the page is given the harness's `Audit` and the entry its `FullTarget`.
`AllowlistConcurrentSaveTests.TheRepositorysWholeTableSave_IsNotPublic`. One existing scan follows the
change: `ReversedOperationsTests.ThePageTakesItsMarkersFromTheRule` pinned the in-session add's text as
`reversedOperationIds.Add(undoPreview.OriginalOperationId)`; it now pins
`reversedOperationIds.Add(preview.OriginalOperationId)`, the captured preview - still exactly one add, of
the preview's operation id. Outside the plan's file list by necessity, not scope.

Caller-visible change, by design: an undo no module claims, and one the page ends with an exception before
its module is called, each leave one `AdminEventLog_Undo` record (category `AdminEventLog`, Failed,
`mutationAttempted` false; `refusedAt: NoHandler` for the first). While an undo runs the panel's X and Cancel
are disabled and a click on the backdrop does nothing; the panel closes as before once the undo has ended.
The messages the operator sees are unchanged. Nothing in the app called the repository's SaveAllowlist.

## Guard proof

No separate pre-fix reproduction: each part of the fix is reverted at its own call site below - M1 and M2
are the two exits as they were, M3 the catch's log line as it was, E1 the close as it was, E2 the
repository's writer as it was - and each fails the test that names it.

Session-local driver (`mutate.ps1`, session scratchpad `a28m-a26j-work`, copied from the a28l/a26g/a26i
round): exact bytes saved, one literal replacement that must match exactly once, Release build with `0
Error(s)` required, filter
`FullyQualifiedName~EventLogUndoRefusalTests|FullyQualifiedName~AllowlistConcurrentSaveTests|FullyQualifiedName~ADAttributeEditorUndoRefusalTests|FullyQualifiedName~AllowlistSaveAuditTests|FullyQualifiedName~AllowlistBannerTests|FullyQualifiedName~ADAttributeEditorPatternTests|FullyQualifiedName~ADAttributeEditorServiceTests|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~PageAuthorizationRecheckTests|FullyQualifiedName~UndoNotificationTests|FullyQualifiedName~PageProtectionGateServicingTests|FullyQualifiedName~AuditCategoryFilingTests`
(167 tests), exact bytes restored in `finally`, MD5 re-checked, mtime advanced so MSBuild rebuilds (log
`a28m-mutations.log`). Every mutant built with 0 errors; each restore matched its original MD5. After the
round, a rebuild on the restored bytes and the filter: 167/167 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named test failing |
|---|---|---|---|
| M1 the no-handler record dropped | `AdminEventLog.razor`, `A1BB2EE1D48262DA116491FB056B7E30` | `8AA5434326509986366E32B2ADD121DF` | `AnUndoNoModuleClaims_LeavesOneRefusalRecord` (no record) |
| M2 the catch's record dropped | `AdminEventLog.razor`, `A1BB2EE1D48262DA116491FB056B7E30` | `0C7D707958A5FF62127557C2B207CCE1` | `AnUndoThatThrowsBeforeItsModuleIsCalled_LeavesOneFailureRecord` (both cases), `ACancelWhileTheUndoRuns_IsRefused_AndTheUndosOutcomeIsShownAndRecorded`, `ThePanelClearedWhileTheUndoRuns_CannotTurnItsOutcomeIntoAnException` (no record) |
| M3 the catch's log line reading `undoTargetEvent.OperationId` again, as at `762c774c` | `AdminEventLog.razor`, `A1BB2EE1D48262DA116491FB056B7E30` | `7E505D66358109E5F43C922CE1BE7C6A` | `ThePanelClearedWhileTheUndoRuns_CannotTurnItsOutcomeIntoAnException` only - with the field cleared mid-run the catch throws out of the handler; the Cancel test passes, as it must, because the guarded close no longer clears it |
| extra E1 CloseUndoPanel's `undoExecuting` guard dropped | `AdminEventLog.razor`, `A1BB2EE1D48262DA116491FB056B7E30` | `32BAB80031319FBE1737E9B72832E865` | `ACancelWhileTheUndoRuns_IsRefused_AndTheUndosOutcomeIsShownAndRecorded` only (the panel closed mid-run); `ThePanelClearedWhileTheUndoRuns_CannotTurnItsOutcomeIntoAnException` still passes - the locals, not the guard, keep the outcome recorded |
| extra E2 `SaveAllowlist` public again | `AttributeEditorRepository.cs`, `40B5B0E09D7758F1AE889DFA8FA3D83E` | `59959A735F692E0F2106CD3F0B658550` | `TheRepositorysWholeTableSave_IsNotPublic` |

Unchanged through the round: `EventLogUndoRefusalTests.cs` `6A3B3BB5FB6C726710C450A1D1BDDA4A`,
`AllowlistConcurrentSaveTests.cs` `AB0B19E72B6963426A0A1FF043E0DBC2`, `ReversedOperationsTests.cs`
`73D0036C8B91E1A61C6E944E0DF9AC08`. The code comments are written after the round, in their own commit, so
the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline
  count - none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is
  written): exit 0, 5,318 passed / 3 skipped (the three known AD skips,
  `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`) / 0 failed, 5,321 total, 5m20s;
  nothing hung, no Sequence file. Baseline at `762c774c`: 5,311 / 3 / 0, 5,314 total; the seven new cases
  are this record's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.
- `ClickGate*` 316/316 and ProgressRegistryTests 35/35 run alone on this tree (Release), and among the
  suite above.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. The page was not rendered in
a browser and no live circuit was run. No coder self-review verdict is claimed.

## Known gaps

- ShowUndoPreview is not refused while an undo runs. The table's Undo buttons sit behind the backdrop but stay
  keyboard-reachable, so a second preview opened mid-run replaces the panel and the first undo's outcome then
  shows on it. The running undo is unaffected - it acts on its locals. A candidate, not fixed here.
- An exception out of ExecuteUndoAsync is the module's to record, and the page records nothing once it has
  called the module. ADAttributeEditorUndoService records every exit inside its try and each of its three
  refusals before it; the BeginOperation before its try is outside its catch, and is not known to throw (its
  trace write goes through the JSONL writer, which logs and swallows its IO errors). Not driven.
- The no-handler exit is defensive: the panel offers Confirm Undo only after a handler previewed the event,
  and FindHandler is a pure function of the event, so the rendered panel cannot reach it. Recorded anyway.
- The page records are audited, not announced (aud-a28d's known gap). They carry no old/new values: the page
  holds the module's record shape only through the module.
- ExecuteUndo and CloseUndoPanel are driven through the compiled page by reflection on a do-nothing renderer,
  not through a rendered circuit; the disabled attributes are pinned by a source scan only.

## Comments after the round

Written after both rounds, in their own commit with aud-a26j's. Checked by `check_comment_diff.py` (session
scratchpad, from the a28l round) over `git diff 73eeb7da -U0` before that commit: every added or removed line
is a comment or blank line, so each file's code lines are unchanged and in order. This record's files:
`Components/Pages/AdminEventLog.razor` `A1BB2EE1D48262DA116491FB056B7E30` -> `AB449695A06A9557A63F7D665F0D28A8`
(why CloseUndoPanel refuses, why the handler reads its inputs once, why the no-handler exit and the catch
record and the module call ends that, LogUndoNotRun's remarks; and the comment above the activity, which said
undoError "is not written by any other control while it is suspended" - false while the close wrote it - now
says why the finally's read is this run's outcome); `Services/Storage/AttributeEditorRepository.cs`
`40B5B0E09D7758F1AE889DFA8FA3D83E` -> `A3EABBCDAE5244551F0A6843023208D1` (why SaveAllowlist is internal);
`EventLogUndoRefusalTests.cs` `6A3B3BB5FB6C726710C450A1D1BDDA4A` -> `8524E535822C164D582F890C3F91A0ED`;
`AllowlistConcurrentSaveTests.cs` `AB0B19E72B6963426A0A1FF043E0DBC2` -> `D193763CC6AC43469E528A70D8A56DDE`.
`ReversedOperationsTests.cs` is unchanged (`73D0036C8B91E1A61C6E944E0DF9AC08`).
