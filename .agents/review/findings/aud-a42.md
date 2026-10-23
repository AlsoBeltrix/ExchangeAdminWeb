# aud-a42: The Program.cs start-up order guard scans raw source text, so a comment can satisfy it

**Severity**: LOW - a test guard that protects an audit/security start-up behaviour can pass for the wrong
reason. `CircuitRevalidationTests.ProgramCs_ResolvesTheOffSwitchAndReportsItAtStartup` finds
`MembershipRevalidatingAuthenticationStateProvider.ReportIfDisabled(` and `app.Run()` in `Program.cs` by
`IndexOf` on the raw text; a comment naming the call before `app.Run()` satisfies it while the real call is
removed or moved. The aud-a28w branch tripped the inverse (a comment naming `app.Run()` earlier made it
fail) and fixed the comment, not the guard.
**Status**: Merged `86df002a`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-a885c578973c1cad3`
**Commit**: `086501ad` (plan `6db3a417`)
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

## Fix

`ExchangeAdminWeb.Tests/CircuitRevalidationTests.cs`: `ProgramCode()`, Program.cs through `RazorSyntax.CodeView` - comments and literals
blanked to same-length spaces, newlines kept - read by its three Program.cs guards (`ProgramCs_RegistersTheRevalidatingProviderAndTheGate`,
still squashed; `ProgramCs_ResolvesTheOffSwitchAndReportsItAtStartup`; `ProgramCs_RefusesStaticGroupsThatCannotBeRevalidatedBeforeTheAppIsBuilt`).

`DefenderServiceRegistrationTests.cs` (both reads), and the `ReadProgram` helpers of `BulkJobProcessorWiringTests.cs`, `ConfigDbCliTests.cs`
and `ServicerLifetimeTests.cs`: the same view. Every check after the read is unchanged, and every one passes on the current Program.cs.
Program.cs is not changed. The three scans the survey lists as already stripped are not changed.

## Guard proof

The aud-a41 driver (`a885-mutate.ps1`), filtered to the nine classes that read Program.cs (`a885-filter-a42.txt`: the six converted,
`PowerShellWarmUpTests`, `PowerShellStartupTests`, `GlobalProgressWiringTests` and `AllowlistConcurrentSaveTests` - 192 tests): the
change staged; MD5; mutate Program.cs by exact text; touch; build; filtered run; `git checkout --` from the index; touch; MD5
re-confirmed; rebuild; the same filter. Each Program.cs mutant built with 0 errors, came back to its MD5, and the restored tree ran
192/192 after each row. The two controls (a session-local `a885-control.ps1`) pair a Program.cs mutant with that guard's read put back
to raw text, run the guard, and restore both files from the index to their MD5s (Program.cs `8A88AAE01507CE76C90538923BED65F9`,
`CircuitRevalidationTests.cs` `0A214FC81010C8B10E03EE2FF8A56C73`, raw-read mutant `C6335AC0399491F3A4693BA6482E6F6A`).

| # | Mutation (call site) | MD5 before | MD5 mutant | MD5 after | Result |
|---|---|---|---|---|---|
| 1 | Program.cs: the real `MembershipRevalidatingAuthenticationStateProvider.ReportIfDisabled(...)` call replaced by a comment naming it, above `app.Run()` | `8A88AAE01507CE76C90538923BED65F9` | `C5B1A43D5BBAAFB7D34E25AB3262C344` | `8A88AAE01507CE76C90538923BED65F9` | fails: `CircuitRevalidationTests.ProgramCs_ResolvesTheOffSwitchAndReportsItAtStartup` ("The disabled switch must be reported at startup: after builder.Build(), before app.Run().") |
| C1 | control: row 1's Program.cs with that guard reading raw text again | `8A88AAE01507CE76C90538923BED65F9` | `C5B1A43D5BBAAFB7D34E25AB3262C344` | `8A88AAE01507CE76C90538923BED65F9` | passes - the comment satisfies the raw scan: the hole this closes |
| E1 | extra, Program.cs: `builder.Services.AddSingleton<DefenderEndpointDeviceService>();` commented out | `8A88AAE01507CE76C90538923BED65F9` | `3C7E7C0C96C782D90A548A5A47018B8C` | `8A88AAE01507CE76C90538923BED65F9` | fails: `DefenderServiceRegistrationTests.TheDefenderServiceItselfIsRegistered` |
| E2 | extra, Program.cs: a comment naming `app.Run()` added above the `ReportIfDisabled` call | `8A88AAE01507CE76C90538923BED65F9` | `C3B27CC0D453EDA50F58B461C2B60CCD` | `8A88AAE01507CE76C90538923BED65F9` | passes, 192/192: a comment no longer moves the guard |
| C2 | control: row E2's Program.cs with that guard reading raw text again | `8A88AAE01507CE76C90538923BED65F9` | `C3B27CC0D453EDA50F58B461C2B60CCD` | `8A88AAE01507CE76C90538923BED65F9` | fails ("...before app.Run().") - the aud-a28w trip, which that branch fixed by rewording its comment |

The MD5s are this commit's bytes.

## Gates - 2026-10-23 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 5,933 passed /
  3 skipped (the three known AD skips) / 0 failed, 5,936 total, 5m40s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

Test-only: nothing ships. No directory, Exchange Online or other service was called; the scans read the repository's own file. No
coder self-review verdict is claimed.

## Known gaps

- `RazorSyntax.CodeView` blanks string literals too, so a future Program.cs check that needs a literal's text (a configuration key, a
  route) must read `RazorSyntax.CommentsBlanked` instead; none of today's does.
- `GlobalProgressWiringTests` keeps its own regex comment stripper, whose known hole (`//` or `/*` inside a literal, review finding
  prog-4b) does not occur in Program.cs today; not converted, as it is not a raw-text scan.
- Program.cs scans outside the test project (none found) are not covered.

## Branch-tip gates - 2026-10-23 (`f219f2e8`: the three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 5,933 passed /
  3 skipped (the three known AD skips) / 0 failed, 5,936 total, 5m46s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
- The comments commit `f219f2e8` changes comment lines only: the code lines of all 19 source files the fixes touch, whole-line comments
  and blank lines stripped, hash the same at `086501ad` and `f219f2e8`.