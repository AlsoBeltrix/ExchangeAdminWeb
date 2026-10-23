# Defect review request: ticket presence and validation on M365 Group Management, Named Locations and Conference Rooms writes; Self-Service Groups outcome scoped to its group (kd-7, kd-8)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `881b8d5b` or later.
- The change is the branch merged at `881b8d5b`: commits `cd25a924`, `11408fae`, `f0ce2431`, `aed153c9`, forked from `3e43b35b`.
  Review `git diff 3e43b35b aed153c9 -- . ':!.agents'` and read the COMBINED `Components/Pages/M365GroupManagement.razor`
  (seven write paths), `Components/Pages/NamedLocations.razor` (Save and Delete), `Components/Pages/ConferenceRooms.razor`
  (four write handlers about 1033, 1277, 1383, 1587 and the helpers about 1720-1730; the page is still 1758 lines),
  `Services/ConferenceRoomService.cs` (the new presence helpers next to `WithAttributesConfirmed`),
  `Components/Pages/SelfServiceGroups.razor` (every completion publishes only while its group is on screen),
  `Services/ServiceNowService.cs` 57-65 (why presence must be checked before validation: a switched-off ServiceNow passes
  every ticket), `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` and `ProgressRegistry.cs` (changed), and the tests
  `M365GroupManagementTicketGateTests.cs`, `NamedLocationsTicketGateTests.cs`, `ConferenceRoomTicketPresenceTests.cs`,
  `SelfServiceGroupsResultScopeTests.cs` (all drive the page handlers by reflection).
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command per
  call. No writes, no builds, no network.
- Records: `.agents/review/findings/kd-7.md` (including the declined Conference Rooms "two paths" claim and why), `kd-8.md`;
  reference shapes: the RiskyUsers and IntuneDevices ticket gates, `kd-4.md`. ALREADY admitted and out of scope - do not
  re-raise: `kd-18` (the same presence gap on Mailbox/Calendar Permissions, Group Management, DHCP Authorization, MFA Reset),
  `kd-19` (Self-Service Groups `LoadMembers` not checking the selected group), the M365 Group Management protection gap
  (`UpdateGroupAsync` / `DeleteGroupAsync` calling no `CheckProtectedAsync` - owner-excluded), and the Conference Rooms
  registry notes at 125 and 158 (wording). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Known Failure Classes (1, 2,
  3) and the ClickGateRegistry line-keying rule; `docs/ProjectConstitution.md` (ticket, audit, notifications).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat aed153c9`. Report whether both succeeded.

## Mandate

Hunt for observable defects. Ticket gates: for each write path name where the presence check and the validation sit
relative to the first mutating call and to the protected-principal check (order matters: a refusal must precede every
write and should not leak protection information before the ticket is known), whether a whitespace-only or
control-character ticket passes presence, whether the refusal row carries `refusedAt=TicketValidation` and
`mutationAttempted=false` on every path including bulk ones (Known Failure Class 2: a refused row inside a bulk run is a
per-row failure), and whether any path still reaches ServiceNow with a blank ticket; the Conference Rooms helpers in the
service - do they change behaviour for the bulk processor, which has no page; M365 Group Management: the seven paths - is
any eighth write (owner add/remove, member add/remove, rename, delete, update, restore?) missed; the "sends no admin
email" choice on a ticket refusal - does the Constitution require an administrator notification on a refused mutating
action, and what do the other modules do; kd-8: every completion path (success, failure, refusal, cancellation, exception)
checks the shown group - name any that does not; the `IsManageBusy` disabled-state claim; the registry edits - any entry
whose key text no longer matches the page line; tests passing for the wrong reason (reflection invoking a handler whose
guard is in markup only; a stub ServiceNow that validates when production's would pass).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.