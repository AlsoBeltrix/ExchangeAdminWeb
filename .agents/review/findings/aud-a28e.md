# aud-a28e: An invalid or slow allowlist pattern throws out of SaveAsync with no audit record

**Severity**: MEDIUM - no AD write happens, but an operator's save attempt can end with no durable
audit event, the aud-a28 / aud-a28c class the Constitution forbids.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
