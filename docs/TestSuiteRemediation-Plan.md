# Test Suite Remediation -- Plan

Status: **Draft, awaiting owner decisions.** Raised by the owner 2026-08-03: "why do we need
1142 tests? how many of those are testing things that matter? how many have ever failed?" The
measurements below answer that; the answer is bad enough to justify a plan.
App version: no bump (test/CI only, ships no behavior).
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`.

## The measurements

Taken 2026-08-03. **Coverage had never been collected before today**, despite
`coverlet.collector` being a declared dependency of the test project the whole time.

| Measure | Value |
|---|---|
| Tests | 1,145 |
| Line coverage | **27.8%** (7,187 / 25,819) |
| Branch coverage | **18.6%** |
| Test code | 12,807 lines vs 22,286 lines of production C# (0.57:1) |
| CI failures caused by a test catching a regression | **0 of 11** |

The 11 red CI runs were all infrastructure: format-check warnings, the ASCII lint gate,
line-ending config, docs commits. Not one was "a test caught a bug."

### The distribution is the actual defect

Coverage is not low everywhere. It is bimodal -- near-100% on small pure helpers, near-0% on
the large services that do the real work:

```
100%  Authorization/SectionAccessGroupIdentity.cs      (43 tests, ~200 lines, written today)
100%  Authorization/SectionAccessSidMigrationPlanner.cs
100%  Services/ProtectedPrincipalEntryValidator.cs
 92%  Authorization/GroupAuthorizationHandler.cs
...
 16%  Services/ConferenceRoomService.cs                 986 lines uncovered
 11%  Services/MigrationService.cs                      665
  2%  Services/SelfServiceGroups/SelfServiceGroupService.cs  573
  0%  Services/MailboxPermissionService.cs              425
  0%  Services/CalendarPermissionService.cs             253
```

`MailboxPermissionService` and `CalendarPermissionService` -- the two services that grant and
revoke access to other people's mailboxes -- have **zero** covered lines.

### Why, mechanically

The uncovered services construct PowerShell runspaces inline and expose no seam:

```
Services/MailboxPermissionService.cs              6 runspace constructions, no interface
Services/CalendarPermissionService.cs             6                        no interface
Services/SelfServiceGroups/SelfServiceGroupService.cs  15                  no interface
```

Nothing can substitute the directory or Exchange, so nothing can be tested without a live
tenant. The pure helpers got tested **because they were easy**, not because they were where the
risk was. That is the whole explanation for both the test count and the coverage number.

### What this predicts, and what actually happened

A suite shaped like this cannot catch a wrong assumption -- only a wrong transcription of an
assumption already held. That prediction has been confirmed twice, both times by a reviewer
rather than a test:

- **ppv-1 (HIGH):** Exchange-fallback work closed an alias bypass for on-prem principals and
  reinstated it for cloud-only ones. Reported complete with "every guard proven non-vacuous."
- **sid-1 (HIGH, 2026-08-03):** the slice-3 commit message claimed an unmigrated store "fails
  CLOSED". It did not -- `IsInRole("Domain Users")` returns **true**, so Windows resolves names
  as well as SIDs. 43 passing tests all agreed with the wrong assumption. **One 10-second shell
  command disproved it.**

Adding tests of the current kind would not have caught either.

## Non-Goals

- **Not a mass deletion.** "Too many tests" is the symptom; misallocation is the disease.
  Deleting redundant tests without covering the 0% services makes the suite smaller and no more
  truthful.
- **No bUnit / UI harness.** `.razor` pages are 14,642 lines at ~0%. Introducing a UI test
  framework is its own decision and not this plan's.
- **No coverage-percentage target as a goal in itself.** A number to chase produces tests
  written to move the number. The gate below exists only to stop *regression*, not to be
  climbed.
- **No rewrite of the untested services' behavior.** Extracting a seam must not change what
  they do.

## Owner decisions

### D1 -- What is the gate?

The suite currently enforces nothing about coverage. Options:

- **(a) Ratchet on security-critical paths only.** CI fails if coverage drops on a named list
  (`Authorization/**`, `Services/ProtectedPrincipal*`, `Services/PermissionValidator.cs`,
  `Services/*Permission*`). Nothing else is gated.
- **(b) Global ratchet.** CI fails if total coverage drops below its current value.
- **(c) No gate.** Measure and report only.

**Recommended: (a).** It puts the enforcement exactly where a defect is an outage or a security
exposure, and it cannot be satisfied by testing easy code -- which is the failure mode that
produced the current shape. (b) is gameable by adding trivial tests anywhere; (c) leaves the
next regression to a reviewer's attention span.

### D2 -- How far to go on the untested services?

Extracting a testable seam from `MailboxPermissionService` and `CalendarPermissionService` is
real work in code that currently has no safety net, which is the uncomfortable part: the change
that makes them testable is itself untested while it happens.

- **(a) Seam + tests for the two permission services only** (grant/revoke: who can be targeted,
  protected-principal refusal, failure aggregation).
- **(b) Also `SelfServiceGroupService`** (573 uncovered lines, live AD writes).
- **(c) Defer all of it**; do the pruning and the gate now.

**Recommended: (a).** Those two decide access to other people's mail, and both sit at 0%.
`SelfServiceGroupService` is a close third but its decision core (`GroupMemberClassifier`,
`MembershipChangeReconciler`, `AdOwnershipFilter`) is already extracted and at 100% -- the
uncovered remainder is mostly the PowerShell plumbing.

### D3 -- Prune, and by how much?

Redundancy is real but smaller than the headline suggests. Candidates: parameterized cases
restating one rule (`SectionAccessGroupIdentityTests` has 43 for ~200 lines), and tests
asserting framework behavior rather than our decisions.

- **(a) Prune only demonstrable duplicates** -- delete a candidate, confirm no unique line
  goes uncovered, keep it deleted.
- **(b) Aggressive prune to a target count.**
- **(c) No pruning.**

**Recommended: (a).** Mechanically justified per deletion, and it cannot remove the only
coverage of a branch. A target count is the same mistake as a coverage target, pointed the
other way.

## Design (assuming the recommended options)

### 1. Make the measurement routine

Add coverage collection to `dotnet test` in CI, upload the report as an artifact, and record
the current numbers in `.agents/repo-guidance.md` so drift is visible. This is the cheap part
and is worth doing whatever is decided on D1-D3.

### 2. Ratchet the security-critical set (D1a)

A small script reads the Cobertura XML, filters to the named paths, and fails if their combined
line coverage falls below a committed floor. The floor starts at today's measured value and is
raised only deliberately, in a commit that says so.

### 3. Seam the two permission services (D2a)

Mirror the pattern this repo already uses successfully: `IOperatorDirectory` and
`ISectionAccessGroupDirectory` are one-member interfaces introduced precisely because
`ADDirectorySearchService` is sealed. Same move here -- extract the Exchange call behind a
narrow interface, leave behavior identical, then test the decision logic around it.

Order matters: extract the seam in one commit with no behavior change (provable by the existing
tests plus a build), then add tests in the next. Not both at once.

### 4. Prune with proof (D3a)

For each candidate: delete, re-run coverage, confirm no line or branch lost coverage. If
anything drops, restore it -- it was not redundant.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`, `dotnet test ExchangeAdminWeb.slnx`,
  `dotnet format ... --verify-no-changes`
- Coverage before/after each slice, since coverage IS the deliverable here.
- The seam extraction (slice 3) must be proven behavior-neutral: existing tests green with no
  edits to them. A test that needed changing means behavior moved.

## Open questions

- **OQ-1.** Whether the `.razor` layer (14,642 lines, ~0%) is accepted as permanently
  untestable or wants a harness. Out of scope here; it is the largest single uncovered surface
  and should not stay unexamined by default.
- **OQ-2.** Whether the non-vacuity probe ritual should stay mandatory for every guard. It
  caught one genuinely vacuous test today (the presence-marker case), so it is not theater --
  but it is also per-guard manual work, and mutation testing would do it mechanically.
