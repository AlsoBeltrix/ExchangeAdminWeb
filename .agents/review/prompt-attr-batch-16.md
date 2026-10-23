# Defect review request: a failed JSONL rotation never drops a row and the log folder is recreated per write (aud-a56); the telemetry switch reads once and fails closed, the config store and event log load cannot throw for a logging failure (aud-a57); configuration Booleans read with a stated default and one warning (aud-a58)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `6b6f642a` or later.
- The change is the branch merged at `3da510cb`: `c3960fe9` (plans), `0ccdc758` (aud-a56), `278dff50` (aud-a57),
  `81333106` (aud-a58), `173613a7` (comments only), `e8242003` (record SHAs), forked from `a7300dee`. Review
  `git diff a7300dee e8242003`, and read the COMBINED `Services/JsonlFileAppender.cs` (`Append`'s `rotationFailed`
  callback; the folder creation before the lock; the new gap-aware `Rotate`), `Services/JsonlLogService.cs` (the
  rotation-failure streak and its one warning; `RotateLogFiles` through `Rotate`), `Services/ExtendedLogService.cs`
  (the same), `Services/UsageTelemetryService.cs` (`ReadSwitch`'s one `TryGetModuleConfig` read),
  `Services/ModuleConfigService.cs` (`ReadModuleConfig`'s guarded log), `Components/Pages/AdminEventLog.razor`
  (`ReportSafely`; the two load catches), `Services/ConfigBoolean.cs` (new), `Components/Pages/Home.razor`,
  `Services/EmailService.cs` (`_smtpUseSsl`, `_notifyUsers`), `Services/ServiceNowService.cs` (`_enabled`), `README.md`
  ("Rotation", "Boolean settings"), and the tests `JsonlWriterRotationTests.cs`, `ConfigBooleanTests.cs`,
  `UsageTelemetryServiceTests.cs`, `ModuleConfigServiceTests.cs`, `EventLogDiagnosticsTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a56.md` (its amendment: gap-aware rotation; folder before the lock),
  `aud-a57.md`, `aud-a58.md` (its amendment: `Email:SmtpUseSsl` and `ServiceNow:Enabled` read a non-Boolean as TRUE,
  the restrictive reading; the supervisor accepted it); parents `aud-a51.md`, `aud-a48.md`, `aud-a49.md`, `aud-a28z.md`,
  `aud-a45.md`, `aud-a43.md`. ALREADY admitted and out of scope: `aud-a62` (numbers and a Boolean parsed in singleton
  constructors: `OperationTrace:Enabled` via `GetValue<bool?>`, `Email:SmtpPort` via `int.Parse`, the writers' file
  limits via `GetValue<int?>/<long?>`), `aud-a63` (the `ExecuteUndo` catch and the config store's post-write
  `LogInformation` unguarded), `aud-a64` (the remaining unguarded logger calls, the constructors' folder creation, the
  telemetry report flag). Do not re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure
  Classes" (1, 2, 3); `docs/ProjectConstitution.md` (audit rules; credential isolation).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 3da510cb`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a56: the gap-aware `Rotate` - with places 1..N, "deletes the oldest only when every place
is taken" and "moves never overwrite": trace a steady state where every place is taken and the active file fills -
the oldest is deleted and the chain moves; now trace a failure at the move of place 2 to 3 (not the last move): what
state is left, and does the next retry skip, overwrite or double-move; the row written past the limit when rotation
fails - unbounded growth of the active file while the failure persists (a SIEM tailer holding the file for days): is
there any ceiling or second report; the streak `HashSet` read under `_lock` for the JSONL writer vs the extended
log's field "only the one consumer touches" - `Dispose`'s second reader (aud-a49) also appends: is the field raced;
the folder creation before the lock - `Directory.CreateDirectory` on an existing path is cheap on NTFS but is a
network share round-trip per write on SMB (the record's SMB assumption) - cost named?; `DirectoryNotFoundException`
now mapped to `JsonlLockRefusedException` at once - text says "the folder refused" for a folder that does not exist:
accurate enough for the operator? a57: `ReadSwitch` one read - the cached unreadable answer for 30 s: during those
30 s telemetry is OFF; the Home page's `Usage.Enabled()` shows what; `IsModuleCorrupt` no longer consulted - was it
answering anything `TryGetModuleConfig` does not (a corrupt-row distinction now lost); the event log's `ReportSafely`
- same shape as the extended log's. a58: `ConfigBoolean.Read` once-per-process set keyed by (setting, value) - a static
set that grows with every distinct bad value seen (a bounded set? a config value changes rarely - fine, but name it);
`EmailService` `_smtpUseSsl` true on a bad value: `SecureSocketOptions.SslOnConnect` vs `StartTls` - "TLS on" means
which, and does a relay that offers only STARTTLS now refuse (a behaviour change on a bad value only - acceptable, but
confirm the record says which); `ServiceNowService._enabled` true on a bad value - `ValidateTicketAsync` then calls a
ServiceNow instance that may not be configured: what does the ticket check report (refuse the write with a reason, or
throw into the page); the source check's two exemptions (the owned pages at line 22) - still present on master after
the other agent's merge, or already fixed (check master's pages). Tests passing for the wrong reason: the rotation tests'
simulated move failure vs a real sharing violation; `ConfigBooleanTests`' once-per-process cases ordering across the
test run (static state shared with other test classes).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
