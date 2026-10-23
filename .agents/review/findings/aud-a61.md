# aud-a61: Self-grant check - bulk grants re-read a large cloud group once per row, the 50-group nesting bound refuses valid grants, and the write-time refusal does not say the earlier check passed

**Severity**: MEDIUM (part 1), LOW (parts 2 and 3). (1) Each bulk row calls `ValidateSelfGrantAsync`, which
calls `ReadGroupMembersAsync`, which runs `Get-UnifiedGroupLinks` / `Get-DistributionGroupMember -ResultSize
Unlimited`; a 200-row upload granting one large cloud group does 200 full traversals and courts throttling.
(2) `ExchangeGroupMembers` throws past 50 nested cloud groups, so a grant to a group with more than 50 nested
groups that does not contain the operator is refused as incomplete. (3) The aud-a50 write-time re-check
refusal shows the generic refusal and records the same `*_SelfGrantAttempt` action as the pre-confirmation
check, so the operator cannot tell that the first check passed and the state changed in between.
**Status**: In progress; plan below, approved under the owner's 2026-10-21 "EVERYTHING" go
**Branch**: `worktree-agent-a83ff1596fd6eee17` (from master `a7300dee`)
**Commit**: -
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

## Guard proof

Pending.
