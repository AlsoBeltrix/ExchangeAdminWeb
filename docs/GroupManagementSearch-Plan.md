# GroupManagement Search & Layout (GM-1) — Plan

Status: Implemented (2026-06-29)
App version at draft: 2.3.27 (unchanged — module-scoped change)
Module: `GroupManagement` (Version `2.0.3` → `2.1.0`)
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.

## Problem / Goal

Two distinct, owner-reported problems on the on-prem Group Management page:

**A. Search quality.** `GroupManagementService.SearchGroupsAsync`
(`Services/GroupManagementService.cs:70–121`) issues a pure substring filter
(`Name -like '*term*' -or SamAccountName -like '*term*' -or Mail -like '*term*'`, line 94)
with `ResultSetSize = 25` (line 97) and **no ordering**. Consequences:
- An exact match (`IAM`) ranks no higher than an incidental substring hit (`DiagramTeam`).
- Results come back in AD's arbitrary order and are shown as-is — the page does not sort
  (`Components/Pages/GroupManagement.razor:54`).
- Worse: with >25 matches, AD returns *some* 25 in no defined order, so the exact match
  **may not be in the returned set at all** — it can be silently absent, not just buried.

**B. Layout.** The results table renders full-width with no height cap, *above* the
management card (`GroupManagement.razor:47–68` then `:70–154`). Selecting a group to manage
leaves up to 25 result rows stacked on top, pushing the Load Members / ticket / Add-Remove
controls off the bottom of the screen. Confusing and messy.

Goal: exact match always first, all other matches ranked beneath it and scrollable; and a
layout where controls stay on top and results sit below in a scrollable frame so they never
displace the controls.

## Owner decisions (this session)

1. **Search ranking** — exact match comes first, always; after that, all other matches
   ranked beneath it, to be scrolled through. (Substring matching is retained so mid-name
   matches still appear — they simply rank below exact and prefix matches.)
2. **Layout order** — results go **below** the controls (not above).
3. **Results container** — results render in a **scrollable frame** (fixed max height,
   internal scrollbar), same pattern as the existing member list
   (`GroupManagement.razor:130`).

## Non-Goals

- No change to membership read/add/remove behavior or the protected-principal gate.
- No change to the M365 Group Management page (separate module, already two-column).
- GM-3 (self-service) is out of scope.
- No new module; this is a module-scoped behavior + UI change to `GroupManagement`.

## Scope of change

### A1. Search — `Services/GroupManagementService.cs`

- **Widen the fetch so the exact match can never be excluded by the cap.** Raise the AD
  query bound (e.g. `ResultSetSize = 200`) so ranking happens over a broad set; the page
  still shows a capped, scrollable list. (Exact-first promotion already guarantees the
  exact match survives any display cap; the wider fetch guarantees it's *fetched* in the
  first place when many groups share the substring.)
- **Add ranking.** Extract a pure, static, unit-testable method:
  `internal static List<GroupInfo> RankGroups(IEnumerable<GroupInfo> results, string term)`.
  Ordering tiers (case-insensitive), exact first then everything else beneath:
  1. Exact match on `Name` or `SamAccountName` (equals term).
  2. Starts-with on `Name` or `SamAccountName`.
  3. Contains (the remaining substring matches).
  Within each tier, order alphabetically by `Name`. `SearchGroupsAsync` calls
  `RankGroups(results, searchTerm.Trim())` before returning.
- **Display cap after ranking.** Render up to **100** ranked results in the scrollable
  frame (owner decision). Fetch wider than that from AD so ranking sees more than it shows
  (proposal: `ResultSetSize = 200`, take top 100 after `RankGroups`). Exact-first ordering
  means the useful hits are always at the top of the 100.
- LDAP-injection note: `searchTerm` is already escaped for the PowerShell filter
  (`.Replace("'", "''")`, line 91). `RankGroups` operates on returned objects in memory; no
  new injection surface. Preserve the existing escaping.

### B1. Layout — `Components/Pages/GroupManagement.razor`

Reorder the page body so render order is:
1. Search box (unchanged, lines 32–45).
2. The management card (`selectedGroup != null`, currently lines 70–154) — moved **above**
   the results block.
3. The search results table (currently lines 47–68) — moved **below**, wrapped in a
   scrollable frame: `<div style="max-height: …; overflow-y: auto;">` around the table,
   mirroring the member-list frame at line 130.

No change to the per-row Manage button, SelectGroup, or the management card's internals.
Results stay visible (scrollable) below while a group is managed above.

### A2/B2. Module version — `Modules/ModuleCatalog.cs`

Bump `GroupManagement` `Version` `2.0.3` → `2.1.0` (search ranking + layout = a
module-scoped behavior change). App `<VersionPrefix>` unchanged (no shared/app-wide code).
Confirm against `docs/ProjectConstitution.md` §Deployment And Versioning at implementation.

### A3. Tests — `ExchangeAdminWeb.Tests/`

New `GroupManagementSearchRankingTests` exercising the pure `RankGroups`:
- Exact match sorts first even when AD/input order puts it last.
- Starts-with ranks above contains.
- Within a tier, alphabetical by Name.
- Exact match on `SamAccountName` (not just Name) promotes to tier 1.
- Case-insensitivity.
- Stable/typical: a realistic "IAM" set returns `IAM` first, then `IAM-*`, then `*IAM*`.
- Each new test proven **non-vacuous** (revert ranking to identity order, see failures,
  restore) per AGENTS.md Verification.

`SearchGroupsAsync` itself (live runspace) is not unit-tested — same constraint as the rest
of this service; the logic-bearing part is `RankGroups`, which is fully covered. The wider
`ResultSetSize` and the page reorder are validated manually on dev deploy.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release` then `dotnet test ExchangeAdminWeb.slnx`.
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore` and
  `git diff --check HEAD`.
- New ranking tests proven non-vacuous.
- Manual (deferred to dev deploy, state clearly if not run): search a term with a known
  exact group among many fuzzy matches → exact appears first; confirm controls stay on top
  and results scroll below without displacing them.

## Resolved decisions (owner, this session)

1. **Search ranking** — exact first always, then prefix, then contains; alphabetical within
   each tier.
2. **Layout** — controls on top, results below in a scrollable frame.
3. **Shown count** — render up to **100** ranked results in the scrollable frame; fetch
   ~200 from AD so ranking sees more than it shows.

## Commit slices (one per landed slice, per AGENTS.md Git Safety)

1. Service: `RankGroups` + wider fetch + wiring in `SearchGroupsAsync` + ranking tests.
2. Page: reorder (controls above, results below) + scrollable results frame.
3. Module version bump + docs (this plan → Implemented, state.md GM-1 done, decisions if
   any durable rule emerged).
