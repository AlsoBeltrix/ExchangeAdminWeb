# Defect review request: the merged findings whose codex review was owed - Migration Approve allow-list and forest-wide excluded-group lookup, the Exchange base's size and recipient reads, Migration denials and exclusion wording (aud-a23r, a23o, a23q, a23p); CI install retries and pins (cdt-39, cdt-40); the EXO pool's post-Dispose borrow (aud-a6f); the Comms-10k count wording (cdr-followup-1, cdr-followup-2); the Exchange-target AD binding (aud-a2c); the on-prem applied-rights parse (aud-a23b); the cutover's snapshot stop and restart report (aud-a38e, aud-a38h)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `0b67355d` or later.
- Each record names its commits; review each range and read the COMBINED files on master:
  1. `aud-a23r`, `aud-a23o`, `aud-a23q`, `aud-a23p`: merged at `d690a6c1` (`2bf76220` plans, `dd74db81` a23r, `990a02ab` a23o,
     `d58a0bc5` a23q, `2dba5725` a23p, `9fa7664d` comments, `4edbd508` records; forked from `5eac8b25`): `git diff 5eac8b25 4edbd508`.
     Files: `Services/MigrationService.cs` (`RefuseUnlessApprovable` through `MigrationBatchActionPlanner.Applies(Complete, ...)`;
     `CheckExcludedGroups` with the forest catalog, `DirectoryLookupRun`, per-domain `Get-ADGroup`; `RecordDenial`,
     `RefusesSingleCreate`/`RefusesBulkCreate`; `PermissionResult.UnverifiedTargets` use), `Services/ExchangeServiceBase.cs`
     (`MailboxSizes`, `GetOnPremMailboxSizeAsync`, `HasCloudMailboxAsync` and `OnPremRecipientExists` under Stop,
     `GetMailboxLocationAsync` throwing, `CheckAdGroupMembership` removed, `IsNotFound`/`ReadUnlessNotFound`/`ReadSize` moved in),
     `Services/RecipientLookupService.cs`, `Models/PermissionResult.cs`, `Components/Pages/Migration.razor` (the eight and twelve
     in-place lines), `Components/Pages/CalendarPermissions.razor` 431 and `MailboxPermissions.razor` 461 (the location-read refusal),
     `Modules/ModuleCatalog.cs` (the ExcludedADGroups description), and the tests `MigrationForestLookupTests.cs`,
     `MigrationDenialTests.cs`, `MigrationExclusionWordingTests.cs`, `ExchangeServiceBaseOnPremReadTests.cs`.
  2. `cdt-39` (merged `86f3d4cb`: `f1fa86cc` plan, `66403d56` fix, `1a253747` comments) and `cdt-40` (merged `bba6d659`:
     `9bb69900` plan, `0f185411` fix, `f0b50bb4` comments): `.github/workflows/ci.yml` (the three-attempt installs of
     PSScriptAnalyzer 1.25.0 and Pester 6.2.0; the versioned `Import-Module`) and `tests/ps/DeployInvariants.Tests.ps1`
     (`CI gates`, `Get-CiStepScript`).
  3. `aud-a6f` (`436a44a1`): `Services/ExoConnectionPool.cs` (the post-slot stop check and the connect after Dispose).
  4. `cdr-followup-1`, `cdr-followup-2` (`e03e7c8`): the Comms-10k resolve-stage prose and validation banner (member count vs
     address count) - `Services/Comms10kService.cs` and the Comms-10k page/processor the records name.
  5. `aud-a2c` (`7f1a0e6e`): the Exchange-target check binding the AD object by canonical SMTP address
     (`Services/PermissionValidator.cs` / `Services/ProtectedPrincipalService.cs` as the record names).
  6. `aud-a23b` (`edafb75`): the on-prem path parsing applied rights out of Detail (`Services/MailboxPermissionService.cs`).
  7. `aud-a38e`, `aud-a38h` (the commits their records name): `tools/cutover.ps1` / `tools/AppPoolControl.psm1` - the snapshot's
     stop-and-wait before snapshotting dev's live database, and the restart report waiting for the pool to come back.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One command per
  call. No writes, no builds, no network.
- Records: `.agents/review/findings/<id>.md` for each id above; parents as each record names. ALREADY admitted and out of
  scope (do not re-raise): `aud-a53`, `aud-a54`, `aud-a55`, `aud-a68`, `aud-a70`, `psl-10`, `aud-a2y`, `aud-a64`, `aud-a67`
  (read their first paragraphs to know what they cover). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known
  Failure Classes" (1, 2, 3), Architectural Invariants 4-7; `docs/ProjectConstitution.md`.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat d690a6c1`. Report whether both succeeded.

## Mandate

Hunt for observable defects. Group 1: the allow list through the planner - a mailbox in a batch of many is unchanged
(aud-a55 later added its status read; confirm the two gates do not now double-refuse or skip a user); the forest
catalog `-Filter` on port 3268 (the labelled assumption); per-domain `Get-ADGroup` with `IsNotFound` deciding "not in
that domain" - a domain unreachable (not not-found) refuses the whole run: right; `DirectoryLookupRun` cached across a
bulk file - a transient failure refuses every row (trade-off named?); the base's `GetMailboxLocationAsync` now
THROWING where it returned "Unknown" - every caller of it (Calendar, Mailbox, Recipient Lookup, Migration): each
catches and refuses before writing? (Known Failure Class 1); `MailboxSizes` consumers summing an unreadable size
(Class 2); the denial rows' `mutationAttempted: false` and the guard returns unaudited; `UnverifiedTargets` on every
site. Group 2: the retry loop's exit code on three failures (does the job fail, or does a failed install fall through
to `Import-Module -RequiredVersion` with a clearer error); the pinned versions vs what the tests pin. Group 3: the
post-Dispose borrow - the window between the stop check and the connect, closed how; a borrow in flight at Dispose.
Group 4: the count wording - every site. Group 5: binding by canonical SMTP - an AD object with the same mail as a
recipient that is not it (a contact) - does the check now refuse or bind the wrong object. Group 6: the applied-rights
parse - what replaced it, and does the cloud path's equivalent agree. Group 7: the stop-and-wait before snapshot -
bounded, and what if the pool does not stop; the restart report - waits for `Started` state or a health probe, and what
if neither comes. Tests passing for the wrong reason: the Pester `Get-CiStepScript` extraction vs the real YAML; the
Migration stand-in's `Get-ADGroup -Server` per domain.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.
