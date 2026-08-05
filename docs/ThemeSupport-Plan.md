# Theme Support Plan

Status: **Approved 2026-08-04** by owner directive: *"add theme support properly. include 8-10
most popular themes in a dropdown selector that replaces the dark/light icon"*. That wording sets
the scope; no further owner gate is open.

Supersedes nothing. Extends `docs/AdminUIRedesign-Plan.md` slice 1 (the token layer), which this
work completes rather than replaces.

## Why

The redesign introduced a token contract -- ~21 named colours in `wwwroot/app.css` that the rest
of the stylesheet is supposed to read from. The contract works: the ~20 pages the redesign never
touched follow the theme without their markup changing, because the tokens also drive Bootstrap's
own `--bs-*` variables.

**But the contract is not enforced, and 61 rules bypass it.** They key off the `dark` class
directly (`html.dark .card`, `html.dark .form-control`, `html:not(.dark) .alert-info`, ...) rather
than reading a token. Two consequences:

1. **Adding a third theme today produces a broken app, not an imperfect one.** Those 61 rules fire
   on `html.dark` and nothing else. A Dracula user would get Dracula's canvas with *light-mode*
   cards, form fields, tables, alerts, dropdowns, toasts and nav tabs painted on top.
2. **It is where the last two visual defects came from.** The greyed-out sidebar (`e442df4`) and
   the undelineated principal lists (`31142d9`) were both rules that never moved onto the token
   layer. The conversion is worth doing on its own merits; themes are the forcing function.

## Approved direction

- **10 themes**, each a block of token values and nothing else. No layout, markup, or component
  changes per theme.
- **A dropdown replacing the sun/moon button.** The toggle is a two-state control and cannot
  express ten.
- **The token contract becomes total.** After this work, no rule anywhere is conditional on which
  theme is active. A theme is data.

## Theme list

Ten, keeping both existing themes so nothing the owner has already approved is removed. `IsDark`
drives the browser's `color-scheme`, which controls the UI *we do not own* -- scrollbars, native
select popups, date pickers, form autofill. Getting it wrong gives white scrollbars on a black
canvas.

| id | Name | Dark | Source palette |
|---|---|---|---|
| `light` | Light | no | current default, unchanged |
| `oled` | OLED Black | yes | current dark, unchanged (owner-approved 2026-08-04) |
| `solarized-light` | Solarized Light | no | Ethan Schoonover base3/base00 |
| `solarized-dark` | Solarized Dark | yes | Ethan Schoonover base03/base0 |
| `dracula` | Dracula | yes | dracula-theme.com |
| `nord` | Nord | yes | nordtheme.com polar night / snow storm |
| `gruvbox-dark` | Gruvbox Dark | yes | morhetz gruvbox dark medium |
| `monokai` | Monokai | yes | classic Monokai |
| `one-dark` | One Dark | yes | Atom One Dark |
| `tokyo-night` | Tokyo Night | yes | enkia tokyo-night storm |

Eight of ten are dark. That is what the popular-editor-theme population looks like; `light` and
`solarized-light` cover the other case, and Light stays the default.

## Design

### Selection mechanism

`<html data-theme="dracula">`, set before first paint by the existing inline script in
`Components/App.razor`. `:root` carries the Light theme, so an absent or unrecognised attribute
degrades to Light rather than to unstyled.

**The `dark` class stays, applied alongside `data-theme` for every theme whose `IsDark` is true.**
It is no longer what any rule in `app.css` keys off -- it is a failsafe. If this conversion misses
a rule, that rule still gets dark-ish treatment on a dark theme instead of white-on-black. Given
that a missed rule is exactly the failure mode that produced the last two defects, the belt and
braces are deliberate. A test (below) is what actually proves the conversion complete.

### Token contract

Current 21 tokens stay. Eight are added, all for the semantic tints that are currently the
hardcoded exceptions -- the success/danger/warning/info table rows and alerts:

    --ui-info  --ui-info-bg  --ui-info-line
    --ui-on-bg  --ui-on-line          (success; --ui-on already exists)
    --ui-warn-bg  --ui-warn-line      (--ui-warn already exists)
    --ui-danger-line                  (--ui-danger, --ui-danger-bg already exist)

29 tokens per theme. Hover states are derived with `color-mix` rather than declared, so a theme
author states 29 values, not 45. `color-mix` is already in use for the focus ring.

### Persistence and migration

`localStorage['theme']` currently holds `light` or `dark`. `dark` migrates to `oled` on read --
that is the same palette, so an existing user sees no change. An unknown stored value resolves to
Light. The mapping lives in C# (`UiThemeCatalog.Resolve`) and is mirrored in the pre-paint script,
which cannot call into C#; the test suite pins both to the same table.

### Where the theme list lives

`Services/UiTheme.cs` -- a record plus a static catalog. Not a hardcoded `<option>` list in the
component, for the reason the repo already applies elsewhere (`MessageTraceExportListing`,
`AdminPageDirtyState`): there is no bUnit harness here, so anything inside a `.razor` file is
untestable. The catalog is the testable seam.

## Slices

1. **Token contract completion. DONE 2026-08-04.** The 61 theme-conditional rules are now 0. Every
   Bootstrap component rule -- card, table, form control, alert, dropdown, toast, modal, popover,
   list group, nav tab, badge, the `bg-light`/`bg-white` utilities -- reads tokens and is
   unconditional. 12 tint tokens added (three each for on/warn/danger/info) so the coloured table
   rows and alerts stop being the hardcoded exception; hover states are derived with `color-mix`
   rather than declared, which is what keeps a theme at 28 values instead of 45.
   **One extra fix landed here, same defect class as the greyed-out sidebar:** the mobile hamburger
   toggler hardcoded its stroke colour per theme (white for dark, black for light) and would have
   been invisible on eight of the ten. It is a mask taking `--ui-nav-fg` now, like every other icon.
2. **`UiThemeCatalog` + tests. DONE.** `Services/UiTheme.cs`, `Services/UiThemeJs.cs`, 25 tests.
3. **The eight new theme blocks. DONE.** Pure data, as intended -- no rule was touched to add them.
4. **`ThemePicker.razor` replacing `ThemeToggle.razor`. DONE.** Grouped `<select>`; `setTheme`
   /`getTheme` JS; the pre-paint script now sets `data-theme` and the failsafe class.
5. **Version bump + docs. DONE** -- app `2.4.1 -> 2.5.0` (minor, not patch: user-visible new
   capability app-wide).
6. **Palette rework (owner rejection, 2026-08-04). DONE** -- app `2.5.2 -> 2.5.3`.

   Owner on seeing the themes live: *"the themes aren't implemented optimally. I see really only
   two main colors."* Correct, and measurement said why. Three faults, all in the token VALUES
   rather than the mechanism -- which is the mechanism working as designed: a theme is data, so
   this was a data fix with no rule touched.

   1. **Surfaces did not separate.** Canvas, surface, header and zebra sat within ~12 luminance
      points of each other (Light 5.4, Tokyo Night 12, Dracula 13). A step needs ~20+ to read as a
      step, so cards did not sit on the page and table headers did not separate from their rows.
      Every palette therefore collapsed to background-plus-text however colourful it was
      underneath. Now 16-33 across the ten, using each project's OWN darker variant as the canvas
      (Dracula `#1e1f29` under `#282a36`, Gruvbox `bg0_h` under `bg0`, One Dark `#21252b`, Tokyo
      Night `#1a1b26`) rather than one hue at four brightnesses.
   2. **The accent appeared in six places, all tiny** -- links, primary button, focus ring, tab
      underline. The colour that makes a theme recognisable never reached a working page. Card
      headers now carry a 2px accent rule.
   3. **The sidebar shared the page background**, so it read as empty margin rather than a pane.
      Every theme now gives it its own darker tone.

   Two contrast repairs found while measuring: Solarized Dark's own base01 tertiary lands at 2.6:1
   on base02, and Solarized Light's base2/base1 pair was similar. Both lifted -- Solarized's
   contrast assumes a code editor, not 13px UI labels.

   **Two new tests pin the property that was broken**, because the existing ones only checked that
   tokens EXIST, which says nothing about them being distinguishable:
   `EveryThemeSeparatesItsSurfacesVisibly` (canvas->header >= 15) and
   `EveryThemeSeparatesItsRaisedSurfaceFromItsCanvas` (canvas->surface >= 6). The second caught
   Tokyo Night at 5.1 during development and forced the deeper canvas above -- a real catch, not a
   formality. Non-vacuity proven by restoring the rejected Dracula values: both fail, naming the
   exact spreads.

7. **Bootstrap's own palette + Dracula fidelity (owner, 2026-08-04). DONE** -- app `2.5.3 -> 2.5.4`.

   Owner, after slice 6 reached dev: *"buttons and checkboxes are still blue on every theme.
   dracula isn't using green or yellow and doesn't match the dracula theme. that one I know well,
   and this is a bad implementation."* Both correct.

   **Why the blue survived a theme rework.** Counted across the markup: this app uses **24
   colour-bearing Bootstrap classes** and the theme layer restyled **3** of them
   (`btn-primary`, `btn-outline-secondary`, `form-check-input` border). Everything else --
   checkboxes, switches, radios, spinners (159 uses), badges (43), `.text-*`/`.bg-*` utilities,
   and the success/danger/warning button variants -- is painted by Bootstrap from
   `--bs-primary: #0d6efd` and friends, which nothing overrode. Slice 1 mapped 8 `--bs-*`
   variables and stopped short of the semantic palette, so the tokens never reached those
   controls no matter what a theme declared.

   Fixed at the variable layer rather than rule by rule: `--bs-primary/success/danger/warning/info`
   and their `-rgb` companions now read the tokens, which repairs the other 21 classes at the
   source. Explicit rules remain only where Bootstrap uses a shorthand that ignores the variable,
   or bakes the colour into an SVG data URI (the switch knob).

   **The `-rgb` companions are a second copy of each accent**, needed because Bootstrap blends
   them for translucent overlays. Two copies drift, and this one already did during development --
   Dracula's warn moved yellow -> orange and its triplet did not follow, which is invisible
   because the solid colour stays right and only overlays go wrong.
   `EveryRgbTripletMatchesItsHexToken` now derives the expected triplet from the hex and fails
   naming both. They are also in `RequiredTokens`, so a theme cannot omit them.

   **Dracula.** The accent VALUES were already spec-correct, but two things were not: the canvas
   `#1e1f29` was invented rather than taken from the palette (now `#21222c`, Dracula's own ANSI
   black), and the state tints were off-hue -- danger was tinted toward pink while its foreground
   was red, and warn used Yellow `#f1fa8c` where Orange `#ffb86c` is the palette's warning colour.
   Every value in that block is now from the published spec.

8. **Button STATES (owner, 2026-08-04). DONE** -- app `2.5.4 -> 2.5.5`.

   Slice 7 shipped and the owner still saw blue buttons, with screenshots. **Slice 7 was not
   wrong, it was incomplete**, and the screenshots contained the diagnosis: on the same Gruvbox
   page the checkboxes were orange while the submit button was blue, and on M365 Group Management
   an *enabled* `btn-primary` ("New Group") rendered orange while *disabled* ones rendered blue.
   Every blue button in every screenshot was **disabled** -- they were empty forms.

   Cause: Bootstrap 5.0 hardcodes `.btn-primary{background-color:#0d6efd}` rather than reading
   `--bs-primary`, and states its variants on **two-class selectors**
   (`.btn-primary.disabled, .btn-primary:disabled`, `.btn-primary:hover`, `:focus`, `:active`).
   A single-class override loses to those on specificity. So slice 7's `.btn-primary` rule won for
   a resting button and lost for every other state. Its focus shadow is likewise a literal
   `rgba(49,132,253,.5)`.

   Fixed by stating disabled/hover/focus/active per variant, matching Bootstrap's own selector
   shape so ours win on equal specificity -- `!important` is deliberately not used.

   **Why the earlier verification missed it:** the check was "does the rule exist and read a
   token", which it did. Specificity is invisible to that question. The new
   `EveryBootstrapColourVariantOverridesItsDisabledState` asserts the disabled state is stated for
   all 11 variants, and `NoBootstrapBrandColourIsHardcodedInOurStylesheet` fails on any of
   Bootstrap's literal brand hexes appearing in our CSS. Both proven non-vacuous by reintroducing
   the exact defect.

## Verification

Per `.agents/repo-guidance.md`: build, `dotnet test`, format check, ASCII lint,
`git diff --check HEAD`, `tools/Test-CoverageFloor.ps1`.

**Result 2026-08-04:** build clean, **1255 tests pass / 3 skipped / 0 fail**, format, ASCII
(`tools/Test-AsciiOnly.ps1`, exit 0) and whitespace all clean.

**The coverage ratchet FAILS at 64.7% against its 65.06 floor -- and it was already failing
before this work.** Measured both ways to establish that rather than assume it: a scratch worktree
at `6f89f1c` (this plan's doc commit, no theme code) reports `1015 / 1569`, byte-identical to the
working tree. Nothing here touches the gated scope -- that scope is authorization and permission
code, and this work is CSS, a catalog record and a picker.

Cause, traced rather than guessed: commit `0e35e7b` ("show section-access groups as DOMAIN\Name")
added 49 lines to `Services/SectionAccessGroupDirectory.cs`, which is **0% covered**, after the
floor was set at `9a66cf4`. Growing an uncovered file lowers the ratio; the gate is working
exactly as designed and is reporting a real regression that predates this plan.

**The floor was NOT lowered.** `.agents/review/coverage-floor.txt` says in terms that lowering it
to make a build pass converts the gate into decoration -- that is finding tsr-1, already made once
in this repo. Recorded here and in `.agents/state.md` as an open item needing its own fix (tests
for `SectionAccessGroupDirectory`, which is a live AD dependency and so needs a seam first). It
is not this plan's scope to fix, and it is not this plan's place to silence.

**The load-bearing automated test is a CSS-contract test**, because the failure mode here is not a
compile error and not a wrong-looking pixel -- it is a *missing* token, which silently inherits
Light's value. A dark theme missing `--ui-fg` renders near-black text on a near-black canvas: the
page is not broken, it is invisible. `UiThemeCssTests` parses `wwwroot/app.css` and asserts, for
every theme in the catalog, that a selector block exists and defines **every** token in the
contract. It fails naming the theme and the token.

Two further assertions in the same file are what keep themes data rather than code: no rule may be
conditional on a specific theme, and no rule may key off the `dark` class -- the one permitted use
being the `color-scheme` declaration, which is checked structurally rather than by comment. That
second one scans the isolation stylesheets too, so it is also the guard against the `app.css` /
`NavMenu.razor.css` mirror trap recorded in `docs/AdminUIRedesign-Plan.md` slice 2.

**Non-vacuity proven 2026-08-04** by breaking each guard and confirming the failure names the
right thing:

| Probe | Result |
|---|---|
| Remove `--ui-fg` from the Dracula block | fails, reporting `dracula: --ui-fg` |
| Append `html.dark .card { ... }` | fails, quoting that exact rule |
| Delete the whole Nord block | fails, reporting `nord` |

All 25 pass again on restore.

### Manual checks

Page rendering is not unit-testable here, so these are the only evidence the themes look right.

1. Each of the 10 themes selected in turn: sidebar, top bar, a table page, a form page, and the
   Admin Settings tabs are all legible. No white-on-white or black-on-black anywhere.
2. Scrollbars, native `<select>` popups and the date picker match the theme's light/dark base --
   this is what `color-scheme` controls and it is invisible until checked on a dark theme.
3. Selection survives a full page reload and a Blazor reconnect, with no flash of the wrong theme
   on load (the pre-paint script is what prevents this; it runs before `<body>`).
4. A user whose stored value is the old `dark` lands on OLED Black, not Light.
5. The four semantic tints -- a success, danger, warning and info alert and table row -- are
   legible on every theme. These are the rules that were hardcoded, so they are the ones most
   likely to have been converted wrongly.
6. Admin page still reachable by `ANALOG\ExchangeWebAdmins` (the `sidf-1` lockout check, re-run
   after any app-wide change).

## Non-goals

- Per-user server-side theme persistence. `localStorage` is per-browser, which matches the
  existing behaviour; a DB-backed preference is separate scope.
- A custom/user-defined theme editor.
- Syntax highlighting. These are editor palettes being used as UI palettes; the app displays no
  code.
- Changing the default. Light stays default for new users.

## Open questions

- **OQ-1.** Eight of the ten themes are dark. If the owner wants more light options, Gruvbox,
  Solarized and Tokyo Night all have published light variants that would be one token block each.
  Not assumed.
