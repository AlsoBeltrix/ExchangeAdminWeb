# aud-a76: A permission write that fails before the permission command was submitted is filed Undetermined, as if its outcome were unknown

**Severity**: MEDIUM - audit semantics and operator triage. Since aud-a73/a75 the pages set the step to
`TheWrite` before calling the service, so a failure that happens BEFORE the permission cmdlet runs - the
cloud pool refusing the borrow (Exchange Online disabled, config unreadable, pool busy or stopped, the
connect failing) or the on-premises `ConnectOnPrem` failing before any `Invoke-Command` - is filed
`Undetermined` with "Whether the change was made could not be confirmed", though nothing was submitted and
nothing could have applied. The Admin Event Log then hides a definite no-write failure from the Failed
filter and sends the operator into needless reconciliation. The aud-a75 test even asserts no
`Invoke-Command` while expecting Undetermined.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); in progress
**Branch**: `aud-a76-agent-a02bf3417cec8f3d3` (from master `a1eb8a3a`)
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-9, 2026-10-23)

## Evidence

At `8c6b908c`: `Components/Pages/MailboxPermissions.razor` 486 and 706, `Components/Pages/CalendarPermissions.razor`
459 and 659 (the step set to `TheWrite` before the service call); `Services/MailboxPermissionService.cs` and
`Services/CalendarPermissionService.cs` (the borrow / `ConnectOnPrem` before the cmdlet; `TryRight` /
`TryRightOnPrem`); `ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs` (`AnOnPremWriteThatThrows_...`
asserting no `Invoke-Command` with Undetermined).

## Approach (to be written by the implementing agent before code)

- The services distinguish a pre-command failure from a submitted-write failure: a borrow or connect that
  fails before the permission cmdlet is reached throws (or returns) a typed pre-write failure
  (`PermissionWriteNotSubmittedException` or a result flag), and the pages file it `Failed` with "Nothing
  was written: <cause>"; `Undetermined` is reserved for a failure after the cmdlet was submitted and the
  outcome is unknown. Where the service already returns a failed result for a refused right
  (`TryRight`/`TryRightOnPrem`), nothing changes.
- Tests: Exchange Online disabled, config unreadable, pool busy, on-prem connect failing - each Failed with
  "Nothing was written" on both pages; a throw from the cmdlet itself still Undetermined. The aud-a75
  on-prem test is retargeted to a cmdlet-level throw for its Undetermined case. Mutation at the call site:
  the pre-write failure filed through `AuditResultOf(TheWrite)` again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21), within the 2026-10-23 stopping rule (MEDIUM). Branch
`aud-a76-agent-a02bf3417cec8f3d3` from master `a1eb8a3a` (the agent's worktree was cut at `1c0e9b6b`, an ancestor with no
commits of its own; the branch was made fresh at the master tip and the worktree branch left where it was): this record
alone, then one fix commit `fix(permission-services,permission-pages): ... (aud-a76)` with the guard proof, comments with the
fix (or a comments-only commit after the mutation round). The pages edited in place (808 and 762 lines, no registry entry
moved). `Services/ExchangeServiceBase.cs` and `Services/ExoConnectionPool.cs` are read, not changed: the fix fits in the
two services, `PermissionChangeFailure` and the pages.

Evidence re-read at `a1eb8a3a`:
- The pages set the step before the call and file by it. Mailbox Permissions `SubmitSingle`: `step` declared 458, `TheWrite`
  486, the cloud write 487-489, `AfterTheWrite` 490; the catch 526-530 - 528 `PermissionChangeFailure.Banner(step, ex)` and
  the log line "failed at {Step}", 529 the record with `filedAs: PermissionChangeFailure.AuditResultOf(step)`. Its
  `ExecuteOnPrem`: 677, 706, 707-709, 710; the catch 742-748 (744 banner and log, 745-747 the record). Calendar Permissions
  `SubmitSingle`: 428, 459, 460-471, 472; the catch 503-507 (505, 506). Its `ExecuteOnPrem`: 631, 659, 660-663, 664; the
  catch 685-691 (687, 688-690). `AuditResultOf` is Undetermined at `TheWrite` only.
- What a cloud write can throw. `MailboxPermissionService.AddMailboxPermissionsAsync` / `RemoveMailboxPermissionsAsync` (50,
  103) run in `RunPooledQueryAsync`; inside, the bind and recipient check return a failed result (52-60, 105-113) and each
  right is `TryRight` (156-167), a failure captured as a result. `CalendarPermissionService.SetCalendarPermissionAsync` /
  `RemoveCalendarPermissionAsync` (37, 84) run in `ExchangeServiceBase.RunAsync` (49-107), which turns every throw of its
  operation into a failed result. So what reaches the page is the pool's: `ExoConnectionPool.RunWithRetryCoreAsync` borrows
  at 507, outside its try, and `BorrowAsync` (262-380) refuses before any delegate runs - stopped (270, 308, 358, 365-370),
  the enablement unreadable (275-279) or off (281-282), the rows unreadable (288 -> `GetExoConfig` 245-252) or not
  configured (290-291), busy (294-298), or the connect failing (359; `CreateConnected`'s own busy gate 784-786). After a run
  nothing realistic throws: `Return` and `Discard` end in `DestroyRunspace`, which never throws (842-900). One exception:
  the Calendar removal is `allowRetry: true` (96), so a first attempt whose session answers with the pre-flight error is
  discarded and the retry's borrow can then fail, after that first attempt's command was sent.
- What an on-premises write can throw. Mailbox (442-506, 509-573): the credential fetch (447, 514), the shared throttle's
  "Exchange service is busy" (`ThrottledAsync` 137-147; 451, 518), `runspace.Open()` (456, 523), `ConnectOnPrem` (462,
  529; `ExchangeServiceBase` 506-557) and the session read (463, 530) come before the first `Invoke-Command`; each right is
  `TryRightOnPrem` (178-191), a result. Calendar (321-370, 373-418): the same steps (326, 330, 335, 341-342; 378, 382, 387,
  393-394), then one `Invoke(ps)` (355, 403) that throws whatever the remote command does - a failure after it was sent.
  The credential fetch returns null for every failure it names (`ModuleCredentialService.LookupCredentialsAsync`), but
  `DelineaService.GetCredentialsBySecretIdAsync` calls `LoadCredentials()` outside its try (137), so it is covered too.
- Other readers of these throws: the two bulk loops catch any exception and record `ex.Message` as a `Bulk..._Error` row
  (Mailbox 402-417, Calendar 288-303). Their message must not change.
- Tests: aud-a71/a73's `ACloudWriteThatThrows_IsRecordedAsUndetermined_WithTheBannersText` and its two siblings drive
  `PoolWhoseWriteBorrowThrows` - the write's own connect throwing, the very case this finding reclassifies - and aud-a75's
  `AnOnPremWriteThatThrows_IsRecordedAsUndetermined_WithTheBannersText` drives `ConnectOnPrem` throwing.

1. **The services say when nothing was sent.** New `Services/PermissionWriteSubmission.cs`:
   `PermissionWriteNotSubmittedException`, a public sealed `InvalidOperationException` whose message is its cause's and
   whose `InnerException` is the cause, so a caller that reads only the message (the bulk loops) reads what it read before;
   and `PermissionWriteSubmission`, one per write call: `Submitting()` marks the point the write's first command is handed
   to Exchange, and `TrackAsync(step)` returns the step's result as it is, lets a throw after the mark out as it is, and
   turns a throw before the mark into `PermissionWriteNotSubmittedException`. The mark is never cleared.
2. **Each write marks its command.** In the eight write methods, in place apart from one `var submission = ...` line each
   (no re-indented body): cloud - `return submission.TrackAsync(() => RunPooledQueryAsync(...))` / `RunAsync(...)`, so the
   borrow and its every refusal fall before the mark; `submission.Submitting()` immediately before the first write
   command's `Invoke` (Mailbox: after the bind and recipient check, before the first `TryRight`; Calendar: before the
   `Set-MailboxFolderPermission` and the `Remove-MailboxFolderPermission` invoke). On-premises - the credential fetch and
   the throttled body each through `TrackAsync`; `Submitting()` after `ConnectOnPrem` and the session read, before the
   first `Invoke-Command` (Mailbox: before the first `TryRightOnPrem`; Calendar: before `Invoke(ps)`). A Calendar removal
   whose first attempt reached its command and whose retry then cannot borrow stays a submitted write (the services do
   not rest "nothing was written" on the pool's reading of the pre-flight error). Nothing else changes: the results
   `TryRight`, `TryRightOnPrem` and `RunAsync` return, the bulk loops.
3. **The pages file by what the exception says.** `Services/PermissionChangeFailure.cs`: a fourth stage,
   `TheConnection = "the connection to Exchange"`; `StageOf(step, ex)` - `TheConnection` when the step is `TheWrite` and the
   throw is a `PermissionWriteNotSubmittedException`, else the step; `Banner(TheConnection, ex)` is "Nothing was written:
   the connection to Exchange failed: <cause>." with the cause read from the exception's `InnerException` through
   `ExchangeServiceBase.ResolveFailure`, the cmdlet's own words; `AuditResultOf` is unchanged, so `TheConnection` files
   Failed. On the four catch lines, in place: `step = PermissionChangeFailure.StageOf(step, ex);` first on the catch's first
   line (Mailbox 528 and 744, Calendar 505 and 687), so the banner, the log line's step and the record's result follow it;
   the action, `success: false`, the error being the banner, `extra` and the record's guard stay. No line added or removed.

Tests, `ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`:
- `PoolWhoseWriteBorrowThrows` becomes one case of `PoolWhoseWriteBorrowFails(atTheLocationRead, connectFailure,
  moduleConfig)`: its first stand-in call - the location read - does what the test gives, and connects after the first
  throw the test's failure.
- New `ACloudWriteWhoseBorrowIsRefused_IsRecordedAsFailed_NothingWasWritten(page, refusal)`, each page with each refusal:
  Exchange Online disabled at the location read (the harness's enablement saved off); the ExchangeOnline rows unreadable
  from the location read on (the pool's module config over a store that fails when told); the connect refused as busy (the
  connect seam throws `CreateConnected`'s busy-gate text); the pool stopped at the location read; the connection lost
  (aud-a71's `LostSession`, raw text unlike its captured error). Each: the banner "Nothing was written: the connection to
  Exchange failed: <the pool's own words>." (never the raw text); one row - `AddFullAccess` / `SetCalendar`, Failed, its
  error the banner, the target, the grantee and the written identity; the activity completed with the banner; one warning
  whose exception is a `PermissionWriteNotSubmittedException` with the cause inside; the location read made; nothing
  written. aud-a71/a73's `ACloudWriteThatThrows_IsRecordedAsUndetermined_WithTheBannersText` is retired into its "connection
  lost" rows; `ACloudWriteThatThrows_WhoseRowTheWriterDrops_...` and `..._WhoseRecordThrows_...` keep their purpose with that
  banner and the cause as the warning's inner exception.
- New `ACloudRemovalWhoseRetryCannotConnect_AfterItsCommandWasSent_IsRecordedAsUndetermined` (Calendar Permissions): the
  removal's command answers with the pre-flight error the pool retries on and the retry's connect fails - the write-step
  banner in the cause's words, one `RemoveCalendar` row Undetermined, one `Remove-MailboxFolderPermission` sent.
- aud-a75's test is retargeted. Its connect-throw case becomes `AnOnPremWriteWhoseConnectFails_IsRecordedAsFailed_NothingWasWritten`
  (each page; "Nothing was written" in the captured words, one `OnPrem_AddFullAccess` / `OnPrem_SetCalendar` row Failed, no
  Invoke-Command, nothing written). Its Undetermined case becomes `AnOnPremWriteThatThrowsOnceSent_IsRecordedAsUndetermined_WithTheBannersText`
  (Calendar Permissions: the remote set and its fallback add fail through the harness's `Failures`, so the Invoke-Command
  was sent and throws - the write-step banner, one `OnPrem_SetCalendar` row Undetermined). Its Mailbox Permissions
  counterpart `AnOnPremRightExchangeRefuses_IsItsOwnFailedRow_AsBefore`: `Add-MailboxPermission` fails, `TryRightOnPrem`
  makes it a result, and its own row is Failed through the write's success path - as before.
- `TheStepBanner_SaysWhatCouldHaveBeenWritten_InTheCausesOwnWords` gains the `TheConnection` row (its cause inside a
  `PermissionWriteNotSubmittedException`); `TheStepsAuditResult_IsUndetermined_OnlyAtTheWrite` gains `TheConnection` ->
  Failed; new `TheStageOfAThrow_IsTheConnection_OnlyForAWriteThatWasNeverSent` (`StageOf` at each stage, typed and untyped).

New `ExchangeAdminWeb.Tests/PermissionWriteSubmissionTests.cs`:
- The tracker: a throw before `Submitting()` is the typed exception, its message the cause's and its inner the cause; a
  throw after it goes out as it is; a result goes through; a step that throws before it returns a task is typed too.
- The eight writes over `ExchangeWriteHarness`: the four cloud writes over a pool whose connect throws - each throws the
  typed exception with that failure inside, and no stand-in was called; the four on-premises writes whose `ConnectOnPrem`
  throws - the same, no Invoke-Command; the two on-premises Calendar writes whose remote command fails - the untyped
  failure after one Invoke-Command.
- The bulk loops: a row whose write cannot connect is recorded as before - one `BulkAdd_Error` / `BulkSetCalendar_Error`
  row whose error is the cause's own message, the entry ERROR with that message.

Mutations at the call site (md5, mutate, rebuild, filtered tests, failing names, restore from git, touch, rebuild, md5
equal, green): M1 the Mailbox Permissions `SubmitSingle` catch without `step = PermissionChangeFailure.StageOf(step, ex);` -
the pre-write failure filed through `AuditResultOf(TheWrite)` again; M1c the same on Calendar Permissions; M2 the Mailbox
Permissions `ExecuteOnPrem` catch without it; M2c the same on Calendar Permissions. Extras: E1 `TrackAsync` types no
throw; E2 `TrackAsync` types every throw, the mark ignored; E3 the on-premises Calendar set marks before `ConnectOnPrem`;
E4 `Banner` reads the cause of a `TheConnection` throw from the typed exception rather than its inner; E5
`RemoveMailboxPermissionsAsync`'s borrow not tracked.

Files: `Services/PermissionWriteSubmission.cs` (new), `Services/PermissionChangeFailure.cs`, `Services/MailboxPermissionService.cs`,
`Services/CalendarPermissionService.cs`, `Components/Pages/MailboxPermissions.razor`, `Components/Pages/CalendarPermissions.razor`
(in place), `ExchangeAdminWeb.Tests/PermissionPagesSelfGrantTests.cs`, `ExchangeAdminWeb.Tests/PermissionWriteSubmissionTests.cs`
(new), this record.

Caller-visible change, by design: a single permission change whose write failed before its command reached Exchange -
Exchange Online disabled, its enablement or settings unreadable, not configured, the pool busy or stopped, the connection
failing; on premises the credentials, the throttle, the session or the connect - is filed Failed with "Nothing was written:
the connection to Exchange failed: <cause>.", where it was filed Undetermined with "Whether the change was made could not
be confirmed: the write failed: ...". A write that failed after its command was sent stays Undetermined. Bulk rows are
unchanged. Versions, the supervisor's: the base app version (the shared `PermissionChangeFailure` and the new shared
`PermissionWriteSubmission`) and the Mailbox Permissions and Calendar Permissions module versions.

## Guard proof

Pending.
