# Defect review request: the test suite clears only its own databases' connection pools, never every pool in the process, with a tripwire (aud-a74, test-only)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `a446daee` or later.
- The change is the branch merged at `fc14800f`: `7e4c042e` (plan), `c414a8c2` (fix), forked from `944b8c72`. Review
  `git diff 944b8c72 c414a8c2`, and read the COMBINED `ExchangeAdminWeb.Tests/ConfigBooleanTests.cs`,
  `ConfigChangeWatcherTests.cs`, `ConfigDbCliTests.cs`, `ConfigValueTests.cs`, `EventLogDiagnosticsTests.cs` (each
  `ClearPool` over the test's own database files) and `SharedTempDatabaseTripwireTests.cs`
  (`NoTestSource_ClearsEveryConnectionPoolInTheProcess`; `ScannedSources` with `params` skips) on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Record: `.agents/review/findings/aud-a74.md` (the observed flake; why the two `ConfigDbCliTests` calls stay mid-test).
  Out of scope: the `Comms10kOwnedJobTests` fixture clearing a pool with no `Mode` (temp-folder hygiene, recorded in the
  record's Known gaps). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Verification.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat fc14800f`. Report whether both succeeded.

## Mandate

Hunt for observable defects in the test suite's behaviour. The `ClearPool` key: `Data Source=<file>;Mode=ReadWriteCreate`
must match the string `SqliteConnectionFactory` builds for every database a test created - a store the test opened
through another path (`ConfigDbTestFiles.Open` unpooled, a `Mode=ReadOnly` reader) leaves a pooled handle the clear
misses: does any of the five tests open such a connection and then try to delete its folder (a leaked folder, or a
sharing violation on delete that fails the test on a slow disposal); the tripwire's `\bClearAllPools\b` with comments
blanked - a string literal containing the name (a test asserting a message) would trip it: any such literal; the
`params` skips - the two temp-directory scans still skip exactly what they skipped before. Tests passing for the wrong
reason: the three green full runs cannot show the flake gone - is any other process-global SQLite call left in the test
project (`SqliteConnection.ClearAllPools` is the only one? `SQLitePCL.raw` shutdown? `Batteries` re-init?) - name any.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
