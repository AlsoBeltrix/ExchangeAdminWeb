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
**Status**: Merged `d690a6c1`; closed on guard proof; codex review pending; codex misc-batch-1 clean (2026-10-23)
**Branch**: `worktree-agent-ac3fd1dd1db4f9c1d` (forked at `5eac8b25`)
**Commit**: `dd74db81` (fix, tests, record); plan `2bf76220`; comments `9fa7664d` (shared with aud-a23o, aud-a23q, aud-a23p)
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

Built as the Plan. Line numbers are at the fix commit's bytes, which carry no new code comments (they come in their own
commit after the last finding's round); the aud-a23n comment that described the removed list went with it.

- `Services/MigrationService.cs`:
  - Part 1: `RefuseUnlessApprovable` (1388-): no status, and Completed, refused in aud-a23n's words as before; then
    `!MigrationBatchActionPlanner.Applies(MigrationBatchAction.Complete, status)` (1403) refuses before the first write:
    "<address> was not approved: approving it completes its migration batch <address>, which is <Status> - not a status the
    batch Complete action completes a batch from. Nothing was changed." `ApprovalRefusedBatchStatuses` is gone. It is called
    where it was (a batch of its own, before `Set-MigrationUser`); a mailbox in a batch of many is unchanged.
  - Part 2: `CheckMigrationEligibilityAsync` (67) makes a `DirectoryLookupRun` for its one check and calls
    `CheckEligibilityAsync` (70), the old body; `CheckBulkMigrationEligibilityAsync` makes one for the whole file (492).
    `CheckExcludedGroups` (284-): after `Import-Module`, the catalog from the run (`ForestGlobalCatalog`, 340:
    `(Get-ADForest -ErrorAction Stop).GlobalCatalogs | Select-Object -First 1` as "host:3268"; none - "the forest reported no
    global catalog"); the user searched on it (`-Server catalog`, 300), exactly one in the forest; the domain its DN names
    (`GroupManagementService.ServerFromDn`); the user read there by DN with memberOf (317); each configured group from the
    run, else `ResolveExcludedGroup` (360): the forest's domains from the run (`ForestDomains`, 349), `Get-ADGroup -Identity
    <value> -Server <domain>` in each (369), a not-found by structure (`IsNotFound`) "not in that domain", exactly one group
    across the forest. `DirectoryLookupRun` (405) and `DirectoryRead<T>` (414) keep what was read, or why not, for the run.
- `ExchangeAdminWeb.Tests/MigrationStubExchange.cs`: the AD stand-in gains `Get-ADForest` (`ForestDomains`,
  `ForestCatalogs`), a catalog search answering every domain's users as the catalog's copy (UPN and DN, no memberOf), a read
  by DN answered on the DN's own domain only, `Get-ADGroup` answered per domain (`DirectoryGroups` may hold several DNs;
  `Failures["Get-ADGroup <identity> @<domain>"]` fails one domain's), `DirectoryNotFoundIn` (the not-found naming the domain
  asked), `DirectoryUserIn` / `UserDn`, and `DirectoryCalls` (each lookup with its server). `-Server` joins the parameters a
  stub's logged step leaves out, so a failure keyed "Get-ADUser" still fails the search.
- `ExchangeAdminWeb.Tests/MigrationExcludedGroupTests.cs`: four expectations take the forest wording; the "no memberOf" row
  gains a DN, by which the read in its own domain finds it.

`ExchangeAdminWeb.Tests/MigrationApproveConfirmationTests.cs`, part 1 (3 tests changed, 1 new):

| Test | Pins |
| --- | --- |
| `ABatchInAStateAnApprovalCannotCompleteFrom_IsRefusedBeforeAnyWrite` (11: Failed, Removing, Stopped, Stopping, Completing, CompletedWithErrors, " stopped ", and now Syncing, SyncedWithErrors, Corrupted, AStatusNothingHereNames) | **the finding**: each asserted first to be a status the planner's Complete does not accept; refused naming it, "Nothing was changed.", the existence read the only call. Before: the last four ran every write |
| `TheApproval_CompletesItsBatchExactlyWhenTheCompleteActionWould` (14 statuses) | one owner: every step when `MigrationBatchActionPlanner.Applies(Complete, ...)` is true (Synced), the refusal and only the read otherwise |
| `ABatchTheCompleteActionCompletes_RunsEveryStep` (Synced, " synced ", "SYNCED"; was `ABatchInAnyOtherState_RunsEveryStep` with Synced, Syncing, SyncedWithErrors) | inverted: only a status the planner accepts runs every step |
| `ARefusalThroughThePage_IsOneFailedAuditRow_NamingTheState` | the page's audit row carries the new refusal |

New `ExchangeAdminWeb.Tests/MigrationForestLookupTests.cs`, part 2, 13 tests (15 cases), a forest of contoso.test (the
host's) and fabrikam.test:

| Test | Pins |
| --- | --- |
| `AUserOfAnotherDomain_IsFoundInTheForest_AndReadInItsOwnDomain` | **the finding**: a member of the group in the second domain - Ineligible; the calls: the catalog search, the read on fabrikam.test, the group asked of each domain. Before: "no directory user has the user principal name", Undetermined |
| `AUserOfAnotherDomain_InNoExcludedGroup_PassesTheCheck` | the same user, no group: the check passes (the size step's reason alone) |
| `AUserFoundNowhereInTheForest_CannotBeChecked` | fail closed: "no directory user in the forest has ...", Undetermined |
| `AUserWhoseDnNamesNoDomain_CannotBeChecked` | "has no distinguished name that names its domain (CN=bob,OU=Users)" |
| `AReadInTheUsersOwnDomainThatFails_CannotBeChecked` (thrown, written) | "reading <DN> in its own domain fabrikam.test failed: ..." |
| `AForestThatReportsNoGlobalCatalog_CannotBeChecked` | "the forest reported no global catalog"; no user lookup |
| `AForestThatCannotBeRead_CannotBeChecked` (thrown, written) | "the forest's global catalog could not be found: ..." |
| `AGroupOfAnotherDomain_IsResolvedThere` | a group only fabrikam.test holds is resolved there and matched |
| `ANameTwoDomainsHold_CannotBeChecked` | "resolved to 2 groups across the forest (contoso.test, fabrikam.test)" - never the first found |
| `AGroupNoDomainHolds_NamesEachDomainsAnswer` | "could not be resolved in any domain of the forest (contoso.test: ...; fabrikam.test: ...)", each in AD's words |
| `ADomainThatCannotBeAsked_LeavesTheGroupUnresolved` | a group found in one domain, another that fails: "could not be resolved in fabrikam.test: ..." |
| `ABulkRun_ResolvesEachGroupOnce_AndTheNextRunAsksAgain` | two rows: Get-ADForest twice (catalog, domains), each domain asked for the group once; a second run asks again |
| `AForestThatCannotBeReadInARun_IsTheSameAnswerForEveryRow` | a failure is kept for the run: every row the same reason, Get-ADForest asked once |

**Against the unfixed code: not runnable as a whole** - the stand-in's forest and the forest wording are new, so each
behaviour was put back at its own call site instead, and each revert fails the tests that pin it.

Mutations at the call site, each by exact-text replacement asserted to match once (session scratchpad `work/mutate.ps1`,
`work/round-a23r.ps1`): the fix staged; md5 recorded; `dotnet build ExchangeAdminWeb.slnx -c Release`; the 1888 tests of
`Migration*|ClickGate*|ProgressRegistry*|ExchangeServiceBase*|AuditService*|GlobalProgressWiring*|BlockedSender*|RecipientLookup*|CalendarPermission*|MailboxPermission*|OutOfOffice*|ConferenceRoom*|ValidatedWriteCallSite*|RazorParseGate*|LookupValidation*|DelegationReport*|MessageTrace*`
run with a TRX logger (failures read from it); the file restored from git's index, its mtime advanced so MSBuild rebuilds,
md5 re-checked equal, rebuilt, the 1888 re-run green - after every mutation. Service bytes (LF) before every mutation and
after every restore: `c0633594e3fc7bb591236fc4a493daf4`.

| Mutation | md5 mutated | Failed (of 1888) | Restored, rerun |
| --- | --- | --- | --- |
| R1 (the record's): the refuse list again (aud-a23n's six states) | `2627003bcc6604b294f3f53b8eff3cd1` | 11: `ABatchInAStateAnApprovalCannotCompleteFrom...` (Syncing, SyncedWithErrors, Corrupted, AStatusNothingHereNames), `TheApproval_CompletesItsBatchExactly...` (Syncing, SyncedWithErrors, Corrupted, Starting, Created, Waiting, IncrementalSyncing) - the writes ran | equal; 1888/0 |
| R2 (the record's): the user search without the forest's catalog (the host's domain answers, as aud-a23j's did) | `caf089596da4b2ef1834ef13ddfc2974` | 10: every `MigrationForestLookupTests` case with a user of the second domain - "no directory user in the forest" | equal; 1888/0 |
| R1b: Completed in the generic words | `dd346ee611d568c378bd91011f57ff9e` | 1: `AnAlreadyCompletedBatch_IsItsOwnOutcome...` | equal; 1888/0 |
| R2b: memberOf read from the catalog's copy, no read in the user's own domain | `4391edc089923fadf0a886fc81456e6b` | 15: every test that finds a user (`MigrationExcludedGroupTests` 8, `MigrationForestLookupTests` 7) - "reported no memberOf" | equal; 1888/0 |
| R2c: each group resolved only in the host's domain | `1f5316af616503ee394f84252305bc7a` | 6: `AUserOfAnotherDomain_IsFound...`, `AGroupOfAnotherDomain...`, `ANameTwoDomainsHold...`, `AGroupNoDomainHolds...`, `ADomainThatCannotBeAsked...`, `ABulkRun...` | equal; 1888/0 |
| R2d: no per-run cache for the groups | `2ed1e4bc56fe8785409ed6bfb58614ba` | 1: `ABulkRun_ResolvesEachGroupOnce...` | equal; 1888/0 |

md5s (LF) of the other files at the fix commit: `MigrationStubExchange.cs` `e338aab1b45451f8d23c73f988004990`,
`MigrationForestLookupTests.cs` `a7fa358852fd1fb272ed1a6602ae2bf6`, `MigrationApproveConfirmationTests.cs`
`7a1e51cb0a94700fcaee924bc61e2203`, `MigrationExcludedGroupTests.cs` `96e049d858d3dfc3bdd6dc5e1067972a`.

On the fix commit's tree: `dotnet build ExchangeAdminWeb.slnx -c Release` 0 errors, 59 warnings (the baseline; none in a
touched file); the 1888 focused tests 1888/0; `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore` exit 0;
`git diff --cached --check` exit 0; `tools/Test-AsciiOnly.ps1` exit 0. No directory was asked anything: every AD session
was the stand-in. The full suite runs at the branch tip.

## Known gaps

- **Labelled consequence (part 1):** a single-user batch whose mailbox needs approval and whose batch reports anything but
  Synced (unverified - no live call shows what Exchange reports then) is refused, naming the status; if the owner wants such
  a mailbox approved without its batch completing, that is a split this record argues against and he decides.
- **Assumptions, labelled (no live call, part 2):** a `-Filter` search on the catalog port spans the forest's domains (the
  repo measured `-LDAPFilter` doing so); the app-pool identity can read the catalog and a domain controller of each domain;
  `Get-ADGroup -Identity`'s "not in this domain" is ADIdentityNotFoundException under ObjectNotFound. Any wrong: the check is
  Undetermined, visible and blocking, one dev check settles it.
- Membership is still direct `memberOf` read in the user's own domain: a group in another domain holding the user (a
  domain-local or universal group there) is not in the user's domain's memberOf unless that domain controller holds its
  links, and a nested membership is not seen - as before. A configured name two domains hold must be given as the group's
  SID or objectGUID.

## Comments and branch-tip gates (shared by aud-a23r, aud-a23o, aud-a23q, aud-a23p)

The canonical copy for all four records; the other three point here.

**Comments (`9fa7664d`).** Written after every finding's mutation round, at the call sites the rounds proved load-bearing;
comments only. Checked by `check-comment-only.ps1` (session scratchpad `work/`): HEAD and the tree are each reduced to their
code - comment-only lines dropped (`//`, `///`, a one-line `@* ... *@`), the trailing comments this branch adds cut off
(`// aud-...`, `@* aud-... *@`), blank lines dropped - and the reductions are identical for all seven files. The checker was
shown to bite: against `d58a0bc5`, before aud-a23p's code, it reports the page, `PermissionResult.cs` and the catalog as
different. In the four `.cs` files every changed line is a comment or blank; on the three pages each changed line is the line
it was plus a trailing comment, on a line this branch had already changed, so `Migration.razor` stays 4377 lines,
`CalendarPermissions.razor` 762 and `MailboxPermissions.razor` 808, and every registered line key holds. The comments that
moved with code in aud-a23o (`IsCancellation`, `ReadSize`, `MailboxSizes`) are reworded for their new home. Code md5s
(reduced, unchanged by the commit): `ExchangeServiceBase.cs` `1bdd56fc386f4c833f2fb51faeefb437`, `MigrationService.cs`
`878c15bcc902d6cd54ab39d587124365`, `RecipientLookupService.cs` `23236d8448234afadf96a3e8c35f94ec`, `PermissionResult.cs`
`ad1ca45caa59a9ed75e474282fedc429`, `Migration.razor` `2c2c31c472f5133780379d3e3b55bdc9`, `CalendarPermissions.razor`
`d82d12ec45491f788371ee49ee6227a3`, `MailboxPermissions.razor` `b9e2b40f216e4e5aad492314fba6f952`. Final file md5s (LF) at
the tip: `Services/ExchangeServiceBase.cs` `653b18af6ad5ec0fac6fd6d11dc20cfa`, `Services/MigrationService.cs`
`9e419c743a3432539fedf788ce61e8ab`, `Services/RecipientLookupService.cs` `8715c127ff3085722977248ad2d681db`,
`Models/PermissionResult.cs` `f24fdbca73ad71e3044862c6b841cb3c`, `Components/Pages/Migration.razor`
`4a74131e65da90eec965556ec83d40a6`, `Components/Pages/CalendarPermissions.razor` `ca4f57000c438beddd95ceaf6cd033dc`,
`Components/Pages/MailboxPermissions.razor` `8e699a6b58d094ee29c03e8f8c3dc50d`; the tests and `Modules/ModuleCatalog.cs` are
as their fix commits left them. Focused rerun after the comments: 2008/0.

**Branch-tip gates (`9fa7664d`, all four fixes and the comments; the records-only commit after it changes no code):**

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 59 warnings - the count at `5eac8b25`, and the
  same 19 code warnings (file, line, column, id) as the baseline build of `5eac8b25`; none in a file this branch touched.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): Passed 6086,
  Skipped 3 (the known `ADDirectorySearchServiceTests ...WhenAdUnavailable_ReturnsEmpty` cases), Failed 0, Total 6089;
  5 m 23 s; "All tests finished running, Sequence file will not be generated." - no hang. This branch adds 97 cases (four new
  test classes, 15 + 42 + 15 + 7, and 18 in `MigrationApproveConfirmationTests`); the suite at `5eac8b25` was not run here, so
  5992 there is derived, not measured.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. The diff whitespace check against HEAD: exit
  0. `tools/Test-AsciiOnly.ps1` after staging everything: exit 0. `Components/Pages/Migration.razor`: 4377 lines.

No live Exchange, Active Directory, Graph, DHCP or IIS call was made: every Exchange Online cmdlet was a stub in an in-process
runspace, every on-premises cmdlet a stub defined through the services' own `ConnectOnPrem` seam, every AD cmdlet a stub in
the directory stand-in (a two-domain forest of `contoso.test` and `fabrikam.test`), the protection check and the mail
stand-ins. No PowerShell source changed, so PSScriptAnalyzer and Pester were not run. No coder self-review verdict is
claimed. Version bumps are the supervisor's: the base app (`ExchangeServiceBase`, `PermissionResult`), and the Migration,
Recipient Lookup, Calendar Permissions and Mailbox Permissions modules.

## Closeout

Fixed on branch `worktree-agent-ac3fd1dd1db4f9c1d`; not merged, not pushed. This record was taken from `691671fb` (master's
copy), so at merge it is an add/add of the same file: master's text plus this branch's Plan, Guard proof, Known gaps, the
shared section and this closeout - take the branch's copy.
