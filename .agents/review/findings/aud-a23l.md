# aud-a23l: Migration - three message leftovers: the "running command stopped" prefix in callers that format `ex.Message` themselves, "is a protected principal" when the protection check failed, and Undetermined filed under result "Ineligible"

**Severity**: LOW - operator messages and audit filing. (1) `FindMigrationUserBatchAsync`,
`GetMigrationUserReportAsync` and the page's own catches format `ex.Message` themselves, so they still show
PowerShell's "The running command stopped because the preference variable ..." prefix that aud-a23i removed
from `RunAsync`. (2) When every target is excluded because the protection check FAILED, `CreateMigrationBatchAsync`
says the target "is a protected principal". (3) `AuditService.LogMigrationCheck` files an Undetermined
verdict under result "Ineligible" (the `status` field does say Undetermined), so the Admin Event Log's
result filter cannot find it.
**Status**: Parts 1 and 2 fixed on branch, guard proof below; part 3 left for a follow-up (files owned by another agent); not merged, not pushed
**Branch**: `worktree-agent-a03d6bd111b571e5a` (forked at `b16898dc`)
**Commit**: `26237a55` (parts 1 and 2: fix, tests, record); plan `f62462b9`; comments `97ca9448` (shared with aud-a23n, aud-a23j, aud-a23m, aud-a23k)
**Reviewer**: implementer (surfaced by the aud-a23h/a23i agent, 2026-10-22, candidates 3, 4 and 8 of its hand-back)

## Evidence

At `ab5361c8`: `Services/MigrationService.cs` (the two methods; the "is a protected principal" text);
`Components/Pages/Migration.razor` catches (line-keyed, edit in place only); `Services/AuditService.cs`
`LogMigrationCheck`; `Components/Pages/AdminEventLog.razor` result filter (NotYetConverted).

## Approach (to be written by the implementing agent before code)

- The three sites use the base's error-text helper (aud-a23i) instead of raw `ex.Message`.
- "could not be verified as unprotected: <reason>; nothing was written" when the check failed; "is a
  protected principal" only when it answered.
- A third result value for Undetermined in `LogMigrationCheck` and in the Admin Event Log filter.
- Tests through `MigrationStubExchange` and the audit capture; mutations at the call site: raw
  `ex.Message` again at one site; the single wording again; "Ineligible" again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `fffdc451`. Parts 1 and 2 only, per the brief:
part 3 touches files another agent owns now and is recorded below for a follow-up. One commit `fix(migration): ...
(aud-a23l)` on branch `worktree-agent-a03d6bd111b571e5a` (forked at `b16898dc`), last, after aud-a23k. Module-scoped:
Migration (the bump is the supervisor's). Line numbers at `b16898dc`.

1. **Part 1, the cmdlet's own words.** `ExchangeServiceBase.ResolveFailure(ex).primary` (aud-a23i; read, not changed)
   where these format `ex.Message` themselves:
   - `Services/MigrationService.cs` `FindMigrationUserBatchAsync` (1335): "Search failed: <the error>", not "Search
     failed: The running command stopped because ...";
   - `GetMigrationUserReportAsync`: the failure aud-a23k made of its catch (1304);
   - the page's own catches over those reads: `LoadMailboxesFor` (1887), `SyncOpenBatchFromUrl` (1950) and
     `FetchUserReport` (4064, and the failure aud-a23k shows from 4068), in place.
   A message a caller composed around an error keeps its words (aud-a23i's restating-wrapper rule).
2. **Part 2, "is a protected principal" only when the check answered that.**
   - `CheckProtectedAsync` (303-354): the gate says which kind of denial it is. `ProtectionGate` gains `IsProtected`
     (default false), true only for the answered "This mailbox is a protected principal" refusal (341); a check that
     was unavailable, ambiguous, failed or threw stays false. The default reads a denial as NOT known to be protected,
     so a denial added later that forgets the flag says "could not be verified", never claims protection.
   - `PartitionByProtectionAsync` (369-396) returns a fourth list, the excluded targets whose check did not answer.
     Its deconstructions in `MigrationServiceProtectedPrincipalTests` and `MigrationServicerTests` take the extra
     element; nothing else about them changes.
   - `CreateMigrationBatchAsync`, every target excluded (450-463): all answered protected - the message as today;
     one target whose check did not answer - "The migration target could not be verified as unprotected: <target -
     reason>; nothing was written."; several, none answered - "None of the N migration targets could be verified as
     unprotected; nothing was written."; a mix - "All N migration targets were excluded: K is/are protected
     principal(s) and M could not be verified as unprotected; nothing was written. Escalate the protected ones ...".
     The Detail (each target and its reason) and `ExcludedTargets` unchanged.
   - `MigrationServiceProtectedPrincipalTests`' two `CreateBatch_..._AllTargetsProtected_...` tests run the real gate
     with no directory secret, so the check is UNAVAILABLE - they pinned the defect ("protected principal" in the
     message). They now pin the fix: "could not be verified as unprotected", no "protected principal(s)" claim.
   - Out of scope, named in the hand-back: the same claim on a partial success (`WithProtectionOutcome`'s NOTE, the
     Detail header in the success formatter) and on the page (the exclusion banners and their audit rows).
3. **Tests** - new `ExchangeAdminWeb.Tests/MigrationMessageTests.cs`, fakes only, over `MigrationStubExchange` (its
   protection stand-in from aud-a23m answers per target):
   - **part 1**: `Get-MigrationUser` writing an error under Stop - the search's `Error` is exactly the error's text;
     the mailbox load's activity text "Unable to load mailboxes in <batch>: <the error>" exactly, and the same from
     the URL; the report fetch's failure and its activity text exactly the error's.
   - **part 2**: one target unavailable, ambiguous or failed - "could not be verified as unprotected", the reason, no
     "is a protected principal"; one protected - the protected wording, as before; two unavailable; a mix; nothing
     created in any (no `New-MigrationBatch`).

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored, touched, rebuilt, green):
L1 (the record's) raw `ex.Message` again at `FindMigrationUserBatchAsync`; L2 (the record's) the single wording
again - every all-excluded message "is a protected principal". Extras: L1b raw `ex.Message` again at the page's
`LoadMailboxesFor`; L2b the protected denial's `IsProtected` not set.

**Gates** as the brief; comments after the mutation round.

## Left for a follow-up (files owned by another agent)

Part 3 is not implemented here: both files belong to another agent working now. What it needs, at `b16898dc`:

- `Services/AuditService.cs` 127, in `LogMigrationCheck` (111-136):
  `["result"] = status == "Eligible" ? "Success" : "Ineligible",` files every non-Eligible status - `Undetermined`
  (aud-a23h) and the page catch's `"Error"` (`Components/Pages/Migration.razor` 2007-2008) - under result
  "Ineligible". Needed: a third result for an undetermined check, e.g. `status switch { "Eligible" => "Success",
  "Ineligible" => "Ineligible", _ => "Undetermined" }`, so neither "Undetermined" nor "Error" is filed as an
  ineligibility (the `status` and `reasons` fields already say which).
- `Components/Pages/AdminEventLog.razor` 91-96, the Result filter's options (All, Success, Failed, Ineligible): add
  `<option value="Undetermined">Undetermined</option>`. The badge at 264 already shows any result other than
  Success and Failed as `bg-warning`. Mind that page's own line-keyed registrations when adding the line.
- Test, then: `AuditService.LogMigrationCheck` with "Undetermined" and with "Error" writes result "Undetermined";
  the filter offers it. Mutation: "Ineligible" again.

## Guard proof

Parts 1 and 2, built as the Plan. The fix commit carries no new code comments. `Components/Pages/Migration.razor` is
4377 lines before and after, four lines changed in place (1887, 1950, 4064, 4068), none registered.

- `Services/MigrationService.cs`:
  - `ProtectionGate` (404) gains `IsProtected` (default false); `CheckProtectedAsync`'s answered refusal sets it (444);
    every other denial - unavailable, ambiguous, a failed check, a throw - leaves it false.
  - `PartitionByProtectionAsync` returns a fourth list, `unverified` (482, 498): the excluded entries whose check did
    not answer, the same "target - reason" strings as `excluded`.
  - `CreateMigrationBatchAsync` (564) through `AllExcludedMessage` (689): all answered protected - the messages as
    before; one unverified - "The migration target could not be verified as unprotected: <target - reason>; nothing was
    written."; several, none verified - "None of the N migration targets could be verified as unprotected; nothing was
    written."; a mix - "All N migration targets were excluded: K is/are protected principal(s) and M could not be
    verified as unprotected; nothing was written. Escalate the protected ones ...". Detail and `ExcludedTargets`
    unchanged.
  - `GetMigrationUserReportAsync`'s failure (1476) and `FindMigrationUserBatchAsync`'s (1507) through
    `ResolveFailure(ex).primary`.
- `Components/Pages/Migration.razor`: `LoadMailboxesFor` (1887), `SyncOpenBatchFromUrl` (1950) and `FetchUserReport`'s
  catch (4064, 4068) through `ExchangeServiceBase.ResolveFailure(ex).primary`.
- `MigrationServiceProtectedPrincipalTests`: the two `CreateBatch_..._AllTargetsProtected_...` tests, which ran the real
  gate with no directory secret (so UNAVAILABLE) and pinned "protected principal(s)", are renamed
  `..._ProtectionUnavailable_CreatesNothing_SaysNotVerified` and pin the fix; both files' partition deconstructions
  take the fourth element (`MigrationServiceProtectedPrincipalTests` 2, `MigrationServicerTests` 7), nothing else
  changed in them.

New `ExchangeAdminWeb.Tests/MigrationMessageTests.cs`, 9 tests (11 cases):

| Test | Pins |
| --- | --- |
| `ASearchThatFails_GivesTheErrorsOwnWords` | **part 1, the finding**: the search's `Error` is exactly the written error's text |
| `AMailboxLoadThatFails_GivesTheErrorsOwnWords` | the page's mailbox-load activity: exactly "Unable to load mailboxes in <batch>: <the error>" |
| `AMailboxLoadFromTheAddressThatFails_GivesTheErrorsOwnWords` | the same through `SyncOpenBatchFromUrl` |
| `AReportFetchThatFails_GivesTheErrorsOwnWords` | the report's `reportFailure` and the activity text, exactly the error's |
| `ATargetWhoseCheckDidNotAnswer_CouldNotBeVerified_ItIsNotCalledProtected` (unavailable, ambiguous, failed) | **part 2, the finding**: "could not be verified as unprotected: <target - reason>; nothing was written.", no "protected principal", no `New-MigrationBatch` |
| `SeveralTargets_NoneVerified_SaySo` | "None of the 2 ..." |
| `AProtectedTarget_IsStillAProtectedPrincipal`, `SeveralProtectedTargets_AreStillProtectedPrincipals` | the controls: the answered wording, as before |
| `AMixOfProtectedAndUnverified_CountsEachApart` | "1 is a protected principal and 1 could not be verified as unprotected" |

Mutations at the call site, by the same driver and protocol (`round-a23l.ps1`, session scratchpad `a23jklmn-work`),
over the 1140 tests of the same filter. Bytes (LF) before every mutation and after every restore: service
`8b5df0d35f60682c906106461796a370`, page `c73c5d5190e7b0b44556f5bbbdb39119`.

| Mutation | File | md5 mutated | Failed (of 1140) | Restored, rerun |
| --- | --- | --- | --- | --- |
| L1 (the record's): raw `ex.Message` again at `FindMigrationUserBatchAsync` | service | `deb29ea31369de0f339b4b6a05c14ad1` | 1: `ASearchThatFails_GivesTheErrorsOwnWords` - the Stop prefix again | equal; 1140/0 |
| L2 (the record's): the single wording again (every all-excluded message "protected principal(s)") | service | `4036c75ea12c94a771837c0fa19c65c3` | 7: `ATargetWhoseCheckDidNotAnswer...` (3), `SeveralTargets_NoneVerified...`, `AMixOf...`, both renamed `MigrationServiceProtectedPrincipalTests` | equal; 1140/0 |
| L1b: raw `ex.Message` again at the page's `LoadMailboxesFor` | page | `fd8bd462f4b601fefab27227d62c115f` | 1: `AMailboxLoadThatFails_GivesTheErrorsOwnWords` | equal; 1140/0 |
| L2b: the answered refusal's `IsProtected` not set | service | `67d5ce03f73afc0e0ad7124b8bf89499` | 3: `AProtectedTarget...`, `SeveralProtectedTargets...`, `AMixOf...` - a protected target called unverified | equal; 1140/0 |
| L1c: raw `ex.Message` again at `GetMigrationUserReportAsync` | service | `49319a92c9039eaf62bbd22f715ad424` | 1: `AReportFetchThatFails_GivesTheErrorsOwnWords` | equal; 1140/0 |

Part 3 ("Ineligible" again) is not run: it is not implemented here (above).

md5s (LF) of the other files at the fix commit: `MigrationMessageTests.cs` `02d6601fc6af9ea5655ad74a2d58a082`,
`MigrationServiceProtectedPrincipalTests.cs` `d2833fffca57b0dd34750dafee5fb4d2`, `MigrationServicerTests.cs`
`42f979a255f61092aeb4857df35ef3c5`.

On the fix commit's tree: Release build 0 errors; the 1140 focused tests 1140/0; `dotnet format --verify-no-changes`
exit 0; `git diff --check HEAD` exit 0; `wc -l` 4377. Out of scope, named in the hand-back: the same "protected
principal" claim over an unverified exclusion on a partial success (`WithProtectionOutcome`'s NOTE, the success
formatter's Detail header) and on the page (the two exclusion banners, their audit rows and notification notes).

## Known gaps

- Part 3 is not implemented (above, "Left for a follow-up").
- The same "protected principal" claim over a target whose check did not answer remains where a batch is created with
  some targets excluded: `WithProtectionOutcome`'s NOTE ("N protected principal(s) were excluded and NOT migrated") and
  the success formatter's Detail header ("EXCLUDED - protected principals, NOT migrated"), and on the page the two
  exclusion banners, the exclusion audit rows ("Excluded - protected principal, not migrated") and the notification
  notes. Named in the hand-back with their lines.
## Comments and branch-tip gates

Shared by the five records of this branch; the canonical copy is in `aud-a23n.md` ("Comments and branch-tip gates"):
comments `97ca9448`, comment-only by a stripped-code comparison; at the tip, Release build 0 errors / 59 warnings,
full suite 5872 passed / 3 skipped / 0 failed (5875), format, `git diff --check`, ASCII lint all exit 0, page 4377 lines.

## Closeout

Fixed on branch `worktree-agent-a03d6bd111b571e5a`; not merged, not pushed.
