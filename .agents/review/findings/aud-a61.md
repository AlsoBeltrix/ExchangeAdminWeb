# aud-a61: Self-grant check - bulk grants re-read a large cloud group once per row, the 50-group nesting bound refuses valid grants, and the write-time refusal does not say the earlier check passed

**Severity**: MEDIUM (part 1), LOW (parts 2 and 3). (1) Each bulk row calls `ValidateSelfGrantAsync`, which
calls `ReadGroupMembersAsync`, which runs `Get-UnifiedGroupLinks` / `Get-DistributionGroupMember -ResultSize
Unlimited`; a 200-row upload granting one large cloud group does 200 full traversals and courts throttling.
(2) `ExchangeGroupMembers` throws past 50 nested cloud groups, so a grant to a group with more than 50 nested
groups that does not contain the operator is refused as incomplete. (3) The aud-a50 write-time re-check
refusal shows the generic refusal and records the same `*_SelfGrantAttempt` action as the pre-confirmation
check, so the operator cannot tell that the first check passed and the state changed in between.
**Status**: Fixed on branch, not merged, not pushed; approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a83ff1596fd6eee17` (from master `a7300dee`)
**Commit**: plan `fa1ed15c`; fix `d945d8c7` (after aud-a60's `e06c1c2d`); comments `1c982906`
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-6, 2026-10-22, candidates 3, 4 and 5)

## Evidence

At `ea5e1a72`: `Services/MailboxPermissionService.cs` around 301 and `Services/CalendarPermissionService.cs`
(the per-row call); `Services/ExchangeGroupMembers.cs` around 71 (the bound); `Components/Pages/MailboxPermissions.razor`
704 and `Components/Pages/CalendarPermissions.razor` 658 (the write-time re-check; both pages line-keyed by
`ClickGateRegistry`, 808 and 762 lines, edit in place only).

## Approach (to be written by the implementing agent before code)

- An operation-scoped membership cache (keyed by operator and grantee group, holding results AND failures)
  handed to the validator by the bulk loops for the run (the aud-a23r `DirectoryLookupRun` shape); the
  single-grant path uses a fresh one.
- The nesting bound is a named constant reported specifically ("more than N nested groups; the check was
  not completed") and raised to a value that fits real tenants (document the choice); a traversal that is
  cut off is still a refusal, never an allow.
- The write-time refusal appends "the check before confirmation had passed; the state changed" and records
  a distinct audit action (`*_SelfGrantAttemptAtWrite`); same-line edits only.
- Tests through the bulk harness (one traversal for N rows), `ExchangeGroupMembersTests` (the bound text),
  `PermissionPagesSelfGrantTests` (the write-time wording and action). Mutations at the call site: the cache
  bypassed; the generic bound text again; the shared action again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21). Branch `worktree-agent-a83ff1596fd6eee17` from master `a7300dee`: one
fix commit `fix(permission-validator,permission-pages): ... (aud-a61)`, second of four, after aud-a60 (the membership reads it
keeps are the ones aud-a60 moves under the directory-read credential); comments in the branch's comments-only commit.
Behaviour: the shared `PermissionValidator`, `ExchangeGroupMembers` and `SelfGrantRefusalRecord` (base app version), the two
bulk loops and the two pages' write-time refusal (both module versions); the bumps are the supervisor's.

Evidence re-read at `a7300dee`:
- `MailboxPermissionService` 304 and `CalendarPermissionService` 185 call `ValidateSelfGrantAsync` once per row with nothing
  carried from row to row; per check, `CompareInExchangeGroupAsync` (`PermissionValidator` 868) reads a cloud group's whole
  membership and `CompareInDirectoryAsync` (816) evaluates the directory groups, so N rows naming one group read it N times -
  and, after aud-a60, fetch the directory-read secret N times.
- `ExchangeGroupMembers` 24 and 71-72: `MaxGroupsRead = 50` counts the root among the groups read, and the throw is a plain
  `InvalidOperationException` that the validator turns into the generic "whether you are a member of the group '<x>' could
  not be read".
- `MailboxPermissions.razor` 704 and `CalendarPermissions.razor` 658 (line-keyed by ClickGateRegistry, 808 and 762 lines): the
  write-time refusal's banner is the validator's refusal and its record is `Add{permType}_SelfGrantAttempt` /
  `SetCalendar_SelfGrantAttempt`, the action `SubmitSingle` records for the check before the confirmation.

1. **One run reads each group once.** New `Services/SelfGrantRun.cs`: what one run of the check has learned - Exchange's
   members of a cloud-only group (keyed by the operator's Exchange identity and the group's canonical address), the
   directory's answer for a set of groups (keyed by the operator's DN and the groups' DNs) and the directory-read credential
   (once per run) - each kept as its task, so a failure is kept as the failure and every later row is told the same (the
   aud-a23r `DirectoryLookupRun` shape; `Lazy` per key, safe if rows ever run concurrently). A new internal overload
   `ValidateSelfGrantAsync(principal, grantee, run)` takes it; the public two-argument check every page calls makes a fresh
   run, so a single grant and the on-premises write-time re-check read afresh. Each bulk loop makes one run before its rows
   and hands it to every row's check. What is per row stays per row: the operator's and the grantee's lookups and the
   directory session.
2. **The nesting bound is named, raised and reported.** `ExchangeGroupMembers.MaxNestedGroups = 250`, counting nested cloud
   groups (the root not counted; each read once by address, so a cycle is read once). Exchange documents no nesting limit
   for distribution groups; the bound exists only to end a read without end, and 250 - five times the old 50 - still bounds a
   check to a few hundred pooled reads. Only cloud-only groups count: a synced group nested in one is read in the directory.
   A traversal that reaches the bound throws `ExchangeGroupNestingBoundException` (the group and the bound), and the validator
   refuses as incomplete saying so - "the group '<x>' holds more than 250 nested groups, more than the check reads" - never an
   allow.
3. **The write-time refusal says what happened.** In place on 704 and 658: the banner is the refusal followed by
   `SelfGrantRefusalRecord.RefusedAtWrite` - "The same check had passed before you confirmed; made again right before the
   write, it refused." (the record's "had passed; the state changed", worded to be true also when the re-check could not be
   completed) - and the record's action is `Add{permType}_SelfGrantAttemptAtWrite` / `SetCalendar_SelfGrantAttemptAtWrite`, its
   `errorDetail` the validator's refusal as before. Same lines; 808 and 762 kept.

Tests. `PermissionValidatorSelfGrantTests`: one run over several checks of a cloud-only group reads its members once, and a
read that fails once with every check refused alike; a directory group evaluated once and its secret fetched once; a fresh
run reads again; both bulk loops over several rows naming one cloud group read it once, and a failing read once; a traversal
past the bound refused with the bound's text. `ExchangeGroupMembersTests`: past the bound throws the bound's exception naming
it; exactly the bound reads whole; a hundred nested groups (past the old bound) read whole. `PermissionPagesSelfGrantTests`: the
write-time refusal's banner and action on both pages, the dropped-row and missing-writer cases updated to them.
`EverySelfGrantRecord_CarriesTheValidatorsOwnRefusal` counts both actions; the overload test takes the run overload as well.

Mutations at the call site: M1 the cache bypassed - the Mailbox Permissions loop handing each row a fresh run (M1c the
Calendar loop); M2 the generic bound text again - the validator's catch of the bound removed; M3 the shared action again on
`MailboxPermissions.razor` 704 (M3c `CalendarPermissions.razor` 658). Extras: E1 the run not consulted for Exchange's members
in the validator; E2 the bound back at 50; E3 the write-time sentence dropped from the banner.

Files: `Services/SelfGrantRun.cs` (new), `Services/PermissionValidator.cs`, `Services/ExchangeGroupMembers.cs`,
`Services/MailboxPermissionService.cs`, `Services/CalendarPermissionService.cs`, `Services/SelfGrantRefusalRecord.cs`,
`Components/Pages/MailboxPermissions.razor` (704), `Components/Pages/CalendarPermissions.razor` (658), the tests named, this
record.

Caller-visible change, by design: a bulk file's rows naming one group read its membership once - a read that failed once,
refusing every such row alike. A cloud group nesting up to 250 cloud groups is checked; more is refused as incomplete, naming
the bound. The on-premises write's re-check refusal says the earlier check had passed and is audited as
`*_SelfGrantAttemptAtWrite`.

## Fix

- New `Services/SelfGrantRun.cs`: `ExchangeMembersAsync(operator, group, read)`, `DirectoryMembershipAsync(operatorDn, groups,
  evaluate)` and `DirectoryReadCredentialAsync(acquire)`, each kept per key as a `Lazy` task, so a result or a failure is had
  once per run and handed to every later caller alike.
- `Services/PermissionValidator.cs`: the public `ValidateSelfGrantAsync(principal, grantee)` is a fresh run of new internal
  `ValidateSelfGrantAsync(principal, grantee, run)`, which hands the run down: `CompareInExchangeGroupAsync` reads Exchange's
  members through `run.ExchangeMembersAsync(exchangeIdentity, <the group's canonical address>, ...)`; `CompareInDirectoryAsync`
  has the credential through `run.DirectoryReadCredentialAsync` and the evaluation through `run.DirectoryMembershipAsync`. A
  read that ran past the bound (`ExchangeGroupNestingBoundException`) refuses "the group '<x>' holds more than 250 nested
  groups, more than the check reads"; any other failure keeps aud-a52's text.
- `Services/ExchangeGroupMembers.cs`: `MaxGroupsRead = 50` (the root counted) is `MaxNestedGroups = 250` (nested cloud groups
  found, each once); the read throws `ExchangeGroupNestingBoundException(group, bound)` - "'<group>' holds more than 250 nested
  groups" - when it finds one more, before reading it.
- `Services/MailboxPermissionService.cs`, `Services/CalendarPermissionService.cs`: one `SelfGrantRun` before the rows, handed to
  every row's check.
- `Services/SelfGrantRefusalRecord.cs`: `RefusedAtWrite` and `AtWrite(refusal)`.
- `Components/Pages/MailboxPermissions.razor` 704 and `Components/Pages/CalendarPermissions.razor` 658, in place (808 and 762
  lines): the banner is `SelfGrantRefusalRecord.AtWrite(selfGrantError)` through `Banner`, the record's action
  `Add{permType}_SelfGrantAttemptAtWrite` / `SetCalendar_SelfGrantAttemptAtWrite`, its `errorDetail` `selfGrantError`.

Tests: `PermissionValidatorSelfGrantTests` +8 - `OneRun_ReadsACloudGroupsMembersOnce_ForEveryCheckThatNamesIt_AndANewRunReadsAgain`,
`OneRun_KeepsAReadThatFailed_AndRefusesEveryCheckAlike`, `OneRun_EvaluatesADirectoryGroupOnce_AndHasItsCredentialOnce`,
`BothBulkLoops_ReadAGroupTheirRowsShare_Once` (both loops, the read answering and failing: three rows, one read; a failure
refuses all three with one text, each recorded), `ACloudGroupNestingPastTheBound_IsRefused_SayingSo`; updated, not weakened:
`EverySelfGrantRecord_CarriesTheValidatorsOwnRefusal` counts both actions and pins one write-time action per page, and
`TheStringOnlySelfGrantCheck_IsGone_AndTheCheckTakesThePrincipal` pins the two overloads (the public one exactly principal and
grantee, the run overload principal, grantee and run). `ExchangeGroupMembersTests`: `ANestingPastTheBound_Throws_NamingTheBound`
(251 nested groups: the exception, its group, bound and text; 251 reads, the 251st nested group found and not read),
`AsManyNestedGroupsAsTheBound_AreReadWhole` (250), `AHundredNestedGroups_PastTheOldBound_AreReadWhole`; the theory's
"more groups than the bound" row moved to them. `PermissionPagesSelfGrantTests`: aud-a50's three write-time cases take the
banner with the write-time sentence and the `*AtWrite` actions (both pages).

## Guard proof

The aud-a60 driver and protocol (that record's Guard proof; log `a61-mutations.log`). Filter
`FullyQualifiedName~PermissionValidator|FullyQualifiedName~ExchangeGroupMembers|FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ExchangeIdentityResolver|FullyQualifiedName~ClickGate|FullyQualifiedName~ProgressRegistry|FullyQualifiedName~BulkPermission|FullyQualifiedName~AuditWriterThrowFree`
(747 tests). Fixed-file MD5s: `Services/PermissionValidator.cs` `79AADDB714D6C9EC8CF7A5A530CFE40C`,
`Services/ExchangeGroupMembers.cs` `3EB46F04FC2026BD7203DDCFA0931395`, `Services/MailboxPermissionService.cs`
`FAD030E0E813D5F1D30704B4FACA151A`, `Services/CalendarPermissionService.cs` `6006CF56404A0E1D50A1F585BC5817BF`,
`Components/Pages/MailboxPermissions.razor` `61124954514D9E7426442A6E693C034B`, `Components/Pages/CalendarPermissions.razor`
`8FA41FEA6EE1CA6FE59038D997B8C275`. Every mutant built with 0 errors and every restore matched; each page mutant kept its
page's line count, so ClickGate and ProgressRegistry ran under it and passed. After the round, the restored tree rebuilt and
the filter: 747/747 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | the cache bypassed: the Mailbox Permissions loop handing each row a fresh run | `MailboxPermissionService.cs` `03D921912F8B9A09DF8CF2AB02DCA5CD` | 2 of 747: both `BothBulkLoops_ReadAGroupTheirRowsShare_Once(calendar: False, ...)` rows - three reads |
| M1c | the same in the Calendar Permissions loop | `CalendarPermissionService.cs` `0732B907AB9467DBF47D49AFF7F95A74` | 2 of 747: the same test's `calendar: True` rows |
| M2 | the generic bound text again: the validator's catch of the bound removed | `PermissionValidator.cs` `620664878F1FC7A799913F60A1DC37D8` | 1 of 747: `ACloudGroupNestingPastTheBound_IsRefused_SayingSo` - "could not be read" |
| M3 | the shared action again, `MailboxPermissions.razor` 704 | `1D18DD6CC98283F9F3F54005992F362F` | 2 of 747: `TheOnPremWrite_ReChecksTheGrantee_RightBeforeItWrites_AndRefusesASelfGrant(page: "Mailbox Permissions")`, `EverySelfGrantRecord_CarriesTheValidatorsOwnRefusal(... MailboxPermissions.razor ...)` |
| M3c | the same, `CalendarPermissions.razor` 658 | `059697406441A01CA9BD67F94C04CEC0` | 2 of 747: the same two tests' Calendar Permissions rows |
| E1 | extra: the run not consulted for Exchange's members (a fresh run per call) | `PermissionValidator.cs` `BDF2EB8683DE26CF3CCB29DFBE162121` | 6 of 747: all four `BothBulkLoops_...` rows, `OneRun_ReadsACloudGroupsMembersOnce_...`, `OneRun_KeepsAReadThatFailed_...` |
| E2 | extra: the bound back at 50 | `ExchangeGroupMembers.cs` `148B2F5116F4636E639F59DDB2F09E36` | 1 of 747: `AHundredNestedGroups_PastTheOldBound_AreReadWhole` |
| E3 | extra: the write-time sentence dropped from the banner (Mailbox Permissions 704) | `MailboxPermissions.razor` `C60087031F5EA01BFF0BDEA799A80320` | 3 of 747: the three write-time cases' Mailbox Permissions rows - `TheOnPremWrite_ReChecksTheGrantee_...`, `TheOnPremRefusal_WhoseRecordCannotBeWritten_...`, `TheOnPremBanner_WhenTheWriterDropsTheRefusalsRow_...` |

No separate pre-fix reproduction: M1 and M3 are the code as it stood at each call site (a run per row is the old per-row
read), and M2 the refusal as it stood.

## Known gaps

- A run keeps what it read for its whole length: a membership that changes while a bulk file is being worked through is
  answered for every later row as the first row found it. A bulk run has no write-time re-check of its own; each row's check
  is made just before that row's write, as before, and only the membership reads are shared.
- A failure is kept the same way: a transient Exchange or directory failure refuses every row of that run naming the group.
  The operator re-runs the file; a new run reads again.
- The bound is the check's own choice, documented in `ExchangeGroupMembers.MaxNestedGroups`; a deeper tree of cloud groups is
  refused as incomplete, naming it. Not measured against a tenant.
- What is per row stays per row: the operator's and the grantee's lookups (two Exchange reads) and the directory session (a
  runspace and the ActiveDirectory module) - the cost aud-a43 records.

## Comments after the round

In the branch's comments commit `1c982906` (aud-a60's record has the check): `Services/PermissionValidator.cs`'s remarks name
the run where the membership reads are described, and `Services/IIdentityResolver.cs`'s `ReadGroupMembersAsync` names the
bound's own exception; code lines equal before and after. The other comments of this record were written with the fix.

## Gates - 2026-10-23 (branch tip `1c982906`: the five fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: exit 0, 0 errors; 19 warnings - the baseline, 19 at
  `a7300dee`, the same nineteen - none in a touched file. NuGet audit on (NU1902/3/4 errors): none raised.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0, 6,387 passed / 3 skipped (the
  three known AD skips, `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`) / 0 failed, 6,390 total, 6m55s;
  nothing hung (the blame folder empty, no Sequence file). After aud-a60 alone: 6,333 / 3 / 0.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `git add -A` then `tools/Test-AsciiOnly.ps1`: exit 0 ("ASCII lint passed"). The pages: 808 and 762 lines.

No AD, Exchange Online, Graph, DHCP or IIS call: the directory is `SelfGrantDirectoryStandIn`, Exchange the harness's pool over
stand-in cmdlets or a stand-in resolver, the on-premises session the harness's `DefineOnPremSession`, the secret store a
stand-in fetch, the pool's connect step a local runspace of stand-in functions. No coder self-review verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-a83ff1596fd6eee17`, not merged, not pushed. Due at merge: the base app version - the shared
`PermissionValidator`, `ExchangeGroupMembers`, `SelfGrantRefusalRecord` and the new `SelfGrantRun` - and the Mailbox
Permissions and Calendar Permissions module versions: a bulk file reads a shared group once, the nesting bound and its
refusal, and the write-time refusal's text and audit action.
