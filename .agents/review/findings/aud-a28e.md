# aud-a28e: An invalid or slow allowlist pattern throws out of SaveAsync with no audit record

**Severity**: MEDIUM - no AD write happens, but an operator's save attempt can end with no durable
audit event, the aud-a28 / aud-a28c class the Constitution forbids.
**Status**: Merged to master (`20ef8984`); closed on guard proof; codex review pending
**Branch**: `worktree-agent-a4f347c5f99ae060f`
**Commit**: `5d2a27ba` (merged `20ef8984`)
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-1, `prompt-attr-batch-1.md`, 2026-10-22)

## Evidence

At `8586b047`, `Services/ADAttributeEditorService.cs`, the proposed-value validation that runs
before `PerformSave`, before `LogRefusal`, and before the aud-a26b notification wrapper:

```
1073            if (!string.IsNullOrEmpty(attr.Pattern) && !Regex.IsMatch(value, attr.Pattern, RegexOptions.None, RegexTimeout))
1074                return $"Attribute '{attr.Label}' does not match the required pattern.";
```

`attr.Pattern` comes from the editable-attribute allowlist, which an admin edits by hand. A
pattern that does not compile (for example `[`) makes `Regex.IsMatch` throw
`ArgumentException`; a pattern and value that exceed `RegexTimeout` throw
`RegexMatchTimeoutException`. Either escapes `SaveAsync` as an exception. The page's outer
catch shows and announces a failure, but the audit log gets neither a refusal row nor a failed
update row for the attempted save.

## Approach (to be written by the implementing agent before code)

- Treat a pattern that cannot be compiled, or a match that times out, as an audited refusal:
  `LogRefusal` with `refusedAt` naming the validation step and `mutationAttempted: false`, then
  a failed result that aud-a26b's wrapper announces once. The message to the operator must say
  the allowlist pattern for that attribute is invalid or too slow, so an admin fixes the
  allowlist; the operator is not asked to guess.
- Also reject an uncompilable pattern at allowlist save time (the admin page), so the bad
  pattern is caught where it is typed. Both layers; the save-time check alone does not protect
  a pattern already stored.
- Tests through `AttributeEditorHarness`: uncompilable pattern -> refusal audited, no directory
  session opened, one notice; timeout pattern -> same; allowlist save with an uncompilable
  pattern is refused. Mutation at the call site: remove the catch so the exception escapes
  again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted by codex attr-batch-1. One commit
`fix(ad-attribute-editor): an invalid allowlist pattern is an audited refusal, not an unaudited
exception (aud-a28e)`. Module behaviour: AD Attribute Editor (the supervisor owns the version
bump). Evidence re-read at `a64416fa`: the pattern check is `ValidateProposedValues` 1073-1074,
called from `SaveAndAuditAsync` 625 - whose refusal at 626-630 already writes
`LogRefusal(..., "Validation", ...)` - and from `PreviewAsync` 554, which has no caller.

1. **Validation time: the catch sits at the regex call.** The `Regex.IsMatch` goes in a try whose
   one catch takes `ArgumentException` (the pattern does not compile) and
   `RegexMatchTimeoutException` (the match ran past `RegexTimeout`) and returns a validation error
   that names the attribute, says its allowlist pattern is invalid or too slow so the value could
   not be checked, and says an administrator must correct the allowlist. The existing validation
   refusal does the rest unchanged: `LogRefusal` with `refusedAt: "Validation"` and
   `mutationAttempted: false`, a failed result, SaveAsync's one notice (aud-a26b). At the regex call
   rather than around the `ValidateProposedValues` call because only there is the attribute known -
   the message must name it and the exception does not carry it - and because PreviewAsync shares
   the method and stops throwing too. Nothing else is inside the try. Fails closed: the catch
   refuses; it never counts an unusable pattern as a match.
2. **Allowlist save time: the admin side.** `ADAttributeEditorService.SaveAllowlist` - the write the
   Module Configuration page's Editable Attributes save makes - compiles every non-empty pattern
   first and throws `ArgumentException` naming the attribute (label and name) and the parser's
   reason before the store is touched. The page's existing catch shows it as "Failed to save: ...";
   its dirty reset, saved banner and settings-change audit sit below the call and do not run. No
   page edit, so no ClickGate or Progress registry entry moves (ModuleConfig.razor is
   `NotYetConverted`). The validation-time catch stays: a pattern already stored, imported from the
   legacy file or edited into the database never passes this check. Only compilation is checked
   here; a timeout depends on the value.

Found while reading: the page has no Pattern input. `AllowlistRow.Pattern` is filled from the loaded
list and written back on save, so a pattern reaches the store through the legacy
`ad-editable-attributes.json` import or out of band, and the page only round-trips it. On the page,
a refused save is cleared by removing the row and adding it again (a new row has no pattern).

Tests, new `ExchangeAdminWeb.Tests/ADAttributeEditorPatternTests.cs`, through
`AttributeEditorHarness`, the pattern planted in the repository as a stored one would be: `[` ->
failed result naming the attribute, no directory session, nothing written, exactly one audit record
(`ADAttributeEdit_Refused`, `refusedAt: "Validation"`, `mutationAttempted: false`, the error the
operator was given) and exactly one failed notice carrying it; a valid pattern is still enforced
both ways (a matching value is written; a non-matching one is the ordinary refusal);
`SaveAllowlist` with `[` throws naming the attribute and leaves the stored allowlist unchanged.
Conditions are asserted, not wording.

Timeout: `RegexTimeout` is a private static two seconds and the harness cannot set it, so no
committed test waits on it. The uncompilable case covers the catch, whose one body serves both
exception types; the timeout half of the filter is exercised once, session-locally, with the timeout
temporarily shortened, and recorded under Guard proof.

Mutations at the call sites: the catch disabled (`when (ex is null)`: the exception escapes again);
the validation refusal's `LogRefusal` deleted (caught but unaudited); the `SaveAllowlist` check
removed; extra, the catch made to fail open (an unusable pattern counted as a match).

## Executed reproduction - 2026-10-22

Branch `worktree-agent-a4f347c5f99ae060f` after the plan commit (`6b74d40d`); service MD5
`23748328529DBAE43BE81DA5B6599649`, as aud-a28c left it. The new `ADAttributeEditorPatternTests`
against it: 2 failed / 2 passed, 0 compiler errors (`reproduction.log` in the session scratchpad's
`a28e-a26d-work`, which also holds aud-a26d's case):

- `AnUncompilablePattern_IsAnAuditedRefusal_NotAnException`: `RegexParseException : Invalid pattern
  '[' at offset 1. Unterminated [] set.` out of SaveAsync - no result, so no record;
- `AnAllowlistSaveWithAnUncompilablePattern_IsRefused_NamingTheAttribute`: `Assert.Throws() Failure:
  No exception was thrown` - the allowlist save stored `[`.

The two controls (a valid pattern enforced both ways; a valid pattern stored) passed.

## Fix

`Services/ADAttributeEditorService.cs`:

- `ValidateProposedValues`: the `Regex.IsMatch` sits in a try whose one catch,
  `when (ex is ArgumentException or RegexMatchTimeoutException)`, returns "The allowlist pattern for
  attribute '<label>' is not a valid regular expression / took too long to evaluate, so the value
  could not be checked. An administrator must correct the attribute allowlist." SaveAndAuditAsync's
  existing Validation refusal records and returns it and SaveAsync announces it. PreviewAsync (no
  caller) now returns it as a failed preview instead of throwing.
- `SaveAllowlist`: each non-empty pattern is compiled first (`DescribeUncompilablePattern`); one that
  does not compile throws `ArgumentException` naming the attribute's label and name with the parser's
  reason, and the store is not written.

Tests: new `ExchangeAdminWeb.Tests/ADAttributeEditorPatternTests.cs`, 4 cases. No page, harness,
registry or PowerShell change; RefusalAuditTests' counts are unchanged (no new refusal return).

Caller-visible change, by design: the operator now sees the refusal (the page's failure branch) where
the raw parser message used to reach the page's outer catch, and the attempt leaves a refusal record;
still exactly one notice, now SaveAsync's. The Module Configuration page now refuses to save an
allowlist holding an uncompilable pattern ("Failed to save: The pattern for attribute ...") where it
used to store it.

## Guard proof

Session-local driver (`mutate.ps1`, session scratchpad `a28e-a26d-work`): exact bytes saved, one
literal replacement that must match exactly once, Release build with 0 `error CS` required, filter
`FullyQualifiedName~ADAttributeEditorPatternTests|FullyQualifiedName~RefusalAuditTests`, exact bytes
restored in `finally`, MD5 re-checked, mtime advanced. Fixed-file MD5
`3CABDD5B22FBEF30DCE882DC4489BE8F`, restored to it after each; every mutant built with 0 errors.
After the round a rebuild and the attribute-editor filter (`ADAttributeEditor*`, RefusalAuditTests,
UndoNotificationTests, PageAuthorizationRecheckTests): 142/142 green.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| the catch disabled (`when (ex is null)`: the exception escapes again) | `421291BD5BAD42B58D99D440F5D432E8` | `AnUncompilablePattern_IsAnAuditedRefusal_NotAnException` (`RegexParseException` out of SaveAsync) |
| caught, but the validation refusal's `LogRefusal` deleted | `DFA2FED1658EC47DC1B05E717A943F53` | `AnUncompilablePattern_IsAnAuditedRefusal_NotAnException` (no record) and `AValidPattern_IsStillEnforced_BothWays`; RefusalAuditTests' two Validation scans agree |
| the `SaveAllowlist` check removed | `CF4A632A8709D971D11F0832263C8927` | `AnAllowlistSaveWithAnUncompilablePattern_IsRefused_NamingTheAttribute` |
| extra: the catch fails open (`matches = true`) | `B931D904FB8F98BE787E4ACCBAC374B8` | `AnUncompilablePattern_IsAnAuditedRefusal_NotAnException` (the save went through) |

Timeout half, session-local and not committed (`timeout-probe.ps1`): with `RegexTimeout` shortened to
1 ms and a throwaway test saving `^(a+)+$` against forty `a` and a `!`, the save is the audited
Validation refusal with the timeout wording, no session, one notice - passed (probe MD5
`CA282F71182A14808E8D09EE95152FB3`). With `RegexMatchTimeoutException` also dropped from the filter,
`RegexMatchTimeoutException` escapes SaveAsync - failed (`6F4C7E4F840B64745B07C1E26E030C5C`).
Restored to `3CABDD5B22FBEF30DCE882DC4489BE8F` and the probe test deleted after each.

The code comments are written after the round, in their own commit, so the MD5s above are this
commit's bytes.

## Gates - 2026-10-22

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings (full rebuild), all
  pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: exit 0, 5,049 passed /
  3 skipped / 0 failed, 5,052 total, 5m02s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, page, AD module, directory, Delinea, SMTP, push or deployment. No coder
self-review verdict is claimed.

## Known gaps

- The committed suite does not drive the timeout: RegexTimeout is a private static two seconds the
  harness cannot set. The probe above is the evidence, and it is session-local.
- The allowlist page cannot show or edit a pattern, so an admin refused there removes the row and
  adds it again (dropping the pattern) or corrects the stored value out of band. A pattern editor
  would be a feature, not this fix.
- The legacy-file import writes rows straight to the repository and skips the save-time check; a bad
  pattern arriving that way, or by a database edit, is caught at validation time.
- The Module Configuration page's allowlist-save refusals (not authorised, corrupt store, a
  denylisted name, now an uncompilable pattern) and its exception path write no audit record; only a
  successful save is audited. Pre-existing; not reached by this record.
- No directory, Delinea or SMTP was exercised; the stand-in cmdlets are the evidence.
