# sweep-d-19: Comms-10k emptied the broadcast list before proving the new membership writable (sweep item 19)

**Severity**: MEDIUM likelihood, destructive if wrong - `.agents/review/live-risk-sweep-2026-10-30.md` item 19: the
replace cleared the `member` attribute, then added members as `<GUID=...>` values (`Comms10kService.cs:598`, `:602`,
ordered by `Comms10kReplaceWriter.cs:238-240`). Whether ADWS passes the GUID form through has never been observed; if it
refuses, the first live replace leaves the list empty and every re-run repeats it.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-d` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-d`, from master `6ea8963b`)
**Commit**: `1fec5a0f` (`fix(sweep-d, item 19): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- At `6ea8963b`: `Comms10kReplaceWriter.ExecuteSequence` ran `clear(); foreach (batch) addBatch(batch);` and
  `Comms10kService.PerformReplace` passed `ClearMembers` (`Set-ADGroup -Clear member`) and `AddMemberBatch`
  (`-Add @{member=<GUID=...>}`). A refusal of the first add batch left the read-back at 0 members: `PartlyApplied`.

## Approach (as built)

- **Add first, remove after.** `PerformReplace` reads the current `member` attribute once more before the write
  (`ReadCurrentMembers`, one `Get-ADGroup` plus the batched DN-to-objectGUID map, and the primary-group read that the
  approved-set check already made). `Comms10kReplaceWriter.Plan` derives the add set (file members not present, file
  order, distinct) and the remove set (present members the file does not name). `ExecuteSequence` writes every add batch
  (2,000, unchanged) and only when every one succeeded writes the remove batches (`Set-ADGroup -Remove @{member=...}`,
  same size). The read-back still decides the outcome; final membership equals the file.
- **A member both present and in the file** is in neither set: never written, never at risk, and the add phase cannot
  fail on it. An unchanged list writes nothing and succeeds on the read-back.
- **A failed add stops the run** before any removal. When the read-back shows the list exactly as before, the outcome is
  `RefusedBeforeAnyChange` with "Add batch k of n (m members) failed: <error> ... nobody was added and nobody was
  removed" (audit and notification carry it). Otherwise `PartlyApplied` says "nobody who was on the list before was
  removed". A failed remove is `PartlyApplied` saying every add succeeded and naming the extra count. The PartlyApplied
  message now counts the file's missing members and the extra ones from the observed membership; the page headline reads
  "The list does not match the file - re-run the same file."
- **Members the host's directory cannot map** (for example a member in another domain, read without a global catalog)
  are not fatal to the pre-write read: they cannot be file members (every file member was resolved by the same unscoped
  query), so they are removed by the exact stored value, after the objectGUID removals. An ambiguous mapping still
  refuses before any change.
- **Retry** is unchanged per operation (one add or remove batch); `-Remove` of an absent member succeeds silently
  (measured, plan doc), so a retried batch cannot double-apply.
- Help: page comment, cancellation and interrupted wording ("add/remove", "a mix of the old and new members"), README
  Comms-10k bullets. Comms10k `1.4.20`.

## Where the code goes beside the fix shape

- The sweep offered "add and verify one GUID before the clear, or fill first and remove after"; the brief chose fill
  first. Cost, from the plan's own measurement: a full disjoint swap at 10,001 members is about 53.2 s against 7.9 s
  (the group briefly holds both); a near-no-op change 3.8 s. Only members not already present are added, so a typical
  re-upload is far cheaper than the disjoint case.
- `ClearMembers` is gone; `RemoveMemberBatch` / `RemoveMemberValueBatch` replace it. `MembersWritten` keeps its meaning
  (every file member covered: present before or an acknowledged add); the add and remove counts are in the service log.
- One more refusal before any change: the current membership could not be read immediately before the write.
- Assumption, unverified: ADWS accepts `-Remove @{member=<GUID=...>}` whenever it accepts the `-Add` form. If it does
  not, the adds have landed and the removals fail: `PartlyApplied`, every file member on the list, extras named.

## Guard proof

| Mutation | Failing tests |
|---|---|
| M1 removals run before the adds | 25 failures: `Comms10kReplaceWriterTests.EveryAddBatchRunsBeforeAnyRemoval_AndNothingIsEverCleared`, `AFailedAdd_StopsTheRun_BeforeAnyRemoval_...`, `AFailedRemove_LeavesEveryFileMember_...`, `AFirstAddRefused_..._NotAnEmptyList`, `ARemoveThatThrew_StillAttemptsTheReadBack`, `ARetriedAddBatchIsRetriedAlone_...`, `UnidentifiedCurrentValues_AreRemovedByValue_...`; `Comms10kInterruptionTests.ActualWriteSequence...` (theory); `Comms10kPowerShellLifecycleTests.TheWritersReadBackAfterAFailureCannotReplayTheMutation`; `Comms10kRefusalTests.C2AnOriginalSurvivingGrant...`, `C2ARevocationAfterTheFirstWrite...`, `C2TheServiceConsultsAuthorizationExactlyOnce...` |
| M2 removals run after a failed add | `AFailedAdd_StopsTheRun_BeforeAnyRemoval_AndNamesTheBatchAndTheError`; `Comms10kInterruptionTests.ActualWriteSequence...` |
| M3 every file member added (present ones too) | `AMemberBothPresentAndInTheFile_IsNeitherAddedNorRemoved_...`, `AnUnchangedList_WritesNothing_...`; `Comms10kInterruptionTests.ActualWriteSequence...` (batch sizes) |
| M4 no "unchanged" refusal after a failed add | `AFirstAddRefused_WithTheListReadBackUnchanged_IsRefusedBeforeAnyChange_NotAnEmptyList`; `Comms10kPowerShellLifecycleTests.TheWritersReadBack...`; `Comms10kInterruptionTests.ActualWriteSequence...` |

The existing interruption, lifecycle, target-pin, approved-set and refusal stand-ins were moved from `-Clear` to
`-Remove`; their assertions follow the new order (never a clear, every add before any remove, a failed add leaves no
remove).

## Gates

Run once for the branch at its tip; see the sweep-d hand-back. Not run: any live directory write.

## Manual checks for the deploy list (from the sweep, unchanged)

- Before the first live replace: download the membership CSV.
- On a scratch group run `Set-ADGroup <scratch> -Add @{member="<GUID=$((Get-ADUser $env:USERNAME).ObjectGUID)>"}`, then
  `Get-ADGroup <scratch> -Properties member`.
- Added by this fix: then `Set-ADGroup <scratch> -Remove @{member="<GUID=$((Get-ADUser $env:USERNAME).ObjectGUID)>"}`
  and read `member` again, so the removal form is proven as well.

## Superseded in part (2026-11-06)

The add-then-remove order is reverted by owner ruling (`.agents/decisions.md` 2026-11-06): it reversed the approved
plan's measured rejection of that order and slows a large swap from seconds to minutes. The GUID member form was
verified live by the owner the same day. The named-refusal and item-20 changes stand. The revert is a separate slice.
