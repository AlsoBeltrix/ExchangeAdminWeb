# aud-a23p: Migration - three wording leftovers: "protected principal" over an unverified exclusion at the partial-exclusion sites, the ExcludedADGroups description does not say what a value must be, and the mailbox-load banner gives no reason

**Severity**: LOW - operator messages. (1) aud-a23l fixed the all-excluded message; where a batch IS created
with some targets excluded, the NOTE in `WithProtectionOutcome`, the success formatter's Detail header, the
two page exclusion banners, the exclusion audit rows ("Excluded - protected principal, not migrated") and
the notification notes still call every excluded target a protected principal even when its check did not
answer. (2) The `ExcludedADGroups` module setting description says "Comma-separated AD groups" but not that
each value must be an identity `Get-ADGroup -Identity` resolves (aud-a23j), nor that a DN cannot be entered
because the setting splits at commas. (3) The mailbox-load banner says "Unable to load mailboxes" with no
reason; the reason reaches only the activity text.
**Status**: Merged `d690a6c1`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`)
**Commit**: `2dba5725` (fix, tests, record); plan `2bf76220`; comments `9fa7664d` (shared with aud-a23r, aud-a23o, aud-a23q)
**Reviewer**: implementer (surfaced by the aud-a23j-n agent, 2026-10-22, candidates 4, 7 and 8 of its hand-back)

## Evidence

At `09d3bacb`: `Services/MigrationService.cs` around 765 (`WithProtectionOutcome` NOTE) and 692 (the Detail
header); `Components/Pages/Migration.razor` around 198 and 368 (banners), 2065 and 2211 (audit errorDetail),
2074 and 2220 (notification notes), 1041 (the load banner) - line-keyed by `ClickGateRegistry`, 4377 lines,
edit in place only; `Modules/ModuleCatalog.cs` around 435 (the setting description - the supervisor owns the
module `Version` lines in that file, the description text is this finding's).

## Approach (to be written by the implementing agent before code)

- The partition's fourth list (aud-a23l `IsProtected`/unverified) drives every site: "excluded - protected
  principal" and "excluded - could not be verified as unprotected" are counted and named apart in the NOTE,
  the Detail header, the banners, the audit rows and the notification notes.
- The setting description names what a value must be and that DNs cannot be entered.
- The load banner carries the reason (the aud-a23i error-text helper).
- Tests through `MigrationStubExchange` and the page's reflection driver; mutations at the call site: one
  site's single wording again; the banner without the reason.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `3a948129`. One commit `fix(migration): ...
(aud-a23p)` on branch `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`), last, after aud-a23q. Migration, plus one
property on the shared `Models/PermissionResult.cs` (named for the base-app bump). Bumps are the supervisor's: in
`Modules/ModuleCatalog.cs` only the ExcludedADGroups description text changes, no `Version`. The page in place, 4377
lines, no registered line or snippet among the edits. Line numbers at `5eac8b25`.

1. **The partition's fourth list reaches the result.** New `PermissionResult.UnverifiedTargets` - the excluded entries whose
   protection check did not answer (aud-a23l's `unverified`; each also in `ExcludedTargets`, the same "target - reason"
   text). Set by the all-excluded result (`MigrationService` 592-598) and by `WithProtectionOutcome` (760-772), which now
   owns it; `WithDetail` (783-793) carries it. The call at 714 passes it.
2. **Named apart, through one helper** - new `MigrationService.ExclusionSummary(excluded, unverified)`: all protected, as
   before, "N protected principal(s) were excluded and NOT migrated."; none protected, "N target(s) could not be verified as
   unprotected and were excluded, NOT migrated."; a mix, "N target(s) were excluded and NOT migrated: K protected
   principal(s) and M that could not be verified as unprotected." - and `ExclusionAdvice(excluded, unverified)` for what to
   do: escalate a protected one (the existing sentence), check an unverified one again once its protection check can answer.
   Used at:
   - the NOTE on a success's message (765) - "NOTE: <summary>";
   - the success formatter's Detail header (688-695) - "EXCLUDED, NOT migrated (N): <summary>", the entries, the advice;
   - the page's two exclusion banners (198, 368) - the summary; their closing paragraph (205, 375) - the advice;
   - the exclusion audit rows (2065, 2211) - an unverified entry's errorDetail "Excluded - could not be verified as
     unprotected, not migrated"; a protected one's unchanged;
   - the admin notification notes (2073-2075, 2219-2221) - " | EXCLUDED (not migrated): <summary> <entries>".
   Each entry's own reason (the gate's denial text) is unchanged. A target is never called a protected principal unless its
   check answered so.
3. **The ExcludedADGroups description** (`Modules/ModuleCatalog.cs` 435) says what a value must be: a group's
   sAMAccountName, SID or objectGUID, which `Get-ADGroup -Identity` resolves in exactly one domain of the forest (aud-a23j,
   aud-a23r); that a distinguished name cannot be entered, because the setting is split at commas; and that membership is
   direct.
4. **The mailbox-load banner carries the reason** (1041): "Unable to load mailboxes in <batch>: <reason>. Refresh to try
   again." - the activity's own text (aud-a23i, in the cmdlet's words since aud-a23l), kept in a new field declared on
   1633's line and set on 1887's and 1950's lines when the load still owns the pane (the generation test the flag already
   uses); shown only while the flag is set, as before.

**Tests** - new `ExchangeAdminWeb.Tests/MigrationExclusionWordingTests.cs`, fakes only, over `MigrationStubExchange` (its
protection stand-in answers per target, aud-a23m; its recording mail stand-in also keeps each note):
- **the finding**: a bulk batch created with one protected, one unverified and one clear target - the NOTE, the Detail
  header and entries, `UnverifiedTargets`; through the page's `CreateMigrationBatch` - the banner counts and names each
  kind, the advice for both, one audit row per exclusion with its own kind's errorDetail, the notification note; the same
  with only unverified exclusions; a single create whose target is unverified - its banner and audit row.
- controls: only protected exclusions read as before (the NOTE `MigrationResultRewrapTests` pins included).
- `MigrationResultRewrapTests`: `UnverifiedTargets` is listed as `WithProtectionOutcome`'s own and `WithDetail` must carry
  it (the test enumerates every property); the call-site pin takes the new argument.
- the description names `Get-ADGroup -Identity` and says a distinguished name cannot be entered.
- the load banner: a failed load - "Unable to load mailboxes in <batch>: <reason>. Refresh to try again."
  (`MigrationMailboxLoadTests`' banner assertion takes the reason).

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored, touched, rebuilt, green): P1 (the
record's) one site's single wording again - the bulk banner's "@bulkExcluded.Count protected principal(s) were excluded and
NOT migrated."; P2 (the record's) the load banner without the reason. Extras: P3 the NOTE's single wording; P4 an
unverified exclusion's audit row filed "protected principal"; P5 the notification note's single wording; P6 the Detail
header's single wording; P7 `WithDetail` drops `UnverifiedTargets`.

**Gates** as the brief, plus `ClickGate*`/`ProgressRegistry*` green and `wc -l` 4377; comments after the mutation round.

## Guard proof

Built as the Plan. The fix commit carries no new code comments. `Components/Pages/Migration.razor` is 4377 lines before and
after: `git diff --numstat` 12/12, each changed line replaced in place (198, 205, 368, 375, 1041, 1633, 1887, 1950, 2065,
2074, 2211, 2220); none registered, no registered snippet changed. `Modules/ModuleCatalog.cs`: one line, the ExcludedADGroups
description (435); no `Version` touched.

- `Models/PermissionResult.cs` (base app): `UnverifiedTargets` (17), beside `ExcludedTargets`.
- `Services/MigrationService.cs`: the all-excluded result carries it (705); the success formatter's exclusion block (798):
  only protected - the header and the escalation line exactly as before; otherwise "EXCLUDED, NOT migrated (N) - <summary>",
  the entries, the advice for the kinds present; the re-wrap called with the partition's fourth list (825);
  `WithProtectionOutcome` (871) owns `UnverifiedTargets` (880) and words its NOTE through `ExclusionSummary` (886):
  only protected - "N protected principal(s) were excluded and NOT migrated." as before; none protected - "N target(s) could
  not be verified as unprotected and were excluded, NOT migrated."; a mix - "N target(s) were excluded and NOT migrated: K
  protected principal(s) and M that could not be verified as unprotected."; `ExclusionAdvice` (897): the escalation sentence,
  the recheck sentence, or both; `WithDetail` carries it (921).
- `Components/Pages/Migration.razor`: the two banners' headline (198, 368) `ExclusionSummary`, their closing paragraph (205,
  375) `ExclusionAdvice`; the exclusion audit rows (2065, 2211) "Excluded - could not be verified as unprotected, not
  migrated" for an unverified entry, the protected text unchanged; the notification notes (2074, 2220) " | EXCLUDED (not
  migrated): <summary> <entries>"; the load banner (1041) "<the load's failure>. Refresh to try again.", from
  `mailboxLoadFailure` (declared on 1633's line, set on 1887's and 1950's lines when the load still owns the pane).
- `ExchangeAdminWeb.Tests/MigrationStubExchange.cs`: the recording mail stand-in keeps each notification's note (`Notes`).
- `ExchangeAdminWeb.Tests/MigrationResultRewrapTests.cs`: `UnverifiedTargets` listed as `WithProtectionOutcome`'s own; the
  call-site pin takes the fourth argument. `MigrationMailboxLoadTests`: the banner assertion takes the reason.

New `ExchangeAdminWeb.Tests/MigrationExclusionWordingTests.cs`, 7 tests:

| Test | Pins |
| --- | --- |
| `ABatchCreatedWithBothKindsExcluded_NamesThemApart` | **the finding**: one protected, one unverified, one clear - the NOTE, `ExcludedTargets`, `UnverifiedTargets`, the Detail block. Before: "2 protected principal(s)", "EXCLUDED - protected principals" |
| `ABatchCreatedWithOnlyUnverifiedExclusions_CallsNoneOfThemProtected` | none called a protected principal; the recheck advice |
| `ABatchCreatedWithOnlyProtectedExclusions_ReadsAsBefore` | the control: NOTE and Detail exactly as before |
| `ThePagesBulkCreate_NamesEachKindApart_InTheBanner_TheAuditRows_AndTheNotification` | **the finding, the page**: the banner's summary and both advices, one row per exclusion with its own kind's errorDetail, the note; and no "2 protected principal(s)" anywhere it renders |
| `ThePagesSingleCreate_OfAnUnverifiedTarget_DoesNotCallItProtected` | the single create: banner, row, note |
| `TheExcludedGroupsSetting_SaysWhatAValueMustBe` | **(2)**: the description names Get-ADGroup -Identity, exactly one domain of the forest, and that a DN cannot be entered |
| `AFailedMailboxLoad_SaysWhyInThePane` | **(3)**: "Unable to load mailboxes in Wave_12: the mailbox query was refused. Refresh to try again." |

**Against the unfixed code: not runnable as a whole** - `UnverifiedTargets` and the mail stand-in's notes are new, so each
behaviour was put back at its own call site instead, and each revert fails the tests that pin it.

Mutations at the call site, by the same driver and protocol, over the 2008 tests of the filter extended by `ModuleCatalog*`
for this finding. Bytes (LF) before every mutation and after every restore: page `a99a3c59778eafe9a2787b57bc34503b`, service
`a76687f894eb7a508c14e564517ad2e7`.

| Mutation | File | md5 mutated | Failed (of 2008) | Restored, rerun |
| --- | --- | --- | --- | --- |
| P1 (the record's): one site's single wording again - the bulk banner's headline (368) | page | `88a1267a2325602d7a2ec4a5d5d9820a` | 1: `ThePagesBulkCreate...` | equal; 2008/0 |
| P2 (the record's): the load banner without the reason (1041) | page | `88928da5082b136a59a83c7e79409ff1` | 2: `AFailedMailboxLoad_SaysWhyInThePane`, `MigrationMailboxLoadTests.AFailedLoadRendersAnError...` | equal; 2008/0 |
| P3: the NOTE's single wording again | service | `6f72a479be7a689e6c0d8145413e5cf6` | 3: `ABatchCreatedWithBothKinds...`, `...OnlyUnverified...`, `ThePagesBulkCreate...` (the message renders above the banner) | equal; 2008/0 |
| P4: an unverified exclusion's audit row filed "protected principal" again (2211) | page | `48339ece130f2f00e126e5784ad19ce1` | 1: `ThePagesBulkCreate...` | equal; 2008/0 |
| P5: the notification note's single wording again (2220) | page | `34e324278be8d15063466a846197e8cb` | 1: `ThePagesBulkCreate...` | equal; 2008/0 |
| P6: the Detail header's single wording again | service | `cbf385a3b0b9235b82375d9bb13ae521` | 2: `ABatchCreatedWithBothKinds...`, `...OnlyUnverified...` | equal; 2008/0 |
| P7: `WithDetail` drops `UnverifiedTargets` | service | `1c2939259a14ee1805391fa571c4e7c3` | 1: `MigrationResultRewrapTests.EveryRewrap_CarriesEveryFieldItDoesNotOwn` (WithDetail) | equal; 2008/0 |

md5s (LF) of the other files at the fix commit: `PermissionResult.cs` `ded36f71838037e6f15601afa4037e74`, `ModuleCatalog.cs`
`ea2914c82475cee104e033b78e54c0b4`, `MigrationStubExchange.cs` `caa9777a99011ad2db0cbe6fddcabb07`,
`MigrationExclusionWordingTests.cs` `1e1732df8cedacf0dd9afcd64ff04ee3` (the round ran on
`f8eaaf410f9662f501bed0258926124a`; the ASCII gate then asked for two string literals as `⚠` / `—` escapes - the
same strings, no test changed - and the 2008 re-ran green), `MigrationResultRewrapTests.cs`
`e5592a6494e3a5cc82a1a840a8a4d64f`, `MigrationMailboxLoadTests.cs` `5c32019b248f3f2f22f7f4c9b2a094b9`.

On the fix commit's tree: Release build 0 errors, 59 warnings, the baseline's 19 code warnings and no other; the 2008 focused
tests 2008/0; `dotnet format --verify-no-changes` exit 0; `git diff --cached --check` exit 0; `tools/Test-AsciiOnly.ps1` exit
0; `wc -l` 4377.

## Known gaps

- `PermissionResult` gains a property (base app): no other module sets or reads it.
- The exclusion audit rows keep their action name, "CreateMigrationBatch_<direction>_ProtectedExclusion", for both kinds: it
  names the gate's exclusion, and a filter on it finds both; the row's errorDetail says which.
- Each banner's list still shows every entry with the gate's own reason, as before; the headline and the closing advice are
  what now tell the kinds apart.

## Comments and branch-tip gates

Shared by the four records of this branch; the canonical copy is in `aud-a23r.md` ("Comments and branch-tip gates"):
comments `9fa7664d`, comment-only by a stripped-code comparison; at the tip, Release build 0 errors / 59 warnings (the
baseline's), full suite 6086 passed / 3 skipped / 0 failed (6089), format, the whitespace check and the ASCII lint all exit
0, page 4377 lines.

## Closeout

Fixed on branch `worktree-agent-ac3fd1dd1db4f9c1d`; not merged, not pushed.
