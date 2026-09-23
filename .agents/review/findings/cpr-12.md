# cpr-12: A malformed word list would pass validation and make the entropy floor a fiction

**Severity**: HIGH - the generator's one promise is that every password it returns measures at
least 60 bits. A pool that defeats the measurement defeats the promise, and the failure is silent:
the generator reports a strong password it did not produce.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit that completes this record

## Evidence

`Services/PasswordGenerator.cs` as of `c219a27` validated the embedded list with one check:

```csharp
if (words.Count < 1000) throw ...
```

Nothing required the words to be distinct, to fall in the documented 3-9 character range, or to
be the measured pool. `EffectivePool` then counts how many list entries share each selected word's
length and scores against that count.

## Predicted observable failure

Replace the embedded resource with 7,771 copies of `aaa` - malformed, but past the floor. Every
selected word is `aaa`, and every one scores against a 7,771-entry length bucket. The computed
entropy clears 60 bits comfortably. The real entropy of the word component is zero, and the
generator hands the module a password it has certified as strong.

The realistic version is not sabotage but a bad merge, a truncated file, or a regenerated list
somebody did not re-measure.

## Approach

Validate at load, before any password is generated, and refuse rather than degrade on each:

- **Distinctness.** Duplicates are the dangerous malformation, because they inflate the very
  buckets the score reads.
- **The documented shape.** 3-9 characters, lowercase ASCII plus the hyphen that four real entries
  carry. An entry outside the length range also breaks the budget arithmetic in the word-fitting
  step, not just the score.
- **An exact count, not a floor.** Pinned to `ExpectedWordCount = 7771`, which is what
  `Resources/CloudPasswordWordList.provenance.md` measures. A pool that GREW changes the entropy
  as surely as one that shrank, so both are a deliberate two-file edit rather than a surprise, and
  the refusal message says which two files.

## Files changed

- `Services/PasswordGenerator.cs` - `ValidateWordList` and `ExpectedWordCount`.
- `ExchangeAdminWeb.Tests/PasswordGeneratorWordListTests.cs` - nine tests.

## Guard proof

Mutation: restore the `< 1000` floor and disable the distinctness check. Four tests fail,
including `A_list_of_duplicates_is_refused`, which uses exactly the reviewer's 7,771-copies case.

A note on the fixture: its first draft built words as `w0001`, which the validator correctly
refused for containing digits. The fixture was wrong, not the rule.

## Coder dispute

None.

## Known gaps

The validator checks shape, not quality. A list of 7,771 distinct but closely-related words - all
sharing a prefix, say - would pass every rule here while offering less real entropy than the score
credits. The provenance record and a human reading the diff are what cover that; no cheap
mechanical check does.

## Reviewer comments

Round 1, Change review over `c219a27^..c219a27`:
Reviewer: codex / `@azure-openai-eus2-global/gpt-5.5-dzs` / xhigh / standard, 2026-09-23.
Capability proof passed. Verdict **unsound**, one HIGH and one MEDIUM, no CRITICAL.
It cleared the draws themselves - the shuffle, the word selection, the guided-fallback length
assignment and the padding distribution - and the refuse-never-degrade path.

A HIGH closes on the coder-side guard proof (`.agents/decisions.md` 2026-08-31).

## Closeout

Fix and this record land with cpr-13 in one commit.
