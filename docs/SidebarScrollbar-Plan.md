# Sidebar -- The Scrollbar That Never Leaves

Status: **Draft, awaiting owner approval.** No code written. One open question (Q1) is a
genuine owner gate and blocks part of S1; the scrollbar fix itself does not depend on it.

## The reported defect, and the evidence

Owner, 2026-09-24: *"left pane scroll bar never leaves, no matter how tall the window
is."* Screenshot of dev (`ashbiamweb1.ad.analog.com/ExchangeAdminWeb`), Risky Users page,
OLED Black theme, a window roughly 1900 CSS pixels tall.

What the screenshot shows, and each of these is a separate fact:

1. A vertical scrollbar runs the full height of the sidebar, with both arrow buttons
   rendered.
2. The nav content ends at the version footer, with roughly 450 pixels of empty sidebar
   below it. **The content is visibly shorter than the container, and it is scrolling
   anyway.**
3. The version footer (`v2.20.2 | Issues?`) sits directly beneath the Administration
   block rather than at the bottom of the sidebar.

Point 2 is the one that identifies the bug. An `overflow-y: auto` container whose content
is shorter than itself does not scroll. Something is making the scroll height exceed the
client height by a small, constant amount -- constant being exactly why a taller window
never helps.

## Root cause

Three rules interact, all in `Components/Layout/NavMenu.razor.css`:

```css
/* :208-219, inside @media (min-width: 641px) */
.nav-scrollable {
    height: calc(100vh - 2.9rem);
    overflow-y: auto;
}
.nav-scrollable nav {
    min-height: 100%;
}

/* :222-230 */
.nav-category {
    margin-top: 0.7rem;
    ...
}
```

And one fact about the markup (`Components/Layout/NavMenu.razor:21-24`): the first child
of `<nav>` is `<div class="nav-category px-3">`, the `EXCHANGE` heading.

The chain:

1. `nav` has **no padding-top and no border-top**. Its first child carries
   `margin-top: 0.7rem`. With nothing between them, that margin **collapses out of the
   child and becomes the `nav` element's own top margin** -- standard CSS margin
   collapsing.
2. `.nav-scrollable` has `overflow-y: auto`, which makes it a **block formatting
   context**. A BFC does not let a child's margin collapse through its own edge, so the
   0.7rem cannot escape the sidebar. Instead it offsets `nav` 0.7rem down *inside* the
   scroll container.
3. `nav` is `min-height: 100%` -- 100% of that container's height.

So the scrollable content measures `0.7rem + 100%` against a container of `100%`. It
overflows by 0.7rem, about 11 pixels, **at every viewport height**, because the offset is
a fixed length and the container is what grows. That is the reported symptom stated
precisely: not "the menu is too long", but "the menu is 11 pixels too long, forever".

### Why `nav` is a block and not a flex container

`<nav class="flex-column">`. Bootstrap defines `.flex-column{flex-direction:column
!important}` (`wwwroot/bootstrap/bootstrap.min.css`) -- **`flex-direction` only; it does
not set `display: flex`.** Nothing in `NavMenu.razor.css` or `wwwroot/app.css` sets
`display: flex` on the `nav` element either. So `nav` is a plain block box, margin
collapsing applies to it, and the bug above is live.

Had `nav` been a real flex container, none of this would happen: flex containers do not
collapse margins with their children.

## A second defect with the same root, visible in the same screenshot

`Components/Layout/NavMenu.razor:130` is `<div class="nav-item px-3 mt-auto pt-3
border-top">` -- the version footer. `mt-auto` is `margin-top: auto`, which pushes an item
to the end of the line **only in a flex container**. On a block box in normal flow, `auto`
resolves to `0`.

So the footer was written to sit at the bottom of the sidebar and does not. That is
observation 3 above. The author wrote `flex-column` on the nav and `mt-auto` on the
footer, which together state the intent plainly; the missing `display: flex` is why
neither took effect.

**This matters to the fix**, because the natural repair for the scrollbar -- making `nav`
the flex container it was written as -- also makes `mt-auto` start working and moves the
footer. See Q1.

## A third weakness, not itself the bug

`height: calc(100vh - 2.9rem)` hardcodes the height of the brand row as a literal in a
rule 190 lines away from `.sidebar-brand { height: 2.9rem }` (`:23-28`), in the same
file but with nothing connecting them. Changing one silently desynchronises the other:
too large and the sidebar overflows permanently (this bug's class), too small and it
leaves dead space. It is worth removing while the file is open, because the structural
fix below makes the constant unnecessary rather than merely correct.

## What is NOT the cause

- **Not the content length.** The menu comfortably fits; the screenshot proves it.
- **Not `overflow-y: scroll`.** The rule is `auto`, correctly.
- **Not a mirrored-stylesheet conflict.** `wwwroot/app.css` deliberately mirrors parts of
  `NavMenu.razor.css` because CSS isolation has been unreliable on published IIS
  (`ExchangeAdminWeb.Tests/UiThemeCssTests.cs:424-430`), and a rule changed in one copy
  and not the other is invisible to the build. Checked: `.nav-scrollable`,
  `min-height: 100%` and `.sidebar-brand` appear **only** in `NavMenu.razor.css`.
  `.nav-category` appears in **both** (`wwwroot/app.css:1025`), with the same
  `margin-top: 0.7rem`.
- **Not theme-specific.** Every rule involved is geometry; no theme token participates.

That last point drives a design choice below.

## Design

Fix it at the container, not at `.nav-category`.

Removing or zeroing the `0.7rem` margin on the first heading would also stop the
overflow, but `.nav-category` is one of the mirrored rules, so that fix must be made
identically in two files or it half-lands -- the exact trap
`docs/AdminUIRedesign-Plan.md` slice 2 recorded and `UiThemeCssTests` exists to catch. It
would also change the spacing above every category heading, which is a visual change to
fix a geometry bug. The container-level fix touches one file and no spacing.

Inside the existing `@media (min-width: 641px)` block:

1. **Make the sidebar a flex column and let the scroll pane take the remaining space**,
   replacing the magic number:
   - `.sidebar` (in `Components/Layout/MainLayout.razor.css`, which already sets
     `height: 100vh` at `:56-61`) gains `display: flex; flex-direction: column;`.
   - `.nav-scrollable` drops `height: calc(100vh - 2.9rem)` and takes
     `flex: 1; min-height: 0;` instead. `min-height: 0` is required -- without it a flex
     item refuses to shrink below its content and the pane overflows the sidebar instead
     of scrolling inside it.
   - `.sidebar-brand` gains `flex: none` so it keeps its 2.9rem and is not compressed.
2. **Make `nav` the flex container it is already marked as:**
   `.nav-scrollable nav { display: flex; flex-direction: column; min-height: 100%; }`.
   This is what actually kills the bug: a flex container does not collapse margins with
   its children, so the 0.7rem stays inside the child where it belongs and the scroll
   height equals the client height.

Keep the changes inside the `min-width: 641px` media query. Below that width the sidebar
is a collapsible drawer with `display: none` until toggled, `.sidebar` has no
`height: 100vh`, and making it a flex column unconditionally would change a layout nobody
reported a problem with.

### Why both halves, when either might do

**Step 2 is the fix. Step 1 is not, and an earlier draft of this plan wrongly said either
one would do.** They are both in scope because they address different things:

- **Step 2 (`nav` becomes flex) is what stops the overflow.** A flex container does not
  collapse margins with its children, so the 0.7rem stays where it was written.
- **Step 1 (sidebar flex column, `calc()` removed) fixes nothing today.** It removes the
  fragile coupling between two constants in two files that nothing keeps in step. Without
  step 2 the margin still collapses and the 11 pixels still overflow; without step 1 the
  bug is fixed but the next edit to the brand row can reintroduce its whole class.

If only one may land, land step 2.

## Slices

One commit. This is a single small CSS change to one subsystem and splitting it would
produce a commit that fixes half a geometry bug.

### S1 -- the sidebar scroll pane

Files: `Components/Layout/NavMenu.razor.css`, `Components/Layout/MainLayout.razor.css`.
No `.razor` markup changes unless Q1 rules (b).

**No change to `wwwroot/app.css`, and the reason needs stating rather than asserting.**
`app.css` does carry a `.sidebar` selector (`:1316`), so "the touched rules are not
mirrored" is too loose a claim to rest on. That rule is
`.sidebar { background-image: none !important; }` -- a theme override and nothing else.
**No sidebar geometry exists in `app.css` at all:** neither `width: 218px` nor
`height: 100vh` appears anywhere in the file, and both are live on the deployed dev
instance, which is visible in the 2026-09-24 screenshot as a 218px-wide full-height
sidebar. Those declarations exist only in `MainLayout.razor.css`, so CSS isolation is
demonstrably working for that component on this deployment and the mirror is not needed
for it. Geometry belongs in the isolated file; `app.css`'s `.sidebar` stays theme-only.

If implementation finds any touched declaration present in both files, stop and mirror
deliberately rather than picking one copy.

Tests: a CSS tripwire in the `ExchangeAdminWeb.Tests/UiThemeCssTests.cs` shape (it already
reads both stylesheets from disk and asserts rule content, `:421-433`). Assert the two
properties that, if lost, silently return the bug:

- `.nav-scrollable nav` declares `display: flex` -- the guard against margin collapsing.
- `.nav-scrollable` declares `min-height: 0` -- the guard against a flex item that will
  not shrink.

Both are shape assertions, not behaviour. **No test in this repo renders a page**, so the
tripwires prove the rules exist and nothing more. Manual check 1 is the only evidence that
the operator sees the scrollbar go away; say so rather than reporting a green suite.

Guard proof: delete `display: flex` from the nav rule, confirm the first tripwire fails;
delete `min-height: 0`, confirm the second fails; restore both and touch the files so
MSBuild rebuilds (the restore-timestamp trap).

## Acceptance criteria

- **AC1.** On a window tall enough to show the whole menu, the sidebar renders **no**
  scrollbar and no scroll arrows.
- **AC2.** On a window too short for the menu, the sidebar scrolls normally and reaches
  the last item, including the version footer.
- **AC3.** Spacing above every category heading (`EXCHANGE`, `DIRECTORY & GROUPS`,
  `IDENTITY & ACCESS`, `INFRASTRUCTURE`, `ADMINISTRATION`) is unchanged from today.
- **AC4.** The brand row keeps its height and its bottom border; the nav pane starts
  immediately below it with no gap and no overlap.
- **AC5.** Below 641px the collapsible drawer behaves exactly as it does today -- hidden
  until toggled, then fully scrollable.
- **AC6.** The Module Config tree still expands and scrolls into view when opened.
- **AC7.** Whatever Q1 rules, the footer's position is the ruled one and is stable at
  every window height.

## Verification

Automated, per `.agents/repo-guidance.md`:

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` (baseline as of `bd29a9c`: 3064 passed / 0 failed /
  3 skipped)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD`

Manual, on dev after deploy. **AC1-AC6 are reachable no other way.** Every one is a
rendered-layout fact and this repo has no bUnit harness:

1. A maximised window on a tall monitor: no sidebar scrollbar (AC1). This is the reported
   defect and the whole point.
2. Resize the window down until the menu no longer fits: the scrollbar appears, scrolls
   to the last item, and the footer is reachable (AC2).
3. Compare category spacing against the screenshot of 2026-09-24 (AC3).
4. A narrow window, under 641px: the hamburger drawer opens, closes and scrolls (AC5).
5. Expand Module Config with a short window: the tree scrolls into view (AC6).
6. Check one light theme as well as OLED Black -- the change is geometry, so a difference
   between themes would mean something unexpected is participating.

## Versioning

Shared infrastructure: the sidebar renders on every page. Base app version bumps
(`<VersionPrefix>`, `AssemblyVersion`, `FileVersion` in `ExchangeAdminWeb.csproj`); no
module version changes, and `Modules/ModuleCatalog.cs` must be byte-identical afterwards,
verified by diff.

A **PATCH** bump, following the `2.21.0 -> 2.21.1` precedent for the click-gating
shared-component change: this is a defect fix in existing shared UI, not new capability.

**Do not hardcode the resulting number from this document.** Read
`ExchangeAdminWeb.csproj` at implementation time and bump from what is actually there.
`docs/RiskyUsersCompleteResults-Plan.md` is also unapproved and also bumps the base app
version, so whichever lands first moves the floor under the other. A literal implementer
writing a stale number would downgrade three version fields -- the trap
`docs/ServiceHealthPublicStatus-Plan.md` was caught in.

## Open questions

**Q1 (owner). Does the version footer move to the bottom of the sidebar?**

Making `nav` a flex container is what fixes the scrollbar, and it also makes the existing
`mt-auto` on the footer start working. That would move `v2.20.2 | Issues?` from just below
the Administration block to the bottom of the sidebar.

- **(a) Let it move.** The markup already says `mt-auto` and `flex-column`; honouring them
  restores what the code was written to do, and a version stamp pinned to the bottom is
  the conventional place for one. Costs nothing extra.
- **(b) Keep it where it is.** The scrollbar fix is unaffected either way. But this costs
  a markup change, not a one-line CSS override: Bootstrap declares
  `.mt-auto{margin-top:auto!important}`, so a plain `margin-top: 0` in the isolated
  stylesheet loses to it. Do it by **removing `mt-auto` from
  `Components/Layout/NavMenu.razor:130`**, which states the intent in the one place a
  reader will look, rather than by fighting `!important` from a stylesheet.

Recommendation: **(a)**. The current position is not a design decision anyone made -- it
is the same missing `display: flex` reported as a bug, showing up somewhere else. But it
is a visible change that was not requested, which is why it is being asked rather than
assumed.

## Review

`openreview codex (@azure-openai-eus2-global/gpt-5.5-dzs @ xhigh, fallback) over
fa0dd1b..941cd77: acceptable_with_changes`. codex-cli 0.154.0, 2026-09-24. Capability
proof passed both halves (read `AGENTS.md`; ran `git diff --stat` over the pins, plus
`git diff --check`). Resolved model identity is not recoverable from the CLI envelope --
the Portkey gateway obscures it -- so this records what was dispatched, per the playbook.

The reviewer confirmed the root-cause analysis against the code and endorsed the
approach: sidebar as a flex column, nav pane `flex: 1; min-height: 0; overflow-y: auto`,
`<nav>` a real column flex container, mobile drawer untouched, footer position asked
rather than assumed.

Three material changes, **all three admitted and folded into the text above**:

1. **The plan contradicted itself.** It opened "Either change alone stops the overflow"
   and then, four lines later, said step 1 alone leaves the 11 pixels. The second
   statement is the correct one. Rewritten: step 2 is the fix, step 1 removes the
   fragile coupling, and if only one lands it must be step 2.
2. **The no-mirror claim was too loose to rest on.** `wwwroot/app.css:1316` does carry a
   `.sidebar` selector. Checked: it is `background-image: none !important` and nothing
   else, and no sidebar geometry exists in `app.css` at all -- neither `width: 218px` nor
   `height: 100vh` appears there, while both are live on dev. That is positive evidence
   CSS isolation works for `MainLayout` on this deployment, which is now written down
   instead of assumed.
3. **Q1 option (b) would not have worked.** Bootstrap declares
   `.mt-auto{margin-top:auto!important}`, so the proposed `margin-top: 0` override loses.
   Option (b) now removes `mt-auto` from the markup instead.

## Related records

- `docs/AdminUIRedesign-Plan.md` -- owns the sidebar's current design; still `In progress`
  with manual checks outstanding. This plan changes geometry only, no appearance.
- `ExchangeAdminWeb.Tests/UiThemeCssTests.cs:424-430` -- the mirrored-stylesheet trap and
  the test shape S1's tripwires follow.
- `docs/ProjectConstitution.md` -- Deployment And Versioning.
- `.agents/state.md` -- records the alphabetical-ordering work's sidebar manual checks as
  still unrun; manual check 3 here overlaps them and can close both if run together.
