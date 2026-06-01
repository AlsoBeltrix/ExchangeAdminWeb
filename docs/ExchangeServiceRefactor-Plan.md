# ExchangeService ThreadStatic Refactor Plan

## Problem

`ExchangeServiceBase` and `ExchangeService` use `[ThreadStatic] static bool ConnectionErrorFlag` to signal whether a pooled EXO runspace should be discarded after a connection error. This flag is set in `Invoke()` / `InvokeOptional()` when a PowerShell error looks like a connection failure, then read by the calling `RunAsync` / `RunPooledQueryAsync` / `RunPooledBatchAsync` to decide whether to discard or return the runspace.

`[ThreadStatic]` works correctly for `RunAsync` and `RunPooledQueryAsync` because their delegates are synchronous — the `Task.Run` callback runs entirely on one thread pool thread. But `RunPooledBatchAsync` wraps an `async` delegate:

```csharp
await Task.Run(async () =>
{
    ConnectionErrorFlag = false;           // set on thread A
    await batchOperation(pooled.PowerShell); // may resume on thread B
    if (ConnectionErrorFlag) discard = true; // read on thread B — sees thread B's value, not A's
});
```

If a connection error occurs on thread A, the flag is set on A's thread-local storage. When the `await` resumes on thread B, the read sees thread B's default `false`. The broken runspace is returned to the pool instead of discarded, causing subsequent operations to fail with stale connection errors.

## Impact

- Bulk CSV operations (mailbox permissions, calendar permissions) that use `RunPooledBatchAsync` can silently return broken runspaces to the pool
- Subsequent unrelated operations inherit the broken runspace and fail
- The pool has only 2 slots (`ExoConnectionPool`), so one poisoned slot degrades half of all EXO operations

## Why It Has Survived

- Most operations use `RunAsync` / `RunPooledQueryAsync` (synchronous delegates) — the flag works correctly there
- `RunPooledBatchAsync` is only used for bulk CSV operations, which are relatively rare
- Connection errors themselves are relatively rare
- When the pool IS poisoned, the next operation fails, the runspace gets discarded on that failure, and the pool self-heals — the window of corruption is short

## Proposed Fix

Replace `[ThreadStatic]` with a captured local variable passed through the call chain. No `AsyncLocal` needed — the simpler approach is to stop using a static flag entirely.

### Approach: Local ConnectionErrorTracker

Create a small mutable wrapper that gets passed into `Invoke` and `InvokeOptional`:

```csharp
internal sealed class ConnectionErrorTracker
{
    public bool HasConnectionError { get; set; }
}
```

Then change `Invoke` and `InvokeOptional` from reading/writing a static field to reading/writing the tracker instance. The caller creates the tracker, passes it in, and reads the result — no cross-thread state.

### Changes Required

**ExchangeServiceBase.cs:**

1. Add `ConnectionErrorTracker` class
2. Remove `[ThreadStatic] protected static bool ConnectionErrorFlag`
3. Change `Invoke(PowerShell ps)` to `Invoke(PowerShell ps, ConnectionErrorTracker tracker)`
4. Change `InvokeOptional(PowerShell ps)` to `InvokeOptional(PowerShell ps, ConnectionErrorTracker tracker)`
5. Update `RunAsync` to create a local tracker, pass it to the delegate, read it after
6. Update `RunPooledQueryAsync` to create a local tracker, pass it to the delegate, read it after

**ExchangeService.cs:**

7. Remove `[ThreadStatic] private static bool ConnectionErrorFlag` (duplicate declaration)
8. Update all calls to `Invoke(ps)` → `Invoke(ps, tracker)` throughout the ~2800-line file
9. Update `RunPooledBatchAsync` to pass tracker through the async delegate
10. Update the monolith's own `RunAsync` / `RunPooledQueryAsync` copies to use tracker

**All services inheriting ExchangeServiceBase:**
- `MailboxPermissionService.cs`
- `CalendarPermissionService.cs`
- `MigrationService.cs`
- `RecipientLookupService.cs`
- `Comms10kService.cs`
- `ConferenceRoomService.cs`

These call `Invoke(ps)` directly inside their delegates. Each call site needs the tracker parameter added.

### Estimated Scope

| File | Approximate call sites |
|------|----------------------|
| ExchangeServiceBase.cs | 5 (Invoke, InvokeOptional, RunAsync, RunPooledQueryAsync, field) |
| ExchangeService.cs | ~40-50 (Invoke/InvokeOptional calls throughout the monolith) |
| MailboxPermissionService.cs | ~10 |
| CalendarPermissionService.cs | ~8 |
| MigrationService.cs | ~6 |
| RecipientLookupService.cs | ~4 |
| Comms10kService.cs | ~4 |
| ConferenceRoomService.cs | ~4 |

Total: ~80-90 call site changes, all mechanical.

### Alternative Considered: AsyncLocal

`AsyncLocal<bool>` would flow across `await` boundaries and fix `RunPooledBatchAsync` without changing any call signatures. However:

- It's implicit shared state, harder to reason about
- It requires careful `Value = false` reset at the right scope
- A captured local is simpler and more explicit
- The call signature change makes it impossible to forget the tracker at a new call site

### Testing Strategy

Existing tests don't cover the connection error flag path (they'd need a real or mocked EXO connection to trigger `IsConnectionError`). The refactor is mechanical — same logic, different storage mechanism. Verify by:

1. Build succeeds with zero warnings
2. All existing tests pass (265)
3. Manual smoke test: run a bulk CSV mailbox permission operation in dev
4. Code review: grep for any remaining `ConnectionErrorFlag` references

### Risk

Low. The change is mechanical (rename parameter threading), doesn't alter control flow, and the existing behavior for synchronous callers is preserved exactly. The only behavioral change is that `RunPooledBatchAsync` will now correctly discard broken runspaces, which is the desired fix.

### Execution

This is a single-session refactor. Recommend:
1. Do ExchangeServiceBase first (define tracker, update Invoke/InvokeOptional/Run helpers)
2. Do ExchangeService next (the bulk of call sites)
3. Do extracted services last (they're smaller)
4. Build, test, commit
