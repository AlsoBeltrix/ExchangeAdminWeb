# mde-3: The upload parse drops rows before the resolver, so they can never be reported

**Severity**: MEDIUM - it contradicts the owner ruling the feature was built to satisfy. The
CSV exists to tell the operator why every non-matching address was rejected; rows discarded at
parse time were decided not to count.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `835a195`

## Evidence

Not a Defender finding. The reviewer was given the whole session diff (`96cdb89..HEAD`), which
contained the Comms-10k rejected-address CSV landed earlier the same day in `a01767c`, and
pulled it back in. It is filed under this stream's prefix because that is where it was found.

`Components/Pages/Comms10k.razor:398-400` (pre-fix) trimmed, dropped blanks, dropped values
with no `@`, and de-duplicated - all before validation. Only the filtered `parsedEmails` list
reached the resolver at `:469` and the queued replace payload at `:536`.

`Services/Comms10kAddressResolver.cs:207-210` can report a blank address as a rejection, but it
never sees one: the page removed it first. So blank rows, malformed no-`@` rows and duplicate
rejected rows could not become rejections at all, and the errors CSV - built from the resolver's
output - omitted them silently.

The ruling it contradicts, verbatim from the owner: *"all of the non-matching addresses need to
be reported to the user... with the reason it was rejected."*

## Predicted observable failure

An operator uploads a workbook with a stray blank row and a mistyped cell. The run reports N
addresses resolved, the errors CSV lists only the resolver's rejections, and the two rows that
were simply dropped appear nowhere. The operator has no way to learn their file was short.

## Approach

Parse rejections become first-class rejections with a reason, carried beside the resolver's and
written to the same file, parse rejections FIRST - those are the ones fixed in the workbook
rather than chased in AD. The address written is the RAW field, untrimmed, for the reason the
resolver already keeps it that way: the operator has to find that row in what they uploaded.

A duplicate is deliberately NOT reported, and a test pins that. The address resolves on its
first appearance, so that person IS on the list; a row in the errors CSV for somebody who was
included sends the operator chasing a non-problem.

## Files changed

- `Components/Pages/Comms10k.razor`
- `Modules/ModuleCatalog.cs` (Comms-10k `1.4.0` -> `1.4.1`)
- `ExchangeAdminWeb.Tests/Comms10kRejectionReportTests.cs`

## Guard proof

**The first version of the guard did not bite**, and that is the part worth carrying: a
whole-file `Contains` matched the reason constant's DECLARATION, so deleting every USE of it
still passed. The guard is now scoped to the parse handler and counts the call sites.

Full suite at the fix: **3,824 passed / 0 failed / 3 skipped**; build 0 errors; format clean;
`git diff --check` clean; ASCII lint passed.

## Coder dispute

The owner questioned whether Comms-10k belonged in a Defender session's scope at all. The coder
judged the fix stays, because dropping it leaves the owner's own ruling unmet by code that
claims to implement it. **That judgement was not confirmed by the owner** - recorded here and in
`.agents/state.md` rather than re-litigated.

## Known gaps

This fix was incomplete and round 2 found the hole: a file whose rows are ALL unusable parses to
nothing, so there is no Validate step, no result, and the download button lived inside the
result. See `mde-5`.

## Reviewer comments

`Reviewer: codex / gpt-5.5 / xhigh`
Range: `96cdb89..HEAD`. Verdict: **findings** (3). Finding 3 of 3.
Raw output: `.agents/review/out/session-review.txt`. The reviewer also reported it did not find
a mis-anchored Defender `ClickGateRegistry` entry in the changed page anchors.

Below CRITICAL, so closed on the coder-side guard proof per `.agents/decisions.md` 2026-08-31.

## Closeout

Fixed directly on `master` in `835a195`, completed by `mde-5`. Not yet pushed - push policy is
ask.
