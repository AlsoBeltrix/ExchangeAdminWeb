# utei-3: The kill-switch read happens on the caller's path, so telemetry can delay an audited operation

**Severity**: MEDIUM - the plan's non-interference rule ("a telemetry write runs off
the caller's path... must not change, delay or mask the operation that triggered it")
is broken before the fire-and-forget hand-off: every audited operation pays a
synchronous config-store read, and a locked shared database can stall it for up to the
5-second SQLite busy timeout.
**Status**: Admitted
**Branch**: -
**Commit**: the commit that completes this record; read it from `git log -1 -- .agents/review/findings/utei-3.md`

## Evidence

`Services/AuditService.cs:434-440` calls `_usage?.RecordAction(...)` synchronously at
the end of every audit write. `Services/UsageTelemetryService.cs:167-174` (`Record`)
calls `Enabled()` BEFORE `Enqueue`, and only `Enqueue` uses `Task.Run`.
`Services/UsageTelemetryService.cs:70-90` (`Enabled`) calls
`_moduleConfig.IsModuleCorrupt` and `_moduleConfig.GetValue`, which read the config
store (`ModuleConfigService` has no cache). `Services/Storage/SqliteConnectionFactory.cs:74-79`
sets `PRAGMA busy_timeout=5000` on each connection. Since `SharedConfigDb` landed, that
store is the file the OTHER instance also writes.

## Predicted observable failure

Prod performs an audited mailbox-permission change while dev is mid-write on the shared
config database (or a deploy-time backup holds the file). The operator's action does not
return until the telemetry kill-switch read acquires the lock - up to 5 seconds added per
audited operation, and once per audited operation in a bulk loop. Nothing has failed and
nothing is logged; the app is simply slower because of a feature that is required to be
invisible.

## What

Two reads were treated as free because they are "just config": the corruption check and
the value read. They are SQLite connections on the request path, and the shared-database
decision made contention across processes a real case.

## Approach

TBD - fix commit will fill this in.

## Files changed

TBD

## Guard proof

TBD

## Coder dispute (if any)

None. Note the fix must keep the fail-closed reading from utei-2 and must keep the
Home disclosure consistent with what is actually collected.

## Known gaps

TBD

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  (same dispatch as utei-1)
Harness: codex-cli 0.153.2. Reviewed `e33b201~1..dea899b`, verdict `findings`,
capability_ok true. Envelope `.agents/review/ute-impl.result.json`.
