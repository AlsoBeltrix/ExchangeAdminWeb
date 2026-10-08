# prog-6: Two page tests anchor on prose, and fail correct code

**Severity**: LOW - no operator-facing failure. It blocks correct changes with a false red.
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit carrying this record

## Evidence

`ExchangeAdminWeb.Tests/IntuneDevicesPageTests.cs:390` and
`ExchangeAdminWeb.Tests/RiskyUsersPageTests.cs:620` take
`body.LastIndexOf("finally", StringComparison.Ordinal)` over the RAW method text
(`MethodBody` reads raw `PageSource()` at `IntuneDevicesPageTests.cs:1273` and
`RiskyUsersPageTests.cs:757`). `IntuneDevicesPageTests.cs:842` repeats the anchor for the
Entra-half notification count.

**Found by tripping it, not by reading it.** The S3 agent's own trailing comment contained
the word "finally" below the admin send, moved the anchor past it, and failed the test
against correct code. It reworded the comment and left the test.

Codex then simulated inserting a harmless `// finally ...` comment below the send and
confirmed the predicate reports `sendAfterFinally=False` on both pages while code order is
still correct.

Predicted observable failure: a future comment-only edit inside either `ExecuteActionAsync`,
below the admin send, containing the word "finally", fails the page test with the code
correct. A false red that blocks a good change.

## Approach

**Anchor on the code view, keep the body raw, and reuse `ProgressScan.CodeView`.** Each of
the three sites now computes its indices over `ProgressScan.CodeView(body)` instead of over
`body`, and the `try` anchor and the `Email.SendAdminNotificationAsync(` count move to the
same view with them - the count would otherwise be inflated by a comment naming the send,
which is the same defect wearing the other hat.

Two existing solutions were available and the finding said to pick one rather than invent a
third. **`ProgressScan.CodeView` was chosen over `ClickGateSource.BlankComments` for two
reasons, both load-bearing here:**

1. `BlankComments` blanks a `//` comment only where no quote opens earlier on the same line
   (`ClickGateSource.cs`, the `(?m)(?<=^[^""'\r\n]*)//` arm). That rule exists to protect a
   `//` inside a string, and it costs the exact shape this finding is about: a trailing
   comment on a line that also carries a literal - `Logger.LogError(ex, "..."); // finally
   ...` - is left intact, and the anchor still moves. Every send in both of these methods is
   wrapped in a `try` whose `catch` logs a string literal, so that is not a hypothetical line
   shape in this method, it is the surrounding one. `CodeView` is a single-pass lexer and has
   no such arm.
2. `CodeView` blanks string literals as well, so a literal counterfeiting the anchor cannot
   move it either. That is not hypothetical either: a literal counterfeiting a progress proof
   is `prog-3`, measured on this same scan surface.

What made the fix small is `CodeView`'s offset preservation - pinned by
`ProgressRegistryTests.TheCodeViewIsTheSameLengthAndShapeAsTheSource`. An index found in the
code view addresses the same character of the raw body, so the two assertions whose needle IS
a string literal (`"Failed to send Intune Devices admin notification"` and its Risky Users and
Entra siblings) keep reading `body[finallyIndex..]` unchanged, and nothing else had to move.

`ProgressRegistryTests.cs` was REFERENCED and not modified; `ProgressScan` is `internal` in
the same test assembly, so no accessibility change was needed either.

The third option the finding mentioned - `IntuneDevicesPageTests.cs:705`'s
`body.IndexOf("finally", WriteIndex(body), ...)` - was not taken. It is safer than
`LastIndexOf` but it is still prose-anchored: a comment containing "finally" between the write
and the real `finally` moves that anchor too, just earlier rather than later, and an anchor
that slips in the passing direction is worse than one that slips in the failing direction.

## Files changed

- `ExchangeAdminWeb.Tests/IntuneDevicesPageTests.cs` - both anchors.
- `ExchangeAdminWeb.Tests/RiskyUsersPageTests.cs` - its one anchor.

Test project only. No page, service or registry was touched, so no version bump:
`docs/ProgressCoverage-Plan.md` Versioning (the test project is not shipped), and no module
behaviour changed.

## Guard proof

The mutation is the one the finding predicts: a comment containing the word "finally",
below the last admin send, inside `ExecuteActionAsync`, **with the code correct throughout**.
Applied to both pages at once, as a word inserted into an existing comment line so the page
line count does not move and nothing else in the suite is disturbed:

- `Components/Pages/IntuneDevices.razor` - "which is the only position that" -> "which is
  finally the only position", in the comment above `activity.Complete`, below both sends.
- `Components/Pages/RiskyUsers.razor` - "Completed LAST, below the administrator
  notification" -> "Completed LAST, finally below the administrator notification".

| Test files | Mutated pages | Result |
| --- | --- | --- |
| fixed (code view) | both comments | PASS 176/176 |
| at `dbcb116` (raw text) | the same two comments | FAIL 3/176 |

The second row is the whole argument: the page mutation is byte-identical in both runs and
only the test files differ, so what turns the red green is the anchor change rather than the
mutation being clumsy. The three failures are exactly the three registered sites, and their
messages are the predicted ones -
`RiskyUsers_ExecuteAction_NotifiesAdminsFromFinallyWrappedAgainstSendFailure` and
`IntuneDevices_..._NotifiesAdminsFromFinallyWrappedAgainstSendFailure` both report "the admin
notification is not sent from the finally block" against a send that is, and
`IntuneDevices_ExecuteAction_EntraHalfGetsItsOwnAdminNotification` reports
`Assert.Equal() Failure: Values differ` because its two-send count collapsed to zero.

Both pages restored byte-identical by `md5sum` (`1748c239...` IntuneDevices,
`fc3dad41...` RiskyUsers) and `touch`ed so MSBuild rebuilt.

**Scored on a filter, stated plainly.** The probe was run on
`FullyQualifiedName~IntuneDevicesPageTests|FullyQualifiedName~RiskyUsersPageTests`, 176
tests, not on the full 3674. What that hides is nothing for this mutation in particular - the
word is inserted into a comment, the line count is unchanged, and no registry in this repo
keys on comment text - but it is a filter, and the full suite was run only on the final
state.

## Known gaps

- **The body itself is still carved out of raw text.** `MethodBody` brace-matches over
  `PageSource()`, so a `{` or `}` inside a comment or a literal can still end a body early or
  late. Fixing that means running the brace walk over the code view and slicing the raw text
  at the result, which is a change to a helper 75 assertions share; it is a different finding
  and is not taken here. No page has that shape today.
- **Two sites in these files keep deliberate raw-text reads**, and they are the string-literal
  assertions - `Assert.Contains("Failed to send ...", body[finallyIndex..])`. Those needles
  are literals, so they cannot read the code view by construction. A comment containing that
  sentence would satisfy one of them. That trade is the price of keeping the message
  assertions at all, and it fails safe: a comment can only make them pass, and the send they
  guard is already pinned by the code-view assertions above them.
- **Every other prose anchor in the suite is untouched.** This fixes the three sites the
  finding names. `grep -n 'LastIndexOf..finally' ExchangeAdminWeb.Tests/*.cs` now returns
  nothing, but other words that occur in both code and prose are still searched raw
  elsewhere.

## Reviewer comments

Raised by the implementing agent of `b95bd5d`/`3c2df38` after tripping it; confirmed and
reproduced by codex reviewing `1cb1284..3c2df38`, which found **no product defect** in the
two handlers themselves.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
Raw output: `.agents/review/prog-s3a.result.json`.

## Closeout

Fixed directly on `master` in the commit that carries this record. Verification on that
commit: `dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors, `dotnet test
ExchangeAdminWeb.slnx` **3674 passed / 0 failed / 3 skipped** (unchanged - no test added, three
existing tests re-anchored), `dotnet format ExchangeAdminWeb.slnx --verify-no-changes
--no-restore` exit 0, `git diff --check HEAD` clean, ClickGate filter 316 passed, every added
line ASCII. Test-project only, so no version bump.

Row `[v]` in `.agents/review/index.md`: the fix is on main and guard-proved, and the push is
outstanding (`.agents/push-policy.md` is ask).

No verification round dispatched: LOW, and `.agents/decisions.md` 2026-08-31 closes everything
below CRITICAL on the coder-side guard proof.
