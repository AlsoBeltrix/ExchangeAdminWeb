# Comms-10k At Full Size - Plan

Status: **APPROVED by the owner, 2026-09-28.** Revision 14, no code written yet. The final review
(round 12, codex openreview) returned "Best approach: no material changes are needed - the plan is
ready as a design", and no owner question is outstanding.
Module: `Comms10k` (`1.2.0` -> `1.3.0`). Base app `2.23.0` unchanged.
Scope: `Services/Comms10kService.cs`, `Components/Pages/Comms10k.razor`,
`Components/Pages/ModuleConfig.razor` (one list entry), `Modules/ModuleCatalog.cs` (version bump),
`README.md`, one new pure helper, new tests, and the governance records named below.

Revision 14 replaces guesswork with measurement. Every claim about directory limits below was
measured on 2026-09-25 against `CN=Test-WebApp,OU=ZZMikeCTest,DC=ad,DC=analog,DC=com` (a
Distribution, Universal group the owner supplied for the purpose) and the numbers are recorded in
"Measured facts". Revisions 7 to 12 designed around an inferred limit; revision 13 asserted the
write was fine. Both were wrong, in opposite directions.

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

**Add and Remove are both idempotent**, which removes most of the complexity a chunked write would
otherwise need:

- `Set-ADGroup -Add` naming a member already present: silently succeeds.
- `Set-ADGroup -Remove` naming a member not present: silently succeeds.

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

## The write: add first, then remove

Since the directory forbids a single operation, the only question is which order leaves the list
safest while the change is in flight. **Measured, on a 12,000-member target with 1,000 additions
and 1,000 removals:**

| Approach | Time | State of the list during the operation |
| --- | --- | --- |
| Clear, then fill in chunks | 17.4s | **empty, then a growing subset** |
| Add all, then remove stale | **3.8s** | **always a superset - nobody is ever missing** |

Both end at exactly the right membership; the add-then-remove run was verified member-for-member
against the target, not just by count.

Add-first wins on both counts, and the safety one is the reason to choose it. For a broadcast
list, the two failure directions are not equal: somebody missing from it does not receive company
communications, while somebody briefly extra receives one message they should not. Clear-then-fill
makes the list a strict subset for the entire operation and leaves it broken if it dies partway.
Add-then-remove never drops anyone who belongs there, and a failure partway leaves the list
over-inclusive and complete rather than under-inclusive.

It is also four and a half times faster, because members already present cost nothing.

The algorithm, in full:

1. **Read the current membership** - one query, 0.2s at 12,000.
2. **Add every resolved CSV member**, in chunks of 2,000. No comparison needed: adding an existing
   member is a silent no-op, so the whole target list is simply added. The list is now a superset
   of both the old and the new membership.
3. **Remove the members that were present and are not in the CSV** - a plain filter of step 1's
   result against the target, in chunks of 2,000.
4. **Read back and compare with the target.** Success is reported only on a match, and the
   reported final count comes from this read.

Because both operations are idempotent, re-running the same CSV is safe and costs almost nothing -
which is also the recovery path if a run fails partway.

**Chunk size is 2,000**, one named constant, with a comment recording that the measured
single-operation ceiling is 10,000 and this sits well under it. It bounds a request; it is not a
limit on membership.

**What this gives up, stated plainly:** atomicity. The current single operation either lands or
does not. This cannot, and neither can anything else at this size, because the directory refuses
it. A partial application is reported exactly - how many were added, how many removed, what the
membership now is - and the audit record carries the observed outcome, not the intent.

**Serialise the sequence per group.** Two operators replacing the same list concurrently could
interleave their chunks. `SelfServiceGroupService.cs:46,523` already does this: a
`SemaphoreSlim(1, 1)` from a `ConcurrentDictionary` keyed on the group's objectGUID. The lock is
per process, so two application instances could still interleave; the read-back is what makes that
visible. That limit is true of every membership write in the app and is recorded, not solved here.

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

**Directory scope is unchanged.** Today's resolve passes no `-Server` and binds to the host's own
domain. The batching pattern borrowed from `GroupManagementService` adds a global-catalog server,
which would make foreign-domain users start resolving. Not copied. Consistent with Architectural
Invariant 7: scope is whatever the host's membership makes it, never named or configured.

## Slices

Four commits, each with its own tests and guard proof.

### Slice 1 - Resolve addresses in batches

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

### Slice 3 - Delete the protected-principal path

Remove from `Comms10k.razor`: the per-member loop (`:319-358`), the servicing block (`:362-377`)
and the `extra:` argument it fed the audit call (`:389`), `ServicerModuleId` (`:172`), and both
service injects (`:15-16`). Remove `"Comms10k"` from `ModulesWithProtectedPrincipalServicing`
(`ModuleConfig.razor:693`), whose stated contract is that every entry has a real caller.

Keep authorization (`:307-313`), the ticket field, audit and the admin notification.

**Three documents state the rule and all three change here**, with a `.agents/decisions.md` entry:
`.agents/repo-guidance.md` Known Failure Class 3, `docs/ProjectConstitution.md`, and
`docs/AdminModuleDeveloperGuide.md:652-656`, the one a new module author reads.
`AuditCategoryFilingTests.cs:88-100` asserts six Comms-10k audit call sites; deleting the blocked
paths changes that count deliberately.

### Slice 4 - Rewrite the write as add-then-remove with read-back

Implement the algorithm above. Testability is part of it:

- **A pure planner**: given the current membership and the target, return the add list, the remove
  list and the chunk boundaries. No directory access, so the 10,001-member chunking and the
  removal filter are directly unit-testable.
- **`internal virtual` seams** for the membership read and the chunked write, matching Slice 1's.
- **An explicit outcome, not a bool.** `Comms10kUpdateResult.Success` cannot express "applied
  partly" or "completed but these could not be removed", and collapsing them is the
  success-aggregation failure Known Failure Class 2 names. Outcomes: nothing to do, succeeded,
  succeeded with exceptions, partly applied, refused before any change.
- **Primary-group members cannot be removed** by a `member` write - `GroupManagementService.cs:973-974`
  records it. They are excluded from the removal list, added to the expected final set so the
  read-back can match, and named in the result as unremovable. Without this the read-back could
  never match on a group containing one.

`README.md:214` documents "Atomic replacement via `Set-ADGroup -Replace` (full member swap in one
AD operation)" and must change with this slice. Two other README lines are already wrong today and
become right: `:212` claims a confirmation showing an add/remove diff, and `:836` documents audit
fields `membersAdded` and `membersRemoved` that the code has never emitted. Emit them from the
observed counts.

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
10. Planner: add list is the whole target, remove list is current-minus-target less primary-group
    members, chunk boundaries at 1,999 / 2,000 / 2,001 and 12,000.
11. Adds are issued before removes. Asserted on call order, because the ordering is the safety
    property and nothing else enforces it.
12. Success requires the read-back to equal the expected final set - target plus unremovable
    primary-group members - and the reported count comes from that read, not the input.
13. A chunk failure stops further chunks and reports partly-applied with the counts that actually
    applied; the audit records observed, not intended.
14. The five outcomes are distinguishable in the page message, the audit record and the
    notification.
15. Oversized upload produces a message naming the limit, not a raw stream error.
16. The per-group lock wraps read, adds, removes and read-back, keyed on objectGUID.

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
4. Re-run the same CSV. It reports nothing to do and writes nothing.
5. A small control file with a known-good address, a repeat, an unknown address and, if one can be
   contrived, an address matching two objects. All four outcomes read as they do today.
6. A CSV containing a protected principal completes rather than being refused.
7. The real list last.

## Risks

- **A failed resolve batch read as "not found" would remove those people.** Slice 1's abort rule
  and test 7 exist for this alone.
- **Partial application is possible** where the single write was atomic. Forced by the directory,
  not chosen. Mitigated by add-before-remove, which keeps the list over-inclusive rather than
  under-inclusive, by exact reporting, and by re-running being safe and cheap.
- **The intermittent audit-event refusal** will fail writes occasionally at any size. The module
  must surface it. Its cause is outside this work.
- Removing the protected-principal check means a replace can add or remove a protected principal.
  That is the intended effect of the ruling.

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

**What settled it** was twenty minutes against a test group: a size ladder, a raw-LDAP check to
locate the limit in the directory rather than the transport, an idempotency probe, and a timed
comparison of the two orderings. Every number in this plan came from that, and it also produced two
findings nobody had predicted - that Add and Remove are idempotent, which removes the need for a
diff on the add side, and the intermittent audit-event refusal.

The lesson worth keeping: the measurement cost less than any one of the six review rounds spent
arguing about the guess.

## Version and commits

- `Modules/ModuleCatalog.cs`: `Comms10k` `1.2.0` -> `1.3.0`.
- `ExchangeAdminWeb.csproj` untouched; verify by diff.
- One commit per slice, one line each in `.agents/token-log.md`, `.agents/state.md` updated as they
  land.
