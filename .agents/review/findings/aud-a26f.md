# aud-a26f: Allowlist remediation text points at a JSON file the allowlist no longer lives in; blank and duplicate rows are silently dropped

**Severity**: LOW - no wrong write. The corrupt-store banner, `LoadAllowlist`'s error, the
`AllowlistConfig` refusal message and README line ~245 all tell an administrator to fix
`config/ad-editable-attributes.json`, but the allowlist has lived in the SQLite config database
since the legacy import; an admin following the text edits a file nothing reads. Separately, the
Editable Attributes table drops a row with a blank name or label on save without saying so (since
aud-a28f at least audited as a removal), and accepts two rows with the same LDAP name while the
store keeps only one.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-aa5d4735f85b48351`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28f/a26e agent, 2026-10-22, candidates 3 and 4 of its hand-back)

## Evidence

At `488e535e`: `Services/ADAttributeEditorService.cs` (`LoadAllowlist` error text; the
`AllowlistConfig` refusal wording kept word for word by aud-a28f), `Components/Pages/ModuleConfig.razor`
(corrupt-store banner; the save's row filtering), `README.md` ~245. The store is
`editable_attribute` in the config database (`docs/ConfigDbOperations.md`); the JSON file is only
an import source that is archived after a successful import.

## Approach (to be written by the implementing agent before code)

- Remediation text: name the real store and the real fix - the Editable Attributes table on the
  Module Configuration page, or the `editable_attribute` table per `docs/ConfigDbOperations.md`
  with both pools stopped - and mention the legacy JSON file only where the import itself failed
  (aud-a26e's import refusal is the one place the file is the thing to fix). Update README.
- Rows: refuse a save holding a row with a blank name or label, naming the row (the onus is on the
  admin; nothing is dropped silently); refuse a save holding two rows with the same LDAP name
  (case-insensitive), naming the name. Both are refusals audited through aud-a28f's
  `LogAllowlistSaveRefused` with their own `refusedAt`.
- Tests through `AttributeEditorHarness` / `AllowlistSaveAuditTests`: blank-name row refused and
  audited, store unchanged; duplicate LDAP name refused and audited; the messages name the store,
  not the JSON file (assert the absence of the file name and the presence of the table/page name as
  a condition, since this finding IS about the text). Mutations at the call site: drop the blank-row
  refusal; drop the duplicate refusal.
- `ClickGateRegistry`: `ModuleConfig.razor` is `NotYetConverted`; confirm.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28f/a26e hand-back
(`22833c0d`), with the folded-in item below from codex attr-batch-3 (`a96b1a7c`). One commit
`fix(module-config): allowlist remediation names the config database, a refused legacy import
names its attribute, and blank or repeated rows are refused, not dropped (aud-a26f)`, after
aud-a28h. Behaviour: Module Configuration (app-shared) and the AD Attribute Editor service (module
version); the supervisor owns both bumps. Evidence re-read at `22833c0d`: `ModuleConfig.razor`'s
banner 510-520 and LoadAllowlist's error 1606 name `config\ad-editable-attributes.json`;
SaveAllowlistAsync 1695-1698 drops a row with a blank name or label before the service sees it;
`ADAttributeEditorService.SaveAllowlistAndAudit` 396 (the `AllowlistConfig` refusal) names the file;
RowsAsWritten 495-501 merges a repeated name, as the repository's upsert does;
`ImportLegacyIfPresent` 618-744 returns only a bool (`_legacyAllowlistCorrupt`, 145/171) and names the
attribute only in the log (671-673); README 245 says the allowlist is configured via the file.

1. **Which store.** The corrupt state has two causes and two fixes, and the service can tell them
   apart: the legacy import refused the file (the file is the thing to fix - the one place it is
   named), or the `editable_attribute` table cannot be read or fails validation (corrected per
   `docs/ConfigDbOperations.md` with both application pools stopped; the page's table is disabled in
   this state, so it cannot be the fix). `ImportLegacyIfPresent` returns its refusal reason instead
   of a bool - each of its six refusal branches says why, the uncompilable-pattern one with
   aud-a26e's attribute-naming text and the validation one naming the attribute ValidateRows refused
   (ValidateRows gains an out reason; the load ignores it). The reason is kept beside the flag and
   exposed read-only as `LegacyAllowlistImportRefusal`. Still fails closed; the file stays in place.
2. **Text.** The banner, LoadAllowlist's error and the `AllowlistConfig` refusal each get the two
   variants: legacy - names the file and carries the import's reason (the banner shows it: the
   folded-in item); database - names the `editable_attribute` table, `docs/ConfigDbOperations.md`
   and both pools stopped, and not the file. README 245: the allowlist is edited in the Editable
   Attributes table on the module's Module Configuration page and stored in the config database; the
   legacy file is imported once and archived. IsAllowlistCorrupt's summary, which describes the file,
   is corrected.
3. **Rows.** The page passes every row, trimmed and unfiltered. SaveAllowlistAndAudit refuses, after
   the corrupt-store check and before the denylist: any row with a blank LDAP name or label, naming
   each by its position in the table and what it does hold (`BlankRow`); an LDAP name on more than
   one row, case-insensitive, naming the name and its rows (`DuplicateName`). Each through
   `LogAllowlistSaveRefused` (`mutationAttempted: false`), nothing written. Nothing is dropped or
   merged and nothing guessed; the admin fills in, removes or corrects the row.
4. **Registries.** ClickGateRegistry: `ModuleConfig.razor` NotYetConverted, no line keys or
   ExpectedLineCount - confirmed; nothing re-anchors. No handler added.

Tests. `AllowlistSaveAuditTests`: a blank-name row and a blank-label row -> refused `BlankRow`, one
record, store unchanged, the message names the row; a repeated name (`title`, `Title`) -> refused
`DuplicateName`, one record, store unchanged, the message names the name; a corrupt table -> the
refusal names `editable_attribute` and `docs/ConfigDbOperations.md` and not the JSON file (text
conditions: this finding is about the text). `ADAttributeEditorServiceTests`: a legacy file with an
uncompilable pattern -> `LegacyAllowlistImportRefusal` names the attribute, and the save's refusal
names the file and the attribute. Page, compiled ModuleConfig rendered to its frame list (the
Comms10k render pattern): a refused import's banner names the attribute and the file; a corrupt
table's banner names the table and not the file. Tripwire: the save passes every row unfiltered.
Mutations at the call site: the BlankRow refusal deleted; the DuplicateName refusal deleted; the
banner's import reason deleted (the folded-in item).

## Guard proof

Pending.

## Folded in: the import refusal names the attribute only in the log (codex, attr-batch-3, 2026-10-22)

When the legacy import refuses a file for an uncompilable pattern (aud-a26e), the allowlist loads
as corrupt and the Module Configuration page shows only the generic corrupt banner; the offending
attribute is named only in the application log (`ADAttributeEditorService.cs` around 658/671,
`ModuleConfig.razor` around 1606). Keep the import's validation detail in the service (a safe,
attribute-naming reason beside the corrupt flag) and surface it in the banner, still leaving the
file in place and failing closed. Test: a refused import's reason reaches the page's banner text and
names the attribute; mutation at the call site: drop the detail from the banner.
