# Defect review request: the attribute editor's syntax-aware routed read-back, undoable clear-only edits, and the event log's re-entry guards (aud-a26l, aud-a26m, aud-a28o)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `fde9d7ea` or later.
- The change is the branch merged at `fee8a58b`: `4684d84d` (plans), `7b7acc41` (aud-a26l incl. the
  folded-in stale-DC item), `d0edf1ea` (aud-a26m), `6d4bd8c7` (aud-a28o), `97592e99` (comments only),
  `7fbe22c4` (record SHAs), forked from `afa9dbf6`. Review `git diff afa9dbf6 7fbe22c4`, and read the
  COMBINED `Services/ADAttributeEditorService.cs` (`PerformSave`, `ConfirmWrite`, `UnconfirmedWrites`,
  the syntax comparison, the `-Server` routing, `LogAudit`'s `attempted`), `Services/ADAttributeEditorUndoService.cs`
  (`CanUndo`, `ExecuteUndoAsync`, `BeginUndoOperation`), `Components/Pages/AdminEventLog.razor`
  (`ShowUndoPreview`, `ExecuteUndo` guards) on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a26l.md` (the comparison rules and four labelled assumptions),
  `aud-a26m.md`, `aud-a28o.md`; parents `aud-a26j.md`, `aud-a28m.md`, `aud-a26g.md`, `aud-a28d.md`,
  `aud-a26c.md`. ALREADY admitted and out of scope: `aud-a26o` (the preview race; `ComputeChanges`
  ordinal; the literal "null" in `GetString`; the preview path's own unrouted runspace; multi-valued
  pre-write re-read) and `aud-a28q` (the audit and trace writers are not throw-free:
  `JsonlLogService.WriteToFile` serialises outside its try; `OperationTraceService.SamName(null)`
  throws). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure
  Classes" (1, 2, 3), Architectural Invariant 7; `docs/ProjectConstitution.md` audit rules.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat fee8a58b`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a26l: the syntax rules - Boolean/integer decided from the TYPE READ
BACK (a Boolean attribute whose read returns null or a string); the four DN-valued names by NAME
(Invariant 7 - is a hard-coded attribute-name list acceptable, and what about other DN-syntax
attributes such as `managedBy` elsewhere); DN comparison split on unescaped commas via `ServerFromDn`'s
split (escaped commas inside an RDN value; multi-valued RDNs; spaces after `=`); String/Choice trimmed
ordinal (internal whitespace); a value that differs only by form confirmed and the READ value
recorded - the undo then reverses to the read form; `-Server ServerFromDn(dn)` on re-read, write and
read-back (a DN naming no domain going unrouted with a warning - a guess?); the DC-affinity
assumption. a26m: `CanUndo` with one side present per attribute; a record with no side; a Failed
record never undoable; the preview/execute reading a missing side as null. a28o: the two guards
versus `Progress.Begin`; `BeginUndoOperation` as an internal virtual seam; `attempted_<name>` nested
object with nulls kept (the writer drops top-level nulls only - verified?); `changedAttributes` empty
so `CanUndo` false. Tests passing for the wrong reason: harness `SetStoresAs` and the routing knobs;
the trace-failure seam.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
