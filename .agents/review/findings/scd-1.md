# scd-1: A configured shared DB path could be silently replaced by an empty database

**Severity**: HIGH - a mistyped `ConfigStore:Path`, a moved or deleted shared file
would start the app on a brand-new database that startup seeding populates at
defaults: no protected principals, no section access, module rows at their default
enablement, and nothing errors. The deploy backup would only warn that no DB was found.
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/scd-1.md`.

## Evidence

`docs/SharedConfigDb-Plan.md` at `8bb46a4`: AC1 resolved the path with no existence
requirement; section 4 row "Shared file unreachable at startup" said the factory
"creates the directory"; the backup row said an absent DB "returns null and the caller
warns, as today". `Services/Storage/SqliteConnectionFactory.cs:24-26` creates the
directory and `:28-38` opens with `Mode = ReadWriteCreate`. `Program.cs:236-237`
`SeedMissingModules()` inserts every catalog module at `EnabledByDefault` when its row
is missing - exactly what a fresh file looks like.

## Predicted observable failure

Cutover done; a later hand edit typos the key on prod. Prod restarts, creates
`exchangeadmin.db` at the typo path, seeds 27 module rows, logs "Config store schema
ready at version 6", and serves. Protected principals: none. Section access: none
(fail-closed permissions deny everyone, which is the visible symptom, but the
protected-principal set is silently empty for any module whose access survives).

## What

Plan defect: the "must exist" property of an explicitly configured shared file was
never stated, so the default create-if-missing behaviour applied to it.

## Approach

Plan revised: `ConfigStorePath.Resolve` returns `mustExist: true` for a configured
path; `SqliteConnectionFactory` gains a `mustExist` flag (no directory creation,
`SqliteOpenMode.ReadWrite`, `FileNotFoundException` naming path and key); `deploy.ps1`
and `promote-dev-to-prod.ps1` `Write-Fail` when the key is set and the resolved file is
absent; the installer refuses `-ConfigStorePath` naming a missing file; only the
cutover script (S4) and the no-key single-instance default create a database. New
section 1 settled item, AC1/AC6/AC7/AC9, three section 4 rows, three tests.

## Files changed

- `docs/SharedConfigDb-Plan.md` - section 1, AC1, AC6, AC7, AC9, section 4, section 6
  (S1 sketch, S3 sketch), section 8, section 10.

## Guard proof

Docs-only. `Factory_MustExist_MissingFile_Throws_AndCreatesNothing` and the Pester
`Write-Fail` guards bite at S1/S3. `git diff --check` clean on the fold commit.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; owner: "plan & review")
Harness: codex-cli 0.152.1, `codex exec -s read-only`. Reviewed SHA `8bb46a4`, base
`e36e798`, capability_ok true, verdict `acceptable_with_changes` (openreview;
material change 1). Dispatched 2026-09-04; envelope at
`.agents/review/scd.result.json`.
