# aud-a2v: Conference Rooms - the row's own early refusals are not logged, a credential failure names no room, an operator message carries a .NET parameter suffix, and refusal wording is inconsistent

**Severity**: LOW - observability and wording; no wrong write. (1) `SetRoomMetadataAndListAsync`'s own
early refusals (no validated room, an invalid time zone, a recipient that is not a room mailbox) are
returned and logged nowhere by the service (the aud-a2t/a2u class on the row's entry). (2) When the
credential lookup fails inside Delinea (an incomplete secret or an HTTP error), the log never names
the room or row; only `ModuleCredentialService`'s missing-secret-ID line carries the purpose. (3) The
unmappable-country message shown to operators ends with .NET's " (Parameter 'countryOrRegion')"
suffix (`ArgumentException.Message`). (4) The City/State/Country preflight's early refusals do not end
"Nothing was written." while the room-list preflight's do; both are true, the operator sees two
styles.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2u agent, 2026-10-22, its four candidates)

## Evidence

At `bf7ba5b4`, `Services/ConferenceRoomService.cs`: `SetRoomMetadataAndListAsync`'s early returns;
the credential step's catch (what it logs); `BuildSyncedUserAttributes`' country mapping throw and
the message that reaches the row; the two preflights' refusal suffixes.

## Approach (to be written by the implementing agent before code)

- Log the row's early refusals once at Warning naming the room and the reason (aud-a2u placement).
- The credential step's failure log names the room (and the list where there is one) beside the
  purpose; the Delinea-side log stays as it is.
- The country mapping throws with a message that has no parameter suffix (or the caller takes
  `ArgumentException.Message` without it); the operator sees the country text and that it could not
  be mapped.
- One suffix rule for both preflights: every refusal decided before any write ends "Nothing was
  written."; the tests pin the City/State/Country preflight's early refusals end so.
- Tests through `ExchangeWriteHarness` with the capturing logger; mutations at the call site per part.

## Guard proof

Pending.
