# gm3-task4-slice1: in-list filter over loaded owned-groups list (GM-3 task 4, AC9)

**Severity**: n/a (slice-landing review, not a pre-recorded finding)
**Status**: Verified (accepted)
**Branch**: none (committed directly to master per repo policy)
**Commit**: `f17f3de` (slice), base `aba97df`

## Scope
Slice-landing review of `git diff aba97df..f17f3de` (GM-3 task 4: the in-list filter,
AC9). Pure client-side filtering over the already-loaded owned-groups list; no new
directory access, no new service call.

Files:
- `Services/SelfServiceGroups/ManageableGroupFilter.cs` (new) — pure, UI-free filter.
  `Matches(group, term)`: blank term matches all; otherwise a case-insensitive SUBSTRING
  test across Name, SamAccountName, Description. `Filter(groups, term)`: preserves input
  order, blank returns all, never null.
- `Components/Pages/SelfServiceGroups.razor` — filter input above the loaded-groups
  table; renders `ManageableGroupFilter.Filter(ownedGroups, filterTerm)`; `filterTerm`
  reset to "" at the start of each load; a filter-empty result shows a message distinct
  from the AC8 load-failure error.
- `ExchangeAdminWeb.Tests/ManageableGroupFilterTests.cs` (new) — 12 xUnit tests.

## Review mandate
Judge against plan `docs/SelfServiceGroupManagement-Plan.md` AC9 (line ~109): "Within
the loaded manageable list, a user can filter/find a group by a NON-PREFIX term (a word
in the middle of the name, or a description word). ... pure in-list client-side
filtering." Key checks:
- The match is a substring (non-prefix), not a prefix — a mid-name word and a
  description word both find the group.
- No directory round-trip: the filter reads only `ownedGroups` already in memory.
- A filtered-to-empty result must NOT read as the AC8 load-failure ("couldn't load your
  groups") nor as a silent drop — it is a distinct filter-result message over a loaded
  list.
- No new authorization surface: filtering cannot widen what was loaded (it only hides).

## Guard proof
The 12 filter tests are the new coverage. Non-vacuity: changing
`ManageableGroupFilter.Contains` from `Contains` (substring) to `StartsWith`
(prefix-only) fails 3 tests (mid-name-word, description-word/order, filter-order) —
exactly the AC9 non-prefix behavior; restoring passes 12/12. The razor markup has no
unit test (razor pages are not unit-tested in this repo); its wiring is
manual-validation-on-dev.

## Reviewer comments

Reviewer: codex-commercial (MCP transport), default model and effort. Dispatched
read-only, static code judgment only (no dotnet execution): two prior dispatches -- codex
cli and a first codex-commercial run -- both died at ~30 min, the cli on the sandbox's
blocked NuGet feed (`dotnet restore` fails, so an isolated snapshot cannot run the tests)
and the MCP run on the transport idle timeout during that same long silent test run. The
runtime guard proof is the coder's responsibility and was already done and documented (see
## Guard proof above); the reviewer's contribution here is independent static judgment of
the diff against AC9, which needs no build.

Verdict: **accepted**, `guard_confirmed=false` (static-only by design; runtime proof is the
coder's, above). reviewed_sha f17f3de, base_sha aba97df. No reopened/invalid findings.

Reviewer's confirmations:
- `ManageableGroupFilter.cs:24` -- blank term matches all; lines 27-30/46-48 do
  case-insensitive substring matching across Name, SamAccountName, Description; lines 37-43
  preserve source order.
- `SelfServiceGroups.razor:77` -- filtering reads only `ownedGroups` and renders only
  `visibleGroups`, so no directory round-trip and no authorization-widening path; lines
  79-83 render a distinct filtered-empty message; line 198 resets `filterTerm` before every
  load.
- `ManageableGroupFilterTests.cs:33` -- static confirmation: lines 33-53 assert non-prefix
  mid-name, description, and sAMAccountName substring matches that WOULD fail under a
  StartsWith regression; lines 80-97 assert blank/full-list and input-order behavior. No
  tests were executed in this review; runtime proof was done by the coder per the finding.

Orchestrator (coder) acceptance decision: envelope well-formed and SHA-consistent with the
reviewed slice; verdict accepted with no material issue; non-vacuity independently proven by
the coder-side runtime guard proof. Accepted.
