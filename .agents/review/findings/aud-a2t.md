# aud-a2t: Conference Rooms - the credential step has no test, and the room-list resolver's refusals are not logged

**Severity**: LOW - a test gap and an observability gap; no wrong write. (1) On the City/State/Country
paths the step "preparing the module's AD credential" is named (aud-a2q/a2r) but no test reaches it;
the room-list paths reach theirs only through a test seam, because the PAM provider refuses an
incomplete secret before the service sees it. (2) The refusals the room-list resolver and membership
read return without throwing (catalog unresolved, no group or several, group answers as another
object) are logged nowhere by the service on either path; only the row's audit carries the message,
so an operator reading the application log sees no trace of why a row was refused.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2s agent, 2026-10-22, its two candidates)

## Evidence

At `3ff4a6d4`, `Services/ConferenceRoomService.cs`: `ResolveAdGroupGuid`, `ResolveForestGlobalCatalog`,
`ReadOnPremListMembership` return refusals without a log line; the City/State/Country credential step
(`CreateAdCredential`) has no harness knob reaching it.

## Approach (to be written by the implementing agent before code)

- Log each returned refusal at Warning with the room, the list and the reason, once, at the point it
  is decided (not again at the caller); the audit is unchanged.
- Reach the credential step on the City/State/Country paths through the aud-a2s `IncompleteCredentialFor`
  seam and pin its step name; label it as not reachable live for the same reason aud-a2s recorded.
- Tests: each refusal logs one warning naming the reason; the credential step is named on both
  City/State/Country paths. Mutations at the call site: drop one refusal's log line; collapse the
  credential step name.

## Guard proof

Pending.
