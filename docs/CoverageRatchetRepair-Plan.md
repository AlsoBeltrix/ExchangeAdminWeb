# Coverage Ratchet Repair Plan

Status: **Draft -- ONE OPEN OWNER GATE (D1, scope-pattern question).** Slices 1-2 are the fix and
need no ruling. D1 decides whether a follow-up is wanted; nothing in slices 1-2 depends on it.

## The problem, measured

`tools/Test-CoverageFloor.ps1` fails: **64.69% against a 65.06 floor**, over the security-critical
scope it gates (`Authorization/`, `Services/ProtectedPrincipal*`, `PermissionValidator`,
`MailboxPermissionOutcome`, `CalendarFolderIdentity`, `BulkCsvRowLimit`, `Services/SectionAccess*`).

Exact arithmetic from the current Cobertura report, because the size of the gap decides the shape
of the fix:

    in scope        1015 / 1569 lines = 64.69%
    floor 65.06 needs                  1021 lines
    SHORTFALL                          6 lines

**Six lines.** This is not a "write a large test suite" problem, and treating it as one would be
the wrong response to the measurement.

### Why it is failing

Not a coverage regression in the ordinary sense -- no test was deleted. Commit `0e35e7b` ("show
section-access groups as `DOMAIN\Name`") added 49 lines to
`Services/SectionAccessGroupDirectory.cs`, a file at **0/115 covered**. Growing an uncovered file
lowers the ratio while the numerator stays still. The gate is behaving exactly as designed and is
reporting a real dilution.

### Why it is not what reddens CI

Recorded because the previous state entry got this wrong. The `build-test` job fails at the
**Test** step, on `SectionAccessGroupIdentityTests.RefusesSddlAliases(alias: "DA")` (fixed
2026-08-05, `506c2d4`). The coverage step runs *after* Test and never executes. **This gate becomes
the visible CI failure once that fix lands** -- it did not cause the current red, and fixing it
would not have turned CI green on its own.

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
6-line problem justifies. They are covered by the live tests where a host allows.

### Expected effect on the ratio

The three members are roughly 20-25 statement lines once extracted, and every one is reachable from
a plain unit test. That converts ~20 uncovered lines to covered **and** removes them from the
uncovered side of `SectionAccessGroupDirectory`. Against a 6-line shortfall this clears the floor
with margin.

**It must be measured, not assumed.** Slice 2 re-runs the gate and records the actual number; if
the extraction does not clear 65.06, the plan is not done and the answer is more extraction from
the same file, never a lower floor.

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

2. **Measure, then raise the floor.** Re-run `dotnet test --collect:"XPlat Code Coverage"` and
   `tools/Test-CoverageFloor.ps1`. Record the new exact value in the plan. Raise
   `.agents/review/coverage-floor.txt` to the measured figure **rounded DOWN to two decimals** --
   the file explains why: the display rounds up, and a floor taken from the displayed figure is
   unreachable by the very run that produced it (a permanently red build teaches people to ignore
   the gate).

No version bump: tests and an internal extraction ship no behaviour
(`docs/ProjectConstitution.md`, Deployment And Versioning -- "Small bug fixes, UI polish, docs
updates, tests" need no plan and no bump; this plan exists because the owner asked for one).

## Verification

Per `.agents/repo-guidance.md`: `dotnet build ExchangeAdminWeb.slnx -c Release`,
`dotnet test ExchangeAdminWeb.slnx`, `dotnet format ExchangeAdminWeb.slnx --verify-no-changes
--no-restore`, `pwsh tools/Test-AsciiOnly.ps1`, `git diff --check HEAD`.

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
- Any change to `Test-CoverageFloor.ps1` itself. The gate is working; the code under it is not.
- Making `SectionAccessGroupDirectory` fully testable. Its remaining lines are genuinely
  AD-dependent, and pretending otherwise means mocking PowerShell.

## Owner gates

**D1 -- should the gated scope include the files this plan does not fix?** Blocks nothing here;
slices 1-2 proceed regardless.

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

## Open questions

- **OQ-1.** The gate reads the newest `coverage.cobertura.xml` under `TestResults`, and
  `dotnet test` writes one per run without cleaning up. A stale report from an earlier run can
  therefore be picked up. Not observed to have caused a wrong result, and not this plan's scope,
  but worth a guard (fail when the newest report is older than the newest test assembly).
