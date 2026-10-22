# Defect review request: every undo exit on the event log recorded and a mid-run Cancel made safe (aud-a28m); the attribute editor's write confirmed by read-back (aud-a26j)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `11191b77` or later.
- The change is the branch merged at `6008a986`: `e0ac383f` (plans), `6bc5e7e9` (aud-a28m), `73eeb7da`
  (aud-a26j), `567b2014` (comments only), `063b21c6` (record SHAs), forked from `762c774c`. Review
  `git diff 762c774c 063b21c6`, and read the COMBINED `Components/Pages/AdminEventLog.razor`
  (`ExecuteUndo`, `LogUndoNotRun`, `CloseUndoPanel`, the category filter), `Services/ADAttributeEditorService.cs`
  (`PerformSave`, `ConfirmWrite`, `UnconfirmedWrites`), `Services/ADAttributeEditorUndoService.cs`,
  `Services/Storage/AttributeEditorRepository.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28m.md`, `aud-a26j.md`; parents `aud-a28l.md`, `aud-a26g.md`,
  `aud-a28d.md`, `aud-a26c.md`, `aud-a2p.md`. ALREADY admitted and out of scope: `aud-a26l` (the
  read-back's strict ordinal comparison falsely fails values the directory stores in its own form -
  DN case, Boolean `True`; a functional regression to be fixed before deploy) and `aud-a28o`
  (`ShowUndoPreview` and `ExecuteUndo` re-entrant mid-run; `BeginOperation` outside the try; the
  unconfirmed save's record has no pre-write value). Do not re-raise them. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md` audit rules.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 6008a986`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a28m: the records filed under `AdminEventLog` / `AdminEventLog_Undo` (any
consumer - `ReversedOperations`, the module filter, CSV - that treats them unexpectedly); exactly one
record per attempt (the `moduleCalled` local: an exception from `ExecuteUndoAsync` itself, which
records its own failure - no double; an exception from the trailing `LoadEvents` after a recorded
success - no record, correct?); the captured locals (target, preview, ticket, ip) versus what the
confirm dialog showed; `CloseUndoPanel` refusing while running, the backdrop; `undoExecuting` lowered
in the finally. a26j: `ConfirmWrite` reading by objectGUID with no `-Server` (the write sends none) -
a DC other than the one that took the write answering a stale value (replication lag) and failing
a correct save (how likely; what the message tells the operator); `-Properties` of the written names
only; the confirmed record carrying the values read; the failed record with no old/new pairs
(`CanUndo` false); the undo path running the same read-back; the error-stream clear in the write's
finally now load-bearing (pinned by E3). Tests passing for the wrong reason: the harness knobs
`SetChangesNothing`, `SetStoresAs`, `ReadBackFailure`; the reflection-driven `ExecuteUndo`.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
