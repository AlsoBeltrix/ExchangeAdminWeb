# psl-7: Three services import the AD module without clearing the error stream, and two keep a dead HadErrors branch after a Stop import

**Severity**: LOW - latent. `GroupManagementService`, `ADAttributeEditorService` and
`ADOrganizationalUnitService` import the ActiveDirectory module without clearing the error stream
afterwards, so a record an import wrote past `-ErrorAction Stop` could be taken for a later read's
error (the psl-2/psl-3 class: a flag read for the wrong operation). `AccountLockoutRemediationService`
(around line 1047) and `ADDirectorySearchService` (around 885) keep an `if (ps.HadErrors)` branch
straight after a `-ErrorAction Stop` import, the dead pattern aud-a2o/a26g/a2r removed elsewhere -
measured: a failing import under Stop throws and leaves zero records. Also measured and worth a
comment where it matters: `PowerShell.HadErrors` reads true after an `Invoke` that threw, even with
zero records.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2r agent, 2026-10-22, candidates 2 to 4 of its hand-back)

## Evidence

At `027c3309`: `git grep -n "Import-Module" Services/*.cs` and the lines after each; the two
`HadErrors` branches named above. aud-a2r's `ConferenceRoomService.ImportActiveDirectoryModule` is
the shape to copy (import under Stop; `finally` clears commands and the error stream).

## Approach (to be written by the implementing agent before code)

- One shared import helper (a static in a shared PowerShell helper class, or the per-service copy
  of aud-a2r's) used by all five services: import under `-ErrorAction Stop`, the thrown error is the
  step's error, `finally` clears commands and the error stream. Remove the two dead branches with the
  comment saying why. Where code reads `HadErrors` after a caught throw, note the measured behaviour
  or restructure so it is not read then.
- Tests: for each service, a stand-in import that writes a non-terminating record is not taken for a
  later read's error; a failing import is reported as the import's failure. Mutation at the call site:
  drop one `finally` clear; restore one dead branch. No live AD.

## Guard proof

Pending.
