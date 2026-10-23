# Defect review request: the JSONL lock is the held handle and needs no delete right, a refused folder is reported as such (aud-a51); usage telemetry cannot throw for a logging failure (aud-a48); the Admin Event Log names unreadable trace files and unparseable lines, the extended log's shutdown counts what it could not write (aud-a49)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `0aad2d07` or later.
- The change is the branch merged at `eb141b30`: `5ed8a5a8` (plans), `3277a185` (aud-a51), `df58ee19` (aud-a48),
  `6372c392` (aud-a49), `c2d6215d` (comments only), `eb39a010` (record SHAs), forked from `132d6e97`. Review
  `git diff 132d6e97 eb39a010`, and read the COMBINED `Services/JsonlFileAppender.cs` (`Lock`, `HeldLock`, the
  access-refusal grace, `JsonlLockRefusedException`, the move-then-delete release), `Services/UsageTelemetryService.cs`
  (`ReportSafely`, `InsertQueued`), `Services/ExtendedLogService.cs` (`Dispose`'s proportional bound and the drain
  count; `SingleReader` false), `Components/Pages/AdminEventLog.razor` (the trace unreadable warning; the per-stream
  unparseable counts and their one line), `README.md` ("Shared log root": the rights named), and the tests
  `JsonlWriterLockTests.cs`, `UsageTelemetryServiceTests.cs`, `ExtendedLogServiceTests.cs`, `EventLogDiagnosticsTests.cs`
  on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a51.md` (its amendment records the measured lock behaviour and the two
  refinements: a 100 ms access-refusal grace, and move-then-delete release), `aud-a48.md`, `aud-a49.md`; parents
  `aud-a47.md`, `aud-a46.md`, `aud-a45.md`, `aud-a28z.md`. ALREADY admitted and out of scope: `aud-a56` (a failed
  rotation drops every row until rotation succeeds; the audit writer never recreates its folder), `aud-a57` (the
  telemetry switch's two-read fail-open; `ReadModuleConfig` and the event log load catches logging unguarded),
  `aud-a58` (`Home.razor`'s `bool.Parse` during render). Do not re-raise them. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3); `docs/ProjectConstitution.md` (audit rules).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat eb141b30`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a51: the lock is now a persistent `<active>.lock` opened `FileShare.None` - a reader or
a backup tool that opens the lock file (not the log) with read sharing: does the writer's exclusive open then fail
as "refused" (access) or "contention" (sharing), and which text does the operator see; the 100 ms grace for an access
refusal - a folder with a real deny ACE now costs 100 ms per write plus the drop: is the drop report emitted once per
streak or per row (log flood under a persistent ACL fault); move-then-delete release - the moved name `<lock>.<32 hex>`
left behind when the delete fails: does anything accumulate those files, and does the rotation or any enumeration
pick them up; `JsonlLockRefusedException` reaching the aud-a28z recovery path - the recovery log itself writes
through the same folder? (if the recovery path is the ILogger provider, fine; if it is the extended log, it hits the
same refused lock - trace the path); the README rights text vs `deploy.ps1`'s ACL grant (Modify) - consistent. a48:
`ReportSafely` swallowing - does any caller depend on an exception from `Record` to know the insert failed (a counter,
a health surface); `InsertQueued` made internal for tests - production path unchanged. a49: the per-stream unparseable
counts - counted on every load or cumulative across loads (a reload double-counts?); the one muted line's grammar
with zero in one stream; `Dispose`'s `max(5 s, 25 ms x queued)` up to 25.6 s - the host's shutdown timeout (IIS app
pool shutdown time limit is 90 s by default; `HostOptions.ShutdownTimeout` is 30 s by default in .NET): is 25.6 s
inside the host's bound or can the host kill the process mid-drain (then the count is lost too); `SingleReader`
false - any perf or ordering consequence for the consumer. Tests passing for the wrong reason: the deny-ACE test's
ACL setup on the CI runner (does it skip or pass vacuously when the ACL cannot be set); the proportional-bound test's
timing on a slow runner.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
