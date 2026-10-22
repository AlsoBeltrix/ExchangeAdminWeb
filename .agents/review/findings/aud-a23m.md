# aud-a23m: Migration - an audit or notification failure replaces a determined eligibility verdict, and the page's writes complete their activity as succeeded on failure

**Severity**: MEDIUM - Constitution: an audit failure must not make an already-completed operation appear
to have failed. (1) The catch in `CheckSingleEligibility` wraps the audit write and the notification as
well as the check, so a failed audit or notification turns a determined verdict into an error. (2) The
Migration page's write actions (create, approve, stop, remove and the rest) still complete their progress
activity `true` on failure; aud-a23i fixed only the loads.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23h/a23i agent, 2026-10-22, candidates 5 and 6 of its hand-back)

## Evidence

At `ab5361c8`: `Services/MigrationService.cs` `CheckSingleEligibility` (the try's extent);
`Components/Pages/Migration.razor`, every write action's `Complete(true)` (line-keyed by
`ClickGateRegistry`, 4377 lines, edit in place only; `ProgressRegistry` tests pin the activity shapes).

## Approach (to be written by the implementing agent before code)

- The verdict is determined first; the audit write and the notification run after it in their own guarded
  steps (the aud-a28q writers never throw; the notification's failure is logged and the verdict stands).
- Each write action completes its activity with its outcome, the aud-a23i load shape; the
  `ProgressRegistry` tests gain the write actions.
- Tests through `MigrationStubExchange` and a throwing notification stand-in; mutations at the call site:
  the audit back inside the verdict's try; one `Complete(true)` restored.

## Guard proof

Pending.
