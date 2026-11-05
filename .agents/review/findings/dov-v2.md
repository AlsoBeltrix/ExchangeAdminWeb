# dov-v2: Verify stored directory objects - the live-check effect of an ignore (slice V2)

**Severity**: MEDIUM - feature slice. A protected group deleted from the directory refused every write whose check found no
other match, and an admin's Ignore on Verify (V1) changed only what Verify reported; the refusal did not name the cure.
Plan `docs/DirectoryObjectVerify-Plan.md` (Approved 2026-11-04), B.4 and section E "V2"; decision `.agents/decisions.md`
2026-11-04.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/dov-v2` (worktree `D:\source\ExchangeAdminWeb-wt\dov-v2`, from master `98105998`)
**Commit**: see `git log 98105998..fix/dov-v2` (`fix(dov-v2): ...`, `record(dov-v2): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built), per plan point

1. **Only a `group_sid` Missing ignore changes enforcement.** `ProtectedPrincipalService.IgnoredMissingGroups(cfg, ignores)`
   returns the SIDs of `group_sid` rows an ignore matches with `VerifyIgnoreList.Matches(entry, "ProtectedPrincipals",
   "group_sid", <canonical SID>, "Missing", "not found")` - the keys V1's adapter and `ProtectedPrincipalVerifyActions.IgnoreAsync`
   write (`VerifyItem.IdentifierOrText` is the canonical SID for an identifier row; finding `VerifyFindingKind.ToString()`;
   observed `VerifyIgnoreList.NotFoundObserved`). A legacy Group row stored as the same SID is excluded, so its refusal stands.
2. **The lookup still runs.** `ProtectedGroupMembership.Evaluate(..., ignoredWhenAbsent)` (new optional parameter, null for
   every other caller): the token match and `Candidates` run as before; only `candidates.Count == 0` for a SID entry in the
   set is skipped - Debug line, `Result.Ignored` counted, not incomplete, no problem named. `Candidates` null, a throw (the
   catch), several matches, a found group's domain or link reads failing: unchanged, incomplete with or without the ignore.
   A found group is evaluated exactly as without the ignore (token, membership links, domain-local of another domain, no token).
3. **One snapshot.** V1's `_cachedIgnores` field is replaced by a `ConditionalWeakTable<ProtectedPrincipalConfig, ignores>`
   filled in `LoadEffectiveConfig` beside `_cachedConfig` from the same `TryRead` transaction; `IgnoresReadWith(cfg)` hands a
   check only the list read with the very rule set it holds (empty for a set the service did not load - a test stand-in -
   so nothing is ignored). No second read. `LoadEffectiveConfigAndIgnores` uses it and no longer takes the cache lock itself.
4. **NotConverted never ignorable at enforcement.** Rows stored by name never reach the evaluator (K.3) and
   `NotConvertedProblems` does not consult ignores; pinned by test.
5. **Corrupt list ignores nothing**: V1's `ParseOrEmpty` (empty + one Warning per load); V2 owns the enforcement test.
6. **Timing line**: `DirectoryReadOperation.CountIgnored(n)`; the line gains ", {Ignored} ignored missing groups" last inside
   the parentheses only when non-zero, so the kd-88 format (and every existing timing test) is unchanged otherwise.
7. **Refusal text**: `LabelGroupProblems` appends `NoSuchGroupCure` (" - run Verify on Admin Settings") to a protected group's
   "no such group", the same shape as "not converted - run db convert-identifiers" (unchanged).
8. **Help**: README Protected principals Verify paragraph (the "In this build an ignore changes only what Verify reports"
   sentence replaced with B.4's truth); new README Troubleshooting entry for "no such group - run Verify on Admin Settings";
   `docs/ConfigDbOperations.md` status-printout sentence (which `ignored` line changes enforcement); Admin Settings pane note,
   Ignore action tip (group_sid Missing: "affects protection"; every other: "only what Verify reports") and the missing badge
   tip for a group.
9. **Versions**: AdminSettings 1.3.0 -> 1.3.1. Base 2.36.0 unchanged.

## Where the code goes beside the plan's text

- **README Troubleshooting**: the brief named an existing "incomplete check" entry to extend; README has none for the
  protected-principal refusal except the "not converted" one. A new entry was added after it for the "no such group" refusal
  rather than folding a second cause into the "not converted" entry.
- **Legacy SID row with the same SID as an ignored `group_sid` row** is not ignored (not in the plan's text; fail-closed reading
  of "only a `group_sid` row's ignore").
- **The run-book sentence** "press Verify as the post-deploy check" was not added: plan D lists it, but the press is not
  needed for any V2 behaviour and V3/V4 add the other panes; left for the slice that completes the panes. Assumption that this
  is acceptable; flag if not.
- **Existing test changed**: `ProtectedGroupMembershipTests.TheServiceDelegatesTheDecision_AndPassesIncompleteThrough` (a
  source pin) now expects the call with `ignoredMissing`. No other existing assertion changed; no existing test pinned the
  "no such group" reason text at service level.

## Tests (new)

`ExchangeAdminWeb.Tests/ProtectedGroupIgnoreTests.cs`: `ProtectedGroupIgnoreEvaluatorTests` (FakeForest) and
`ProtectedGroupIgnoreEnforcementTests` (the service over `SelfGrantDirectoryStandIn`, ignores seeded in `app_setting`);
`DirectoryObjectVerifyPageTests.TheIgnoreTip_SaysAMissingGroupsIgnoreAffectsProtection_AndEveryOtherIsDisplayOnly`.
Plan E items: 1 `AnIgnoredSid_..._IsSkipped_...`, `AnIgnoredMissingGroup_StopsRefusing_...`; 2 (a-d)
`ARestoredGroup_ProtectsAgain_WithTheIgnorePresent_ExactlyAsWithout` x4 and service `ARestoredGroup_ProtectsAgain_...`;
3 `AGroupThatCannotBeRead_StaysIncomplete_...` x3 (lookup, group's domain, member links), `AnIgnoredGroupWhoseLookupFails_...`;
4 `ANonIgnoredMissingGroup_StillRefuses_NamingIt_AndPointsAtVerify`; 5 `ARowStoredByName_IsNeverIgnoredAtEnforcement_...` x2,
`ALegacyRowStoredAsTheSameSid_...`; 6 `ACorruptIgnoreList_IgnoresNothing_AndIsWarnedOnce`; 7
`SeveralGroupsAnsweringForAnIgnoredSid_StayIncomplete`; 8 timing assertions in the item-1 and item-4 service tests; 9
`AnIgnoreForADifferentRowIdentifierOrFinding_DoesNotSuppress` x5, `IgnoredMissingGroups_UsesTheKeysVerifyWrites_...`,
`AnIgnoreForAnotherSid_...`, `AnEntryThatIsNotASid_...`; snapshot `TheIgnoresAreThoseReadWithTheseRules_NeverAnotherSets`.

## Guard proof

Each mutation applied to the code at `7be58b19`, the test project built, the filtered tests (ProtectedGroupIgnore*,
ProtectedGroupMembershipTests, DirectoryObjectVerify*, ProtectionCheckTiming*, DirectoryReadOperation*; 162 tests) run and seen
to fail, the file restored (scripted; tree clean afterwards). 15 mutations, 15 caught.

| Mutation | Failing tests |
|---|---|
| M1 no skip on a confirmed zero | `AnIgnoredSid_..._IsSkipped_...`, `AnIgnoredMissingGroup_StopsRefusing_...` |
| M2 ignored entry short-circuited before any read (top of loop) | 13: restored x4, cannot-be-read x3, several, not-a-SID, item 1 x2, service restored, service lookup-fails |
| M3 several matches skipped too | `SeveralGroupsAnsweringForAnIgnoredSid_StayIncomplete` |
| M4 a failed lookup (catch) skipped when ignored | `AGroupThatCannotBeRead_...` (FindGroups), `AnIgnoredGroupWhoseLookupFails_StillRefuses` |
| M5 no SID-kind guard | `AnEntryThatIsNotASid_IsNeverSkipped_EvenWhenNamed` |
| M6 legacy SID row not excluded | `ALegacyRowStoredAsTheSameSid_KeepsRefusing_...` |
| M7 match on the row alone (no finding / observed) | `AnIgnoreForADifferent...` (finding, observation), `IgnoredMissingGroups_UsesTheKeys...` |
| M8 any store / row key | `AnIgnoreForADifferent...` (row key, store), `IgnoredMissingGroups_UsesTheKeys...` |
| M9 refusal without the Verify pointer | `ANonIgnoredMissingGroup_StillRefuses_NamingIt_AndPointsAtVerify` |
| M10 ignored count not on the timing line | `AnIgnoredMissingGroup_StopsRefusing_..._AndIsCountedOnTheTimingLine` |
| M11 lenient parse keeps the complete entries of a corrupt list | `ACorruptIgnoreList_IgnoresNothing_AndIsWarnedOnce` |
| M12 list re-read and re-parsed on every check (cache off) | `ACorruptIgnoreList_..._AndIsWarnedOnce` |
| M13 ignores of the latest load handed to any rule set | `TheIgnoresAreThoseReadWithTheseRules_NeverAnotherSets` |
| M14 a row stored by name dropped when an ignore names its text | `ARowStoredByName_IsNeverIgnoredAtEnforcement_...` x2 |
| M15 Ignore tip never says it affects protection | `TheIgnoreTip_SaysAMissingGroupsIgnoreAffectsProtection_...` |

Note on item 5: no V2 code path can make a row stored by name ignorable (such rows never reach the evaluator), so its test is
proven against M14, the plausible wrong implementation, rather than a revert.

## Gates

At `7be58b19`, from the worktree root: build 0 errors (17 warnings, below the 19 baseline); `dotnet test ExchangeAdminWeb.slnx
-- xUnit.MaxParallelThreads=4` Passed 8515 / 8515; `dotnet format --verify-no-changes` exit 0; `git diff --check HEAD` exit 0;
`tools/Test-AsciiOnly.ps1` passed. Not run: any live database, directory or Exchange; the page in a browser.

## Left for later

- V3/V4 per plan E; the run-book "press Verify on each page" post-deploy sentence with the last pane.
