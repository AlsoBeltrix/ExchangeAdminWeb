# aud-a38k: The "nothing at the folder" refusal tells the cutover it is refusing to replace files it never replaces

**Severity**: LOW - wording only. `Assert-AppPoolServesPath`'s refusal when IIS serves nothing at
the folder says "Refusing to replace files that may still be served" to every caller, the cutover
included; `Move-ConfigDbToShared.ps1` replaces no files, so the operator reads a reason that does
not describe what was refused. Dates from aud-a38g; the aud-a38j `-NestedNodes Allow` path kept the
text.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a38j/a7f/a7g agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `f18431d0`, `tools/AppPoolControl.psm1`, `Assert-AppPoolServesPath`, the nothing-at-the-folder
refusal text; `tools/Move-ConfigDbToShared.ps1` `Assert-PoolServesInstance` wraps it with
`Write-Fail` and appends its own "Nothing has been stopped or copied." but the module's clause
about replacing files is still inside the message.

## Approach (to be written by the implementing agent before code)

- Word the module's refusals by the rule, not the caller's action: under `-NestedNodes Refuse` the
  mirror clause stays (those callers mirror); under `Allow` the refusal says the named pool does not
  serve the folder and nothing will be stopped, with no mention of replacing files. Pester: the
  cutover's lifted wrapper over the real module produces a message without the mirror clause; the
  promote/deploy messages keep it. 5.1 `ParseFile` zero errors; pure ASCII; `Import-Module Pester
  -RequiredVersion 6.2.0` before the analyzer. Mutation at the call site: the `Allow` message gets
  the mirror clause back.

## Guard proof

Pending.
