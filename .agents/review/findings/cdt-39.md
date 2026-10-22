# cdt-39: The CI PSScriptAnalyzer install has no retry, so a transient Gallery download fails the powershell job

**Severity**: LOW - CI reliability. `.github/workflows/ci.yml`, the `PSScriptAnalyzer` step, runs
`Install-Module PSScriptAnalyzer -Force -Scope CurrentUser` once; the PowerShell Gallery's
intermittent "Package failed to download" (seen 2026-10-21 on the Pester install, fixed there by
ConfigDbToolInApp S2 with a three-attempt retry, and again 2026-10-22 on run 39847030059 for the
analyzer) fails the job on a docs-only commit. The two installs now behave differently for the same
transient cause.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
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

## Guard proof

Pending.
