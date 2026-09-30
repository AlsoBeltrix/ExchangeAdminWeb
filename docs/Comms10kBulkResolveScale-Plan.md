# Comms-10k At Full Size - Plan

Status: **Approved by the owner, 2026-09-28**, at revision 15; revision 16 folds in the round-13
review findings and 17 through 24 the findings of rounds 14 to 21, all within that approved
scope. **Round 22 returned "best approach - no material changes are needed", ready to implement
as written.** **S1 of 4 IMPLEMENTED 2026-09-30 (module set to `1.3.0`); S2, S3 and S4 not started.**
Module: `Comms10k` (`1.2.0` -> `1.3.0`). **No base app bump** - this work is module-scoped.
`ExchangeAdminWeb.csproj` is at `2.24.0` as of 2026-09-28 and must be byte-identical after every
slice; verify by diff rather than by reading this line, which goes stale whenever another work
stream bumps it.
Scope: `Services/Comms10kService.cs`, `Components/Pages/Comms10k.razor`,
`Components/Pages/ModuleConfig.razor` (one list entry), `Modules/ModuleCatalog.cs` (version bump),
`README.md`, one new pure helper, new tests, and the governance records named below.

Revision 15 settles the write shape by measurement: **clear the group, then fill it in batches.**
Every claim about directory limits below was measured on 2026-09-25 against
`CN=Test-WebApp,OU=ZZMikeCTest,DC=ad,DC=analog,DC=com`, a Distribution, Universal group the owner
supplied for the purpose. The numbers are in "Measured facts".

**Correction to the record.** Commit `1a957d1`, "docs(comms10k): the scale plan is approved",
marked this plan approved by the owner **at a point when it was not**. The owner had stated the
opposite on 2026-09-25 ("this plan is not approved"), and the review verdict that commit cited -
codex round 12, "best approach, no material changes needed" - was returned against revision 12,
whose write design revision 15 replaces on measured evidence. That header was written by another
session.

**Approval was subsequently given, by the owner, on 2026-09-28, at revision 15** - after the plan
was rewritten around the measured directory limits. The status line above is that approval, not
the earlier false one. This paragraph stays because commit `1a957d1` remains in the log and its
message reads as approval three days before any was given.

## What this module does

The communications team mails more people than Exchange allows as individual recipients in one
message, so they mail one distribution list instead. This module sets that list's membership from
an uploaded CSV. Ten thousand is the number it was sized against and is a target, not a limit.

## Measured facts

Measured, not inferred. Method: populate the test group, time each operation, read back and
compare. Directory: `ASHBDC1`, 26,000 user objects available, average DN length 72.6 characters.

**A single whole-list replace has a hard ceiling below the module's target.**

| Members in one `Set-ADGroup -Replace` | Result |
| --- | --- |
| 5,000 | succeeded, 26.4s |
| 8,000 | succeeded, 49.7s |
| 9,500 | succeeded, 52.7s |
| 10,000 | **failed in 0.2s**, `ADServerDownException` |
| 10,500 and 12,000 | same, reproduced four times |

The directory was healthy immediately after each failure and the group still held its previous
9,500 members, so this is a per-operation limit, not an outage. **The module is named for ten
thousand and the atomic write fails at exactly ten thousand.**

**It is Active Directory's limit, not the web-services layer's.** A single LDAP modify of 12,000
values through `System.DirectoryServices.Protocols`, bypassing ADWS entirely, is refused by the
directory itself:

```
ResultCode: OperationsError
000021B1: SvcErr: DSID-0315154C, problem 5005 (UNABLE_TO_PROCEED)
```

So no API offers a single-operation replacement at this size. **Non-atomic is forced by the
directory.** This is the fact that revision 13 got wrong by assuming, and revisions 7 to 12 got
accidentally right for reasons that were not evidence.

**Write cost scales with the size of the group being written to, not with the size of the batch.**
This is the measurement that decides the design. The same 2,000-member `-Add` chunk:

| Group size when the chunk is applied | Time for that chunk |
| --- | --- |
| 0 to 8,000 | 0.7s to 1.4s |
| 10,000 to 20,000 | 9.0s to 10.8s |

**Full-swap comparison, 10,001 members out and a disjoint 10,001 in:**

| Approach | Total | Why |
| --- | --- | --- |
| **Clear, then add in batches** | **7.9s** | every write lands in a group of 10,000 or less |
| Add all, then remove the stale | 53.2s | peaks at 20,002 members; each add chunk costs ~10s |

Both produced exactly the target membership, compared member by member. Add-then-remove is
**seven times slower** on a full swap and additionally requires reading the current membership
first. Its one advantage - the list is never incomplete - buys an eight-second window at the cost
of a 7x slowdown and a mandatory read, so it loses. Clear-then-fill also needs no diff, no removal
set and no knowledge of what was there.

For the record, on a near-no-op change (12,000 members, only 1,000 in and 1,000 out)
add-then-remove finishes in 3.8s because unchanged members cost nothing. That case does not
justify the design: it is the best case, not the one to size against.

**Add and Remove are both idempotent** - `-Add` on a member already present and `-Remove` on one
absent both succeed silently. Not needed by the chosen design, but it means a re-run is safe.

**Reading 12,000 members takes 0.2s** through the `member` attribute and returns all of them. No
truncation at this size.

**The directory intermittently refuses writes for an unrelated reason.** Three of nine large
writes failed with `A required audit event could not be generated for the operation` - at 6,000
(twice of three attempts), 9,000 and 9,800, while 9,500 succeeded. Non-deterministic and unrelated
to size; it never appeared during any chunked run. This is a domain-controller audit condition,
not something this module causes, but it will surface as occasional write failures and the module
must report it rather than swallow it. **Worth investigating separately from this work.**

## What is broken

**Validate asks the directory one question per CSV row.** `Services/Comms10kService.cs:133-157`
issues one `Get-ADUser -Filter` per address. At 5,279 addresses that is 5,279 sequential queries
and ADWS invalidates an enumeration context partway through - the reported error, passed through
by the catch at `Comms10k.razor:294`. The string appears nowhere in this repository.

**The write cannot reach the target size**, per the measurements above.

**Preview and Download CSV already fail.** `Comms10kService.cs:64-68` calls `Get-ADGroupMember`,
which this file documents at `:197-205` as capped at 5,000 by ADWS and throwing past it. That is
why `ExecuteReplaceAsync` reads the raw `member` attribute instead; `GetMembersAsync` never got
the same fix.

**The write reports a success it never checks.** On no exception it returns
`FinalCount = resolvedDns.Count` (`:244`) - its own input, not the directory's state. Every other
membership write in this repository reads back; `GroupManagementService.cs:648-660` states the
rule: success is decided only by the membership reflecting the change.

**The protected-principal check runs once per member**, opening a runspace and importing the AD
module each time (`ProtectedPrincipalService.cs:548-558`), and again per member per protected
group (`:828-931`). Being deleted, not optimised - owner ruling below.

**Secondary:** `Comms10kService.cs:135-137` interpolates the address into `-Filter` after doubling
only the single quote. `SectionAccessGroupDirectory.cs:132` records why that is wrong: `-Filter`
expands `$` as a PowerShell variable, and `$` is legal in an address. Fixed by the resolve
rewrite.

## The write: clear, then fill

The directory forbids a single operation at this size, so the write becomes two steps - and they
are exactly the two the module is described by: **empty the group, populate it from the CSV.**

0. **Resolve the configured group name to its objectGUID and distinguished name once**, before
   the lock is taken, and use that identity for the lock key and for every subsequent operation.
   **Verify at the same moment that the target is a DISTRIBUTION group, and refuse if it is
   not** - see the guard below, which is a condition of the protected-principal exemption.
   The Constitution requires directory mutations to bind to immutable identifiers and re-read
   before write where practical (`docs/ProjectConstitution.md:91`). Today every call passes the
   configured **name** (`Comms10kService.cs:207`, `:223`), so a rename or a same-named object in
   another container between operations would retarget the write mid-sequence - a risk the
   single atomic write did not have, and one the lock cannot cover because the lock key is
   derived from the same identity. Resolution failure refuses before anything is written.
0b. **After the lock is held and immediately before the clear, re-read the target by its
   objectGUID and re-check the category.** Step 0 runs before the lock because its objectGUID is
   the lock key, so an unbounded wait can sit between resolving and writing. The Constitution
   requires a re-read before write where practical (`docs/ProjectConstitution.md:91`), and here
   it is one cheap query that also confirms the object still exists and is still a Distribution
   group. A mismatch - gone, or no longer Distribution - refuses before the clear.
1. **`Set-ADGroup -Clear member`** against that resolved identity - one operation, 2.8s at
   10,001. The group is now empty.
2. **`Set-ADGroup -Add` in batches of 2,000** until the resolved list is written - 0.7s to 1.4s
   per batch, because every write lands in a group of 10,000 or less.
3. **Read back and compare with the target.** Success is reported only on a match, and the
   reported final count comes from that read, never from the input.

Measured end to end at 10,001 members with no overlap: **7.9s**, final membership exactly the
target.

No read of the existing membership. No comparison. No removal set. Nothing in the write depends on
what was in the group before, which is what makes it both the fastest option and the smallest
amount of code.

**Batch size is 2,000** - one named constant, with a comment recording that the measured
single-operation ceiling is 10,000 and that write cost rises steeply with group size, so this is
not a number to raise casually. It bounds a request; it is not a limit on membership.

**What this gives up:** atomicity, and it is not recoverable. The current single operation either
lands or does not; nothing at this size can, because the directory refuses it. Between the clear
and the last batch - about eight seconds - the list is empty and then partial, and a failure in
that window leaves it incomplete. The alternative that avoids the window, adding everyone before
removing the stale, was measured at 53.2s on the same swap, seven times slower, and needs a read
of current membership: it buys an eight-second exposure for a 7x slowdown on every run. Rejected.

Consequences that follow, and must be handled rather than hoped away:

- **A partial application is reported exactly** - how many were written, what the membership now
  is, and that the list is incomplete. The audit record carries the observed outcome, not the
  intent. This is the one case where the operator must act, so the message says to re-run the
  same CSV, which fully repairs it.
- **Re-running is always safe.** The write does not depend on prior state, and `-Add` on an
  existing member is a silent no-op, so a repeat run of the same file converges.
- **Serialise per group, and a per-process lock is not enough here.** Two concurrent replaces
  would interleave clears and batches and could leave the list holding neither file - one run's
  clear landing midway through the other's fill. That is strictly worse than the single atomic
  write it replaces, so the lock is required, not optional.

  `Services/SelfServiceGroups/SelfServiceGroupService.cs:46,523` is the in-process precedent:
  a `SemaphoreSlim(1, 1)` from a
  `ConcurrentDictionary` keyed on the group's objectGUID. **It is insufficient for this sequence.**
  Dev and prod run as separate processes against the same directory (`.agents/repo-guidance.md`
  Architectural Invariant 2 has them sharing one configuration database, and both can hold this
  module enabled), so two processes writing the same group is a real configuration, not a
  hypothetical. For a single atomic write that interleaving is survivable - last writer wins,
  with a coherent membership either way. For clear-then-fill it is not.

  **Use a system-wide named mutex (`Global\`) keyed on the target group's objectGUID**, taken
  around the clear, every add batch and the read-back, with the in-process semaphore retained
  inside it so same-process contention never reaches the mutex. The objectGUID, not the name, so
  a rename cannot split the lock.

  **A named mutex is not shareable by default and the details decide whether it works at all.**
  Dev and prod run under different app-pool identities, so a mutex created with default security
  by one is not openable by the other - the lock would silently become per-instance again, which
  is the exact failure it exists to prevent, and it would look like it was working. Specify:
  - **Security:** create with an explicit `MutexSecurity`, create-or-open, never create-only.
    **The principals are not known to the code and must not be named in it.** `deploy.ps1:11`
    accepts a single `ServiceAccount` for the pool being deployed, so no source file or script
    today knows both identities, and hardcoding either would violate Architectural Invariant 7.
    Grant by a rule rather than a list, and name the rule exactly so there is one reading:
    a single `MutexAccessRule` for the well-known **Authenticated Users** SID
    (`WellKnownSidType.AuthenticatedUserSid`, constructed via `SecurityIdentifier` so no account
    name is hardcoded and Invariant 7 holds), granting **`MutexRights.Synchronize |
    MutexRights.Modify`** and nothing else, `AccessControlType.Allow`, applied to this one named
    object at creation. No `FullControl`, which would let any holder rewrite the descriptor and
    lock the other instance out. Both pool identities are authenticated, so both satisfy it
    without either being named. A mutex confers ordering and no data access, so this grant
    reaches nothing beyond the ability to wait in line for this one group.
    **Settled, not open.** Two alternatives were considered and rejected. A new deploy parameter
    listing the permitted principals is a deploy-script change with its own approval, for no
    security gain over the rule above. A lease row in the shared configuration database would
    additionally cover the cross-host case, but it trades the operating system's
    abandoned-holder signal for a timeout that has to guess whether a holder died - and that
    signal is what tells this module a previous run left the list half-written. The mutex keeps
    it. Revisit only if the instances are ever split across hosts.
  - **Acquisition order, explicitly:** in-process semaphore first, then the global mutex, then
    the clear, the add batches and the read-back; release in reverse, each in a `finally`.
    Taking the mutex first would let one process hold a machine-wide lock while queueing on its
    own semaphore, blocking the other instance behind purely local contention.
  - **Timeout:** a bounded wait, not `WaitOne()` forever - a replace is a foreground operation
    behind a spinner. On timeout, **refuse before the clear** and tell the operator another
    replace of this list is in progress. Never proceed unlocked.
  - **Abandoned mutex:** a process dying mid-fill leaves the mutex abandoned and the list
    half-written. The next acquirer receives `AbandonedMutexException`, **owns the lock**, and
    must log that the previous run did not finish. It then proceeds normally: clear-then-fill is
    self-correcting, so the recovery is the operation itself. Swallowing that exception without
    logging would hide the only trace that a list was left incomplete.
  - **Release in a `finally`**, on the same thread that took it, or the next run inherits an
    abandoned lock for no reason.

  **Residual, stated for the owner to accept or reject rather than buried:** a named mutex is
  host-wide. If the two instances are ever split across hosts, they can still interleave and
  nothing short of a directory-side or database-side lease would prevent it. The read-back is
  what makes the damage visible rather than silent. Both instances are presently on one deploy
  host, so the mutex closes the case that actually exists today.
- **Retry each write operation on a transient directory failure.** The directory intermittently
  refuses writes with "A required audit event could not be generated for the operation" - three
  of nine large writes during measurement. Without a retry a replace fails for a reason that has
  nothing to do with the request, and at ten thousand members that means the operator re-uploads
  and waits again.

  **A retry is safe here only because both operations are idempotent**, which was measured:
  clearing an already-empty group succeeds, and `-Add` naming a member already present succeeds
  silently. A retried batch that had partly applied cannot double-apply. That property is what
  makes this a two-line change rather than a design problem, and it must be re-checked if the
  write shape ever changes.

  Constraints:
  - **Per operation, not per run.** Retry the individual clear or add batch, not the whole
    sequence - re-running the clear after batches have landed would wipe them.
  - **Bounded and reported.** Three attempts with a short backoff. Exhausting them is a real
    failure and reports as one; the attempt count goes in the log, never silently swallowed.
  - **Not a blanket catch, and the classifier is named rather than described.** Verified against
    the live assembly: `Microsoft.ActiveDirectory.Management.ADServerDownException` derives
    **directly from `System.Exception`, not from `ADException`**, so the two are cleanly
    separable by type. The observed audit fault surfaces as exactly `ADException`.
    - **Match on type NAME, never on the type itself.** `Microsoft.ActiveDirectory.Management`
      appears nowhere in this solution - not a `PackageReference`, not a `Reference`, not a
      `using` (verified 2026-09-28). The app reaches AD entirely through the PowerShell SDK and
      command strings. A `catch (ADException)` would require referencing an assembly that ships
      with RSAT rather than NuGet, which may not be present on the CI agent, so it would be both
      a build risk and an architectural departure. The classifier therefore takes the normalized
      exception's `GetType().FullName` and compares strings.
    - **Shape:** a pure `static bool IsRetryable(string exceptionTypeName)` plus a normalizer
      that walks to the innermost exception and returns its full type name. Both are testable
      with no directory and no RSAT present, which is the point.
    - **Retry:** `Microsoft.ActiveDirectory.Management.ADException` only.
    - **Never retry:** `...ADServerDownException` - that is the signature an oversized request
      produced during measurement, and retrying it would repeat a request the directory will
      refuse every time - nor `...ADIdentityNotFoundException`, `...ADInvalidOperationException`
      or `System.UnauthorizedAccessException`.
    - An unrecognised type name is **not** retried. An unknown fault is not evidence of a
      transient one, and the log line names the type so the list can be extended deliberately.
    - `ADException` is a broad base type and some permanent faults will also land there, so
      **every retry logs the exception message**. A non-transient fault being retried three times
      must be visible in the log rather than inferred, and that log line is what tells a future
      reader whether the classifier needs narrowing.
    - **Classify the unwrapped exception, not the one `Invoke()` throws.** A cmdlet failing under
      `-ErrorAction Stop` surfaces through `PowerShell.Invoke()` wrapped - the AD exception is the
      inner one, or reachable as the error record's exception. This repository already works
      around it at `Services/ADAttributeEditorService.cs:797`, which reads
      `ex.InnerException?.Message ?? ex.Message` for exactly this reason. A classifier matching
      on the outer type would match nothing and **every fault would be treated as
      non-retryable**, so the retry would silently never fire and nothing would look broken.
      Normalise first: walk `InnerException`, and fall back to
      `ps.Streams.Error.FirstOrDefault()?.Exception`, then classify.
    - Tests pin the classification per type for **both shapes** - a raw `ADException` and one
      wrapped in the cmdlet-invocation exception - and likewise for `ADServerDownException`, so
      the unwrap itself is guarded and not just the type list.
  - **The read-back still decides.** A retry that appears to succeed does not make the write
    successful; only the final membership comparison does.

  **Honest limit:** the fault repeated twice consecutively during measurement, so three attempts
  will not always clear it. This reduces the failure rate; it does not eliminate it. The
  underlying domain-controller audit condition is outside this module and worth fixing at source.

- **Primary-group members survive the clear.** Clearing the `member` attribute cannot remove
  somebody whose membership comes from their primary group
  (`GroupManagementService.cs:973-974`). The read-back therefore compares against the target plus
  any such members, and they are named in the result as unremovable. Without this the read-back
  could never match on a group that has one.

## The rule every step must satisfy

Chunking is pagination, never truncation. Every loop processes every chunk and aggregates every
outcome. Nowhere may impose an upper bound on the number of people handled.

One bounded guard exists and stays: `Comms10k.razor:234` reads the upload with
`OpenReadStream(maxAllowedSize: 16 * 1024 * 1024)`. At the measured average DN and a typical CSV
that is roughly half a million addresses - fifty times the target. It is a malformed-upload guard,
not a membership cap. Two obligations: it must fail with a message naming the size limit rather
than the raw stream error it produces today through the generic parse catch at `:263-266`, and the
call site needs a comment saying what it bounds.

## Decisions

**Protected principals are out of scope for this module.** Owner ruling, 2026-09-25:

> protected principals are out of scope here [...] protected principals are frequently added and
> removed from this group, and not doing so because of protection designed only to stop security
> incidents with C-suite execs makes this module detrimental rather than useful.

List membership is not a security boundary and grants access to nothing. Applied here the rules
produce only false positives and block the module's intended use.

**The exemption is conditional on the target being a DISTRIBUTION group, and the condition must
be enforced in code.** The justification above is true of a distribution group. It is **false of
a security group**, whose membership can appear on ACLs and in Kerberos tokens.

Nothing today constrains which kind is configured: `TargetGroupName` is a generic
`ConfigFieldType.AdGroup` field (`Modules/ModuleCatalog.cs:463`) and the service writes to
whatever name it holds (`Comms10kService.cs:174`, `:223`). Without a guard, pointing this module
at a security group would rewrite privileged membership in bulk **with no protected-principal
check at all** - a privilege-escalation path created by the exemption and not present before it.

**Guard, fail closed, at the group-resolution step of every write:** read the resolved group's
category and refuse unless it is Distribution. A security group, or a category that cannot be
read, refuses before the clear and says why. Not a configuration-time check - the configured
name can be repointed at a different object between runs, so it runs every time.

The refusal states the reason plainly: this module gives up the protected-principal safeguards on
the assumption its target is a mailing list, so it will not write to a security group. Wanting
that behaviour needs a different module or a new owner ruling, not a config change.

A test pins the guard and pins that it runs before any write.

**Directory scope is unchanged.** Today's resolve passes no `-Server` and binds to the host's own
domain. The batching pattern borrowed from `GroupManagementService` adds a global-catalog server,
which would make foreign-domain users start resolving. Not copied. Consistent with Architectural
Invariant 7: scope is whatever the host's membership makes it, never named or configured.

## Slices

Four commits, each with its own tests and guard proof.

### Slice 1 - Resolve addresses in batches -- LANDED 2026-09-30

Replace the per-row loop with one `-LDAPFilter` OR-query per batch, then match in memory - the
shape proven at `GroupManagementService.cs:852`, with the live query behind an `internal virtual`
test seam.

- **Batch size 500**, chosen for this workload rather than copied. `BulkIdentityList.ChunkSize = 50`
  exists for modules capped at 200 identities where it barely matters; here it decides whether
  ten thousand addresses cost 20 round trips or 200. One named constant; changing it affects speed
  only, never completeness.
- **Match keys unchanged:** `userPrincipalName` or `mail`, users only.
  `BulkIdentityList.BuildBatchFilter` also matches `sAMAccountName` and groups, so it is not
  reused; this module gets its own builder.
- **No `-Server`, no `ResultSetSize`** - the latter would truncate silently and break ambiguity
  detection.
- **RFC 4515 escaping** via `AdOwnershipFilter.EscapeLdapFilterValue`.
- **A batch that errors aborts the whole validation.** It must never read as "these 500 were not
  found": the write removes everyone absent from the resolved list, so a silently failed batch
  would drop 500 real people. Mirror `GroupManagementService.cs:885-892`.
- Outcomes unchanged: one match resolves, more than one is ambiguous, none is not found, a repeat
  of an already-resolved person is written once. Input order preserved.

### Slice 2 - Fix Preview and Download CSV

Replace `Get-ADGroupMember` with the raw `member` attribute read already used at `:206-218`, then
resolve display details for those DNs with a batched query keyed on `distinguishedName`. This is a
**second resolver**, not Slice 1's: different filter, different input, and a not-found must never
exclude anyone. Preview applies its display limit to the DN list before resolving details and
reports the full membership count; export resolves all of them. A DN that does not resolve falls
back to the leading CN rather than disappearing, matching today's behaviour.

The linked `member` attribute omits members whose membership comes from their primary group, which
`Get-ADGroupMember` included. This repository already fixed that once
(`GroupManagementService.cs:414-448`, pinned by `GroupMemberListingTests.cs:172-209`); the same
`(primaryGroupID=<rid>)` union applies here. It affects the listing only.

### Slice 3 - Add the distribution-group guard, THEN delete the protected-principal path

**Order within this slice is a security property, and the two halves must not be separated into
two commits.** Every slice here is independently deployable, so a commit that removes the
member check while the guard is still in Slice 4 leaves the module, for as long as that build is
live, with neither the protected-principal check nor the constraint that makes its absence safe
- pointing it at a security group in that window would rewrite privileged membership unguarded.
That is precisely the gap the exemption's condition exists to prevent, and the slice order as
first written created it.

So this slice lands, in one commit:

1. **The group resolution and the distribution-group guard** described in the write design -
   resolve the configured name to objectGUID and DN, refuse unless the category is Distribution,
   refuse if the category cannot be read. At this point the guard runs ahead of the existing
   protected-principal path; both are active, which is safe in the other direction.
2. **Then the deletion** below.

Slice 4 consumes the resolved identity this slice introduces rather than adding it.

Remove from `Comms10k.razor`: the per-member loop (`:319-358`), the servicing block (`:362-377`)
and the `extra:` argument it fed the audit call (`:389`), `ServicerModuleId` (`:172`), and both
service injects (`:15-16`). Remove `"Comms10k"` from `ModulesWithProtectedPrincipalServicing`
(`ModuleConfig.razor:693`), whose stated contract is that every entry has a real caller.

Keep authorization (`:307-313`), the ticket field, audit and the admin notification.

**Three documents state the rule and all three change here**, with a `.agents/decisions.md` entry:
`.agents/repo-guidance.md` Known Failure Class 3, `docs/ProjectConstitution.md`, and
`docs/AdminModuleDeveloperGuide.md:652-656`, the one a new module author reads.

**The amendment is a named, scoped, CONDITIONAL exception - not a softening of the rule.** The
Constitution's Protected Principals section (`docs/ProjectConstitution.md:86-87`) states the rule
with "no group-management or routine change carve-out", then carries exactly one exception in a
fixed shape: a bolded **Scoped exception (owner ruling DATE, `.agents/decisions.md`)** line naming
the one module, what is exempt, and why the boundary does not apply there. Follow that shape
exactly:

- Name Comms-10k specifically. No wording that any other module could read itself into.
- State that the exemption is from the MEMBER check, and say so explicitly - it is broader than
  the Self-Service Groups exception directly above it, which exempts only Protected Group
  Targets and keeps its member check. A reader must not conflate them.
- **Carry the distribution-only condition into the exception text itself, in all four places.**
  The condition is not a note about the implementation; it is what makes the exception sound.
  Every one of `docs/ProjectConstitution.md`, `.agents/repo-guidance.md`,
  `docs/AdminModuleDeveloperGuide.md` and the `.agents/decisions.md` entry must say the
  exemption applies **only while the module's target is a distribution group, enforced
  fail-closed at write time**.

  This is the difference between a correct record and a dangerous one. `TargetGroupName` is a
  generic `ConfigFieldType.AdGroup` field (`Modules/ModuleCatalog.cs:463`), so an unqualified
  "Comms-10k is exempt from the member check" is true of whatever group is configured, including
  a security group. The Constitution outranks this plan and outranks the code: if its text is
  broader than the guard, the text is what a future reader and a future reviewer will apply, and
  the guard will look like an over-implementation somebody may remove.
- Give the reason: the write target is a broadcast distribution list, membership of which grants
  access to nothing, so the check produces only false positives and blocks the module's intended
  use - and that reason is precisely why the condition belongs in the same sentence, because the
  reason fails the moment the target is not a distribution list.
- Leave every other clause in that section untouched, including fail-closed behaviour,
  transitivity, and the cloud-only-by-address rule.

The existing sentence "Never bypass protected-principal checks in privileged modules unless the
bypass is narrowly scoped, documented, and required for compensation cleanup"
(`ProjectConstitution.md:92`) is the test this amendment must satisfy: narrowly scoped and
documented. It does not fit "compensation cleanup", so that clause needs the owner ruling named
alongside it rather than being stretched to cover this.

`AuditCategoryFilingTests.cs:88-100` asserts six Comms-10k audit call sites; deleting the blocked
paths changes that count deliberately.

### Slice 4 - Rewrite the write as clear-then-fill with read-back

Replace the single `Set-ADGroup -Replace` (`Comms10kService.cs:220-228`) with clear, then batched
add, then read-back, per the write design above. **The group resolution and the
distribution-group guard already landed in Slice 3** - this slice consumes that resolved identity
for the lock key and every write, and does not reintroduce it. Testability:

- **Batching is pure**: given the resolved list, produce the batches. No directory access, so the
  10,001-member case is directly unit-testable.
- **`internal virtual` seams** for the clear, the batched add and the read-back, matching
  Slice 1's.
- **An explicit outcome, not a bool.** `Comms10kUpdateResult.Success` cannot express "the group
  is half written" or "completed but these could not be removed", and collapsing them is the
  success-aggregation failure Known Failure Class 2 names. Outcomes: succeeded, **could not
  confirm**, succeeded with
  exceptions (unremovable primary-group members), **partly applied - the list is incomplete and
  the same CSV must be re-run**, and refused before any change.

  **"Could not confirm" is a distinct outcome and not a shade of the others.** If the read-back
  itself fails after the clear or any add has landed, the module does not know what the
  membership is.

  **The outcome is DERIVED by one procedure, not assigned per failure site.** Rounds 18, 20 and
  21 each found a different corner of this taxonomy contradicting another, because outcomes were
  being decided at each error site. They are not. There is one rule:

  ```
  if a failure occurs before the clear is issued  -> refused before any change   (stop)
  otherwise, whatever happened, ATTEMPT THE READ-BACK:
      read-back fails                             -> could not confirm
      read-back == expected final set             -> succeeded
                                                     (with exceptions, if any member was
                                                      unremovable)
      read-back != expected final set             -> partly applied, reported FROM THE
                                                     OBSERVED MEMBERSHIP, never from which
                                                     batches were thought to have run
  ```

  Two consequences worth stating because they are what the earlier per-site rules kept getting
  wrong. A failed add batch is **not** automatically "partly applied" - if the read-back then
  also fails, the outcome is "could not confirm". And a failed add batch whose read-back matches
  the target **is a success**: the batch may have applied before the error surfaced, and the
  membership is what decides, not the error.

  **The dividing line for the first branch is whether the clear was attempted, not whether it
  reported failure.**
  Everything that refuses *before* the clear command is issued - lock timeout, resolution
  failure, the distribution-group guard, the post-lock re-read mismatch, an empty target list -
  is "refused before any change", and the membership is provably untouched. From the moment the
  clear is issued, a failure whose effect cannot be established is "could not confirm": a
  timeout or a dropped connection proves neither that it cleared nor that it did not. Only a
  refusal the directory returns *without applying anything*, which this module cannot reliably
  distinguish, would be the former - so anything after the clear is attempted defaults to "could
  not confirm".

  Reporting that as "partly applied" claims knowledge of what applied, which the module does not
  have. Reporting it as "refused before any change" claims the list is untouched, which may be
  false and is the more dangerous of the two, because it tells the operator to walk away from a
  list that might be empty. This repository has the lesson recorded from Cloud Password Reset:
  four of ten findings there were the same mistake, an unanswered question read as a negative
  answer.

  Its wording says what is and is not known: the write may or may not have completed, the
  membership could not be read back, and the operator must check the group directly before
  deciding. The audit row and notification carry the same, and re-running the CSV is safe and
  is the repair either way.
- The partly-applied message is the only one that demands operator action, so it says what state
  the list is in and that re-running the same file repairs it.
- **Primary-group members survive the clear** - `GroupManagementService.cs:973-974` records that a
  `member` write cannot evict them. The read-back compares against the target plus any such
  members, and they are named in the result as unremovable.

`README.md:214` documents "Atomic replacement via `Set-ADGroup -Replace` (full member swap in one
AD operation)". Both halves stop being true and it must change with this slice, stating the clear
-then-fill sequence and that the list is briefly incomplete during it.

`README.md:836` documents audit fields `membersAdded` and `membersRemoved` that the code has never
emitted. Clear-then-fill knows how many it wrote but not how many it removed, since it never reads
the prior membership. Record `membersWritten` and the observed final count, and correct the README
line rather than fabricating a removal count. (`README.md:212` claims a confirmation showing an
add/remove diff; that also cannot be produced without reading current membership first, so correct
it too - the confirmation states the number of members the list will be set to.)

## Tests

The module has no service tests today.

1. Batching covers every input exactly once, in order, at 0, 1, 499, 500, 501 and 10001.
2. The filter escapes the five RFC 4515 metacharacters; `$` survives as a literal.
3. The filter emits no `sAMAccountName` clause and no group clause.
4. The query passes no `-Server` and sets no `ResultSetSize`.
5. Matching: one match resolves, two are ambiguous, none is not found, a repeat is written once.
6. Order preserved across batch boundaries, including a repeat spanning one.
7. A batch error aborts the whole resolve and never yields not-found rows.
8. Preview and export read the `member` attribute, not `Get-ADGroupMember`; the reported total is
   the full count; the primary-group union is applied; an unresolvable DN still produces a row.
9. `Comms10k.razor` holds no protected-principal reference and `ModuleConfig.razor`'s servicing set
   does not contain `"Comms10k"` - the tripwire against a later sweep re-adding it.
10. Write batching: every resolved member lands in exactly one batch, in order, at 1,999 / 2,000 /
    2,001 and 10,001 members.
11. The clear is issued exactly once and before every add. Asserted on call order - a second
    clear partway through would wipe what was already written.
**Tests 12 to 15 all exercise the single outcome procedure in the write design. Write them
against that procedure, not against individual error sites - deciding outcomes per error site is
what produced three rounds of contradictions.**

12. The procedure runs as written: any failure before the clear short-circuits to
    refused-before-any-change with no read-back attempted; **every** post-clear path attempts
    the read-back, including one where an add batch threw.
13. Post-clear outcomes are derived from the read-back and nothing else:
    - read-back throws -> could not confirm;
    - read-back equals the expected final set -> succeeded, **even though an add batch
      reported failure** - the batch applied before the error surfaced and the membership is
      what decides;
    - read-back differs -> partly applied, and the counts reported come from the observed
      membership, not from which batches were believed to have run.
14. A failure before the clear is issued - lock timeout, resolution failure, the
    distribution-group guard, the post-lock re-read mismatch - reports refused-before-any-change
    and issues no adds; the membership is provably untouched. The dividing line is
    attempted-or-not, never the error text.
15. All five outcomes are distinguishable in the page message, the audit record and the
    notification, asserted per outcome, because the whole point is that they must not collapse
    into each other. `FinalCount` comes from the read-back in every outcome that has one, and is
    absent rather than guessed in could-not-confirm.
16. Oversized upload produces a message naming the limit, not a raw stream error.
17. The lock wraps the clear, every add batch and the read-back, is keyed on objectGUID rather
    than the group name, and is a `Global\` named mutex - not only the in-process semaphore. It
    is created with explicit security rather than defaults, a failure to acquire within the
    timeout refuses **before** the clear, and an abandoned mutex is logged and then proceeds.
18. The group is resolved to objectGUID and DN once before the lock, every write targets that
    resolved identity rather than the configured name, and a resolution failure refuses before
    the clear.
19. **The distribution-group guard.** A Distribution target proceeds; a Security target refuses
    before the clear, naming the reason; a category that cannot be read refuses the same way.
    The check runs on every replace, not once at configuration, and **again after the lock is
    taken** - a target that is Distribution at resolve time and not at re-read time refuses.
    This is the condition the protected-principal exemption rests on, so it is a security test,
    not a validation nicety.
20. Retry, classified by exception type NAME as a string - the classifier is a pure function
    over a type name and compiles with no reference to `Microsoft.ActiveDirectory.Management`,
    which this solution does not have; an unrecognised name is not retried. An `ADException` on
    one add batch retries that batch and the run completes;
    `ADServerDownException`, `ADIdentityNotFoundException`,
    `ADInvalidOperationException` and `UnauthorizedAccessException` each fail on the first
    attempt with no retry delay; three consecutive `ADException`s exhaust the retry and report a
    real failure carrying the attempt count. Every retry logs the exception message. A retry
    never re-issues the clear once any batch has landed.

Guard proof: revert each fix, confirm the matching test fails, restore, confirm green. Restoring by
copy keeps the old timestamp and the build skips it - touch the file afterwards.

## Verification

`dotnet build ExchangeAdminWeb.slnx -c Release`, `dotnet test ExchangeAdminWeb.slnx`,
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`, `git diff --check HEAD`,
ASCII scan. No PowerShell changes, so Pester and PSScriptAnalyzer are not required - say so rather
than claiming they ran.

## Acceptance (owner, on dev)

1. Preview and Download CSV stop throwing on the real group; the export row count matches the
   membership.
2. Validate the 5,279-row CSV; it completes.
3. Validate and replace a CSV of more than ten thousand addresses against `Test-WebApp`, then
   confirm the count in ADUC or PowerShell rather than from the page. The directory work behind
   this is already proven; this checks the module drives it correctly.
4. Re-run the same CSV. It completes and the membership is unchanged.
5. A small control file with a known-good address, a repeat, an unknown address and, if one can be
   contrived, an address matching two objects. All four outcomes read as they do today.
6. A CSV containing a protected principal completes rather than being refused.
7. The real list last.

**Time the ten-thousand run.** The directory work measures at 7.9s; if the module takes
dramatically longer, the batching is not doing what this plan says and that is worth knowing
before the real list.

## Risks

- **A failed resolve batch read as "not found" would remove those people.** Slice 1's abort rule
  and test 7 exist for this alone.
- **The list is empty or partial for about eight seconds during every replace**, and a failure in
  that window leaves it incomplete until someone re-runs the file. This is the real cost of the
  design and the one an operator can be bitten by. It is forced by the directory - no single
  operation exists at this size - and the alternative that avoids it was measured seven times
  slower on every run. Mitigated by exact reporting and by re-running being safe, not eliminated.
- **The intermittent audit-event refusal** will fail writes occasionally at any size. The module
  must surface it. Its cause is outside this work.
- Removing the protected-principal check means a replace can add or remove a protected principal.
  That is the intended effect of the ruling.

## Review

Reviewer throughout: openreview codex, `@azure-openai-eus2-global/gpt-5.5-dzs` at xhigh,
fallback grade, codex-cli 0.154.0. The resolved model id does not appear in the invocation
envelope; the dispatched pair is recorded instead.

**Rounds 1 to 12 reviewed designs that no longer exist** - revisions 1 to 12, which assumed an
inferred write limit, and whose convergence at round 12 was on the add-then-remove shape. That
verdict does not apply to this plan and is retained only in the retrospective below.

**Rounds 13 to 21 reviewed the current design across nine rounds and produced eighteen material
changes, every one admitted and folded in.** The ones that mattered, because they were defects
that would have shipped:

- **Round 16** - the protected-principal exemption opened a privilege-escalation path. Its
  justification holds only for a distribution group, and `TargetGroupName` accepts any group;
  pointing the module at a security group would have rewritten privileged membership unguarded.
- **Round 19** - the slice order created that same gap for real: the check was deleted in one
  commit and the guard added in the next, and every slice here is independently deployable.
- **Round 20** - the retry classifier would not have compiled. `Microsoft.ActiveDirectory.Management`
  is referenced nowhere in this solution, so catching its exception types needed an RSAT
  assembly the CI agent may not have.
- **Round 14** - the cross-process mutex would silently not have been shared, because a named
  mutex with default security is not openable across app-pool identities; and the retry would
  never have fired, because PowerShell wraps the exception and the classifier read the wrapper.
- **Rounds 18, 20 and 21** - three separate corners of the outcome taxonomy contradicting each
  other, because outcomes were being decided per error site. Round 21 replaced them with one
  derivation procedure.

**Round 22, over the current head: "best approach - no material changes are needed", ready to
implement as written.** It named one implementation constraint to preserve: Slice 3's internal
order, guard first and deletion second, in one commit.

**The loop is converged.** What remains is implementation.

## What the earlier revisions got wrong

Recorded because the failures were in reasoning, not code.

**Revisions 7 to 12** replaced the atomic write with a chunked delta and added a lock, a
stale-confirmation check, a delta planner and a five-way result to contain the failure modes that
introduced. None of it rested on an observation. The chain was: a documented ADWS cap of 5,000 on
`Get-ADGroupMember`, which is a read, generalised without justification into a write cap, combined
with a recalled 1 MB request limit that was never verified. The one real data point - 6,840 members
written in one operation - was evidence the write worked at that size and was cited as a ceiling.
Six review rounds did not challenge it, because the plan asserted it as a defect rather than posing
it as a question.

**Revision 13** corrected the overreach by declaring the write sound and out of scope. That was the
same error inverted: another confident claim with no measurement behind it. It was wrong.

**Revision 14** chose add-then-remove on a 3.8s measurement taken from a change where 11,000 of
12,000 members were unchanged - the best case, presented as the number. On a full swap with no
overlap the same approach measures 53.2s, because it peaks at 20,002 members and write cost rises
steeply with group size. Clear-then-fill does that swap in 7.9s. The safety argument for
add-first - never leaving the list incomplete - was real but bought an eight-second window at 7x
the cost of every run.

**What settled it** was measurement against a test group: a size ladder, a raw-LDAP check to locate
the limit in the directory rather than the transport, an idempotency probe, and timed runs of all
three orderings at full swap. Every number in this plan came from that, and it produced findings
nobody predicted - that write cost scales with the destination group's size rather than the batch's,
which is the fact the whole design now rests on, and the intermittent audit-event refusal.

Two lessons worth keeping. The measurement cost less than any one of the six review rounds spent
arguing about the guess. And a benchmark of the easy case is not a benchmark: the 3.8s figure was
honestly obtained, correctly reported, and still wrong to design on, because nothing established
that a mostly-unchanged list was the case worth sizing against.

## Version and commits

- `Modules/ModuleCatalog.cs`: `Comms10k` `1.2.0` -> `1.3.0`.
- `ExchangeAdminWeb.csproj` untouched; verify by diff.
- One commit per slice, one line each in `.agents/token-log.md`, `.agents/state.md` updated as they
  land.
