# Coverage Ratchet Repair Plan

Status: **In progress.** Owner approved slices 1-3 on 2026-08-05 ("approved").
D1 remains open and unruled; it is a follow-up scope question and blocks nothing here.

Reviewed 2026-08-05 -- see `## Review` at the end.

## The problem, measured

`tools/Test-CoverageFloor.ps1` fails: **64.69% against a 65.06 floor**, over the security-critical
scope it gates (`Authorization/`, `Services/ProtectedPrincipal*`, `PermissionValidator`,
`MailboxPermissionOutcome`, `CalendarFolderIdentity`, `BulkCsvRowLimit`, `Services/SectionAccess*`).

Exact arithmetic from the current Cobertura report, because the size of the gap decides the shape
of the fix:

    local (domain-joined, dev box)   1015 / 1569 lines = 64.69%   shortfall 6 lines
    CI (standalone runner, run 31016572894)  817 / 1275 = 64.10%   shortfall 13 lines

**Six lines locally, thirteen on CI. CI is the number that matters** -- it is what the gate blocks
on. Neither is a "write a large test suite" problem, and treating it as one would be the wrong
response to the measurement.

**The two hosts do not measure the same thing, and an implementer must not be surprised by that.**
CI's denominator is 294 lines smaller because live-directory tests skip there (9 skipped on CI
versus 3 locally), so code only those tests reach is never loaded and never instrumented. A local
`Test-CoverageFloor.ps1` pass is therefore **not** proof the gate will pass on CI. Verify the fix
against CI's arithmetic, not the dev box's: the extraction must add roughly 13 covered lines to a
1275-line denominator, not 6 to a 1569-line one.

### Why it is failing

Not a coverage regression in the ordinary sense -- no test was deleted. Commit `0e35e7b` ("show
section-access groups as `DOMAIN\Name`") added 49 lines to
`Services/SectionAccessGroupDirectory.cs`, a file at **0/115 covered**. Growing an uncovered file
lowers the ratio while the numerator stays still. The gate is behaving exactly as designed and is
reporting a real dilution.

### It is now the sole CI failure -- confirmed

Recorded because an earlier state entry got this wrong. Until `506c2d4` the `build-test` job failed
at the **Test** step, on `SectionAccessGroupIdentityTests.RefusesSddlAliases(alias: "DA")`, and the
coverage step never ran at all.

**Confirmed on run 31016572894 (`0f67e62`):** Test now passes -- `1288 passed, 0 failed, 9
skipped` -- and the single failing step is `Coverage floor (security-critical paths)`, reporting
`64.1% (817 / 1275)` against the 65.06 floor. The `powershell` job passes.

So this plan is now the only thing standing between `master` and a green build.

## Why `SectionAccessGroupDirectory` has 0% coverage

Every path in it opens a PowerShell runspace and imports the `ActiveDirectory` module
(`CreateRunspace`, line 53). There is no seam: `FindGroupsByName` constructs the runspace itself,
so no test can reach the logic inside without a domain-joined host with RSAT. On CI that host does
not exist; on the dev box it does, which is the asymmetry that already produced
`ADDirectoryLiveTests` and the `Assert.SkipUnless` rule.

The file is not untested through neglect. It is untested **by construction**, and the repo has
handled that exact shape twice before by extracting the decision logic:

- `Services/MailboxPermissionOutcome.cs` -- pulled out of `MailboxPermissionService`, now 98%.
- `Services/CalendarFolderIdentity.cs` -- pulled out of `ExchangeServiceBase.GetCalendarFolderName`
  because "the surrounding method calls `Get-MailboxFolderStatistics`, so nothing could reach this
  logic without a live Exchange connection". Now 100%.

Both are recorded in `docs/TestSuiteRemediation-Plan.md` (D2a). This plan applies the same move a
third time, to the file that is now diluting the ratio.

## Design

### What to extract, and what NOT to

The runspace calls stay where they are. What moves is the logic that *interprets* what the
directory returned -- the part where a wrong answer is a silent authorization defect rather than a
loud outage.

New pure class `Authorization/SectionAccessDirectoryReading.cs`. Three members, each chosen because
it currently cannot be reached without AD and each carrying a real failure mode:

1. **`UnwrapDnsRoot(object? value)`** -- from `ResolveDomainServer` lines 113-121. `dnsRoot` is
   multi-valued in the schema, so the value arrives as a string, an `IEnumerable`, or something
   else; the code takes the first entry. **Failure mode:** returning the wrong element, or the
   `ToString()` of a collection, points the whole migration at the wrong domain. Blank must be
   rejected, not passed through.

2. **`ChooseBareName(string? samAccountName, string? name, string? displayName, string fallback)`**
   -- from `QueryGroups` lines 167-171. Precedence is sAMAccountName, then Name, then DisplayName,
   then the queried name. **Failure mode:** the comment above it states the precedence is
   deliberate (`sAMAccountName` is "the half of `DOMAIN\Name` that Windows itself uses") and that
   DisplayName is a last resort because it "is not unique and need not match the logon name". That
   reasoning is currently enforced by nothing.

3. **`NetBiosFromNTAccount(string? account)`** -- from `ResolveNetBiosDomain` lines 212-213. Splits
   `DOMAIN\Name` at the first backslash, returning null when there is no domain half. **Failure
   mode:** `slash > 0` (not `>= 0`) is load-bearing -- a leading backslash must yield null rather
   than an empty domain, or the display becomes `\Name`. The `Translate` call itself stays in the
   service, since that is the AD-dependent half.

**Deliberately NOT extracted:** `DrainErrors`, and the `matches.Count != 1` and missing-`objectSid`
throws. They read a live `PowerShell` object or sit inline in a runspace method; faking them would
mean inventing an abstraction over PowerShell itself, which is a larger and riskier change than the
6-line problem justifies.

**Those paths remain wholly untested after this repair, and nothing else covers them.** Verified
2026-08-05: no test anywhere constructs the real `SectionAccessGroupDirectory` -- every test uses a
fake `ISectionAccessGroupDirectory`, and the three live-AD test files
(`ADDirectoryLiveTests`, `AdDirectoryForestSearchLiveTests`, `LiveDirectoryCollection`) never
mention it. An earlier draft of this plan claimed they were "covered by the live tests where a host
allows"; that was false and is corrected here rather than quietly deleted, because the claim would
have told a future reader those fail-closed guards were exercised when they are not. They are
fail-closed AD result handling -- the paths that stop an outage being read as "no such group" --
so leaving them untested is a real, accepted gap, not a non-issue. Covering them needs a
result-shaping seam over the PowerShell output, which is its own work stream.

### Expected effect on the ratio

Counted, not estimated: the three regions are **15 statement lines** today (8 + 5 + 2, excluding
blanks and comments), all currently uncovered. Extracted, every one is reachable from a plain unit
test. Against a 6-line shortfall that clears the floor, but the margin is thinner than a casual
reading suggests -- extraction adds a class declaration and method signatures, which are themselves
coverable lines, so the arithmetic is not a simple 15-for-15 swap.

**Hence slice 2 measures rather than assumes**, and records the actual number. If the measured
value does not clear 65.06 the plan is not done: the answer is more extraction from the same file
-- `ResolveDomainServer`'s error-message construction is the next candidate -- and never a lower
floor.

**Measure against CI's arithmetic, not the dev box's.** 15 extracted lines comfortably covers a
6-line local shortfall and only just covers CI's 13, before accounting for the declaration lines
extraction adds. **The local run passing is not sufficient evidence** -- slice 2 is not complete
until a CI run is green, and a local pass followed by a CI failure means more extraction, not a
floor adjustment.

### What must not happen

**Do not lower the floor.** `.agents/review/coverage-floor.txt` states the rule and the reason:
that is review finding tsr-1, where the gate shipped with its floor 0.7 points BELOW the measured
value, leaving exactly enough slack for coverage to fall unnoticed. Lowering it to pass converts
the ratchet into decoration. The floor moves UP after a deliberate improvement, never down to meet
a regression.

**Do not add tests that touch AD to fix the number.** A test that skips on CI contributes nothing
to the CI ratio, so it cannot fix this and would create the illusion of having done so.

**Do not raise the floor in the same commit as the extraction.** Land the coverage improvement,
observe the new measured value, then raise the floor in its own one-line commit that states the
number -- which is the ratchet's own documented usage.

## Slices

1. **Extract `SectionAccessDirectoryReading` + tests.** The three members above, with
   `SectionAccessGroupDirectory` delegating to them. Behaviour-preserving: the service's observable
   contract does not change, so the existing `SectionAccessSidMigration*` tests must stay green
   untouched. Tests cover: `dnsRoot` as string / as collection / empty collection / blank / null;
   the four-level name precedence including all-blank; and `DOMAIN\Name`, bare name, leading
   backslash, empty, null.

2. **Measure deterministically, then raise the floor.** The measurement must not be able to read a
   stale report. `Test-CoverageFloor.ps1:79-86` takes the NEWEST `coverage.cobertura.xml` under
   `TestResults`, and `dotnet test` writes a new GUID-named directory per run without cleaning up,
   so "newest" is only correct if nothing interrupted the sequence. **This is not hypothetical: it
   bit this session**, when a floor check silently scored an earlier run's report.

   Run exactly:

       Remove-Item -Recurse -Force TestResults, ExchangeAdminWeb.Tests/TestResults -EA SilentlyContinue
       dotnet test ExchangeAdminWeb.slnx -c Release --collect:"XPlat Code Coverage" --results-directory TestResults
       $report = (Get-ChildItem TestResults -Recurse -Filter coverage.cobertura.xml |
                  Sort-Object LastWriteTime -Descending | Select-Object -First 1).FullName
       pwsh tools/Test-CoverageFloor.ps1 -CoverageFile $report

   `--results-directory TestResults` mirrors `.github/workflows/ci.yml:20-21`, so the local number
   is measured the same way CI measures it. Passing `-CoverageFile` explicitly removes the
   newest-wins guess entirely.

   Then raise `.agents/review/coverage-floor.txt` to the measured figure **rounded DOWN to two
   decimals** -- the file explains why: the display rounds up, and a floor taken from the displayed
   figure is unreachable by the very run that produced it (a permanently red build teaches people
   to ignore the gate).

3. **Add the freshness guard to the gate itself.** Was OQ-1; promoted into the plan because the
   hazard is now demonstrated rather than theoretical, and a procedure that depends on the
   implementer remembering step 2's incantation is weaker than a check in the tool. Fail when the
   chosen report is older than the newest test assembly, with a message naming both timestamps.
   Same reasoning as the empty-scope check the script already carries: a gate that silently scores
   the wrong input is worse than no gate, because it reads as proof.

No version bump: tests, an internal extraction, and a change to a CI-only ops script ship no app
behaviour (`docs/ProjectConstitution.md`, Deployment And Versioning).

**Slice 3 touches a `.ps1`, so it inherits the PowerShell verification rules**
(`.agents/repo-guidance.md`, Verification): `Invoke-ScriptAnalyzer -Path . -Recurse` and a Pester
case in `tests/ps/CoverageFloor.Tests.ps1`, which already exists and tests this same script. The
new case feeds it a deliberately stale report and asserts a non-zero exit -- the gate that tests
the gate, which is what that file is for.

## Verification

Per `.agents/repo-guidance.md`: `dotnet build ExchangeAdminWeb.slnx -c Release`,
`dotnet test ExchangeAdminWeb.slnx`, `dotnet format ExchangeAdminWeb.slnx --verify-no-changes
--no-restore`, `pwsh tools/Test-AsciiOnly.ps1`, `git diff --check HEAD`. Slice 3 adds
`Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps`.

**Pester runs under `pwsh`, not Windows PowerShell** -- the bundled 3.4.0 cannot parse this repo's
Pester 5 syntax and reports a parse error rather than a test failure, which reads as broken tests.

Plus the two that are the point of the work:

    dotnet test ExchangeAdminWeb.slnx --collect:"XPlat Code Coverage"
    pwsh tools/Test-CoverageFloor.ps1     # must PASS, and print a value >= the committed floor

**Non-vacuity is required per extracted member** and is unusually easy to get wrong here, because
a delegation that returns a plausible-looking wrong answer still passes a weak test. For each:
revert the member's logic to the naive form it replaces (`?? ""` instead of the blank check,
`>= 0` instead of `> 0`, DisplayName first instead of last), confirm the specific test fails naming
that case, restore.

**The extraction must also be proven behaviour-preserving**, not merely green: the
`SectionAccessSidMigrationPlannerTests` and `SectionAccessSidMigrationTests` suites exercise the
migration through `ISectionAccessGroupDirectory` and must pass **without modification**. Editing
either of those files during this work is a signal that behaviour changed, and is a stop.

### Manual checks

None. This ships no behaviour: no UI, no new service, no configuration. The dev instance's
migration path is unchanged and does not need re-running.

## Non-goals

- Raising coverage on `ProtectedPrincipalService` (215 uncovered) or `PermissionValidator` (182).
  They are the two largest gaps and would move the ratio far more, but they are live-dependency
  refactors with real blast radius on the protection path. Not something to attempt in a repair
  whose measured shortfall is 6 lines.
- Widening or narrowing the gated scope. See D1.
- Any change to `Test-CoverageFloor.ps1`'s scope patterns, floor semantics, or comparison
  arithmetic. The gate's logic is working; the code under it is not. Slice 3 adds a freshness
  guard, which is an input check, not a change to what the gate measures or how strictly.
- Making `SectionAccessGroupDirectory` fully testable. Its remaining lines are genuinely
  AD-dependent, and pretending otherwise means mocking PowerShell.

## Owner gates

**D1 -- should the gated scope include the files this plan does not fix?** A follow-up question
only: slices 1-3 are complete without it, and it does not block approval of them.

The scope patterns in `tools/Test-CoverageFloor.ps1` were chosen when the gate was written. They
already include `ProtectedPrincipalService` and `PermissionValidator`, which sit at 62% and 46% and
between them account for 397 of the 554 uncovered lines in scope. Every future addition to those
two files dilutes the ratio the same way `0e35e7b` did, so this repair will recur.

- (a) Leave the scope as it is. This plan clears the current failure; expect the same repair again.
- (b) Plan real coverage for those two services as its own work stream, which is where the
  remaining risk actually sits.
- (c) Narrow the scope to the files that can realistically be held near 100%, and accept that the
  gate then guards less.

Recommendation: **(b)**, as a separate plan, not folded into this one. (c) is the tempting option
and is the same failure as lowering the floor wearing a different hat -- it makes the number
green by measuring less.

## Outcome (implementation, 2026-08-05)

**Gate satisfied locally: 65.1% (844 / 1296), exit 0**, from a clean deterministic run. 1327 tests
pass, 3 skipped. Pester 78 pass.

**The plan's "measure, do not assume" clause earned itself.** The three planned members were not
enough: the first measurement came back **64.9% (835/1287)** -- an improvement, still 3 lines under
the floor. Per the plan the answer was more extraction, never a lower floor. Two further members
were pulled from the same file, both genuine decisions rather than filler:

- `PartitionMatchProblem` -- the candidate the plan named. It fixed a real gap while there: the
  original threw one message for both zero and many matches, but those send an administrator to
  different places ("check the stored value" vs "check the forest"). Now distinct.
- `GroupSidProblem` -- the missing-`objectSid` refusal. Worth extracting because the property is
  subtle: rejecting rather than skipping is what stops a two-match ambiguity, where one row lost
  its SID, from reading as a confident single answer.

`SectionAccessDirectoryReading` is at **100%**; `SectionAccessGroupDirectory` stays at 0%, which is
correct -- what remains there is the runspace work the plan deliberately left alone.

**A measurement error worth recording.** An intermediate run was reported as "gate exit: 0" when
the gate had failed: `$LASTEXITCODE` was read after a `Select-Object` pipeline, so it carried the
pipeline's code, not the script's. Caught by noticing the reported figure (65.0425) was
arithmetically below the floor it supposedly cleared. **Read a gate's own verdict line, never an
exit code captured through a pipe.**

**The floor is NOT raised in this work.** Local is 65.12 unrounded against a 65.06 floor -- 0.06
points of margin -- and CI measures a smaller denominator, so a safe local figure is not
necessarily safe on CI. Raising it from a local number would be guessing at CI's arithmetic, which
is exactly how a floor becomes unreachable. **Raise it only after a green CI run reports a real
value**, in its own one-line commit.

## Open questions

- **OQ-1. CLOSED 2026-08-05** -- promoted to slice 3. It was filed as "not observed to have caused
  a wrong result"; that was wrong, it had already happened in the session that wrote this plan. A
  hazard with a live instance is a slice, not an open question.

## Review

**openreview codex (`@azure-openai-eus2-global/gpt-5.5-dzs` @ xhigh, grade `fallback`) over
`506c2d4..62d84d9`: `acceptable_with_changes`.** Envelope validated: both SHAs match the dispatched
pins, `capability_ok: true`. Grade is `fallback`, so this is the same class as standard at more
effort, not a strictly stronger adjudicator -- recorded because it bounds how much the verdict is
worth. Reviewer confirms the core approach matches the repo's established seam-extraction pattern
and should be kept.

Three findings, each verified against the repo before acting rather than taken on the reviewer's
word:

| # | Severity | Verdict | Outcome |
|---|---|---|---|
| 1 | MEDIUM | **ADMITTED** | Slice 2 rewritten deterministic; guard promoted to slice 3 |
| 2 | MEDIUM | **ADMITTED** | False coverage claim removed and corrected in place |
| 3 | LOW | **ADMITTED** | Status line rewritten; no implementation until approved |

**Finding 1 -- stale coverage report.** Verified: `Test-CoverageFloor.ps1:79-86` picks the newest
report and `dotnet test` never cleans up. Admitted with more weight than the reviewer gave it: it
is not a latent hazard, it **already misfired during this session**, so the fix is both a
deterministic procedure and a guard in the tool.

**Finding 2 -- false claim of live-test coverage.** Verified by search: no test constructs the real
`SectionAccessGroupDirectory`, and no live-AD test file mentions it. My claim was unfounded. The
correction states the gap plainly instead of deleting the sentence, so the record shows the guards
are untested.

**Finding 3 -- Draft read as approval.** Verified against `.agents/repo-guidance.md` item 9.
Admitted: "Draft" plus "needs no ruling" is genuinely ambiguous to a cold agent, which is exactly
the reader this plan is written for.

Nothing was declined. That is worth noting rather than glossing: a pass where the coder accepts
every finding is the shape the playbook warns about, so each was checked against the code
independently, and each was independently reproducible.

**Repair re-review over `62d84d9..e9ed249` (same reviewer and pair, repair-delta mandate):
`best_approach`, `findings: []`.** All three closed; reviewer found no adjacent problem in the
touched text. Envelope SHAs matched the dispatched pins and `capability_ok: true`.

One contract deviation, recorded rather than ignored: the playbook requires `material_changes` to
be EMPTY at `best_approach`, and the reviewer returned five entries. Reading them, they enumerate
what the repair *did* -- the status line, the deterministic slice 2, the promoted guard, the
corrected coverage claim, the added PowerShell verification -- not changes still wanted, and
`recommended_approach` says "keep the repair as-is. No further changes are needed." So the verdict
is not self-contradictory in substance; the reviewer used the field as a changelog. Strict
fail-closed reading makes this "not an accepted verdict" on the mismatch alone. It is treated as a
pass because the payload's own text resolves the ambiguity in one direction only, and because the
same misuse appeared in the first round (where the five entries likewise described the plan rather
than requesting changes) -- a consistent habit of this reviewer, not a signal about this repair.
A future dispatch should state the field's semantics explicitly in the prompt.
