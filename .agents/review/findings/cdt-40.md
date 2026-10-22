# cdt-40: The CI Pester install is unpinned, so a new Pester release changes CI's test runner with nothing in the repo changing

**Severity**: LOW - CI reproducibility, the cdt-39 class. `.github/workflows/ci.yml`, the Pester
step, installs `-MinimumVersion 5.5`; CI therefore runs the Gallery's newest Pester (6.2.0 today, the
dev host's version). A later release would change the runner's behaviour on every push with no
commit in the repo explaining it, and could fail or silently change what the suites assert.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
