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
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan below)
**Branch**: `worktree-agent-aa41f72251493711c`
**Commit**: -
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

## Guard proof

Pending.
