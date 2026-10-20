# aud-operational-hardening: the CI gates that were not running, and one that still needs a host

**Severity**: MEDIUM. Nothing was broken in the app. What was broken is the evidence: a CI
job that reported green while skipping the tests it existed to run, and an invariant nothing
exercised anywhere.
**Status**: Two of three fixed; the third is implemented opt-in and needs a host check
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

The last three entries in the plan's lower-priority Wave 4 list: *"narrow publish-folder
Modify permissions; require the SQLite-dependent CI tests to actually run; exercise
5.1-compatible scripts under Windows PowerShell 5.1."* All three verified.

### The SQLite tests never ran in CI

Every test in `SqliteConfigBackup.Tests.ps1` is gated on `-Skip:(-not $HasSqlite)`, and
`.github/workflows/ci.yml` never installed `sqlite3`. So **the logic that protects the
config database across every deploy, promotion and cutover had zero CI coverage**, and the
job reported success by skipping it. The skip gate is right - a developer without sqlite3
should not be blocked - but in CI it hides exactly what the job is for.

### Windows PowerShell 5.1 was never exercised

Every step ran `shell: pwsh` (7.x). Architectural Invariant 6 exists because 5.1 reads a
BOM-less non-ASCII file as ANSI and turns it into a parse error, and `deploy.ps1` plus the
modules it imports are read by 5.1 in production. Nothing anywhere ran them under 5.1.

### The publish folder grants Modify to the identity that executes it

`Set-DirectoryAcl -Path $PublishPath -Identity $appPoolIdentity -Rights "(OI)(CI)M"`. The
app pool identity can rewrite the binaries it runs.

## Approach

**sqlite3 is installed before Pester, and a skip now fails the build.** `Invoke-Pester -PassThru`
and a non-zero `SkippedCount` is an error, naming each skipped test. A gate that can quietly
stop testing is not a gate.

**A new CI step parses the 5.1-critical scripts under `shell: powershell`**, which is
Windows PowerShell 5.1 on `windows-latest`, and asserts `PSVersion.Major -eq 5` so it cannot
silently become another pwsh step. The scripts are **parsed, never run** - executing a
deploy on a CI runner is neither possible nor desirable, and a parse is what Invariant 6 is
actually about.

Verified by running that exact logic locally under real 5.1 (`Desktop 5.1.20348.5756`): all
four files parse. Then proven to bite by appending a BOM-less smart quote and an
unterminated string - 5.1 reported the parse error, which is the failure mode the invariant
describes.

**The ACL narrowing is implemented as `-NarrowPublishAcl`, opt-in.** It grants
ReadAndExecute instead of Modify on the publish root; `logs\`, `config\` and any shared
config directory keep Modify, because those are the paths the app actually writes.

**It is opt-in deliberately, and that is a judgement worth disagreeing with.** The reduction
cannot be validated from a build machine: anything writing under the publish root - a module
caching to disk, Data Protection falling back to the content root, a file provider - would
fail only after the pool restarts, in an environment nobody connects to the deploy. Shipping
a silent permission reduction to every installation on reasoning alone is how a deploy
breaks production at a moment nobody attributes to it. The switch, the warning it prints and
this record are what an owner needs to adopt it: run a dev install with it, exercise the
modules, read the app log, then make it the default.

## Files changed

- `.github/workflows/ci.yml` - sqlite3 install, skip-is-failure, the 5.1 parse step
- `tools/Install-ExchangeAdminWeb.ps1` - `-NarrowPublishAcl` and its help text
- `tests/ps/DeployInvariants.Tests.ps1` - a `CI gates` Describe
- `tests/ps/InstallerConfigBackup.Tests.ps1` - the ACL guard
- `tests/ps/TestSuiteIntegrity.Tests.ps1` (new) - see below

No version bump: CI and ops-script changes, and the installer default is unchanged.

## A worse problem found while fixing these

A stray edit left an unterminated string in `DeployInvariants.Tests.ps1`. Pester could not
parse it, ran **none** of its ~80 tests, and reported `P=170 F=0`. Green - with a third of
the PowerShell suite silently gone. I noticed by accident, reading an unrelated mutation
result and wondering why the number had moved.

`TestSuiteIntegrity.Tests.ps1` now parses every sibling in `tests/ps` and fails if any does
not, and also fails a file that declares no `Describe`. The check cannot live inside the
file it protects - a broken file runs nothing, including its own guard - so it parses its
siblings from outside.

Proven by appending an unterminated string to `JobStateWarning.Tests.ps1`: the suite lost 7
tests and the guard failed. Before, that was a silent green.

## Guard proof

| Test | What it pins |
| --- | --- |
| `installs sqlite3 before running Pester` | the `choco install sqlite` command, before the Pester step |
| `fails the build when a test skips in CI` | `SkippedCount -gt 0` is an error |
| `parses the 5.1-critical scripts under Windows PowerShell 5.1` | `shell: powershell`, the version assertion, and all four file names |
| `narrows the publish ACL only when asked...` | the switch, the conditional rights, and that logs/config keep Modify |
| `parses every test file, so none is silently skipped` | the incident above |
| `has a Describe block in every test file` | the other way a file contributes nothing |

| Mutation | Result |
| --- | --- |
| `choco install sqlite` removed | FAIL - the sqlite guard |
| skip check disabled | FAIL - the skip guard |
| `shell: powershell` changed to `pwsh` | FAIL - the 5.1 guard |
| ACL always narrow (no switch) | FAIL - the ACL guard |
| `config\` narrowed to RX | FAIL - 3 tests, including two that predate this work |
| an unterminated string in a test file | FAIL - the integrity guard |

**One guard was found weak by its own mutation**: renaming the step to
`Install sqlite3-disabled` still contained `Install sqlite3` as a substring and passed. It
now pins the command rather than the step name.

Gate: ASCII lint clean; `Invoke-Pester tests/ps` 255 passed / 0 failed / **0 skipped**;
PSScriptAnalyzer 0 errors; `git diff --check` clean. The .NET suite was not re-run - no C#
or Razor changed since it last passed at 4456.

## Known gaps

- **`-NarrowPublishAcl` has never been run against IIS.** Nothing here proves an
  ASP.NET Core app under ANCM tolerates ReadAndExecute on its content root. That is the
  whole reason it is opt-in.
- **The CI steps have not run in CI.** They are asserted by source guards and the 5.1 logic
  was executed locally under real 5.1, but no pipeline run has exercised `choco install
  sqlite` on a GitHub runner. The first push will prove or disprove it.
- **`choco` is assumed present on `windows-latest`.** It is today; if that changes the step
  fails loudly, which is the right direction.
- **The skip-is-failure rule is blunt.** A future test legitimately skipped for another
  reason - a platform guard, say - would fail CI and need the rule refined rather than
  removed.

## Closeout

Fixed on `master`. Independent review not yet run on this finding.
