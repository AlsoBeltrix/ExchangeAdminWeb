# AD Identity Picker Legibility Plan

Status: **Draft - awaiting owner approval.** Nothing implemented.

Reviewed by codex (gpt-5.5-dzs @ xhigh) 2026-08-06: verdict **sound after one cause correction**,
which is incorporated below (the truncation mechanism is end-truncation of a combined inline
stream, not a DOM-order effect, and the fix must make the primary label survive rather than merely
be non-shrinking).

## The defect

Reported 2026-08-06 with a screenshot of Module Config > Delegation Report > Access. Typing
`exchangeweb` into the group picker returns four suggestions, all rendered as
`AD\ExchangeWeb...` / `AD\ExchangeWebP...` - **the names are truncated at exactly the point where
they start to differ**, so the four candidates are visually identical or near-identical.

This is not cosmetic. The picker's output is written to `section_access` as the SID of whichever
row is clicked, and that row decides who can reach a module. Choosing the wrong one grants module
access to the wrong group, and the operator has no way to tell the rows apart before clicking.

## Cause, read from the markup

`Components/Shared/ADIdentityAutocomplete.razor:28-45`. One flex row per suggestion:

- **`:29`** - a single `text-truncate` div wraps the domain prefix, the display name AND the
  secondary text. They are one inline stream, and `text-truncate` end-truncates that whole stream
  at the box's right edge. **Correction to an earlier draft:** that draft said the secondary text
  was "protected by being last in the DOM" - it is not; being last means it is clipped first. The
  real mechanism is simpler and worse: the string rendered is
  `DOMAIN\DisplayName <secondary>`, and the cut lands at a fixed pixel width from the LEFT. Every
  candidate here shares the prefix `AD\ExchangeWeb`, so every candidate survives to the same point
  and dies at the same point - the truncation removes precisely the suffix that distinguishes
  them. Shared-prefix families are the worst case for end-truncation, and this directory is full
  of them.
- **`:41-44`** - a fixed `badge` (`Group` / `User` / `OU`) sits outside the truncating box and is
  never clipped, consuming horizontal space in every row. In a group-only picker every badge reads
  `Group`, so the space is spent on the one field carrying no information.
- **`:37-39`** - `GetSecondaryText` (`:168+`) returns UPN/SAM for a user and the **full DN** for an
  OU. A DN is long by nature, so for OUs the name is crowded out by design.
- **`:18-19`** - the dropdown is `w-100`, i.e. exactly the width of its input. The Module Config
  Access pane is narrow, so the available width is small before any of the above applies.

## Fix

Invert the truncation priority and stop spending width on non-distinguishing content.

1. **Make the identity itself survive.** Separate the name from the secondary text so they truncate
   independently, and let the primary label **wrap to a second line** rather than clip. A
   non-shrinking primary label alone is not sufficient: a single group name can be longer than the
   available width on its own, so it would still end-truncate at the same place for a shared-prefix
   family. Wrapping guarantees the distinguishing suffix is visible; middle-truncation (item 5) is
   the more compact alternative if two-line rows are unacceptable in the narrow pane.
2. **Show the badge only when the results are mixed.** A picker restricted to one object type
   (the group pickers, the OU picker) renders no badge at all; a mixed `Any` search keeps it. The
   component already knows its `ObjectType` filter.
3. **`title` on the row** carrying the fully qualified value (`DOMAIN\Name` plus secondary text),
   so hovering discloses anything still clipped. Cheap, and it makes the remaining truncation
   recoverable rather than fatal.
4. **Let the dropdown exceed the input width.** Replace `w-100` with a minimum of 100% and a
   max-width bounded by the viewport, so a long group name is readable in a narrow pane without
   the input itself being widened.
5. **Middle-truncate rather than end-truncate where a common prefix is likely.** `ExchangeWebAdmins`
   and `ExchangeWebPerms` differ only after 11 characters; end-truncation is worst-case for exactly
   the shared-prefix families this directory contains. If CSS-only middle truncation proves
   awkward, (1)-(4) alone resolve the reported case and this can be dropped - it is the one item
   here that is nice-to-have.

## Scope

`Components/Shared/ADIdentityAutocomplete.razor` only. The component is shared, so this improves
every picker at once: section-access groups, protected-principal users/groups/OUs, module admins.
No service, no storage, no authorization change - the selected value and everything downstream of
it are untouched.

## Verification

Markup-only, and the repo has no bUnit harness, so automated coverage is limited to what is
assertable without rendering:

- If any logic moves into a method (e.g. "should the badge render"), it is testable and gets a
  test.
- Otherwise this is verified visually, on dev, against the reported case: type `exchangeweb` in
  Module Config > Delegation Report > Access and confirm all four candidates are distinguishable
  without hovering.

Also confirm on the narrowest pane that uses the component, since width is the aggravating factor.

Standard gates still apply: build, `dotnet test ExchangeAdminWeb.slnx`, format,
`git diff --check HEAD`.

## Versioning

Shared component used by many modules, so this is app-wide: base app version bump, no single
module version. (`docs/ProjectConstitution.md` Deployment And Versioning.)

## Non-goals

- Changing what the picker searches or returns. The forest-wide search and the `DOMAIN\Name`
  qualification are correct and were added deliberately (`:30-35`).
- Changing what is stored. SIDs, unchanged.
