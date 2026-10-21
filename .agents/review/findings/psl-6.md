# psl-6 - one search base's error decides the whole attribute lookup, and only if it is the last

Status: Fixed on branch; awaiting merge. Commit `<pending>`.
Severity: MEDIUM (proposed). Reviewer: implementer (surfaced by the psl agent, 2026-10-21; first
noted as the "Adjacent observation" in psl-5 on master). Adjacent to psl-2..psl-5 - a PowerShell's
diagnostics are read for the wrong operation - but not their stale-record class: no record
survives here; one flag is read once, after the loop, for N invocations.
Evidence head: `ca6575a21e48596848ca9521d4683424f10cf577`.

## Evidence and consequence

`ADAttributeEditorService.PerformLookup` (Services/ADAttributeEditorService.cs at the evidence
head). With search bases configured it runs one `Get-ADUser -LDAPFilter ... -SearchBase <base>
-ErrorAction SilentlyContinue` per base, in a loop whose body is `try { allUsers.AddRange(ps.Invoke()); }
catch { }` then `ps.Streams.Error.Clear(); ps.Commands.Clear();` (652-668). Only AFTER the loop
does it look at diagnostics: `if (ps.HadErrors)` fails the lookup with the first error record's
message, else "AD query failed." (670-675).

SDK facts, checked in this session against the test project's SDK with advanced functions taking
the same paths a compiled cmdlet does (psl-5 recorded the same for 7.6): HadErrors describes only
the most recent invocation and is reset by the next one; it survives `Streams.Error.Clear()`; a
cmdlet error under SilentlyContinue (`WriteError`) writes no record but sets HadErrors; a
terminating error (`ThrowTerminatingError`) throws out of Invoke whatever the ErrorAction and
leaves HadErrors true.

So the check after the loop sees the LAST base alone:

1. The user is found in an earlier base and the last base fails, either way: the lookup fails
   with "AD query failed." - never with the detail, because the stream was already cleared.
   Editing that user, and undoing an edit (undo looks the target up first), is unavailable for as
   long as the last base is unreachable.
2. An earlier base fails and the last answers: the failure is lost. A user who lives in the
   failed base is reported "not found", a wrong answer rather than a failure.

Confirmed by reproduction below. Fail-closed throughout (nothing is written on a failed lookup),
so this is availability and a misleading answer, not an authorization defect.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"), with the rule given for this record:
evaluate and clear diagnostics per base inside the loop; a user found on one base while another
base errors is a success with a logged warning naming the base that failed; no user found
anywhere while any base errored is a failure that names the failing base(s); which bases are
searched, and in what order, does not change. Applied whichever base fails, earlier or later:
the defect is precisely that the order decided the outcome. One commit
`fix(ad-attribute-editor): ... (psl-6)`, after aud-a26b and aud-a28b. Module behaviour: AD
Attribute Editor (the supervisor owns the version bump).

Scope: PerformLookup's search-base branch. Each base's invocation is judged inside its own
iteration - HadErrors read straight after Invoke (with the first record's message as detail when
there is one), a thrown error caught with its message - and the stream and commands cleared in a
`finally` before the next base runs. After the loop: nobody found and a base failed -> a failure
naming every failed base and its reason; somebody found and a base failed -> a warning naming
them, then the unchanged dedupe, ambiguity, scope and projection. The no-search-base branch (one
query under `-ErrorAction Stop`, whose errors already throw to the caller) keeps its own
HadErrors check, moved inside that branch, unchanged.

Proof: reproduction first, against the unfixed loop through the aud-a26b runspace seam and
`AttributeEditorHarness` (stand-in Get-ADUser failing per base with the cmdlet's own two failure
paths). Tests: found-then-later-base-fails (both failure kinds) succeeds and warns naming the
base; earlier-base-fails-then-nobody-found (both kinds) fails naming the base; found in a later
base after an earlier failure succeeds; every base failing names each; controls - nobody found
with no failure is still "not found", a clean lookup logs no warning, the bases are queried once
each in configured order, two matches across bases are still ambiguous. Mutations at the call
sites: the post-loop check restored; the per-base HadErrors check removed; the catch's recording
removed; the not-found branch ignoring the failures; the warning removed.

## Executed reproduction - 2026-10-21

Branch `worktree-agent-acad2a289bd002e11` after aud-a26b (`277f2b9a`) and aud-a28b (`5b5bd6f6`);
service unchanged since aud-a26b (MD5 `C30973909BB6E08176984458B5A2E0AD`).
`ExchangeAdminWeb.Tests/ADAttributeEditorLookupTests.cs` through the harness, bases
`OU=Staff,DC=example,DC=test` then `OU=Contractors,DC=example,DC=test`. Result 9 failed / 4 passed,
0 compiler errors (`acad2a-psl6-reproduction.log` in the session scratchpad):

- user in Staff, Contractors (last) failing, either kind: `AD query failed.` - the user had been
  found;
- user in Staff, Staff failing, either kind: `User 'alex.morgan@example.test' not found.` - a wrong
  answer;
- user in Contractors, Staff failing, either kind, and a Staff answer that also errored
  (`partial`): success with no warning - the failure was simply forgotten;
- both bases failing: `AD query failed.`, naming neither base and carrying no detail;
- an undo of a Staff user while Contractors was down: `Cannot read current state of
  'alex.morgan@example.test': AD query failed.`
- controls passed: nobody found and nothing failing is still "not found", a clean lookup warns
  about nothing, bases queried once each in configured order, two matches still ambiguous.

A first run found a fault in the stand-in, not the code: its non-terminating mode still returned
the user after writing the error, so one case was asking the wrong question. The mode now returns
nothing; returning AND erring became its own `partial` mode with its own test. A later added case
(a base that leaves an error RECORD, below) first failed because the harness session was
`CreateDefault2()` with autoloading off, where `Write-Error` does not exist and the resulting
command-not-found error is swallowed under SilentlyContinue; the session is now `CreateDefault()`,
as production builds. Both were found by the tests failing, and the full-revert mutation below
re-runs the reproduction on the final tests.

## Fix

`Services/ADAttributeEditorService.cs`, PerformLookup's search-base branch: each base's
Get-ADUser is judged inside its own iteration - `HadErrors` read straight after `Invoke` (detail
from the first record when one exists, else "the query reported an error without detail"), a
thrown error caught with its message - and the error stream and commands cleared in a `finally`
before the next base. After the loop: nobody found while a base failed returns
`AD lookup for '<identity>' could not be completed: search base '<base>' failed (<reason>); ...`;
somebody found while a base failed logs a warning naming each failed base and reason, then the
unchanged dedupe, ambiguity, scope and projection. The no-search-base branch keeps its HadErrors
check, moved inside that branch. Bases searched and their order are unchanged. New
`DescribeFailedBases` formats the list.

Tests: `ExchangeAdminWeb.Tests/ADAttributeEditorLookupTests.cs` (new, 14 cases - three theories
over both failure kinds, the `partial` and `recorded` cases, every-base-failing, the undo, four
controls); harness: `LocalAd.Add(User)`, the `partial` and `recorded` modes, `CreateDefault()`.

## Guard proof

Same session-local driver as aud-a26b, filter `FullyQualifiedName~ADAttributeEditorLookupTests`,
fixed-file MD5 `0FC516343C93A12955E68B638C115AB7`, restored to it after each mutation; after the
round a rebuild and the filter again, 14/14 green. Every mutant built with 0 errors.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| the whole fix reverted to the evidence-head loop (the reproduction, on the final tests) | `C4CF84AF1253065361760528EC34F0A9` | `AUserFoundInOneBase_IsNotLostToALaterBaseFailing(failure: "terminating")` - 10 of 14 fail, the 4 controls pass |
| the post-loop `HadErrors` check restored | `22BBA43B024C8F1F5CDAE4B9B3686D5C` | `AUserFoundInOneBase_IsNotLostToALaterBaseFailing(failure: "nonterminating")` |
| the per-base `HadErrors` check removed | `C09B69D66255D1E2B29E6EA773202AD8` | `AFailingBase_IsNotLost_WhenNobodyIsFoundInTheOthers(failure: "nonterminating")` |
| the catch swallows again | `3B733AA2AA4DF67789579D3BD7BD1991` | `AFailingBase_IsNotLost_WhenNobodyIsFoundInTheOthers(failure: "terminating")` |
| the not-found branch ignores the failed bases | `CB786C1D002A0E9F8EA2DEB884DF5AD5` | `EveryBaseFailing_FailsAndNamesEachBaseWithItsReason` |
| the warning disabled | `8116BA099ECFBA6A74495C3C7F2D700A` | `AUserFoundInALaterBase_IsNotLostToAnEarlierBaseFailing_AndTheFailureIsLogged(failure: "terminating")` |
| the per-base `Streams.Error.Clear()` removed | `7FD8FE186223D5B496EAC159562A338A` | `AnErrorRecordLeftByOneBase_IsJudgedAgainstThatBaseOnly` |

The comments were in place before the round; the MD5 above is the committed bytes.

## Gates - 2026-10-21

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 40 warnings (incremental), all
  pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: exit 0, 4,522
  passed / 3 skipped / 0 failed, 4,525 total, 5m04s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the new files staged: exit 0.

No PowerShell source changed (no analyzer or Pester run). No AD module import, directory,
Delinea, SMTP, push or deployment. No coder self-review verdict is claimed; this record was
opened by the implementer, so an independent review is the supervisor's call.

## Known gaps

- The owner's rule accepts a residual risk this record states rather than hides: when a base
  fails, a SECOND match living there cannot reach the ambiguity check, so "found exactly one" means
  one among the bases that answered. The warning names the failed base; the operator sees the
  account's name and UPN before editing; nothing is written without the save's own re-read by DN
  and GUID.
- The success-with-warning is in the log only; `AttributeLookupResult` has no warning channel, so
  the page does not show it.
- Which of the two failure paths a real Get-ADUser takes for a given fault (an unreachable
  server, a missing base, a referral) was not checked against a directory; both are covered.
