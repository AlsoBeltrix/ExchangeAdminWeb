# Sidebar Nav Icon Clipping & Header Row Fix

**Status:** Implemented (v2.3.3, build verified)  
**Date:** 2026-06-08

## Context

Two visual bugs visible in screenshots of the Admin Portal:

1. **SVG icons clipped** — Some nav items (e.g. "Exchange Migration", "Conference Rooms", "Message Analysis") have their leading SVG icon cut off, while shorter-named items render fine. Root cause: the `.bi` icon spans inside flex nav-links lack `flex-shrink: 0`, so when the display name text is long, the flex layout compresses the 1.25rem icon span below its minimum, clipping the background-image SVG.

2. **Header row not uniform** — The sidebar brand ("Admin Portal") lives inside the `.sidebar` div, while the top-row (user info, theme toggle) lives inside `<main>`. In light mode especially, they have different background colors (dark slate sidebar brand vs light gray top-row), creating a visual seam. They should appear as one continuous header bar at the same height.

## Changes

### 1. Fix icon clipping — fixed flex slot on `.bi` icons

Give the `.bi` icon span a fixed, non-shrinking flex slot so a long display name can never compress it below its 1.25rem width:

```css
flex: 0 0 1.25rem;
min-width: 1.25rem;
```

Mirror this in **both** copies so global and isolated CSS stay in sync:
- `wwwroot/app.css` — `.bi` rule (~line 64)
- `Components/Layout/NavMenu.razor.css` — `.bi` rule (~line 28)

### 2. Uniform header row across both panes — variable-driven

The sidebar brand and the top-row are in separate DOM parents, so they can only look continuous if they consume the same color values. Introduce a single source of truth for the header colors and have all three header elements consume it.

Define the variables (light defaults in `:root`, dark overrides in `html.dark`):

```css
:root {
    --toprow-bg: #edeef1;
    --toprow-border: #d8dae0;
    --toprow-color: #1e2028;
}
html.dark {
    --toprow-bg: #2b2f33;
    --toprow-border: #3a3f44;
    --toprow-color: #e0e0e0;
}
```

Then consume them in `.top-row`, `.sidebar-brand`, and `.sidebar .navbar-brand`.

**Critical fixes flagged in review (must do, or the header silently breaks):**
- **Brand anchor color:** `wwwroot/app.css:451` has `.sidebar .navbar-brand { color: #f8fafc !important; }`. On a light brand strip this leaves white-on-light text. Change to `color: var(--toprow-color) !important`.
- **Dark-mode variable conflict:** `app.css` currently has both `html.dark .top-row { background-color: #2b2f33 !important; }` (line ~190) and `html.dark { --toprow-bg: #1e2128; }` (line ~382) — they disagree. Remove the hardcoded `html.dark .top-row` background override and the redundant `html:not(.dark) main > .top-row` background override so the variables are authoritative. Reconcile the `html.dark` var block to `#2b2f33`.
- **Header line continuity:** add `border-bottom: 1px solid var(--toprow-border)` to `.sidebar-brand` so the bottom rule continues across the left pane.

### 3. Mobile hamburger contrast (light mode)

`.navbar-toggler` (NavMenu.razor.css) uses a white SVG stroke + translucent-white bg, which becomes near-invisible once the brand strip is light in light mode. Add a `html:not(.dark) .navbar-toggler` override with a dark stroke and a faint dark background.

## Files to modify

- `wwwroot/app.css` — `.bi` flex slot; `:root`/`html.dark` toprow vars; remove conflicting `html.dark .top-row` and redundant `html:not(.dark) main > .top-row` bg overrides; make `.sidebar .navbar-brand` color var-driven
- `Components/Layout/NavMenu.razor.css` — `.bi` flex slot (mirror); `.sidebar-brand` var-driven bg/color + border-bottom; light-mode `.navbar-toggler` override
- `Components/Layout/MainLayout.razor.css` — `.top-row` add `color: var(--toprow-color)`

## Verification

1. `dotnet build -c Release` — clean build
2. Deploy to dev, open in browser
3. Sidebar: all SVG icons render at uniform 1.25rem width, no clipping regardless of text length
4. Light mode: sidebar brand + top-row are the same color, one continuous bar; "Admin Portal" text is dark and readable; bottom border runs across both panes
5. Dark mode: same — uniform color across both panes, brand text white
6. Sidebar body color below the brand unchanged (dark slate)
7. Mobile (narrow viewport, light mode): hamburger toggler is visible against the light brand strip
