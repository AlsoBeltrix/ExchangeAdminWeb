# aud-a26r: Attribute editor - the lookup still reads values with ToString, a binary attribute gets a misleading refusal, and a closed loading preview blocks the next one

**Severity**: LOW. (1) The editor's lookup still reads attribute values with `ToString`, so a
collection-valued attribute shows its type name in the form and in the undo conflict check (aud-a26o
fixed the pre-write re-read and the preview, not the lookup). (2) A binary (byte-array) attribute is
refused as "holds N values", where N is the byte count: the outcome is safe, the message misleads.
(3) After a still-loading preview panel is closed, a new preview is refused until the first one
returns (the aud-a26o loading guard outlives the panel).
**Status**: Fixed on branch; not merged, not pushed; awaiting review
**Branch**: `worktree-agent-a906aa98bce748ee5`
**Commit**: -
**Reviewer**: implementer (surfaced by the psl-7/a28q/a26o agent, 2026-10-22, candidates 4, 6 and 7 of its hand-back)

## Evidence

At `5e2b80f3`: `Services/ADAttributeEditorService.cs` `PerformLookup` (value reads), the several-values
refusal in the pre-write re-read (`ReadDirectoryValue` on a `byte[]`), the undo conflict check's read;
`Components/Pages/AdminEventLog.razor` `ShowUndoPreview` / `CloseUndoPanel` (`undoLoading` not cleared by
a close).

## Approach (to be written by the implementing agent before code)

- The lookup and the undo conflict check read through `ReadDirectoryValue` (one-element collection as its
  element; several values and binary values named as such).
- A `byte[]` is "binary (N bytes); the attribute editor writes text attributes only", not "holds N values".
- Closing the panel while a preview loads marks the pending preview stale (aud-a26o already drops its
  result) and clears the loading guard so the next preview can start; the dropped result still never lands.
- Tests through `AttributeEditorHarness` and `EventLogUndoRefusalTests`; mutations at the call site:
  `ToString` again in the lookup; the "holds N values" wording for a `byte[]`; the guard left set on close.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28q/psl-7/a26o hand-back (`6fc94167`). One
commit `fix(ad-attribute-editor,event-log): ... (aud-a26r)`, second of two on branch `worktree-agent-a906aa98bce748ee5`
(after aud-a28u). Behaviour: AD Attribute Editor (parts 1 and 2) and Admin Event Log (part 3) - both module versions;
the supervisor owns the bumps. Evidence re-read at `6fc94167`:

1. `Services/ADAttributeEditorService.cs` PerformLookup (1178-1308) reads each allowlisted value as
   `adUser.Properties[attr.Name]?.Value` and keeps its `ToString()` (1300-1305), so a one-element collection is its
   type's name. Those values fill the editor's form and are what both undo conflict checks compare:
   `ADAttributeEditorUndoService` PreviewUndoAsync (92-93, 132-138) and ExecuteUndoAsync (214-215, 264-268) read
   `lookupResult.CurrentValues` and compare it ordinal with the record's new value, which the save's read-back records
   as `ReadDirectoryValue(...)?.ToString()` (UnconfirmedWrites 1612-1614). An edit of such an attribute could never be
   undone - its conflict check met the type name. The undo service needs no change: the lookup is its read.
2. `DirectoryValues` (1751-1762) takes any non-string IEnumerable as a collection, so a `byte[]` is its bytes, and
   `SeveralValuedRefusal` (1778-1795), behind the save's pre-write re-read (1423) and the preview's (1845), refuses one
   as "holds N values", N its byte count. `ReadDirectoryValue` (1738-1747) would join those bytes.
3. `Components/Pages/AdminEventLog.razor` ShowUndoPreview (910-966) is refused while `undoLoading` (920), which only its
   own finally lowers (963); CloseUndoPanel (968-983) clears the target but not the flag, so after a close the next
   preview is refused until the closed one's handler returns. Its answer is dropped by the entry check
   (`ReferenceEquals(undoTargetEvent, evt)`, 950 and 957), which - once a close lowers the flag - a re-request of the
   same entry would pass, landing the closed request's answer on the new panel; and the closed request's finally would
   lower the flag under the new one. `ClickGateRegistry.cs` 87: NotYetConverted, no line-keyed entry, so nothing
   re-anchors; `ProgressRegistry` pins `handler.PreviewUndoAsync` inside the handler's window, which stays.

**Rule.** The lookup reads a value as the save, the preview and the read-back read it, so the form and the undo's
conflict check see what the record holds; a binary value is named as binary - never its bytes counted as values, never
its type's name; and a closed panel's preview neither blocks the next one nor lands on it.

1. `DirectoryValues`: a `byte[]` is one value, as a string is.
2. `ReadDirectoryValue`: a `byte[]` is named "binary (N bytes)" rather than returned, alone or in the several-values
   join; every other answer is unchanged. Nothing compares a binary value - the refusal below stops a save or preview of
   one before ComputeChanges, the write and the read-back - so naming it loses no syntax.
3. `SeveralValuedRefusal` is renamed `NotSingleTextRefusal` and names each proposed attribute that is not one text
   value: several values as now ("'<Label>' (<name>) holds N values in the directory"), a binary value as "'<Label>'
   (<name>) is binary (N bytes) in the directory". Each group is closed by its own rule - "the attribute editor writes
   single-valued attributes only." / "the attribute editor writes text attributes only." - then "Nothing was written.";
   the several-values message is byte-identical to today's.
4. PerformLookup: `currentValues[attr.Name] = ReadDirectoryValue(adUser, attr.Name)?.ToString();` - the read-back's own
   expression.
5. AdminEventLog.razor: a field `undoPreviewRequest` (object?), a new token for each ShowUndoPreview. The answer and the
   error preview are applied only while that token is still the panel's (replacing the entry check, which it implies),
   and the finally lowers `undoLoading` only for the panel's own request. CloseUndoPanel nulls the token - the pending
   preview is stale and its answer dropped - and lowers `undoLoading`, so the next preview starts at once. The guard
   still returns before `Progress.Begin`; the window, with `handler.PreviewUndoAsync` inside it, is unchanged.
6. Comments after the mutation round, checked code-identical.

Tests. `ADAttributeEditorSaveTests` through `AttributeEditorHarness`, its `ReadsAs` typing a value as a collection or a
`byte[]`:
- `AnAttributeReadAsAOneElementCollection_IsItsElementInTheLookup_AndItsEditCanBeUndone`: the lookup's value is the
  element, and the undo of the edit that set it finds no conflict and writes the old value back;
- `SeveralValuesAndABinaryValue_AreNamedInTheLookup`: "555-0100; 555-0101" and "binary (16 bytes)";
- `ABinaryAttribute_IsRefusedAsBinary_AndNothingIsWritten`: the save, its failed record and the preview name it "is
  binary (16 bytes) in the directory; the attribute editor writes text attributes only. Nothing was written.", never
  "holds"; nothing written; and beside a several-valued attribute each is named under its own rule.
`EventLogUndoRefusalTests`, the held stand-in handler:
- `APreviewAfterALoadingPanelWasClosed_Starts_AndTheClosedOnesAnswerIsDropped` (the same entry again, and another):
  after the close the flag is down and the next preview starts; the closed request's answer leaves the new one's
  placeholder and flag alone; a third request while the new one loads is refused; the new answer is shown for its own
  entry.

Mutations at the call site (as aud-a28u's): M3 the lookup by ToString again; M4 the "holds N values" wording for a
`byte[]` again (the binary branch's text); M5 the loading guard left set on close; extras E3 a `byte[]` as its bytes
again in DirectoryValues (the defect's own reproduction), E4 the answer applied by the entry check again, E5 the closed
request's finally lowering the flag unconditionally again.

Files: `Services/ADAttributeEditorService.cs`, `Components/Pages/AdminEventLog.razor`,
`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`, `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`, this
record.

## Fix

- `Services/ADAttributeEditorService.cs`:
  - PerformLookup: `currentValues[attr.Name] = ReadDirectoryValue(adUser, attr.Name)?.ToString();`.
  - DirectoryValues: `if (value is string or byte[]) return [value];` - a byte array is one value.
  - ReadDirectoryValue: one value is `Named(items[0])`, several are joined as `Named(v)?.ToString()`; new private
    statics `Named` (a `byte[]` as `Binary(bytes)`, anything else as itself) and `Binary` ("binary (N bytes)").
  - `SeveralValuedRefusal` renamed `NotSingleTextRefusal`: per proposed attribute, several values into the
    several-valued group as before, a single `byte[]` (`values is [byte[] bytes]`) into a binary group as "'<Label>'
    (<name>) is binary (N bytes) in the directory"; each group joined and closed by its rule, the groups joined with a
    space, then "Nothing was written."; null when neither. Its two callers (PerformSave, whose local is renamed
    `notSingleText`, and ReReadCurrentValues) call it by its new name.
- `Components/Pages/AdminEventLog.razor`: field `private object? undoPreviewRequest;`. ShowUndoPreview, after its
  guard, stamps `var request = new object(); undoPreviewRequest = request;`; the answer and the catch's error preview
  are assigned `if (ReferenceEquals(undoPreviewRequest, request))` (was the entry check), and the finally lowers
  `undoLoading` under the same condition. CloseUndoPanel, after its guard, sets `undoPreviewRequest = null;` and
  `undoLoading = false;`. NotYetConverted in ClickGateRegistry, no line-keyed entry, so nothing re-anchors (1,582
  lines before, 1,588 after); the guard still returns before `Progress.Begin`, and the pinned
  `handler.PreviewUndoAsync` stays inside its window. CloseUndoPanel stays local page state (ProgressRegistry).

Tests: `ADAttributeEditorSaveTests` +3 - `AnAttributeReadAsAOneElementCollection_IsItsElementInTheLookup_AndItsEditCanBeUndone`,
`SeveralValuesAndABinaryValue_AreNamedInTheLookup`, `ABinaryAttribute_IsRefusedAsBinary_AndNothingIsWritten` (with
helpers `Photo`, `PhotoBytes`, `AllowAPhoneListAndAPhoto`, `ReadsThePhotoAsBytes`); `EventLogUndoRefusalTests` +2 -
`APreviewAfterALoadingPanelWasClosed_Starts_AndTheClosedOnesAnswerIsDropped` (the same entry again, and another).

Caller-visible change, by design: the attribute editor's lookup shows an attribute read as a one-element collection
as its element, several values joined ("a; b") and a binary value as "binary (N bytes)" - in the form, and in both
undo conflict checks, so an edit of such an attribute can be undone. A save or preview of a binary attribute is
refused as "is binary (N bytes) in the directory; the attribute editor writes text attributes only. Nothing was
written." instead of as holding one value per byte; the several-values refusal reads as before. In the event log,
closing a panel whose preview is loading lets the next preview start at once; the closed preview's answer is dropped,
and neither fills nor unblocks the new panel.

## Guard proof

Reproduction: M3 is PerformLookup's read as at `6fc94167` (the same expression on one line) and E3 DirectoryValues as
at `6fc94167`, the defect's own reproduction for parts 1 and 2. For part 3 the page as at `6fc94167` is three pieces -
the close leaving the flag up (M5), the answer by the entry check (E4), the finally lowering the flag whatever the
request (E5) - each put back at its call site below, and each fails the tests named; and the whole page as at
`6fc94167` was run too (R6, `repro-a26r.ps1`, log `a26r-repro.log`, the same protocol, the filter
`FullyQualifiedName~EventLogUndoRefusalTests|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~ClickGate`, 367
tests): its MD5 `943DE5D48A8D0864F3A17D3D7F86E7D7` (aud-a26o's comments commit), built with 0 errors, 2 of 367
failing - `APreviewAfterALoadingPanelWasClosed_Starts_AndTheClosedOnesAnswerIsDropped`, both cases: after the close
the next preview is refused. Restored to `9FEB4661573C9F0801D15CD82E1797ED` (matched), rebuilt, 367/367 green.

Same driver and conditions as aud-a28u's (`round-a26r.ps1`, log `a26r-mutations.log`), the filter
`FullyQualifiedName~EventLogUndoRefusalTests|FullyQualifiedName~ADAttributeEditorSaveTests|FullyQualifiedName~ADAttributeEditorUndoRefusalTests|FullyQualifiedName~ADAttributeEditorLookupTests|FullyQualifiedName~ADAttributeEditorServiceTests|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~UndoNotificationTests|FullyQualifiedName~UndoIdentityTests|FullyQualifiedName~AttributeEditorPageSaveAuditTests|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~ClickGate|FullyQualifiedName~DirectoryModuleImportTests`
(570 tests). Every mutant built with 0 errors; each restore matched its original MD5. After the round, a rebuild on
the restored bytes and the filter: 570/570 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M3 the lookup by ToString again (`adUser.Properties[attr.Name]?.Value?.ToString()`) | `ADAttributeEditorService.cs`, `5E2FAF128A45BA8618E0FFDBB66E3469` | `DD40D607D3BE16BC8101802688F99F69` | 2 of 570: `AnAttributeReadAsAOneElementCollection_IsItsElementInTheLookup_AndItsEditCanBeUndone` (the lookup's value is the type's name) and `SeveralValuesAndABinaryValue_AreNamedInTheLookup` |
| M4 the "holds N values" wording for a `byte[]` again (the binary branch's text) | `ADAttributeEditorService.cs`, `5E2FAF128A45BA8618E0FFDBB66E3469` | `54A01A8CBA4F17C76D5C8B059D1FDDF6` | 1 of 570: `ABinaryAttribute_IsRefusedAsBinary_AndNothingIsWritten` - "holds 16 values", the single-valued rule |
| M5 the loading guard left set on close (`undoLoading = false;` dropped from CloseUndoPanel) | `AdminEventLog.razor`, `9FEB4661573C9F0801D15CD82E1797ED` | `A45781DDC9964CA396ACA3865997EA63` | 4 of 570: `APreviewAfterALoadingPanelWasClosed_Starts_AndTheClosedOnesAnswerIsDropped`, both cases (the next preview refused), and aud-a26o's `APreviewThatFinishesAfterItsPanelWasClosed_IsDropped`, both cases - with the request token the closed preview's finally no longer lowers the flag, so a close that leaves it up leaves it up for good |
| extra E3 a `byte[]` as its bytes again (`if (value is string)`) | `ADAttributeEditorService.cs`, `5E2FAF128A45BA8618E0FFDBB66E3469` | `2C32A08F5CB4EA9A47AC48DCFAC85A9D` | 2 of 570: `ABinaryAttribute_IsRefusedAsBinary_AndNothingIsWritten` ("holds 16 values") and `SeveralValuesAndABinaryValue_AreNamedInTheLookup` (the bytes joined as values) |
| extra E4 the answer by the entry check again (`ReferenceEquals(undoTargetEvent, evt)`) | `AdminEventLog.razor`, `9FEB4661573C9F0801D15CD82E1797ED` | `E2EF497276C44A0DA51698E585F2AC76` | 1 of 570: `APreviewAfterALoadingPanelWasClosed_Starts_AndTheClosedOnesAnswerIsDropped(sameEntry: True)` - the closed panel's answer lands on the new panel for the same entry; another entry's is still dropped, as the entry check drops it |
| extra E5 the closed request's finally lowering the flag unconditionally | `AdminEventLog.razor`, `9FEB4661573C9F0801D15CD82E1797ED` | `76234EDE29DE2170DDFDEE666ED9F7C0` | 2 of 570: `APreviewAfterALoadingPanelWasClosed_Starts_AndTheClosedOnesAnswerIsDropped`, both cases - the closed answer lowers the new preview's flag, and a third request would start |

Not run, by design: the module's real answer for a binary attribute. *Assumption, not probed*: the ActiveDirectory
module returns an Octet String attribute (a photo, a certificate) as a `byte[]`, a multi-valued one as a collection of
them; the harness's `ReadsAs` types it so. A directory probe the owner forbids.

Unchanged through the round: `EventLogUndoRefusalTests.cs` `912EF3060DCC5C788503AED207E3194E`,
`ADAttributeEditorSaveTests.cs` `207F6F91910A95517CB0CC8ACC29A797`. The code comments are written after the round, in
their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none
  in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug): exit 0, 5,721 passed / 3 skipped
  (the three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`) / 0 failed, 5,724 total,
  5m13s; nothing hung, no Sequence file. aud-a28u's tree: 5,716 / 3 / 0, 5,719 total; the five new cases are this
  record's.
- `ClickGate*` and `ProgressRegistryTests` inside the round's filter, green on the restored bytes (Release).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No AD, EXO, Graph, DHCP or IIS call, no real module, directory, Delinea, SMTP, push or deployment: the harness's local
stand-ins for the AD cmdlets. The page was not rendered in a browser and no live circuit was run: ShowUndoPreview and
CloseUndoPanel are driven through the compiled page by reflection on a do-nothing renderer (aud-a28m's harness). No
coder self-review verdict is claimed.
