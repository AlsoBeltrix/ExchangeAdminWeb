# aud-a25: a group's manager was never told its membership changed

**Severity**: MEDIUM. Nothing is mis-mutated and everything is audited; what was missing is
that the person accountable for a security group's membership found out only by looking.
**Status**: Verified, fixed, independently reviewed, four review findings remediated
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 25 of the 2026-10-16 audit, verified against the code.
`Components/Pages/GroupManagement.razor` audited every membership change and emailed
`Email:AdminNotificationEmail`. Nothing reached the group's own manager.

**The owner ruled this one personally on 2026-10-19, and the ruling is narrower than the
audit's**:

> "it ABSOLUTELY needs to email users when mailbox or calendar permissions are changed. NOT
> for group membership changes. Email the manager of the group with a summary of the
> changes, but do not email every user in the group."

So this finding is the near-opposite of `aud-a24`, which made per-affected-user email the
right answer for mailbox and calendar permissions. Here fan-out is refused.

## Predicted observable failure

Someone is added to a security group that grants access to a share. The audit log records
it; the administrators get mail. The manager who owns that group - the person who would
recognise the name as someone who should not have it - is told nothing, and finds out at
the next access review, if there is one.

## Approach

**Resolution in the service, dispatch in the page.** `GroupInfo` carries `ManagedBy` (a DN,
read with the existing group search at no extra round trip) and
`GroupManagementService.GetGroupManagerAsync` turns it into a mailbox on demand. The page
owns `EmailService` and `AuditService` already, so no new service dependencies were needed.

**One message per operation.** The per-row helpers already take a `sendAdminEmail` flag that
is false for batch rows; it doubles as the "standalone operation" gate, and the batch paths
send one summary after their loop.

**No manager means no recipient.** Recorded in the audit as `<action>_ManagerNotNotified`
with the reason. No fallback to the admin address, no fallback to the members.

## Independent review - `codex` (`@azure-openai-eus2-global/gpt-5.5-dzs`, xhigh, read-only)

Dispatched 2026-10-20 against the uncommitted working tree. Prompt kept at
`.agents/review/prompt-a25.md`. **It returned four product findings, all of them real, all
verified against the code before remediation.**

| # | Severity | What it found |
| --- | --- | --- |
| R1 | High | The manager notice was gated on `result.Success`, but `AddMemberAsync`/`RemoveMemberAsync` return **Ok** for idempotent no-ops ("already a member", "is not a member"). Adding 50 people who were already in a group mailed the manager "50 of 50 applied" having changed nothing. |
| R2 | High | `GetGroupManagerAsync` accepted any object carrying a `mail` attribute, without checking `objectClass`. A `managedBy` naming a **mail-enabled group - including the managed group itself** - would have sent the notice to the group address, delivering to every member. That is the precise outcome the owner refused. |
| R3 | Medium | The lookup did not route with `ServerFromDn(managedBy)`, unlike every other `Get-ADObject` in the same service. A forest search returns groups from several domains, so an out-of-domain manager resolved to null - indistinguishable from "no manager", and audited as such. |
| R4 | Medium | `summary.MemberLines` contains **Not done** rows alongside successful ones, and the notice was sent with `success: true`. A 3-of-50 partial removal produced a SUCCESS-framed email with failed rows inside a field labelled as what changed. |

**R2 is the one that matters most**: a correct-looking implementation that would have
violated the explicit ruling in production, through a directory configuration nobody here
controls.

It also confirmed two claims of mine - the ClickGate line-key reasoning (registered controls
at 35-307, all edits below) and that `AddParameter("Identity", ...)` is injection-safe - and
named three of my guards as passing against broken code.

### Remediation

- **R1**: `PermissionResult.NoChange(...)` and a `Changed` flag (default true, so no existing
  caller changes meaning); the two idempotent returns use it; every gate is
  `Success && Changed`. `BulkRowOutcome.Changed` and `Summary.ChangedCount` carry it through
  batches.
- **R2**: `objectClass` is requested and must contain `user`, checked **before** the address
  is read. Anything else logs and returns null - no recipient.
- **R3**: `ServerFromDn(managedBy)` routes the lookup.
- **R4**: the notice uses `ChangedCount`/`ChangedLines` and is framed with `summary.Success`.

### The three test gaps it named, all closed

| Gap | Fix |
| --- | --- |
| The batch guard counted *gates*, so an extra ungated call elsewhere would pass | Call sites are now **counted**: exactly 4 |
| The audit guard checked both strings existed *somewhere* in the method, so a branch returning before the audit would pass | Now asserts the audit happens **before** the `return` inside the no-manager branch |
| The lookup guard accepted the cross-domain and object-class defects | Now pins `ServerFromDn` and the class check's position |

## Files changed

- `Services/GroupManagementService.cs` - `ManagedBy`, `GetGroupManagerAsync`, `NoChange` at
  the two idempotent returns
- `Models/PermissionResult.cs` - `Changed`, `NoChange`, carried through `WithServicedNote`
- `Services/BulkIdentityList.cs` - `BulkRowOutcome.Changed`, `Summary.ChangedCount`/`ChangedLines`
- `Components/Pages/GroupManagement.razor` - `NotifyGroupManagerAsync` and four call sites
- `ExchangeAdminWeb.Tests/GroupManagerNotificationTests.cs`
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - `ExpectedLineCount` 1050 -> 1139. No
  line-keyed entry moved; every registered control is at 35-307 and all edits are below 585.
  Confirmed by the ClickGate suite and by the reviewer independently.
- `Modules/ModuleCatalog.cs` - GroupManagement 2.11.2 -> 2.12.0
- `ExchangeAdminWeb.csproj` - 2.32.0 -> 2.33.0. `PermissionResult` and `BulkIdentityList`
  are shared.

## Guard proof

Source scans, declared as such: both halves need a live directory - the lookup runs
`Get-ADObject` against the forest, the page method needs a rendered circuit.

12 tests. The ones that carry the ruling: `TheMembersOfAGroupAreNeverEmailed` (no
`SendUserNotificationAsync`/`SendOwnerNotificationAsync` anywhere on the page),
`ABatchSendsOneSummary_NotOneMessagePerRow`, `AMailEnabledGroupIsNeverAcceptedAsTheManager`,
`AnIdempotentNoOpIsNotAnnouncedAsAChange`,
`ABatchNoticeCountsOnlyWhatChanged_AndDoesNotMixInFailures`.

| Mutation | Result |
| --- | --- |
| email the members as well | FAIL - `TheMembersOfAGroupAreNeverEmailed` |
| drop the `sendAdminEmail` gate from one per-row site | FAIL - the batch guard (after it was tightened; it passed before, caught by this mutation) |
| fall back to the admin address when there is no manager | FAIL - `NothingIsSentWhenThereIsNoManager` |
| stop recording an undelivered summary | FAIL - `AnUndeliveredSummaryIsAlsoRecorded` |
| make the lookup throw instead of failing soft | FAIL - `TheManagerLookupFailsSoftAndSaysWhichWay` |

Sources restored byte-identical (md5) after each.

Gate: Release 0 errors; suite **4444 passed / 3 known AD skips / 0 failed**; format and
`git diff --check` clean; ClickGate 316/316.

## Known gaps

- **Nothing reached a directory or an SMTP server.** Every assertion is source text. That
  `objectClass` for a user contains `"user"`, and that `ServerFromDn` returns a routable
  server for a foreign-domain DN, are read from the surrounding code's own usage.
- **A contact is refused as a manager.** Requiring `user` rejects a mail-enabled contact,
  which some directories legitimately use for `managedBy`. It fails closed to "no
  recipient" and audits, which is the safe direction, but it is a real narrowing.
- **`msExchCoManagedByLink` co-owners are ignored** - deliberately; the ruling says manager,
  singular. A test pins that so widening it is a decision someone makes, not a drift.
- **`Changed` defaults to true**, so a future no-op that forgets `NoChange()` is announced
  as a change. The alternative was auditing every `PermissionResult.Ok()` in the app.
- **Only the two idempotent returns were converted.** Other paths in other services may
  return success without changing anything; not surveyed.

## Closeout

Fixed and reviewed on `master`. This is the first finding in the stream to get its
independent review before landing rather than after - and it found two High defects, one of
which would have broken the owner's explicit instruction in production. The backlog of
earlier findings reviewed by nobody is the open risk; `codex` is working and clearing it is
the next work.
