# aud-a26e: Allowlist patterns cannot be seen or edited on the page, and the legacy import bypasses the pattern check

**Severity**: LOW - no wrong write, but an admin has no way to see or correct an attribute's
validation pattern on the Module Configuration page (the row only round-trips the stored value),
so a refused save under aud-a28e can only be cleared by deleting and re-adding the row, which
silently drops the pattern. The legacy `ad-editable-attributes.json` import writes rows straight to
the repository and so skips the aud-a28e save-time check; the validation-time catch still refuses
at save, so the gap is operability, not safety.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
