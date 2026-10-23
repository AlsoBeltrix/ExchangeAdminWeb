# Defect review request: every JSONL audit and trace write under a cross-process lock file (aud-a47); the config change watcher's logging cannot throw into its readers (aud-a45); the extended log shares the locked appender, reports unreadable files and counts overflow (aud-a46)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `24eab6c9` or later.
- The change is the branch merged at `2a15e9ed`: `45a99de1` (plans), `867957f8` (aud-a47), `d552bd61` (aud-a45),
  `543f5d5f` (aud-a46), `ab666f85` (comments only), `3f449c42` (record SHAs), forked from `2df48c36`. Review
  `git diff 2df48c36 3f449c42`, and read the COMBINED `Services/JsonlFileAppender.cs` (the lock file: open mode,
  `DeleteOnClose`, the bounded retry, `JsonlLockTimeoutException`; the offset read, tail repair, write, truncate-back
  under the lock), `Services/JsonlLogService.cs` (its use of the appender; rotation under the lock),
  `Services/ExtendedLogService.cs` (the consumer through the appender; `GetEntries` returning `ExtendedLogRead`;
  the `itemDropped` callback; `Dispose`), `Services/Storage/ConfigChangeWatcher.cs` (`LogOnce`),
  `Components/Pages/AdminEventLog.razor` (`LoadEvents` reading `.Entries` and `Unreadable`; the diagnostics warning
  and the drop count), `README.md` ("Shared log root"), `appsettings.json.sample` (`_AuditNote`), and the tests
  `JsonlWriterLockTests.cs`, `ConfigChangeWatcherTests.cs`, `ExtendedLogServiceTests.cs`, `EventLogDiagnosticsTests.cs`
  on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a47.md` (its Approach explains why a named mutex was rejected), `aud-a45.md`,
  `aud-a46.md`; parents `aud-a28z.md`, `aud-a28u.md`, `aud-a28q.md`. ALREADY admitted and out of scope: `aud-a48`
  (`UsageTelemetryService`'s unguarded logger calls), `aud-a49` (an unreadable trace file not shown; unparseable
  lines dropped uncounted; `ExtendedLogService.Dispose`'s fixed 5 s wait vs a full queue). Do not re-raise them.
  Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3) and Architectural Invariant
  7; `docs/ProjectConstitution.md` (audit rules).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 2a15e9ed`. Report whether both succeeded.

## Mandate

Hunt for observable defects. a47: the lock file `<active>.lock` with `FileShare.None` + `DeleteOnClose` - on
Windows, `DeleteOnClose` with a second opener: can a second process's open attempt see the file in a
delete-pending state and get a different error than sharing violation, and does the retry treat every failure
the same (a permissions error retried for 2 s per write, then dropped - is that reported distinctly from
contention); the lock is taken per write - under load, N writers each waiting up to 2 s: is the audit path's
latency now bounded by 2 s per row with the Blazor circuit waiting (where is the audit write awaited from); the
2 s bound - what did the agent base it on; rotation under the lock - the ROTATED file's name is derived from the
active path: does a second instance that just rotated leave the first instance writing to a renamed file (the
lock is per ACTIVE path - after rotation the lock file name changes; is there a window between the size check
and the rename where instance B takes the old lock and writes to a file A is about to rename); readers "never
take the lock" - the aud-a28z tail repair on the writer side vs a reader reading mid-write (a partial last line
read as unparseable, then dropped silently - that is aud-a49, confirm it is only that); the README text and
`_AuditNote` - do they say both instances must run this build, and is the sample's `_AuditNote` key ignored by
configuration binding (a stray key that fails binding would be a defect). a45: the guard keeps the flag set
when the report fails - a provider that throws once and then works never gets the warning (acceptable? the
record says one attempt per failure streak - verify the streak resets). a46: `GetEntries` reading each file in
its own try - a file held by the active writer's lock is NOT the lock file, so a reader opens the active log
with full sharing: confirm the active log's open mode on the writer side allows a concurrent reader (sharing
flags on the appender's data-file handle); `itemDropped` counted not logged - a sustained overflow never reaches
any log line; is the page's count enough (the record says yes); the page's warning only "with diagnostics on" -
is an unreadable file also a reason to warn when diagnostics are off (the audit stream is the aud-a49 part).
Tests passing for the wrong reason: the two-writer tests use raw handles standing in for the other instance -
do they take the same open mode the appender does; the `HoldExclusively` retry the agent added to fix its own
race (does it mask a product race).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
