# tll-1: An unreadable signInActivity shape is reported as a verified absence

**Severity**: HIGH - it manufactures the one reading this module exists to prevent. A cloud
source that came back in a shape we cannot read is reported as a source that answered "no
sign-ins", and with both log queries empty that composes to `LogVerified` - the single state the
plan says is safe to act on. Acting on it means disabling a live account.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `4725109`

## Evidence

`Services/CloudSignInService.cs:301-302`:

```csharp
if (!user.TryGetProperty("signInActivity", out var activity) || activity.ValueKind != JsonValueKind.Object)
    return (new CloudSignInAnswer(true, null, null), null);
```

That single branch covers three different facts and returns the same answer for all of them:

1. the property is **absent** - Graph's shape for an account with no recorded activity, and a
   genuine answer;
2. the property is **JSON null** - also Graph's shape for no recorded activity, also genuine;
3. the property is **any other non-object** (string, number, array) - a response this module
   cannot read at all.

The third is not an answer. The class remarks four paragraphs above the method state the rule it
breaks: *"A value that cannot be read is a source that did not answer."*

A second instance of the same rule break, `Services/CloudSignInService.cs:404-406`:

```csharp
var raw = value.GetString();
if (string.IsNullOrWhiteSpace(raw))
    return DateRead.Absent;
```

A date field that is PRESENT and blank is unreadable, not absent. `Absent` is not discarded at
`:307-311` - only `Malformed` is - so a blank date string reads as "no sign-in of that kind".

Triggering condition, either form:

```json
{"value":[{"userPrincipalName":"u@example.test","signInActivity":"bad"}]}
{"value":[{"userPrincipalName":"u@example.test","signInActivity":{"lastSignInDateTime":""}}]}
```

with both raw-log queries returning an empty `value` array - the ordinary shape for an account
with nothing in the retained 30-day window.

## Predicted observable failure

`CloudSignInLookup.Result.Verified == LogVerified` and `NoSignInReported == true`, with no error
on either source. The page renders the strongest possible claim - both sources agree this person
has never signed in - from a response it could not parse. Per `docs/TrueLastLogon-Plan.md`,
`LogVerified` is the only value marked "safe to act on", so this is the exact input that reaches
an account-disable decision.

## Approach

Split the branch so each fact gets its own answer, and make a present-but-unreadable date
`Malformed` rather than `Absent`:

- absent or JSON null -> answered, no activity (unchanged, and it is the real Graph shape);
- any other non-object -> `DidNotAnswer` with a named reason;
- a present blank date string -> `Malformed`, which the caller already discards.

## Files changed

- `Services/CloudSignInService.cs`
- `ExchangeAdminWeb.Tests/CloudSignInServiceTests.cs`

## Guard proof

Three tests in `ExchangeAdminWeb.Tests/CloudSignInServiceTests.cs`, each mutated and watched to
fail:

| Test | Mutation | Result |
| --- | --- | --- |
| `ASignInActivityInAShapeWeCannotReadIsNotAVerifiedAbsence` | restore the single `!= JsonValueKind.Object` branch | FAIL, restore -> 24/24 |
| `APresentButBlankActivityDateIsUnreadableRatherThanAbsent` | blank date string back to `DateRead.Absent` | FAIL, restore -> 24/24 |
| `AnExplicitlyNullSignInActivityIsStillARealAnswer` | over-tighten to `if (!hasActivity)` so JSON null falls into the unreadable branch | FAIL, restore -> 24/24 |

The third is the guard against over-correcting. Graph really does send
`"signInActivity": null` for an account with no activity, so a fix that treated every non-object
as a failure would make every genuinely dormant account permanently unverifiable - the opposite
error, and one that would have looked like extra safety.

**One assertion of mine was wrong and the tests caught it, not the code.** I first asserted
`NoSignInReported == false` on the repaired path. It is `true`, correctly: under `LogOnly30d` the
log DID answer and DID report nothing, so an absence really was reported - what changed is the
trust attached to it. The assertion now pins the verification state, which is the thing the
finding is about.

## Coder dispute

None. The finding is correct and the contradiction is mine: the file states the rule in its own
class remarks and then does not apply it at this branch.

## Known gaps

Graph emitting a non-object `signInActivity` has not been observed and is unlikely. The finding
stands on impact rather than probability: the failure is silent, it is the module's worst
reading, and the guard costs one branch.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Harness: codex-cli 0.154.0 (`codex exec`, CLI transport, `-s read-only`, generation pass).
Range: `046e3d213586b769680826354d714102ce4af721..cb968daa76da3711f238c80af6c6f252e1cf17d5`
(both SHAs echoed correctly). `capability_ok: true` - read `docs/TrueLastLogon-Plan.md` and
`.agents/repo-guidance.md`, ran `git diff --stat 046e3d2..cb968da`.
Verdict: **findings** (2). Timestamp: 2026-09-30.
Escalation triggers: none matched. No T1 sensitive path in the diff.

No verification round was dispatched: the finding is HIGH, not CRITICAL, and
`.agents/decisions.md` 2026-08-31 closes everything below CRITICAL on the coder-side guard
proof.

Note on the dispatch itself: the first attempt died before the model saw anything, on
`Invalid schema for response_format ... Missing 'capability_note'`. This gateway enforces strict
structured outputs, so every property must appear in `required`. That is a transport fact, not a
review outcome, and it is now fixed in `.agents/review/tll-s2.schema.json`.

## Closeout

Fixed directly on `master` in `4725109`. Full suite at `a908598` (the tll-2 fix on top):
**3344 passed / 0 failed / 3 skipped**, build 0 errors, format, ASCII lint, `git diff --check`.
Status `[x]` in `.agents/review/index.md`. Not yet pushed - push policy is ask.
