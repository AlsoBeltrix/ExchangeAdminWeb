# Defect review request: the JSONL writer reports written or dropped and its recovery logging cannot throw, a dropped audit row is a Failed AuditWritten step (aud-a28u); the attribute editor's lookup reads typed values and names binary attributes, a closed undo preview no longer blocks the next (aud-a26r)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `d886a1f8` or later.
- The change is the branch merged at `172902c2`: `7dcf2815` (plans), `23a88f20` (aud-a28u with the codex fold-in),
  `00e71f2d` (aud-a26r), `258e8b96` (comments only), `1227ec45` (record SHAs), forked from `6fc94167`. Review
  `git diff 6fc94167 1227ec45`, and read the COMBINED `Services/JsonlLogService.cs` (`Write`, `WriteAudit`,
  `WriteTrace`, `WriteToFile`, `JsonlWriteOutcome`, the nested try around the catch's `LogError`),
  `Services/AuditService.cs` (`WriteAuditEvent`: the "AuditWritten" step, the usage-telemetry call, how the
  operation's own outcome is derived), `Services/ADAttributeEditorService.cs` (`PerformLookup` through
  `ReadDirectoryValue`, `NotSingleTextRefusal`, the `byte[]` case), `Components/Pages/AdminEventLog.razor`
  (`ShowUndoPreview`, `CloseUndoPanel`, the request token) and the tests `AuditWriterThrowFreeTests.cs`,
  `ADAttributeEditorSaveTests.cs`, `EventLogUndoRefusalTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28u.md`, `aud-a26r.md`; parents `aud-a28q.md`, `aud-a26o.md`, `aud-a26l.md`,
  `aud-a28f.md`. ALREADY admitted and out of scope: `aud-a28z` (`ExtendedLogService`'s unguarded catch logging; a
  throwing `ToString` in a field `WriteAuditEvent` reads), `aud-a26u` (several-valued and binary attributes shown as
  editable text on the page; several binary values refused under the single-valued wording; a closed preview's
  progress bar lingers), `cdt-41` (NU1902/NU1903 package advisories). Do not re-raise them. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md` ("Audit failure must
  not make an already-completed operation appear to have failed").

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 172902c2`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a28u: `JsonlWriteOutcome` - the verdict "depends on how far the write got": a partial
line written then an IO failure (is the file left with a truncated line, and is that reported as `FileWriteFailed`
or `Written`); a `NotSerializable` record - is the record's identity (what operation, who) still logged so the gap
can be reconciled; the "AuditWritten" Failed step - does any consumer treat a Failed step in the trace as the
operation failing (the page's outcome banner, the event log's result column, the notification); the telemetry skip
- the count now differs from the audit row count in the OTHER direction when the trace write drops but the audit
write succeeded (which one is the source of truth); the nested try - is there any remaining path in `WriteToFile`
or its callers where a throw can escape (the `TextField` conversion, the `Dispose`, the lock). a26r: the lookup
through `ReadDirectoryValue` - a `byte[]` as one value, a `string[]` of one as its element: does the FORM now show a
value the save path refuses (the page's field vs the refusal); the undo conflict checks reading the lookup's values
- a conflict check comparing a typed description ("binary (N bytes)") against the stored text (false conflict or
missed conflict); the request token - a token compared by reference or value, two previews opened in one render,
the token cleared while the handler is mid-await; `CloseUndoPanel` lowering `undoLoading` - can the dropped answer
still write to a field the next preview reads. Tests passing for the wrong reason: the throwing-logger theory
(does it reach `WriteToFile`'s catch, or only the outer methods); the page driven by reflection (which members the
tests call vs what the markup binds).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
