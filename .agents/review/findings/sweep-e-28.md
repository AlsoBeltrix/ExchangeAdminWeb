# sweep-e-28: a Room Finder row looked the AD credential up and opened an AD session up to four times (sweep item 28)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 28: `ConferenceRoomService.cs`
`LookupDirectoryCredentialAsync` at the City/State/Country preflight and write and the on-prem room-list preflight and
add, each into a fresh runspace with its own ActiveDirectory module import; estimated +7-10 s per synced-list row.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-e` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-e`, from master `54dcfb40`)
**Commit**: `a3eca651` (`fix(sweep-e, item 28): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- At `54dcfb40` each of the four paths ran `LookupDirectoryCredentialAsync`, `OpenDirectoryRunspace()`,
  `ActiveDirectoryModuleImport.Import(ps)` and `CreateAdCredential(...)` for itself.

## Approach (as built) - kd-88's one-credential-per-operation rule, the row as the operation

- `ConferenceRoomDirectorySession` (new, internal): the row's credential lookup made once - the first lookup's answer,
  credential or failure, serves every later step; a lookup that threw (the operator's cancel) keeps nothing - and one
  directory runspace opened once, the module imported once, the PSCredential prepared once. Each step takes the pipeline
  with commands and every stream cleared, so a record one step left cannot be another's `HadErrors`. A runspace that is
  no longer open is replaced and the module imported into it again. Disposed with the row.
- `SetRoomMetadataAndListAsync` creates one per row and threads it to the four paths; `AddToRoomListAsync` takes it as an
  optional parameter and makes its own when called outside a row (the tests). What each step reads and writes, its
  order, its cancellation looks and its refusal words are unchanged. No protected-principal check is added (owner ruling
  2026-11-03).
- The DelineaService five-minute credential window already made repeat fetches cheap; the saving here is chiefly the three
  extra runspaces and module imports per row.
- Comments that described per-path runspaces updated. ConferenceRooms `2.20.1`. No help change (no visible behaviour).

## Where the code goes beside the fix shape

- Per row, not per job: a job-wide session would hold an AD runspace across a whole job and couple rows' failures; per row
  meets "one credential and one runspace per row or per job".
- The write paths' own "no credential" refusals stay as defensive code; within a row they are unreachable (the row's
  lookup is the preflight's, which refused the row had it returned none). The on-prem add's is still reached when it is
  the row's first lookup or is called outside a row.
- Tests that injected failures into the write paths' own runspace, import or lookup (they no longer exist after
  Set-Place) were changed: the runspace and import cases now close the session's runspace at Set-Place (harness
  `AtSetPlace`/`CloseDirectoryRunspaces`), so the reopen path is what they test; the "credential" cases and the write-side
  cases of the two credential-refusal theories became `TheRowsWrites_AskForNoCredentialOfTheirOwn` and
  `AnAddMadeOnItsOwn_WhoseLookupReturnsNone_IsRefused`; three cancellation tests that used the write's lookup as their
  "after Set-Place" moment now use Set-Place itself, and one page test drops its City so the room-list lookup is the row's.

## Guard proof

| Mutation | Failing tests |
|---|---|
| N1 the session keeps no lookup | `ConferenceRoomDirectorySessionTests.TheFirstLookupsAnswer_...`; `ConferenceRoomWriteIdentityTests.TheRowsWrites_AskForNoCredentialOfTheirOwn` (both), `EachRowHasASessionOfItsOwn`, `TheRowsAdStepsShareOneLookupOneRunspaceAndOneImport` |
| N2 a new runspace for every step | `OneRunspaceAndOneImport_ServeEveryStep_EachHandedAClearPipeline`; `EachRowHasASessionOfItsOwn`; `TheRowsAdStepsShare...` |
| N3 a closed runspace is reused | `ARunspaceNoLongerOpen_IsReplaced_AndTheModuleImportedAgain`; `AFailureAtEachStepOfTheCityStateAndCountryWrite_NamesThatStep(runspace, import)`; `AFailureAtEachStepOfTheOnPremRoomListAdd_NamesThatStep(runspace, import)`; `AFailedImportOnTheOnPremRoomListPaths_NamesThatStep(add)` |
| N4 the pipeline is not cleared between steps | `OneRunspaceAndOneImport_ServeEveryStep_EachHandedAClearPipeline` |
| N5 the two writes get a fresh session | `TheRowsWrites_AskForNoCredentialOfTheirOwn` (both); `EachRowHasASessionOfItsOwn`; `TheRowsAdStepsShare...` |
| N6 the module is imported for every step | `OneRunspaceAndOneImport_ServeEveryStep_EachHandedAClearPipeline` |

## Gates

Run once for the branch at its tip; see the sweep-e hand-back. Not run: a live Room Finder row.

## Deploy-time checks

- One synced-list Room Finder row with City/State/Country: the app log should show one credential purpose for the row
  ("Set-ADUser preflight for ...") and the row should complete; time it against the old ~25-30 s.
