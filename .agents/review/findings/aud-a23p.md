# aud-a23p: Migration - three wording leftovers: "protected principal" over an unverified exclusion at the partial-exclusion sites, the ExcludedADGroups description does not say what a value must be, and the mailbox-load banner gives no reason

**Severity**: LOW - operator messages. (1) aud-a23l fixed the all-excluded message; where a batch IS created
with some targets excluded, the NOTE in `WithProtectionOutcome`, the success formatter's Detail header, the
two page exclusion banners, the exclusion audit rows ("Excluded - protected principal, not migrated") and
the notification notes still call every excluded target a protected principal even when its check did not
answer. (2) The `ExcludedADGroups` module setting description says "Comma-separated AD groups" but not that
each value must be an identity `Get-ADGroup -Identity` resolves (aud-a23j), nor that a DN cannot be entered
because the setting splits at commas. (3) The mailbox-load banner says "Unable to load mailboxes" with no
reason; the reason reaches only the activity text.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`)
**Commit**: -
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

Pending.
