# aud-a2u: Conference Rooms - the directory-object refusals and the pre-directory refusals are not logged

**Severity**: LOW - observability only; no wrong write. (1) `ConfirmDirectoryObject`'s refusals (no
object, or another object, answering for the proven objectGUID) are returned and logged nowhere on
all four directory paths. (2) The refusals returned before the directory work starts (an unmappable
country, no list GUID, no sync state reported, no proven directory object) are not logged by the
service either. Only the row's audit carries them, so the application log shows no trace of why a
row was refused (the aud-a2t class, on the remaining refusal sites).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2t agent, 2026-10-22, its two candidates)

## Evidence

At `932fd294`, `Services/ConferenceRoomService.cs`: the four callers of `ConfirmDirectoryObject`
turning its refusal into the row's; the early refusals in the row pipeline (`RunAsync` and the
preflights) before any runspace is opened.

## Approach (to be written by the implementing agent before code)

- Log each at Warning once, at the call site where the path turns the reason into the row's refusal
  (the aud-a2t placement), naming the room, the list where there is one, and the reason; row
  outcomes, messages, audit and notifications unchanged. Tests through `ExchangeWriteHarness` with
  the capturing logger: each refusal logs exactly one warning. Mutation at the call site: drop one
  log line per group.

## Guard proof

Pending.
