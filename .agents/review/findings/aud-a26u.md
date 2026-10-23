# aud-a26u: Attribute editor page - several-valued and binary values are shown as editable text, several binary values are refused under the single-valued wording, and a closed preview's progress bar lingers

**Severity**: LOW - the save is refused before any write in every case; the page misleads. (1)
`ADAttributeEditor.razor` renders a several-valued or binary attribute (named as such by the aud-a26r
lookup) in an editable text field; the operator can type into it and only learns at save time that the
editor writes text attributes only. (2) An attribute holding several binary values is refused with the
several-values wording rather than named as binary. (3) On the Admin Event Log page, a closed undo
preview's progress bar stays visible until its handler returns (cosmetic; aud-a26r cleared the loading
guard, not the bar).
**Status**: Fixed on branch, not merged, not pushed; closed on guard proof; codex review pending
**Branch**: `worktree-agent-aca5b5b4f6295736c` (from `18530b58`)
**Commit**: `2904ec9b` (fix, tests, record); comments `7698c096`; plan `5a6e5a5d`
**Reviewer**: implementer (surfaced by the aud-a28u/a26r agent, 2026-10-22, candidates 2, 3 and 4 of its hand-back)

## Evidence

At `172902c2`: `Components/Pages/ADAttributeEditor.razor` (the value fields; NotYetConverted for
`ClickGateRegistry`); `Services/ADAttributeEditorService.cs` `ReadDirectoryValue` and
`NotSingleTextRefusal` (the `byte[]` single case vs a collection of `byte[]`);
`Components/Pages/AdminEventLog.razor` `CloseUndoPanel` (the bar's visibility condition; NotYetConverted).

## Approach (to be written by the implementing agent before code)

- The page marks a several-valued or binary attribute read-only with the lookup's description in place of
  the input; the save path's refusal stays as the second line of defence.
- `NotSingleTextRefusal` names a collection whose elements are `byte[]` as binary.
- `CloseUndoPanel` hides the bar with the panel.
- Tests through `AttributeEditorHarness` and the page's reflection driver; mutations at the call site: the
  editable input again; the several-values wording for a binary collection; the bar left visible.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the aud-a28u/a26r hand-back (`d886a1f8`). One
commit `fix(ad-attribute-editor,event-log): ... (aud-a26u)`, second of three on branch
`worktree-agent-aca5b5b4f6295736c` (after aud-a28z; cdt-41 follows). Behaviour: AD Attribute Editor (parts 1 and 2)
and Admin Event Log (part 3) - both module versions; the supervisor owns the bumps. Evidence re-read at `18530b58`:

1. `Components/Pages/ADAttributeEditor.razor` 142-168: every allowlisted attribute's "New Value" cell is a select (a
   Choice with choices) or a text input, whatever the attribute holds. The lookup's values (`PerformSearch` 393-395)
   are text - since aud-a26r "a; b" for several values and "binary (N bytes)" for a binary one - so such an attribute
   is offered as editable text and refused only at save, by `NotSingleTextRefusal` (`Services/ADAttributeEditorService.cs`
   PerformSave 1427-1432). The lookup (`PerformLookup` 1304-1310) returns `CurrentValues` only: nothing tells the page
   which attributes are not one text value. NotYetConverted in `ClickGateRegistry` (76: "tier 2, not approved").
2. `NotSingleTextRefusal` (1792-1816) takes `values.Count > 1` as several values (1803-1804) before its binary case
   (1805-1806, a single `byte[]` only), so an attribute holding several byte arrays is refused as "holds N values in
   the directory; the attribute editor writes single-valued attributes only."
3. `Components/Pages/AdminEventLog.razor` `ShowUndoPreview` (913-976) holds its activity in a local (`using var activity`,
   936-937), completed in its finally (969). `CloseUndoPanel` (978-998) clears the panel, the request token and the
   loading flag (aud-a26r) but cannot reach the activity, so "Building undo preview" stays in the status frame until the
   closed request's handler returns. NotYetConverted (`ClickGateRegistry` 87); `ProgressRegistry` exempts
   CloseUndoPanel as local page state (561) and pins `handler.PreviewUndoAsync` inside ShowUndoPreview's window (456).

**Rule.** The editor offers for editing only what it can write: an attribute that holds several values or a binary
value is shown read-only with the reason, and the save's refusal stays behind it. A binary attribute is named binary
however many values it holds. A panel that closes takes its preview's status-bar activity with it.

1. Service. New private `readonly record struct NotText(bool Binary, string Holds)` and private static
   `NotSingleText(PSObject read, string name)`, from `DirectoryValues`: one `byte[]` is binary ("is binary (N bytes) in
   the directory"); several values that are all `byte[]` are binary ("holds N binary values in the directory"); several
   values otherwise are several ("holds N values in the directory"); anything else is null. `NotSingleTextRefusal`
   groups each proposed attribute by it - the several-values and the single-binary messages byte-identical to today's,
   several binary values joining the binary group under "the attribute editor writes text attributes only." The rules
   become two constants. `AttributeLookupResult` gains an optional last member `ReadOnlyAttributes`
   (`IReadOnlyDictionary<string, string>?`, default null, so every construction compiles and reads as before);
   PerformLookup fills it with each allowlisted attribute that is not one text value, by name, its reason "This
   attribute <holds>; <rule>" - for example "This attribute holds 2 values in the directory; the attribute editor
   writes single-valued attributes only."
2. Page. A field `readOnlyAttributes`, reset with the others at the top of PerformSearch and set from the lookup beside
   `currentValues`. The "New Value" cell shows the reason (`<small class="text-muted">`) in place of the select or input
   for an attribute in it. Its edit value stays the lookup's, so it is never a change, and the Reset button never shows
   for it; SaveAsync's refusal stays the second line of defence.
3. AdminEventLog.razor. A field `undoPreviewActivity` (`IActivityHandle?`), set to the activity right after its Begin.
   CloseUndoPanel completes it (`Complete(false, ...)`; Complete is idempotent, so the closed request's own finally then
   completes nothing) and clears it; the finally clears it only while its request is still the panel's - the aud-a26r
   condition that lowers the loading flag - so a closed request never clears the next one's. The guard still returns
   before Begin and `handler.PreviewUndoAsync` stays inside the window. ProgressRegistry's CloseUndoPanel exemption keeps
   its class (no remote, disk or JS call) and says it ends the closed preview's activity.
4. Comments after the mutation round, checked code-identical.

Tests.
- `ADAttributeEditorSaveTests` through `AttributeEditorHarness`: the lookup marks the several-valued and the binary
  attribute read-only with their reasons, and neither a text attribute nor one read as a one-element collection; an
  attribute read as two byte arrays is refused as "holds 2 binary values in the directory; the attribute editor writes
  text attributes only." by the save and the preview, never under the single-valued rule, and nothing is written.
- `AttributeEditorPageSaveAuditTests` (the page's reflection driver; the page's own BuildRenderTree into a
  RenderTreeBuilder, the Comms10k and Defender page tests' shape): the grid for a lookup holding a text, a
  several-valued and a binary attribute renders one value input, and each read-only attribute's reason where its input
  would be.
- `EventLogUndoRefusalTests` (the held stand-in handler): the preview's activity is in the frame while it loads and gone
  when the panel closes though the handler is still held; the next preview's activity is its own - the closed one's
  answer leaves it - and closing that panel takes it too.

Mutations at the call site (md5 before and after, the named failing test, restored, md5 matched, touched, rebuilt,
green): M1 the editable input again (the page's read-only branch dropped); M2 the several-values wording for a binary
collection (the several-binary case removed from NotSingleText); M3 the bar left visible (CloseUndoPanel's Complete
removed); extras E1 the lookup's read-only map left empty, E2 the closed request's finally clearing the activity field
whatever the request.

Files: `Services/ADAttributeEditorService.cs`, `Components/Pages/ADAttributeEditor.razor`,
`Components/Pages/AdminEventLog.razor`, `ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`,
`ExchangeAdminWeb.Tests/AttributeEditorPageSaveAuditTests.cs`, `ExchangeAdminWeb.Tests/EventLogUndoRefusalTests.cs`,
`ExchangeAdminWeb.Tests/ProgressRegistry.cs` (one exemption's reason), this record.

## Fix

- `Services/ADAttributeEditorService.cs`:
  - `AttributeLookupResult` gains `IReadOnlyDictionary<string, string>? ReadOnlyAttributes = null`, its last member.
  - New private constants `SingleValuedRule` and `TextOnlyRule` (the two rules' sentences, unchanged), private
    `readonly record struct NotText(bool Binary, string Holds)`, private static `NotSingleText(PSObject read, string
    name)` - one `byte[]` "is binary (N bytes) in the directory", several values all `byte[]` "holds N binary values
    in the directory", several otherwise "holds N values in the directory", else null - and `RuleFor(NotText)`.
  - `NotSingleTextRefusal` groups each proposed attribute by `NotSingleText`, binary or several; its messages are built
    as before from the constants, so the several-values and the single-binary refusals read exactly as they did.
  - PerformLookup fills a case-insensitive `readOnlyAttributes` beside `currentValues` - "This attribute <holds>;
    <rule>" for each allowlisted attribute `NotSingleText` names - and returns it as the result's `ReadOnlyAttributes`.
- `Components/Pages/ADAttributeEditor.razor`: field `readOnlyAttributes` (`IReadOnlyDictionary<string, string>`),
  emptied at the top of PerformSearch with the others and set from the lookup beside `currentValues`. The "New Value"
  cell is an `@if` chain: the reason in `<small class="text-muted">` for an attribute in the map, else the select (a
  Choice with choices), else the text input - the two branches unchanged. NotYetConverted in ClickGateRegistry, so no
  line key moves (609 lines before, 616 after).
- `Components/Pages/AdminEventLog.razor`: field `undoPreviewActivity` (`IActivityHandle?`). ShowUndoPreview stores its
  activity there right after `Progress.Begin`, and its finally clears the field together with lowering `undoLoading`,
  only while its request is still the panel's. CloseUndoPanel, after its guard, completes the stored activity
  (`Complete(false, "Closed before the preview was ready.")`) and clears the field. NotYetConverted; 1,597 lines before,
  1,604 after; the guard still returns before Begin, and the pinned `handler.PreviewUndoAsync` stays in its window.
- `ExchangeAdminWeb.Tests/ProgressRegistry.cs`: CloseUndoPanel's exemption reason says it ends the activity of a preview
  it closes (still no remote, disk or JS call).

Tests: `ADAttributeEditorSaveTests` +2 - `SeveralValuesAndABinaryValue_AreReadOnlyInTheLookup_EachWithItsReason`,
`SeveralBinaryValues_AreRefusedAsBinary_NotUnderTheSingleValuedRule_AndNothingIsWritten`; `AttributeEditorPageSaveAuditTests`
+1 - `TheGrid_ShowsTheReasonInPlaceOfTheInput_ForEachAttributeTheEditorCannotWrite` (PerformSearch driven against the
harness's directory, the grid read from the page's own BuildRenderTree; helpers `PerformSearch` and `Rendered`);
`EventLogUndoRefusalTests` +1 - `AClosedPanelsPreviewActivity_LeavesTheFrameWithIt_AndTheNextPreviewKeepsItsOwn`.

Caller-visible change, by design: the AD Attribute Editor's grid shows an attribute that holds several values or a
binary value as read-only, its reason where the input was ("This attribute holds 2 values in the directory; the
attribute editor writes single-valued attributes only." or "... is binary (16 bytes) in the directory; the attribute
editor writes text attributes only."); its current value still shows, and it can no longer be typed into. An attribute
holding several binary values is refused, and marked, as "holds N binary values in the directory; the attribute editor
writes text attributes only." instead of under the single-valued rule. Every other refusal reads as before. In the event
log, closing a panel whose preview is still loading takes "Building undo preview" out of the status frame at once; the
closed preview's answer is still dropped.

## Guard proof

Reproduction: M1 takes the read-only branch out of the cell, which leaves the select-or-input chain as at `18530b58`; its
failure is the defect as the operator met it - three text inputs, holding "Analyst", "555-0100; 555-0101" and "binary
(16 bytes)". M2 drops the several-binary case, which is NotSingleTextRefusal's reading as at `18530b58` (several values
counted before their elements are looked at). M3 is CloseUndoPanel as at `18530b58` beside the new field. Each fails
the tests named.

Same driver and conditions as aud-a28z's (session scratchpad `mutate/round-a26u.ps1`, log `mutate/a26u-mutations.log`),
the filter
`FullyQualifiedName~ADAttributeEditor|FullyQualifiedName~AttributeEditorPageSaveAuditTests|FullyQualifiedName~EventLogUndoRefusalTests|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~UndoNotificationTests|FullyQualifiedName~UndoIdentityTests|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~ClickGate|FullyQualifiedName~DirectoryModuleImportTests|FullyQualifiedName~Allowlist`
(629 tests). Every mutant built with 0 errors; each restore matched its original MD5. After the round, a rebuild on the
restored bytes and the filter: 629/629 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 the editable input again (the read-only branch asks an empty map) | `ADAttributeEditor.razor`, `3E2CCEA7D79DC24B19CE5EE775B0B6DE` | `FEE81306CA66B6CE90AF3154E007AE2B` | 1 of 629: `TheGrid_ShowsTheReasonInPlaceOfTheInput_ForEachAttributeTheEditorCannotWrite` - three text inputs, the several values and the binary value among them |
| M2 the several-values wording for a binary collection (the several-binary case removed from NotSingleText) | `ADAttributeEditorService.cs`, `93E1F928F6AB25589730C9FC2288CA45` | `3F0EBBFBB8E32169D414808B91B3BE61` | 1 of 629: `SeveralBinaryValues_AreRefusedAsBinary_NotUnderTheSingleValuedRule_AndNothingIsWritten` - "holds 2 values in the directory; the attribute editor writes single-valued attributes only." |
| M3 the bar left visible (CloseUndoPanel's Complete removed) | `AdminEventLog.razor`, `2FA498D8922B42348E0097157BB83C1A` | `DF44C1DED5377E27D05FD46875D71378` | 1 of 629: `AClosedPanelsPreviewActivity_LeavesTheFrameWithIt_AndTheNextPreviewKeepsItsOwn` - "Building undo preview" still in the frame after the close |
| extra E1 the lookup's read-only map left empty | `ADAttributeEditorService.cs`, `93E1F928F6AB25589730C9FC2288CA45` | `0CC9A816B880B74107BFB47078C53F2A` | 3 of 629: `SeveralValuesAndABinaryValue_AreReadOnlyInTheLookup_EachWithItsReason`, `SeveralBinaryValues_AreRefusedAsBinary_NotUnderTheSingleValuedRule_AndNothingIsWritten` (its lookup step) and `TheGrid_ShowsTheReasonInPlaceOfTheInput_ForEachAttributeTheEditorCannotWrite` |
| extra E2 the closed request's finally clearing the activity field whatever the request | `AdminEventLog.razor`, `2FA498D8922B42348E0097157BB83C1A` | `04911BBB13DBC0AAFFFB41FC93BF2CC2` | 1 of 629: `AClosedPanelsPreviewActivity_LeavesTheFrameWithIt_AndTheNextPreviewKeepsItsOwn` - the closed preview's answer dropped the next one's handle, so closing that panel left its activity in the frame |
| extra E3 the page ignoring the lookup's map | `ADAttributeEditor.razor`, `3E2CCEA7D79DC24B19CE5EE775B0B6DE` | `0CC6970C0D5B48FCEA8D7C39EAC1132C` | 1 of 629: `TheGrid_ShowsTheReasonInPlaceOfTheInput_ForEachAttributeTheEditorCannotWrite` - three text inputs |

Not run, by design: the module's real answer for a binary or several-valued attribute, a directory probe the owner
forbids (the harness's `ReadsAs` types the values), and the page in a browser - the grid is read from the compiled
page's render tree, its search driven by reflection.

Unchanged through the round: `ADAttributeEditorSaveTests.cs` `62FD52DDD806C2FEEE6E2B8D26B4D07C`,
`AttributeEditorPageSaveAuditTests.cs` `CAC52364471D184FDF6EED1ABF3AEFBB`, `EventLogUndoRefusalTests.cs`
`AD4478AE12E38A82E000C95C895DF775`, `ProgressRegistry.cs` `F8478512F8932DFB1F251CB8977EBE59`. The code comments are
written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none in
  a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug): exit 0, 5,862 passed / 3 skipped
  (the three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`) / 0 failed, 5,865 total,
  5m45s; nothing hung, no Sequence file. aud-a28z's tree: 5,858 / 3 / 0, 5,861 total; the four new cases are this
  record's.
- `ClickGate*` 316/316 and `ProgressRegistryTests` 35/35, each run alone (Release).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No AD, EXO, Graph, DHCP or IIS call, no real module, directory, Delinea, SMTP, push or deployment: the harness's local
stand-ins for the AD cmdlets and a held stand-in undo handler. No coder self-review verdict is claimed.

## Comments after the round

Written after every round, in their own commit with aud-a28z's, checked comment-only as aud-a28z's record says. This
record's files:

- `Services/ADAttributeEditorService.cs` `93E1F928F6AB25589730C9FC2288CA45` -> `9F960B2E1F32CBD7139D4CC5F17D2AF9`: 1,466
  code lines, their MD5 `D2E8606A8684D08E7FDB614761592171` on both sides; 14 lines added, 1 changed - the lookup result's
  read-only map, why the lookup marks an attribute and by which reading, the two rules, `NotText`, and why several byte
  arrays are binary. The refusal's comment said "Several binary values still read as several", which this fix made
  untrue; it is corrected.
- `Components/Pages/ADAttributeEditor.razor` `3E2CCEA7D79DC24B19CE5EE775B0B6DE` -> `2A55CD2BA734A50BDCFD6F428DBC875B`: 477
  code lines, MD5 `A7F5F93D8B9A666265F7489FAA27090E`; 3 added - a one-line Razor comment on the cell, the field's
  purpose (616 lines -> 619; NotYetConverted, no key moves).
- `Components/Pages/AdminEventLog.razor` `BFFA1A8F58E108547BCAD80871218075` (aud-a28z part 5's commit) ->
  `75EAE03C38841B30FAC72552A4CA884D`: 1,239 code lines, MD5 `0CE3DC49A8B1A004ADBF14E2E9A4F914`; 9 added, 1 changed - the
  activity field, who clears it and why only the panel's request does, and why the close ends it and cancels nothing
  (1,605 lines -> 1,613; NotYetConverted).

The tests carry their comments from their own commit.

## Branch-tip gates - 2026-10-22 (`7698c096`)

As aud-a28z's record: Release build 0 errors / 21 warnings (cdt-41's baseline), none in this record's files; the full
suite 5,865 / 3 / 0, 5,868 total, 5m45s, the four cases here among the branch's twenty-six; `ClickGate*` 316/316 and
`ProgressRegistryTests` 35/35 alone; Pester 478/0/0; format, `git diff --check HEAD` and `tools/Test-AsciiOnly.ps1` exit
0; a dry `git merge-tree` against master `1410013e` reports no conflict, and master changed none of this record's files.

## Closeout

Fixed on branch `worktree-agent-aca5b5b4f6295736c`, not merged, not pushed. Two module-scoped behaviour changes: the AD
Attribute Editor (the read-only marks, the several-binary refusal) and the Admin Event Log (the closed preview's
activity): both module versions are due. No shared code changed here. The bumps are the supervisor's, at merge:
`Modules/ModuleCatalog.cs` and the csproj version elements were outside this brief.

## Known gaps

- *Assumption, not probed* (aud-a26r's): the module answers a binary attribute with a `byte[]` and several binary
  values with a collection of them.
- The mark is the lookup's: an attribute that gains a second value after the lookup still shows its input until the
  next search, and the save's refusal stops it, unchanged.
- The closed preview's handler still runs to its end behind the panel, its answer dropped (aud-a26r). The status frame
  no longer shows it and the navigation guard no longer counts it - a read whose answer nobody waits for. Its
  activity's cancellation is not requested: `PreviewUndoAsync` takes no token, so a cancel would stop nothing.
