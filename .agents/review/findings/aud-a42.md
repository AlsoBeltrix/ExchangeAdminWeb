# aud-a42: The Program.cs start-up order guard scans raw source text, so a comment can satisfy it

**Severity**: LOW - a test guard that protects an audit/security start-up behaviour can pass for the wrong
reason. `CircuitRevalidationTests.ProgramCs_ResolvesTheOffSwitchAndReportsItAtStartup` finds
`MembershipRevalidatingAuthenticationStateProvider.ReportIfDisabled(` and `app.Run()` in `Program.cs` by
`IndexOf` on the raw text; a comment naming the call before `app.Run()` satisfies it while the real call is
removed or moved. The aud-a28w branch tripped the inverse (a comment naming `app.Run()` earlier made it
fail) and fixed the comment, not the guard.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (settings-batch-5, 2026-10-22)

## Evidence

At `8151de3e`: `ExchangeAdminWeb.Tests/CircuitRevalidationTests.cs` around line 1300 (the `IndexOf` scan);
`ExchangeAdminWeb.Tests/PowerShellWarmUpTests.cs` (the source-order test that strips comments first, the
shape to copy); `RazorSyntax.CodeView` or the equivalent comment-stripping helper in the test project.

## Approach (to be written by the implementing agent before code)

- The guard scans a comment-stripped code view of `Program.cs` (the same helper the warm-up order test
  uses), then applies its `IndexOf` order checks; any other raw-text `Program.cs` scan in the test project
  does the same (list them in the record).
- Mutation at the call site: the raw text scanned again, with a comment that names the call before
  `app.Run()` and the real call removed - the guard must fail.

## Guard proof

Pending.
