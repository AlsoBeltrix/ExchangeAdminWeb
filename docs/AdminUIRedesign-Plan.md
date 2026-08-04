# Admin UI Redesign -- Plan

Status: **In progress.** Owner approved the visual direction and D1 (app-wide) 2026-08-04
(mockups `docs/mockups/q1`, `q2`, `q3`). Slices 1-3 landed; slices 4-5 (the page rebuilds) are
the remaining work. Both bugs the redesign had to fix rather than restyle around -- B1
cross-domain picker, B2 unsaved-changes guard -- are done and shipped ahead of the rebuilds.
App version: `2.3.36` -> `2.4.0` (shared shell + theme + two admin pages).
Modules: `AdminSettings 1.0.3 -> 1.1.0`; every module inherits the shell without its own bump.
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`, `docs/AdminModuleSpec.md`.

## Why

Owner assessment 2026-08-04: "it does not look like a professional app, it looks like a vibe
coded toy... the important security group entry system is half-baked." Measured against the code,
each complaint is concrete:

- **8 separate Save buttons** across `AdminSettings.razor` (3) and `ModuleConfig.razor` (5), each
  easy to miss, with **no unsaved-changes guard anywhere in the application** -- verified: no
  `beforeunload`, `NavigationLock`, or `OnLocationChanging` in any component. Change a section,
  navigate away, the change is gone silently. On the page that controls who can reach every
  module.
- **`ModuleConfig.razor` is 1,233 lines** and renders every section stacked in one scrolling
  column. With 10-20 groups on a module it becomes unusable (owner).
- **Access is rendered as Bootstrap `badge` chips** in a wrapping row -- one line whose height
  changes per module, no alignment, no sort, no filter, no bulk action, no SID visible.
- **The group picker cannot see WINROOT** (see the Bugs section) -- a functional defect the
  redesign must not paper over.

The visual layer is stock Bootstrap 5.1 defaults (`card-header`/`card-body`, `badge`, `btn-sm`)
over 517 lines of `app.css`. The defaults are the "toy" look; restyling them was tried and
rejected seven times before the structure changed.

## Approved direction

Three mockups, kept as the reference; the rejected drafts were deleted to prevent
ambiguity about which is current.

| Mockup | Covers |
|---|---|
| `docs/mockups/q1-tabbed.html` | Per-module admin page (replaces `ModuleConfig.razor`) |
| `docs/mockups/q2-adminsettings.html` | Global admin page (replaces `AdminSettings.razor`) |
| `docs/mockups/q3-picker.html` | Cross-domain group picker |

Load-bearing decisions the mockups encode, with the reason each was chosen:

1. **Tabbed panes, each with its own scroll.** The page never grows. 2 groups or 200, the
   viewport is fixed. This is the direct answer to "everything on a monolithic scrolling page is
   out."
2. **Grants are table rows, not chips.** Domain / group / SID / member count / remove, aligned and
   sortable. Rejected explicitly and repeatedly in any chip form.
3. **Checkbox column + bulk action strip.** Removing 15 groups is select-then-one-action.
4. **One save bar per page**, naming which tab is dirty. Replaces the 8 buttons.
5. **NetBIOS domain naming throughout** (`ANALOG`, `WINROOT`) -- what Windows returns from
   `SecurityIdentifier.Translate` and what the forest's `CN=Partitions` crossRef records hold.
   Mixing NetBIOS with DNS (`ad.analog.com`) was a correctness defect in earlier drafts, not a
   style choice.
6. **No colour distinction for cross-domain grants.** `WINROOT\Enterprise Admins` on
   DhcpAuthorization is deliberate and correct; amber implied a fault and made ANALOG look like
   the default.
7. **OLED dark theme**: `#000000` canvas, silver `#d6dadf` text (full white smears at 13px),
   cyan `#3fd8e8` accent, surfaces lifted by value rather than by borders. Light mode retained --
   the app supports both and a redesign does not remove capability.

## Bugs this work must fix, not inherit

- **B1 (functional): the group picker cannot reach WINROOT.** `ADDirectorySearchService.Search`
  issues `Get-ADGroup` with no `-Server`, so it only ever queries the local domain. Verified:
  the same LDAP filter returns 0 matches locally and 1 against `winroot.analog.com`. **Fix:**
  query the forest global catalog (port 3268), which returns both domains in one search --
  verified to return `winroot.analog.com/Users/Enterprise Admins` and
  `ad.analog.com/AMER/NWD/Groups/ExchangeWebAdmins` together.
- **B2 (data integrity): no unsaved-changes guard.** Add one `NavigationLock` per admin page.

**Not a bug, recorded so it is not re-investigated:** `WINROOT\Enterprise Admins` failing to grant
DHCP access during dev testing was correct behavior -- the tester browsed as `mcoelho`, and the
group contains `mcoelho-2`. Cross-domain SIDs work: the token carries 10 WINROOT SIDs and
`IsInRole` matches them.

## Non-Goals

- **The ~20 module task pages** (Mailbox Permissions, Message Analysis, ...). They inherit the
  shell and theme but their internals are a separate problem -- forms and result tables, not
  admin config. Out of scope; they must keep working unchanged.
- **No authorization behavior change.** This is presentation. Who can reach what is decided by
  `GroupAuthorizationHandler` and is not touched.
- **No new dependency.** No Tailwind, no component library. Hand-written CSS over the existing
  Bootstrap, with Bootstrap's component classes progressively removed from the touched pages.
- **No change to the config store schema.** Same tables, same repositories.

## Owner decisions

### D1 -- Does the shell change apply app-wide immediately, or only to admin pages first?

**RULED: app-wide (owner, 2026-08-04).** The shell (nav, top bar, theme tokens) lands across
every page at once; page internals convert over time.

Consequences that follow, binding on the slices below:

- **All 22 pages are touched**, so all 22 need a smoke pass before prod (manual check 2). No page
  ships unverified on the grounds that only its chrome changed.
- **Bootstrap component classes cannot be deleted wholesale.** Untouched pages still render
  `card`, `badge`, `btn-sm`; the token layer must override Bootstrap's variables rather than
  remove its classes, or 20 pages break at once.
- **Slice 1 and 2 must be independently revertible.** They are the two that can regress every
  page simultaneously, so each lands as its own commit with nothing else in it.

### D2 -- Is the coverage gate extended to the new page logic?

The redesign moves real decision logic (dirty tracking, bulk selection, filtering) into pages
that no test can reach -- this repo has no bUnit harness, which is why `MessageTraceExportListing`
and `ProtectedPrincipalEntryValidator` exist as extracted services.

- **(a) Extract page logic to testable services**, as those two precedents did, and add the new
  files to the coverage floor.
- **(b) Accept the pages as untested**, as today.

**Recommended: (a).** Dirty-state tracking that silently fails is exactly the class of defect
that loses an admin's edit, and this repo has now been bitten twice by logic that no test could
reach (`MailboxPermissionService`, `CalendarPermissionService`, both at 0% before last week).

**Taken as (a) for the work already landed**, without waiting for a ruling, because B2 shipped
early: `AdminPageDirtyState` is an extracted service with 14 tests rather than page fields. If
the owner prefers (b) the service stays -- it costs nothing -- so this is not a decision the
landed work forecloses.

### D3 -- Add a bUnit harness?

Out of scope for this plan but it is the honest answer to D2's constraint. Flagged, not proposed.

## Design

### Layer 1: theme tokens (`wwwroot/app.css`)

One `:root` / `html.dark` token set: surfaces, text, lines, brand, state colours. Every value in
the mockups already comes from this set. Existing Bootstrap variables are overridden rather than
removed, so untouched pages keep working.

### Layer 2: shell (`MainLayout.razor`, `NavMenu.razor`)

Dense text nav grouped by the five real categories from `ModuleCatalog`, jump-to search, version
and identity in the footer, disabled-module marker. Top bar carries breadcrumb, title, and the
light/dark toggle.

### Layer 3: shared components

- `AdminTabs` -- tab strip with counts and dirty markers.
- `PrincipalTable` -- the grant/admin/principal table: sticky header, filter, checkbox column,
  bulk strip, per-row remove. Used in five places across the two pages.
- `GroupPickerDialog` -- the B1 fix. Global-catalog search, multi-select, keyboard-driven,
  already-added rows marked, SID always visible, typed text never accepted.
- `SaveBar` -- one per page; reads dirty state, names the offending tab.
- `UnsavedChangesGuard` -- wraps `NavigationLock`; fixes B2.

### Layer 4: the two pages

`ModuleConfig.razor` and `AdminSettings.razor` are rebuilt against the components above. This is
where the 1,233-line file gets cut down -- most of it is markup the components now own.

## Slices

1. **Theme tokens + light/dark.** No layout change; the existing pages simply restyle. Smallest
   possible first step, and it proves the token set on real screens.
   **DONE 2026-08-04.** Token block at the head of `wwwroot/app.css` driving both themes, with
   Bootstrap's `--bs-*` variables pointed at it -- the mechanism that lets untouched pages follow
   the theme without markup changes. 54 hardcoded dark-mode values collapsed into the token set;
   the ~62 that remain are the semantic table tints (success/danger/warning/info), which keep
   their own hues deliberately. One real fix landed with it: the focus ring was a two-ring style
   painting a white halo -- near-invisible on white, a bright smear on black -- now a single
   brand-tinted ring. **Not yet seen on a running instance:** dev is on `2.3.35` and this needs a
   deploy to observe.
2. **Shell** (nav + top bar). Every page inherits it. Smoke pass here, per D1.
   **DONE 2026-08-04.** CSS only -- no `.razor` markup touched, so the nav's authorization logic
   is provably unchanged. Rows 3rem -> ~1.9rem (all 22 modules now fit without scrolling),
   sidebar 250 -> 218px, brand and top row 3.5 -> 2.9rem. **Nav icons needed a real fix:** all 24
   SVGs are hardcoded `fill='white'` and vanish on a light sidebar, so each is now used as a CSS
   mask with `currentColor` -- one asset set, correct in both themes.

   **Corrected 2026-08-04 (this note previously claimed the sidebar read from the tokens; it did
   not).** Only the CSS-isolation copy in `Components/Layout/NavMenu.razor.css` was migrated.
   `wwwroot/app.css` carries a duplicate set of the same nav rules -- present deliberately, per
   its own comment, because isolation scoping has been unreliable on published IIS -- and that
   copy still had the 1.25rem painted icons, 2.25rem rails and `rgba(255,255,255,.22)` borders.
   `--sidebar-bg` itself was never a token at all: it was hardcoded `#2c3345` light / `#1a1d2e`
   dark, so the nav stayed a dark slate slab under light-theme grey label text. Result on dev:
   every module read as greyed out / disabled. Both copies now resolve to `--ui-nav-bg` and the
   token set; the dead `--sidebar-active` / `--sidebar-hover` / `--bs-body-bg: #f4f5f7` overrides
   and the light-mode top-row colour override are gone. **Rule: any nav or shell rule edited in
   `NavMenu.razor.css` must be edited in the `app.css` mirror in the same change.**
3. **`PrincipalTable` + `GroupPickerDialog`**, with the B1 global-catalog fix and its tests.
   Shipped behind the existing pages first -- the picker can be swapped in without the redesign.
   **B1 and B2 DONE 2026-08-04**, ahead of the table/dialog components:
   - **B1**: group search now targets the forest global catalog, so WINROOT groups are pickable.
     Two defects surfaced while verifying, both invisible to unit tests -- reading
     `GlobalCatalogs` off the returned PSObject yields empty, producing the server string
     `":3268"` which `Get-ADGroup` accepts while quietly serving the local domain; and the first
     live tests SKIPPED rather than failed when the fix was reverted. Both fixed; the tests now
     ask the forest its domain count independently of the code under test.
   - **B2**: `UnsavedChangesGuard` + `AdminPageDirtyState` (14 tests), wired into both admin
     pages. Every mutation on both pages marks its section dirty; every save clears only its own
     section. This lands without waiting for slices 4-5, so the data-loss hole closes now.
   - **A flaky test turned out to be a real product bug, found only because it was chased.** The
     live test passed in isolation and failed in the full suite, reporting "Forest has 2 domains
     but group search returned only: ad.analog.com". Two causes, both mine, both fixed:
     1. The probe term was "admins", which matches exactly 50 groups here -- the service's result
        cap. `Search` sorts by DisplayName then truncates, so whether the 3 WINROOT matches
        survived the cut was a race. Now probes "Enterprise Admins" (8 matches). **Rule for live
        directory tests here: never probe with a term that can saturate the result cap.**
     2. **The real defect:** `ResolveGlobalCatalog` set its "probed" flag BEFORE running the
        probe, so one transient `Get-ADForest` failure permanently pinned the service to
        local-domain-only searching -- silently restoring the very bug being fixed, for the life
        of the process. Now only a SUCCESS is cached; a failure retries on the next search.
     3. Residual nondeterminism after both fixes was the environment: several test classes each
        open their own runspace and contend for the directory, and the services are fail-soft, so
        a throttled call is indistinguishable from "no matches". All live-AD classes now share a
        `[Collection]` with parallelisation disabled (`LiveDirectoryCollection`).
     The first was a bad test. The second would have shipped and degraded the picker in
     production after any momentary directory hiccup, with no error surfaced to anyone. Worth
     recording that I attributed this flake to two wrong causes before finding the real one --
     the lesson is that a flaky live test deserves the same evidence standard as a failing one.
4. **`ModuleConfig.razor` rebuild** (q1) -- tabs, panes, save bar, guard.
   **DONE 2026-08-04.** Five stacked cards became five tabs, each pane owning its own scroll, so
   the page no longer grows with the group count -- the failure reported at 10-20 groups. Tabs
   carry a count (so an operator knows what is behind one without opening it) and a dot when that
   tab holds unsaved edits, which is the only way a BACKGROUND tab's state is visible. Five Save
   buttons became one bar naming the dirty section. **Access grants are table rows now** --
   domain / group / SID / remove, aligned -- replacing the wrapping chips rejected repeatedly;
   domain is its own column rather than a `DOMAIN\` prefix, because a prefix pushes every name to
   a different x-position. Module admins converted the same way. The page opens on the first tab
   the operator can actually see, since a global-admin-only tab would render blank for a module
   admin.
5. **`AdminSettings.razor` rebuild** (q2) -- modules, protected principals, logging.
   **DONE 2026-08-04.** Same structure: three tabs, one save bar, counts and dirty dots. The four
   protected-principal lists were initially left as fixed-height scroll boxes on the reasoning
   that they were already row-per-entry rather than chips. **Wrong, corrected same day on owner
   report:** row-per-entry is not row-*delineated*. They were borderless lines inside one bordered
   box, so at 4+ entries -- and with a wrapping multi-line DN in the mix -- nothing told the eye
   where one principal ended. All four now use the same `.adm-tbl` as the Access grants table
   (zebra, hover, per-row rule) inside a capped `.adm-scroll` wrapper, and the per-row control is
   the same text `Remove` as elsewhere rather than a red X button.
   **Not built: the diagnostics tab** from mockup q2. It shows facts (schema version, domain
   mappings, RSAT availability, "59 of 59 grants stored as SIDs") that nothing in the app
   currently exposes, so it is new capability rather than a redesign of existing capability, and
   OQ-2 (live probes vs cached status) is undecided. Recorded rather than silently dropped.
6. **Version bumps + docs.** **DONE** -- app `2.4.0`, AdminSettings module `1.1.0`.
7. **Post-deploy corrections** (owner observation on dev `2.4.0`, 2026-08-04). **DONE** -- app
   `2.4.1`. Two defects, both only visible on a running instance: the `app.css` nav mirror was
   never migrated to the tokens (slice 2 note above), and the protected-principal lists had no row
   delineation (slice 5 note above). Both are CSS/markup only; no service or authorization code
   touched.

**Method note, recorded because it nearly shipped a broken page.** The two rebuilds were done by
scripted line-range edits against 1,200-line files. Two of those edits silently deleted the block
they were meant to replace -- the entire Access grants table and the Module Admins table -- and
the project still BUILT clean, because Razor markup that is simply absent is not a compile error.
Caught only by diffing the finished files against pre-edit backups for a list of expected
symbols. Anyone doing this again: keep the backup, diff against it, and do not treat a green
build as evidence that markup survived.

Slices 1-3 are independently shippable and reversible. Slice 3 fixes a live bug on its own.

## Verification

Per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx`
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `tools/Test-CoverageFloor.ps1` (security-critical floor, currently 65.06)
- `git diff --check HEAD`, ASCII lint

Page markup is not unit-testable here, so the manual checks below are the only evidence the UI
works.

### Manual checks

**Checks 1-6 apply to what has landed (slices 1-3) and are runnable as soon as `2.4.0` is on
dev. Checks 7-8 need the page rebuilds and cannot be run yet.**

1. **Admin page still reachable.** `ANALOG\ExchangeWebAdmins` can open Admin Settings and a
   module config page. This is the finding sidf-1 lockout; it must be re-checked after any change
   near authorization.
2. Every one of the 22 modules still loads and its main action still works (D1 app-wide smoke
   pass). The shell changed on every page, so no page is exempt.
3. Light and dark both legible everywhere -- particularly the **nav icons**, which changed from
   painted white SVGs to CSS masks, and the **focus ring**, which changed from a two-ring style
   to a single brand-tinted ring.
4. Picker returns WINROOT groups; selecting one stores its SID. This is B1, the reported bug.
5. Typed text in the picker is refused.
6. **The guard**: edit any section on Admin Settings or a module config page, navigate away
   without saving, confirm the prompt names the section. Then save and confirm navigating away
   is silent. Also confirm saving ONE section does not clear the warning for another still-edited
   section -- that is the defect the eight separate Save buttons had.
7. *(needs slice 4)* Bulk-select 3 groups, remove, save, reload -- exactly those 3 gone.
8. *(needs slice 4)* A module with 15+ groups scrolls inside its pane; the page does not grow.

## Open questions

- **OQ-1.** `Security:AllowedGroups` / `AdminGroups` remain name-based (sidf-1 known gap), so the
  cross-domain ambiguity is closed for module access and still open for admin access. Not this
  plan's scope, but the diagnostics tab will now display it, which makes the gap visible.
- **OQ-2.** Whether the diagnostics tab's connectivity checks (EXO, Delinea, Graph, SMTP) should
  be live probes or last-known status. Live probes on page load add latency and can hang; cached
  status can be stale. Not decided.
