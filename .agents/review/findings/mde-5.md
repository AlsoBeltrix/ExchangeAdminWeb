# mde-5: An all-invalid upload still cannot reach its errors CSV

**Severity**: MEDIUM - it re-opens `mde-3` in the case that needs it most. The upload with the
most to explain was the only one that could not produce an explanation.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `ac71855`

## Evidence

Not a Defender finding - same provenance as `mde-3`: the reviewer was reading the session's
whole diff, which carried the Comms-10k work.

`mde-3`'s fix (`835a195`) recorded parse-time rejects at
`Components/Pages/Comms10k.razor:438` and `:445`, but left the route to them gated twice:

- the only pre-validation path shown was gated on `parsedEmails.Count > 0` (`:94`);
- the errors CSV button lived inside `resolveResult.Success` (`:102`).

A file whose rows are ALL unusable satisfies neither. It produces
`rejectedBeforeLookup.Count >= 1` and `parsedEmails.Count == 0`, so there is no Validate button,
no resolver call, no `resolveResult` - and therefore no download button anywhere on the page.

## Predicted observable failure

A file containing a header row `Email` and one row `not-an-address` gives
`rejectedBeforeLookup.Count == 1` and nothing else. The operator uploads it, the page offers no
next step, and the one rejection with its reason is unreachable. This is the ordinary shape of a
wrong-column or wrong-sheet upload, which is exactly when an operator needs to be told what the
page saw.

## Approach

Offer the download on the rejections alone. A danger alert renders when
`parsedEmails.Count == 0 && rejectedBeforeLookup.Count > 0 && resolveResult == null`, states the
rejected count and that there is nothing to validate, and carries its own
`DownloadRejections` button.

## Files changed

- `Components/Pages/Comms10k.razor`
- `Modules/ModuleCatalog.cs` (Comms-10k `1.4.1` -> `1.4.2`)
- `ExchangeAdminWeb.Tests/Comms10kRejectionReportTests.cs`

## Guard proof

`AFileWithNoUsableRowsStillOffersTheErrorsReport` in `Comms10kRejectionReportTests.cs`. It pins
the all-rejected condition in the source AND counts the `DownloadRejections` call sites at
exactly two - learning from `mde-3`, whose first guard matched a declaration rather than a use.
Counting the sites means deleting EITHER download path fails the test.

| Test | Mutation | Result |
| --- | --- | --- |
| `AFileWithNoUsableRowsStillOffersTheErrorsReport` | delete the new `@if` block | FAIL; restored -> passes |

Mutation run this session. Full Release suite at the fix: **3,826 passed / 0 failed / 3 skipped**;
build 0 errors; format clean; `git diff --check` clean; ASCII lint passed.

## Coder dispute

The scope question raised against `mde-3` applies here too: the owner questioned whether
Comms-10k belonged in a Defender session at all, and the coder judged the fix stays because
dropping it leaves the owner's ruling unmet. **Not confirmed by the owner.**

## Known gaps

The guard is a source-text assertion over the `.razor` file, not a rendered-DOM test. It proves
the markup and the handler are present on that path; it does not prove the alert renders in a
browser. Nothing in this repo's suite can prove that.

The reviewer also noted that duplicate valid rows are deliberately not reported by the parse
path - that is the ruling recorded in `mde-3`, not a gap.

## Reviewer comments

`Reviewer: codex / gpt-5.5 / xhigh`
Round 2 of the stream. Verdict: **findings** (2). Finding 2 of 2.
Raw output: `.agents/review/out/round2.txt`.

Below CRITICAL, so closed on the coder-side guard proof per `.agents/decisions.md` 2026-08-31.

## Closeout

Fixed directly on `master` in `ac71855`. Round 3 is owed. Not yet pushed - push policy is ask.
