# aud-a28f: The Module Configuration allowlist save audits no refusal and records every attribute as newly added

**Severity**: MEDIUM - the editable-attribute allowlist decides which directory attributes operators
may write. Its save on the Module Configuration page writes no audit record for any refusal (not
authorised, corrupt store, denylisted attribute name, and since aud-a28e an uncompilable pattern) or
for its exception path, and the one record it does write on success carries an empty "before"
state, so every allowlist save is audited as adding every attribute and removing none. An admin
cannot reconstruct from the Admin Event Log what the allowlist change actually was.
**Status**: Fixed on the branch below, closed on guard proof; not merged; codex review pending
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

## Fix

`Services/ADAttributeEditorService.cs`:

- `SaveAllowlistAndAudit(attributes, performedBy, ip)` returns `AllowlistSaveResult(Success, Error,
  StoreCorrupt)`. One fresh `LoadAllowlistFromDisk(out stored)` - a new overload that also hands
  back the raw rows it read - is both the corruption re-check and the before-state. Three refusals,
  each recorded through `LogAllowlistSaveRefused` directly above its return: `AllowlistConfig`
  (corrupt store, `StoreCorrupt` true), `Denylist`, `PatternCheck` (aud-a28e's
  `DescribeUncompilablePattern`, now a refusal before the write rather than an exception out of
  it). Before the write it builds the rows the write will leave (`RowsAsWritten`: a blank name
  skipped, a repeated name kept once, as the repository's upsert keeps it) and the field-level
  differences (`DescribeChangedRows`); then `SaveAllowlist` writes. The catch records
  `LogAllowlistSaveFailed` with the message the admin is shown and returns a failure. After the
  try, in its own try, `LogSettingsChange(stored names, written names, changed)`; an audit failure
  there is logged and the save still reports success.
- `LogAllowlistSaveRefused` / `LogAllowlistSaveFailed`, public because the page writes two records
  through them: `AuditService.LogSettingsChangeFailed` under `AllowlistAuditSection`, wrapped so an
  audit failure is logged and never thrown; a refusal adds `refusedAt` and
  `mutationAttempted: false`.
- New records `AllowlistSaveResult` and `AllowlistFieldChange` (attribute, field, old, new).

`Services/AuditService.cs`: `LogSettingsChangeFailed(performedBy, ip, section, errorDetail, extra)` -
LogSettingsChange's action and category (now two private constants both methods use), result
Failed, `error`.

`Components/Pages/ModuleConfig.razor`, SaveAllowlistAsync: the authorisation re-check stays and its
refusal calls `LogAllowlistSaveRefused(..., "Authorization", ...)` before returning; the rest is one
`SaveAllowlistAndAudit` call, whose `StoreCorrupt` sets the corrupt banner as the page's own check
did; the catch calls `LogAllowlistSaveFailed`. The page's corruption check, denylist check, direct
`SaveAllowlist` call and its own `LogSettingsChange` are gone - moved, not dropped.

Tests: new `ExchangeAdminWeb.Tests/AllowlistSaveAuditTests.cs` (13 cases: 10 through the harness, 3
page tripwires); `ADAttributeEditorServiceTests` +1
(`SaveAllowlistAndAudit_StoreCorruptedBehindAValidCache_IsRefusedAndNotOverwritten`);
`PageAuthorizationRecheckTests.ModuleConfig_SaveAllowlist_RechecksCorruptionBeforeWrite` rewritten
as `ModuleConfig_SaveAllowlist_WritesOnlyThroughTheRecheckingSave`, since the re-check it pinned
moved into the service; `UsageTelemetryServiceTests` lists the new audit method (11 -> 12); harness:
`SwitchableStore.WritesFail`, the allowlist store exposed as `AllowlistStore`.

Caller-visible change, by design: every allowlist save attempt now leaves exactly one Admin Event
Log row, category AdminSettings, section `ADAttributeEditor:Allowlist` - a refusal or failure as a
Failed row carrying what the admin saw, a success with the real `added`, `removed` and `changed`.
The messages the page shows are unchanged, except that an uncompilable pattern now shows as the
refusal itself rather than "Failed to save: " in front of it. A save that changes nothing also
leaves a row.

## Guard proof

No pre-fix reproduction: the paths this record is about ran in the page, which no test can drive,
and the service method the tests drive did not exist. Each mutation below puts one of the original
defects back at its call site instead.

Session-local driver (`mutate.ps1`, session scratchpad `a28f-a26e-work`): exact bytes saved, one
literal replacement that must match exactly once, Release build with `0 Error(s)` required, filter
`FullyQualifiedName~AllowlistSaveAuditTests|FullyQualifiedName~ADAttributeEditorServiceTests|FullyQualifiedName~PageAuthorizationRecheckTests|FullyQualifiedName~RefusalAuditTests`
(103 tests), exact bytes restored in `finally`, MD5 re-checked, mtime advanced so MSBuild rebuilds.
Fixed-file MD5s: `Services/ADAttributeEditorService.cs` `7C2B979F4861877A96EACDD2AD8EAC63`,
`Components/Pages/ModuleConfig.razor` `434C5FF2B24221A477551BA5659961C8`; each restored to its MD5
after every mutant, and every mutant built with 0 errors. After the round, a rebuild on the restored
bytes and the filter: 103/103 green.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| the before-state passed as empty again (`stored.Select(r => r.Name).ToArray()` -> `Array.Empty<string>()`) | `B5545B435CB6726F45D6D89692F0B890` | `ARowRemoved_IsRecordedAsExactlyThatRemoval` (4 of 103 failed: also ARowAdded, ARowChanged, ASaveThatChangesNothing) |
| the Denylist refusal's `LogAllowlistSaveRefused` deleted | `DC77B0FF43BB570A62F16ED27389D6FD` | `ADenylistedName_IsRefused_Audited_AndNothingIsWritten` (and ARefusal_IsFiledBesideTheChangesItWouldHaveMade) |
| the catch's `LogAllowlistSaveFailed` deleted | `35C4A2D1343EE7BF450E14B2C964BF53` | `AWriteThatThrows_IsAuditedAsAFailure_AndTheStoreIsUnchanged` |
| extra, page: the authorisation refusal's `LogAllowlistSaveRefused` deleted | `ADADF33A2D4055CCA19F2AF0EF827CA4` | `ThePageAuthorisationRefusal_IsAuditedBeforeItReturns` |
| extra, page: the catch's `LogAllowlistSaveFailed` deleted | `270CB6D8B2F291BFE2BB31A182E4E12B` | `ThePageCatch_AuditsTheFailure` |
| extra: the corruption gate decided by the cached list (`... is var _ && GetAllowlist() == null`) | `EDBCCB30E4E4738A91792D04FE93580E` | `SaveAllowlistAndAudit_StoreCorruptedBehindAValidCache_IsRefusedAndNotOverwritten` |

The harness's corrupt-store case (`ACorruptStore_IsRefused_Audited_AndNotSavedOver`) stayed green
under the last mutant, as it must: its corruption invalidates the cache, so a cached gate sees it
too. The stale-cache case in `ADAttributeEditorServiceTests` is the one that pins the fresh read.

The code comments are written after the round, in their own commit, so the MD5s above are this
commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings (full rebuild), all
  pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: exit 0, 5,100 passed /
  3 skipped / 0 failed, 5,103 total, 5m35s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. No coder self-review
verdict is claimed.

## Known gaps

- The page's two records (the authorisation refusal and its catch) are pinned by source tripwires,
  not driven: no bUnit host exists. Everything after the re-check is driven.
- The catch is reachable only from the authorisation re-check (the auth-state, policy and
  module-admin calls), which was reasoned from the code, not driven.
- The refusals are audited, not announced: settings saves on this page send no administrator
  notice, which is unchanged (the record's approach said no notice change).
