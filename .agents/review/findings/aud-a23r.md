# aud-a23r: Migration Approve still runs its multi-step writes for batch states it cannot complete, and the excluded-group user lookup is scoped to the host's domain, not the forest

**Severity**: HIGH (part 1), MEDIUM (part 2). (1) aud-a23n made the pre-read a REFUSE list (Completed,
CompletedWithErrors, Completing, Failed, Removing, Stopped, Stopping, no status), so an unlisted state
proceeds: with the batch Syncing, Corrupted, SyncedWithErrors or any state the agent did not foresee, the
approval applies `Set-MigrationUser`, `Set-MigrationBatch -CompleteAfter`, `Set-MoveRequest` and
`Resume-MoveRequest` before `Complete-MigrationBatch` refuses, leaving partial writes (and possibly a past
completion request) after the page reports failure (Known Failure Class 1). `MigrationBatchActionPlanner`
already says a normal Complete is valid only for `Synced`; the approval tests explicitly let Syncing and
SyncedWithErrors run every step. (2) aud-a23j's `Get-ADUser -Filter` runs with no `-Server` and no
forest/GC search, so a ToCloud user whose AD object is in another domain of the forest reads "no directory
user has the user principal name ..." and is Undetermined - fail-closed, but a functional false block for a
valid mailbox whenever excluded groups are configured.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; folded into the aud-a23o/p/q agent's scope
**Branch**: `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`)
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (mig-batch-7, 2026-10-22, candidates 1 and 2)

## Evidence

At `ee02f283`: `Services/MigrationService.cs` around line 1289 (the refuse list) and around line 293 (the
user lookup); `Services/MigrationBatchActionPlanner.cs` around line 146 (Complete valid only for Synced);
`ExchangeAdminWeb.Tests/MigrationApproveConfirmationTests.cs` (the cases that let Syncing and
SyncedWithErrors run every step).

## Approach (to be written by the implementing agent before code)

- Approval's batch-completion path is an ALLOW list that agrees with the planner: the completion steps run
  only when the own batch's status is one the planner names as completable (`Synced`; the planner is the
  single owner of that rule - call it, do not copy it); any other status refuses before any write, naming
  the state and that nothing was written. Where mailbox approval without batch completion is a valid
  operator intent, split it so the mailbox step alone runs and the Detail says the batch was not completed
  and why; otherwise refuse whole. The tests that let Syncing/SyncedWithErrors run every step are inverted.
- The excluded-group user lookup finds the user across the forest: the forest GC search by UPN (the aud-a2d
  shape - `mail`/`userPrincipalName` against a GC, host forest discovered at runtime, nothing named -
  Invariant 7), then `Get-ADUser -Server <domain of the found object>` for `memberOf`; the group
  resolutions `Get-ADGroup -Identity` likewise against the group's domain, resolved once per check run and
  cached for that run. A user found nowhere in the forest is still Undetermined (fail closed).
- Tests through the `MigrationStubExchange` AD stand-in (a user in a second domain; the planner's
  completable set vs each batch status); mutations at the call site: the refuse list again; the lookup
  without the forest search again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted at `691671fb` into the aud-a23o/p/q agent's scope, first
in its order. One commit `fix(migration): ... (aud-a23r)` on branch `worktree-agent-ac3fd1dd1db4f9c1d` (forked at
`5eac8b25`; this record taken from `691671fb`, whose other changes are records only), before aud-a23o. Module-scoped:
Migration (the bump is the supervisor's; `Modules/ModuleCatalog.cs`, `ExchangeAdminWeb.csproj` not edited). No page
change. Line numbers at `5eac8b25`, `Services/MigrationService.cs`.

1. **Part 1, an allow list with one owner.** `RefuseUnlessApprovable` (1294-1314) keeps aud-a23n's two outcomes of its own,
   in their words - no status, and Completed - and then refuses every status that
   `MigrationBatchActionPlanner.Applies(MigrationBatchAction.Complete, status)` does not accept, where it refused only the six
   `ApprovalRefusedBatchStatuses` (1288-1289, removed). The planner is called, not copied: it keeps Complete to exactly
   Synced (owner ruling 2026-09-25: "accepting an unanticipated status is the harmful direction - a wrongly-accepted Complete
   cuts mailboxes over"). The refusal throws before the first `Set-MigrationUser` (1203), so `RunAsync` returns it as the
   row's failure and both callers' existing audits record it Failed: "<address> was not approved: approving it completes its
   migration batch <address>, which is <Status> - not a status the batch Complete action completes a batch from. Nothing
   was changed."
   - **Not split into "approve the mailbox, leave the batch".** For a mailbox in a batch named for it, the approval's
     mailbox steps include `Set-MigrationUser -CompleteAfter <past>` (complete this mailbox now) and `Resume-MoveRequest`:
     for that batch's one mailbox they are the batch's cutover by another route - the thing the Complete rule refuses for a
     batch that is not Synced - so a mailbox-only split would do it while saying the batch was not completed. The whole
     approval refuses. A mailbox in a batch of many (no batch of its own) is unchanged: its mailbox steps run, as aud-a23h
     left it. **Labelled consequence:** if a single-user batch whose mailbox needs approval reports a status other than
     Synced (unverified - no live call), its approval is refused, naming the status; whether the owner wants a split for
     that case is his to decide on that evidence.
2. **Part 2, the user found across the forest and read in its own domain.** `CheckExcludedGroups` (281-327):
   - After `Import-Module`, the forest's global catalog, discovered at runtime from the host's own forest:
     `(Get-ADForest -ErrorAction Stop).GlobalCatalogs | Select-Object -First 1` (AddScript, the repo's measured idiom -
     reading GlobalCatalogs off the returned object comes back empty), as "host:3268", never a bare port. A failed read, or
     none, and the check cannot be completed. Nothing is named or configured (Invariant 7).
   - The user: `Get-ADUser -Filter "UserPrincipalName -eq '...'" -Server <catalog>` - exactly one in the forest (none:
     "no directory user in the forest has the user principal name <address>"; several: "N directory users in the forest
     have ..."); then re-read by its DN on a domain controller of the domain its DN names (`GroupManagementService.ServerFromDn`)
     with `-Properties memberOf`, because a global catalog holds only a partial memberOf for a user of another domain. A DN
     that names no domain, a re-read that fails or does not return the one user: cannot be checked.
   - Each configured group: `Get-ADGroup -Identity <value> -Server <domain>` in each domain of the forest
     (`(Get-ADForest -ErrorAction Stop).Domains` through `ADEmployeeIdLookup.ProjectDomainNames`); a not-found there, by
     structure (`IsNotFound`), means "not in that domain"; exactly one group across the forest is the group, and its DN is
     what memberOf is matched against. None ("could not be resolved in any domain of the forest (<domain>: <AD's words>;
     ...)"), several ("resolved to N groups across the forest (<domains>)"), or any other failure in a domain: cannot be
     checked. aud-a23j resolved each value in the host's domain only.
   - Once per check run: a new `DirectoryLookupRun` holds the catalog, the domains and each group's DN - or why one could
     not be read, kept too, so every row of a run is told the same and the directory is not asked again.
     `CheckMigrationEligibilityAsync` makes one for its check; `CheckBulkMigrationEligibilityAsync` (391-395) one for the
     whole file. The next run asks again.
   - Unchanged: the match (the resolved DN in memberOf, exact, ignoring case), what a check that cannot be completed does
     (the aud-a23j catch, 176-185: Undetermined, the reason in the cmdlet's own words), the app-pool identity (no
     credential, as before), direct membership only.
3. **Labelled assumptions (no live call).** The repo measured a `-LDAPFilter` search on the catalog port spanning domains
   (`ADDirectorySearchService.ResolveGlobalCatalog`); a `-Filter` search on it is assumed to as well (Conference Rooms makes
   one, aud-a2j). The app-pool identity can read the catalog and a domain controller of each domain. `Get-ADGroup -Identity`
   reports "not in this domain" as the AD module's ADIdentityNotFoundException under ObjectNotFound (the shape the
   stand-in writes and `IsNotFound` reads). If any of these is wrong, the check reads Undetermined - visible, never Eligible.

**Tests.** `MigrationApproveConfirmationTests`: `ABatchInAnyOtherState_RunsEveryStep` is inverted - Synced (also " synced ",
"SYNCED") runs every step; Syncing, SyncedWithErrors, Corrupted and a status nothing names refuse, the existence read the
only call; the refused-state tests take the new wording; new: the approval over a list of statuses runs its batch steps
exactly when `MigrationBatchActionPlanner.Applies(Complete, status)` is true, and refuses before any write otherwise.
`MigrationStubExchange`'s AD stand-in gains `Get-ADForest` (`ForestDomains`, `ForestCatalogs`), a catalog search that
answers every domain's users as the catalog's partial copy (no memberOf), a read by DN answered only on the DN's own
domain, `Get-ADGroup` answered per domain, and a log of each directory call with its server. `MigrationExcludedGroupTests`:
the existing cases take the forest wording; new - a user in a second domain (a member: Ineligible; not a member: passes),
found in no domain, a DN that names no domain, a re-read that fails; a group in a second domain; one name in two domains; no
catalog; the forest unreadable; a bulk run that resolves each group once, and a second run that asks again.

**Mutations at the call site** (md5 before/mutated/after, failing tests named, restored, touched, rebuilt, green): R1 (the
record's) the refuse list again; R2 (the record's) the user search without the forest's catalog. Extras: R1b Completed in
the generic words; R2b memberOf read from the catalog's copy (no re-read); R2c each group resolved only in the host's domain;
R2d no per-run cache.

**Gates** as the brief; comments after the mutation round.

## Guard proof

Pending.
