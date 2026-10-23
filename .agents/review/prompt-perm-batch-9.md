# Defect review request: a permission write whose outcome is unknown is filed Undetermined rather than Failed, on the cloud path (aud-a73) and the on-premises path (aud-a75)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `4898576e` or later.
- The change is the branch merged at `39afd9a1`: `f224dd22` (a73 plan), `75355a7b` (aud-a73), `ff0b9ded` (a73 record),
  `b8bad419` (a75 plan), `73af9fc8` (aud-a75), `8715bc89` (a75 record), forked from `066ea1a2`. Review
  `git diff 066ea1a2 8715bc89 -- . ':!.agents'`, and read the COMBINED `Services/AuditResult.cs` (new),
  `Services/AuditService.cs` (`LogMailboxPermission` / `LogCalendarPermission` with `AuditResult? filedAs = null`;
  `ResultText`; `IsSuccessfulAuditResult` unchanged), `Services/PermissionChangeFailure.cs` (`AuditResultOf`),
  `Components/Pages/MailboxPermissions.razor` 529 and 747, `Components/Pages/CalendarPermissions.razor` 506 and 690, and
  the tests `PermissionPagesSelfGrantTests.cs`, `AuditServiceTests.cs`, `EventLogSectionsTargetTests.cs`,
  `AuditWriterThrowFreeTests.cs` on master.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command
  per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a73.md` (why an optional parameter, not an overload), `aud-a75.md`; parents
  `aud-a71.md`, `aud-a59.md`, `aud-a53.md`, `aud-a28z.md` (part 5). ALREADY admitted and out of scope: `aud-a70` items 12
  and 13 (the Calendar on-prem set/remove letting a cmdlet refusal out as an exception, so a refused write now files
  Undetermined; `IsSuccessfulAuditResult` counting Undetermined as a success for usage telemetry and the trace). Do not
  re-raise them. Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1, 2, 3);
  `docs/ProjectConstitution.md` (audit rules).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 39afd9a1`. Report whether both succeeded.

## Mandate

Hunt for observable defects. The `filedAs` parameter: a caller passing `filedAs: Success` with `success: false` - the
error is dropped ("kept unless the row is filed Success") while the bool says failure: is that combination reachable
and is it right; `AuditResultOf(step)` - `TheWrite` -> Undetermined: a write that threw BEFORE Exchange was contacted
(the pool refused the borrow: module off, settings unreadable) is "could not be confirmed" though nothing could have
applied - is the banner and the Undetermined filing honest there, or should the pool's pre-connect refusal be a
Failed/nothing-written case (name severity; the aud-a71 record says the borrow throws reach this catch); the event
log's Undetermined filter now holds eligibility checks AND permission writes - any report or export that counts
Undetermined rows as one kind; `AuditWriterThrowFreeTests`' argument builder passing Undetermined - does the tripwire
still reach the `success`-derived branch (both branches covered); `AuditExtraChannelTests` reading the first signature
match - still the right method after the parameter was appended. Tests passing for the wrong reason: the on-prem
write-throw test's `ConnectOnPrem` throwing before any `Invoke-Command` - does the production catch distinguish
"connect failed" (nothing written) from "write failed" (unknown), and should it (the record files both Undetermined at
the write step - name it if a connect failure is provably nothing-written).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.
