# prog-8: A third guard anchored on prose, found the same way as the first two

**Severity**: LOW - no operator-facing failure. It blocks correct changes with a false red.
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

## Evidence

`ExchangeAdminWeb.Tests/ProtectedGroupWriteTargetTests.cs:417` locates
`SweepExistingEntriesAsync` by raw page text, `:419` bounds it by the next method signature,
and `:421` asserts:

```csharp
Assert.DoesNotContain("ppTargets", page[sweepStart..sweepEnd], StringComparison.Ordinal);
```

That slice is **not** passed through `ProgressScan.CodeView` or any comment stripper.

**The S4 agent avoided it by accident of wording.** `Components/Pages/AdminSettings.razor:778-782`
spells its comment `Group TARGETS` rather than `ppTargets`; the executable sweep correctly
omits the token either way. Codex confirmed the guard would have fired on the comment.

Predicted observable failure: a correct future change that adds an explanatory comment or a
string literal mentioning `ppTargets` inside `SweepExistingEntriesAsync` fails
`AdminPage_TargetRows_AreNotSwept_AndDedupeByGuid` while target rows are still not swept. CI
blocked on prose.

## The pattern this completes

**Third guard in this repo found anchored on raw text rather than code**, and the third found
the same way - by someone tripping it or nearly tripping it, never by reading it:

| Finding | Guard | How it surfaced |
|---|---|---|
| `prog-6` | `IntuneDevicesPageTests` / `RiskyUsersPageTests`, `LastIndexOf("finally")` | an agent's own comment contained the word |
| `prog-3` | `ProgressRegistryTests`, covered-call lookup | a reviewer constructed a counterfeit literal |
| `prog-8` | `ProtectedGroupWriteTargetTests`, `DoesNotContain("ppTargets")` | an agent avoided it by chance of wording |

The repo now has TWO working comment-strippers - `ClickGateSource.BlankComments` and
`ProgressScan.CodeView` (the stricter, which also blanks literals; prog-6 chose it after
finding `BlankComments` refuses to blank a `//` on a line where a quote opens earlier). The
fix is always to use one of them.

**Worth a sweep rather than a third one-off fix:** any test slicing raw page or method text
and asserting on a token is this defect. That sweep is not in any approved plan.

## Approach

Pending. Minimum: route the slice through `ProgressScan.CodeView` before asserting, keeping
the intent - the sweep must not READ the `ppTargets` collection.

## Reviewer comments

Raised during the review of `ee1c0f9..6a561df` (slice S4), which was otherwise **sound**:
the AdminSettings dispatchers are thin and reach registered reporting methods, the
AdminBulkJobs handlers were genuinely synchronous before and now yield after `Begin`, the
`JobChanged` split preserves the same reloads without strobing, and `NotFound` versus
`Unavailable` is drawn at the right layer.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard.
Verification was no-build: 80/80 on the three filtered classes, `git diff --check` clean.
Raw output: `.agents/review/prog-s4.result.json`.

## Closeout

Pending.
