# prog-5: CloudPasswordReset's window does not pin its delivery tail

**Severity**: MEDIUM - guard false-negative on the most sensitive operation in the app
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit carrying this record

## How this was found, which is the point

**Not by a reviewer.** The agent implementing the OutOfOffice fix (`2d93c26`) reasoned about
where its own closing pin belonged, looked across at the already-landed CloudPasswordReset
entry, and reported that it stops short. Codex then verified it independently.

That entry was reviewed and passed twice: once as the original S2 fix, and again after
`prog-1` added a second covered call to it. **Neither review caught it, and an implementer
working on a different page did.**

## Evidence

`ExchangeAdminWeb.Tests/ProgressRegistry.cs:479` registers
`CloudPasswordReset.ExecuteResetAsync` with exactly two covered calls:
`ResetService.DeriveDestination(` and `ResetService.ResetPasswordAsync(`.

The handler does not end at the PATCH. `Components/Pages/CloudPasswordReset.razor:464` is
the reset; `:504` sends `Email.SendCloudPasswordResetAsync`; `:528` awaits
`NotifyAdminsAsync`; the `finally` completes at `:542`.

Codex's reproduction: adding `activity.Complete` immediately after `ResetPasswordAsync` in a
disposable copy - `ProgressRegistryTests` **still passed 25/25**.

Predicted observable failure: a future edit moves the completion up to the PATCH, the
registry stays green, and the operator watches the bar go idle during password delivery and
admin notification **after an irreversible reset** - the one operation on these pages where
an operator most needs to know work is still in flight.

## Coder dispute

None. `prog-1` fixed the OPENING end of this window and nobody checked the closing end in
the same pass. The multi-call shape that `prog-1` introduced is exactly what makes this
fixable in one line.

## Approach

Registry only. The entry gains a THIRD covered call, `NotifyAdminsAsync(`, and the page is
not touched - its runtime shape was already right (`Begin` above the `try`, `Complete` in
the `finally` after the notification); only the assertion stopped short of it.

**Which call is the genuinely last one was worked out from the handler, not taken from the
finding's `:528`.** Everything below the PATCH, in source order: the indeterminate-write
notification (`:477`), `RefuseAsync` on a failed write (`:483`), the revealed-path
notification (`:500`), the delivery email (`:504`), the send-failed notification (`:519`),
and the sent-and-delivered notification (`:528`). The last remote call in the method's own
body is therefore `NotifyAdminsAsync`, not the delivery email - and because conditions 2 and
3 are checked at EVERY occurrence of a named fragment, naming `NotifyAdminsAsync(` pins all
four at once, the `:528` one being the one that sits below everything else.

Two calls were considered and deliberately not named:

- **`Email.SendAdminNotificationAsync`** is the call that actually leaves the process, and
  it is what the `ADAttributeEditor` entry (`f1257bd`) names on its page. It cannot be named
  here: it runs inside the `NotifyAdminsAsync` helper, not in `ExecuteResetAsync`'s own body,
  and condition 1 requires the named fragment to be in the method's own code. That rule is
  the one that caught `BlockedSenders.ConfirmUnblock` and is not worth weakening for this.
  The cost is recorded under Known gaps.
- **`Email.SendCloudPasswordResetAsync(`** at `:504` is a real remote call, and naming it
  would add a string and no constraint. Condition 3 at `:528` is strictly stronger than
  condition 3 at `:504` (one activity, one `Complete`: still open at 528 implies still open
  at 504), and condition 2 is already held further up by `ResetService.DeriveDestination(`
  at `:427`. A pin that forbids nothing the existing pins do not forbid is padding.

## Files changed

- `ExchangeAdminWeb.Tests/ProgressRegistry.cs`

## Guard proof

The mutation named in the finding, applied to `Components/Pages/CloudPasswordReset.razor`:
`activity.Complete(true);` inserted immediately after
`var outcome = await ResetService.ResetPasswordAsync(...)` at `:464`.

| Registry entry | Mutated page | Result |
| --- | --- | --- |
| the new three-call entry | early `Complete` at the PATCH | FAIL 1/25 - `AReportedOperationIsStillReportingWhenTheCoveredCallRuns`, four lines, all `'activity.Complete(' runs at offset 7250, before 'NotifyAdminsAsync(' at 8024 / 9117 / 10247 / 10699` |
| the old two-call entry | the same early `Complete` | PASS 25/25 - the defect, reproduced here and not taken from the finding |

The second row is the whole argument: the page mutation is byte-identical in both runs and
only the registry entry differs, so what catches it is the added pin rather than the
mutation being clumsy. It also reproduces codex's 25/25 locally rather than inheriting it.

The failure names ONLY `NotifyAdminsAsync(`, which is the isolation the probe is for:
`ResetService.ResetPasswordAsync(` at `:464` sits above the injected `Complete` and still
passes, and `ResetService.DeriveDestination(` at `:427` likewise - so the probe proves the
new pin and nothing else is doing the work.

Page restored byte-identical (`md5sum -c`) and `touch`ed so MSBuild rebuilt; registry filter
back to 25/25.

## Known gaps

- **The pin is on a page-local helper, so it is one indirection away from the thing that
  waits.** `NotifyAdminsAsync` is what the registry can name; `Email.SendAdminNotificationAsync`
  inside it is what blocks. If someone makes `NotifyAdminsAsync` local-only, the pin stays
  green and means less. This is condition 1 working as designed - crediting a helper's
  contents is exactly what `BlockedSenders.ConfirmUnblock` exploited - and the registry's own
  "an entry proves only the calls it NAMES" limitation already covers it.
- **Source order is still not control flow.** The pin forbids a `Complete` lexically above
  the notifications; it cannot prove the sent-and-delivered path is reached, nor stop an
  early `return` before `:528` on some future branch. Unchanged from `prog-1`.
- Codex's full/normal build stalled again with no output; it killed the process tree and used
  no-build filtered runs. Probes B and C from the OutOfOffice slice were reproduced in a
  disposable tree. Third review in a row where the solution-level build stalled for the
  reviewer but not for the implementing agents - worth its own look if it recurs. **The
  implementing agent's full solution build and full suite both ran normally for this fix**, so
  the asymmetry still has not reproduced outside the reviewer's environment.

## Reviewer comments

Raised by the implementing agent of `2d93c26`; verified by codex during the review of
`28a252e..2d93c26`, which was otherwise **sound**.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
Raw output: `.agents/review/prog-s2b.result.json`.

## Closeout

Fixed directly on `master` in the commit that carries this record. Verification on that
commit: `dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors, `dotnet test
ExchangeAdminWeb.slnx` **3674 passed / 0 failed / 3 skipped** (unchanged - the fix adds no
test, it brings one more call inside three existing assertions), `dotnet format
ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0, `git diff --check HEAD`
clean, ASCII only. Test-project only, so no version bump: the registry is not shipped
(`docs/ProgressCoverage-Plan.md`, Versioning), and the page was not edited. Row `[v]` in
`.agents/review/index.md`: the fix is on main and guard-proved, and the push is outstanding
(`.agents/push-policy.md` is ask).

No verification round dispatched: MEDIUM, and `.agents/decisions.md` 2026-08-31 closes
everything below CRITICAL on the coder-side guard proof.
