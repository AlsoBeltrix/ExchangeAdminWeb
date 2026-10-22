# cdt-40: The CI Pester install is unpinned, so a new Pester release changes CI's test runner with nothing in the repo changing

**Severity**: LOW - CI reproducibility, the cdt-39 class. `.github/workflows/ci.yml`, the Pester
step, installs `-MinimumVersion 5.5`; CI therefore runs the Gallery's newest Pester (6.2.0 today, the
dev host's version). A later release would change the runner's behaviour on every push with no
commit in the repo explaining it, and could fail or silently change what the suites assert.
**Status**: Fixed on branch; closed on guard proof; review pending
**Branch**: `worktree-agent-ab74c2738ad037db4` (forked at `dc0555e2`)
**Commit**: -
**Reviewer**: implementer (surfaced by the cdt-39 agent, 2026-10-22, its one candidate)

## Evidence

At `86f3d4cb`, `.github/workflows/ci.yml`, the Pester step's install loop (`-MinimumVersion 5.5`);
the dev host runs Pester 6.2.0 and every local gate imports `-RequiredVersion 6.2.0`.

## Approach (to be written by the implementing agent before code)

- Pin the install to `-RequiredVersion 6.2.0` and import `-RequiredVersion 6.2.0` before
  `Invoke-Pester`, the cdt-39 shape; extend the cdt-39 version guard in `DeployInvariants.Tests.ps1`
  to pin both installs' versions. Mutation at the call site: drop the pin from the Pester install.
  Pure ASCII; `Import-Module Pester -RequiredVersion 6.2.0` before the analyzer locally; 5.1 parse of
  the test file.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("EVERYTHING"); admitted at `ea687a30`; forked at `dc0555e2`. Evidence re-read
there: `ci.yml`, the Pester step (79-106), installs inside its retry loop (88-97) with
`Install-Module Pester -Force -Scope CurrentUser -MinimumVersion 5.5 -SkipPublisherCheck
-ErrorAction Stop` (90), and `$result = Invoke-Pester tests/ps -CI -PassThru` (98) auto-loads the
first Pester on `PSModulePath`; nothing imports a version. CI and tests only - no app or module
behaviour, so no version bump.

1. `ci.yml`, the Pester step, line 90: `-MinimumVersion 5.5` becomes `-RequiredVersion 6.2.0`. The
   other flags and the loop (88-97) are unchanged; the cdt-39 retry test already pins the loop.
2. A new line between the loop (97) and `Invoke-Pester` (98): `Import-Module Pester -RequiredVersion
   6.2.0 -ErrorAction Stop`, the analyzer step's shape (65). The install does not decide which
   Pester runs: auto-loading takes the first copy on `PSModulePath`, not the newest (probed in
   cdt-39), so the versioned import does, and it fails the step when 6.2.0 is not there.
3. Version 6.2.0: `Get-Module Pester -ListAvailable` on the dev host lists 6.2.0, 6.0.1 and 5.7.1
   (CurrentUser) and 3.4.0 (the Windows PowerShell inbox copy); every local gate imports
   `-RequiredVersion 6.2.0`. `Find-Module Pester -AllVersions`, 2026-10-22: 6.2.0 is the Gallery's
   newest stable (published 2026-09-09), the version `-MinimumVersion 5.5` installs today, so the
   pin changes nothing CI runs now; it stops a later release from changing CI's runner with nothing
   in the repo changing.
4. Guard, `tests/ps/DeployInvariants.Tests.ps1`, `Describe 'CI gates'` (1988-2151). The cdt-39
   version test (2124-2150) becomes `It 'runs <Module> <Version>, the version the dev host runs
   (<Finding>)' -TestCases`, PSScriptAnalyzer (1.25.0, runner `Invoke-ScriptAnalyzer`, cdt-39) and
   Pester (6.2.0, runner `Invoke-Pester`, cdt-40). The body is unchanged but for the case's step,
   module, version and runner: the step's one `Install-Module` carries `-RequiredVersion <Version>`;
   exactly one `Import-Module <Module>`, a top-level statement, with `-RequiredVersion <Version>` and
   `-ErrorAction Stop`, after the install and before the step's one `<Runner>` call. The analyzer
   case expands to the cdt-39 test's name, so that record's guard proof still names a real test.
   The retry test (2073-2122) is unchanged: it counts `Install-Module` only, so the import does not
   reach it.
5. Mutations at the call site (`ci.yml`), the cdt-39 driver shape (scratch, `cdt40-mutations`):
   md5 of the LF content (as git stores it) and of the CRLF working copy before and after, the
   whole `DeployInvariants.Tests.ps1` in a fresh pwsh under Pester 6.2.0, the fixed bytes copied
   back, both md5s re-matched, the file touched, the file rerun green. M1 `-RequiredVersion 6.2.0`
   dropped from the Pester install: the version test's Pester case fails on the install's version,
   and nothing else. M2 the import dropped: the same case fails on the import count, and nothing
   else. Extra probe, outside the table: the pre-fix `ci.yml` (`dc0555e2`) fails the Pester case and
   nothing else. Comments are written after the round, in their own commit; the md5s are of the fix
   commit's bytes, and the post-comment md5 is recorded.
6. Behaviour probe of the step (scratch, `cdt40-stepprobe`): its loop and import lifted from
   `ci.yml` and run in a fresh pwsh behind GitHub's `$ErrorActionPreference = 'stop'` prefix, with
   `-WhatIf` on the install: PowerShellGet resolves 6.2.0 on the Gallery, the import loads 6.2.0,
   and `Invoke-Pester` resolves to it.

Files: `.github/workflows/ci.yml`, `tests/ps/DeployInvariants.Tests.ps1`, this record. Pure ASCII;
the test file parses under Windows PowerShell 5.1 with zero errors.

Gates, one pwsh session: `Import-Module Pester -RequiredVersion 6.2.0`, then `Invoke-ScriptAnalyzer
-Path tests/ps/DeployInvariants.Tests.ps1 -Severity Error` zero; `Invoke-Pester tests/ps` 0 failed,
0 skipped; 5.1 `ParseFile` of the test file zero errors; `git diff --check HEAD`; `git add -A`,
then `tools/Test-AsciiOnly.ps1`; `tools/Install-ExchangeAdminWeb.ps1` still never names
`deploy.ps1`.

## Fix

As planned. `.github/workflows/ci.yml`, the Pester step (79-107): the install at 90 carries
`-RequiredVersion 6.2.0` where it had `-MinimumVersion 5.5`, its other flags and the loop (88-97)
unchanged; `Import-Module Pester -RequiredVersion 6.2.0 -ErrorAction Stop` at 98, a top-level
statement between the loop and `$result = Invoke-Pester tests/ps -CI -PassThru` (99); 100-107
unchanged. The analyzer step is unchanged. `tests/ps/DeployInvariants.Tests.ps1`, `Describe 'CI
gates'` (1988-2154): the version test (2124-2153) runs two cases; the retry test (2073-2122) is
unchanged.

Behaviour probe of the step itself (scratch, `cdt40-stepprobe/probe.ps1`): its loop and import
lifted from the `ci.yml` run block (parsed, not copied by hand) and run in a fresh pwsh 7.6.6 the
way GitHub runs a pwsh step, `pwsh -command ". '<file>'"` behind the `$ErrorActionPreference =
'stop'` prefix; `Invoke-Pester` itself was never run. With `-WhatIf` on the install, PowerShellGet
resolved "Version '6.2.0' of module 'Pester'" on the Gallery, the import loaded 6.2.0 and
`Invoke-Pester` resolved to it (exit 0). With the Windows PowerShell module path, which holds the
inbox Pester 3.4.0, moved first on `PSModulePath`: without the import, `Invoke-Pester` resolved to
3.4.0 and an unversioned `Import-Module Pester` loaded 3.4.0; with the step's import, 6.2.0. The
import naming a version that is not installed (9.9.9) failed the script ("no valid module file was
found in any module directory") before the line after it ran (exit 1). The loop's failure path is
not re-probed: cdt-39 probed the identical loop in the analyzer step, and only the install's version
parameter changed here.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`, `Describe 'CI gates'`) | Pins |
| --- | --- |
| `runs <Module> <Version>, the version the dev host runs (<Finding>)`, cases PSScriptAnalyzer 1.25.0 (cdt-39) and Pester 6.2.0 (cdt-40) | The step's `run: \|` block, parsed: its one `Install-Module` carries `-RequiredVersion <Version>`; one top-level `Import-Module <Module> -RequiredVersion <Version> -ErrorAction Stop`, after the install and before the step's one `<Runner>` call (`Invoke-ScriptAnalyzer`, `Invoke-Pester`) |

The analyzer case expands to the name the cdt-39 record's guard proof uses, `runs PSScriptAnalyzer
1.25.0, the version the dev host runs (cdt-39)`; the Pester case is `runs Pester 6.2.0, the version
the dev host runs (cdt-40)`.

Session-local driver (scratch, `cdt40-mutations/mutate.ps1`); each run is the whole
`DeployInvariants.Tests.ps1` in a fresh pwsh under Pester 6.2.0. Fixed `ci.yml` md5
`A12C3F10DB2D6D4498AA50AD59D1A0D4` (LF content, as git stores it - this commit's bytes; the CRLF
working copy is `02B7217F19D137AECFBD1C37F8F3868C`); baseline 115 passed, 0 failed, 0 skipped. Each
mutant was applied to those bytes (both md5s checked before it); after its run the fixed bytes were
copied back, both md5s re-matched, the file was touched and the file rerun: 115 passed, 0 failed, 0
skipped, every time.

| Mutation (call site, `ci.yml`) | md5 after (LF) | Failed |
| --- | --- | --- |
| M1 `-RequiredVersion 6.2.0` dropped from the Pester install | `7AD889980D427E8A7B33B6FAD10683C2` | `runs Pester 6.2.0, the version the dev host runs (cdt-40)` only ("Expected '6.2.0', because CI installs the Pester version the dev host runs, but got $null.") |
| M2 the Pester import dropped | `43F11C73C837BF02D08504CFB3CF3004` | `runs Pester 6.2.0, the version the dev host runs (cdt-40)` only ("Expected 1, because installing a version does not make it the copy that loads; the import does, but got 0.") |

Extra probe, same driver: the pre-fix `ci.yml` (`dc0555e2`, md5 `A34104CA659CD81F8C579DB146CA003A`,
the driver's reconstruction checked against git's blob) fails the Pester case on the install's
version and nothing else. The analyzer case passed on all three.

## Files changed

`.github/workflows/ci.yml`, `tests/ps/DeployInvariants.Tests.ps1`, this record. No version bump:
CI and tests only.
