# aud-a28q: The audit and trace writers are not throw-free

**Severity**: LOW/MEDIUM - every "never throws" helper added since aud-a28f (`LogAllowlistSaveRefused`,
`LogSaveRefused`, `LogUndoRefused`, `LogUndoNotRun`, the aud-a28h settings helpers) catches around the
audit call, but the writers themselves can throw before their own try: `JsonlLogService.WriteToFile`
serialises the record outside its try, so a value `System.Text.Json` cannot serialise throws out of
the audit call; `OperationTraceService.SamName(null)` throws on a null actor. A handler that calls an
audit or trace method directly (the success paths do) can therefore end with an exception instead of
its outcome, and the record is lost. The helpers' catches hide it on the refusal paths only.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a26l/a26m/a28o agent, 2026-10-22, candidate 4 of its hand-back)

## Evidence

At `fee8a58b`: `Services/JsonlLogService.cs` `WriteToFile` (serialisation before the try);
`Services/OperationTraceService.cs` `SamName` (null actor). `git grep -n "LogAudit\|LogSettingsChange\|LogModuleAction" Components/Pages/*.razor Services/*.cs`
for the direct callers on success paths.

## Approach (to be written by the implementing agent before code)

- `WriteToFile`: serialise inside the try; on any failure log one error (with the record's action and
  category, never its payload) and return - an audit write never throws into a handler. The same for
  the trace writer; `SamName` tolerates null (returns "unknown", as the aud-a28g lookup refusal does).
- A tripwire test: every public method of `AuditService` and `OperationTraceService` completes without
  throwing when handed a record containing an unserialisable value and a null actor, and logs one
  error. Mutation at the call site: serialise outside the try again; `SamName` throws on null again.
- Keep the Constitution's rule: an audit failure is logged and never reported as a failed save (aud-a28f),
  and the operation's own outcome is unaffected.

## Guard proof

Pending.
