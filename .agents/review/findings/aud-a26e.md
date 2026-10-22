# aud-a26e: Allowlist patterns cannot be seen or edited on the page, and the legacy import bypasses the pattern check

**Severity**: LOW - no wrong write, but an admin has no way to see or correct an attribute's
validation pattern on the Module Configuration page (the row only round-trips the stored value),
so a refused save under aud-a28e can only be cleared by deleting and re-adding the row, which
silently drops the pattern. The legacy `ad-editable-attributes.json` import writes rows straight to
the repository and so skips the aud-a28e save-time check; the validation-time catch still refuses
at save, so the gap is operability, not safety.
**Status**: Merged to master (`488e535e`); closed on guard proof; codex attr-batch-3 clean (raised aud-a28i; one LOW folded into aud-a26f)
**Branch**: `worktree-agent-a0e4f589237f5b88c`
**Commit**: `fc117a1c` (merged `488e535e`)
**Reviewer**: implementer (surfaced by the aud-a28e/a26d agent, 2026-10-22, candidates 3 and 4 of its hand-back)

## Evidence

At `20ef8984`, `Components/Pages/ModuleConfig.razor`, the Editable Attributes table: `AllowlistRow`
has a `Pattern` member filled from the loaded list and written back on save, with no input bound
to it. `Services/ADAttributeEditorService.cs`: the legacy-file import path writes through the
repository without `SaveAllowlist`, so `DescribeUncompilablePattern` (aud-a28e) does not run for
imported rows.

## Approach (to be written by the implementing agent before code)

- Add a Pattern input to the allowlist row (plain text, optional) so the stored value is visible and
  editable; the aud-a28e save-time check refuses an uncompilable one with the message naming the
  attribute. The onus stays on the admin to enter a correct pattern; the page does not guess or
  auto-fix.
- Route the legacy import through the same compile check; an import holding an uncompilable
  pattern is refused as a whole with the attribute named (fail closed; no partial import).
- Tests: the row renders and saves the pattern; an import with a bad pattern is refused and stores
  nothing. Mutation at the call site: drop the import check.
- A page edit that adds lines invalidates `ClickGateRegistry` entries below it; `ModuleConfig.razor`
  is `NotYetConverted`, so only `ExpectedLineCount` (if present) changes; confirm and follow
  `.agents/playbooks/clickgate-reanchor.md` if any keyed entry exists.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the a28e/a26d hand-back
(`b38a6861`). One commit `fix(module-config): an allowlist pattern can be seen and edited, and the
legacy import refuses one that does not compile (aud-a26e)`, after aud-a28f on the same branch.
Behaviour: the Module Configuration page (app-shared, base app version) and the AD Attribute
Editor service's legacy import (its module version); the supervisor owns both bumps. Evidence
re-read at `b38a6861`: the Editable Attributes table (`ModuleConfig.razor` 538-588) has no input
for `AllowlistRow.Pattern`, which LoadAllowlist fills (1611-1615) and the save passes back (1697);
`ADAttributeEditorService.ImportLegacyIfPresent` (385-) checks the file with `ValidateRows` (418)
and stores `wrapper.Attributes` through `_repository.ImportAllowlistIfMissing` (435), never through
the aud-a28e compile check.

1. **Page.** A Pattern column - one header, one plain-text input per row after Max Len - with
   `value="@attr.Pattern"` and `@onchange` to a new `UpdateAttrPattern`, which keeps the text as
   typed (empty is no pattern) and counts one edit, as the other cells do. Nothing is guessed or
   fixed: aud-a28f's PatternCheck refusal names the attribute and says an administrator must
   correct the pattern. The Choices sub-row's colspan follows the column count (7 -> 8).
   `ProgressRegistry` lists every handler on this page by name, so `UpdateAttrPattern` joins the
   other cell edits as an `AllowlistRowEdit` exemption.
2. **Message.** `DescribeUncompilablePattern` names the attribute (label and LDAP name) and the
   parser's reason; the allowlist save adds that it was not saved and an administrator must correct
   the pattern. One wording for SaveAllowlist's throw and the audited refusal.
3. **Import.** After `ValidateRows` passes, every row of the file is compiled before anything is
   stored. One that does not compile: the error is logged naming the attribute, nothing is
   imported, the file is left in place (not archived) and the allowlist fails closed
   (`_legacyAllowlistCorrupt`), exactly as the import's other validation failures do. Whole file or
   nothing.
4. **Registries.** `ClickGateRegistry` holds no `ModuleConfig.razor` entry or `ExpectedLineCount`
   (`NotYetConverted`), so the added lines move nothing; no re-anchor.

Tests. `ADAttributeEditorServiceTests`: a legacy file holding `[` on one row -> GetAllowlist null,
IsAllowlistCorrupt true, the store holds nothing and is not marked configured, the file is still in
place, and the error logged names the attribute; a legacy file holding a valid pattern -> imported
with it, file archived. Round trip: `AllowlistSaveAuditTests` - a pattern entered on a row is saved by
SaveAllowlistAndAudit, read back by GetAllowlist, and recorded under `changed`; page tripwire (no
bUnit): the row's input carries `attr.Pattern` to `UpdateAttrPattern`, the handler writes the row's
Pattern, LoadAllowlist fills it and the save passes it into the attribute it saves. Conditions
asserted, not wording.

Mutation at the call site: the import's compile check deleted. Extra: the page handler made to drop
what was typed.

## Executed reproduction - 2026-10-22

Branch `worktree-agent-a0e4f589237f5b88c` after aud-a28f (`07c366ef`); service MD5
`7C2B979F4861877A96EACDD2AD8EAC63`, page MD5 `434C5FF2B24221A477551BA5659961C8`, as aud-a28f left
them. The four new cases against them: 2 failed / 2 passed, 0 compiler errors
(`a26e-reproduction.log` in the session scratchpad's `a28f-a26e-work`):

- `Construction_LegacyFileWithAnUncompilablePattern_ImportsNothing_AndFailsClosed`:
  `Assert.Null() Failure: Value is not null` - the file was imported, `[` included, and the
  allowlist loaded as valid;
- `ThePatternInput_CarriesTheRowsPatternInAndOut`: `Assert.Matches() Failure: Pattern not found in
  value` - no input carries `attr.Pattern`.

The controls (a legacy file with a valid pattern is imported with it; a pattern given to the save is
stored, read back, recorded and can be cleared) passed: the store and the save already round-tripped
a pattern, and only the page and the import were missing their part.

## Fix

`Components/Pages/ModuleConfig.razor`, the Editable Attributes table: a Pattern column after Max Len
- one header and, per row, a plain-text input with `value="@attr.Pattern"`, `@onchange` to the new
`UpdateAttrPattern`, placeholder `none` and a one-line tooltip (a saved value must match it; ^ and $
anchor the whole value). `UpdateAttrPattern` stores the text as typed - nothing trimmed, guessed or
repaired - with an empty box meaning no pattern, and counts one edit like the other cells. The Choices
sub-row's colspan follows the extra column (7 -> 8). A pattern that does not compile is refused when
the allowlist is saved, by aud-a28f's PatternCheck refusal, naming the attribute.

`Services/ADAttributeEditorService.cs`:

- `DescribeUncompilablePattern` split: a core `(name, label, pattern)` naming the attribute and the
  parser's reason, and the allowlist-save wording on top of it - "... is not a valid regular
  expression: <reason> The allowlist was not saved: an administrator must correct the pattern." -
  which both SaveAllowlist's throw and SaveAllowlistAndAudit's refusal use.
- `ImportLegacyIfPresent`: after `ValidateRows` passes, every row of the file is compiled. One that
  does not compile: logged as an error naming the attribute, nothing imported, the file left in
  place (not archived), `allowlistCorrupt` set - the import's existing fail-closed signal, so the
  allowlist loads as corrupt until an administrator corrects the file - exactly as the import's
  other validation failures already behaved. Whole file or nothing.

`ExchangeAdminWeb.Tests/ProgressRegistry.cs`: `UpdateAttrPattern` registered as an `AllowlistRowEdit`
exemption beside the other cell edits. Required: with it removed,
`ProgressRegistryTests.EveryOperationHandlerOnACoveredPageIsClassified` fails (checked once, mutant
MD5 `0C9024BB26CE02207C47C46D8A87E69C`, restored to `B423EE39A52F179B2730A53D02FE0A41`).

Tests: two cases in `ADAttributeEditorServiceTests` (the refused import, its control) and two in
`AllowlistSaveAuditTests` (the round trip, the page tripwire).

Registries: `ClickGateRegistry` holds no `ModuleConfig.razor` entry (`NotYetConverted`, "tier 2, not
approved") and no `ExpectedLineCount` for it, so the three lines the page gains (1,869 -> 1,872;
1,888 at `b38a6861` before aud-a28f moved the save out) move nothing and there was nothing to
re-anchor.

Caller-visible change, by design: the Editable Attributes table shows and edits each attribute's
pattern. An upgrade whose legacy `ad-editable-attributes.json` holds an uncompilable pattern now
leaves the allowlist failing closed (the corrupt banner on the page, every attribute save refused)
until the file is corrected, where it used to import the file and fail only that attribute's saves.
The pattern refusal's wording now ends by saying an administrator must correct the pattern.

## Guard proof

Same session-local driver as aud-a28f (`mutate.ps1`), filter
`FullyQualifiedName~AllowlistSaveAuditTests|FullyQualifiedName~ADAttributeEditorServiceTests|FullyQualifiedName~ADAttributeEditorPatternTests`
(94 tests). Fixed-file MD5s: `Services/ADAttributeEditorService.cs`
`6C9838D52EED5ACFF2EEA7B5FA872CF6`, `Components/Pages/ModuleConfig.razor`
`64F62F379E99679CD6B6B396AD6DF450`; each restored to its MD5 after every mutant, and every mutant
built with 0 errors. After the round, a rebuild on the restored bytes and the filter: 94/94 green.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| the import's compile check disabled (`else if (false && wrapper.Attributes ...`) | `9C66E872FBF3B6AFE6D00F284796B95D` | `Construction_LegacyFileWithAnUncompilablePattern_ImportsNothing_AndFailsClosed` (`Assert.Null`: the file was imported) |
| extra, page: `UpdateAttrPattern` drops what was typed (`Pattern = null`) | `073A38B428E8D868207A39E540B904A9` | `ThePatternInput_CarriesTheRowsPatternInAndOut` |
| extra: the save-time compile check the new input relies on removed (`string? uncompilable = null;`) | `7AE31AD4EFF64346AD156E8C4E1F9D66` | `AnUncompilablePattern_IsRefused_Audited_NamingTheAttribute` (SaveAllowlist's own throw then lands in the catch: a failure record, not the refusal) |

The code comments are written after the round, in their own commit, so the MD5s above are this
commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, all warnings pre-existing, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: exit 0, 5,104 passed /
  3 skipped / 0 failed, 5,107 total, 5m16s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. The page was not
rendered: no live circuit was run. No coder self-review verdict is claimed.

## Known gaps

- The input itself is pinned by a source tripwire, not rendered: no bUnit host exists. What the save
  does with a pattern is driven.
- A pattern that compiles but is catastrophically slow still passes the save and the import; only
  the validation-time catch (aud-a28e) refuses it, because a timeout depends on the value.
- The legacy import runs from the service's constructor - the service is scoped, so on each
  construction while the file is still there and the store is unconfigured - and the only place its
  refusal names the attribute is the application log (once per construction); the page shows its
  generic corrupt-store banner. (Corrected in the comments commit: an earlier line here said "once
  per process start".)
