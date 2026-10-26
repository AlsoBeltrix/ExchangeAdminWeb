# cdt-56: the 8.3 test's positive branch is bypassed by an if, not a skip, when no short name can be made

**Severity**: LOW - guard weakness. `tests/ps/AppPoolControl.Tests.ps1` about 1236, 1241, 1276: when `fsutil file setshortname` fails (not elevated, 8.3 disabled) and Program Files has no short alias, `$script:Cdt46HasShortName` is false and the real-8.3 assertions are skipped by an `if`; the suite reports green with 0 skipped while only the synthetic case ran.
**Status**: Merged to master `84a31608`; codex defect review ops-batch-15 in flight
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-55 commit `f348fabf`
**Commit**: the `fix(cdt-56)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard; codex-cli 0.163.0; base 42c2690b head 7f1890f3 (merged fb2b288f); 2026-10-23 evening; capability proof succeeded (AGENTS.md read, git show --stat 7f1890f3)

## Evidence

Codex ops-batch-14 finding 2 at `fb2b288f`.

In the Severity line, the variable name was missing from master's copy of this record (it read
"`` is false"). It is restored here as `$script:Cdt46HasShortName`, the name the evidence below shows.

Confirmed by reading the file. The lines are unchanged since `fb2b288f`: the cdt-54 commit only
appends to the file. In the Describe `cdt-46: ...`, its `BeforeAll` sets the flag:

```
1236:         if ($script:Cdt46Short -eq $script:Cdt46Long) {
1237:             $null = cmd /c "fsutil file setshortname `"$($script:Cdt46Folder)`" CDT46L~1 2>&1"
...
1241:         if ($script:Cdt46Short -eq $script:Cdt46Long) {
1242:             $script:Cdt46Long = $env:ProgramFiles
...
1246:         $script:Cdt46HasShortName = ($script:Cdt46Short -ne $script:Cdt46Long)
1247:         if (-not $script:Cdt46HasShortName) { $script:Cdt46ShortSource = 'none: this host gives no 8.3 spelling' }
```

and the test `takes an 8.3 short spelling for the folder it names, and never a short-looking spelling
that is not on disk (cdt-50: no skip path)` reads it:

```
1276:         if ($script:Cdt46HasShortName) {
1277:             Test-SamePhysicalFolder -Path $script:Cdt46Short -Other $script:Cdt46Long | Should -BeTrue -Because ...
...
1283:         }
```

On a host where none of the three sources gives a short spelling, the `if` passes over the
three real-8.3 assertions. The test then passes on the two synthetic assertions alone. It is not
reported as skipped, so neither CI's skip check nor `TestSuiteIntegrity.Tests.ps1`, which refuses
skips (cdt-50), can see the gap. fsutil's output is discarded (`$null = ...`), so nothing records why
it failed either.

## Approach

Written before the code. Line numbers are those above.

1. **The fixture (`BeforeAll`, 1228-1247).** The same three sources, tried in the same order. Each
   one that gives nothing now adds its reason to a list, `$script:Cdt46ShortTried`:
   - the fixture folder's own short name: "has no short name: its volume made none";
   - fsutil: its exit code and its output, which are captured now instead of discarded;
   - Program Files: "has no short name", with its path.
   When all three fail, `$script:Cdt46ShortSource` becomes `none: ` followed by the reasons. The
   flag `$script:Cdt46HasShortName` is computed as before.
2. **The test (1263-1284).** The `if` goes. The synthetic assertions stay first, so they still run
   on every host. Then comes `$script:Cdt46HasShortName | Should -BeTrue`, with a `-Because` that
   names the cause, followed by the three real-8.3 assertions, unconditionally. A host that gives no
   short spelling now fails the test and says why, instead of passing it on the synthetic case alone.
3. **Unchanged:**
   - the test's title, which the cdt-50 record names;
   - `TestSuiteIntegrity.Tests.ps1`: no skip, pending or inconclusive result is added, and no
     `-Skip`.

Files: `tests/ps/AppPoolControl.Tests.ps1`, this record. No product code changes.

**How the guard is proved.** The defect only shows on a host with no 8.3 spelling. This one has
one: the fixture folder under `%TEMP%` on C: gets its own short name. Such a host is simulated in a
child pwsh, with three changes:

- `TMP` and `TEMP` point at a folder on a volume that makes no short names. D: here: a long-named
  folder on D: was checked and has none.
- `ProgramFiles` points at a long-named folder on that same volume.
- fsutil is left alone, because it fails unelevated anyway.

The suite then runs four ways:

| Code | Host | Expected |
| --- | --- | --- |
| fixed | normal | passes |
| fixed | no 8.3 spelling | fails, naming all three causes |
| M1: the `if` restored, exactly the cdt-50 form (the record's mutation) | normal | passes |
| M1 | no 8.3 spelling | passes with 0 skipped: the defect |

Files: `tests/ps/AppPoolControl.Tests.ps1`, this record.

**After the fix**, as built, in `tests/ps/AppPoolControl.Tests.ps1`: 1228-1258 the fixture (1235
`$script:Cdt46ShortTried`, 1240, 1247 and 1254 the three reasons, 1258 the source line), and
1283-1295 the test's assertions, with 1288 the `Should -BeTrue` on the flag.

## Files changed

The file before the repair, at `f348fabf` (its blob, then the md5 of its committed LF content), and the
md5 after:

- `tests/ps/AppPoolControl.Tests.ps1`: blob `dc1f5820dc24`, md5 `A9A2F9E446868BDC4AE8E9E0D314F91C`; after `CDCA2F712AC13B99928F2A7F0120BB2F`
- this record: copied from master's (`8e61dbf0`) and filled in

No product code changes. No version bump.

## Guard proof

The guard is the test itself, so the proof is the four runs the Approach set out. Each run executed
the cdt-46 Describe's 8.3 test in a child pwsh, under Pester 6.2.0, in this host's environment and
then in a simulated one with no 8.3 spelling: `TMP`, `TEMP` and `ProgramFiles` pointed at long-named
folders on D:, a volume that was checked and makes no short names, with fsutil left alone (it fails
unelevated here anyway). Each mutant was applied to the fixed file by a script that fails unless its
edit matches exactly once and the mutant passes a sanity check of its own shape; afterwards the
original bytes were restored, the mtime refreshed and the md5 re-confirmed, and both runs repeated.

| Code | md5 | Normal host | No 8.3 spelling |
| --- | --- | --- | --- |
| fixed | `CDCA2F712AC13B99928F2A7F0120BB2F` | 1 passed, 0 failed, 0 skipped | **0 passed, 1 failed**, 0 skipped |
| M1: the `if` restored around the real-8.3 assertions, the `Should -BeTrue` gone (the record's mutation) | `6C29BFC578500AD102B7C860B2F48A46` | 1 passed | 1 passed, 0 skipped - **the defect** |
| M2: the whole fix reverted (the file as at `f348fabf`, byte-identical) | `A9A2F9E446868BDC4AE8E9E0D314F91C` | 1 passed | 1 passed, 0 skipped - **the defect** |
| fixed, restored after each mutant | `CDCA2F712AC13B99928F2A7F0120BB2F` | 1 passed | 0 passed, 1 failed |

So on a host with no 8.3 spelling the old test passed while only the synthetic case ran, and the
fixed one fails. Its message names all three causes, for example:

```
Expected $true, because a real 8.3 spelling is needed to show that one is taken for its folder, and
this host gave none (none: the fixture folder '<temp>\cdt46_<guid>\Publish Folder With A Long Name'
has no short name: its volume made none; fsutil file setshortname on it exited 1 and set none:
Error:  Not all privileges or groups referenced are assigned to the caller.; Program Files
('<pf>') has no short name), but got $false.
```

The simulation's fixture folders were created under `obj\`, which `.gitignore` excludes, and removed
at the end of the run.

`TestSuiteIntegrity.Tests.ps1`'s guard is untouched and still passes: the fix adds no
`Set-ItResult -Skipped/-Pending/-Inconclusive` and no `-Skip`, so a host without a short spelling
now fails CI rather than reporting green.

Gates on the final tree:

- `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7): 505 passed, 0 failed, 0 skipped.
- `Invoke-ScriptAnalyzer -Path . -Recurse` (1.25.0): 0 errors and 321 warnings; the per-file
  findings are identical to those before this change.
- `git diff --check`: clean.
- `tools/Test-AsciiOnly.ps1`: passed.
- Windows PowerShell 5.1.20348.5756 parses the file with no errors (it runs under pwsh 7; parsed
  for the ASCII/5.1 convention).
- The file stays CRLF.

Not run: the test on a real host whose volume has 8.3 creation disabled. The simulation above sets
the same conditions through the environment.

## Coder dispute

None.

## Known gaps

- The fix makes the test depend on the host offering some 8.3 spelling. On this machine and on
  `windows-latest` the C: volume generates short names and `C:\Program Files` has `C:\PROGRA~1`, so
  CI is unaffected; a host with 8.3 creation disabled everywhere would now fail this test rather
  than pass it hollow. That is the intent of the finding: a guard that cannot run should say so.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.
