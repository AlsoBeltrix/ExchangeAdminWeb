# aud-a26r: Attribute editor - the lookup still reads values with ToString, a binary attribute gets a misleading refusal, and a closed loading preview blocks the next one

**Severity**: LOW. (1) The editor's lookup still reads attribute values with `ToString`, so a
collection-valued attribute shows its type name in the form and in the undo conflict check (aud-a26o
fixed the pre-write re-read and the preview, not the lookup). (2) A binary (byte-array) attribute is
refused as "holds N values", where N is the byte count: the outcome is safe, the message misleads.
(3) After a still-loading preview panel is closed, a new preview is refused until the first one
returns (the aud-a26o loading guard outlives the panel).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan below)
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

## Guard proof

Pending.
