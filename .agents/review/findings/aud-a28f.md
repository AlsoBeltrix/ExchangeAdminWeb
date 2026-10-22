# aud-a28f: The Module Configuration allowlist save audits no refusal and records every attribute as newly added

**Severity**: MEDIUM - the editable-attribute allowlist decides which directory attributes operators
may write. Its save on the Module Configuration page writes no audit record for any refusal (not
authorised, corrupt store, denylisted attribute name, and since aud-a28e an uncompilable pattern) or
for its exception path, and the one record it does write on success carries an empty "before"
state, so every allowlist save is audited as adding every attribute and removing none. An admin
cannot reconstruct from the Admin Event Log what the allowlist change actually was.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a0e4f589237f5b88c`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28e/a26d hand-back
(`b38a6861`). One commit `fix(module-config): every allowlist save attempt is audited, with the real
before-state (aud-a28f)`, before aud-a26e on the same branch. Behaviour: the Module Configuration
page (app-shared, base app version) and the AD Attribute Editor service's allowlist save (its module
version); the supervisor owns both bumps. Evidence re-read at `b38a6861`, `ModuleConfig.razor`
SaveAllowlistAsync 1663-1726: refusal returns 1675-1679 (not authorised), 1688-1693 (corrupt
store), 1700-1705 (denylisted name), none audited; `AttrEditorService.SaveAllowlist(valid)` 1707,
whose aud-a28e `ArgumentException` lands in the unaudited catch 1720-1724; the one record,
`LogSettingsChange(..., Array.Empty<string>(), names)` 1714-1716.

1. **The save moves into the service, so every path can be driven.** New
   `ADAttributeEditorService.SaveAllowlistAndAudit(attributes, performedBy, ip)` returns
   `AllowlistSaveResult(Success, Error, StoreCorrupt)` and does not throw. In order: one disk-fresh
   read (`LoadAllowlistFromDisk`, the read `IsAllowlistCorrupt` already makes) that is both the
   corruption re-check and the before-state - the raw stored rows, so a row the load drops is still
   seen to go; null -> refusal `AllowlistConfig`; a denylisted name -> refusal `Denylist`; a pattern
   that does not compile -> refusal `PatternCheck` (the aud-a28e check, now a refusal before the
   write instead of an exception out of it; `SaveAllowlist` keeps its own throw for any other
   caller); the write (`SaveAllowlist`); the success record. Messages unchanged except the
   pattern one (aud-a26e). Everything the success record needs is computed before the write.
2. **Records, exactly one per attempt.** Success: `LogSettingsChange` with the stored names as
   "before" and the written names (a blank name skipped, a repeated name kept once, as the
   repository's insert does) as "after", so `added`/`removed` are real; plus `changed`, one entry
   per field that differs on a row kept (name, label, type, choices, required, allowClear,
   maxLength, pattern, level) with old and new. Written even when nothing differs. In its own try
   after the write: an audit failure is logged, never reported as a failed save. Refusal and
   failure: new `AuditService.LogSettingsChangeFailed` - LogSettingsChange's action, category and
   section, result Failed, what the admin was shown as `error`; refusals add `refusedAt` and
   `mutationAttempted: false` (aud-a28's fields); the exception path adds neither, since the write
   may have been tried. Written through two public service helpers, `LogAllowlistSaveRefused` and
   `LogAllowlistSaveFailed`, which log an audit failure and never throw. `UsageTelemetryServiceTests`'
   every-public-audit-method list gains the new method.
3. **The page keeps the authorisation re-check** (it holds the principal and the policies). Its
   refusal calls `LogAllowlistSaveRefused(..., "Authorization", ...)` before returning; it then calls
   `SaveAllowlistAndAudit` and renders the result (`StoreCorrupt` sets the corrupt banner as before).
   Its catch - reachable only from the re-check, since the service records and returns its own
   outcomes - calls `LogAllowlistSaveFailed`. The page writes no other record. No notification
   change.
4. **Registries.** `ClickGateRegistry`: `ModuleConfig.razor` is `NotYetConverted` ("tier 2, not
   approved"), with no line-keyed entry and no `ExpectedLineCount` - confirmed at `b38a6861`, so
   nothing re-anchors. `ProgressRegistry` is keyed by handler name; SaveAllowlistAsync is reached
   through `SaveCurrentTabAsync`, already exempt as a config-store write.

Tests. New `ExchangeAdminWeb.Tests/AllowlistSaveAuditTests.cs`, through `AttributeEditorHarness`
(its allowlist store gains a write-failure switch): one row removed -> Success and exactly one
record, `removed` that row, no `added`, no `changed`; one row changed (pattern, level, a flag) ->
those fields in `changed`, nothing added or removed; a denylisted name -> refused, store unchanged,
one record (`refusedAt` Denylist, `mutationAttempted` false); an uncompilable pattern -> the same
with PatternCheck, the attribute named; a corrupt store -> refused, StoreCorrupt, AllowlistConfig,
not overwritten; a failing write -> Failed, one record without `refusedAt`, store unchanged; a
refusal and a success filed under the same action, category and section. The page tripwire
`PageAuthorizationRecheckTests.ModuleConfig_SaveAllowlist_RechecksCorruptionBeforeWrite` pinned the
corruption re-check in the page; the re-check moves, so the tripwire becomes "the page writes only
through SaveAllowlistAndAudit", and a behavioural case in `ADAttributeEditorServiceTests` pins the
property itself: a store corrupted behind a valid cached list is still refused and not
overwritten. Page tripwires (no bUnit): SaveAllowlistAsync calls no `Audit.` method; its
authorisation refusal and its catch each call their helper. Conditions asserted, not wording.

Mutations at the call sites: the before-state passed as empty; the Denylist refusal's audit call
deleted; the catch's audit call deleted; extra - the page's authorisation-refusal audit deleted,
and the corruption gate made to read the cached list.

## Guard proof

Pending.
