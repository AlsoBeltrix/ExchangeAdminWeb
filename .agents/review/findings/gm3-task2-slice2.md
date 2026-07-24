# gm3-task2-slice2: on-demand single-group search + SDDL-alias gate fix (GM-3 task 2, slice 2)

**Severity**: HIGH — caller-SID gate accepted SDDL aliases, letting a different principal reach `Get-ADUser -Identity` (AC6 caller-identity boundary defeated)
**Status**: Verified (accepted after one reopen→repair round)
**Branch**: none (committed directly to master per repo policy)
**Commit**: `d85c511` (slice) → `e748e32` (repair)

## Scope
Slice-landing review of `git diff 1a0cf58..d85c511` (on-demand single-group search +
shared-helper refactor), then repair-delta review of `git diff d85c511..e748e32`.

Files (slice): `AdOwnershipFilter.cs` (BuildGroupByNameFilter), `GroupSearchResult.cs`,
`SelfServiceGroupService.cs` (SearchManageableGroupAsync + extracted
PrepareAdRunspace/ResolveCallerDn/ProjectGroup), `AdOwnershipFilterTests.cs` (5 tests).
Files (repair): `SelfServiceGroupService.cs` (IsSecurityIdentifier),
`SelfServiceGroupServiceTests.cs` (6 tests).

## Finding (round 1, reopened)
`IsSecurityIdentifier` used bare `new SecurityIdentifier(value)` parse-success. SDDL
2-letter aliases parse successfully and resolve to real principals — verified empirically:
`BA`→S-1-5-32-544 (BUILTIN\Administrators), `DA`→Domain Admins, `SY`→S-1-5-18,
`WD`→S-1-1-0. The alias then reached bound `Get-ADUser -Identity` as a DIFFERENT
principal than the authenticated caller, defeating the AC6 caller-identity boundary.
Shared gate → affected both GetOwnedGroupsAsync and SearchManageableGroupAsync.

## Repair (round 2, accepted)
`e748e32`: require the input to equal the canonical SID string it parsed to
(`sid.Value == value`). A genuine SID round-trips; an alias ("BA"→"S-1-5-32-544") and any
padded form do not. 5 new non-vacuous regression tests (BA/DA/SY/WD/ba + padded SID).

## Guard proof
- Slice: `AdOwnershipFilterTests` — breaking BuildGroupByNameFilter escaping fails 2 tests; restore passes 15.
- Repair: `SelfServiceGroupServiceTests` — reverting the round-trip check to bare parse
  fails 5 alias/padded tests; restore passes 17/17. Confirmed independently in the
  reviewer's own worktree (guard_confirmed true both rounds).

## Reviewer comments
`Reviewer: codex / gpt-5.5-dzs / xhigh / standard` (round 1); on reopen escalates one
tier by playbook, but owner ruled "use codex at its default, do not specify model or
effort" (2026-07-24), so the repair round redispatched at the same codex default. codex-cli
0.145.0, transport cli.

Round 1 (slice) — reviewed_sha d85c511, base 1a0cf58, guard_confirmed true, **reopened**,
2026-07-24T~18:05Z. Comment: SDDL-alias alternate-identity path (see Finding above).

Round 2 (repair) — reviewed_sha e748e32, base d85c511, guard_confirmed true, **accepted**,
2026-07-24T~18:35Z. Comments:
- SelfServiceGroupService.cs:437 — round-trips parsed SID.Value against supplied value,
  closing SDDL-alias path while preserving canonical SID strings.
- SelfServiceGroupService.cs:70 / :144 — both entry points gate callerSid through the
  shared validator before AD lookup.
- SelfServiceGroupServiceTests.cs:39 — guard tests cover alias + padded rejection and
  valid acceptance; bare parse-success failed alias cases, restored head passed 17/17.

Note: the first repair dispatch hit the harness's 10-minute foreground cap mid-guard-proof
(two full `dotnet test` runs at ~2.5 min each); redispatched uncapped in background — the
prior run produced no verdict envelope, so per the playbook the dispatch (not the review)
had failed and was re-run cleanly.
