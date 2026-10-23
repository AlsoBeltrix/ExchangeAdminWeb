# Defect review request: Self-Service Groups load race, ticket presence on Group Management / DHCP Authorization / MFA Reset, bulk processor ticket, shared TicketPresence rule (kd-19, kd-18 three pages, kd-23, kd-24)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `f3194ce3` or later.
- The change is the range merged at `1973e512`: commits `4a6e0271` (kd-19), `26a5cd4a`, `20b0528b`, `790bcb56` (kd-18 on
  GroupManagement, DhcpAuthorization, MfaReset), `77aa2641` (kd-23), `2554e7b6` (kd-24), on top of `aed153c9`; the supervisor's
  merge resolution of `Modules/ModuleCatalog.cs` and its repair `f3194ce3` (a duplicate Version line) are part of what landed.
  Review `git diff aed153c9 2554e7b6 -- . ':!.agents'` and read the COMBINED `Components/Pages/SelfServiceGroups.razor` (`SelectGroup`
  refusing mid-operation; `LoadMembers` publishing by token, not a live `selected` read), `Components/Pages/GroupManagement.razor`
  (four writes), `Components/Pages/DhcpAuthorization.razor` (authorize; deauthorize already checked), `Components/Pages/MfaReset.razor`,
  `Services/Jobs/ConferenceRoomBulkProcessor.cs` (`ProcessRowAsync`: the no-ticket refusal after the disabled-module refusal and before
  the authorization re-check, the protection gate and any write), `Services/TicketPresence.cs` (new: no blank, no control characters;
  used by every gate on this branch, DHCP `RemoveServer` and `TicketValidationService`), `Services/ConferenceRoomService.cs`,
  `Components/Pages/M365GroupManagement.razor`, `Components/Pages/NamedLocations.razor`, `Modules/ModuleCatalog.cs` (seven version
  entries), and the tests in the diff.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/kd-19.md`, `kd-18.md`, `kd-23.md`, `kd-24.md`; parents `kd-7.md`, `kd-8.md`; the ticket-batch-1
  result is `.agents/review/ticket-batch-1.result.txt`. ALREADY admitted and out of scope - do not re-raise: `kd-45` (five more
  `IsNullOrWhiteSpace` ticket checks), `kd-46` (MFA Reset reads the ticket after an await), `kd-47` (a test catches only IOException),
  the two permission pages' half of kd-18 (another branch), the owner-deferred Account Lockout module. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` Known Failure Classes (1, 2, 3); `docs/ProjectConstitution.md` (ticket, audit).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 2554e7b6`. Report whether both succeeded.

## Mandate

Hunt for observable defects. kd-19: the token - can a stale load still publish if the token is reused or compared by value, does
`SelectGroup`'s refusal while busy leave the operator with no way out if a load hangs, and is the adapted kd-8 test still proving
kd-8's claim; kd-18: on each of the three pages, the presence check sits before validation and before every write, the refusal
row has `refusedAt=TicketValidation` and `mutationAttempted=false`, and no path still reaches ServiceNow blank; kd-23: the
processor's refusal is per row (Known Failure Class 2), the row's audit shape matches the page's, and a job with a ticket on some
rows and none on others (is the ticket per job or per row?) behaves as intended; kd-24: `TicketPresence` - which characters count
as control (C0 only, or also C1, zero-width, bidi marks, non-breaking space), whether trimming happens before the check, and whether
its use in `TicketValidationService` (BitLocker and Cloud Password Reset) changed those modules' accepted inputs without a version
bump; `ModuleCatalog.cs` after the resolution - every entry has exactly one `Version`, each note's number matches the Version
below it, and no module lost a note; tests passing for the wrong reason (reflection handlers whose guard is markup-only; a
control-character fixture that `IsNullOrWhiteSpace` would also refuse).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.