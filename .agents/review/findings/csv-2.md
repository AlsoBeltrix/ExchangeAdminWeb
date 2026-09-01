# csv-2: Base app bump deferred past the slice introducing shared infrastructure

**Severity**: LOW - a deploy cut between S1 and S7 would carry new shared code
while the sidebar and assembly report the prior base version; wrong paperwork,
not wrong behavior.
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/csv-2.md`.

## Evidence

`docs/ModuleCsvExport-Plan.md` (pre-fix, at `533c1fe`), S1: "No version bump yet
(nothing ships a behavior change until a consumer exists; the base bump lands in
S7)". `docs/ProjectConstitution.md` Deployment And Versioning: "Shared
infrastructure changes bump the base app version" - the rule binds to the change,
not to observable behavior, and S1's `Services/CsvExport.cs` is the shared change.

## Predicted observable failure

A deploy after S1 (or after any S2-S6 consumer slice) ships a binary containing
new shared infrastructure that is indistinguishable by version from the previous
build - the same cannot-tell-builds-apart cost `.agents/state.md` records for the
2026-08-13 Migration deploy.

## What

Plan defect: version paperwork detached from the slice that ships the change,
against the Constitution's letter and the repo's one-slice-one-paperwork motion.

## Approach

Plan revised: the csproj triple bump moves into S1 with the helper; S7 becomes
README-only; AC6 states the bump ships with the change.

## Files changed

- `docs/ModuleCsvExport-Plan.md` - AC6, S1, S7, Review log.

## Guard proof

Docs-only; `git diff --check` clean on the fold commit. The bump itself is an
implementation-time check (AC6), as in the sibling plans.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; owner-named dispatch: "have codex review all the plans")
Harness: codex-cli 0.150.1. Reviewed SHA `533c1fe3...`, base `a9b0ebc3...`,
capability_ok true, verdict `acceptable_with_changes` (openreview; also material
change 3). Dispatched 2026-08-31; envelope at `.agents/review/plans2.result.json`.
