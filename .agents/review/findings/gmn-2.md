# gmn-2: The cycle guard tests the wrong direction and sits in the page slice

**Severity**: HIGH — as specified it refuses legitimate adds, misses the cycle it exists to catch, and is bypassable by any non-page caller.
**Status**: Verified
**Branch**: —
**Commit**: `c414619` (plan revision)

## Evidence

Two defects in one bullet of `docs/GroupMemberNesting-Plan.md` (S5, nesting guards).

**Direction.** The prose says to refuse when "the TARGET group is already a member,
directly or transitively, of the group being added" — the cycle. The filter given beneath
it is
`(&(distinguishedName=<candidate>)(memberOf:1.2.840.113556.1.4.1941:=<target>))`,
which asks the opposite question: is the CANDIDATE already inside the TARGET. That is the
already-a-member case, which is a benign no-op, not a cycle.

**Placement.** The slice list assigns the nesting guards to S5c, the page slice, while the
write entry point is `GroupManagementService.AddMemberAsync`
(`Services/GroupManagementService.cs:251-298`). The repo's standing rule is the opposite —
`GroupManagementService.cs:36-38` records that a previous page-only check was bypassed by
identity format and by non-page callers ("UI hiding is not security", Constitution).

## Predicted observable failure

An admin adds group B to group A where B is already a member of A: refused as a "cycle"
when it is an idempotent no-op. An admin adds group B to group A where A is already a
member of B: allowed, closing a real loop that cannot be repaired from this page. Both
outcomes are the inverse of the guard's stated purpose.

## What

The guard's stated intent is correct and its implementation sketch inverts it. Separately,
placing it in the page leaves the service — the only thing that actually writes — ungated,
repeating the exact defect the module's own comments record as previously shipped.

## Approach

The guard bullet now names TARGET and CANDIDATE explicitly, states the cycle query with
TARGET as its subject and CANDIDATE as the group searched, and carries an inline warning
that the mirror query answers the already-a-member question and must be an idempotent
no-op. The guards moved from S5c to S5b, inside `AddMemberAsync` before
`Add-ADGroupMember`, and S5c is restated as carrying no authorization or nesting logic at
all. A fail-closed rule was added: an unreadable cycle query refuses. AC11b pins both
directions and AC11c pins service-level enforcement with no page involved.

## Files changed

- `docs/GroupMemberNesting-Plan.md` — corrected filter direction; guards moved to the
  service slice

## Guard proof

Not applicable: plan document. The implementation slice must carry a pure predicate test
covering both directions — already-a-member allowed as a no-op, reverse nesting refused —
because a single-direction test passes against the inverted filter.

## Coder dispute (if any)

None. Verified against the plan text and the cited service code before admitting.

## Known gaps

Whether AD itself refuses a given cycle depends on group scope and is not relied on
either way; the guard is local and explicit for that reason.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade: fallback — frontier equals standard on this transport, owner-ruled 2026-08-03)

openreview over `618235e9e18bb957860e36a03f1a4b4c5cd42b38..074bfdb7ddffd91e5e6e80904ed71e173ff4f03d`,
verdict `acceptable_with_changes`, `capability_ok: true`, 2026-08-11T18:24Z.
