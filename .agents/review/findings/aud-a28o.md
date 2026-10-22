# aud-a28o: The event log's undo can be re-entered or re-targeted mid-run, its service's BeginOperation sits outside the try, and an unconfirmed save records no pre-write value

**Severity**: LOW. (1) `ShowUndoPreview` is not refused while an undo runs; the table's Undo buttons
behind the backdrop are reachable by keyboard, so a second preview mid-run replaces the panel and
shows the first undo's outcome on it. (2) `ExecuteUndo` has no re-entry guard beyond the disabled
Confirm button (the ClickGate class; the page's conversion is tier 4, not approved): a double-click
before the re-render can start two undos. (3) `ADAttributeEditorUndoService.ExecuteUndoAsync` calls
`BeginOperation` before the try and outside its catch; not known to throw, but if it did there would
be no record. (4) An unconfirmed attribute save's record (aud-a26j) has no pre-write value and its
trace step lists no changes, so a reviewer cannot see what the editor tried to set.
**Status**: Fixed on branch; closed on guard proof; awaiting merge and review
**Branch**: `worktree-agent-a6af37cca287ebc2c`
**Commit**: `6d4bd8c7` (comments `97592e99`; plan `4684d84d`)
**Reviewer**: implementer (surfaced by the aud-a28m/a26j agent, 2026-10-22, candidates 1 to 4 of its hand-back)

## Evidence

At `6008a986`: `Components/Pages/AdminEventLog.razor` `ShowUndoPreview`, `ExecuteUndo` (no
`undoExecuting` re-entry check at the top), `Services/ADAttributeEditorUndoService.cs`
`ExecuteUndoAsync` (`BeginOperation` placement), `Services/ADAttributeEditorService.cs` `ConfirmWrite`'s
failed record (`LogAudit(target, [], ...)`).

## Approach (to be written by the implementing agent before code)

- `ShowUndoPreview` and `ExecuteUndo` return at once while `undoExecuting` (re-entry guard at the top
  of the handler, not only the disabled button); this is a guard, not a ClickGate conversion - the
  page stays `NotYetConverted` and gains no `ActionsDisabled`.
- Move `BeginOperation` inside the try so a throw there is recorded by the existing catch.
- The unconfirmed save's record carries the attempted changes as `attempted_<name>` (old and
  proposed) in `extra`, not as old/new pairs (so `CanUndo` stays false), and the trace step lists them.
- Tests in the `EventLogUndoRefusalTests` pattern: a preview requested mid-run is refused; a second
  `ExecuteUndo` mid-run returns without a second record; a throwing `BeginOperation` leaves one failure
  record; the unconfirmed save's record carries the attempted values. Mutations at the call site:
  drop each guard; move `BeginOperation` back out; drop the attempted values.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28m/a26j hand-back (`a8ae8dd9`). One
commit `fix(event-log,ad-attribute-editor): ... (aud-a28o)`, third of three on branch
`worktree-agent-a6af37cca287ebc2c`. Behaviour: Admin Event Log (the two guards) and AD Attribute Editor (the
undo's trace inside its try; the unconfirmed record's attempted values) - both module versions due; the
supervisor owns the bumps. Evidence re-read at `afa9dbf6`: `Components/Pages/AdminEventLog.razor` ShowUndoPreview
910-948 replaces undoTargetEvent, undoPreview and undoTicket (912-917) with no undoExecuting check, and the
table's Undo button (279) stays keyboard-reachable behind the backdrop; ExecuteUndo 967-1086 checks the target,
the preview and the ticket (975) and raises undoExecuting only at 983, so two calls before the re-render both
pass; Confirm is disabled while undoExecuting (429). `Services/ADAttributeEditorUndoService.cs` ExecuteUndoAsync:
`using var scope = _operationTrace.BeginOperation(...)` (194-201) sits before the try (203), outside the catch
that writes the failure record (328-345). `Services/ADAttributeEditorService.cs` PerformSave records an
unconfirmed save as `LogAudit(target, [], ...)` (1466): no changes in the record nor in the AttributeWriteCompleted
trace step (1821).

1. **Page guards.** `if (undoExecuting) return;` first in ShowUndoPreview and first in ExecuteUndo. No record: a
   second click is not a second attempt, and the running undo records its own outcome. Not a ClickGate
   conversion - the page stays NotYetConverted ("tier 4, not approved") with no ActionsDisabled and no line-keyed
   entry, so nothing re-anchors. ProgressRegistry: both guards return before `Progress.Begin`, so the pinned
   calls (`handler.PreviewUndoAsync`, `handler.ExecuteUndoAsync`) stay inside their windows.
2. **BeginOperation inside the try.** `OperationTraceService.OperationScope? scope = null;` before the try, the
   operation begun as the try's first statement, the catch completing it when there is one (`scope?.Complete`),
   a finally disposing it (the using's disposal, same order). A throw there reaches the existing catch: one failed
   `ADAttributeEditor_Undo` record, the exception-path notice, a failed result. Seam: the call goes through
   `internal virtual BeginUndoOperation(...)` (the editor service's CreateDirectoryRunspace shape) so a test can
   make it throw; production begins exactly the operation it begins now.
3. **The unconfirmed save names what it tried.** LogAudit gains an optional `attempted`: for each change the
   record carries `attempted_<name>` = `{ old, proposed }` - a nested object, whose nulls the writer keeps (it
   drops top-level nulls only; checked on this SDK) - and the trace step `attemptedChanges` (name, old,
   proposed); `changedAttributes` and the `old_`/`new_` pairs stay empty, so the record is not undoable (Failed,
   and no changed attributes). PerformSave passes it for both unconfirmed outcomes (not confirmed; could not be
   confirmed).
4. Comments after the mutation round, in their own commit.

Tests: EventLogUndoRefusalTests (the aud-a28l/a28m pattern) - (a) a preview requested while an undo runs returns
at once, no preview is built, the panel keeps the event, preview and ticket the undo was confirmed for, and the
undo's outcome is shown and recorded once; (b) a second ExecuteUndo while one runs completes at once with no
record of its own; when the first ends there is one record. ADAttributeEditorUndoRefusalTests - (c) a
BeginOperation that throws: nothing escapes, one failed `ADAttributeEditor_Undo` record carrying the error and no
`refusedAt`, nothing written, no directory session, one failure notice. ADAttributeEditorSaveTests - (d) the
unconfirmed saves' records carry `attempted_title` (old Analyst, proposed Lead Analyst) and their trace steps list
it; a clear's proposed is null. Mutations at the call site: M4 ExecuteUndo's guard dropped - (b) fails; M5
BeginOperation back before the try - (c) fails; M6 the attempted values dropped - (d) fails; extra E1
ShowUndoPreview's guard dropped - (a) fails.

Files: `Components/Pages/AdminEventLog.razor`, `Services/ADAttributeEditorUndoService.cs`,
`Services/ADAttributeEditorService.cs`, `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`,
`ExchangeAdminWeb.Tests/ADAttributeEditorUndoRefusalTests.cs`, `ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`,
`ExchangeAdminWeb.Tests/AttributeEditorHarness.cs` (the seam's subclass, a trace reader), this record.

## Fix

- `Components/Pages/AdminEventLog.razor`: `if (undoExecuting) return;` as the first statement of ShowUndoPreview
  and of ExecuteUndo. NotYetConverted in ClickGateRegistry ("tier 4, not approved"), no line-keyed entry, so no
  key moves; both guards return before `Progress.Begin`.
- `Services/ADAttributeEditorUndoService.cs`, ExecuteUndoAsync: `OperationTraceService.OperationScope? scope =
  null;` before the try, `scope = BeginUndoOperation(...)` its first statement, the catch's `scope?.Complete(false,
  ex.Message)`, a new `finally { scope?.Dispose(); }` in place of the using. New `BeginUndoOperation` (internal
  virtual, the test seam): the `BeginOperation` call it replaces, unchanged.
- `Services/ADAttributeEditorService.cs`: LogAudit's optional `attempted` adds `attemptedChanges` (name, oldValue,
  proposedValue) to the AttributeWriteCompleted trace step and `attempted_<name>` = `{ "old": ..., "proposed": ...
  }` to the record; PerformSave's unconfirmed save passes `attempted: changes`. The record's `changedAttributes`
  stays empty and it has no `old_`/`new_` pair.

Harness (`ExchangeAdminWeb.Tests/AttributeEditorHarness.cs`): `CreateUndoServiceWhoseTraceFails(message)`, over a
private `TraceFailingUndoService` whose BeginUndoOperation throws; `TraceEvents()` beside `AuditEvents()`, both
through one reader; CreateUndoService's enablement setup moved into a helper both use.

Tests: `EventLogUndoRefusalTests` -
`APreviewRequestedWhileAnUndoRuns_IsRefused_AndTheUndosOutcomeStaysOnItsPanel`,
`ASecondConfirmWhileAnUndoRuns_ReturnsAtOnce_AndLeavesNoRecordOfItsOwn` (PageConfirming's log row now built by an
`Entry` helper the preview test also uses); `ADAttributeEditorUndoRefusalTests` -
`AnUndoWhoseTraceCannotBegin_LeavesOneFailureRecord`; `ADAttributeEditorSaveTests` -
`AnUnconfirmedSave_RecordsWhatItTriedToSet_NotAsAPairToUndo` (not confirmed; could not be confirmed).

Caller-visible change, by design: while an undo runs, the event log's Undo buttons and a second Confirm do nothing;
the running undo's panel, outcome and record are its own. An undo whose trace operation cannot begin is a failed
`ADAttributeEditor_Undo` record with a failure notice instead of an exception out of the module. An unconfirmed
attribute save's record and trace step name each attribute's value before the write and the value proposed.

`AdminEventLog.razor`: 1,558 lines before, 1,564 after; NotYetConverted in ClickGateRegistry, so no line key moves.

## Guard proof

No separate pre-fix reproduction: M4 and E1 are the two handlers as at `afa9dbf6` (no re-entry guard), M5 the
operation begun before the try as at `afa9dbf6`, M6 the unconfirmed record as at `afa9dbf6` (no attempted values);
each fails the test that names it.

Same driver and conditions as aud-a26l (`a28o-mutations.log`), the filter widened to the page's other tripwires:
`FullyQualifiedName~ADAttributeEditor|FullyQualifiedName~AttributeEditorPageSaveAuditTests|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~UndoNotificationTests|FullyQualifiedName~UndoIdentityTests|FullyQualifiedName~EventLogUndoRefusalTests|FullyQualifiedName~Allowlist|FullyQualifiedName~PageAuthorizationRecheckTests|FullyQualifiedName~Progress|FullyQualifiedName~ClickGate`
(668 tests). Every mutant built with 0 errors; each restore matched its original MD5. After the round, a rebuild on
the restored bytes and the filter: 668/668 green. (The round was run twice: the first time on a test file whose
`Task.Delay` raised xUnit1051; the token was passed and the round re-run on these bytes, with the same verdicts.)

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named test failing |
|---|---|---|---|
| M4 ExecuteUndo's re-entry guard dropped | `AdminEventLog.razor`, `24B3D9EB7303E3E70007015FC5131785` | `15476CEE216194941708597C9865AEB5` | `ASecondConfirmWhileAnUndoRuns_ReturnsAtOnce_AndLeavesNoRecordOfItsOwn` only - the second call passes every check the first did and waits on its held authorization check instead of returning |
| M5 BeginOperation back before the try | `ADAttributeEditorUndoService.cs`, `D28863B61DF5580BBE92C1FA7313D288` | `7D200207953DFE479617CF1CAB063B97` | `AnUndoWhoseTraceCannotBegin_LeavesOneFailureRecord` only - the throw escapes ExecuteUndoAsync instead of reaching its catch |
| M6 the attempted values dropped from the unconfirmed save's record | `ADAttributeEditorService.cs`, `8A8C96CCDDD10ECCF34A658693AC8582` | `7B32ADBC2E729B40BB732FE3130B354F` | `AnUnconfirmedSave_RecordsWhatItTriedToSet_NotAsAPairToUndo`, both cases |
| extra E1 ShowUndoPreview's re-entry guard dropped | `AdminEventLog.razor`, `24B3D9EB7303E3E70007015FC5131785` | `5ED252B12AD033534C493AB264B035F7` | `APreviewRequestedWhileAnUndoRuns_IsRefused_AndTheUndosOutcomeStaysOnItsPanel` only - the preview replaces the panel the undo was confirmed on |

Unchanged through the round: `EventLogUndoRefusalTests.cs` `94E350B12ACC22F172C248E3D87C10A8`,
`ADAttributeEditorUndoRefusalTests.cs` `F3402FA6B7F0D29A65196F6AF5E6CAF2`, `ADAttributeEditorSaveTests.cs`
`A41EF2E291008EEF5A6D8D890A50ADE1`, `AttributeEditorHarness.cs` `A05AD976CDD631899932CDEB352FD869`. The code comments
are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental --no-restore`: 0 errors, 41 warnings, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --no-build --blame-hang --blame-hang-timeout 10m` (Debug, after a Debug
  build of this tree): exit 0, 5,453 passed / 3 skipped (the three known AD skips) / 0 failed, 5,456 total, 4m58s;
  nothing hung, no Sequence file. aud-a26m's tree (`d0edf1ea`): 5,448 / 3 / 0, 5,451 total; the five new cases are
  this record's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1`: exit 0. `ClickGate*`, `ProgressRegistryTests` and `PageAuthorizationRecheckTests`
  among the 668 above, green.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. The page was not rendered in a
browser and no live circuit was run. No coder self-review verdict is claimed.

## Known gaps

- ShowUndoPreview is guarded while an undo RUNS, as the record asks, not while a preview LOADS: two previews
  requested back to back (keyboard, behind the backdrop) can still finish out of order, leaving one event's preview
  on the panel beside the other event as the target. A candidate, not fixed here.
- The guards record nothing: a second click is not a second attempt, and the running undo records its own outcome.
- ExecuteUndo and ShowUndoPreview are driven through the compiled page by reflection on a do-nothing renderer, not
  through a rendered circuit (aud-a28m's harness).
- The trace seam is a virtual method on the undo service; production begins exactly the operation it began before.

## Comments after the round

Written after all three rounds, in their own commit (`97592e99`) with aud-a26l's and aud-a26m's; the same check (only
comment and blank lines changed in `git diff -U0`). This record's files: `Components/Pages/AdminEventLog.razor`
`24B3D9EB7303E3E70007015FC5131785` -> `33B6FC7ABDA35AC6F64C80752B36968C` (why each handler refuses while an undo runs;
1,564 lines -> 1,571, still NotYetConverted, no key to move); `Services/ADAttributeEditorUndoService.cs`
`D28863B61DF5580BBE92C1FA7313D288` -> `3FFC152A18B26FEE1E9C23A4D8B142BD` (why the operation begins inside the try, the
seam's remarks); `Services/ADAttributeEditorService.cs` `8A8C96CCDDD10ECCF34A658693AC8582` ->
`050D3F534AD1A4CBA9183F880805B977` (why an unconfirmed save records attempted_<name> and not a pair, why a nested
object). The tests and the harness are unchanged (MD5s as in the guard proof).

## Branch-tip gates - 2026-10-22 (`97592e99`: the three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none
  in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,453 passed / 3 skipped (the three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`)
  / 0 failed, 5,456 total, 5m12s; nothing hung, no Sequence file. Baseline at `4684d84d` (the code as at `afa9dbf6`):
  5,427 / 3 / 0, 5,430 total; the twenty-six new cases are this branch's (aud-a26l seventeen, aud-a26m four,
  aud-a28o five).
- `ClickGate*` 316/316 and `ProgressRegistryTests` 35/35, also run alone (Release).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

## Closeout

Fixed on branch `worktree-agent-a6af37cca287ebc2c`, not merged, not pushed. Two module-scoped behaviour changes: the
Admin Event Log (its two re-entry guards) and the AD Attribute Editor (an undo whose trace cannot begin is a recorded
failure; an unconfirmed save's record names what it tried): both module versions are due. No shared code changed, so
no base app bump. The version bumps are the supervisor's, at merge: `Modules/ModuleCatalog.cs` and
`ExchangeAdminWeb.csproj` were outside this brief.
