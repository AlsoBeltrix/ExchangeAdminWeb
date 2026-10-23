# Defect review request: configuration numbers and the trace switch read with a stated default instead of throwing in constructors (aud-a62); the event log's undo always writes its audit row and the config store's post-write logging cannot fail a saved change (aud-a63); a persistent rotation failure re-warns as the file grows and is shown on the event log (aud-a65)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `fbcddb6f` or later.
- The change is the branch merged at `a4fc0e29`: `46dacfdd` (plans), `0e6b8abc` (aud-a62), `44827a38` (aud-a63),
  `3c306bcc` (aud-a65 plan), `bdf013cf` (aud-a65), `eeb7d9d9` (record SHAs), forked from `4a7a202e`. Review
  `git diff 4a7a202e eeb7d9d9`, and read the COMBINED `Services/ConfigValue.cs` (new: `ReadInt`, `ReadLong`,
  `ReadDouble`, the once-per-value warning, the finite check), `Services/OperationTraceService.cs` (the optional logger),
  `Services/EmailService.cs` (the port), `Services/JsonlLogService.cs` and `Services/ExtendedLogService.cs` (the limits;
  the `RotationFailureStreak` use; `RotationFailures`), `Services/Jobs/BulkJobService.cs` (the three settings),
  `Services/RotationFailureStreak.cs` (new), `Services/ModuleConfigService.cs` (`ReportSafely`; the three post-write
  logs), `Components/Pages/AdminEventLog.razor` (`ExecuteUndo`'s catch order; the rotation-failure line), `README.md`
  (the config-number and rotation sections), and the tests `ConfigValueTests.cs`, `EventLogUndoRefusalTests.cs`,
  `ModuleConfigServiceTests.cs`, `JsonlWriterRotationTests.cs`, `ExtendedLogServiceTests.cs`, `EventLogDiagnosticsTests.cs`
  on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a62.md` (its amendment: the bulk job runner's three settings; hexadecimal forms
  no longer accepted), `aud-a63.md`, `aud-a65.md` (built: escalation and the diagnostics line; NOT built: the overflow
  files, with the reasoning - the supervisor accepted that); parents `aud-a58.md`, `aud-a57.md`, `aud-a56.md`,
  `aud-a28z.md`, `aud-a45.md`. ALREADY admitted and out of scope: `aud-a64` (the remaining unguarded logger calls; the
  writers' constructor folder creation; the telemetry report flag; the three BulkJobs range problems: overflow in
  `TimeSpan.FromDays`, a negative retention pruning everything, a negative limit as no limit). Do not re-raise them.
  Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat a4fc0e29`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a62: `ConfigValue.ReadInt` with `NumberStyles.Integer` and the invariant culture - a value
with a thousands separator or a trailing unit ("50 MB") reads as the default with a warning: fine; the `OperationTraceService`
constructor's optional logger defaulting to null - when the container injects it, is the registration unchanged
(singleton with the logger resolved) and does any test construct it with a positional argument that now shifts;
`Audit:MaxFileBytes` "not set" as null `whenBlank` - a blank string vs absent key both "not set" (the MB setting
applies) - confirm; the source check's allowlist of database and CSV parses - a new config parse disguised as a
database read escapes it (name the shape); hexadecimal no longer accepted - any documented setting that used it
(the record says none; spot-check `appsettings.json.sample`). a63: `ExecuteUndo`'s catch order - `LogUndoNotRun`
writes a row for an undo that threw BEFORE its module: for an undo that threw INSIDE its module (after the module
started), is a row written by the module's own path and is the catch's `LogUndoNotRun` correctly skipped (no double
row, no missing row); the `ModuleConfigService.ReportSafely` guard inside the lock - the lock is still held while the
provider is called: a slow provider holds the write lock (a latency concern, not a defect - name only if a provider
could block). a65: `RotationFailureStreak.Failed` - the size read "(guarded)" each failed write: a `FileInfo.Length`
per write on every row while the streak lasts (cost under load); the hourly repeat uses which clock (testable? the
test injects time?); the event log's rotation-failure line shown "whether or not diagnostics are on" - the operator
text names what clears it (release the hold or grant delete) - confirm; the state `replaced whole` per failure read
by the page thread - a torn read impossible (reference assignment) - fine; the extended log's streak field "the one
consumer touches" and `Dispose`'s drain (aud-a49) also appending via the same `AppendLine` - is the field raced
during shutdown. Tests passing for the wrong reason: the rotation tests simulating the move failure by a held handle
vs the ACL case; `ConfigValueTests` static once-per-value state shared with `ConfigBooleanTests` (distinct keys per
test?).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
