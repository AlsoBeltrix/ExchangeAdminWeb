# bind-on-change: five boxes that gate a button bound on change, so the button stayed disabled until blur

**Severity**: LOW - nothing wrong was written and no check was skipped: the check behind each button is unchanged.
The operator typed a ticket, the button stayed grey until the box lost focus, and the first click on the button was
spent on that blur instead of pressing it.
**Status**: Fixed on branch; awaiting merge
**Branch**: `fix/p0-c-phase0-lows` (worktree `D:\source\ExchangeAdminWeb-wt\p0-c`, from master f151f527)
**Commit**: 91f7f3b5 (the five boxes, tests), 7b3edf69 (versions) |
**Implementer**: Claude (supervised agent)
**Reviewer**: Fable 5.2 audit 2026-11-10, confirmed by Gemini 4 Argon (High) through agy; orchestrator triage
**Plan**: `docs/SharedPiecesAndSlop-Plan.md` slice 0.7 (defect #5). Interim: phase 3 replaces the ticket boxes with
the shared one.

## Evidence (master f151f527)

Each box was `@bind="..."` with no event, which Blazor binds to the change event; each gates a button whose
`disabled` reads the bound field.

- `Components/Pages/AdminEventLog.razor:436` `undoTicket` - gates **Confirm Undo** (`:456`).
- `Components/Pages/Migration.razor:52` `singleEmail` and `:63` `singleTicketNumber` - gate **Check Eligibility** (`:67`).
- `Components/Pages/Migration.razor:237` `bulkTicketNumber` - gates **Check All** (`:241`).
- `Components/Pages/NamedLocations.razor:187` `formTicketNumber` - gates **Create / Update** (`:205`) and
  **Confirm Delete** (`:196`).

## Fix

Each of the five gained `@bind:event="oninput"`, the idiom the app's other ticket boxes use (for example
`AccountLockoutRemediation.razor:133`, `Migration.razor:1491`). Nothing else: no new component, no handler, no
change to any `disabled` expression or to the check behind any button.

- Every edit is within its line, so no line moved; the click-gate registry's entries for these boxes (it lists four
  of them as DOM-synced controls by line and `@bind="..."` text) still match, and its tests pass.
- Help: no page note, hover tip or README line describes when these buttons enable, so there was none to rewrite.
- App code: +5 / -5 lines.

## Tests

`TicketBoxBindsOnInputTests` (new, 4). The test project has no browser and no bUnit, but it does execute pages'
compiled render trees (as the Migration and Attribute Editor page tests do), and that is enough for this: each test
builds the real page's tree, finds the box, fires **its input-event handler** with the typed text - what a browser
sends on each keystroke, with no blur and no change event - builds the tree again, and reads the button.

- `EventLogUndo_TypingTheTicket_EnablesConfirmUndo`
- `MigrationSingle_TypingTheAddressAndTheTicket_EnablesCheckEligibility` (disabled with nothing typed, still
  disabled with only the address, enabled with both)
- `MigrationBulk_TypingTheTicket_EnablesCheckAll`
- `NamedLocations_TypingTheTicket_EnablesCreate`

What this does not cover: a real browser's focus behaviour (the lost first click itself) - only that the box now has
an input handler and that the handler's effect reaches the button. Named Locations' **Confirm Delete**, gated by the
same box, is not separately asserted.

No test deleted or changed.

## Guard proof (reverted, the class's 4 tests, restored)

| Reverted | Failed | Tests that failed |
|---|---|---|
| All five boxes (the three pages as at master) | 4 | all four - each "the '...' box has no input-event handler" |
| Only Migration's address box | 1 | `MigrationSingle_...` |

Restored: 4 of 4 pass; the click-gate, Migration, Named Locations and Event Log test classes pass (1285 in the
targeted run).

## Left alone, and why

- Other boxes on these pages that bind on change and gate nothing (Migration's batch names, Named Locations' display
  name and IP ranges) are not in the defect and were not touched. Named Locations' display name does take part in
  the Create button's gate and still binds on change; the audit named the ticket box only, and the plan's row is
  "the four boxes" plus the Migration address box.
- No comment was added or edited.
- Versions: Event Log 1.5.5, Migration 1.27.7, Named Locations 1.6.3. No base bump: no shared code changed.
