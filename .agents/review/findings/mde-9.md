# mde-9: Two rows of the same non-matching address are reported as one

**Severity**: MEDIUM - the errors CSV undercounts against the exact ruling it was built to
satisfy. An operator reconciling the report against their workbook finds one row accounted for
and one not.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `3f16528`

## Evidence

Not a Defender finding - the fourth Comms-10k one, same provenance as `mde-3`, `mde-5` and
`mde-7`.

`Components/Pages/Comms10k.razor:482` (pre-fix) dropped every repeat unconditionally:

```csharp
if (!seen.Add(val))
{
    // Not an error, and deliberately NOT reported: the address resolves on its
    // first appearance, so the person is on the list. Reporting it would put a
    // row in the errors CSV for somebody who was included.
    continue;
}
```

**The comment states a condition the code does not check.** "The address resolves on its first
appearance" is true for a duplicate of an address AD matches. It is false for a duplicate of
one AD does not - and then nobody was included, so the justification for silence is gone. The
resolver receives one row, produces one rejection, and the CSV reports one rejected row for two
rejected rows in the file.

This is the same decision `mde-3` recorded and pinned with a test. The decision is not wrong;
it was being applied outside its own stated premise.

## Predicted observable failure

Upload a workbook listing `ghost@example.com` twice, where that address has no AD match (or an
ambiguous one, or no distinguished name). The alert says "1 rejected" and the errors CSV has
one row. The file had two. An operator reconciling row counts is short by one with nothing
explaining it, which is precisely the "manually identify the affected users" complaint the
whole feature exists to answer.

## Approach

Keep the decision where its reasoning holds and drop it where it does not.

The parse still records NO rejection for a duplicate - it keeps the row, with its RAW text, in
a new `duplicateRows` field. `AllRejections` then emits one rejection per kept duplicate **only
if the resolver rejected the address it duplicates**, carrying that rejection's reason:

```csharp
foreach (var (raw, address) in duplicateRows)
{
    var match = fromResolver.FirstOrDefault(
        r => string.Equals(r.Address, address, StringComparison.OrdinalIgnoreCase));
    if (match != null)
        all.Add(new Comms10kAddressResolver.Rejection(raw, match.Reason));
}
```

A duplicate of a RESOLVED address is still silent, and `mde-3`'s test still pins that. Before
validation nothing is emitted either, which is correct: until the resolver has answered, it is
not yet known which case this is.

`duplicateRows` is cleared at both sites that clear `rejectedBeforeLookup`, so a second upload
cannot inherit the first one's rows.

## Files changed

- `Components/Pages/Comms10k.razor`
- `Modules/ModuleCatalog.cs` (Comms-10k `1.4.3` -> `1.4.4`)
- `ExchangeAdminWeb.Tests/Comms10kRejectionReportTests.cs`

## Guard proof

`ADuplicateOfAnAddressThatDidNotResolveIsReported` pins that the parse keeps the raw duplicate,
that `AllRejections` walks them and matches against the resolver's rejections, that it emits
only on a match, and - the reviewer asked about stale state specifically - that the number of
`duplicateRows = new()` resets equals the number of `rejectedBeforeLookup = new()` resets.
`ADuplicateOfAnAddressAlreadyAcceptedIsNotReportedAsRejected` is unchanged in substance and
still green: the parse records no rejection.

| Mutation | Result |
| --- | --- |
| `if (match != null)` -> `if (false)` (never emit) | FAIL; restored -> passes |
| delete one of the two `duplicateRows = new()` resets | FAIL; restored -> passes |

Page restored byte-identical after both (md5 verified) and touched.

**A test-harness defect was found and fixed on the way.** `Member()` in this test class stopped
at the first semicolon, which is right for an expression body and wrong for a block one - it
returned the first statement and called it the member. Its own doc comment claimed it handled
both. That was invisible while every member it read was expression-bodied; `AllRejections`
became a block here and two assertions started failing against correct code. It now brace-
matches. **Any assertion that read a block-bodied member through this helper has been checking
one statement**, which is a false-green class, not just this finding's problem.

## Coder dispute

None on the finding. One scoping note: this changes behaviour that `mde-3` recorded as a
deliberate decision, so the fix was shaped to preserve that decision exactly where its stated
premise holds rather than to overturn it. The owner has not been asked, because the ruling the
decision serves - *"all of the non-matching addresses need to be reported to the user... with
the reason it was rejected"* - is the one being honoured here. **Flagged rather than assumed
settled.**

## Known gaps

Matching is by the normalised address against the resolver's `Rejection.Address`. The resolver
is given the trimmed value, so the two agree today; if the resolver ever rewrote the address it
reports, the match would silently stop firing and duplicates would go unreported again. Nothing
pins that contract.

Source-text guard over a `.razor` file, not behaviour - `AllRejections` is a private page
property and there is no bUnit harness.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Round 4 of the stream, over `96cdb89..ec2bdc8`. Verdict: **findings** (2). Finding 2 of 2.
It noted explicitly that the existing duplicate guard "passes this broken path because it only
asserts the duplicate `continue`" - correct, and the reason the new test pins `AllRejections`
instead.
Raw output: `.agents/review/out/mde-r4.txt`.

Below CRITICAL, so closed on the coder-side guard proof per `.agents/decisions.md` 2026-08-31.

## Closeout

Fixed directly on `master`. Round 5 came back CLEAN, which closes the stream. Not yet pushed - push
policy is ask.
