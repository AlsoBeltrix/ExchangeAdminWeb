# aud-a73: A cloud permission write whose outcome is unknown is filed as Failed, so the event log's Undetermined filter cannot find it

**Severity**: MEDIUM - audit result bucket misleading for privileged-access reconciliation. The aud-a71 row
for a write that threw at `PermissionChangeFailure.TheWrite` carries the banner "Whether the change was made
could not be confirmed..." but is written with `success: false`, so `AuditService.LogMailboxPermission` /
`LogCalendarPermission` file it as `result: "Failed"`; the Admin Event Log's Undetermined filter (aud-a28z
part 5) does not find it, and a reviewer reconciling Exchange may treat a possibly-applied change as a
failed one.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); in progress
**Branch**: `worktree-agent-a22f2baf02d6abcdd` (from master `066ea1a2`)
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-8, 2026-10-23, candidate 1)

## Evidence

At `c78de758`: `Components/Pages/MailboxPermissions.razor` around 529 and `Components/Pages/CalendarPermissions.razor`
around 506 (the catch passing `success: false`); `Services/AuditService.cs` around 71 (`LogMailboxPermission`'s
result from the bool) and `LogCalendarPermission`; `LogMigrationCheck`'s three-way result (aud-a28z part 5)
and the event log's Result filter.

## Approach (to be written by the implementing agent before code)

- The two permission audit methods accept an explicit result (`Success`, `Failed`, `Undetermined`) beside
  the bool they take today (an overload or an optional parameter, so no other caller changes); the pages
  pass `Undetermined` for the write step's "could not be confirmed" row and `Failed` for the steps before
  and after the write; the banner text, written identity, servicer note and error detail stay.
- Tests through `PermissionPagesSelfGrantTests` (the write-step row's result is Undetermined; a
  before-the-write row is Failed) and the event log filter test gains the permission row; mutation at the
  call site: `success: false` again at the write step.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21), within the 2026-10-23 stopping rule (MEDIUM). Branch
`worktree-agent-a22f2baf02d6abcdd` from master `066ea1a2`: this record alone, then one fix commit
`fix(permission-pages,audit): ... (aud-a73)`; the pages edited in place (808 and 762 lines, no registry entry moved);
comments with the fix, or after the mutation round in a comments-only commit. Behaviour: the two pages' `SubmitSingle`
catch (both module versions) and the shared audit service, a new shared result type and `PermissionChangeFailure` (base
app version); the bumps are the supervisor's.

Evidence re-read at `066ea1a2`:
- `Components/Pages/MailboxPermissions.razor` 528-529 and `Components/Pages/CalendarPermissions.razor` 505-506: the catch
  writes the change's row through `SelfGrantRefusalRecord.Banner(failure, () => Audit.Log...(..., false, ticket,
  errorDetail: failure, extra: ...), Logger, target, FailedSingleChangeRecord)` at every step; at
  `PermissionChangeFailure.TheWrite` the banner, and so the row's error, is "Whether the change was made could not be
  confirmed: ...".
- `Services/AuditService.cs` `LogMailboxPermission` (51-83) and `LogCalendarPermission` (86-116) file
  `["result"] = success ? "Success" : "Failed"` and `["error"] = success ? null : errorDetail`. `extra`, merged last,
  could already put any text under "result"; that is a free-text override of a computed field at two call sites, not the
  explicit result the Approach names.
- `LogMigrationCheck` (118-146) files a three-way result since aud-a28z part 5; the Admin Event Log's Result filter
  offers Undetermined (`AdminEventLog.razor` 96) and matches a row's result exactly (996), and its badge shows any result
  but Success and Failed as a warning (284). Nothing to change there.
- The other readers of a row's result: `ADAttributeEditorUndoService.CanUndo` (its own category only), the writer's
  dropped-row report (the text), and `WriteAuditEvent`'s `IsSuccessfulAuditResult` - any result but "Failed" is a success
  for the implicit trace operation's completion and for the usage count.
- `AuditWriterThrowFreeTests` pins 13 public `AuditService` methods (15 taking a dictionary) and builds an argument for
  each parameter type it knows; `AuditExtraChannelTests` reads a method by the first match of its signature, so an
  overload of the same name would be read in its place. Hence an optional parameter, not an overload.

1. **An explicit result.** New `Services/AuditResult.cs`: `public enum AuditResult { Success, Failed, Undetermined }`, each
   name the text a row carries. `LogMailboxPermission` and `LogCalendarPermission` take a last optional
   `AuditResult? filedAs = null`: the row's result is `filedAs` when given, else Success or Failed from `success` as now
   (one private helper beside `IsSuccessfulAuditResult`), and its error is kept unless the row is filed Success. Every
   caller that gives no `filedAs` - all but the two below - writes the row it wrote before.
2. **The step says the result.** `PermissionChangeFailure.AuditResultOf(step)`: Undetermined at `TheWrite`, whose banner
   says whether the change was made could not be confirmed; Failed before the write (nothing was written) and after it
   (the written change has its own row; this one says a later step failed).
3. **The pages pass it.** In place, inside the catch's audit call on its own line (Mailbox 529, Calendar 506):
   `filedAs: PermissionChangeFailure.AuditResultOf(step)`. `success: false`, the banner as the error, the written identity
   and the servicer note in `extra`, and the record's guard stay as they are.

Not in scope, both candidates in the hand-back: `ExecuteOnPrem`'s catch (aud-a59; Mailbox 744-747, Calendar 687-690)
files its own write-step throw the same way, Failed under the "could not be confirmed" banner - this record names
`SubmitSingle`'s catch, whose written identity and servicer note the Approach keeps. And `IsSuccessfulAuditResult` stays:
an Undetermined row counts as a non-failure in the usage count and completes the audit's implicit trace operation as
succeeded, as an undetermined eligibility check has since aud-a28z part 5, where the Failed row counted as a failure.

Tests:
- `PermissionPagesSelfGrantTests`. aud-a71's `ACloudWriteThatThrows_IsRecordedAsFailed_WithTheBannersText` becomes
  `ACloudWriteThatThrows_IsRecordedAsUndetermined_WithTheBannersText`: the one row's result Undetermined, the rest as it
  was (each page). New `ACloudChangeThatThrowsBeforeTheWrite_IsRecordedAsFailed` (each page): a target Exchange Online
  does not hold (a test pool whose Get-Mailbox answers ObjectNotFound, as Exchange does) and on-premises Exchange does (the
  harness's stand-in through the page service's on-premises seam, a server configured), whose on-premises permission
  check throws - the one step before the write that can throw: the before-the-write banner, one Failed row with that text,
  nothing written, nothing staged for confirmation. aud-a53's `AThrowAfterTheWrite_IsNamedAsSuch_...` keeps its Failed row
  for the step after the write. New `TheStepsAuditResult_IsUndetermined_OnlyAtTheWrite` (each stage).
- `AuditServiceTests`: each method given `filedAs: Undetermined` files that result and keeps its error, whatever its bool.
- `EventLogSectionsTargetTests`: each method's Undetermined row, parsed by the page, is found by its Result filter under
  Undetermined and not under Failed.
- `AuditWriterThrowFreeTests`: the argument builder knows the new parameter type (it passes Undetermined), so the
  tripwire calls the two methods with it; its counts stand.

Mutations at the call site (md5, mutate, rebuild, filtered tests, failing names, restore from git, touch, rebuild, md5
equal, green): M1 `success: false` again at the write step - the Mailbox Permissions catch's `filedAs` argument removed;
M1c the same on Calendar Permissions. Extras: E1 `AuditResultOf` Undetermined at every step; E2 `LogMailboxPermission`
files from its bool alone (`filedAs` ignored); E3 its error dropped by the bool again (`success ? null : errorDetail`).

Files: `Services/AuditResult.cs` (new), `Services/AuditService.cs`, `Services/PermissionChangeFailure.cs`,
`Components/Pages/MailboxPermissions.razor`, `Components/Pages/CalendarPermissions.razor` (in place),
`ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`, `ExchangeAdminWeb.Tests/AuditServiceTests.cs`,
`ExchangeAdminWeb.Tests/EventLogSectionsTargetTests.cs`, `ExchangeAdminWeb.Tests/AuditWriterThrowFreeTests.cs`, this
record.

Caller-visible change, by design: a single permission change whose write threw is filed with result "Undetermined", its
error still the "could not be confirmed" banner, shown with the warning badge and found by the Admin Event Log's
Undetermined filter; a throw before or after the write is filed Failed as before.

## Guard proof

Pending.
