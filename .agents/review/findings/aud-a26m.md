# aud-a26m: A clear-only or empty-to-value attribute edit cannot be undone

**Severity**: MEDIUM - a successful directory mutation loses its recovery path. `AuditService` writes
`old_<attr>` / `new_<attr>` for each changed attribute, but `JsonlLogService` drops null-valued fields,
and `ADAttributeEditorUndoService.CanUndo` (around line 59) requires at least one `old_` key AND one
`new_` key. A save where every changed attribute has one side null - clearing `department`, or
setting a previously empty attribute - leaves a successful `ADAttributeEditor_Update` row with
`changedAttributes` but no Undo button, although the preview/execute code already treats a missing
side as null.
**Status**: Merged to master (`fee8a58b`); closed on guard proof; codex review pending
**Branch**: `worktree-agent-a6af37cca287ebc2c`
**Commit**: `d0edf1ea` (comments `97592e99`; plan `4684d84d`; merged `fee8a58b`)
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-7, `prompt-attr-batch-7.md`, 2026-10-22)

## Evidence

At `6008a986`: `Services/ADAttributeEditorUndoService.cs` `CanUndo`; `Services/JsonlLogService.cs`
(null fields omitted on write); `Services/AuditService.cs` `LogADAttributeEdit` (`old_`/`new_` pairs).

## Approach (to be written by the implementing agent before code)

- `CanUndo`: a successful `ADAttributeEditor_Update` with non-empty `changedAttributes` is undoable
  when, for each changed attribute, at least one of `old_<attr>` or `new_<attr>` is present; a
  missing side means null/empty, exactly as the preview and execute paths read it. A record with
  neither side for some attribute stays not undoable (nothing to restore from).
- Keep aud-a26c (`ReversedOperations` requires Success) and aud-a28d/a28m (refusals are Failed
  records) untouched - a Failed record never becomes undoable.
- Tests through the undo tests and `AttributeEditorHarness`: a clear-only edit is undoable and its
  undo restores the old value; a set-from-empty edit is undoable and its undo clears; a record with
  no side for an attribute is not undoable; a Failed record is not undoable. Mutation at the call
  site: require both keys again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from codex attr-batch-7 (`afa9dbf6`). One
commit `fix(ad-attribute-editor): ... (aud-a26m)`, second of three on branch `worktree-agent-a6af37cca287ebc2c`.
Behaviour: AD Attribute Editor (module version; the supervisor owns the bump). Evidence re-read at `afa9dbf6`:
`Services/ADAttributeEditorUndoService.cs` CanUndo 46-62 asks for any `old_` key AND any `new_` key in the
record (59-61); `Services/AuditService.cs` LogADAttributeEdit writes `old_<name>`/`new_<name>` per change
(354-358); `Services/JsonlLogService.cs` drops every null top-level field (68), so a cleared side (NewValue null)
or a side that was not set (OldValue null) is absent from the line; PreviewUndoAsync (119-120) and
ExecuteUndoAsync (255, 282) already read an absent side as null.

**Rule.** A successful `ADAttributeEditor_Update` naming changed attributes is undoable when every one of them
has at least one recorded side, `old_<attr>` or `new_<attr>`, present with a value; a key held null (a record
built by hand) counts as absent, as the JSONL writer drops it. The missing side is "not set", as the preview and
execute paths read it. An attribute with neither side has nothing to restore from or check against: not
undoable. Category, action and Success are checked first, unchanged, so a Failed record - the aud-a28c thrown
write with its pairs, the aud-a26j unconfirmed save, every refusal (aud-a28d, aud-a28m) - is never undoable, and
aud-a26c's rule that only a successful record marks an edit reversed is untouched. An empty `changedAttributes`
stays not undoable (aud-a26d's no-change record).

Tests (`ADAttributeEditorUndoRefusalTests`, through the harness; the records read back from the JSONL and turned
into the dictionary the page builds): (a) a clear-only edit (department cleared) - no `new_department`, CanUndo
true, ExecuteUndoAsync puts Finance back, confirmed, the undo record Success; (b) a set-from-empty edit
(department set on a user without one) - no `old_department`, CanUndo true, the undo clears it; (c) an attribute
with neither side, beside one with both - not undoable; the only side held null - not undoable; (d) a Failed
record carrying both sides (a refused write) - not undoable. Mutation at the call site: M3 both keys required
again - (a) and (b) fail.

Files: `Services/ADAttributeEditorUndoService.cs`, `ExchangeAdminWeb.Tests/ADAttributeEditorUndoRefusalTests.cs`,
this record.

## Fix

`Services/ADAttributeEditorUndoService.cs`, CanUndo: after the unchanged category, action and Success checks, the
record's `changedAttributes` must be non-empty and each of them must have a side - `HasSide(old_<attr>)` or
`HasSide(new_<attr>)`. New `HasSide` (private static): the key is present with a value that is neither null nor a
JSON null. PreviewUndoAsync and ExecuteUndoAsync are unchanged: they already read a missing side as null, so the
undo of a clear proposes the old value and expects the attribute unset, and the undo of a set-from-empty proposes a
clear.

Tests (`ExchangeAdminWeb.Tests/ADAttributeEditorUndoRefusalTests.cs`, 4 cases, the records saved through the real
service and read back from the JSONL as the event log reads them):
`AClearOnlyEdit_IsUndoable_AndItsUndoPutsTheValueBack`, `ASetFromEmptyEdit_IsUndoable_AndItsUndoClearsIt`,
`AnAttributeWithNeitherSideRecorded_IsNotUndoable` (the both-sides control, an attribute with no side beside one
with both, the only side held null) and `AFailedEdit_IsNotUndoable_ThoughItRecordsBothSides` (a refused write's
record, which carries both sides). The class summary names aud-a26m.

Caller-visible change, by design: a successful edit that cleared an attribute, or set one that had no value, now
shows its Undo button in the Admin Event Log, and the undo puts the attribute back as it was - the value restored,
or the attribute cleared. A record naming an attribute with no recorded side, a Failed record and an undo record
still offer none.

## Guard proof

No separate pre-fix reproduction: M3 is CanUndo's rule as at `afa9dbf6`, restored at its call site, and it fails
the tests that name the defect.

Same driver and conditions as aud-a26l (`a26m-mutations.log`), the same filter, 238 tests on this tree. The mutant
built with 0 errors; the restore matched the original MD5. After the round, a rebuild on the restored bytes and the
filter: 238/238 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named test failing |
|---|---|---|---|
| M3 an `old_` key and a `new_` key required anywhere in the record again, as at `afa9dbf6` | `ADAttributeEditorUndoService.cs`, `617C3F3F5A6B0DDA35EBC75824CCD56E` | `ACE232308FA0607DD32BD78B42EF808A` | `AClearOnlyEdit_IsUndoable_AndItsUndoPutsTheValueBack`, `ASetFromEmptyEdit_IsUndoable_AndItsUndoClearsIt` (no Undo for a one-sided edit), and `AnAttributeWithNeitherSideRecorded_IsNotUndoable` (the old rule asked for any such key, so an attribute with none beside one with both, and keys held null, read as undoable); `AFailedEdit_IsNotUndoable_ThoughItRecordsBothSides` passes either way, as the control must |

Unchanged through the round: `ADAttributeEditorUndoRefusalTests.cs` `DAE958D01D647953734425F9B58AFCB8`. The code
comments are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental --no-restore`: 0 errors, 41 warnings, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --no-build --blame-hang --blame-hang-timeout 10m` (Debug, after a Debug
  build of this tree): exit 0, 5,448 passed / 3 skipped (the three known AD skips) / 0 failed, 5,451 total, 5m39s;
  nothing hung, no Sequence file. aud-a26l's tree (`7b7acc41`): 5,444 / 3 / 0, 5,447 total; the four new cases are
  this record's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1`: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. No coder self-review verdict is
claimed.

## Known gaps

- The preview of a one-sided edit shows the missing side as "(empty)", as it always read it; nothing in the panel
  says the side was absent from the record rather than recorded empty - the writer never records an empty value.
- A record written by hand with a side held as an empty string counts as a side, as the JSONL writer would have
  kept it.

## Comments after the round

Written after all three rounds, in their own commit (`97592e99`) with aud-a26l's and aud-a28o's; the same check (only
comment and blank lines changed in `git diff -U0`). This record's file, `Services/ADAttributeEditorUndoService.cs`:
`617C3F3F5A6B0DDA35EBC75824CCD56E` at this record's commit, `D28863B61DF5580BBE92C1FA7313D288` after aud-a28o's code
(the trace begun inside the try), `3FFC152A18B26FEE1E9C23A4D8B142BD` after the comments - why each changed attribute
needs one recorded side and not both, and why a key held null is no side. The tests carry their comments from this
record's commit.

## Branch-tip gates - 2026-10-22 (`97592e99`: the three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings - the baseline count - none
  in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,453 passed / 3 skipped (the three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`)
  / 0 failed, 5,456 total, 5m12s; nothing hung, no Sequence file. Baseline at `4684d84d` (the code as at `afa9dbf6`):
  5,427 / 3 / 0, 5,430 total; the twenty-six new cases are this branch's (aud-a26l seventeen, aud-a26m four,
  aud-a28o five).
- `ClickGate*` 316/316 and `ProgressRegistryTests` 35/35, also run alone (Release).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

## Closeout

Fixed on branch `worktree-agent-a6af37cca287ebc2c`, not merged, not pushed. A module-scoped behaviour change to the AD
Attribute Editor (one-sided edits offer their Undo): its module version is due. No shared code changed, so no base app
bump. The version bumps are the supervisor's, at merge: `Modules/ModuleCatalog.cs` and `ExchangeAdminWeb.csproj` were
outside this brief.
