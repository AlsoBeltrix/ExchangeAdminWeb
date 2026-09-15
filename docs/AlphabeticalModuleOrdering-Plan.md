# Alphabetical Module Ordering Plan

Status: Approved 2026-09-15 - ready to implement
Base app version at drafting: 2.20.2
Repository base: `7d4b976`

Review: `openreview codex (@azure-openai-eus2-global/gpt-5.5-dzs @ xhigh, frontier
graded fallback - the harness's frontier and standard pairs are identical, so this
carries standard-tier weight) over 7d4b976..c9688a0`: **Acceptable with changes**.
Two required changes, both verified against the code and both now folded in: the
missed `GetConfigurablePolicyAliases()` use of `SortOrder`, and the stale
`SortOrder = 800,` fixture in `tests/ps/ValidatorChecks.Tests.ps1`. One further
suggestion (make the package validator reject `SortOrder`) is recorded under
Out of scope.

## Goal

Modules appear in alphabetical order by display name everywhere the application
lists them, and the hand-maintained `SortOrder` integer disappears from the module
contract. Today every new module forces a placement decision ("which number slots
this between MfaReset and NamedLocations?") that carries no information once the
list is alphabetical.

The owner ruled 2026-09-15: modules sort alphabetically within each nav section;
the section headings keep their current order (Exchange, Directory & Groups,
Identity & Access, Infrastructure).

## Verified current state

All line references verified at `7d4b976`.

- `Modules/AdminModuleDescriptor.cs:11` declares `public required int SortOrder { get; init; }`.
- All 28 descriptors in `Modules/ModuleCatalog.cs` set it.
- `Modules/ModuleCatalog.cs:32`:
  `public IReadOnlyList<AdminModuleDescriptor> GetOrdered() => _modules.OrderBy(m => m.SortOrder).ToList();`
- `Modules/ModuleCatalog.cs:40`: `GetConfigurablePolicyAliases()` independently
  orders by `m.SortOrder`. It does not call `GetOrdered()`, so it needs its own
  replacement order or the build breaks.
- `GetOrdered()` consumers: `Components/Layout/NavMenu.razor:40,160,186,190,195`;
  `Components/Pages/Home.razor:51,67`; `Components/Pages/AdminSettings.razor:81`;
  `Components/Pages/AdminEventLog.razor:1275`;
  `Components/Pages/ExchangeOnlineConfig.razor:162`;
  `ExchangeAdminWeb.Tests/UsageTrackerWiringTests.cs:152`.
- `tests/ps/ValidatorChecks.Tests.ps1:44` builds a fixture descriptor snippet that
  includes `SortOrder = 800,`. `tools/validate-module-package.ps1` never reads
  `SortOrder`, so the fixture is stale text rather than a validated field, but it
  would still teach the next module author to write a field that no longer exists.
- `SortOrder` has exactly three jobs:
  1. Position within a rendered list (all `GetOrdered()` consumers above).
  2. Position of the policy-alias list from `GetConfigurablePolicyAliases()`.
     Its one caller, `Components/Pages/ModuleConfig.razor:1201`, seeds a
     dictionary, and `Catalog_GetConfigurablePolicyAliases_MatchesExpected`
     (`ModuleCatalogTests.cs:218`) asserts membership with `Assert.Contains`, so
     this order is not currently observable. It still has to compile.
  3. A 900 threshold that splits primary nav from the Administration block, at
     `NavMenu.razor:161` (`m.SortOrder < 900`) and `NavMenu.razor:195`
     (`m.SortOrder >= 900`).

### The threshold is redundant with Category

Enumerating the catalog: the only descriptors with `SortOrder >= 900` are
`AdminSettings` (900), `AdminEventLog` (910) and `AdminBulkJobs` (920). Those are
also exactly the descriptors with `Category = "Administration"`. No other module
carries that category, and no Administration-category module sits below 900.
`NavMenu.razor:150-158` `PrimaryCategoryOrder` independently lists exactly the four
non-Administration categories, so the category is already the operative concept in
that file; the integer threshold restates it.

### Ordering cannot affect authorization

`Services/SectionAccessService.cs:64` `GetGroupsForSection(string section)` is keyed
on `MainPermission.PolicyAlias` / granular `PolicyAlias`, built in
`BuildFailClosedSet()` (`SectionAccessService.cs:45-61`). Neither `Category` nor
`SortOrder` reaches any authorization path. This change touches no policy alias, so
the fail-closed set and every stored `section_access` row are unaffected. This is a
stated non-goal, asserted by a test rather than assumed.

## Scope

### In scope

1. Delete `SortOrder` from `AdminModuleDescriptor`.
2. Delete the `SortOrder = N,` line from all 28 descriptors in `ModuleCatalog.cs`.
3. Redefine `GetOrdered()` to order by `DisplayName` using
   `StringComparer.OrdinalIgnoreCase`. The method name stays: every consumer wants
   "the catalog in display order", and that is still what it returns.
4. Replace the two threshold checks in `NavMenu.razor` with a category test against
   a single named constant rather than a repeated string literal.
5. Remove `.ThenBy(m => m.SortOrder)` at `NavMenu.razor:163`, replacing it with an
   explicit `.ThenBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)` so the
   within-section order does not depend on `OrderBy` stability.
6. Reorder `GetConfigurablePolicyAliases()` (`ModuleCatalog.cs:40`) by
   `DisplayName`, case-insensitive, keeping each module's main alias immediately
   before its own granular aliases. Same ordering rule as `GetOrdered()`, so the
   two stay consistent.
7. Update the stale fixture snippet in `tests/ps/ValidatorChecks.Tests.ps1:44` to
   drop the `SortOrder = 800,` line.
8. Tests (see Verification).
9. Update the two binding contract docs: `docs/AdminModuleSpec.md:23,242` and
   `docs/AdminModuleDeveloperGuide.md:214,273`.
10. Bump `<VersionPrefix>`, `<AssemblyVersion>` and `<FileVersion>` in
   `ExchangeAdminWeb.csproj` from 2.20.2 to 2.21.0. Shared UI and a module-contract
   change, so the base app version moves; no module `Version` changes, because no
   module's own behavior changes.

### Out of scope

- Section heading order. It stays Exchange, Directory & Groups, Identity & Access,
  Infrastructure, by owner ruling.
- The Administration block's internal structure. It renders as three separate
  blocks (system pages, the Module Config tree, admin-adjacent modules) and is not
  a single sorted list; reworking it was explicitly not requested.
- Historical `docs/*-Plan.md` files that quote specific `SortOrder` values. Those
  are implementation history, not current contract, and `.agents/playbooks/drift.md`
  governs whether history is rewritten. Leave them.
- `Components/Pages/ServiceHealth.razor`'s local `sortOrder` variable. Unrelated
  identifier for the service-list sort control.
- Teaching `tools/validate-module-package.ps1` to reject a submitted snippet that
  still carries `SortOrder`. Raised in review; declined for this work stream. The
  validator has never read the field, so nothing regresses by leaving it alone,
  and adding a new rejection rule is new validator behavior rather than part of
  removing the field. Reconsider if a module package arrives carrying it.

## Settled ruling

Owner ruling 2026-09-15: the nav split reads the category, not a new descriptor
boolean. Implement it as the named constant `ModuleCategories.Administration`,
guarded by the catalog test that pins the Administration set to exactly
`AdminSettings`, `AdminEventLog` and `AdminBulkJobs`. This adds no field to the
contract while the work is removing one, and the test closes the typo risk that a
raw string literal would carry. Do not introduce `IsAdministration`.

Owner instruction, same ruling: whatever the final shape is, the module creation
path must teach it. `docs/AdminModuleDeveloperGuide.md` and
`docs/AdminModuleSpec.md` must describe the descriptor exactly as it ends up -
no `SortOrder`, and the category named from `ModuleCategories` - so the next
module author writes a descriptor that compiles and lands in the right section.
`.claude/commands/new-module-command.md` needs no edit: it delegates to those two
documents and never names the field itself (verified 2026-09-15).

## Implementation steps

Each step is a separate commit, per `.agents/repo-guidance.md` ("one finding or fix
per commit").

1. **Introduce the category constant.** Add `ModuleCategories` with the five
   category names as constants in `Modules/`. Update `ModuleCatalog.cs` descriptors
   and `NavMenu.razor`'s `PrimaryCategoryOrder` to use them. No behavior change;
   the existing suite must stay green.
2. **Switch the nav split to the category.** Replace `m.SortOrder < 900` and
   `m.SortOrder >= 900` at `NavMenu.razor:161,195` with the category test. Add the
   catalog test asserting the Administration set is exactly
   `AdminSettings`, `AdminEventLog`, `AdminBulkJobs`. Still no visible change,
   because the two conditions select identical sets today.
3. **Make ordering alphabetical.** Redefine `GetOrdered()`; replace
   `.ThenBy(m => m.SortOrder)` with the display-name comparison; reorder
   `GetConfigurablePolicyAliases()` the same way. Replace
   `Catalog_GetOrdered_ReturnsSortedBySortOrder`
   (`ExchangeAdminWeb.Tests/ModuleCatalogTests.cs:337`) with an alphabetical
   assertion. This is the step with visible effect.
4. **Delete the field.** Remove it from `AdminModuleDescriptor` and from all 28
   descriptors, and from the fixture snippet in
   `tests/ps/ValidatorChecks.Tests.ps1:44`. The compiler enumerates any missed C#
   reference, since the property is `required`; the PowerShell fixture is a
   here-string and the compiler cannot see it, so it is listed explicitly.
5. **Update the contract docs and bump the version.** In
   `docs/AdminModuleDeveloperGuide.md` (lines 214, 273) and
   `docs/AdminModuleSpec.md` (lines 23, 242): remove `SortOrder` from the
   descriptor listing and the worked example, replace the "sorted by `SortOrder`"
   sentence with the alphabetical rule, and state that `Category` must be one of
   the `ModuleCategories` constants and that `Administration` is what places a
   module in the bottom nav block. Read both documents for any other sentence
   that implies an author chooses a position; the goal is that someone following
   the guide end to end writes a descriptor that compiles and lands correctly.
   Then bump the app version.

Steps 1, 2 and 4 are behavior-preserving; only step 3 changes what an operator sees.
Splitting them this way keeps the visible change isolated in one reviewable commit.

## Verification

Repo verification entry point (`.agents/repo-guidance.md`), run after each commit:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

Step 4 touches a `.ps1` file, so that commit also runs
`Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps`.

### Tests this change owns

All in `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs` unless noted.

1. `GetOrdered()` returns descriptors in case-insensitive display-name order.
   Replaces the existing `SortOrder` assertion at line 337.
2. The Administration category contains exactly `AdminSettings`, `AdminEventLog`
   and `AdminBulkJobs`. This is the guard that the nav split did not change meaning
   when it stopped reading an integer.
3. Every descriptor's `Category` is one of the five `ModuleCategories` constants.
   Catches a typo that would silently drop a module out of the primary nav.
4. No policy alias changed: the set of `MainPermission.PolicyAlias` plus granular
   aliases is unchanged by this work. Protects the non-goal in "Ordering cannot
   affect authorization".

Prove non-vacuity per repo guidance: for each new test, revert the corresponding
production change, confirm the test fails, restore, confirm green.

### Not covered by automation

`Components/Layout/NavMenu.razor` and `Components/Pages/Home.razor` are Blazor
components and this solution has no bUnit or equivalent component-test harness
(`ExchangeAdminWeb.Tests.csproj` references only xunit.v3, NSubstitute,
Microsoft.NET.Test.Sdk and coverlet). The catalog tests above cover the ordering
source and the category split, which is where the logic lives after this change,
but the rendered markup itself is not asserted.

Manual acceptance, owner or operator, after a dev deploy:

- [ ] Sidebar: within each of the four sections, modules read A-Z.
- [ ] Sidebar: section headings still read Exchange, Directory & Groups,
      Identity & Access, Infrastructure, in that order.
- [ ] Sidebar: Administration still shows its system pages, the Module Config
      tree and any admin-adjacent modules, with nothing missing or duplicated.
- [ ] Module Config tree: entries read A-Z.
- [ ] Home page: module tiles read A-Z.
- [ ] Admin Settings module list reads A-Z.
- [ ] A non-global-admin account with module-admin rights still sees only its own
      modules in the Module Config tree, now A-Z.
- [ ] Spot-check that a module an account could open before is still openable, and
      one it could not is still denied. Section access is expected to be untouched.

## Risks

- **A module silently leaves the primary nav.** Only if its `Category` stops
  matching a `PrimaryCategoryOrder` entry. Test 3 closes this.
- **Ordering differs from the operator's expectation for names with punctuation or
  digits.** `OrdinalIgnoreCase` sorts digits before letters and does not apply
  culture rules. Accepted deliberately: it is stable across hosts and locales,
  which a culture-sensitive comparison is not. No current display name begins with
  a digit or punctuation mark.
- **Merge conflicts against in-flight module work.** Step 4 touches all 28
  descriptors. `.agents/state.md` records no module code in flight, so the window
  is clean; land this before starting new module work.

## Rollback

Each step is a self-contained commit; reverting step 3 alone restores the previous
visible order while keeping the constant and category cleanup. Reverting all five
returns to `7d4b976`.
