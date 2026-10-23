# cdt-50: the cdt-46 8.3 Pester test can skip, and CI fails on any skip

**Severity**: LOW - CI gate risk. The 8.3 short-name test in `tests/ps/AppPoolControl.Tests.ps1` skips when neither the temp volume nor Program Files has a short name; `.github/workflows/ci.yml` fails the powershell job on any skip. Unlikely on windows-latest but not proven.
**Status**: Verified on branch; awaiting merge and codex review - admitted 2026-10-23 evening from the cdt-36 agent's second hand-back; inside the released scope
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-49 commit `e999ddda`
**Commit**: the `fix(cdt-50)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `0c9917d5`. Trigger: a runner volume with 8.3 names disabled. Predicted failure: the powershell CI job fails on a skip, not a defect.

At `e999ddda`, `tests/ps/AppPoolControl.Tests.ps1`:

```
1232:         $script:Cdt46Short = & $shortOf $script:Cdt46Folder
1233:         if ($script:Cdt46Short -eq $script:Cdt46Long) {
1234:             $script:Cdt46Long = $env:ProgramFiles
...
1254:         if (-not $script:Cdt46HasShortName) {
1255:             Set-ItResult -Skipped -Because "neither the fixture's volume nor '$env:ProgramFiles' has an 8.3 short name to test with"
```

and `.github/workflows/ci.yml` 107-110 fail the job on `$result.SkippedCount -gt 0`. That
`Set-ItResult -Skipped` is the only skip path in `tests/ps` (searched).

Checked alongside, and not a risk: GitHub's Windows runners often have `TEMP` at an 8.3 spelling
(`C:\Users\RUNNER~1\...`). The suite was run with `TMP` and `TEMP` set to the 8.3 spelling of a
long-named folder, and passed 493/0/0. .NET 10's `GetTempPath` returns the long form itself, so the
temp-based fixtures compare consistently.

## Approach

Written before the code.

1. The fixture's short spelling, in order: the fixture folder's own (its volume generates 8.3 names);
   else one set on it with `fsutil file setshortname` (the volume allows short names but did not
   generate one - this needs elevation, which GitHub's runners have, and a failure there just falls
   through); else `$env:ProgramFiles`'. The source is kept, and the assertions name it.
2. The test asserts on every host, with no skip path. Always, on a synthetic spelling: `GetFullPath`
   does not check that a path exists and expands a short component only when it names one on disk.
   So `Z:\CDT50X~1\APP`, which is not there, stays as written. `Test-SamePhysicalFolder` does not take
   it for `Z:\CDT50 Not Here\APP`: it fails closed. When a real short spelling exists (step 1), the
   cdt-46 assertions follow. The test's name says both, and the cdt-46 record gets a note of the
   rename.
3. A suite-wide guard in `tests/ps/TestSuiteIntegrity.Tests.ps1`, beside its existing "every file
   parses" checks. No test file may contain a `Set-ItResult` with `-Skipped`, `-Pending` or
   `-Inconclusive`, or an `It` with `-Skip`. It is found by AST, so comments and strings do not count.
   CI fails a skip only when one happens on some runner; this refuses a skip path before it can.

Constraints: the tests run under `Set-StrictMode -Version Latest` (both files); no new dependency. No
version bump.

Guards and their proof: the integrity test bites on the skip put back. A mutation of the 8.3 test that
forces "no short name on this host" shows the test still asserts, and passes, with no skip. A
mutation that makes `Test-SamePhysicalFolder` take everything shows the synthetic assertion catches
a fail-open. Beyond these, the comparison itself is proven by cdt-46's mutations, unchanged.

Files: `tests/ps/AppPoolControl.Tests.ps1`, `tests/ps/TestSuiteIntegrity.Tests.ps1`, a note in
`.agents/review/findings/cdt-46.md`, this record.

**After the fix**, as built: `tests/ps/AppPoolControl.Tests.ps1` 1228-1247 (the three sources, in
order, and the source label) and 1263 onwards (the test, renamed, with no skip);
`tests/ps/TestSuiteIntegrity.Tests.ps1` 63 onwards (the guard).

## Files changed

Before the repair at `e999ddda` (blob; md5 of the committed LF content), and after:

- `tests/ps/AppPoolControl.Tests.ps1` - `e7487778e6cf`, `F56D475C6BB7ACFFC6E9BE1025C813CA`; after `E8ABA839F8AA58DCE588750AF1DB3619`
- `tests/ps/TestSuiteIntegrity.Tests.ps1` - `fab13706a879`, `04769D7CFDC1D67A3A14A269C2E801BF`; after `F1CA848C3546823D48E3F651F7A3D9CC`
- `.agents/review/findings/cdt-46.md` (the note) - `ea3d5f9344c0`, `7905CA8DBFD064A016B1CD9CC391A339`; after `26D63451C56ED2DC2A8B5F81F846EFB6`
- this record - copied from master's (`8a984e94`) and filled in

No production code changed. No version bump.

## Guard proof

| Test | Pins |
| --- | --- |
| `tests/ps/AppPoolControl.Tests.ps1`: `takes an 8.3 short spelling for the folder it names, and never a short-looking spelling that is not on disk (cdt-50: no skip path)` (1263) | Always: `ConvertTo-ComparablePhysicalPath 'Z:\CDT50X~1\APP'` is exactly `Z:\CDT50X~1\APP`, and `Test-SamePhysicalFolder` does not take it for `Z:\CDT50 Not Here\APP`. When this host gives a real short spelling - here the fixture folder's own - the cdt-46 assertions follow, with the source named in their messages |
| `tests/ps/TestSuiteIntegrity.Tests.ps1`: `has no test that can skip itself, because CI fails the job on any skip (cdt-50)` (63) | Parsing every `tests/ps/*.Tests.ps1`: no `Set-ItResult` with `-Skipped`, `-Pending` or `-Inconclusive`, and no `It`, `Context` or `Describe` with `-Skip`. A hit is named by file and line |

Proofs on the fixed files (md5s above), each applied by a script that fails unless every edit matches
exactly once and the change passes a sanity check of its own shape, run against
`AppPoolControl.Tests.ps1`, `TestSuiteIntegrity.Tests.ps1` and `DeployInvariants.Tests.ps1`; then the
original bytes restored, the mtime refreshed, the md5 re-confirmed and the three re-run green each
time (220 passed):

| Change | md5 after | Result |
| --- | --- | --- |
| N1 the skip put back in the 8.3 test (the cdt-46 shape) | test file `784634DA45E27E2DA24F31229A8CBBB7` | 1 failed: the integrity test ("AppPoolControl.Tests.ps1:1265: Set-ItResult -Skipped ...") |
| N2 no short name on this host, forced (`$script:Cdt46HasShortName = $false`) | test file `77E488A00246EACECFCE57FC7F85F331` | 220 passed, 0 skipped: with no short spelling the test still asserts - the synthetic case - and passes. This is the branch a runner without 8.3 names takes |
| N3 `Test-SamePhysicalFolder` takes every pair (fail-open) | module `58E32B1C3976C5E1168DE7C806F83200` | 10 failed, among them this test's synthetic assertion ("Expected $false ... but got $true"), which therefore catches a fail-open on every host |

The `fsutil` source was not reached on this host: the fixture's own short name came first. Run
unelevated here, `fsutil file setshortname` refused with "Not all privileges or groups referenced are
assigned to the caller." (exit 1), and the fixture code then falls through to the next source. It is
there for elevated runners whose volume allows short names but did not make one.

Gates on the final tree: `Invoke-Pester tests/ps` (Pester 6.2.0, pwsh 7) 494 passed, 0 failed, 0
skipped; `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error,Warning` (1.25.0) 0 errors, 319
warnings, none new; `git diff --check HEAD` clean; `tools/Test-AsciiOnly.ps1` passed; both test files
stay CRLF. And, from Evidence, the suite under `TMP`/`TEMP` at an 8.3 spelling: 493/0/0.

## Coder dispute

None. The record's outline said the mutation was not applicable beyond cdt-46's proof. The new
integrity guard has a mutation of its own (N1), and N2 and N3 show the test's two branches.

## Known gaps

- Where no short spelling exists, the test asserts only the synthetic case. The real 8.3
  assertions then do not run there, and nothing skips. The comparison's own proof is cdt-46's
  mutations, which do not depend on 8.3.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, codex review, merge into `master`, branch deletion.
