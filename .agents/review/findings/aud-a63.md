# aud-a63: The event log's ExecuteUndo catch and the config store's post-write logging are unguarded, so a throwing provider leaves an undo unaudited and reported as success, or makes a saved setting look failed

**Severity**: MEDIUM - the aud-a28q/a45 class at two places where it changes an outcome. (1)
`AdminEventLog.razor` `ExecuteUndo`'s catch calls `Logger.LogError` unguarded, before `undoError` is set and
before `LogUndoNotRun`; with a throwing provider an undo that failed before its module was called leaves no
audit row, the `finally`'s `activity.Complete(undoError == null, ...)` reports success, and the exception
reaches the circuit. (2) `ModuleConfigService` calls `LogInformation` after the committed write, unguarded,
inside the lock and before `ConfigSaved?.Invoke` (three sites); a throwing provider makes a saved change
look failed and skips this instance's `ConfigSaved` subscribers.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a56/a57/a58 agent, 2026-10-23, candidates 1 and 5 of its hand-back)

## Evidence

At `3da510cb`: `Components/Pages/AdminEventLog.razor` around 1209 (the `ExecuteUndo` catch; the page is
NotYetConverted for `ClickGateRegistry`); `Services/ModuleConfigService.cs` 103, 127, 223 (the
post-write `LogInformation` calls), the `ConfigSaved` invocation that follows each.

## Approach (to be written by the implementing agent before code)

- `ExecuteUndo`'s catch sets `undoError` and writes `LogUndoNotRun` FIRST, then reports through the page's
  guarded `ReportSafely` (aud-a57); the activity completes with the failure; nothing reaches the circuit.
- The three post-write logs go through a nested guard (the aud-a28u shape) so a saved change is reported
  saved and `ConfigSaved` is raised whatever the provider does.
- Tests: `EventLogUndoRefusalTests` with a throwing logger (an undo that fails before its module: one
  `LogUndoNotRun` row, activity failed, no throw); `ModuleConfigServiceTests` with a throwing logger (the
  save returns saved, `ConfigSaved` raised). Mutations at the call site: the unguarded log before
  `undoError` again; one post-write guard removed.

## Guard proof

Pending.
