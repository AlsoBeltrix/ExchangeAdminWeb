# Defect review request: throw-free extended log and audit recovery paths, one-write JSONL lines with truncate-back and tail repair, identity in dropped-row reports, Undetermined filed and filterable (aud-a28z parts 1-5); read-only reasons for several-valued and binary attributes, a closed preview takes its activity (aud-a26u); vulnerable packages raised with NuGet audit as a build error (cdt-41)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `6b71adcc` or later.
- The change is the branch merged at `36da9789`: `5a6e5a5d` (plans), `1756aeb4` (aud-a28z parts 1-4), `2904ec9b`
  (aud-a26u), `f42f23aa` (cdt-41), `efbea61b` (part-5 plan), `d54da14e` (aud-a28z part 5), `7698c096` (comments only),
  `44fed23b` (record SHAs), forked from `18530b58`. Review `git diff 18530b58 44fed23b`, and read the COMBINED
  `Services/ExtendedLogService.cs` (the guarded report helper, the consumer loop, `DroppedEntryCount`, `SetLevel`),
  `Services/AuditService.cs` (the guarded `ToString` reads, `LogMigrationCheck`), `Services/JsonlLogService.cs`
  (one-write lines, the truncate-back, the tail repair, the dropped-row report fields, eventType routing),
  `Services/ADAttributeEditorService.cs` (`NotSingleText`, `ReadOnlyAttributes`), `Components/Pages/ADAttributeEditor.razor`
  (the read-only cell), `Components/Pages/AdminEventLog.razor` (`undoPreviewActivity`, `CloseUndoPanel`, the Undetermined
  filter option), `ExchangeAdminWeb.csproj` and `ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj` (the pins and the
  audit PropertyGroup), and the tests `ExtendedLogServiceTests.cs`, `AuditWriterThrowFreeTests.cs`,
  `ADAttributeEditorSaveTests.cs`, `AttributeEditorPageSaveAuditTests.cs`, `EventLogUndoRefusalTests.cs`,
  `EventLogSectionsTargetTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a28z.md`, `aud-a26u.md`, `cdt-41.md`; parents `aud-a28u.md`, `aud-a28q.md`,
  `aud-a26r.md`, `aud-a23l.md`, `cdt-39.md`, `cdt-40.md`; `.agents/decisions.md` 2026-06-26 (SQLite advisory: do not
  suppress NU1903). ALREADY admitted and out of scope: `aud-a45` (`ConfigChangeWatcher.LogOnce` logs unguarded),
  `aud-a46` (the extended log consumer's own half-line hazard; `GetEntries` bare catch; `DroppedEntryCount` shown
  nowhere; DropOldest overflow uncounted). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  "Known Failure Classes" (1, 2, 3) and Architectural Invariant 2 (one shared config database); `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 36da9789`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a28z: the truncate-back - the file length is captured before the write and
`SetLength` on failure: with two writers (the app and the `db` command-line mode, or dev and prod instances sharing a
log root - do they?) a truncate can cut another process's completed row; the tail repair writes a newline into a file
another process is mid-write on; is the file opened with a share mode that makes either impossible, and is the
"one writer per file" assumption stated where an operator can read it; the repair is logged through the same logger
path - guarded; "(unreadable <TypeName>)" marker - does any consumer (the event log's parser, the undo preview) treat
that text as a value; eventType routing only when it is a string - a non-string eventType goes where; part 5's
three-way result - any OTHER filer of `result` for a migration check (the page catch's "Error" status) and any
dashboard counting "Ineligible" that now undercounts. a26u: `ReadOnlyAttributes` as an optional last record member -
any `with` or positional construction that silently leaves it null so the page offers an input for a binary
attribute; the read-only cell's "edit value stays the lookup's so it is never a change" - the aud-a26o/a26l change
detection on that text ("binary (16 bytes)") vs the directory's real value on the pre-write re-read: a false
conflict or a false no-change; `undoPreviewActivity` cleared only while the request is the panel's - two closes in a
row, a close during Begin. cdt-41: `NuGetAuditMode` all with NU1902/3/4 as errors - the CI and the deploy build now
fail on any new advisory anywhere in the transitive graph with no override path: is that the intended posture under
the 2026-06-26 decision, and does `.agents/push-policy.md` or the deploy pipeline have a step that would now fail at
an inconvenient moment (a hotfix); the e_sqlite3 2.1.12 pin against Microsoft.Data.Sqlite 10.0.7's bundle 2.1.11 -
does the bundle's native loader pick the pinned engine (the agent read `sqlite_version()` 3.53.3 through the test
output - from the app's own graph or the tests'); dev and prod opening one database with engines 3.53.3 and 3.49.1
until both deploy - any WAL or journal-mode feature in use that the older engine cannot read. Tests passing for the
wrong reason: the half-line tests drive a stream double or the real file; the page reflection drivers vs markup.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
