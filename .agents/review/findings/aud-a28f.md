# aud-a28f: The Module Configuration allowlist save audits no refusal and records every attribute as newly added

**Severity**: MEDIUM - the editable-attribute allowlist decides which directory attributes operators
may write. Its save on the Module Configuration page writes no audit record for any refusal (not
authorised, corrupt store, denylisted attribute name, and since aud-a28e an uncompilable pattern) or
for its exception path, and the one record it does write on success carries an empty "before"
state, so every allowlist save is audited as adding every attribute and removing none. An admin
cannot reconstruct from the Admin Event Log what the allowlist change actually was.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28e/a26d agent, 2026-10-22, candidates 1 and 2 of its hand-back)

## Evidence

At `20ef8984`, `Components/Pages/ModuleConfig.razor`, `SaveAllowlistAsync`: the call to
`LogSettingsChange` passes an empty list as the previous allowlist, so the diff it records is
"all added, none removed" on every save. The method's refusal returns (authorisation, corrupt store,
denylisted name) and its catch write no audit record; the aud-a28e `ArgumentException` from
`ADAttributeEditorService.SaveAllowlist` lands in that catch and is shown as "Failed to save: ..."
with no row. Same class as aud-a28 / aud-a28c / aud-a28d: an attempt that leaves no durable record,
here on an authorization-adjacent store.

## Approach (to be written by the implementing agent before code)

- Load the stored allowlist immediately before the save and pass it as the "before" state, so the
  settings-change record lists real additions, removals and changed rows (pattern, level, flags).
- Every refusal and the exception path write a refusal/failure audit record naming the reason,
  with `mutationAttempted: false` for refusals, through the same audit service the success path
  uses. One record per attempt; no notice change (the Constitution's admin notice rules for
  settings changes stay as they are).
- Tests: page-level or service-level, whichever the existing `ModuleConfig` tests allow without
  live directory: a save with one row removed records exactly that removal; a denylisted name is
  refused and audited; an uncompilable pattern is refused and audited; an exception from the
  repository is audited as a failure. Mutations at the call site: pass the empty list again; drop
  the refusal audit.
- ClickGateRegistry: `ModuleConfig.razor` is `NotYetConverted`, so no line keys move; confirm.

## Guard proof

Pending.
