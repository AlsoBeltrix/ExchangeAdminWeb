# cons-3: the ConfigDbToolInApp plan still claims a five-second db CLI wait

**Severity**: LOW - an operator document now imprecise. `docs/ConfigDbToolInApp-Plan.md` says the `db` command-line
mode waits 5 s on a held database. aud-a67 (2) brought it down from a measured 30.5 s to a measured 7.8-9.4 s, and the
residue is structural: `busy_timeout` is a PRAGMA settable only after opening, so the open and the statement each get
one wait. The plan reads Implemented, so this is a correction, not a relabel.
**Status**: Open - admitted 2026-10-26 from the aud-a64/a67 agent's hand-back; inside the released scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (aud-a64/a67 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `d0fc4c70`; the measurement is in the aud-a67 record (Release executable against a database another process holds
exclusively; note a plain `BEGIN IMMEDIATE` takes RESERVED and does not block readers, so the fixture needs a journal
spill to take EXCLUSIVE).

## Approach

- State the measured figure and why it is about twice the configured timeout; no behaviour change; doc check only.

## Guard proof

Not applicable (docs only).