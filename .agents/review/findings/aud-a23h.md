# aud-a23h: Migration eligibility reads fail-open under -ErrorAction Ignore, and the approve path reports completion whether or not Complete-MigrationBatch ran

**Severity**: MEDIUM - two decisions taken from reads or writes whose failures are suppressed
(Known Failure Class 2; the aud-a23g part-1 class on the module's other paths). (1)
`CheckMigrationEligibilityAsync` (`Services/MigrationService.cs` around 76-89) reads
`Get-MigrationUser -Identity` and `Get-Mailbox -Identity` under `-ErrorAction Ignore`, so a failed
read - a throw included - reads as "no migration in progress" or "no cloud mailbox", and the
operator is told a user is eligible when the directory could not be asked. (2)
`ApproveMigrationUserAsync` runs `Set-MigrationBatch` twice and `Complete-MigrationBatch` through
`Ignore` / `InvokeOptional`, so none of them can fail; the page reports "initiated completion"
whether or not `Complete-MigrationBatch` ran.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23g agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At `cafef918`, `Services/MigrationService.cs`: `CheckMigrationEligibilityAsync` (the two `Ignore`
reads and how their empty results are interpreted); `ApproveMigrationUserAsync` (the two
`Set-MigrationBatch` calls and `Complete-MigrationBatch`, each through `InvokeOptional`). Compare
aud-a23g part 1: `-ErrorAction Stop`, a failed call reported as a failure, never as an empty answer.

## Approach (to be written by the implementing agent before code)

- Eligibility: run both reads under `-ErrorAction Stop`; a failure is an eligibility result of
  "could not be determined: <reason>" that the page shows and that blocks the action (fail closed,
  no guess); a genuine not-found (the cmdlet's own not-found error, recognised by its category or
  `FullyQualifiedErrorId`, not by text) keeps its current meaning. Label the recognition rule an
  assumption until a dev check.
- Approve: each write under `-ErrorAction Stop`; a failure is the row's failure naming the step
  (`Set-MigrationBatch` / `Complete-MigrationBatch`), audited as failed, never "initiated completion".
  Where the write's success must be confirmed, read the batch back (aud-a2l/a2p shape) before
  reporting.
- Tests through the Migration stub runspace: a throwing `Get-MigrationUser` yields "could not be
  determined" and blocks; a not-found yields "no migration in progress"; a throwing
  `Complete-MigrationBatch` yields a failed, audited row; a clean run reports completion. Mutations
  at the call site: `Ignore` again on the eligibility read; `InvokeOptional` again on the complete
  call. `Migration.razor` has line-keyed `ClickGateRegistry` entries - edit in place or re-anchor
  per the playbook.

## Guard proof

Pending.
