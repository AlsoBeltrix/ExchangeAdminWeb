# aud-a2x: Conference Rooms - every credential refusal says "not configured (set the AD Delinea Secret ID)" even when the ID is set and the Delinea lookup failed

**Severity**: LOW - operator guidance. All four directory credential refusals in `ConferenceRoomService`
carry the same text, so when the secret ID is configured and the lookup itself failed (an incomplete
secret, an HTTP error) the operator is sent to set an ID that is already set.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a2v agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At `6ac190f7`: `Services/ConferenceRoomService.cs`, the four credential lookups and their refusal texts
(aud-a2v added the log lines around them); `Services/ModuleCredentialService.cs` (what the lookup
returns, and whether it distinguishes "no ID" from "lookup failed").

## Approach (to be written by the implementing agent before code)

- The credential lookup's outcome distinguishes no ID configured from a lookup that failed (if the
  credential service already does, consume it; if not, surface the distinction without changing other
  callers' behaviour).
- Two refusal texts: "not configured (set the AD Delinea Secret ID)" only when no ID is set; otherwise
  "the AD credential could not be read from Delinea: <reason category>; nothing was written".
- Tests through the `NoCredentialFor` seam (aud-a2v) extended with a failed-lookup variant; mutation at
  the call site: one text for both again.

## Guard proof

Pending.
