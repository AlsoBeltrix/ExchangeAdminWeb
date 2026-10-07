# prog-3: A string literal can still counterfeit the progress proof

**Severity**: MEDIUM - same observable failure class as prog-1, reached by a different route
**Status**: Open
**Branch**: - (direct to main)
**Commit**: -

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

Pending. The minimum is: strip literals before `ActivitiesIn` as well, fix `$"..."` versus
`$@"..."`, handle raw string literals, and add negative tests for a fake `Progress.Begin`
and a fake covered call inside strings. **The reviewer's first recommendation is to move the
guard to Roslyn**, which converges with the open decision on `leak-1`.

## Known gaps

Pending.

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

Pending.
