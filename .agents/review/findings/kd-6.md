# kd-6: BlockedSenderService.UnblockSenderAsync takes no CancellationToken and has no timeout

**Severity**: LOW - a hang risk. `Services/BlockedSenderService.cs` `UnblockSenderAsync` (about 48) runs the unblock with no cancellation and no bound, so a stuck Exchange Online call holds the circuit's operation indefinitely.
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. Trigger: the EXO call stalls. Predicted failure: the operation never completes or fails; the activity shows progress forever.

## Approach (to be written by the implementing agent before code)

- Take the caller's token, bound the call with the module's standard timeout shape, report a timeout as a failed (or Undetermined, per the aud-a73 rule) result with its audit row.
- Mutation: the bound removed.

## Guard proof

Pending.