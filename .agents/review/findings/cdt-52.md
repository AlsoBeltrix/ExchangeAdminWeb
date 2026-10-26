# cdt-52: the installer's move refusal does not name a folder spelling that does not exist on disk

**Severity**: LOW - fail-closed, less actionable than deploy.ps1's message after cdt-46. `tools/Install-ExchangeAdminWeb.ps1` (the cdt-15/cdt-48 check): when a compared path does not exist on disk the refusal does not say which spelling differed.
**Status**: Verified on branch; awaiting merge and codex review - admitted 2026-10-23 evening from the cdt-36 agent's third hand-back; inside the released scope
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), from `7f1890f3`
**Commit**: the `fix(cdt-52)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `fb2b288f`. Trigger: a mismatch where one path does not exist. Predicted failure: the operator cannot tell which value to fix.

`7f1890f3`, the branch point, has the same `tools/`, `deploy.ps1` and `tests/ps/` as `fb2b288f`; master since then
has added only these three records.

## Approach

Written before the code. Line numbers are `tools/Install-ExchangeAdminWeb.ps1` and `deploy.ps1` at `7f1890f3`.

**Mechanism.** The installer's `Assert-ApplicationTargetUnchanged` (644-680) compares the two folders
through the module. Its refusal names both values, but it does not say whether either exists:

```
674:     if (-not (Test-SamePhysicalFolder -Path $ExistingPhysicalPath -Other $PublishPath)) {
675:         $differences += "files at '$ExistingPhysicalPath' (this run: -PublishPath '$PublishPath')"
676:     }
677:     if ($differences.Count -gt 0) {
678:         Write-Fail "IIS application $Location already exists, with $($differences -join ' and '). An update must name the pool and folder it was installed with - ...
```

`Test-SamePhysicalFolder` takes two spellings for one folder in two cases: they are lexically equal,
or both exist and resolve to one final path (cdt-46). A spelling that is not on disk has no final
path, so it can never be matched the second way. Examples: a folder that was deleted or not yet
created, or a mistyped `-PublishPath`. A junction in IIS and a run's folder that is gone then read
as two different folders, and the operator is not told that one of them is missing.

deploy.ps1 has said so since cdt-46:

```
234:     $spellingNote = ''
235:     if (-not (Test-SamePhysicalFolder -Path $ExistingPhysicalPath -Other $PublishPath)) {
236:         $differences += "files at '$ExistingPhysicalPath' (this run: -PublishPath '$PublishPath')"
237:         $missing = @(foreach ($spelling in @($ExistingPhysicalPath, $PublishPath)) {
238:                 $comparable = ConvertTo-ComparablePhysicalPath $spelling
239:                 if ($comparable -and -not [System.IO.Directory]::Exists($comparable)) { "'$spelling'" }
240:             })
241:         if ($missing.Count -gt 0) { $spellingNote = " A spelling that cannot be resolved on disk is not taken as the same folder: $($missing -join ', ')." }
242:     }
```

Both of deploy.ps1's refusals carry the note straight after the differences:
`$($differences -join ' and ').$spellingNote` (251, 257).

**Change.**

1. In the installer's function, the same lines as deploy.ps1 234-242. `$spellingNote = ''` goes before
   the folder comparison. Inside the comparison, after the difference is added, go the `$missing`
   loop and the note, word for word.
2. `ConvertTo-ComparablePhysicalPath` is the module's (exported at `tools/AppPoolControl.psm1` 277).
   The installer already imports that module at 779, before the check runs at 817, and already uses
   it for the comparison. So this adds no import and no coupling (Invariant 1).
3. The refusal at 678 gains `$spellingNote` straight after `$($differences -join ' and ').`. The rest
   of its words are unchanged, so the cdt-15 pins still hold: "Re-run with -AppPoolName ...
   -PublishPath ..." and "Nothing was changed".
4. The comment above the comparison names cdt-52.

The decision does not change. The note is computed only when the folders already differ, and it
feeds only the message. Three consequences:

- A difference in the pool alone gets no note.
- An empty spelling (an application with no folder) is not named, as in deploy.ps1.
- The check also runs at script level in plan mode (817), so a plan's refusal carries the note too.

Constraints: Invariant 6 (pure ASCII, because Windows PowerShell 5.1 reads the installer) and
Invariant 1 (above). No version bump.

Guards go in `tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-15: ...`. One new real-files
test uses a temporary folder, a sibling and paths that do not exist:

- IIS at the folder and the run at a missing path: the refusal gives the differences sentence, then
  "A spelling that cannot be resolved on disk is not taken as the same folder: '<missing>'.", then
  the rest of the message unchanged. This is the same sentence deploy.ps1's cdt-46 test pins.
- IIS at a missing path and the run at the folder: only the missing one is named.
- Both missing: both are named, in order.
- Both exist and differ: no note.
- A difference in the pool alone, with one missing folder spelled the same both times: refused for
  the pool, with no note.
- An application with no folder: refused, with no note.

Mutations:

- M1: the refusal's message reverted to its `7f1890f3` text, so the note is computed but not shown.
  This is the record's mutation.
- M2: the whole fix reverted, so the installer is byte-identical to `7f1890f3`.
- M3: every differing spelling named, without checking whether it exists.

Files: `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1`, this record.

**After the fix**, as built, in `tools/Install-ExchangeAdminWeb.ps1`:

- 674-675: the comment.
- 676-684: the note, computed inside the folder comparison.
- 686: the refusal, carrying the note.

The comment names cdt-46, not deploy.ps1. The installer must never mention deploy.ps1, and a
standalone guard enforces that (see the guard proof).

## Files changed

Each file before the repair, at `7f1890f3` (its blob, then the md5 of its committed LF content), and the
md5 after:

- `tools/Install-ExchangeAdminWeb.ps1`: blob `b8cf2295d752`, md5 `EF255F21E79D4723F83B3BAB00A5634A`; after `362DCC172A426437717106C5E32245D7`
- `tests/ps/InstallerConfigBackup.Tests.ps1`: blob `0205cb8af12b`, md5 `956BA94E954BA3863386AA9B030E43AE`; after `1731A6C8F1A4630D4D858ED491042438`
- this record: copied from master's (`e9db736f`) and filled in

No version bump.

## Guard proof

Tests, in `tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-15: an update refuses to move an existing application to another pool or folder`:

| Test | Pins |
| --- | --- |
| `names a spelling it cannot resolve on disk, and only that one (cdt-52, real files)` (986) | The installer's real `Assert-ApplicationTargetUnchanged`, lifted by AST and run with the real module, against a temporary folder, a sibling and two paths that do not exist. The run at a missing path: the whole refusal, from "IIS application Site/App already exists, with files at '<folder>' (this run: -PublishPath '<missing>'). A spelling that cannot be resolved on disk is not taken as the same folder: '<missing>'. An update must name the pool and folder it was installed with" to "Nothing was changed." That is the sentence deploy.ps1's cdt-46 test pins. IIS at a missing path: only that path is named. Both missing: both are named, IIS first. No note for two folders that exist and differ, for a difference in the pool alone over one missing spelling, or for an application with no folder |
| the cdt-15 cases and the cdt-48 junction test (unchanged) | They still pass, so the refusal's other words hold: "Re-run with -AppPoolName ... -PublishPath ..." and "Nothing was changed" |

Each mutation was applied to the fixed installer (md5 `362DCC172A426437717106C5E32245D7`) by a script that
fails unless every edit matches exactly once and the mutant passes a sanity check of its own shape.
Each was run against the four suites that read the installer: `InstallerConfigBackup`,
`DeployInvariants`, `AppPoolControl` and `SqliteConfigBackup`. After each one the original bytes were
restored, the mtime refreshed and the md5 re-confirmed, and all four suites were re-run green (341
passed, 0 failed, 0 skipped):

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1: the refusal's message reverted, so the note is computed but not shown (the record's mutation) | `404D2C811C996E5A9B2CE3E89EE597D9` | 1: the cdt-52 test (the refusal at a missing path has no note) |
| M2: the whole fix reverted (the installer as at `7f1890f3`, byte-identical) | `EF255F21E79D4723F83B3BAB00A5634A` | 1: the cdt-52 test |
| M3: every differing spelling named, without checking whether it exists | `8FD161D500FEA87305C8E505FB173439` | 1: the cdt-52 test (the folder that exists is named too) |

The first build of the fix named `deploy.ps1` in the new comment. The standalone guard
(`DeployInvariants.Tests.ps1`, `is standalone: never references deploy.ps1`) failed on it, so the
comment was reworded to name cdt-46 instead. The proof above was run on the reworded installer.

Windows PowerShell 5.1.20348.5756:

- The installer, deploy.ps1 and the module parse with no errors.
- A scratch harness lifted the check with the 5.1 parser and ran it with the real module:
  - folder vs missing, and missing vs folder: the missing spelling named;
  - missing vs missing: both named;
  - two folders that exist: no note;
  - a folder against itself with a trailing separator: passed.

Gates on the final tree:

- `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7): 497 passed, 0 failed, 0 skipped.
- `Invoke-ScriptAnalyzer -Path . -Recurse` (1.25.0): 0 errors and 319 warnings, the same count as
  at `7f1890f3`, with none on the new lines.
- `git diff --check`: clean.
- `tools/Test-AsciiOnly.ps1`: passed.
- Both files stay CRLF.

Not run: a real install (it needs elevation and IIS).

## Coder dispute

None.

## Known gaps

None.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.
