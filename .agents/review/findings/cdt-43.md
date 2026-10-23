# cdt-43: docs/ConfigDbOperations.md points at deploy.ps1:447 for a refusal that is no longer there

**Severity**: LOW - a stale line pointer in an operator document; an operator following it lands on unrelated code. The pointer was wrong before cdt-36.
**Status**: Verified on branch; awaiting merge (docs only) - admitted 2026-10-23 evening from the cdt-36 agent's hand-back; inside the released scope. Admitted first as `cdt-39` in `f8d9e6fd`, an ID a completed finding already held; re-filed as cdt-43 by the coordinator (2026-10-23, `4755a8b3`), who restored the original `cdt-39` record on master
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-42 commit `2bb80879`
**Commit**: the `docs(cdt-43)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: implementer (cdt-36 agent, candidate; admitted by the supervisor)

## Evidence

At `72430407`: `docs/ConfigDbOperations.md` 49 says `deploy.ps1` "fails before mirroring anything
(`deploy.ps1:447`)" when `ConfigStore:Path` names a missing file; the refusal it describes is at
`deploy.ps1` 613 (upgrade) and 902 (`-Force`), and at 625 and 914 after cdt-42 (`2bb80879`). Line 447
there is in the ACL step. Every edit above the refusal moves it again, which is how the pointer went
stale.

## Approach

Written before the edit. Point at the refusal by its message, which survives edits, rather than by
line number. `docs/ConfigDbOperations.md` 47-49 becomes:

```
   does not exist, the app refuses to start and `deploy.ps1` fails before mirroring anything,
   with "ConfigStore:Path in ... names '...' but no database exists there" (the upgrade and
   the `-Force` path alike). **Deleting the database is not a recovery step.** See *Recover from
```

The quoted words are the fixed part of both refusals - `Write-Fail "ConfigStore:Path in $configPath
names '$configDbPath' but no database exists there. ..."` at 625 and 914 - with the two variables
shown as `...`. The claim around it stays true: both refusals come before the pool stops and before
anything is mirrored.

Files: `docs/ConfigDbOperations.md`, this record. No guard beyond a doc check: the quoted words occur
in `deploy.ps1` exactly twice, and the document carries no `deploy.ps1:<line>` pointer.

## Files changed

- `docs/ConfigDbOperations.md` - before `1214863f3dee`, md5 `10DE760AA5690BDD6574DA4277DE6CB2` (at
  `2bb80879`); the diff is the Approach's three lines for the old two:

```
-   does not exist, the app refuses to start and `deploy.ps1` fails before mirroring anything
-   (`deploy.ps1:447`). **Deleting the database is not a recovery step.** See *Recover from
+   does not exist, the app refuses to start and `deploy.ps1` fails before mirroring anything,
+   with "ConfigStore:Path in ... names '...' but no database exists there" (the upgrade and
+   the `-Force` path alike). **Deleting the database is not a recovery step.** See *Recover from
```

- this record - new

## Guard proof

Not applicable as a test (docs only). The doc check, run against the worktree's files: the quoted
words occur in `deploy.ps1` exactly twice, both in the full refusal `ConfigStore:Path in $configPath
names '$configDbPath' but no database exists there` (625 upgrade, 914 `-Force`); the document has no
`deploy.ps1:<line>` pointer left; the file stays LF. `git diff --check HEAD` clean. No PowerShell file
changed, so the cdt-42 gates stand (Pester 483/0/0, analyzer 0 errors).

A first run of the doc check read the wrong files and was discarded: it handed relative paths to
`[IO.File]::ReadAllText` after `Set-Location` into the worktree, and .NET resolves those against the
process's own directory, which was the main checkout. It reported the main checkout's unchanged
pointer. Nothing was written there; the check was re-run with absolute paths (above).

## Coder dispute

None.

## Known gaps

None. Other documents still carry `deploy.ps1:<line>` pointers - for example
`docs/Containerization-Feasibility.md` and `.agents/state.md` 210 - and they go stale the same way.
They are outside this finding.

## Reviewer comments

None yet.

## Closeout

Pending: merge into `master`, branch deletion.
