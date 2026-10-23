# aud-a45: ConfigChangeWatcher.LogOnce calls the reader's logger unguarded, so a throwing logger reaches every cache-hit reader

**Severity**: MEDIUM - the aud-a28q/a28z class in shared storage code. `Services/Storage/ConfigChangeWatcher.cs`
`LogOnce` reports an unreadable change token through the caller's logger with no guard; if the provider
throws, `HasChangedSince` throws into every reader that hits its cache: `ADAttributeEditorService` (two
sites), `PermissionValidator`, `ProtectedPrincipalService`, `UsageTelemetryService`. aud-a28z guarded
`ExtendedLogService`'s own call; one guard in `LogOnce` covers all of them.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28z/a26u/cdt-41 agent, 2026-10-22, candidate 1 of its hand-back)

## Evidence

At `36da9789`: `Services/Storage/ConfigChangeWatcher.cs` around line 120 (`LogOnce`); callers at
`Services/ADAttributeEditorService.cs` 216 and 754, `Services/PermissionValidator.cs` 639,
`Services/ProtectedPrincipalService.cs` 373, `Services/UsageTelemetryService.cs` 79-110 (tip line numbers of
the a28z branch).

## Approach (to be written by the implementing agent before code)

- `LogOnce` wraps its logger call in the aud-a28u nested guard; `HasChangedSince` never throws for a logging
  failure (an unreadable token still reads as "changed", fail closed on caching - keep that).
- Test: the aud-a28q throwing-logger theory extended to `ConfigChangeWatcher`; a reader with a throwing
  logger and an unreadable token still answers. Mutation at the call site: the guard removed.

## Guard proof

Pending.
