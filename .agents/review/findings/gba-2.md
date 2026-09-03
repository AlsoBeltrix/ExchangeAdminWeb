# gba-2: Plan omitted the base app version bump for new shared infrastructure

**Severity**: MEDIUM - a deployed build would carry new shared `Services/` code
under the old base version, breaking the versioning contract that deployed
validation reasons from.
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/gba-2.md`.

## Evidence

`docs/ProjectConstitution.md:112` - "Shared infrastructure changes bump the base app
version." `.agents/repo-guidance.md` (Versioning) - shared/app-wide changes bump the
csproj triple. `docs/GroupBulkActions-Plan.md` at `2e89f7a`: header "No base app
bump" and section 6 "Shared helpers" arguing module bumps only on the precedent of
helpers added to `GroupManagementService` and consumed by `SelfServiceGroupService`.

## Predicted observable failure

Dev shows base `2.17.0` with `Services/BulkIdentityList.cs` present; a later reader
comparing the sidebar version against the changelog cannot tell whether the shared
class is in the build. Same shape as csv-2 (2026-08-31).

## What

Plan defect: the drafted precedent (module-scoped fixes adding helpers to an existing
module service) does not fit a NEW shared class consumed by two modules, which is
exactly the shape the rule names.

## Approach

Plan revised: base app `2.17.0` -> `2.18.0` lands in S1 with the shared class (the
csv-2 rule: the bump ships with the change). Header, AC13, S1 and the section 6
paragraph updated; the withdrawn argument is recorded so it is not re-drafted.

## Files changed

- `docs/GroupBulkActions-Plan.md` - header Versions, AC13, section 6 "Shared
  helpers", S1, section 10.

## Guard proof

Docs-only; versioning is checked by reading `ExchangeAdminWeb.csproj` at S1.
`git diff --check` clean on the fold commit.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; same dispatch as gba-1)
Harness: codex-cli 0.152.1. Reviewed SHA `2e89f7a`, base `f1bec06`, capability_ok
true, verdict `acceptable_with_changes` (material change 2). Dispatched 2026-09-03;
envelope at `.agents/review/gba.result.json`.
