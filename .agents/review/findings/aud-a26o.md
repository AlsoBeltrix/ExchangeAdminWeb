# aud-a26o: Attribute editor - a preview race can target the wrong event, ComputeChanges is ordinal, a JSON-null side reads as the text "null", the preview path is unrouted and unseamed, and the pre-write re-read mishandles multi-valued attributes

**Severity**: LOW. (1) `ShowUndoPreview` is not guarded while a preview is still loading; two previews
requested back to back can finish out of order, leaving one event's preview on the panel against
another event as the target - `ExecuteUndo` would undo the target event while the in-session
"reversed" marker uses the other preview's operation id. (2) `ComputeChanges` compares ordinal, so an
edit that differs from the current value only in form ("TRUE" against "True") is written and recorded
as a change whose old and new values read the same. (3) `ADAttributeEditorUndoService.GetString`
returns the text "null" for a JSON-null value; a hand-made record with a JSON-null side would make
the undo write that string (latent: the writer drops nulls). (4) The preview's `ReReadCurrentValues`
builds its own runspace outside the test seam and stays unrouted, so it cannot be tested through the
harness and may ask a different DC than the save (aud-a26l routed the save chain). (5) `PerformSave`'s
pre-write re-read uses `.Value?.ToString()`, so a collection-valued attribute reads as its type name
while the read-back takes its element; v1 excludes multi-valued attributes but the allowlist does not
enforce that.
**Status**: Merged to master (`5e2b80f3`); closed on guard proof; codex review pending
**Branch**: `worktree-agent-aa41f72251493711c`
**Commit**: `e4fa78ad` (fix, tests, record; comments `d4e69a27`; plan `9301c34b`); merged `5e2b80f3`
**Reviewer**: implementer (surfaced by the aud-a26l/a26m/a28o agent, 2026-10-22, candidates 1, 2, 3, 5 and 6 of its hand-back)

## Evidence

At `fee8a58b`: `Components/Pages/AdminEventLog.razor` `ShowUndoPreview` (no loading guard; a
completed later preview overwrites an earlier one's target); `Services/ADAttributeEditorService.cs`
`ComputeChanges` (ordinal), `PerformSave` pre-write re-read (`.Value?.ToString()`); `Services/ADAttributeEditorUndoService.cs`
`GetString` ("null"), `ReReadCurrentValues` (own runspace, no `-Server`).

## Approach (to be written by the implementing agent before code)

- Preview: a loading guard (`undoPreviewLoading`), and the preview result applied only if the target
  event is still the one it was requested for (compare the captured event id).
- `ComputeChanges` compares with the aud-a26l syntax rules so a form-only difference is not a change
  (and the audit does not record a non-change).
- `GetString` returns null for a JSON-null; a record with a null side reads as "not set".
- The preview re-read goes through the editor service's runspace seam and the aud-a26l routing
  (`ServerFromDn` of the event's target DN), so it can be tested and asks the same domain as the save.
- The pre-write re-read reads a one-element collection as its element and refuses a multi-valued
  attribute with a message naming it (v1 scope), or the allowlist save refuses adding one - say which.
- Tests through `AttributeEditorHarness` and `EventLogUndoRefusalTests`; mutations at the call site for
  each part.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a26l/a26m/a28o hand-back (`8f831a0d`).
One commit `fix(event-log,ad-attribute-editor): ... (aud-a26o)`, third of three on branch
`worktree-agent-aa41f72251493711c` (after aud-a28q and psl-7). Behaviour: Admin Event Log (the preview guard) and
AD Attribute Editor (the other four parts) - both module versions; the supervisor owns the bumps. Evidence re-read
at `89096ac9` (the line numbers are that tree's; psl-7 moves the editor's below its imports):

1. `Components/Pages/AdminEventLog.razor` ShowUndoPreview 910-955: refused only while an undo runs (916); it sets
   the target (919) and `undoLoading` (920), awaits the auth state and the handler (941-942), and assigns
   `undoPreview` whatever the target has become - another ShowUndoPreview, or CloseUndoPanel (957-972), can change
   it meanwhile. `ClickGateRegistry.cs` 87: NotYetConverted ("tier 4, not approved"), no line-keyed entry, so
   nothing re-anchors; `ProgressRegistry` pins `handler.PreviewUndoAsync` inside the handler's window.
2. `Services/ADAttributeEditorService.cs` ComputeChanges 1792-1810: `string.Equals(oldNorm, newValue, Ordinal)`;
   its callers PerformSave (1418) and ComputePreview (1321).
3. `Services/ADAttributeEditorUndoService.cs` GetString 533-547: a JsonElement that is not a string returns
   `GetRawText()` - "null" for a JSON null - read for the recorded sides by PreviewUndoAsync (130-131),
   ExecuteUndoAsync (263, 290) and LogUndoAudit (482-483).
4. `ReReadCurrentValues` lives in `ADAttributeEditorService` (1746-1790), not the undo service as the evidence line
   above has it: its own runspace (1753-1756) and no `-Server` (1766-1770). Its one caller, ComputePreview, is
   reached from PreviewAsync (999-1024), which takes its credentials from `_moduleCredentials` (1009), not the
   `GetCredentialsAsync` seam - so the harness cannot reach it. PreviewAsync has no caller in the app today (`git
   grep PreviewAsync`): a public path no page uses yet.
5. PerformSave's pre-write re-read: `reReadUser.Properties[attr.Name]?.Value?.ToString()` (1416), the preview's the
   same (1782); the read-back's `ReadDirectoryValue` (1725-1742) takes a one-element collection's element and joins
   several.

**Rule.** The panel's preview is its own event's; a change is a difference the read-back would see; a recorded side
the writer would have dropped reads as not set; the preview asks what the save asks, where the save asks it; and an
attribute holding several values is never written as one.

1. ShowUndoPreview: `if (undoExecuting || undoLoading) return;` - `undoLoading` is already the preview's loading
   flag (the record's "undoPreviewLoading"), so no new field - and after the awaits the result, or the catch's error
   preview, is assigned only while `ReferenceEquals(undoTargetEvent, evt)`: the entry the preview was requested
   for, not its operation id, which can be blank and is not the entry. An answer for an entry that is no longer the
   target is dropped. Both before `Progress.Begin` / inside its window as now.
2. ComputeChanges takes the re-read's values as read (`IReadOnlyDictionary<string, object?>`) and keeps a proposed
   value as a change only when `ReadsAsWritten(SyntaxOf(attr.Type, attr.Name, read), proposed, current, read)` is
   false - aud-a26l's rules, so "TRUE" against a Boolean True, a DN in another case and spacing, and a string
   differing only by leading or trailing whitespace are not changes. It stays private: the aud-a28l tripwire takes
   any non-private method handed allowlist rows for a writer.
3. GetString: a JsonElement of kind Null or Undefined is null.
4. ReReadCurrentValues opens its session through `CreateDirectoryRunspace()` and sends
   `-Server GroupManagementService.ServerFromDn(target.DistinguishedName)` - the save's server, with the save's
   warning for a DN naming no domain; PreviewAsync takes its credentials through `GetCredentialsAsync`. It returns
   the object read, and ComputePreview reads its values as PerformSave does.
5. The pre-write re-read and the preview's read each attribute through `ReadDirectoryValue`, so a one-element
   collection is its element. An attribute the save would write that holds several values refuses the save before
   the write: a failed `ADAttributeEditor_Update` naming it ("'<Label>' (<name>) holds N values in the directory;
   the attribute editor writes single-valued attributes only. Nothing was written."), announced as every result
   is; the preview returns the same refusal. Chosen over refusing it in the allowlist save: the save cannot tell a
   multi-valued attribute without reading the schema, a directory read it does not make, and one holding a single
   value is safely written as one.
6. Comments after the mutation round, checked code-identical.

Tests. `EventLogUndoRefusalTests`, a held stand-in handler in a real `UndoRegistry`: (a) a preview requested while
one loads is refused, and the first is shown for its own event; (b) a preview that finishes after its panel was
closed is dropped. `ADAttributeEditorSaveTests` through `AttributeEditorHarness`: (c) a form-only edit - a Boolean
"FALSE" against False, a manager DN in another form, a title with surrounding spaces - is no change: nothing
written, the no-change record; (d) the preview re-read through the session seam, to the object's domain, and for
a DN naming none unrouted with the warning; (e) an attribute holding several values refused by name, nothing
written, by the save and by the preview; (f) one read as a one-element collection is its element - the recorded
old value, and confirmed. (g) A record whose old side is a JSON null: the preview's revert-to is not set and the
undo clears the attribute instead of writing "null".

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched, touched, rebuilt,
green): M5 a stale preview applied again (the target check dropped); M6 ComputeChanges ordinal again; M7 GetString
returning "null" again; extras E4 the loading guard dropped, E5 `-Server` dropped from the preview re-read, E6
PreviewAsync's credentials from `_moduleCredentials` again, E7 the several-valued refusal dropped, E8 the pre-write
re-read by ToString again. Not run: the preview's runspace back to its own - a real session importing the real
module, a directory probe the owner forbids.

Files: `Components/Pages/AdminEventLog.razor`, `Services/ADAttributeEditorService.cs`,
`Services/ADAttributeEditorUndoService.cs`, `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`,
`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`, this record.

## Fix

- `Components/Pages/AdminEventLog.razor` ShowUndoPreview: `if (undoExecuting || undoLoading) return;` first; the
  handler's answer is assigned only `if (ReferenceEquals(undoTargetEvent, evt))`, and the catch's error preview the
  same. NotYetConverted in ClickGateRegistry, no line-keyed entry, so nothing re-anchors (1,571 lines before, 1,574
  after); the guard returns before `Progress.Begin` and the pinned `handler.PreviewUndoAsync` stays in its window.
- `Services/ADAttributeEditorService.cs`:
  - ComputeChanges takes `IReadOnlyDictionary<string, object?>` - the values as read - and keeps a proposed value as
    a change only when `ReadsAsWritten(SyntaxOf(attr.Type, attr.Name, read), proposed, current, read)` is false.
  - PerformSave's pre-write re-read: `SeveralValuedRefusal` first - a failed `ADAttributeEditor_Update` with its
    message, returned as a failed save, when an attribute the save would write holds several values - then
    `ComputeChanges(proposedValues, CurrentValues(reReadUser, allowlist), allowlist)`.
  - PreviewAsync takes its credentials through `GetCredentialsAsync("attribute preview")`. ReReadCurrentValues opens
    its session through `CreateDirectoryRunspace()`, sends `-Server` from `GroupManagementService.ServerFromDn(
    target.DistinguishedName)` (a warning when the DN names no domain, as the save logs), and returns the values as
    read, or the several-valued refusal; ComputePreview returns that refusal as the preview's error.
  - New private statics beside ReadDirectoryValue: `DirectoryValues` (an attribute's values, read; ReadDirectoryValue
    is now built on it, its answers unchanged), `CurrentValues` and `SeveralValuedRefusal`. All take the allowlist as
    rows and are private, outside the aud-a28l writer tripwire.
- `Services/ADAttributeEditorUndoService.cs` GetString: a JsonElement of kind Null or Undefined is null.

Tests: `EventLogUndoRefusalTests` +3 - `APreviewRequestedWhileAnotherLoads_IsRefused_AndTheFirstIsShownForItsOwnEvent`,
`APreviewThatFinishesAfterItsPanelWasClosed_IsDropped` (its answer a preview, or a failure); a `HeldPreviews` stand-in
handler whose previews wait for the test, and the page's set-up shared by `PageConfirming` and the new
`PageWithoutAPanel`. `ADAttributeEditorSaveTests` +8 - `AnEditThatDiffersOnlyInForm_IsNoChange_AndNothingIsWritten`
(a Boolean "FALSE" against False, the manager DN in another case and spacing, the title with spaces round it; the
preview finds the same), `ThePreview_ReReadsThroughTheSessionSeam_FromTheObjectsOwnDomain`,
`ThePreviewOfADnThatNamesNoDomain_GoesToTheDefaultDomainController_AndSaysSo`,
`AnAttributeHoldingSeveralValues_IsRefusedByName_AndNothingIsWritten` (the save and the preview),
`AnAttributeReadAsAOneElementCollection_IsItsElement_BeforeTheWriteAsAfterIt`,
`ARecordedSideThatIsAJsonNull_ReadsAsNotSet_SoTheUndoClearsTheAttribute`.

Caller-visible change, by design: the event log refuses a second Undo preview while one loads, and drops an answer
for an entry that is no longer the panel's (a closed panel stays closed). An attribute edit that differs from the
directory's value only in form - a Boolean's case, a DN's case and spacing, a string's surrounding spaces - is no
change: nothing is written and the save is recorded as nothing needing changing, as an identical value is. A
recorded side that is a JSON null reads as not set, so an undo clears the attribute instead of writing "null". The
editor's preview (PreviewAsync, which no page calls yet) re-reads through the save's session and server. A save or
preview of an attribute that holds several values is refused, naming it; one read as a one-element collection is its
element before the write as after it.

## Guard proof

No separate pre-fix reproduction: each mutation puts one part back as at `89096ac9`, at its call site - M5 and E9
the two assignments without their target check, E4 the guard without `undoLoading`, M6 the ordinal comparison, E5
and E6 the preview's re-read unrouted and its credentials unseamed, E7 and E8 the pre-write re-read without the
refusal and by ToString. M7's mutant is byte-identical to `ADAttributeEditorUndoService.cs` at `89096ac9` (MD5
`3FFC152A18B26FEE1E9C23A4D8B142BD`, the file at aud-a28o's comments commit), so its failure is the record's own
reproduction of part 3.

Same driver and conditions as aud-a28q (`round-a26o.ps1`, log `a26o-mutations.log`), the filter
`FullyQualifiedName~EventLogUndoRefusalTests|FullyQualifiedName~ADAttributeEditorSaveTests|FullyQualifiedName~ADAttributeEditorUndoRefusalTests|FullyQualifiedName~ADAttributeEditorLookupTests|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~UndoNotificationTests|FullyQualifiedName~UndoIdentityTests|FullyQualifiedName~AttributeEditorPageSaveAuditTests|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~ClickGate|FullyQualifiedName~DirectoryModuleImportTests`
(486 tests). Every mutant built with 0 errors; each restore matched its original MD5. After the round, a rebuild on
the restored bytes and the filter: 486/486 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M5 a stale preview applied again (`undoPreview = preview;` without its target check) | `AdminEventLog.razor`, `5B35C3695508B525655EED7105BAF603` | `8D1DEBE5156C2A93309D39147411982E` | 1 of 486: `APreviewThatFinishesAfterItsPanelWasClosed_IsDropped(previewFails: False)` - the panel reopens with the answer, beside no event |
| M6 ComputeChanges ordinal again (`!string.Equals(oldValue, newValue, Ordinal)`) | `ADAttributeEditorService.cs`, `F91DC378E5BB16F3AD8BF2E2DA4D07A6` | `6182D4D69421ADCCCFB7A2F5517BBCCE` | 3 of 486: `AnEditThatDiffersOnlyInForm_IsNoChange_AndNothingIsWritten`, all three forms - each written |
| M7 GetString returning "null" again (the file as at `89096ac9`) | `ADAttributeEditorUndoService.cs`, `5CB73E0C79682EA89BED4D7DBF1273AC` | `3FFC152A18B26FEE1E9C23A4D8B142BD` | 1 of 486: `ARecordedSideThatIsAJsonNull_ReadsAsNotSet_SoTheUndoClearsTheAttribute` - the preview's revert-to is "null" and the undo writes it |
| extra E4 the loading guard dropped (`if (undoExecuting)`) | `AdminEventLog.razor`, `5B35C3695508B525655EED7105BAF603` | `2CE42A8377A53AA7CD04971B344C5D14` | 1 of 486: `APreviewRequestedWhileAnotherLoads_IsRefused_AndTheFirstIsShownForItsOwnEvent` - the second preview starts and takes the target |
| extra E5 `-Server` dropped from the preview's re-read | `ADAttributeEditorService.cs`, `F91DC378E5BB16F3AD8BF2E2DA4D07A6` | `0BBE1E83415308F6F1BB7EFE57A9C263` | 1 of 486: `ThePreview_ReReadsThroughTheSessionSeam_FromTheObjectsOwnDomain` - read unrouted |
| extra E6 PreviewAsync's credentials from `_moduleCredentials` again | `ADAttributeEditorService.cs`, `F91DC378E5BB16F3AD8BF2E2DA4D07A6` | `2ED697B40B86CA9D493204E04607F506` | 6 of 486: every test that previews - the two preview tests, the three form-only cases and the several-valued one - "AD credentials unavailable." (the harness configures no secret, so this mutant stops before any session) |
| extra E7 the save's several-valued refusal dropped (`string? severalValued = null;`) | `ADAttributeEditorService.cs`, `F91DC378E5BB16F3AD8BF2E2DA4D07A6` | `E973F2B3DBF2F9209373E24C367F2EEA` | 1 of 486: `AnAttributeHoldingSeveralValues_IsRefusedByName_AndNothingIsWritten` - one value written over two |
| extra E8 the values read by ToString again (in CurrentValues) | `ADAttributeEditorService.cs`, `F91DC378E5BB16F3AD8BF2E2DA4D07A6` | `8DF00F8C1B0725D0F7096C2A2E069ED6` | 2 of 486: `AnAttributeReadAsAOneElementCollection_IsItsElement_BeforeTheWriteAsAfterIt` (the type name never equals the value, so an unchanged value is written) and the Boolean form-only case (read as text, a Boolean's syntax is lost) |
| extra E9 a stale preview failure applied again (the catch's target check dropped) | `AdminEventLog.razor`, `5B35C3695508B525655EED7105BAF603` | `4BB607AF7907FBCBB18A45FA97E1616F` | 1 of 486: `APreviewThatFinishesAfterItsPanelWasClosed_IsDropped(previewFails: True)` |

Not run, by design: the preview's runspace back to its own - a real session importing the real module, a directory
probe the owner forbids.

Unchanged through the round: `EventLogUndoRefusalTests.cs` `974A9C133750006EC7EFC3DB8DD591BD`,
`ADAttributeEditorSaveTests.cs` `9A4D0115060052E7E7EBC3742D54E271`. The code comments are written after the round, in
their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none
  in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug): exit 0, 5,604 passed / 3 skipped
  (the three known AD skips) / 0 failed, 5,607 total, 5m19s; nothing hung, no Sequence file. psl-7's tree: 5,593 / 3 /
  0, 5,596 total; the eleven new cases are this record's.
- `ClickGate*` 316/316 and `ProgressRegistryTests` 35/35, each also run alone (Release).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No PowerShell source, real module, directory, Delinea, SMTP, push or deployment. The page was not rendered in a
browser and no live circuit was run: ShowUndoPreview is driven through the compiled page by reflection on a
do-nothing renderer (aud-a28m's harness). No coder self-review verdict is claimed.

## Known gaps

- *Assumption, not probed*: the module answers a multi-valued attribute holding several values as a collection, and
  one holding a single value as that value or a one-element collection. A byte array, being a collection, reads as
  several values and is refused with a count of its bytes - a misleading message for a binary attribute, which the
  editor does not edit; refusing it is the safe side.
- The lookup (PerformLookup, which fills the editor's form and the undo's conflict check) still reads each value by
  ToString, so a one-element collection shows there as its type name; only the save's and the preview's re-reads,
  and the read-back, take its element. Outside this record's five parts; a candidate.
- The event log's preview guard refuses a second preview until the first answers, a closed panel included: an
  operator who closes a loading panel and asks for another preview at once is refused until the first returns.

## Comments after the round

Written after all three rounds, in their own commit (`d4e69a27`), checked comment-only as aud-a28q's record says
(113 lines over eleven files). This record's files: `Components/Pages/AdminEventLog.razor`
`5B35C3695508B525655EED7105BAF603` -> `943DE5D48A8D0864F3A17D3D7F86E7D7` (8 lines: why a preview is refused while one
loads, why an answer is shown only for the entry it was asked for, and the failure the same; 1,574 lines -> 1,582,
still NotYetConverted, no key to move); `Services/ADAttributeEditorService.cs` `F91DC378E5BB16F3AD8BF2E2DA4D07A6` ->
`97BB11FACD3F1BE4BD65C7932FA77807` (33 added, 3 removed, psl-7's two-line note among them: why the preview's
credentials come through the seam, why the pre-write re-read reads as the read-back and refuses several values,
why ComputeChanges judges in the attribute's syntax, why the preview's re-read is seamed and routed, each new
helper and its assumption; the two seams' summaries and ReadDirectoryValue's now name their new callers);
`Services/ADAttributeEditorUndoService.cs` `5CB73E0C79682EA89BED4D7DBF1273AC` -> `1DE1BD0E2B0CA4002D742F213A36C04F` (2:
why a JSON null is not set). The tests carry their comments from their own commit.

## Branch-tip gates - 2026-10-22 (`d4e69a27`: the three fixes and the comments)

As aud-a28q's record: Release build 0 errors / 61 warnings, none in this record's files; the full suite 5,604 / 3 / 0,
5,607 total, 5m23s, the eleven cases here among the branch's fifty-two; `ClickGate*` 316/316 and
`ProgressRegistryTests` 35/35 alone; format, `git diff --check HEAD` and `tools/Test-AsciiOnly.ps1` exit 0.

## Closeout

Fixed on branch `worktree-agent-aa41f72251493711c`, not merged, not pushed. Two module-scoped behaviour changes: the
Admin Event Log (the preview's loading guard and target check) and the AD Attribute Editor (ComputeChanges' syntax
comparison, the undo's JSON-null side, the seamed and routed preview, the pre-write re-read's reading and
several-valued refusal): both module versions are due. No shared code changed here, so no base app bump from this
record. The bumps are the supervisor's, at merge: `Modules/ModuleCatalog.cs` and `ExchangeAdminWeb.csproj` were
outside this brief.
