# kd-9: ClickGateRegistry.cs still says tiers 2-4 are "audited, NOT approved for conversion"

**Severity**: LOW - a stale statement in a code file. `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` about line 75 says tiers 2-4 are audited and not approved for conversion; the owner approved them 2026-10-12 (`.agents/decisions.md`). It is a code file, so correcting it is a code change, which is why it sat in `.agents/state.md` Blockers.
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded in `.agents/state.md` Blockers, re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. No behaviour; a wrong comment that steers the next agent away from approved work.

## Approach

- Reword the comment to the current ruling and point at `docs/ClickGatingAudit-Plan.md`. No guard possible beyond the wording; say so.

## Guard proof

Not applicable (comment only); record the diff.