# aud-a26f: Allowlist remediation text points at a JSON file the allowlist no longer lives in; blank and duplicate rows are silently dropped

**Severity**: LOW - no wrong write. The corrupt-store banner, `LoadAllowlist`'s error, the
`AllowlistConfig` refusal message and README line ~245 all tell an administrator to fix
`config/ad-editable-attributes.json`, but the allowlist has lived in the SQLite config database
since the legacy import; an admin following the text edits a file nothing reads. Separately, the
Editable Attributes table drops a row with a blank name or label on save without saying so (since
aud-a28f at least audited as a removal), and accepts two rows with the same LDAP name while the
store keeps only one.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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
