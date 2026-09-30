# App-Wide Layout And Scrolling -- One Contract Instead Of Thirty-Six Guesses

Status: **Draft. Needs owner approval AND the codex review the owner asked for, before any
code.** Queue items 21 and 22. Written against app `2.24.0`.

Item 22 asked for an app-wide audit of layout, alignment and scrolling, explicitly as one
holistic plan rather than a spot fix. Item 21 -- "popup report has no scrollbar and ignores the
mouse wheel" -- turned out to be one instance of the same failure, so it is folded in here as
the plan's first worked case rather than tracked separately.

## The evidence

Two owner screenshots on dev `v1.22.1`, 2026-09-30.

- `no_scrollbar.PNG` -- the migration report dialog. The header row (Export / Fetch again /
  Close) sits at the top of the viewport; the report text runs straight off the bottom edge with
  no scrollbar. There is no way to read the end of the report in the UI.
- `scrollbar_issue2.PNG` -- the migration two-pane card. Both pagers ("1-12 of 12 batches",
  "1-1 of 1 mailboxes") are sliced in half by the bottom of the viewport.

## The failure class, in one sentence

**A scroll region only works if every ancestor between it and a height-bounded box also
participates, and this app has no shared idea of where that height bound comes from -- so
thirty-three separate places each guess, in a different literal, and every guess is wrong the
moment the chrome above it changes.**

It breaks three ways here, and all three are present.

### 1. An inert wrapper swallows the flex contract -- this is item 21

`Components/Pages/Migration.razor:1174-1215` renders:

```
.mig-modal-backdrop   position: fixed; inset: 0; display: flex; padding: 2rem
  .mig-modal          display: flex; flex-direction: column; max-height: 100%; min-height: 0
    .card-header
    .card-body.p-0    <-- Bootstrap. A BLOCK. No min-height: 0, no overflow.
      pre.mig-report-text   overflow: auto; flex: 1 1 auto; min-height: 0
```

`Migration.razor.css:300-312` puts the scroll region on the `<pre>`, and its comment says the
report "scrolls inside the dialog rather than growing it past the viewport". It does not. The
`<pre>` is not a flex child of `.mig-modal` -- `.card-body` is -- so `flex: 1 1 auto` and
`min-height: 0` on the `<pre>` are **inert properties on a child of a block container**. Nothing
constrains the `<pre>`'s height, so `overflow: auto` never has an overflow to act on.
`.card-body` then grows to the full report height, and because `.mig-modal` sets `max-height`
without `overflow: hidden`, the content simply paints outside the modal box. That is exactly the
screenshot.

**The rule this breaks, stated so it is checkable:** between the element that owns
`overflow: auto` and the element that owns the height, every intervening box must be a flex
item of a flex container AND carry `min-height: 0`. One Bootstrap utility div in the middle
ends the chain silently -- nothing errors, nothing warns, the scrollbar is simply absent.

### 2. The height bound is a hardcoded guess at the chrome above it -- this is item 22

`Migration.razor.css:13-21`:

```css
.mig-split {
    height: calc(100vh - 21rem);   /* a literal guess at everything above the split */
    min-height: 26rem;
}
```

`21rem` has to cover the sticky top row, the article padding, the audit banner, the `<h1>` and
its version badge, the subtitle, the tab strip, the card header with Search / Export / Refresh,
and the card padding. On the owner's viewport it does not. The split is taller than the space
left, so its bottom -- the pagers -- lands below the fold.

**And the page scroll that would rescue it is unreachable where the operator's cursor is.**
`MainLayout.razor.css` gives `main` `flex: 1` with no height cap and no overflow, so the
document scrolls. But the split fills most of the viewport and its panes own their own scroll,
so a wheel event anywhere over the content goes to a pane, not to the document. The page's
remaining few rem of scroll is only reachable by putting the cursor in the margin. That is both
halves of what the owner reported: "bottom always cut off" and "ignores the mouse wheel".

### 3. There is no shared contract, so every page invented its own literal

**Twenty-six height-constrained scroll regions across the app** -- 25 inline `max-height`
declarations in `.razor` files plus `.mig-split` in CSS -- and no two pages agree. Nine of them
are `100vh` arithmetic, which is the sharp end: each is a guess at the same unknown, how tall is
the chrome above me. (Counted 2026-09-30 with `grep -rn "max-height" --include=*.razor
Components/` and `grep -rn "100vh" Components/Pages/`; re-count rather than trusting this number
if the sweep starts later.)

| Magic number | Where |
| --- | --- |
| `calc(100vh - 21rem)` | `Migration.razor.css:19` |
| `calc(100vh - 420px)` | `ADAttributeEditor.razor:119`, `BitLockerRecovery.razor:155` |
| `calc(100vh - 380px)` | `LicensingUpdates.razor:87`, `:144` |
| `calc(100vh - 320px)` | `BlockedSenders.razor:69`, `M365GroupManagement.razor:77` |
| `calc(100vh - 260px)` | `DhcpAuthorization.razor:64`, `NamedLocations.razor:70` |
| `max-height: 480px` | `AccountLockoutRemediation.razor:158` |
| `max-height: 400px` | `GroupManagement.razor:237`, `:295`, `ModuleConfig.razor:541` |
| `max-height: 360px` | `AccountLockoutRemediation.razor:76` |
| `max-height: 350px` | `ModuleConfig.razor:433` |
| `max-height: 300px` | `Comms10k.razor:61`, `CountryCodePicker.razor:2`, and the four autocomplete dropdowns |
| `max-height: 250px` | `ADAttributeEditor.razor:196` |
| `max-height: 200px` | `M365GroupManagement.razor:132`, `OutOfOffice.razor:75`, `:82` |
| `max-height: 150px` | `M365GroupManagement.razor:174` |

The fixed-pixel ones are a milder fault than the `100vh` ones -- a 300px dropdown is a
deliberate size, not a guess at the chrome -- but they are listed because the sweep must decide
about each one rather than skip a category.

## The app already contains the right answer, on three pages out of thirty-six

`wwwroot/app.css:1390-1510`, the `.adm` shell from `docs/AdminUIRedesign-Plan.md`:

```css
.adm      { display: flex; flex-direction: column;
            height: calc(100vh - 2.9rem - 1.1rem); min-height: 0; }
.adm-hd   { flex: none; }          /* heading: takes what it needs */
.adm-tabs { flex: none; }          /* tab strip: takes what it needs */
.adm-pane { flex: 1; min-height: 0; overflow: auto; }   /* body: takes the rest, scrolls */
.adm-bar  { flex: none; }          /* footer stays on screen */
```

Its own comment states the property the whole app should have: *"Each pane owns its own scroll,
so the PAGE never grows: 2 groups or 200, the viewport is the same height."*

`AdminSettings.razor`, `ModuleConfig.razor` and `CloudPasswordReset.razor` use `.adm`. The other
thirty-three pages do not, and neither does the migration dialog.

**Note the one literal it still contains** -- `2.9rem - 1.1rem`, the top row plus the article
padding. That is a guess too, just a smaller and more stable one. The proposal below removes it
rather than copying it twenty-six times.

**And the pattern works a second time already, with no literal at all.**
`AdminEventLog.razor:341-346`, the undo panel: a `position-fixed` box pinned `top-0 bottom-0`
with `d-flex flex-column`, a `flex: none` header, and a body that is
`class="p-3 flex-grow-1 overflow-auto"`. No `100vh`, no pixel guess -- the fixed positioning
supplies the height and the flex chain distributes it. That is the same contract `.adm` states,
reached from the other direction, and it is the proof that this is a house pattern the app can
adopt rather than a new idea being introduced by this plan.

## Proposed fix

**One page shell, opted into, with no page ever writing `100vh` again.**

1. **Give the layout the height instead of guessing it.** `MainLayout.razor.css`: `.page`
   becomes `height: 100vh`, `main` becomes a flex column with `min-height: 0`, `.top-row` stays
   `flex: none`, and `article.content` becomes `flex: 1; min-height: 0`. The chrome's height is
   then a fact the browser computes, not a number anyone types. The sidebar's existing
   `height: 100vh` + `position: sticky` is unaffected.

2. **Publish the shell as `.pg-*` classes** generalising `.adm`: `.pg` (flex column, fills what
   `article` gives it), `.pg-hd` (`flex: none`), `.pg-body` (`flex: 1; min-height: 0;
   overflow: auto`), `.pg-foot` (`flex: none`). `.adm` becomes an alias so the three pages on it
   do not churn.

3. **Convert pages onto it, deleting the magic number as each one lands.** Not all at once --
   see the slices.

4. **Fix item 21 by ending the inert-wrapper chain**: either drop the `.card-body` wrapper so
   `.mig-report-text` is a direct flex child of `.mig-modal`, or give that wrapper
   `display: flex; flex-direction: column; min-height: 0`. The first is simpler and is what this
   plan proposes. `.mig-modal` also gains `overflow: hidden` so a future break clips visibly
   instead of painting outside the box.

5. **Tripwire it.** `AppLayoutCssTests`, modelled on the existing `SidebarScrollCssTests` (which
   guards exactly this kind of invisible CSS defect and says in its own remarks that it proves a
   SHAPE, never a behaviour):
   - no `.razor` under `Components/Pages` may contain `100vh` in an inline style -- the shell
     owns viewport height now;
   - every element declaring `overflow: auto` / `overflow-y: auto` as a page-level scroll region
     must be a `.pg-body`, or be listed in a registry entry with a written reason (the
     autocomplete dropdowns are legitimately fixed-size popovers, not page regions);
   - `.mig-report-text` must be a direct child of `.mig-modal`.

## Alignment: mostly already closed, and worth saying so

Item 22 named alignment alongside scrolling. The audit found **no open alignment defect**. The
two that existed were migration `1.20.6` (a read-only operator saw every row one column left of
its heading) and `1.20.8` (the selection pane header lost a cell), and `1.21.0` removed the
cause by replacing both CSS-grid tables with real `<table>` elements -- a table sizes each column
to its own widest cell, so there is no width to get wrong and a header cannot misalign against
its body. The only remaining `grid-template-columns` rules in the app are
`repeat(auto-fit, minmax(...))` card grids in `ServiceHealth.razor.css` and `app.css`, which are
self-sizing and have no header to misalign.

**So alignment contributes one rule to this plan, not a work item:** a tabular surface uses
`<table>`. A CSS grid with fixed tracks needs a width per column and a header cell per track by
hand, and this repo has already paid for that twice in one day.

## Slices

| Slice | What | Owner input needed |
| --- | --- | --- |
| S1 | Item 21 only: the report dialog's inert wrapper, plus `overflow: hidden` on `.mig-modal`, plus its tripwire. Migration module version bump. | No |
| S2 | The layout shell: `MainLayout.razor.css` height chain and the `.pg-*` classes, with `.adm` aliased onto them. No page converted yet. Base app version bump. | No |
| S3 | Convert the `calc(100vh - Npx)` pages -- Migration, ADAttributeEditor, BitLockerRecovery, BlockedSenders, DhcpAuthorization, LicensingUpdates, M365GroupManagement, NamedLocations. One commit per page, each deleting its literal. | No |
| S4 | Decide and act on the fixed-pixel caps, one line per decision: keep (a deliberate popover size) or convert (a page region in disguise). | No |
| S5 | The tripwire test and the registry of deliberate exceptions. | No |

**S1 is separable and is the one the owner is actually waiting on.** It is a two-line markup
change against a diagnosed cause and it makes the reports readable again. S2 onwards touches
every page in the app and should not ride along with it.

## Verification

Build, full suite, format, `git diff --check`, and the CSS tripwires.

**And a hard limit that must not be glossed:** nothing in this repo can render a Blazor
component or measure a laid-out box. Every assertion this plan can add proves a SHAPE in the
source, never that a scrollbar appeared. `SidebarScrollCssTests` says the same thing about
itself in its own remarks, and it is right. **A green suite is not evidence that any of this
works.** Each slice needs an owner check on dev at a real viewport:

- S1: open a long migration report, confirm it scrolls inside the dialog and the wheel works.
- S2/S3: on each converted page, confirm the page itself does not scroll, the heading and any
  footer stay on screen, and the list scrolls inside its region.
- All: check one short viewport (a laptop at 768px tall) and one tall one, because a guess that
  happens to be right at 1440px is the whole reason this plan exists.

## Open question for the owner -- one

**Should the page itself stop scrolling entirely?**

Fixing the chrome-height guess (S2) can land two ways:

- **(a) The app fills the viewport and never scrolls as a page.** Every module is a fixed frame;
  only the regions inside it scroll. This is what `.adm` already does and what the two-pane
  migration layout was designed for. Headings, action bars and pagers are always on screen. The
  cost: a page with a genuinely long single column of content -- a form, a detail view -- has to
  grow a scroll region it did not need, and on a short laptop screen some pages will feel
  cramped.
- **(b) The page keeps scrolling, and inner regions are sized from real available space rather
  than from `100vh`.** Less disruptive, keeps long forms feeling natural. The cost: two scroll
  contexts still coexist, so the "wheel went to the wrong thing" complaint is reduced but not
  eliminated.

**Recommendation: (a), applied per page rather than globally.** Pages built around a list or a
two-pane split -- which is most of the modules the owner has complained about -- get the fixed
frame. Form-shaped pages keep a normal scrolling document and simply stop declaring `100vh`
regions inside themselves. That removes the mixed-context problem where it actually bites
without forcing a fixed frame onto pages that read better long.

Nothing else in this plan is an open question. The rest is mechanical once this is settled.

## Review

The owner asked for this plan to be **reviewed with codex** before implementation, and that has
not happened yet. It is a plan review, not a defect hunt: whether the shell contract is the
right shape, whether (a) or (b) is the right default, and whether the slice order is safe.
