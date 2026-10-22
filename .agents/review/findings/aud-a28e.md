# aud-a28e: An invalid or slow allowlist pattern throws out of SaveAsync with no audit record

**Severity**: MEDIUM - no AD write happens, but an operator's save attempt can end with no durable
audit event, the aud-a28 / aud-a28c class the Constitution forbids.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress
**Branch**: `worktree-agent-a4f347c5f99ae060f`
**Commit**: -
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

## Guard proof

Pending.
