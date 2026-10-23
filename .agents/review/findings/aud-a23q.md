# aud-a23q: Migration - an authorization denial on a single batch or user action, or on either create, shows nothing and writes no audit row

**Severity**: MEDIUM - audit of denied actions (Constitution: every denied mutating attempt is audited and
visible). `ExecuteBatchAction` and `ExecuteUserAction` return silently on an authorization denial: no
message, no audit row, while the bulk executors audit a "_Denied" row. The two creates
(`CreateSingleMigrationBatch`, `CreateMigrationBatch`) show nothing on a denial or on a guard return.
**Status**: Merged `d690a6c1`; closed on guard proof; codex review pending; codex misc-batch-1 clean (2026-10-23)
**Branch**: `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`)
**Commit**: `d58a0bc5` (fix, tests, record); plan `2bf76220`; comments `9fa7664d` (shared with aud-a23r, aud-a23o, aud-a23p)
**Reviewer**: implementer (surfaced by the aud-a23j-n agent, 2026-10-22, candidate 5 of its hand-back)

## Evidence

At `09d3bacb`: `Components/Pages/Migration.razor` around 3301-3302 and 3365-3366 (the silent returns),
2033-2037 and 2174-2178 (the creates' denial and guard returns); the bulk executors' "_Denied" audit rows
for the shape - line-keyed by `ClickGateRegistry`, 4377 lines, edit in place only.

## Approach (to be written by the implementing agent before code)

- Each denial writes the same "_Denied" audit row the bulk executors write (action, actor, target,
  `mutationAttempted: false`) and shows "Not authorized to <action>; nothing was done." in the page's
  banner; a guard return that is not a denial says why nothing happened.
- The activity (aud-a23m) completes not-succeeded with that text.
- Tests through the page's reflection driver with a denying authorization stand-in and the audit capture;
  mutations at the call site: one silent return restored; the audit row dropped at one site.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `3a948129`. One commit `fix(migration): ...
(aud-a23q)` on branch `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`), after aud-a23o and before aud-a23p (both
edit the page). Module-scoped: Migration (the bump is the supervisor's). `Components/Pages/Migration.razor` in place: 4377
lines before and after, each changed line replaced by one line; none of them registered (the registered lines run to
1540) and no registered snippet changed; no handler, lifecycle method or `Complete(` added. Line numbers at `5eac8b25`.

1. **A denial is shown, audited and ends the activity with its text.** The four silent `return;`s after a failed
   authorization - `CreateSingleMigrationBatch` 2034 and `CreateMigrationBatch` 2175 (MigrationCreate), `ExecuteBatchAction`
   3302 and `ExecuteUserAction` 3366 (MigrationManage) - each become one statement on that line that sets the page's banner
   (`singleCreateResult`, `createResult`, `batchActionResult`) to a failure, sets aud-a23m's local (`createFailure`,
   `actionFailure`) to the same text so the `finally`'s existing `Complete` reports it, and returns. The text: "Not
   authorized to <action>; nothing was done." - <action> is "<audit action> on <target>" for the two executors and "create a
   migration batch" for the creates.
   - The audit row is the bulk executors' (3534-3541): `Audit.LogMigrationAction(actor, ip, "<audit action>_Denied",
     target, false, ticket, errorDetail: "<policy> authorization denied")`, plus `extra` `mutationAttempted: false` (the
     aud-a28 refusal convention); a failed audit write logged and swallowed, as theirs is, so it cannot replace the denial.
     Written through new `MigrationService.RecordDenial` (internal static; the page passes its audit service, logger, actor
     and address) so each line stays one statement. The creates' audit action is "CreateMigrationBatch_<direction>_Denied",
     their target the mailbox checked (single, "(no eligibility check)" when none) or the batch name typed ("(bulk batch)"
     when none).
2. **A guard return says why nothing happened** - `CreateSingleMigrationBatch` 2036-2037 and `CreateMigrationBatch`
   2177-2178: the same shape, the text from new `MigrationService.SingleCreateRefusal` / `BulkCreateRefusal` (pure, the
   guards' own conditions, in their order): no eligibility check has been run; the check did not find the mailbox eligible
   (its status named) / no row of the file is eligible; no ticket number - each "...; nothing was created." Not audited:
   nothing was attempted (the bulk executors' "nothing to do" rule, 3500-3506).
3. Unchanged: every authorized path; the bulk executors.

**Tests** - new `ExchangeAdminWeb.Tests/MigrationDenialTests.cs`, the page's handlers over `MigrationStubExchange` (its
`Page` gains a policy to deny; the recording progress channel, aud-a23i; the audit file read back), fakes only:
- **the finding**: each of the four denials - the banner's text, exactly one "_Denied" row (action, target, ticket, result
  Failed, error "<policy> authorization denied", `mutationAttempted` false), the action never called / no
  `New-MigrationBatch`, the activity (false, the text). Before: no banner, no row, the activity (false, "Nothing was
  done." / "Nothing was created.").
- each guard return (single: no check, an ineligible check, an undetermined check, no ticket; bulk: no check, none
  eligible, no ticket) - its text, no audit row, nothing created, the activity (false, the text);
- a denial whose audit write throws (no audit service) - the banner still says it, nothing escapes;
- controls: an authorized action still runs and audits as before.

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored, touched, rebuilt, green): Q1 (the
record's) one silent return restored - `ExecuteUserAction`'s; Q2 (the record's) the audit row dropped at one site -
`ExecuteBatchAction`'s. Extras: Q3 the single create's denial silent again; Q4 a create guard return silent again; Q5
`mutationAttempted` dropped from `RecordDenial`.

**Gates** as the brief, plus `ClickGate*`/`ProgressRegistry*` green and `wc -l` 4377; comments after the mutation round.

## Guard proof

Built as the Plan, with one shape the build forced: the guard helpers are `bool` with an `out` refusal and
`[NotNullWhen(false)]` on the check they read, so the compiler's null analysis still knows, past the guard, that the check is
there (as the inline `== null` test told it); a `string?`-returning helper made three CS8602 warnings at the lines that use
it. The fix commit carries no new code comments. `Components/Pages/Migration.razor` is 4377 lines before and after: `git diff
--numstat` 8/8, each changed line replaced in place (2034, 2036, 2037, 2175, 2177, 2178, 3302, 3366); none registered (the
registered lines run to 1540), no registered snippet changed; no handler, lifecycle method or `Complete(` added.

- `Services/MigrationService.cs`: `RecordDenial` (902): the bulk executors' row through `Audit.LogMigrationAction` -
  "<audit action>_Denied", the target, Failed, the ticket, "<policy> authorization denied", `extra` `mutationAttempted:
  false` (909) - a failed write logged and swallowed; returns "Not authorized to <description>; nothing was done."
  `RefusesSingleCreate` (919), `RefusesBulkCreate` (928): the guards' own conditions in their order, each "...; nothing was
  created."
- `Components/Pages/Migration.razor`: the four denials (2034, 2175 MigrationCreate; 3302, 3366 MigrationManage) each set the
  banner and aud-a23m's local to `RecordDenial`'s text and return; the creates' audit action "CreateMigrationBatch_<direction>",
  target the mailbox checked or "(no eligibility check)" (single), the batch name typed or "(bulk batch)" (bulk). The guard
  pairs (2036-2037, 2177-2178) through `RefusesSingleCreate` / `RefusesBulkCreate`, banner and activity the refusal, not
  audited.
- `ExchangeAdminWeb.Tests/MigrationStubExchange.cs`: `Page(service, deny)` - the authorization stand-in refuses that one policy.
- `ExchangeAdminWeb.Tests/MigrationEligibilityAndApproveTests.cs`: aud-a23h's `CreatingABatchFromTheAnswer_GoesThroughOnlyWhenEligible`
  read "nothing shown" as "did not go through" - the silence this finding removes; it now tells the guard's refusal from the
  service's own result, and still asserts no `New-MigrationBatch` either way.

New `ExchangeAdminWeb.Tests/MigrationDenialTests.cs`, 7 tests (15 cases):

| Test | Pins |
| --- | --- |
| `ADeniedAction_IsShown_Audited_AndNothingRuns` (ExecuteBatchAction, ExecuteUserAction) | **the finding**: "Not authorized to <action> on <target>; nothing was done." in the banner (rendered on the status tab), one "_Denied" row (target, ticket, Failed, "MigrationManage authorization denied", `mutationAttempted` false), the action never called, the activity (false, the text). Before: no banner, no row, "Nothing was done." |
| `AnAuthorizedAction_RunsAsBefore` | the control: the action runs, its own row, no denial |
| `ADenialWhoseAuditWriteThrows_IsStillShown` | no audit service (its call throws): the banner and the activity still say it, nothing escapes |
| `ADeniedSingleCreate_IsShown_Audited_AndNothingIsCreated` (ToCloud, ToOnPrem) | **the finding**: the banner, one "CreateMigrationBatch_<direction>_Denied" row (target the mailbox, "MigrationCreate authorization denied", `mutationAttempted` false), no `New-MigrationBatch`, the activity |
| `ADeniedBulkCreate_IsShown_Audited_AndNothingIsCreated` (a name typed; none) | the same; target "Wave_12" / "(bulk batch)" |
| `ASingleCreateGuard_SaysWhyNothingWasCreated` (no check, ineligible, undetermined, no ticket) | each guard's own text in the banner and the activity; no audit row; nothing created |
| `ABulkCreateGuard_SaysWhyNothingWasCreated` (no check, none eligible, no ticket) | the same for the bulk create |

**Against the unfixed code: not runnable as a whole** - `Page`'s denying stand-in and the helpers are new, so each behaviour
was put back at its own call site instead, and each revert fails the tests that pin it.

Mutations at the call site, by the same driver and protocol (session scratchpad `work/mutate.ps1` - given a `-Line` option
here, since the two executors' denial lines are identical text: the line must equal the mutation's text before it is
replaced - and `work/round-a23q.ps1`), over the 1945 tests of the same filter. Bytes (LF) before every mutation and after
every restore: page `a06ad226e9ecdb5522a5cac427cae1ca`, service `eb33349757787d53fa16423c56438dd0`.

| Mutation | File | md5 mutated | Failed (of 1945) | Restored, rerun |
| --- | --- | --- | --- | --- |
| Q1 (the record's): one silent return restored - `ExecuteUserAction`'s (3366) | page | `d9da2f11cb47b57476732ad82e0fc11b` | 1: `ADeniedAction...` (ExecuteUserAction) | equal; 1945/0 |
| Q2 (the record's): the audit row dropped at one site - `ExecuteBatchAction`'s (3302): the banner, no row | page | `61756cc1bb22ab6f13b89dbfad3929ce` | 1: `ADeniedAction...` (ExecuteBatchAction) | equal; 1945/0 |
| Q3: the single create's denial silent again (2034) | page | `69484fae44458a460b9ebaa8fcb4ebc2` | 2: `ADeniedSingleCreate...` (both) | equal; 1945/0 |
| Q4: the single create's guard return silent again (2037) | page | `e5af7afc23e2730aed18d17cd9a2f52d` | 5: `ASingleCreateGuard...` (4), `CreatingABatchFromTheAnswer...` (Undetermined) | equal; 1945/0 |
| Q5: `mutationAttempted` dropped from the denial row | service | `c18c6fc57d2ff669d86aafe84acfa623` | 6: every denial case that reads the row | equal; 1945/0 |

md5s (LF) of the other files at the fix commit: `MigrationStubExchange.cs` `b30cf99a3d0a1388a1e85fc7e12be4a8`,
`MigrationDenialTests.cs` `27bd19d9768ae96a1572284b94a58365`, `MigrationEligibilityAndApproveTests.cs`
`a27f7530ec4f562711bb97e37eba8e07`.

On the fix commit's tree: Release build 0 errors, no warning outside the baseline's (the three CS8602 above were removed by
the helpers' shape before the round); the 1945 focused tests 1945/0 (`ClickGate*` and `ProgressRegistry*` among them);
`dotnet format --verify-no-changes` exit 0; `git diff --cached --check` exit 0; `tools/Test-AsciiOnly.ps1` exit 0; `wc -l` 4377.

## Known gaps

- The guard returns of the two bulk executors - "no list loaded" (`if (migrationBatches == null) return;` and the mailbox
  executor's) - still show nothing; outside this finding's named sites (named in the hand-back as a candidate).
- No circuit or browser ran: the page's handlers and compiled render tree were driven directly.

## Comments and branch-tip gates

Shared by the four records of this branch; the canonical copy is in `aud-a23r.md` ("Comments and branch-tip gates"):
comments `9fa7664d`, comment-only by a stripped-code comparison; at the tip, Release build 0 errors / 59 warnings (the
baseline's), full suite 6086 passed / 3 skipped / 0 failed (6089), format, the whitespace check and the ASCII lint all exit
0, page 4377 lines.

## Closeout

Fixed on branch `worktree-agent-ac3fd1dd1db4f9c1d`; not merged, not pushed.
