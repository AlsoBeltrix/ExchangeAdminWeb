# ute-1: Usage events were planned for the promoted config database

**Severity**: HIGH - every prod promotion would have overwritten prod's telemetry
with dev's, and a prod-to-dev refresh would copy prod usage into dev; the data the
feature exists to produce would be environment-contaminated and lossy.
**Status**: Verified (plan and decision revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/ute-1.md`.

## Evidence

`.agents/decisions.md` (2026-09-04 entry, first form): "Storage is the existing SQLite
config database". `docs/UsageTelemetry-Plan.md` at `4af217e`: S1 appended a
`ConfigStoreMigrator` step v7 creating `usage_event` in `config/exchangeadmin.db`.
`tools/promote-dev-to-prod.ps1:400-408`: prod promotion calls `Copy-SqliteConfigDb`,
a "wholesale replace, not a per-key merge" of prod's `exchangeadmin.db` with dev's
(owner decision 2026-06-18). The plan's own section 5 had also documented a second
consequence of the same choice: a build that reverts the migrator step refuses to
start on a v7 database (`ConfigStoreMigrator.cs:163-169`).

## Predicted observable failure

After the first promotion following deployment, prod's Usage view shows dev's opens,
actions and sessions and none of prod's. Nothing errors; the numbers are simply about
the wrong environment.

## What

Plan defect: telemetry, an environment-local operational stream, was placed in the
one store the repo deliberately promotes as configuration.

## Approach

Plan revised: the table lives in its own `config/exchangeadmin-usage.db` through its
own `SqliteConnectionFactory`, the jobs database's precedent (`Program.cs:63-70`);
schema is created idempotently by the repository (`CREATE TABLE IF NOT EXISTS`), so
`ConfigStoreMigrator` is untouched and a revert cannot refuse to start. The file is
deploy-excluded with the rest of `config/`, not promoted (`Copy-SqliteConfigDb` copies
`exchangeadmin.db` by name) and not backed up (disposable). S6 adds one sentence to
repo-guidance invariant 3 so a later deploy change does not "fix" its absence. The
decisions entry was corrected in place and says it was wrong.

## Files changed

- `docs/UsageTelemetry-Plan.md` - header, section 1 (new settled item), AC1, AC8,
  section 4 (three rows), section 5 (rewritten), section 6 (storage, repository
  constructor, Program.cs, new Deploy scripts note), S1, S6, section 8, section 10.
- `.agents/decisions.md` - storage bullet of the 2026-09-04 entry.

## Guard proof

Docs-only. The planned `ConfigStoreMigrator_IsUntouched` test (TargetVersion still 6)
and the repository tests against a temp-path factory bite at S1. `git diff --check`
clean on the fold commit.

## Coder dispute (if any)

None. I had documented the revert hazard the choice created and still kept the choice.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; owner: "run it through codex as before")
Harness: codex-cli 0.152.1, `codex exec -s read-only`. Reviewed SHA `4af217e`, base
`2938db7`, capability_ok true, verdict `acceptable_with_changes` (openreview;
material change 1). Dispatched 2026-09-04; envelope at
`.agents/review/ute.result.json`.
