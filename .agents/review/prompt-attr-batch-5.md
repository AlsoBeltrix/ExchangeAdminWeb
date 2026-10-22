# Defect review request: the event log's undo refusal recorded, the unaudited allowlist writer removed, the dead Set-ADUser branch removed, multi-section records searchable (aud-a28l, aud-a26g, aud-a26i)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `3dfa54f8` or later.
- The change is the branch merged at `f0cccea8`: `1aeaa315` (plans), `5dc2e6e4` (aud-a28l),
  `748d358d` (aud-a26g), `4a538d40` (aud-a26i), `ee00d337` (comments only), `d474c270` (record SHAs),
  forked from `374718b3`. Review `git diff 374718b3 d474c270`, and read the COMBINED
  `Components/Pages/AdminEventLog.razor` (`ExecuteUndo`, the target text and CSV export),
  `Services/IUndoableModule.cs`, `Services/ADAttributeEditorUndoService.cs` (`LogUndoRefused`),
  `Services/ADAttributeEditorService.cs` (`PerformSave`'s `Set-ADUser` write; `SaveAllowlist` gone),
  `Services/Storage/AttributeEditorRepository.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28l.md`, `aud-a26g.md`, `aud-a26i.md`; parents `aud-a28d.md`,
  `aud-a26c.md`, `aud-a28g.md`, `aud-a28h.md`, `aud-a28i.md`, `aud-a2o.md`. ALREADY admitted and out of
  scope: `aud-a28m` (`ExecuteUndo`'s no-handler return and its catch write no record; Cancel during
  an undo nulls `undoTargetEvent` so the catch's log line throws out of the handler; the repository's
  `SaveAllowlist` is still public) and `aud-a26j` (`PerformSave`'s `Set-ADUser` write is not confirmed
  by a read-back). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known
  Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md` audit rules.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat f0cccea8`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a28l: the new interface member on `IUndoableModule` (any other
implementer; DI registration); the refusal record's shape versus aud-a28d's (fields; `originalOperationId`
set but result Failed - does `ReversedOperations` leave the button; does any consumer treat a Failed
undo record with `originalOperationId` as a reversal); removing `SaveAllowlist` (the aud-a28e
uncompilable-pattern throw went with it - is every former caller's expectation met by the audited
PatternCheck refusal); the source-grep tripwire's coverage. a26g: the try/catch/finally; the success
record reached only on no-throw; `-ErrorAction Stop` load-bearing (pinned?). a26i: the target text
`ModuleEnablement (sections: ...)` in table, tooltip, expanded row, CSV - CSV escaping of commas and
quotes; a very long sections list; filtering by text matching the sections. Tests passing for the
wrong reason: reflection-driven `ExecuteUndo` on a do-nothing renderer; the harness knobs.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
