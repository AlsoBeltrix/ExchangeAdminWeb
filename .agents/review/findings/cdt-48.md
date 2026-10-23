# cdt-48: the installer's cdt-15 comparison stays literal, so a junction-spelled IIS path is refused early

**Severity**: LOW - fail-closed but inconsistent with deploy.ps1 after cdt-46. `tools/Install-ExchangeAdminWeb.ps1` (the cdt-15 check) compares paths literally; an application whose IIS `physicalPath` is a junction spelling of the same folder is refused as a move.
**Status**: Closed - merged to master `fb2b288f`; codex ops-batch-14 clean on this finding. Complete once fix/cdt36 is deleted
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-47 commit `fd7ddf18`
**Commit**: the `fix(cdt-48)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `0c9917d5`. Trigger: IIS stores a junction path; the installer is run with the target path. Predicted failure: a false "would move the application" refusal.

## Approach

Written before the code. Line numbers are `tools/Install-ExchangeAdminWeb.ps1` at `fd7ddf18`.

**Mechanism.** The cdt-15 check compares folders through its own lexical normaliser:

```
644: function ConvertTo-InstallerComparablePath {
...
654:         $full = [System.IO.Path]::GetFullPath($expanded)
...
685:     $existingFolder = ConvertTo-InstallerComparablePath $ExistingPhysicalPath
686:     $runFolder = ConvertTo-InstallerComparablePath $PublishPath
687:     if ([string]::IsNullOrWhiteSpace($existingFolder) -or -not [string]::Equals($existingFolder, $runFolder, [System.StringComparison]::OrdinalIgnoreCase)) {
```

`GetFullPath` leaves a junction as written (cdt-46), so IIS serving `<root>\link`, a junction to the
folder the run names, reads as a move, and the update is refused at 691. Later in the same run the
installer's `Assert-AppPoolServesPath` (1091) takes the junction for the folder since cdt-46. So the
early check is stricter than the late one: fail-closed, but inconsistent, and wrong for that
folder.

**May the installer use the module?** Yes. The coordinator asked for this to be checked, and it has
been. The installer already imports `tools/AppPoolControl.psm1`, unconditionally, at 792 - before the
cdt-15 read (816-829) and the check (830). The comment there gives the reason: "Ships beside this
script and is environment-neutral - it names no host, domain or configuration - so importing it
keeps this installer standalone and uncoupled from any environment-specific deploy tooling
(Architectural Invariant 1)." It imports `SqliteConfigBackup.psm1` from `tools/` too (736). Using
the module's exported `Test-SamePhysicalFolder` adds no new coupling, and nothing from `deploy.ps1`
is touched or read.

**Change.**

1. `Assert-ApplicationTargetUnchanged` (661-693): the folder comparison becomes
   `-not (Test-SamePhysicalFolder -Path $ExistingPhysicalPath -Other $PublishPath)`. That is the same
   lexical rule as before (expand, absolute, trailing separators, case-insensitive equality, never
   prefix, empty never the same), plus the final path when both spellings exist. It is the rule
   `deploy.ps1` and `Assert-AppPoolServesPath` use, so the installer's two checks agree. The comment
   names cdt-48.
2. `ConvertTo-InstallerComparablePath` (644-659) is then dead - its only callers were 685-686 - and
   is removed. That is verified: no other reference in the repo outside the cdt-15 test's lift list
   and historical records.
3. No other change: the refusal's words, the read, the plan-mode warning and the order are as
   cdt-15 left them.

Constraints: Invariant 1 (as above); Invariant 6 (the installer is read by Windows PowerShell 5.1:
pure ASCII); the module's `Add-Type` compile happens on the first comparison that needs a final path,
in plan mode as in apply - in memory, no host change. No version bump.

Guards, in `tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-15: ...`:

- Its `BeforeAll` lifts `Write-Fail` and `Assert-ApplicationTargetUnchanged` (no longer the removed
  normaliser) and imports the real `tools/AppPoolControl.psm1`, as the installer does. The cdt-15
  cases stay unchanged and must still pass: every spelling, every refusal and its words.
- New: against a temporary folder and a junction to it (`mklink /J`), IIS at the junction and the run
  at the folder is an in-place update, both ways round; IIS at the junction and the run at a sibling
  folder is refused, naming the junction and the sibling.

Mutation: the literal comparison restored (the `fd7ddf18` function and normaliser, in place of the
new line).

Files: `tools/Install-ExchangeAdminWeb.ps1`, `tests/ps/InstallerConfigBackup.Tests.ps1`, this record.

**After the fix**, as built: `tools/Install-ExchangeAdminWeb.ps1` 644 (`Assert-ApplicationTargetUnchanged`,
now the first function after the ConfigStore helpers), 668-676 (the comment and the comparison
through `Test-SamePhysicalFolder`); the normaliser is gone.

## Files changed

Before the repair at `fd7ddf18` (blob; md5 of the committed LF content), and after:

- `tools/Install-ExchangeAdminWeb.ps1` - `de1050bbf74b`, `D54EAD67F6E272BCB913022FF7A51967`; after `EF255F21E79D4723F83B3BAB00A5634A`
- `tests/ps/InstallerConfigBackup.Tests.ps1` - `a6b3e071eaa2`, `6C64EE76235AFC1133A7D5E9B088983C`; after `956BA94E954BA3863386AA9B030E43AE`
- this record - copied from master's (`8a984e94`) and filled in

No version bump.

## Guard proof

Tests, `tests/ps/InstallerConfigBackup.Tests.ps1`, Describe `cdt-15: an update refuses to move an existing application to another pool or folder`:

| Test | Pins |
| --- | --- |
| `takes a junction for the folder it points at, and still refuses a sibling (cdt-48, real files)` (958) | The installer's real `Assert-ApplicationTargetUnchanged`, lifted by AST, with the real module, against a temporary folder and a junction to it (`mklink /J`). IIS at the junction and the run at the folder is an in-place update; so is the other way round, with a trailing separator. IIS at the junction and the run at a sibling folder is refused: "files at '<junction>' (this run: -PublishPath '<sibling>') ... Nothing was changed" |
| the cdt-15 cases (unchanged; `BeforeAll` now lifts `Write-Fail` and the check and imports the module) | Every spelling still passes and every refusal keeps its words: a different pool, a different folder, both, a folder that only starts with the run's, an application with no folder, and `%CDT15_ROOT%` |

Mutations on the fixed installer (md5 `EF255F21E79D4723F83B3BAB00A5634A`), each applied by a script that
fails unless every edit matches exactly once and the mutant passes a sanity check of its own shape,
run against `InstallerConfigBackup.Tests.ps1` and `DeployInvariants.Tests.ps1`; then the original
bytes restored, the mtime refreshed, the md5 re-confirmed and both re-run green (196 passed):

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| L1 the literal comparison restored, through the module's lexical normaliser (the cdt-15 rule exactly, no final path) - the record's mutation | `9F93650E2B1E3185F6E8B9F9E169B445` | 1: the junction test (IIS at the junction refused as a move) |
| R0 the whole fix reverted (the installer as at `fd7ddf18`, its own normaliser back; byte-identical) | `D54EAD67F6E272BCB913022FF7A51967` | 7: all six cdt-15 behavioural cases and the junction test - but for a structural reason. The tests no longer lift the removed normaliser, so the old function's call to it is "not recognized". L1 is the behavioural proof |

Windows PowerShell 5.1.20348.5756: the installer parses with no errors, and a scratch harness lifting
the check with the 5.1 parser and the real module gave in place for junction vs folder and for folder
vs junction-with-separator, refused junction vs sibling, in place for `D:/apps/App` vs `D:\apps\App`,
and refused the prefix `D:\apps\AppOther`.

Gates on the final tree: `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7) 492 passed, 0 failed, 0
skipped; `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error,Warning` (1.25.0) 0 errors, 317
warnings, none new; `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed; both files stay
CRLF.

Not run: a real install (it needs elevation and IIS).

## Coder dispute

None.

## Known gaps

- The installer's refusal does not yet name a spelling that does not exist on disk, as deploy.ps1's
  does since cdt-46. The comparison fails closed either way, and the message is out of this
  finding's scope.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.

## Reviewer comments (supervisor, ops-batch-14)

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 42c2690b head 7f1890f3 (merged fb2b288f); 2026-10-23 evening; capability proof succeeded (AGENTS.md read, git show --stat 7f1890f3). Two candidates across the range, admitted as cdt-55 and cdt-56. Codex confirmed: cdt-49 uses the staged build's db active-jobs per repo guidance and missing or unreadable jobs databases soft-warn; the cdt-48 module import predates the change; no ops-script line pointer remains in the containerization doc; the changed PowerShell is ASCII-only. Artefacts: `.agents/review/ops-batch-14.result.txt`, `.stream.log`, `prompt-ops-batch-14.md`.
