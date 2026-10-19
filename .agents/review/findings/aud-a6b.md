# aud-a6b: Disconnect-ExchangeOnline may tear down every borrower's connection, not one

**Severity**: HIGH if confirmed - it would mean the connection pool's central premise is
unsound, and that cleaning up one retired connection can break unrelated operators
mid-operation, after earlier mutations have already committed.
**Status**: CONFIRMED locally 2026-10-19 - not fixed; awaiting an owner decision on approach
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



## Verification, 2026-10-19 - CONFIRMED

Done locally against the installed module. No tenant, no AD, and no code from the module was
executed: the field check is a metadata-only read through `PEReader` /
`MetadataReader`, not `Assembly.LoadFrom`.

Installed: **ExchangeOnlineManagement 3.9.2** only (`C:\Program Files\WindowsPowerShell\Modules`).
3.10.1 is not on this machine, so that half of the reviewer's claim remains unchecked here.

**1. The default disconnect really does remove everything.**
`netCore/ExchangeOnlineManagement.psm1:945`, inside `Disconnect-ExchangeOnline`'s
`DefaultParameterSet` branch:

```powershell
[Microsoft.Exchange.Management.ExoPowershellSnapin.ConnectionContextFactory]::RemoveAllConnectionContexts()
```

`Services/ExoConnectionPool.DestroyRunspace` calls exactly that form -
`Disconnect-ExchangeOnline -Confirm:$false`, no selector.

**2. The map is static.** In
`netCore/Microsoft.Exchange.Management.ExoPowershellGalleryModule.dll`:

```
TYPE: Microsoft.Exchange.Management.ExoPowershellSnapin.ConnectionContextFactory
  FIELD connectionContextMap                Static=True
```

A static field lives in the assembly's load context, which is the **process**, not the
runspace. Every pooled runspace in this app shares one map.

**So the reviewer was right.** Destroying one retired runspace removes the connection context
of every other borrower in the process, including ones mid-operation. The pool's premise -
several concurrent EXO connections, independently disposable - does not hold as built.

**3. A targeted disconnect exists, but wiring it up is not trivial.**

`Disconnect-ExchangeOnline` has a `ConnectionId` parameter set (`psm1:810`) routing to
`GetModulesToRemoveByConnectionId` / `RemoveConnectionContextUsingConnectionId` instead of the
blanket removal. That is the shape a fix would use.

The obstacle: **`Connect-ExchangeOnline` generates the id itself and does not accept one**
(`psm1:413`, "Generate a ConnectionId to use in all logs and to send in all server calls"). So
the pool cannot assign an id per runspace; it has to read back the one that was generated.

`Get-ConnectionInformation` is exported by the module and returns active connections with
their `ConnectionId`. But **it reads the same process-wide static map**, so called inside a
runspace it returns every connection in the process, not that runspace's. Picking the right
row is the actual design problem - a before/after diff around the connect, or "newest", and
both are racy when two borrowers connect concurrently. The pool would likely have to
serialise connection establishment to attribute ids reliably.

## Recommendation to the owner

Confirmed, and the repair is a connection-pool design change, not a patch. Options, cheapest
first:

1. **Serialise EXO work.** One connection, a real lock around borrow. Removes the defect
   outright and is by far the smallest change. Costs the concurrency the pool exists for.
2. **Attribute ConnectionIds and disconnect by id.** Keeps concurrency. Needs connection
   establishment serialised anyway to attribute ids safely, plus a new field on
   `PooledRunspace` and a changed destroy path.
3. **Stop disconnecting entirely and just dispose the runspace.** Leaks the server-side
   session until it times out, and leaves the static map holding dead contexts. Not
   recommended, recorded because it is the "do nothing" option and it is not free.

Nothing is reproduced against a live tenant in any of this; it is a static reading of the
shipped module plus the code that calls it.

## Closeout

Verified, not fixed. Awaiting an owner decision on which option, per the plan's
`W1F-A6B` entry, which already said to stop here.
