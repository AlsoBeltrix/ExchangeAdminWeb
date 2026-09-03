# gba-3: Admin bulk-add matcher could not represent group Name matches

**Severity**: MEDIUM - a group the directory returned because its `name` matched
the pasted line would be marked Not found or Ambiguous by the matcher, so the
resolution table lies about a valid input.
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/gba-3.md`.

## Evidence

`docs/GroupBulkActions-Plan.md` at `2e89f7a`, section 6: the `Candidate` record
carried `DistinguishedName, ObjectClass, DisplayName, UserPrincipalName,
SamAccountName, Mail, ObjectGuid` - no `Name`; the `Match` comment said "for group
candidates also name"; `BuildBatchFilter`'s group clause was
`(&(objectCategory=group)(|(name=a)(sAMAccountName=a)(mail=a)))`. The existing
typed path (`GroupManagementService.ResolveMemberForWrite:1012`) uses the same
`name=` clause, so groups whose sAMAccountName differs from their name are a real
population.

## Predicted observable failure

Paste "Exchange Web Admins" (name) for a group whose sAMAccountName is
"ExchangeWebAdmins": the query returns the group, `Match` compares UPN/mail/sAM
only, finds no equal attribute, and reports Not found. A faithful implementation
would pass every planned test.

## What

Plan defect: the filter and the matcher disagreed on the key set.

## Approach

Plan revised: `Name` added to `Candidate`, to the `-Properties` projection of both
batch queries, and to `Match` as a GROUP-ONLY key (a user's `name` is not an
identity a user is addressed by, and matching it would widen user resolution beyond
the single path's filter). Two new tests: `Match_GroupCandidate_ByNameOnly_Resolves`
and `Match_UserCandidate_NameDoesNotMatch`. AC10 updated.

## Files changed

- `docs/GroupBulkActions-Plan.md` - AC10, section 6 (`Candidate`, `Match` comment,
  both seam comments), section 8 (two tests), section 10.

## Guard proof

Docs-only. The planned tests carry their mutations (drop Name from the comparison ->
FAIL; compare Name for every class -> FAIL), biting at S1. `git diff --check` clean
on the fold commit.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; same dispatch as gba-1)
Harness: codex-cli 0.152.1. Reviewed SHA `2e89f7a`, base `f1bec06`, capability_ok
true, verdict `acceptable_with_changes` (material change 3). Dispatched 2026-09-03;
envelope at `.agents/review/gba.result.json`.
