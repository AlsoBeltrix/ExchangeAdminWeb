# Defect review request: the 2026-10-26 consolidation - six branches written in parallel against different bases, merged by the supervisor

You are reviewing, not implementing. Do not modify any file.

## Why this review exists

Eight implementation agents were working in parallel worktrees when all of them were killed mid-slice by an
account spend limit. The supervisor merged every slice each agent had COMMITTED and stopped. Each branch was
written against a different base and none of their authors could see the others' work. The highest-value
target of this review is therefore INTERACTION between the merged slices, and anything the supervisor's own
conflict resolutions got wrong - not a re-review of each slice in isolation.

One interaction has already been found and fixed by the supervisor at `8f55802a`: kd-52 made Conference Rooms'
access refusal audited, which broke a kd-7 control test that pinned the absence of that row. Assume there are
more of that shape.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `8f55802a` or later.
- The consolidation is `00798adb..8f55802a`. Review `git diff 00798adb 8f55802a -- . ':!.agents'` and read the
  COMBINED files it touches. The merges, in order, and what each carried:
  - `32a051fc` fix/cdt36: cdt-52 (the installer's move refusal names a spelling that does not exist on disk),
    cdt-53 (docs pointers), cdt-54 (before each stop, deploy.ps1 names the pool's other applications and reads
    their bulk jobs). Files: `deploy.ps1`, `tools/Install-ExchangeAdminWeb.ps1`, `tools/AppPoolControl.psm1`,
    `tools/JobStateWarning.psm1`, `tests/ps/*`, `docs/Containerization-Feasibility.md`.
  - fix/a64-a67: aud-a64 items (1)-(4) - the Admin Event Log's undo, preview and usage catches set their state
    then report through the guard; `ModuleConfigService` and `Services/Storage/LegacyConfigImport.cs` report
    through the guard. Items (5)-(9) were NOT finished and are not here.
  - `f4d09bd9` fix/a68-kd2: kd-33 (a Migration write whose action throws is audited, Failed when nothing was
    sent and Undetermined when it may have been), kd-34 (nine more catches worded through ResolveFailure),
    kd-35 (a search that opens another batch withdraws a staged row action), kd-36 (a denied bulk run's audit
    row carries its count and only its first 50 names). Files: `Components/Pages/Migration.razor`,
    `Services/MigrationService.cs`, tests.
  - `b7440dce` fix/a70: kd-18 and kd-40 for MAILBOX Permissions only - the page requires a ticket and records
    its refusals before the target is bound. Calendar Permissions was left unfinished and is NOT here, so the
    two pages are now deliberately asymmetric.
  - `4757fbfe` fix/psl10-a2y: kd-52 - Conference Rooms' four write handlers audit an access refusal once.
    The supervisor resolved `Modules/ModuleCatalog.cs` to Conference Rooms 2.13.0.
  - `c47ec979` fix/kd7-kd8: kd-47 - the extended-log folder retry treats a delete-pending lock file as not yet.
  - `8f55802a`: the supervisor's repair of the kd-7 control test described above.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`, `git grep`). One
  command per call. No writes, no builds, no network, no IIS, no directory.
- Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` "Known Failure Classes" (1 side-effect ordering, 2 success
  aggregation, 3 fail-closed authorization) and the `ClickGateRegistry` line-keying rule; `docs/ProjectConstitution.md`
  (audit on every mutating action, notifications, ticket).
- Out of scope, do not re-raise: anything already an open row in `.agents/review/index.md` (read it); the
  deliberate Mailbox/Calendar asymmetry itself (it is recorded as unfinished work, though a DEFECT caused by the
  asymmetry is in scope); the absence of a native -PlanOnly in deploy.ps1.

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 8f55802a`. Report whether both succeeded.

## Mandate

Hunt for observable defects, weighted toward interactions and merge damage.

1. **Tests that now pass or fail for the wrong reason.** The kd-7/kd-52 case was one. Look for any other test
   whose assertion pins the ABSENCE of a row, a message, a call or a field that another merged slice now adds -
   and for any test whose fixture was written against a handler another slice has since changed.
2. **Audit-row collisions.** Several slices add refusal rows on paths that already audit. Name any path that now
   writes TWO rows for one refusal, or a row whose `refusedAt` contradicts another slice's, or a success row that
   survives a refusal.
3. **Mailbox vs Calendar Permissions.** Mailbox now requires a ticket and audits its refusals; Calendar does not.
   Name any shared service, test, registry entry or notification path that the asymmetry actually breaks (for
   example a shared helper now called with different expectations from the two pages).
4. **Migration.** kd-33, kd-34, kd-35 and kd-36 all edit the same page and service. Check the audit row for a
   throwing action (Known Failure Class 1: is it written on every escape path, exactly once, with the right
   Failed vs Undetermined choice), the bounded `targets` and its count, and the search-route withdrawal against
   the other two batch-change routes.
5. **The ops scripts.** cdt-54 enumerates a pool's applications before each stop. Check its failure mode when the
   enumeration throws or returns nothing, whether it can now refuse a legitimate deploy, `$LASTEXITCODE` handling
   on any native call, pure-ASCII and Windows PowerShell 5.1 parse safety.
6. **The supervisor's conflict resolutions.** `Modules/ModuleCatalog.cs` is the one file several branches all
   touched: confirm every module entry has exactly one `Version`, that each version note's number matches the
   `Version` below it, that no note was lost, and that no module's behaviour changed in this range without its
   version moving.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and
line; triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to
impact; a better approach. No style preferences, no out-of-scope work.