# Boolean config fields render as non-ambiguous controls

Status: Draft 2026-09-01, owner-directed P1 the same day. No owner decision is
outstanding. Awaiting a go.
Owner: Michael
Last verified against code: `b4f6047` / 2026-09-01
Versions: base app bump (shared `ModuleConfigField` type + `ModuleConfig.razor`
rendering; read the csproj number at implementation time and minor-bump). No
module version bumps: no module's behavior changes - only how the shared admin
page renders its declared field.
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. Owner ruling recorded in `.agents/decisions.md`
2026-09-01: boolean settings must be non-ambiguous controls, never free text -
"no compromise".

## 1. Goal

A boolean module setting must be impossible to mistype. Today every ConfigField
that is not an AD-group/user/OU picker falls through to a plain
`<input type="text">` (`Components/Pages/ModuleConfig.razor:451-457`), so
`PreventSelfGrant` - a security setting - is an admin typing `true` or `false`
into a text box (`Modules/ModuleCatalog.cs:150`, description "(true/false)").

Done means: a `Boolean` config field type exists, Module Config renders it as a
checkbox (the page already renders checkboxes - the module Enabled toggle at
`ModuleConfig.razor:114` is the visual precedent), toggling stores exactly
`true`/`false`, and both known boolean fields use it: `PreventSelfGrant` now, and
the BitLocker plan's `ValidateTickets` when its S2 lands (that plan now declares
the dependency).

The server-side parse guards are NOT relaxed: the stored value remains a string
that an out-of-band edit or bad deploy can corrupt, so `PermissionValidator.cs:62`
stays as is and the ticket validator's fail-closed unparseable rule (btv-1)
remains the backstop. The control fixes the honest-operator path; the guards
cover everything else.

## 2. Non-goals

- Changing what any module does with its setting's value.
- Migrating stored rows. Existing stored `true`/`false` strings load as the
  checkbox state; the storage format is unchanged.
- New controls for non-boolean fields (dropdowns, numeric steppers, ...). The
  ruling covers ambiguity on booleans; nothing else is in evidence.
- The CSV-upload documentation strings mentioning True/False
  (`MailboxPermissions.razor:111-113`) - file-format docs, not controls.
- bUnit or any page-rendering test harness.

## 3. Acceptance criteria

- AC1: `ConfigFieldType` (`Modules/ModuleConfigField.cs:3-8`) gains `Boolean`.
- AC2: Module Config renders a `Boolean` field as a checkbox (form-check shape,
  like `:114`), never the text fallthrough; the label is the field's Label and
  the description renders as the existing form-text. Toggling calls the existing
  `SetConfigValue` with exactly `"true"` or `"false"`.
- AC3: Checkbox state on load: checked iff the stored (or default) value parses
  as boolean true. A stored unparseable value renders per the field's
  DefaultValue and the next save writes the canonical string - the value heals
  on save; until then the server-side guards govern.
- AC4: `PreventSelfGrant` is declared `FieldType: ConfigFieldType.Boolean` and
  its description drops the "(true/false)" instruction.
- AC5: Tripwire test: every catalog ConfigField whose DefaultValue parses as a
  boolean has `FieldType == Boolean` - the next flag cannot ship as text.
- AC6: Base app version bumped in the same slice; no module version changes;
  `ModuleCatalogTests` green (no count/alias changes).
- AC7: `docs/AdminModuleDeveloperGuide.md` field-type list (locate by reading)
  documents `Boolean` and the rule that boolean settings must use it.

## 4. Failure behavior

| Case | Behavior | Basis |
|---|---|---|
| Stored value is garbage (out-of-band edit) | Checkbox renders per DefaultValue; consumers keep their own parse guards (fail closed where the setting is a control) | AC3; btv-1 backstop |
| Admin toggles and save fails | Existing Module Config save error path, unchanged | No new save logic |
| A future boolean field declared as Text | AC5 tripwire fails the build | AC5 |
| JS disabled / render quirk | Same exposure as the existing Enabled checkbox; no new mechanism | AC2 reuses the page's shape |

## 5. Rollback / blast radius

Revert the commit. Storage format unchanged, so nothing written under the
checkbox needs undoing - `true`/`false` strings are what the text box stored.
Blast radius: the shared field-type enum, one rendering branch on Module Config,
one catalog declaration, the developer guide.

## 6. Design sketch

- `Modules/ModuleConfigField.cs`: add `Boolean` to `ConfigFieldType`.
- `Components/Pages/ModuleConfig.razor`: a new branch in the field-rendering
  chain (before the `:451` fallthrough), rendering a `form-check` checkbox:
  `checked` = `bool.TryParse(GetConfigValue(field.Key, field.DefaultValue), out var b) && b`;
  `@onchange` -> `SetConfigValue(capturedKey, checked ? "true" : "false")`.
  Description as form-text, same as the sibling branches.
- `Modules/ModuleCatalog.cs:150`: `PreventSelfGrant` gets
  `FieldType: ConfigFieldType.Boolean`; description reworded without
  "(true/false)".
- Tripwire test beside the other catalog tests in
  `ExchangeAdminWeb.Tests/ModuleCatalogTests.cs`: for every module, every
  ConfigField with `bool.TryParse(DefaultValue, out _)` true must have
  `FieldType == ConfigFieldType.Boolean`.
- Cross-plan: `docs/BitLockerMandatoryTicket-Plan.md` S2 declares
  `ValidateTickets` with the Boolean type (that plan carries the note; its S2
  runs after this plan lands).

Rendering is not automatable (no bUnit); a source guard asserting the razor file
contains a `ConfigFieldType.Boolean` branch is comment-satisfiable (blr-3/blr-4)
and is still worth one line as a wiring hint, with the manual check as proof.

## 7. Task breakdown

**S1 (single slice) - type, rendering, catalog, tripwire, base bump, guide.**
Serves all ACs. One commit: the pieces are inseparable (a Boolean field type
with no renderer would fall through to text and violate the ruling it exists
to implement).

## 8. Test plan

| AC | Test | Non-vacuity |
|---|---|---|
| AC5 | `Catalog_BooleanDefaultedFieldsDeclareBooleanType` | Revert PreventSelfGrant to Text; FAIL |
| AC1/AC4 | compile + the tripwire | n/a |
| AC2/AC3 | source guard `ModuleConfig_RendersBooleanBranch` (razor contains the Boolean branch and the fallthrough excludes it) + manual checks below | Remove the branch; FAIL |

Manual checks after deploy:

1. Module Config -> Mailbox Permissions: Prevent Self-Grant is a checkbox, no
   text box; toggle it, save, re-open - state persisted; flip it back.
2. Confirm the underlying stored value reads `true`/`false` (Event Log config
   audit or the config DB).
3. Confirm a module with only text fields still renders unchanged.

Verification commands: the standard four from `.agents/repo-guidance.md`.
Non-vacuity: revert each named target, FAIL, restore, PASS.

## 9. Traceability check

To be completed at implementation time.

## 10. Review log

None yet.
