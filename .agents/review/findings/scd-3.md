# scd-3: Newer-schema acceptance checked tables only, not columns

**Severity**: MEDIUM - a database labelled newer than the build but missing a column
this build reads and writes would pass the tolerant migrator and fail later inside a
section-access save.
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/scd-3.md`.

## Evidence

`docs/SharedConfigDb-Plan.md` at `8bb46a4`, section 6 S1: `RequiredTables` "derived
by ... regexes `CREATE TABLE` names". `Services/Storage/ConfigStoreMigrator.cs:139-141`:
v6 is `ALTER TABLE section_access ADD COLUMN group_display_name TEXT;`, creating no
table. `Services/Storage/SectionAccessRepository.cs:141, :165, :239` read and write
`group_display_name`.

## Predicted observable failure

A restored or hand-edited database with `user_version` 9 and `section_access` lacking
`group_display_name` starts the app ("serving on the newer schema") and the first
Admin Settings save of section access fails with "no such column".

## What

Plan defect: the required-schema check was scoped to the DDL shape of v1-v5 and missed
that v6 is a column addition.

## Approach

Plan revised: `RequiredSchema` lists tables and their columns (from `CREATE TABLE`
column lists and `ALTER TABLE ... ADD COLUMN`), `Missing(connection)` checks
`sqlite_master` and `PRAGMA table_info`, a test pins the list to the statements, and a
second test drops the v6 column from a "newer" database and expects the named refusal.

## Files changed

- `docs/SharedConfigDb-Plan.md` - section 1, AC2, section 4, section 6 (S1 sketch),
  section 8, section 10.

## Guard proof

Docs-only. `Migrate_DatabaseNewerThanBuild_ThrowsWhenARequiredColumnIsMissing` and
`RequiredSchema_MatchesTheMigrationStatements` bite at S1. `git diff --check` clean on
the fold commit.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; same dispatch as scd-1)
Harness: codex-cli 0.152.1. Reviewed SHA `8bb46a4`, base `e36e798`, capability_ok
true, verdict `acceptable_with_changes` (material change 3). Dispatched 2026-09-04;
envelope at `.agents/review/scd.result.json`.
