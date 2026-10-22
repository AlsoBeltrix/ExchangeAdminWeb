# aud-a28l: The event log's undo authorization refusal writes no record, and the public SaveAllowlist has no caller, no version check and no record

**Severity**: MEDIUM - (1) `AdminEventLog.razor` `ExecuteUndo` refuses "Authorization denied." and
writes no audit record: an attempt on the authorization-adjacent undo path that leaves no trace (the
aud-a28g class; aud-a28d audited the undo SERVICE's refusals, not the page's). (2) After aud-a28i,
`ADAttributeEditorService.SaveAllowlist` has no production caller; it still writes the allowlist
with no version check and no audit record, so any future caller bypasses both guards.
**Status**: Fixed on branch; closed on guard proof; awaiting merge and review
**Branch**: `worktree-agent-ad10b5ab24c8ce164`
**Commit**: `5dc2e6e4` (comments `ee00d337`; plan `1aeaa315`)
**Reviewer**: implementer (surfaced by the aud-a28g/h/a26f/a28i agent, 2026-10-22, candidates 6 and 10 of its hand-back)

## Evidence

At `f1b4692d`: `Components/Pages/AdminEventLog.razor` `ExecuteUndo`, the authorization refusal
return; `Services/ADAttributeEditorService.cs` `SaveAllowlist` (public; `git grep` shows tests as
its only callers).

## Approach (to be written by the implementing agent before code)

- Undo page refusal: record it through the aud-a28d undo-record shape (`refusedAt: Authorization`,
  `mutationAttempted: false`, `originalOperationId` set so aud-a26c's rule still does not mark the
  edit Reversed because the record is not a success) via a non-throwing helper on the undo service;
  exactly one record per attempt. `ClickGateRegistry`: check `AdminEventLog.razor`'s status; re-anchor
  per the playbook if it has line-keyed entries.
- `SaveAllowlist`: make it private (or remove it) so the only write path is `SaveAllowlistAndAudit`;
  move the tests that used it to the audited path; a tripwire test asserts no public allowlist write
  bypasses the version check.
- Tests: an unauthorised undo leaves exactly one refusal record and the edit keeps its Undo button;
  the reflection tripwire. Mutations at the call site: drop the undo refusal record; make
  `SaveAllowlist` public again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28g/h/a26f/a28i
hand-back (`374718b3`). One commit `fix(event-log,ad-attribute-editor): an undo the event log refuses
for authorization is audited, and the allowlist's unversioned public save is gone (aud-a28l)`, first
of three on this branch. Behaviour: Admin Event Log (the page) and AD Attribute Editor (its undo and
editor services); the supervisor owns the version bumps. Evidence re-read at `374718b3`:
`Components/Pages/AdminEventLog.razor` ExecuteUndo 957-1050 - the handler is found at 974, and the
re-check 987-993 sets `undoError = "Authorization denied."` and returns with no record;
`Services/ADAttributeEditorService.cs` SaveAllowlist 407-426 - public, called by no production code
(the Module Configuration save goes through SaveAllowlistAndAudit) and by five tests
(ADAttributeEditorServiceTests 451, 806, 834; ADAttributeEditorPatternTests 110, 124).

1. **The undo refusal is recorded.** `IUndoableModule` gains `LogUndoRefused(auditEvent,
   performedBy, ip, ticket, refusedAt, reason)`: an undo its caller refused before calling
   ExecuteUndoAsync, recorded as the module's own refused-undo record, never thrown. On the
   interface because the page holds the handler only as one (Program.cs 362 registers the undo
   service only as `IUndoableModule`), the record's shape is the module's, and a later undoable
   module must then write one rather than nothing. `ADAttributeEditorUndoService.LogUndoRefused` is
   the existing `LogUndoAudit` with `refusedAt` - aud-a28d's record: `ADAttributeEditor_Undo`,
   Failed, the reason as `error`, `originalOperationId` the event's `operationId`,
   `changedAttributes` and the reversed old/new pairs the event names, `refusedAt`,
   `mutationAttempted: false`, target `(none)` when the event names none - inside a catch that logs
   an audit failure. Page: the refusal calls `handler.LogUndoRefused(undoTargetEvent.RawFields,
   userName, clientIp, undoTicket.Trim(), "Authorization", undoError)` directly above its return;
   `userName` and `clientIp` are read before the re-check rather than after it (same values).
   Exactly one record per attempt: the refusal returns before ExecuteUndoAsync, which records
   everything it reaches. Failed, so aud-a26c's rule leaves the edit's Undo button. Not announced:
   the page never announced this refusal (aud-a28d's known gap stands).
2. **SaveAllowlist is removed**, not made private: a private method with no caller is dead code.
   SaveAllowlistAndAudit is the service's only allowlist writer; aud-a28e's uncompilable-pattern
   throw goes with SaveAllowlist, the audited PatternCheck refusal being the check that runs. The
   tests move to the audited path: ADAttributeEditorServiceTests' cache-invalidation and
   persist-and-read-back tests save through SaveAllowlistAndAudit with the version their load
   returned; its denylisted-row test plants the row through the repository - the audited save
   refuses one (AllowlistSaveAuditTests), so only an out-of-band write can store it - and still
   asserts GetAllowlist strips it; ADAttributeEditorPatternTests' two admin-side tests save through
   SaveAllowlistAndAudit, refused PatternCheck with the store unchanged, and the valid pattern
   stored. The pattern-wording comment that names SaveAllowlist's throw is corrected.
3. **Tripwire**, in AllowlistConcurrentSaveTests (the version check's home): (a) reflection - every
   non-private method of ADAttributeEditorService with a parameter that is a collection of
   `EditableAttribute` or `AttributeRow` is SaveAllowlistAndAudit, and it takes `loadedVersion`;
   (b) source - in `Services/`, `Components/`, `Modules/` and `Program.cs` the store's whole-table
   writers (`.SaveAllowlist(`, `.ReplaceAllowlist(`) are called only from SaveAllowlistAndAudit, so
   nothing bypasses it through the repository either (a DI singleton, Program.cs 80).
4. **Registries.** ClickGateRegistry: AdminEventLog.razor is NotYetConverted ("tier 4, not
   approved"), with no line-keyed entry and no ExpectedLineCount - confirmed at `374718b3`; nothing
   re-anchors. ProgressRegistry: ExecuteUndo's pin (`handler.ExecuteUndoAsync`) unchanged; no handler
   added.

Tests. New `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs` drives ExecuteUndo itself: the
compiled page, its injected services set by reflection, on a do-nothing renderer (it calls
StateHasChanged), the real undo service over AttributeEditorHarness behind a real UndoRegistry, a
substituted authorization service. Either policy denied (EventLog, UndoAuditedActions) -> exactly one
record, the refusal above, its `error` what the page showed, the operator's user, ip and ticket; no
directory session, nothing written, no notice; the edit not marked Reversed - not in the page's
in-session set, and not by the rule over the records the log now holds (ReversedOperations.From, as
LoadEvents applies it). `LogUndoRefused` with no audit writer logs and does not throw. The tripwire
above. Conditions asserted, not wording.

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched,
touched, rebuilt, green): M1 the page's `handler.LogUndoRefused(...)` deleted; M2 SaveAllowlist
restored, public, as at `374718b3`; extra E1 the helper's catch removed.

Files: `Services/IUndoableModule.cs`, `Services/ADAttributeEditorUndoService.cs`,
`Services/ADAttributeEditorService.cs`, `Components/Pages/AdminEventLog.razor`,
`ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs` (new), `AllowlistConcurrentSaveTests.cs`,
`ADAttributeEditorServiceTests.cs`, `ADAttributeEditorPatternTests.cs`, this record.

## Fix

`Services/IUndoableModule.cs`: `LogUndoRefused(auditEvent, performedBy, ip, ticket, refusedAt,
reason)` - an undo the caller refused before calling ExecuteUndoAsync, recorded as the module's own
refused-undo record; never throws.

`Services/ADAttributeEditorUndoService.cs`: `LogUndoRefused` calls the existing `LogUndoAudit` with
`refusedAt` - aud-a28d's record (`ADAttributeEditor_Undo`, Failed, the reason as `error`,
`originalOperationId`, the event's changed attributes with their values reversed, `refusedAt`,
`mutationAttempted: false`), target `(none)` when the event names none - inside a catch that logs an
audit failure.

`Components/Pages/AdminEventLog.razor`, ExecuteUndo: the authorization refusal calls
`handler.LogUndoRefused(undoTargetEvent.RawFields, userName, clientIp, undoTicket.Trim(),
"Authorization", undoError)` directly above its return; `userName` and `clientIp` are read before the
re-check instead of after it. Net zero lines (1466 before and after); the page is NotYetConverted in
ClickGateRegistry, so there is no line key to move.

`Services/ADAttributeEditorService.cs`: `SaveAllowlist` removed, with aud-a28e's throw; the
pattern-wording remark no longer names it.

Tests: new `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs` (3 cases: the refusal under either
policy; the helper with no audit writer); `AllowlistConcurrentSaveTests` (the two tripwires);
`ADAttributeEditorServiceTests` (three moved - two save through SaveAllowlistAndAudit with the version
their load returned, the denylisted row is planted through the repository) and
`ADAttributeEditorPatternTests` (two moved to SaveAllowlistAndAudit).

Caller-visible change, by design: an undo the event log refuses for authorization now leaves one
`ADAttributeEditor_Undo` record, Failed, `refusedAt: Authorization`. The message the operator sees, the
absence of a notice and the edit's Undo button are unchanged. Nothing in the app called SaveAllowlist.

## Guard proof

No separate pre-fix reproduction: M1 below is the page's refusal as it was (moving `userName` and
`clientIp` above the re-check changes no value), and M2 puts the removed method back exactly.

Session-local driver (`mutate.ps1`, session scratchpad `a28l-a26g-a26i-work`, copied from the
a28g/h/a26f/a28i round): exact bytes saved, one literal replacement that must match exactly once,
Release build with `0 Error(s)` required, filter
`FullyQualifiedName~EventLogUndoRefusalTests|FullyQualifiedName~ADAttributeEditorUndoRefusalTests|FullyQualifiedName~AllowlistConcurrentSaveTests|FullyQualifiedName~AllowlistSaveAuditTests|FullyQualifiedName~ADAttributeEditorPatternTests|FullyQualifiedName~ADAttributeEditorServiceTests|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~PageAuthorizationRecheckTests|FullyQualifiedName~UndoNotificationTests|FullyQualifiedName~PageProtectionGateServicingTests`
(153 tests), exact bytes restored in `finally`, MD5 re-checked, mtime advanced so MSBuild rebuilds
(log `a28l-mutations.log`). Every mutant built with 0 errors; each restore matched its original MD5.
After the round, a rebuild on the restored bytes and the filter: 153/153 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named test failing |
|---|---|---|---|
| M1 the page's `handler.LogUndoRefused(...)` deleted | `AdminEventLog.razor`, `5E39AE7D8BCEBA8FA6992C34141DD0E0` | `0D4ECABFA139000BF13C2083F84041F3` | `AnUndoRefusedForAuthorization_LeavesOneRefusalRecord_AndTheEditKeepsItsUndo`, both policies (no record) |
| M2 `SaveAllowlist` restored, public, as at `374718b3` | `ADAttributeEditorService.cs`, `1E356AF53434EFE7A1E4C68AEAC046F2` | `8896263BAF23ACFE537386C86FDDC378` | `TheServicesOnlyAllowlistWriter_IsTheSaveThatChecksTheLoadedVersion`, `NoProductionCode_ReplacesTheStoredAllowlist_OutsideTheSaveThatChecksTheLoadedVersion` |
| extra E1 the helper's catch removed | `ADAttributeEditorUndoService.cs`, `151479F88A4D6F52F3D5866020B93F7C` | `AA537784221A62F9B22A51228E3EC3E8` | `ARefusalWhoseRecordCannotBeWritten_IsLogged_NotThrown` |

Unchanged through the round: `Services/IUndoableModule.cs` `CAC44BBC08F98BB9CB2C2A9564C7D4A6`;
`EventLogUndoRefusalTests.cs` `520E6FBE6CF751D76D33864AB0AF7E69`, `AllowlistConcurrentSaveTests.cs`
`888B21B023F5E2562CB70C8DA2DFAA43`, `ADAttributeEditorServiceTests.cs` `CBC98E9EE1620532F99438D92AA0143B`,
`ADAttributeEditorPatternTests.cs` `1FB285965930C30D081403CEA88A6CD5`. The code comments are written after
the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings, all pre-existing, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is
  written): exit 0, 5,278 passed / 3 skipped (the three known AD skips) / 0 failed, 5,281 total,
  5m12s; nothing hung, no Sequence file. Baseline at `374718b3`: 5,273 / 3 / 0, 5,276 total; the five
  new cases are this record's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. The page was not
rendered in a browser and no live circuit was run. No coder self-review verdict is claimed.

## Known gaps

- Two exits of ExecuteUndo still leave no record, and neither is this finding: "No undo handler
  available for this event." (no module claims the event, so there is no undo record to write), and
  the page's catch (an exception before ExecuteUndoAsync - the authorization or level calls - is
  logged and shown, the aud-a28g catch class). Named as candidates in the hand-back.
- The refusal is audited, not announced (aud-a28d's known gap: whether a refused undo should notify
  administrators is an owner decision).
- ExecuteUndo is driven through the compiled page by reflection on a do-nothing renderer, not through a
  rendered circuit: the Confirm Undo button's own gating is not exercised.
- `AttributeEditorRepository.SaveAllowlist` stays public: the tests seed the store with it. No
  production code calls it, and the source tripwire refuses one that does.

## Comments after the round

Written after the round, in `ee00d337`, with aud-a26g's and aud-a26i's. Checked by
`check_comment_diff.py` (session scratchpad) over `git diff 4a538d40 ee00d337 -U0`: every added or
removed line is a comment or blank line, so each file's code lines are unchanged and in order. This
record's files: `Services/IUndoableModule.cs` `CAC44BBC08F98BB9CB2C2A9564C7D4A6` ->
`B03D1347375608F9C14053139CD03D82`; `Services/ADAttributeEditorUndoService.cs`
`151479F88A4D6F52F3D5866020B93F7C` -> `D46A574CA9ABB271D9628D59C75B6E6D` (LogUndoRefused's remarks, and the
aud-a28d comment that said the page audits nothing); ExecuteUndo's two comments in
`Components/Pages/AdminEventLog.razor` (the file `A55F8DB9DE66EFF00FDC20707AAF22DF` at `4a538d40` ->
`A75626D42DC54C5F573B0EADAF0CE3B4`, with aud-a26i's); SaveAllowlistAndAudit's remark in
`Services/ADAttributeEditorService.cs` (`35D6C756F2A774CE2D86F18957B63FBC` at `748d358d` ->
`933676D6B9174FFDC8917AA04882D0C7`, with aud-a26g's); `EventLogUndoRefusalTests.cs`
`520E6FBE6CF751D76D33864AB0AF7E69` -> `97D3C76DA55FC320F0B2EA865E37966A`; `AllowlistConcurrentSaveTests.cs`
`888B21B023F5E2562CB70C8DA2DFAA43` -> `416333D54334100046AFBE942FA442B8`. Two stale comments this
record's change made are corrected in the same pass: `PageAuthorizationRecheckTests.cs` (it named
SaveAllowlist as a live route) and `ADAttributeEditorUndoRefusalTests.cs` (it said the event log audits
nothing).

## Branch-tip gates - 2026-10-22 (`ee00d337`: all three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings, all pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is
  written): exit 0, 5,285 passed / 3 skipped (the three known AD skips) / 0 failed, 5,288 total,
  5m22s; nothing hung, no Sequence file. Baseline at `374718b3`: 5,273 / 3 / 0, 5,276 total; the
  twelve new cases are this branch's (aud-a28l five, aud-a26g four, aud-a26i three). The 316
  `ClickGate*` cases are among those passed.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
