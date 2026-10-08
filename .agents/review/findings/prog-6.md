# prog-6: Two page tests anchor on prose, and fail correct code

**Severity**: LOW - no operator-facing failure. It blocks correct changes with a false red.
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

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

Pending. Codex's recommendation: anchor on code, not prose - strip comments and literals
before searching, or use the safer pattern already in the same file at
`IntuneDevicesPageTests.cs:705` (`body.IndexOf("finally", WriteIndex(body), ...)`) with
enough brace context to prove the send is inside the block.

Note the repo already solved this problem twice elsewhere: `ClickGateSource` blanks comments
before every scan, and `ProgressScan.CodeView` blanks comments AND literals (prog-3). The fix
is to use what exists rather than invent a third thing.

## Reviewer comments

Raised by the implementing agent of `b95bd5d`/`3c2df38` after tripping it; confirmed and
reproduced by codex reviewing `1cb1284..3c2df38`, which found **no product defect** in the
two handlers themselves.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
Raw output: `.agents/review/prog-s3a.result.json`.

## Closeout

Pending.
