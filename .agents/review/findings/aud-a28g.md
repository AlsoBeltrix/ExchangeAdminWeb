# aud-a28g: The attribute editor page's own refusals and its catch write no audit record

**Severity**: MEDIUM - same class as aud-a28 / a28c / a28d / a28e / a28f: an operator's save
attempt that ends with no durable record. `ADAttributeEditor.razor`'s save handler refuses before
calling the service when authorization is denied, when the allowlist is corrupt, and when an
attribute is above the caller's level; none of those writes an audit record. Its catch sends a
notice but writes no record either.
**Status**: Plan written; in progress
**Branch**: `worktree-agent-aa5d4735f85b48351`
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28f/a26e agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `488e535e`, `Components/Pages/ADAttributeEditor.razor`, the save handler: three page-level
refusal returns ahead of `SaveAsync` (authorization re-check, corrupt allowlist, level-unauthorized
attribute) with no `LogRefusal`; the outer catch dispatches a failure notice and returns. The
service (`SaveAndAuditAsync`, aud-a26b/a28b/a28c/a28e) audits every outcome IT reaches; these paths
never reach it.

## Approach (to be written by the implementing agent before code)

- Follow aud-a28f's shape: move what can move into the service so every path is drivable, or add
  two small page-callable service helpers (`LogSaveRefused`, `LogSaveFailed`) that write the
  refusal/failure record (`refusedAt` naming the step, `mutationAttempted: false` for refusals) and
  never throw. The page's authorization re-check stays on the page (it holds the principal) and
  records through the helper before returning; the corrupt-allowlist and level refusals either move
  into `SaveAndAuditAsync` (preferred, if the service can see the same inputs) or record through the
  helper. The catch records a failure through the helper. Exactly one record per attempt; the
  aud-a26b single notice stays single.
- Tests through `AttributeEditorHarness` and the page tripwires used by a28b/a28f: each page-level
  refusal leaves exactly one refusal record and no directory session; the catch path leaves one
  failure record; no attempt leaves zero records. Mutations at the call site: drop each refusal
  record; drop the catch record.
- `ClickGateRegistry`: `ADAttributeEditor.razor` is `NotYetConverted`; confirm. `ProgressRegistry`
  pins unchanged.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28f/a26e hand-back
(`22833c0d`). One commit `fix(ad-attribute-editor): every save attempt on the editor page leaves
exactly one audit record (aud-a28g)`, first of four on this branch. Behaviour: AD Attribute Editor,
page and service (module version; the supervisor owns the bump). Evidence re-read at `22833c0d`,
`Components/Pages/ADAttributeEditor.razor` ConfirmSave 471-607: four page-level refusal returns,
none recorded - no looked-up user 491-496, authorization 527-531, corrupt allowlist 535-539
(`GetAllowlistForLevel` null), a change outside `GetAllowlistForLevel(saveMaxLevel)` 540-546; the
catch 595-601 sends the one notice and writes nothing. The first is not in the finding's list; it is
the same shape (an attempt that ends with no record) and is included so no attempt leaves zero
records. `ADAttributeEditorService.SaveAndAuditAsync` 889-974 already sees the same inputs:
`GetAllowlist()` (the cache the page's call reads), `maxLevel` (the page passes `saveMaxLevel`) and
the proposed names.

1. **Moved into the service where it sees the same inputs.** Corrupt allowlist: SaveAndAuditAsync's
   first refusal (898-904, `AllowlistConfig`) already refuses it, so the page's copy is deleted.
   Level: new refusal `LevelCheck` in SaveAndAuditAsync after the level filter (906-908) and before
   ValidateProposedValues - a proposed name the allowlist holds above `maxLevel` is refused with the
   page's wording ("Access denied: you do not have permission to edit X."), LogRefusal directly above
   the return. A name the allowlist does not hold at all stays the Validation refusal ("not in the
   allowlist"), which is accurate where the page's lumped check said "Access denied". It applies
   whenever a `maxLevel` is passed, so an undo of an attribute above the undoing operator's level is
   now refused as LevelCheck with that wording instead of the Validation wording.
2. **Recorded from the page where it is not.** The authorization re-check and the lookup check stay:
   the page holds the principal and the panel state. Two public service helpers in the aud-a28f
   shape, each logging an audit failure and never throwing: `LogSaveRefused(target, performedBy, ip,
   ticket, refusedAt, reason)` - the `ADAttributeEdit_Refused` record LogRefusal writes, with
   `refusedAt` and `mutationAttempted: false`; `LogSaveFailed(target, performedBy, ip, ticket,
   reason)` - a failed `ADAttributeEditor_Update` with no changes and neither refusal field (the
   aud-a28c catch's record, without its write-completed trace step). Page: the lookup refusal records
   target `(none)` (aud-a28d's precedent) at `Lookup`; the authorization refusal records the UPN at
   `Authorization`; the catch records `LogSaveFailed` with the exception message, beside its notice.
3. **Exactly one record per attempt.** SaveAsync records every result it returns, so the page records
   only on the two returns that never reach it, and in a catch that sees only exceptions SaveAsync
   does not return.
4. **Notices.** The two page-level refusals stay unannounced, as before. The moved corrupt and level
   refusals are now announced once by SaveAsync, as every SaveAsync result is (aud-a26b); the
   catch's notice stays the only one for an exception.
5. **Registries.** ClickGateRegistry: `ADAttributeEditor.razor` is NotYetConverted ("tier 2, not
   approved"), with no line-keyed entry and no ExpectedLineCount - confirmed at `22833c0d`; nothing
   re-anchors. ProgressRegistry: ConfirmSave's pins (`EditorService.SaveAsync(`, the catch's
   `Email.SendAdminNotificationAsync(`) are unchanged; its rationale's sentence about the page's
   GetAllowlistForLevel read is corrected.

Tests. New `ExchangeAdminWeb.Tests/AttributeEditorPageSaveAuditTests.cs` drives ConfirmSave itself -
reflection over the compiled page, the Comms10k/Defender handler pattern, no bUnit - with
AttributeEditorHarness's service and a substituted authorization service: authorization denied and
no looked-up user -> exactly one refusal record each, no directory session, no notice; a corrupt
allowlist and an attribute above the operator's level -> exactly one refusal record each
(`AllowlistConfig`, `LevelCheck`), no session, one notice; an exception out of the authorization
call -> exactly one failure record without refusal fields, and the catch's one notice; a save that
goes through -> exactly one success record. `ADAttributeEditorSaveTests.AnAttributeAboveTheCallersLevel_IsRefused`
expects `LevelCheck`; a new case keeps Validation for a name the allowlist does not hold. Conditions
asserted, not wording. Mutations at the call site: the authorization refusal's `LogSaveRefused`
deleted; the catch's `LogSaveFailed` deleted; extra - the service's LevelCheck refusal deleted.

## Fix

`Services/ADAttributeEditorService.cs`:

- SaveAndAuditAsync: with a `maxLevel`, a proposed name the allowlist holds above that level is the
  new `LevelCheck` refusal - "Access denied: you do not have permission to edit X." (the page's
  wording), LogRefusal directly above the return - before ValidateProposedValues runs against the
  level-filtered list. A name the allowlist does not hold stays the Validation refusal.
- `LogSaveRefused(target, performedBy, ip, ticket, refusedAt, reason)`: the `ADAttributeEdit_Refused`
  record LogRefusal writes (LogRefusal and it now share `WriteRefusal`), target `(none)` when the
  page had no principal. `LogSaveFailed(target, performedBy, ip, ticket, reason)`: a failed
  `ADAttributeEditor_Update` with no changes, the target's GUID when known, and neither refusal
  field. Both public, both log an audit failure and never throw.

`Components/Pages/ADAttributeEditor.razor`, ConfirmSave: the no-principal refusal records
`LogSaveRefused(null, ..., "Lookup", ...)`; the authorization refusal records `LogSaveRefused(target,
..., "Authorization", ...)`; the page's corrupt-allowlist and level checks are gone (SaveAsync makes
both, recorded and announced); the catch records `LogSaveFailed` beside its notice.

Tests: new `ExchangeAdminWeb.Tests/AttributeEditorPageSaveAuditTests.cs` (6 cases, ConfirmSave
driven through the compiled page); `ADAttributeEditorSaveTests`: the level case expects
`LevelCheck`, and a new case pins Validation for a name the allowlist does not hold.

Caller-visible change, by design: every ConfirmSave attempt leaves exactly one audit record. A
refusal for a corrupt allowlist reads "Attribute allowlist configuration is corrupt." (SaveAsync's
wording) where the page said "Attribute allowlist is corrupt.", and it and the level refusal are now
announced to administrators by SaveAsync; the authorization and no-principal refusals are recorded
and, as before, not announced. A save naming an attribute the allowlist no longer holds at all is
refused as "not in the allowlist" where the page said "Access denied". An undo of an attribute above
the undoing operator's level is refused as LevelCheck with the access-denied wording.

## Guard proof

No pre-fix reproduction: the paths this record is about ran only in the page, which no test drove.
Each mutation below puts one of the original defects back at its call site instead.

Session-local driver (`mutate.ps1`, session scratchpad `a28g-a28h-a26f-a28i-work`): exact bytes
saved, one literal replacement that must match exactly once, Release build with `0 Error(s)`
required, filter `FullyQualifiedName~AttributeEditorPageSaveAuditTests|FullyQualifiedName~ADAttributeEditorSaveTests|FullyQualifiedName~RefusalAuditTests`
(29 tests), exact bytes restored in `finally`, MD5 re-checked, mtime advanced so MSBuild rebuilds.
Fixed-file MD5s: `Components/Pages/ADAttributeEditor.razor` `586FDD85B37E14540BD5CD41A4D9BC98`,
`Services/ADAttributeEditorService.cs` `E8C961B64BB4D625F1672B6945327254`; each restored to its MD5
after every mutant, and every mutant built with 0 errors. After the round, a rebuild on the
restored bytes and the filter: 29/29 green.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| page: the authorization refusal's `LogSaveRefused` deleted | `FBED09C22A3D823831E6411BA416BC9F` | `AnAuthorizationRefusal_LeavesOneRefusalRecord_AndOpensNoDirectorySession` |
| page: the catch's `LogSaveFailed` deleted | `7049E3F560A20A97175492AA10EA1577` | `AnExceptionTheSaveDoesNotReturn_LeavesOneFailureRecord_AndTheCatchsOneNotice` |
| extra, page: the no-principal refusal's `LogSaveRefused` deleted | `9D0FF707A6FD9E41B385691388486207` | `ASaveWithNoLookedUpUser_LeavesOneRefusalRecord_AndOpensNoDirectorySession` |
| extra, service: the LevelCheck refusal disabled (`if (false && aboveLevel.Count > 0)`) | `CB9C9BBC5219F3315385CBD295C34E67` | `AnAttributeAboveTheCallersLevel_IsRefused`, `AnAttributeAboveTheOperatorsLevel_IsRefusedOnce_ByTheService` |

Under the last mutant the attribute is still refused - by the Validation step, as "not in the
allowlist" - so the two failures are the wording and step, not a write: the level boundary itself
was never only the page's.

The code comments are written after the round, in their own commit, so the MD5s above are this
commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings (full build), all
  pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx -c Release --no-build --blame-hang --blame-hang-timeout 10m`:
  exit 0, 5,149 passed / 3 skipped / 0 failed, 5,152 total, 5m14s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. The page was not
rendered and no live circuit was run. No coder self-review verdict is claimed.

## Known gaps

- The two refusals that stay on the page (authorization, no looked-up user) are recorded, not
  announced; SaveAsync's refusals are announced. Unchanged by this record: the page never announced
  its own refusals.
- ConfirmSave is driven through the compiled page by reflection, not through a rendered circuit:
  no bUnit host exists, and the Save button's own gating is not exercised.
