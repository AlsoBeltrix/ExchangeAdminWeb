# aud-a26b: notification dispatch for attribute edits belongs to the page, not the operation

**Severity**: MEDIUM. Not a live defect after `aud-a26` - both known callers now notify. It
is the reason `aud-a26` existed, and it is still there.
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-acad2a289bd002e11`
**Commit**: `<pending>`

## Evidence

`ADAttributeEditorService.SaveAsync` performs the write and writes the audit record. It does
not notify. Notification is sent by `Components/Pages/ADAttributeEditor.razor`, at three
points around its own call to `SaveAsync`.

So the rule is "callers must remember", and the undo service did not - which was `aud-a26`.
That fix added a second remembering caller rather than removing the requirement.

## Predicted observable failure

A third caller of `SaveAsync` - a bulk job, a scheduled remediation, an API surface - writes
to the directory and notifies nobody, and nothing in the type system, the tests or the
review checklist asks whether it should have.

## Approach (proposed, not approved)

Move dispatch into `SaveAsync`, which already has the applied changes, the target, the
operator, the ip and the ticket, and remove the page's success and failure calls. The page
keeps its outermost catch, which notifies on an exception `SaveAsync` never sees.

The risk is in the removal, not the addition: a missed call site is a DUPLICATE notification,
which is noisy but harmless, while removing one too many is silent. Both call sites and the
undo service's would need a guard before the move, not after.

`aud-a27` did something adjacent for Emergency Disable but is not a precedent for this - it
changed a recipient and a claim, and left dispatch where it was.

## Known gaps

- Scope is this one service. Whether other modules put dispatch in the page was not
  surveyed, and the same shape is likely elsewhere.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"). One commit
`fix(ad-attribute-editor): ... (aud-a26b)`. Module behaviour: AD Attribute Editor (the
supervisor owns the version bump). Evidence re-read at `ca6575a2`: the page dispatches at
`ADAttributeEditor.razor` 565 (success), 609 (failure) and 624 (outer catch); the undo service
at `ADAttributeEditorUndoService.cs` 282 (save failure), 292 (success) and 316 (catch).

1. **One exit point.** SaveAsync's body moves unchanged into a private `SaveAndAuditAsync`.
   Public `SaveAsync` awaits it and then sends exactly one administrator notice for the result it
   returns - an applied write, a no-op, any of the seven audited refusals, any audited write
   failure - so a refusal added later is announced without anyone remembering. The send runs
   after the AD throttle is released and is logged-and-swallowed on failure, as the page did. An
   exception that escapes the core is not announced by SaveAsync; both callers' catches still do.
2. **Forward notice unchanged in action and rows**: `ADAttributeEditor_Update`; success
   {Target = UPN, Changed}, failure {Target, Error} plus the error. `Changed` names the changes
   actually applied, which SaveAsync knows and the page only approximated with what it asked
   for; a no-op (nothing needed changing) says so instead of naming attributes.
3. **Undo**: new optional `AttributeEditReversal? reversal` on SaveAsync. With it the notice is
   `ADAttributeEditor_Undo` with aud-a26's content (Target, Reverted, Original operation, Error
   on failure), built by one method on the record, which the undo's own catch-path notice also
   uses. ExecuteUndoAsync passes it and drops its two dispatches around the save result; its
   catch keeps its dispatch. No `_Update` notice for an undo, so no duplicate.
4. **Page**: ConfirmSave's success and failure dispatches are removed; the outer catch keeps its
   dispatch. Progress goes from three steps to two, because the notification is no longer a
   separate stage after the write. The page is not in ClickGateRegistry (`NotYetConverted`), so
   no line keys move; ProgressRegistry's ConfirmSave pin on the catch's dispatch still holds and
   its rationale text is corrected.
5. **Seams, no behaviour change**: `internal virtual Runspace CreateDirectoryRunspace()` (used by
   PerformLookup and PerformSave) and `internal virtual GetCredentialsAsync(purpose)` (used by
   LookupAsync and SaveAsync), the shapes of `Comms10kService.CreateReplaceRunspace` and
   `GroupManagementService.GetCredentialsAsync`. Tests substitute a session whose Import-Module,
   Get-ADUser and Set-ADUser are local functions, with module autoloading off; no AD module,
   directory, Delinea or SMTP.

Tests: a service-level save notifies with no page involved (success, refusal, failed write); a
new caller written as a bulk job gets the notice without asking; an undo driven end to end
sends exactly one notice and it is `_Undo` (success and failed write); an unsendable notice does
not fail the save; ConfirmSave's code holds one dispatch and it is in the catch. RefusalAuditTests
follow the body into SaveAndAuditAsync; UndoNotificationTests' three-call scan is replaced by the
end-to-end undo tests. Mutations at the call sites: SaveAsync's dispatch deleted; the page's
success dispatch restored; the undo's `reversal` argument removed; the undo's own success
dispatch restored; the dispatcher's catch made to rethrow.

## Fix

- `Services/ADAttributeEditorService.cs`: `SaveAsync` = `SaveAndAuditAsync` (the old body, byte
  for byte apart from the credential seam call) then `NotifyAdministratorsAsync`, then return.
  New `AttributeEditReversal` record (action constant and the one rows builder) and optional
  `reversal` parameter. Seams `CreateDirectoryRunspace` and `GetCredentialsAsync`; PreviewAsync
  and ReReadCurrentValues are untouched.
- `Services/ADAttributeEditorUndoService.cs`: passes the reversal, drops the save-failure and
  success dispatches, keeps the catch's; its dispatcher builds from `AttributeEditReversal`.
- `Components/Pages/ADAttributeEditor.razor`: the success and failure dispatches are gone, the
  outer catch's stays; `Steps(3)` -> `Steps(2)`, step two reads "writing the attributes to Active
  Directory and notifying administrators". No ClickGate line keys exist for this page.
- Tests: `AttributeEditorHarness.cs` (new; shared with aud-a28b and psl-6),
  `ADAttributeEditorNotificationTests.cs` (new, 9 tests); `RefusalAuditTests` now scan
  `SaveAndAuditAsync`; `UndoNotificationTests` lost its three-dispatch scan to the end-to-end
  undo tests; `ProgressRegistry` ConfirmSave rationale corrected.

Behaviour change, by design and in the plan: the forward notice's `Changed` row names what was
applied, and a save that changed nothing says `(none - the directory already held the requested
values)` where the page named the attributes it had asked for.

## Guard proof

Session-local driver (`acad2a-mutate.ps1` in the session scratchpad): exact bytes saved, one
literal replacement that must match exactly once, Release build with 0 `error CS` required, the
two test classes run (`ADAttributeEditorNotificationTests|UndoNotificationTests`), exact bytes
restored in `finally`, MD5 re-checked, mtime advanced; after the round a rebuild and the same
filter, 14/14 green.

| Mutation (call site) | File | Original MD5 | Mutant MD5 | Named test failing |
|---|---|---|---|---|
| SaveAsync's `NotifyAdministratorsAsync` call deleted | service | `C30973909BB6E08176984458B5A2E0AD` | `57156F0A719B9575667B89696B28C3ED` | `ASave_AnnouncesItself_WithNoPageInvolved` (and 7 more) |
| the page's success dispatch restored | page | `8167F7EC525CAF5F7E064FC4D6CF2C22` | `B9CE161495DA690DF51F8AFD2154AB59` | `TheEditorPage_LeavesTheResultNoticeToTheService` |
| the undo's `reversal:` argument removed | undo | `8F7D2577FA4F2CD01E3F619439E84378` | `6E5D567B996D6B13E62917286DD0DC28` | `AnUndo_IsAnnouncedOnce_AsAnUndo`, `AFailedUndo_IsAnnouncedOnce_AsAFailedUndo` |
| the undo's own success dispatch restored | undo | `8F7D2577FA4F2CD01E3F619439E84378` | `464EA15FBE1FF530605E86DEEE0DE764` | `AnUndo_IsAnnouncedOnce_AsAnUndo` (two notices) |
| `throw;` added to the dispatcher's catch | service | `C30973909BB6E08176984458B5A2E0AD` | `ED6132B2005497F40CCFD6D4BAB8BFDC` | `AnUnsendableNotice_DoesNotFailTheSave` |

Every restore matched its original MD5; every mutant built with 0 errors. The comments were in
place before the round, so the MD5s above are the committed bytes.

## Gates - 2026-10-21

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 40 warnings incremental (61 on a
  full rebuild), all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: exit 0, 4,498
  passed / 3 skipped / 0 failed, 4,501 total, 5m07s. No hang; no filter needed.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the new files staged: exit 0.

No PowerShell source changed (no analyzer or Pester run). No AD module import, directory,
Delinea, SMTP, push or deployment. No coder self-review verdict is claimed.

## Known gaps (after the fix)

- An exception that escapes SaveAsync is still announced only by the caller's catch, and is not
  audited by SaveAsync at all. Out of this finding's scope (it moves dispatch; it does not add
  audit); recorded separately if confirmed.
- No SMTP and no real directory were exercised; the stand-in cmdlets are the evidence.
