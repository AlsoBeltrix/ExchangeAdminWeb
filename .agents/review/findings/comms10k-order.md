# comms10k-order: the Comms-10k write order goes back to clear-then-add (owner ruling 2026-11-06)

**Severity**: owner ruling, not a defect finding - sweep-d item 19 (`1fec5a0f`) replaced the approved clear-then-add
write with add-first-then-remove, which `docs/Comms10kBulkResolveScale-Plan.md` had measured at 53.2 s against 7.9 s on
a 10k disjoint swap and rejected. Owner: "this change violates the rule. this significantly changes the workflow by
increasing the time." (`.agents/decisions.md` 2026-11-06).
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/comms10k-order` (worktree `D:\source\ExchangeAdminWeb-wt\comms10k-order`, from master `9e2d7e61`)
**Commit**: `952c290d` (`fix(comms10k): write order back to clear-then-add per the 2026-11-06 ruling; item 20's single
check kept`) | **Reviewer**: implementer (candidate)

## Evidence

- At `9e2d7e61`: `Comms10kService.PerformReplace` read the current membership (`ReadCurrentMembers`), the writer's
  `Plan` derived an add set and a remove set, and `ExecuteSequence` wrote the adds then `Set-ADGroup -Remove` batches
  (`RemoveMemberBatch`, `RemoveMemberValueBatch`). No `-Clear`.

## Approach (as built)

- **Clear, then add, then read back** - the plan's design. `PerformReplace` runs `Set-ADGroup -Clear member` on the
  pinned target (`ClearMembers`, restored), then adds every file member as `<GUID=...>` values in 2,000 batches, in file
  order. No pre-write membership read, no diff, no remove set: every file member is added (`-Add` is idempotent,
  measured). The service, writer, processor, payload and page are the pre-item-19 code (`1fec5a0f^`) with the parts
  below re-applied.
- **Removed with the order they served:** `RemoveMemberBatch`, `RemoveMemberValueBatch`, `RemoveMemberValues`,
  `ReadCurrentMembers`, `MapMemberGuids`' unidentified-value split, the writer's `CurrentMembership`, `WritePlan`,
  `Plan`, `StopPhase`, the "add failed and the list reads back unchanged, so refused before any change" branch, and the
  missing/extra PartlyApplied wording. **The pre-write current-membership read and its refusal are removed too**: they
  existed only to compute the diff, and the primary-group check reads the primaries directly again as it did before
  item 19 (a failure there still refuses before any change through the preparation catch).
- **Kept from item 19:** after the clear, a failed add batch stops the run and the message names it - "Add batch k of n
  (m members) failed: <error>" - inside the PartlyApplied (or CouldNotConfirm) result the read-back decides. The
  read-back still compares final membership by GUID against the approved set (C4, unchanged).
- **Kept from item 20:** `authorizationDenial()` is consulted exactly once in `PerformReplace`, after the target re-read
  and the primary-group check, immediately before `ExecuteSequence` (whose first write is the clear). A denial refuses
  with nothing written. The clear's `beforeDispatch` only marks dispatch; add batches carry no hook. No check between
  batches.
- Help: page comment, cancellation and interrupted/cancelled summaries back to "clear/refill" and "may be empty or
  partial"; README Comms-10k bullets back to clear-then-fill (adds the named batch, "empty or partial until a re-run",
  and one line on the 2026-11-06 reversal); the README audit-field note "clear-then-fill never reads the prior
  membership" is true again and unchanged. Comms10k `1.4.21` (no test pins the version).
- Unchanged: the GUID member value form, the pinned identity, the approved-set fingerprint, the 2,000 batch size, the
  retry rule (per operation: the clear, or one add batch).

## Where the code goes beside the fix shape

- The page comment restored verbatim from `1fec5a0f^` carries its earlier duplicated sentence ("The write is NOT atomic:
  it clears ... ENQUEUED, NOT EXECUTED HERE. The write clears ..."); left as it was to keep the revert exact and the
  page line count unchanged (no ClickGateRegistry re-anchor needed: 8 lines changed in place).
- Tests: the Comms10k suites are the pre-item-19 versions, with item 20's tests ported to the clear: `C2ARevocationAfter
  TheFirstWriteDoesNotLeaveAHalfReplacedList` (boundaries clear, clear-retry, between-batches, add-retry),
  `C2TheServiceConsultsAuthorizationExactlyOnce_AfterPreparation_BeforeTheClear`, `C2ADenialAtTheSingleCheck...`,
  `AuthorizationIsConsultedOnce_BeforeTheClear_AndNeverInsideAWrite`. The pre-item-20 mid-run revocation tests
  (`C2AfterAnyClearAttemptRevocationStopsWritesAndReportsReadback`, `C2ReadbackCanConfirmTheApprovedPrimarySetAfter
  AuthorizationStopsAdds`) stay deleted. New: `AFailedAddBatchAfterTheClear_StopsTheRun_IsPartlyApplied_AndNamesThe
  BatchAndTheError`, `AFailureBeforeTheClearWasDispatched_RefusesWithoutAReadBack`. Net test count 8,762 to 8,757 (-5):
  seven item-19 writer tests go (six for the add/remove planning, plus its dispatch test, replaced by the clear-form
  port above) and the two new ones come; the `remove-fails` interruption case is replaced by `clear-fails`.
- Deploy check list: item 19 added a scratch-group `-Remove @{member=<GUID=...>}` step. That form is no longer used;
  the list lives outside this slice's files and is for the supervisor to trim.

## Guard proof

Each mutation applied alone to the committed code, Comms10k tests run (406), then restored.

| Mutation | Failing tests |
|---|---|
| M1 adds before the clear | 22: `Comms10kReplaceWriterTests.TheClearIsIssuedExactlyOnceAndBeforeEveryAdd`, `ARetriedAddBatchNeverReIssuesTheClear`, `AFailedAddBatchAfterTheClear_...`; `Comms10kInterruptionTests.ActualWriteSequence...` (5 cases); `Comms10kPowerShellLifecycleTests.TheWritersReadBackAfterAFailureCannotReplayTheMutation` (2); `Comms10kRefusalTests.C2ARevocationAfterTheFirstWrite...` (5), `C2TheServiceConsultsAuthorizationExactlyOnce...BeforeTheClear`, `C2AnOriginalSurvivingGrant...` (2), `C4PreflightSubset...` (2), `C4ReadbackMapsEveryMemberInBatches...`, `C4ObservedOtherIdentity...(concurrent-primary)` |
| M2 no clear | 31: the M1 set plus `Comms10kInterruptionTests` success/readback-fails/concurrent-removal cases and `C4UnreadableOrAmbiguousReadbackIsUnconfirmed` (5), `C4ObservedOtherIdentity...(reused-dn)` |
| M3 an authorization re-check on every add batch | 8: `C2ARevocationAfterTheFirstWriteDoesNotLeaveAHalfReplacedList` (5), `C2TheServiceConsultsAuthorizationExactlyOnce...BeforeTheClear`; `Comms10kReplaceSequenceTests.AuthorizationIsConsultedOnce_BeforeTheClear_AndNeverInsideAWrite`, `TheWriteBindsToTheReReadIdentity_NotTheConfiguredName` |
| M4 no authorization check at all | 11: `C2ADenialAtTheSingleCheckRefusesWithTheListUntouched`, `C2RevocationDuringAwaitedCredentialsRefusesBeforeActualDispatch` (2), `C2TheFinalClearGateFollowsAllPreparation` (6), `C2TheServiceConsultsAuthorizationExactlyOnce...`; `AuthorizationIsConsultedOnce_BeforeTheClear...` |
| M5 failed add not named (bare error text) | 2: `AFailedAddBatchAfterTheClear_...AndNamesTheBatchAndTheError`; `Comms10kRefusalTests.C4ObservedOtherIdentityNeverExpandsTheApprovedSet(concurrent-primary)` |

## Gates

At `952c290d`, from the worktree root, sequentially: `dotnet build ExchangeAdminWeb.slnx -c Release` 19 warnings, 0
errors; `dotnet test ExchangeAdminWeb.slnx -c Release --no-build -- xUnit.MaxParallelThreads=4` 8,757 passed, 0
failed; `dotnet format ... --verify-no-changes --no-restore` clean; `git diff --check HEAD` clean;
`tools/Test-AsciiOnly.ps1` passed. Not run: any live directory write. The `<GUID=...>` `-Add` form was verified live by
the owner on 2026-11-06 (decision entry); the module's own end-to-end path on a scratch group remains a deploy check.
