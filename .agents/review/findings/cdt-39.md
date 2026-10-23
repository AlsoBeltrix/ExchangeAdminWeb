# cdt-39: The CI PSScriptAnalyzer install has no retry, so a transient Gallery download fails the powershell job

**Severity**: LOW - CI reliability. `.github/workflows/ci.yml`, the `PSScriptAnalyzer` step, runs
`Install-Module PSScriptAnalyzer -Force -Scope CurrentUser` once; the PowerShell Gallery's
intermittent "Package failed to download" (seen 2026-10-21 on the Pester install, fixed there by
ConfigDbToolInApp S2 with a three-attempt retry, and again 2026-10-22 on run 39847030059 for the
analyzer) fails the job on a docs-only commit. The two installs now behave differently for the same
transient cause.
**Status**: Merged to master (`86f3d4cb`); closed on guard proof; Pester 477/0/0 in the repo; codex review pending; codex misc-batch-1 clean (2026-10-23) (batched with the next ops group)
**Branch**: `worktree-agent-a05fc8e899ba981d1` (forked at `34c7ccd4`)
**Commit**: `66403d56` (fix, tests, guard proof); plan `f1fa86cc`; comments `1a253747`; merged `86f3d4cb`
**Reviewer**: implementer (CI run 39847030059 on `e9a59ed4`, 2026-10-22; rerun requested)

## Evidence

`.github/workflows/ci.yml` lines 47-56 (one install, no retry) versus the Pester step's install
with its retry loop (S2, `b1577f7`); the failed step log: `Install-Package: Package 'PSScriptAnalyzer'
failed to download.` after 52 seconds.

## Approach (to be written by the implementing agent before code)

- Give the analyzer install the same three-attempt retry the Pester install has (one shared
  snippet or an identical loop; pin the version used locally, 1.25.0, so CI and the dev host run
  the same rules), and extend the existing `DeployInvariants` CI-workflow guard that pins the Pester
  retry to pin this one too.
- Mutation at the call site: remove the retry (the Pester guard fails). Pure ASCII; `Import-Module
  Pester -RequiredVersion 6.2.0` before the analyzer locally; `Invoke-Pester tests/ps` 0 failed.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("EVERYTHING"); admitted at `34c7ccd4`. Evidence re-read there: `ci.yml`
47-56, one `Install-Module PSScriptAnalyzer -Force -Scope CurrentUser` (50), no retry; the Pester
step's loop at 73-82. CI and tests only - no app or module behaviour, so no version bump.

1. `ci.yml`, the `PSScriptAnalyzer` step: line 50 becomes the Pester step's loop (73-82), identical
   but for the module, its version and the warning text: `for ($attempt = 1; $attempt -le 3;
   $attempt++)`, `try { Install-Module PSScriptAnalyzer -RequiredVersion 1.25.0 -Force -Scope
   CurrentUser -ErrorAction Stop; break }`, `catch { if ($attempt -eq 3) { throw }; Write-Warning
   ...; Start-Sleep -Seconds (10 * $attempt) }`. The original flags stay; `-ErrorAction Stop` is
   added so a failed download reaches the catch, as in the Pester install. Lines 51-56 unchanged.
2. Then `Import-Module PSScriptAnalyzer -RequiredVersion 1.25.0 -ErrorAction Stop`, before
   `Invoke-ScriptAnalyzer` (51). An install does not decide which analyzer runs: auto-loading takes
   the first copy along `PSModulePath`, not the newest. Probe on this host, 2026-10-22 (pwsh
   7.6.6): one module in two paths, 1.0.0 in the first, 2.0.0 in a later one - calling its command
   loaded 1.0.0, an unversioned `Import-Module` loaded 1.0.0, `-RequiredVersion 2.0.0` loaded
   2.0.0. The import pins the rules CI applies, and fails the step if 1.25.0 is not there.
3. Version 1.25.0: `Get-Module PSScriptAnalyzer -ListAvailable` on the dev host lists 1.25.0
   (CurrentUser, the copy pwsh loads) and 1.24.0 (the Windows PowerShell AllUsers path).
   `Find-Module PSScriptAnalyzer -AllVersions`, 2026-10-22: 1.25.0 is the Gallery's newest
   (published 2026-03-20), so the pin changes nothing CI runs today; it stops a later release from
   changing CI's rules with nothing in the repo changing.
4. One identical loop, not a shared snippet: each `run:` step is its own pwsh process, so sharing
   needs a new tracked script (its own Pester, analyzer and ASCII obligations, and the loop leaves
   the workflow its reader is in) or one step installing both modules (Pester installed before the
   analyzer runs, and the step name no longer says which install failed). Ten duplicated lines cost
   less than either; the guard holds both copies to one shape, so they cannot drift unnoticed.
5. Guards, `tests/ps/DeployInvariants.Tests.ps1`, `Describe 'CI gates'` (1988-2037). Plan
   correction: no test pins the Pester retry today - this record's approach and the brief assumed
   one; `grep` of `tests/ps` finds none. So the retry guard is written once and run for both
   installs, which also pins the Pester retry the analyzer one copies.
   - BeforeAll (1993-1997) gains `Get-CiStepScript`: the named powershell-job step's `run: |`
     block, de-indented and parsed with the PowerShell parser, so the guards read commands and
     loops, not text a comment could satisfy. It throws when the step or its block is missing.
   - `It 'retries the <Module> install three times, rethrowing the last failure (cdt-39)'
     -TestCases` PSScriptAnalyzer, Pester: the block parses; exactly one `Install-Module <Module>`,
     with `-ErrorAction Stop`; it is the first statement of a try body whose second is `break`;
     the try is a direct statement of `for ($attempt = 1; $attempt -le 3; $attempt++)`; one
     catch-all, whose first statement is `if ($attempt -eq 3) { throw }` (a bare rethrow) and
     which calls `Start-Sleep`.
   - `It 'runs PSScriptAnalyzer 1.25.0, the version the dev host runs (cdt-39)'`: the install
     carries `-RequiredVersion 1.25.0`; exactly one `Import-Module PSScriptAnalyzer`, a top-level
     statement, with `-RequiredVersion 1.25.0` and `-ErrorAction Stop`, after the loop and before
     `Invoke-ScriptAnalyzer`.
6. Mutations at the call site (`ci.yml`): md5 of the LF content before and after, the whole
   `DeployInvariants.Tests.ps1` run, original bytes restored, md5 re-confirmed, touched, rerun
   green. M1 the analyzer loop replaced by its bare install line: the PSScriptAnalyzer case of the
   retry test fails. M2 `-RequiredVersion 1.25.0` dropped from the install: the version test fails.
   Extra probes, outside the table: the pre-fix `ci.yml` (`34c7ccd4`) fails both analyzer tests;
   M1's shape applied to the Pester step fails the Pester case. Comments are written after the
   round; the md5s are of the pre-comment bytes, and the post-comment md5 is recorded.

Files: `.github/workflows/ci.yml`, `tests/ps/DeployInvariants.Tests.ps1`, this record. Pure ASCII;
the test file parses under Windows PowerShell 5.1 with zero errors.

Gates, one pwsh session: `Import-Module Pester -RequiredVersion 6.2.0`, then `Invoke-ScriptAnalyzer
-Path tests/ps/DeployInvariants.Tests.ps1 -Severity Error` zero; `Invoke-Pester tests/ps` 0 failed,
0 skipped; 5.1 `ParseFile` of the test file zero errors; `git diff --check HEAD`; `git add -A`,
then `tools/Test-AsciiOnly.ps1`; `tools/Install-ExchangeAdminWeb.ps1` still never names
`deploy.ps1`. Also the CI step's own analyzer run under 1.25.0 (`Invoke-ScriptAnalyzer -Path .
-Recurse -Severity Error,Warning`), zero errors, since that pinned run is what CI will now do.

## Fix

As planned. Line numbers after the comments commit (at `66403d56` its comment lines are absent, so
the lines below them sit higher in both files). `.github/workflows/ci.yml`, the
`PSScriptAnalyzer` step (47-71): the why-comment at 50-54, the loop at 55-64, the install at 57
(`-RequiredVersion 1.25.0 -ErrorAction Stop` added to the original flags), the import at 65,
`Invoke-ScriptAnalyzer` at 66, the rest of the step unchanged. The Pester step (now 79-106) is
unchanged. `tests/ps/DeployInvariants.Tests.ps1`, `Describe 'CI gates'`: `Get-CiStepScript` in its
BeforeAll (1998-2031), the retry test (2073-2122), the version test (2124-2150).

Behaviour probe of the step itself (scratch, `cdt39-stepprobe`): its loop and import lifted from
`ci.yml` and run in a fresh pwsh 7.6.6 behind GitHub's `$ErrorActionPreference = 'stop'` prefix.
With `-WhatIf` on the install, PowerShellGet resolved "Version '1.25.0' of module
'PSScriptAnalyzer'" on the Gallery and the step's import loaded 1.25.0 (exit 0). With
`-RequiredVersion 9.9.9` and `Start-Sleep` shadowed: attempts 1 and 2 each warned with the
`Install-Package` error ("No match was found ...") and asked for 10 s then 20 s; the third rethrew
it and ended the script before the line after the loop (exit 1). Assumption, not reproduced: a
"Package failed to download" reaches the catch the same way - it is raised by the same
`Install-Package` (the CI log names it), and cannot be produced on demand.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`, `Describe 'CI gates'`) | Pins |
| --- | --- |
| `retries the <Module> install three times, rethrowing the last failure (cdt-39)`, cases PSScriptAnalyzer and Pester | The step's `run: \|` block, parsed: one `Install-Module <Module>` with `-ErrorAction Stop`, the first statement of a try body whose second is `break`; the try a direct statement of `for ($attempt = 1; $attempt -le 3; $attempt++)`; one catch-all whose first statement is `if ($attempt -eq 3) { throw }` (bare) and which calls `Start-Sleep` once |
| `runs PSScriptAnalyzer 1.25.0, the version the dev host runs (cdt-39)` | The install's `-RequiredVersion 1.25.0`; one top-level `Import-Module PSScriptAnalyzer -RequiredVersion 1.25.0 -ErrorAction Stop`, after the install and before `Invoke-ScriptAnalyzer` |

Session-local driver (scratch, `cdt39-mutations/mutate.ps1`); each run is the whole
`DeployInvariants.Tests.ps1` in a fresh pwsh under Pester 6.2.0. Fixed `ci.yml` md5
`080138727EFBC40F6C20B007F5505575` (LF content, as git stores it - this commit's bytes; the CRLF
working copy is `A34718014A3354E103C176BA1D8491F7`); baseline 114 passed, 0 failed, 0 skipped. After
each mutant the fixed bytes were copied back, both md5s re-matched, the file was touched and the
file rerun: 114 passed, 0 failed, 0 skipped, every time.

| Mutation (call site, `ci.yml`) | md5 after (LF) | Failed |
| --- | --- | --- |
| M1 the analyzer loop replaced by its own install line | `272066097C93E52D5D1997D4791E0A9C` | `retries the PSScriptAnalyzer install three times, rethrowing the last failure (cdt-39)` only ("Expected the value to have type [System.Management.Automation.Language.TryStatementAst] ..., because the PSScriptAnalyzer install must run inside the retry's try") |
| M2 `-RequiredVersion 1.25.0` dropped from the install | `4BD604E760A67A26C4446A331178C47F` | `runs PSScriptAnalyzer 1.25.0, the version the dev host runs (cdt-39)` only ("Expected '1.25.0', because CI installs the analyzer the dev host runs, but got $null.") |

Extra probes, same driver: the pre-fix `ci.yml` (`34c7ccd4`, md5 `97FA06E7448C75660F9C5DB534F1E709`)
fails both analyzer tests (the retry case on the missing `-ErrorAction Stop`, the version test on
the missing pin) and nothing else; M1's shape applied to the Pester step
(`2F9E101E7ED3F950D16F93B16E8572CA`) fails the Pester case and nothing else.

The comments were written after the round, in their own commit, so the md5s above are the fix
commit's bytes. Post-comment `ci.yml` md5 `A34104CA659CD81F8C579DB146CA003A` (LF); the comments are
PowerShell `#` lines inside the step's `run: |` block, which the parser ignores, and the whole
suite was rerun on them (Gates).

One assertion message changed in that commit too, after a probe (scratch,
`cdt39-stepprobe/probe-erroraction.ps1`): the loop with a version that does not exist, run the way
GitHub runs a pwsh step (`pwsh -command ". '<file>'"`). With `-ErrorAction Stop` the failure was
caught on attempts 1 and 2 and rethrown on 3 (exit 1), with or without GitHub's
`$ErrorActionPreference = 'stop'` prefix; without the parameter but with the prefix, the same;
with neither, the failure skipped the catch, the loop broke as if the install had succeeded and the
script ran on (exit 0). So the parameter does not change today's CI behaviour; it keeps the retry
working without resting on the runner's prefix. The retry test's `-Because` had said the catch
never sees the failure without it; it now says the catch sees it only when the shell's error
preference is Stop. Pass and fail are unchanged.

## Files changed

`.github/workflows/ci.yml`, `tests/ps/DeployInvariants.Tests.ps1`, this record. No version bump:
CI and tests only.

## Gates - 2026-10-22

One pwsh 7.6.6 session (scratch, `cdt39-gates/gates.ps1`), Pester 6.2.0 imported before the
analyzer, PSScriptAnalyzer 1.25.0, on the final tree, comments included:

- `Invoke-ScriptAnalyzer -Path tests/ps/DeployInvariants.Tests.ps1 -Severity Error`: 0. Its
  warnings: 15, the same 15 as at `34c7ccd4`, all at 1645-1679, untouched.
- The CI step's own run, `-Severity Error,Warning`, over the 31 tracked `.ps1`/`.psm1`/`.psd1`
  files (CI analyzes before anything is built, so local `bin/` and `obj/` output is left out): 0
  errors, 298 warnings; the test file is the only PowerShell file changed.
- `Invoke-Pester tests/ps`: 477 passed, 0 failed, 0 skipped, 0 not run (103 s), against the
  Release build of the app (`dotnet build ExchangeAdminWeb.csproj -c Release`, exit 0).
- Windows PowerShell 5.1.20348 `ParseFile` of `tests/ps/DeployInvariants.Tests.ps1`: 0 errors, at
  the fix commit's bytes and again with the comments.
- `git diff --check HEAD`: clean. `git add -A`, then `tools/Test-AsciiOnly.ps1`: passed; `ci.yml`
  and this record hold no non-ASCII byte.
- `tools/Install-ExchangeAdminWeb.ps1`: unchanged, and still never contains `deploy.ps1`.

Not run: `dotnet build ExchangeAdminWeb.slnx` and `dotnet test` (no .NET source changed), and any
GitHub run. No coder self-review verdict is claimed.

## Known gaps

- Nothing ran on a GitHub runner; the guards parse `ci.yml`, and the step was probed on the dev
  host. The next CI run is the first real execution of the new step.
- A real "Package failed to download" cannot be produced on demand; that it reaches the catch is
  an assumption resting on the probe's `Install-Package` error taking the same path (Fix).
- The runner image's own analyzer copies and `PSModulePath` order were not inspected; the versioned
  import makes the step independent of both, and fails it if 1.25.0 is missing.

## Candidates noticed, not fixed

- LOW, same class as this finding: the CI Pester install is `-MinimumVersion 5.5`, unpinned, so CI
  runs the Gallery's newest Pester (6.2.0 on 2026-10-22, the dev host's version) and a new
  release changes CI's test runner with nothing in the repo changing.
