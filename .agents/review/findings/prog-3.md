# prog-3: A string literal can still counterfeit the progress proof

**Severity**: MEDIUM - same observable failure class as prog-1, reached by a different route
**Status**: Verified
**Branch**: - (direct to main)
**Commit**: the commit carrying this record

## Evidence

The prog-2 fix added literal blanking for COVERED-CALL lookup only
(`ProgressRegistryTests.cs:145`). `ActivitiesIn` still scans the raw body
(`:389-397`) using `ActivityOpened` (`:74`).

So a `Reported` method containing a string literal with the text
`using var activity = Progress.Begin(` before the real covered call makes the regex believe
an activity is open. Conditions 2 and 3 then pass with **no real activity at all**.

A second defect in the new blanker itself: `SkipLiteral` (`:289`) marks any `$"..."` as
verbatim, so an escaped quote inside a normal interpolated string leaves the remainder
unblanked. Codex's probe left `ResetService.DeriveDestination(` visible in the blanked code
and conditions 1-3 evaluated true.

Predicted observable failure: a future edit puts a slow call outside the real window, or
behind a helper, while a string literal keeps the expected text in the method. Tests stay
green; the operator waits with the bar idle.

## Coder dispute

None. The asymmetry was deliberate and defended in the prog-2 handback - blank literals when
locating calls, not when discovering handlers, because markup attributes are quoted. The
reasoning was right about markup and wrong about `ActivitiesIn`, which reads C# bodies where
no such constraint applies. **Closing one literal hole and leaving its twin open is a worse
outcome than not having looked**, because the handback reads as though literals were handled.

**Seventh guard finding in six days.** prog-1 was "the rule does not cover the second call";
this is "the rule can be satisfied by text that is not code". Both are the same root: a
regex has no notion of what is code and what is data.

## Approach

The minimum closure, deliberately: no Roslyn. That remains a live owner decision, open on
`leak-1`, and taking it here would have settled it by implementation.

**One code view, built in one lexical pass.** `StripStringLiterals` is replaced by
`ProgressScan.CodeView`, which walks the RAW file once and blanks whichever construct starts
at each position - a block comment, a razor comment, `//` to end of line, or a string or char
literal with its delimiters - using same-length blanks. `PageScan` now carries both views:
`Source` (comments blanked, literals intact) and `Code` (comments and literals blanked).
Indices match, so a method span found in one addresses the same text in the other.

Blanking comments and literals in ONE pass rather than two is load-bearing and was not in the
original scope. Comment-first ends a line at any `//`, including one inside a string, which
deletes that string's closing quote; a VERBATIM literal then runs on past its line, the next
literal pairs one quote out of step, and real string content is left outside every literal and
readable as code. Demonstrated, and now pinned by a test row.

**Which scan reads which view.** Written out because the prog-2 fix got exactly this half
right and half wrong, and a reader has no other way to check it:

| Scan | View | Why |
| --- | --- | --- |
| `Handlers` - the six markup attribute patterns | DISCOVERY | Handler names ARE quoted attribute values; the code view has blanked them |
| `Handlers` - lifecycle methods and `UnextractedLambdas` | DISCOVERY | Same pass over the same markup |
| `DeclaredMethods`, `MatchDelimiter`, `StatementEnd` | DISCOVERY | Method spans; see Known gaps |
| Condition 1 `AReportedOperationContainsTheCallItClaimsToCover` | CODE | "Does this method CALL x" |
| Condition 2 `AReportedOperationOpensItsActivityBeforeTheCallItCovers` | CODE | Both the call and the activity - **this was the finding** |
| Condition 3 `AReportedOperationIsStillReportingWhenTheCoveredCallRuns` | CODE | Call, activity, Complete, scope end |
| `ActivitiesIn` and `EnclosingBlockEnd` | CODE | Read the raw body before this fix; that is the defect |
| `ADispatchersExemptionNamesAMethodItActuallyCallsAndThatIsItselfRegistered` | CODE | Same literal-counterfeit class - the twin hole, closed with it |
| `EveryPageUnderComponentsPagesIsRegistered` and the other registry-shape tests | neither | They read `ProgressRegistry`, not page source |

The asymmetry is now asserted rather than described:
`HandlerDiscoveryStillReadsQuotedMarkupAttributes` fails if discovery is ever moved to the code
view - a change that otherwise collapses the operation list to empty and leaves every assertion
over it passing vacuously.

**`SkipLiteral` rewritten.** The prefix is read by scanning back over `@` and `$`, and only `@`
makes a literal verbatim; `$` makes it interpolated, which still honours backslash escapes. A
verbatim literal escapes its quote by doubling it and may span lines. A raw literal (three or
more quotes, no `@` prefix) closes on a quote run at least as long as the one that opened it,
and two quotes is the empty string rather than a raw literal. An interpolation hole is skipped
with brace depth, recursing into literals inside it, because since C# 11 a hole may carry its
own quoted string. A backslash immediately before a newline escapes nothing.

## Files changed

- `ExchangeAdminWeb.Tests/ProgressRegistryTests.cs` - the only file. No product code, no
  registry entries, no version bump.

## Guard proof

Two probes on `Components/Pages/CloudPasswordReset.razor`, each with a matching `Reported`
entry in `ProgressRegistry.cs` naming `Task.Delay(`. Same probe both times; only the scanner
differs.

Probe A, the finding itself - a counterfeit activity and no real one. The method body was
`var pretend = "using var activity = Progress.Begin(";` followed by `await Task.Delay(1);`.

| Scanner | Result |
| --- | --- |
| before the fix | **PASS, 12/12** - the defect, reproduced: no activity in the method and all three conditions satisfied |
| after the fix | FAIL - condition 2: "CloudPasswordReset.razor: ProbeCounterfeitActivity opens no activity before 'Task.Delay(' at offset 153 (it begins no activity at all)" |

Probe B, the second defect - the interpolated-string mis-read, with a REAL activity open so
that only the literal handling is under test. The body was
`using var activity = Progress.Begin("Probe", "probe", ActivitySize.Steps(1));` followed by
an interpolated string holding escaped quotes around `Task.Delay(`.

| Scanner | Result |
| --- | --- |
| before the fix | **PASS, 12/12** - the dollar prefix read as verbatim, the literal ended on the escaped quote, and the payload sat outside every literal |
| after the fix | FAIL - condition 1: "CloudPasswordReset.razor: ProbeCounterfeitActivity does not call 'Task.Delay('" |

Both probes removed, both files restored from git and timestamps refreshed.

The new scanner tests were then mutation-checked, because a test that cannot fail is the thing
this finding is about. Each mutation was applied alone, built, and run:

| Mutation | Caught by |
| --- | --- |
| prefix read one character back again, so `$` is verbatim | the interpolation-hole row of `NoStringFormSmugglesTextPastTheBlanker` |
| raw-literal arm disabled | `ARawStringLiteralIsBlankedWholeIncludingItsNewlines` |
| `CodeView` made two-pass, comments stripped first | the verbatim `//` row of `NoStringFormSmugglesTextPastTheBlanker` |
| discovery moved to the code view | `HandlerDiscoveryStillReadsQuotedMarkupAttributes`, and **nothing else** - which is the vacuity that test exists to catch |

## Known gaps

Bypasses attempted against the new blanker. The first list is CLOSED, each one pinned by a test
row; the second list SURVIVED.

Closed: an interpolated string with escaped quotes (the finding's own second defect); an
escaped backslash immediately before the closing quote; verbatim doubling; a quoted string
inside an interpolation hole; a char literal holding a double quote; raw literals, single-line
and multi-line; `//` inside a verbatim literal; a quote inside a `//` comment.

Surviving, in descending order of how reachable each is:

- **A preprocessor-disabled region still counts as code.** `#if NEVER` around a
  `using var activity = Progress.Begin(` would be seen by `ActivitiesIn` and excluded by the
  compiler. The code view is lexical and has no notion of conditional compilation. No page
  under `Components/Pages` uses `#if` today. Same root as the two findings before it: regex
  cannot see what the compiler sees. Closable honestly only with a real parser.
- **Unreachable code still counts as code.** `if (false) { using var activity = ... }`
  satisfies conditions 2 and 3 exactly as before. This is the control-flow half of the root
  cause, already recorded on prog-1 and prog-2 and not touched here.
- **`DeclaredMethods` still reads the DISCOVERY view**, so a method declaration written inside
  a string literal would enter the method table as a phantom. It is not the counterfeit this
  finding is about - a registry entry naming such a method is hand-written code, not a drive-by
  - and moving method discovery to the code view changes which methods the whole suite sees,
  `EventSubscriptionLifetimeTests` included, since it shares `DeclaredMethods`. Left
  deliberately, recorded rather than folded in.
- **A covered call written ONLY inside an interpolation hole is now invisible.** Holes are
  blanked with the literal around them, so a call made only inside one would fail condition 1.
  That is the fail-closed direction - it reports a missing call rather than inventing one - and
  no page does it today, but it is a false alarm waiting to happen, and the answer if it ever
  fires is to name a different call, not to loosen the blanker.
- **An unquoted markup attribute is still invisible**, unchanged from prog-2 and still the
  named next action there.

One attempt is recorded as NOT discriminating rather than as a pass: a `//` inside a plain
single-line string. A non-verbatim literal ends at the newline, so a comment-first pass would
swallow the payload along with the rest of that line instead of exposing it. The row is kept
with that written on it, because the obvious shape to try should not look untried.

## What this review CLEARED

Recorded because it was the thing most suspected and it held up:

- **The 25 `@onchange` exemptions are sound.** Codex spot-checked across AdminSettings,
  ModuleConfig, Migration, GroupManagement, SelfServiceGroups, MessageTrace and
  ADAttributeEditor and found none that should be a new `KnownGap`. The sweep was careful
  work, not exemption-washing.
- Multi-call checking and the anti-vacuity test are sound apart from the literal issue.
- The five recorded open bypasses are "mostly accurate": unquoted attributes and the weak
  `op(`/`work(` fragments are closable follow-ups; method-group dispatch and the Pages-only
  scope are Roslyn or design-scope issues, correctly left open.
- **The full suite did NOT stall this time**: 3661 passed / 0 failed / 3 skipped in 4m57s,
  independently run. The two earlier stalls were transient, not a standing property.

## Reviewer comments

Change review of `931d005..8456780`.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
Raw output: `.agents/review/prog12.result.json`.

## Closeout

Fixed directly on `master` in the commit that carries this record. Test-project only, so no
version bump (`docs/ProjectConstitution.md`, Deployment And Versioning).

Verification on that commit, all observed: `dotnet build ExchangeAdminWeb.slnx -c Release`
0 errors; `dotnet test ExchangeAdminWeb.slnx` **3674 passed / 0 failed / 3 skipped, 3677
total** - the full suite, which did not stall; `dotnet format ExchangeAdminWeb.slnx
--verify-no-changes --no-restore` clean; `git diff --check HEAD` clean; ASCII only. The count
is 3661 + 13: thirteen new scanner tests, eight of them rows of one theory.

Row `[v]` in `.agents/review/index.md`: on main and guard-proved, push outstanding
(`.agents/push-policy.md` is ask). No verification round dispatched - MEDIUM, and
`.agents/decisions.md` 2026-08-31 closes everything below CRITICAL on the coder-side guard
proof.

Next action proposed, not taken: the owner decision on moving these guards to Roslyn, open on
`leak-1`. It is the only honest closure for the two surviving control-flow bypasses above.
