# aud-a42: The Program.cs start-up order guard scans raw source text, so a comment can satisfy it

**Severity**: LOW - a test guard that protects an audit/security start-up behaviour can pass for the wrong
reason. `CircuitRevalidationTests.ProgramCs_ResolvesTheOffSwitchAndReportsItAtStartup` finds
`MembershipRevalidatingAuthenticationStateProvider.ReportIfDisabled(` and `app.Run()` in `Program.cs` by
`IndexOf` on the raw text; a comment naming the call before `app.Run()` satisfies it while the real call is
removed or moved. The aud-a28w branch tripped the inverse (a comment naming `app.Run()` earlier made it
fail) and fixed the comment, not the guard.
**Status**: In progress (plan below); approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a885c578973c1cad3`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `4830230a`. One commit `test(startup): the Program.cs source
guards read its code, not its comments (aud-a42)`, after aud-a40 on this branch. Test-only: nothing ships, no version moves.
Program.cs is not changed.

Survey at `4830230a` - every Program.cs scan in the test project:

| File | Test | Reads Program.cs as |
|---|---|---|
| `CircuitRevalidationTests.cs` 1270 | `ProgramCs_RegistersTheRevalidatingProviderAndTheGate` | raw text, whitespace squashed |
| `CircuitRevalidationTests.cs` 1300 | `ProgramCs_ResolvesTheOffSwitchAndReportsItAtStartup` (this finding's) | raw text |
| `CircuitRevalidationTests.cs` 1323 | `ProgramCs_RefusesStaticGroupsThatCannotBeRevalidatedBeforeTheAppIsBuilt` | raw text |
| `DefenderServiceRegistrationTests.cs` 29, 60 | `EveryConstructorDependencyOfTheDefenderServiceIsRegistered`, `TheDefenderServiceItselfIsRegistered` | raw text |
| `BulkJobProcessorWiringTests.cs` 30-43 (`ReadProgram`) | `EveryProcessorIsBothMappedAndConstructible` | raw text |
| `ConfigDbCliTests.cs` 25-38 (`ReadProgram`) | `Program_DispatchesTheDbModeBeforeTheLoggerAndBeforeTheWebHostBuilder` | raw text |
| `ServicerLifetimeTests.cs` 85-98 (`ReadProgram`) | `TheServicerService_IsRegisteredAsASingleton`, `TheSingletonConsumersOfTheServicer_AreStillSingletons` | raw text |
| `PowerShellWarmUpTests.cs` 47 | `Program_StartsTheWarmUp_AfterTheHostIsBuilt_AndBeforeItRuns` | `RazorSyntax.CodeView` - the shape to copy; unchanged |
| `GlobalProgressWiringTests.cs` 22, 352 | `NoSingletonTakesThePerCircuitProgressService`, the page-load middleware case | its own regex comment stripper - not raw; unchanged (its known hole, `//` or `/*` inside a literal, does not occur in Program.cs) |
| `AllowlistConcurrentSaveTests.cs` 205 | Program.cs among the production sources it scans | `RazorSyntax.CommentsBlanked`; unchanged |

1. Every raw read above scans `RazorSyntax.CodeView(...)` instead - comments and string literals replaced by same-length spaces,
   newlines kept, so every index, line anchor and whitespace squash is as before - and then applies its existing checks unchanged.
   Literals going too is deliberate and costs nothing here: checked, every one of these patterns is code, none text inside a literal.
2. The record's list is the table above.

Tests: the converted tests pass on the current Program.cs. Mutations at the call site: (1) Program.cs: the real
`MembershipRevalidatingAuthenticationStateProvider.ReportIfDisabled(...)` call removed and a comment naming it left above
`app.Run()` - `ProgramCs_ResolvesTheOffSwitchAndReportsItAtStartup` must fail; control (C1): the same Program.cs with that guard's
read back to raw text - it passes, which is the hole closed; extra (E1) Program.cs: the Defender service's registration commented
out - `TheDefenderServiceItselfIsRegistered` must fail; (E2) a comment naming `app.Run()` above the `ReportIfDisabled` call (the
aud-a28w trip) - the guard must still pass.

## Guard proof

Pending.
