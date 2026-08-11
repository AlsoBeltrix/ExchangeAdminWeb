# pgwt-2: A DN-only target principal silently skips the pattern and direct-identity rules

**Severity**: MEDIUM — a group protected by sAMAccountName pattern or object GUID reads as unprotected, with the DN-path tests green.
**Status**: Verified
**Branch**: —
**Commit**: `7c5f8a6` (plan revision)

## Evidence

`docs/ProtectedGroupWriteTarget-Plan.md` T2 says to gate the on-prem target on the
module's existing resolution, `ResolveAdGroupIdentity`
(`Services/GroupManagementService.cs:368-391`) — which returns a bare distinguished-name
string (`:385-386`), nothing else.

A `ResolvedDirectoryPrincipal` built from a DN alone reaches the four rules like this:

- `CheckOuMatches` (`ProtectedPrincipalService.cs:628-639`) — works, reads
  `DistinguishedName`.
- The `Groups` in-chain rule (`:641-673`) — works, keyed on DN.
- `CheckDirectUserMatches` via `MatchesIdentity` (`:572-598`) — **partially blind**: it
  compares `SamAccountName`, `ObjectGuid`, `UserPrincipalName` and `PrimarySmtpAddress`
  too, and all four would be null.
- `CheckPatternMatches` (`:610-620`) — **returns immediately** when `SamAccountName` is
  empty (`:612-613`). Every pattern rule is skipped.

## Predicted observable failure

An administrator protects the admin group family with the pattern `adm-*`. The new target
gate resolves `adm-tier0` to a DN, builds a principal with no `SamAccountName`,
`CheckPatternMatches` returns at its first line, and the group is treated as unprotected —
members can be added to it freely. Tests written around the `Groups`-list DN path all pass,
because that is the one rule a DN satisfies.

## What

The plan reused a resolver built for a different purpose. A DN is enough to WRITE to a
group and is not enough to ASK whether it is protected; the protection engine's rules key
on four identifiers, and three of them would be absent.

## Approach

T2 now requires resolving the on-prem target once into a full immutable snapshot —
`DistinguishedName`, `SamAccountName`, `ObjectGuid`, `mail`, `Name` — and passing that
snapshot to the gate, with its DN used for the write, so the object cleared is the object
written. The plan states explicitly that the existing string-returning resolver is not
sufficient for a protection decision, and the verification section requires a pattern-rule
test alongside the DN one.

## Files changed

- `docs/ProtectedGroupWriteTarget-Plan.md` — T2 target resolution; pattern-rule coverage
  in Verification

## Guard proof

Not applicable: plan document. The implementation slice must include a target protected by
a sAMAccountName PATTERN, not only by the `Groups` list — a suite that exercises one rule
proves nothing about the three that need identifiers the DN does not carry.

## Coder dispute (if any)

None. Verified against the resolver and all four rule methods before admitting.

## Known gaps

The same DN-only hazard applies to any future caller that builds a principal for a
convenience reason. Not generalised here; noted so a later reader sees the shape.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade: fallback — frontier equals standard on this transport, owner-ruled 2026-08-03)

openreview over `2eedaa9094ac58bb8ff30d1eab98a1fbf39a7826..503c1a8dcaf3a599c8f62d3423cbaa62ed638812`,
verdict `acceptable_with_changes`, `capability_ok: true`, 2026-08-11T20:22Z.
