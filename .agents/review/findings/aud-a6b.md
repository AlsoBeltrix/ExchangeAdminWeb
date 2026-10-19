# aud-a6b: Disconnect-ExchangeOnline may tear down every borrower's connection, not one

**Severity**: HIGH if confirmed - it would mean the connection pool's central premise is
unsound, and that cleaning up one retired connection can break unrelated operators
mid-operation, after earlier mutations have already committed.
**Status**: Open - REPORTED BY A REVIEWER, NOT VERIFIED BY ME
**Branch**: -
**Commit**: -

## Evidence

Raised by codex-commercial (gpt-6-astra, max) while reviewing `1dcc1a1`, unprompted - the
review question was about the A6 fix, and this came back as a larger problem underneath it.

The reviewer states they inspected the **installed** ExchangeOnlineManagement modules, versions
**3.9.2 and 3.10.1**, reading assembly metadata and IL rather than documentation, and found:

- `Disconnect-ExchangeOnline -Confirm:false` with no connection selector routes to
  `ConnectionContextFactory.RemoveAllConnectionContexts`
  (cited at `ExchangeOnlineManagement.psm1:945` in the 3.9.2 netCore module).
- `connectionContextMap` is **static**, and that method clears the tokens and then the map.
  It is not a per-runspace collection.

`Services/ExoConnectionPool.DestroyRunspace` calls exactly that form.

If the reading is right, the consequence is not subtle: a retired connection returning and
being destroyed removes the connection context of **every other borrower in the process**,
including ones mid-operation. Their next cmdlet fails, possibly after they have already
written changes.

**This is not something the A6 work caused, and moving destruction inside `_connectedLock`
would not help** - the damaged connections are ones already borrowed and in use, not ones
sitting in the pool.

## Why this is filed rather than fixed

1. **I have not verified it.** The claim rests on someone else's IL inspection of a module on
   this machine. The repo's own rule is that control-flow claims are settled by reading the
   file; I have not read it, and neither of us has reproduced the effect against a tenant.
2. **If true, the repair is a design change, not a fix.** Either every destroy names its
   connection (`Disconnect-ExchangeOnline -ConnectionId`, which Microsoft documents for REST
   connections) or the pool stops holding concurrent connections. Both are well outside
   `docs/RepoAudit-Remediation-Plan.md`.
3. It needs an owner decision about scope before any code moves.

## Proposed next step

Verify first, in this order, stopping at the first answer:

1. Read the installed module at the cited path and confirm or refute the call chain. Cheap,
   local, no tenant.
2. If confirmed, check whether `PooledRunspace` can carry a `ConnectionId` and whether
   `Disconnect-ExchangeOnline -ConnectionId` exists in the installed version.
3. Report to the owner with both answers before proposing a change.

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19, reviewing `1dcc1a1`. Their own
caveat, recorded: the finding is based on static implementation inspection, with no live
tenant reproduction.

## Closeout

Not started.
