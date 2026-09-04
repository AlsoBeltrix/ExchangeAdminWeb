# ute-2: Action telemetry hooked one audit method and would miss major modules

**Severity**: HIGH - the Usage view would undercount actions for Mailbox Permissions,
Conference Rooms, Migration, MFA Reset and others, making used modules read as
"opened but not used" - the exact question the feature exists to answer.
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/ute-2.md`.

## Evidence

`docs/UsageTelemetry-Plan.md` at `4af217e`, section 1: "the one choke point every
module already passes through, `AuditService.LogModuleAction`"; section 6: only
`LogModuleAction` gained the hook. `Services/AuditService.cs`: `LogMailboxPermission`
(`:39`, category `MailboxPermission`), `LogCalendarPermission` (`:72`),
`LogMigrationCheck` (`:103`), `LogMigrationBatch` (`:130`), `LogMigrationAction`
(`:164`), `LogLookupAction` (`:219`), `LogMfaResetAction` (`:246`), the ConferenceRooms
writer (`:299`), `LogADAttributeEdit` (`:321`), `LogSettingsChange` (`:356`) - each
builds its own event and calls `WriteAuditEvent`, never `LogModuleAction`.
`Components/Pages/MailboxPermissions.razor:350`, `ConferenceRooms.razor:820`,
`Migration.razor:938` call those methods.

## Predicted observable failure

An operator grants ten mailbox permissions in a day; the Usage view shows Mailbox
Permissions with N opens and 0 actions.

## What

Plan defect: a claim about the audit call graph ("every module passes through
LogModuleAction") that reading `AuditService` disproves.

## Approach

Plan revised: the hook is the LAST statement of the private `WriteAuditEvent`, the
one method every public audit writer ends in, reading `category`, `action` and
`result` off the event dictionary already built. Reporting maps `category` to a module
id through a pure `ModuleOf` (catalog id verbatim, else a small static table for the
legacy categories the Event Log dropdown already enumerates, else null) and the table
gains a `category` column so unmapped categories still report by name under "Other".
The test plan replaces the single-method behavioral test with one that calls EVERY
public `Log*` method once and expects one row each.

## Files changed

- `docs/UsageTelemetry-Plan.md` - section 1 (settled item), AC3, AC5, AC9, section 6
  (`UsageTelemetryService`, schema note, `AuditService` change), S2, section 8,
  section 10.

## Guard proof

Docs-only. The planned `AuditService_EveryPublicAuditMethod_CallsRecordAction` and
`AuditService_HookIsLastInWriteAuditEvent` tests bite at S2. `git diff --check` clean
on the fold commit.

## Coder dispute (if any)

None.

## Known gaps

`LogSettingsChange` audits Admin Settings edits, which have no module; they report
under "Admin Settings" by category. Recorded as scope, not a gap.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; same dispatch as ute-1)
Harness: codex-cli 0.152.1. Reviewed SHA `4af217e`, base `2938db7`, capability_ok
true, verdict `acceptable_with_changes` (material change 2). Dispatched 2026-09-04;
envelope at `.agents/review/ute.result.json`.
