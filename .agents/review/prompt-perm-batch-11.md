# Defect review request: permission pages - guarded success rows, refusal audit rows, Undetermined outcomes, UPN-only sign-in lookup, dynamic-group refusal, credential hints (aud-a70 parts 1-5, 7-9, 11-12, 14-15, 17)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `ea565cbd` or later.
- The change is the branch merged at `ea565cbd`: commits `13909c2f`, `83311a2b`, `9b0aa71b`, `dc3abcd7`, `fe99aff4`, `d0ed01eb`,
  `7576cea7`, `0ef2ac77`, `32d548ec`, `f374e13a`, `a4155f5c`, `d609001e`, forked from `eb017102`. Review
  `git diff eb017102 d609001e -- . ':!.agents'` and read the COMBINED `Components/Pages/MailboxPermissions.razor` (808 lines) and
  `CalendarPermissions.razor` (762 lines): `SubmitSingle`, `ExecuteOnPrem`, `ProcessBulk`; `Services/MailboxPermissionService.cs`,
  `Services/CalendarPermissionService.cs` (on-prem Calendar now returns Exchange's refusal as a failed result), `Services/ExchangeServiceBase.cs`
  (`OutcomeUnknown`: the pool's connection reading plus a `PSRemotingTransportException` check), `Services/MailboxPermissionOutcome.cs`
  (`RightOutcome.Unconfirmed`), `Models/PermissionResult.cs` (`UnconfirmedRights`, `WithMessage`), `Services/SelfGrantRefusalRecord.cs`
  (`Recorded` / `Said`), `Services/PermissionChangeNotifier.cs` (`_UNCONFIRMED` admin notification; no user or owner notification),
  `Services/PermissionChangeFailure.cs` (one-line banner), `Services/SelfGrantDirectory.cs` and `Services/PermissionValidator.cs` (a UPN-only
  sign-in looked up under the directory-read credential, refused without it), `Services/ExchangeGroupMembers.cs` (typed exception and
  refusal naming an empty dynamic group), `Services/MigrationService.cs` (touched - say what and why), `Components/Pages/AdminSettings.razor`
  and `README.md` (credential hints), and the tests listed in the diff.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network, no directory.
- Records: `.agents/review/findings/aud-a70.md` (17 numbered items across five sections; the record states which are done, which are
  owner calls (6, 13), the (10) coder dispute and the (16) by-design); shapes: `aud-a53.md`, `aud-a28q.md`, `aud-a28z.md`, `aud-a73.md`,
  `aud-a76.md`, `aud-a60.md`, `aud-a69.md`. ALREADY admitted and out of scope - do not re-raise: `kd-38` (ProcessBulk's admin notification
  unguarded), `kd-39` (ProcessBulk's finally Complete unguarded), `kd-40` (ticket-validation and re-authorization refusals write no
  audit row), `kd-41` (`WithServicedNote` drops `UnverifiedTargets`), `kd-18` (blank ticket passes while ServiceNow is dormant), items
  (6), (10), (13), (16). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Known Failure Classes (1, 2, 3), Architectural Invariant 7;
  `docs/ProjectConstitution.md` (audit, notifications on every mutating action, credential isolation).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat d609001e`. Report whether both succeeded.

## Mandate

Hunt for observable defects. Undetermined (12, 14): `OutcomeUnknown`'s two signals - can a refusal Exchange sent AFTER the command
ran (a post-write policy error) be read as Unconfirmed, or a transport drop BEFORE the command as a refusal; the per-right
`Unconfirmed` flag across a multi-right mailbox write - a second right's drop after the first applied; both bulk loops filing
Undetermined rows (Known Failure Class 2); the retargeted aud-a76 tests - did an assertion that mattered get lost. (15): the
`_UNCONFIRMED` admin notification - sent exactly once, never for a Failed-nothing-sent result, and does the Constitution's "every
mutating action" also want the affected user told something; the catch-path notification nothing now reaches - dead or
reachable. (7): the UPN-only lookup under the directory-read credential - fail-closed when absent, no app-pool fallback, and no
environment fact in the reasoning (Invariant 7). (11)/(8): the empty-dynamic-group refusal - can a legitimately empty calculated
group ever be granted now, and does the message tell the operator what to do. (1)/(4)/(5): every refusal and failure row carries
the written identity, the servicer note where one applies, and `mutationAttempted` consistent with whether a command was sent;
the "Nothing was written." suffix only where nothing was. (9)/(17): the Complete guard on both paths; the banner's whitespace
collapse not eating a meaningful line break in a cause. `Services/MigrationService.cs` - why is it in this diff. Tests passing for
the wrong reason (the stand-in session throwing a `PSRemotingTransportException` shape production never produces; a guard test
that passes with the guard removed because the stand-in never throws).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.