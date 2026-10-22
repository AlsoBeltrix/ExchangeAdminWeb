# aud-a26e: Allowlist patterns cannot be seen or edited on the page, and the legacy import bypasses the pattern check

**Severity**: LOW - no wrong write, but an admin has no way to see or correct an attribute's
validation pattern on the Module Configuration page (the row only round-trips the stored value),
so a refused save under aud-a28e can only be cleared by deleting and re-adding the row, which
silently drops the pattern. The legacy `ad-editable-attributes.json` import writes rows straight to
the repository and so skips the aud-a28e save-time check; the validation-time catch still refuses
at save, so the gap is operability, not safety.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a0e4f589237f5b88c`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28e/a26d agent, 2026-10-22, candidates 3 and 4 of its hand-back)

## Evidence

At `20ef8984`, `Components/Pages/ModuleConfig.razor`, the Editable Attributes table: `AllowlistRow`
has a `Pattern` member filled from the loaded list and written back on save, with no input bound
to it. `Services/ADAttributeEditorService.cs`: the legacy-file import path writes through the
repository without `SaveAllowlist`, so `DescribeUncompilablePattern` (aud-a28e) does not run for
imported rows.

## Approach (to be written by the implementing agent before code)

- Add a Pattern input to the allowlist row (plain text, optional) so the stored value is visible and
  editable; the aud-a28e save-time check refuses an uncompilable one with the message naming the
  attribute. The onus stays on the admin to enter a correct pattern; the page does not guess or
  auto-fix.
- Route the legacy import through the same compile check; an import holding an uncompilable
  pattern is refused as a whole with the attribute named (fail closed; no partial import).
- Tests: the row renders and saves the pattern; an import with a bad pattern is refused and stores
  nothing. Mutation at the call site: drop the import check.
- A page edit that adds lines invalidates `ClickGateRegistry` entries below it; `ModuleConfig.razor`
  is `NotYetConverted`, so only `ExpectedLineCount` (if present) changes; confirm and follow
  `.agents/playbooks/clickgate-reanchor.md` if any keyed entry exists.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28e/a26d hand-back
(`b38a6861`). One commit `fix(module-config): an allowlist pattern can be seen and edited, and the
legacy import refuses one that does not compile (aud-a26e)`, after aud-a28f on the same branch.
Behaviour: the Module Configuration page (app-shared, base app version) and the AD Attribute
Editor service's legacy import (its module version); the supervisor owns both bumps. Evidence
re-read at `b38a6861`: the Editable Attributes table (`ModuleConfig.razor` 538-588) has no input
for `AllowlistRow.Pattern`, which LoadAllowlist fills (1611-1615) and the save passes back (1697);
`ADAttributeEditorService.ImportLegacyIfPresent` (385-) checks the file with `ValidateRows` (418)
and stores `wrapper.Attributes` through `_repository.ImportAllowlistIfMissing` (435), never through
the aud-a28e compile check.

1. **Page.** A Pattern column - one header, one plain-text input per row after Max Len - with
   `value="@attr.Pattern"` and `@onchange` to a new `UpdateAttrPattern`, which keeps the text as
   typed (empty is no pattern) and counts one edit, as the other cells do. Nothing is guessed or
   fixed: aud-a28f's PatternCheck refusal names the attribute and says an administrator must
   correct the pattern. The Choices sub-row's colspan follows the column count (7 -> 8).
   `ProgressRegistry` lists every handler on this page by name, so `UpdateAttrPattern` joins the
   other cell edits as an `AllowlistRowEdit` exemption.
2. **Message.** `DescribeUncompilablePattern` names the attribute (label and LDAP name) and the
   parser's reason; the allowlist save adds that it was not saved and an administrator must correct
   the pattern. One wording for SaveAllowlist's throw and the audited refusal.
3. **Import.** After `ValidateRows` passes, every row of the file is compiled before anything is
   stored. One that does not compile: the error is logged naming the attribute, nothing is
   imported, the file is left in place (not archived) and the allowlist fails closed
   (`_legacyAllowlistCorrupt`), exactly as the import's other validation failures do. Whole file or
   nothing.
4. **Registries.** `ClickGateRegistry` holds no `ModuleConfig.razor` entry or `ExpectedLineCount`
   (`NotYetConverted`), so the added lines move nothing; no re-anchor.

Tests. `ADAttributeEditorServiceTests`: a legacy file holding `[` on one row -> GetAllowlist null,
IsAllowlistCorrupt true, the store holds nothing and is not marked configured, the file is still in
place, and the error logged names the attribute; a legacy file holding a valid pattern -> imported
with it, file archived. Round trip: `AllowlistSaveAuditTests` - a pattern entered on a row is saved by
SaveAllowlistAndAudit, read back by GetAllowlist, and recorded under `changed`; page tripwire (no
bUnit): the row's input carries `attr.Pattern` to `UpdateAttrPattern`, the handler writes the row's
Pattern, LoadAllowlist fills it and the save passes it into the attribute it saves. Conditions
asserted, not wording.

Mutation at the call site: the import's compile check deleted. Extra: the page handler made to drop
what was typed.

## Guard proof

Pending.
