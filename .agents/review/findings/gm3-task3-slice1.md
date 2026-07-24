# gm3-task3-slice1: SelfServiceGroups module descriptor + page skeleton (GM-3 task 3)

**Severity**: n/a (slice-landing review, not a pre-recorded finding)
**Status**: Verified (accepted, no material issue)
**Branch**: none (committed directly to master per repo policy)
**Commit**: `ba22cf5` (slice), base `883b311`

## Scope
Slice-landing review of `git diff 883b311..ba22cf5` (GM-3 task 3: module descriptor +
DI registration + Blazor page skeleton). This is UI wiring over the already-reviewed
task-1/task-2 service; it adds no new service logic.

Files:
- `Modules/ModuleCatalog.cs` — new SelfServiceGroups descriptor (Access-only FailClosed
  policy, Directory & Groups, SortOrder 165, EnabledByDefault=false, v1.0.0, on-prem
  DelineaSecretId config field).
- `Program.cs` — `AddScoped<SelfServiceGroupService>()` DI registration.
- `Components/Pages/SelfServiceGroups.razor` — new page: `[Authorize(Policy=
  "SelfServiceGroups")]`, OnInitializedAsync re-check, ModuleVersion, load button
  (nothing loads on open, AC2) with required spinner + disabled state (plan 6.4),
  owned-groups table, on-demand single-group search box (6.3). Caller SID from the
  PrimarySid claim; owner is always the bound Windows principal (AC6).
- `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs` — count-guards updated (22->23
  modules, 31->32 policy aliases; asserts the new alias is present).

## Review mandate
Judge the slice against plan `docs/SelfServiceGroupManagement-Plan.md` sections 6.1,
6.3, 6.4 and task 3 (plan section 7). Key invariants to check:
- AC2: nothing loads on page open; explicit load button only.
- AC6: the self-service owner is always the authenticated Windows principal; no
  submitted id can widen it (SID is read from the principal, not user input).
- AC8: a load failure never renders "no groups found" — it shows a clear error.
- Plan 6.4: loading spinner + disabled load button while the per-group ACL read runs.
- Descriptor correctness per 6.1 (Access-only FailClosed, no granular perms, correct
  category/sort/version, on-prem DelineaSecretId).
- Adding a module must NOT bump the base app version (decision 2026-07-21).

## Guard proof
The only new tests in this slice are the ModuleCatalog count-guards. Guard proof:
reverting the descriptor addition (removing the SelfServiceGroups block) drops the
module count back to 22 and the alias count back to 31, failing both updated asserts;
restoring passes. The page/DI have no unit tests (razor pages are not unit-tested in
this repo; the service already carries task-1/2 coverage).

## Reviewer comments
`Reviewer: codex / gpt-5.5-dzs / xhigh / standard` (codex default per owner ruling
2026-07-24 "use codex at its default, do not specify model or effort"). codex-cli
0.145.0, transport cli. Thread 019f95d9-5a20-7fd2-ba9e-5978a9449e71.

Round 1 (slice) — reviewed_sha ba22cf5, base_sha 883b311, guard_confirmed true,
**accepted**, no comments, 2026-07-24T~20:45Z. The reviewer grounded against the plan
mandates, confirmed the diff is limited to descriptor + DI + page + catalog tests,
checked AC6 (the page passes no caller-controlled owner value into the already-landed
service), confirmed `ExchangeAdminWeb.csproj` is unchanged (base app version not bumped,
decision 2026-07-21), and ran the guard proof in a detached `git archive` temp snapshot
(worktree/clone are sandbox-blocked on this Windows host). Verdict envelope: reviewed_sha
and base_sha both matched the dispatched SHAs; orchestrator computed acceptance.

Note: the first dispatch (a shell `&` background job) was reaped mid-`dotnet test` and
produced no verdict envelope, so per the playbook the dispatch (not the review) had
failed; redispatched via the harness's own background mechanism (uncapped) with a
host-note pre-supplying the `git archive` snapshot recipe, which completed cleanly.

## Coder-side guard proof (independent)
Confirmed by the coder in the main checkout, NOT relying on the reviewer's word: removing
the SelfServiceGroups descriptor block from Modules/ModuleCatalog.cs and running
`dotnet test ExchangeAdminWeb.slnx --filter "FullyQualifiedName~ModuleCatalogTests"`
fails 3 tests (module count 23->22, alias count 32->31, and the new alias-presence
assert; "Expected: 23 Actual: 22"). Restoring the descriptor passes 24/24. `git diff
--stat Modules/ModuleCatalog.cs` empty after restore, confirming byte-identical to the
committed slice. The count-guards are non-vacuous.
