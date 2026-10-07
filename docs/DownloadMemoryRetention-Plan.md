# Download Memory Retention - Plan

Status: **DRAFT - awaiting owner go, and DEPENDENT ON A MEASUREMENT NOT YET TAKEN.**

**`docs/ProductionMemory-Plan.md` is the parent and owns the ORDER - read it first.** This is
step 3 of that sequence and is GATED on step 0.s measurement and the re-measurement after
step 2.

---

## Exec summary

*This is the only part of this plan the owner reads or is bound by
(`.agents/decisions.md` 2026-09-30, 2026-10-01). Everything below it is agent-facing.*

**What it does.** Changes how the app hands a file to the browser. Today every export - CSV,
zip, report - is converted into a text form and held whole in memory before being sent down
the connection, costing roughly three times the file's size in one go. This streams the file
instead, and puts an upper limit on how much memory the app is allowed to take.

**What it gets you.** This is the best explanation found for the SIZE of the problem. The
leak in the other plan explains why memory never comes back down; this explains why there is
so much of it. A 10 MB export currently costs something like 70-90 MB of memory in a single
click, and the app is configured to keep rather than return it.

**What it costs.** The largest of the three plans. Thirteen pages download files and all of
them use the same mechanism, so this is a repeated change across the UI plus a new way of
serving files. It is the one with real regression risk: downloads are something operators do
constantly, and getting it wrong breaks a visible, frequently used thing.

**Biggest risk.** This is NOT a bug. It is the default behaviour of a server application
with no memory ceiling, doing what it was told. Changing it is a performance and
configuration decision, not a defect repair - which means it could be wrong, and it could be
unnecessary. See the next paragraph.

**One thing that should change your answer: this plan is not yet justified by evidence.** I
have not measured whether the memory in question is the kind this plan addresses. That
measurement takes twenty seconds and needs an elevated shell - it is the counters step in the
script already on your machine. **If it comes back saying most of the memory is NOT managed,
this plan is aimed at the wrong thing and should be discarded rather than approved.** I would
rather say that now than have you approve a week of UI work on a hunch.

**What approving authorises:** nothing yet, by design. Read this as "here is the shape of the
fix if the measurement supports it". The honest sequence is measure, then approve.

---

## The mechanism

`Components/App.razor:103` defines the one download path the whole app uses:

```js
function downloadFile(filename, contentType, base64)
```

**Thirteen pages** call it. Per export the allocation chain is:

`StringBuilder` -> `sb.ToString()` (UTF-16, 2x the byte count) -> `Encoding.UTF8.GetBytes`
-> `Convert.ToBase64String` (a further 1.33x, again UTF-16, so **2.67x** the original bytes)
-> SignalR's JSON serialisation of that string.

Anything over ~42 KB lands on the Large Object Heap, which is not compacted by default. Shape
is visible at `MessageTrace.razor:953-957` and `:1106-1110`.

**Largest uncapped producers:** `Comms10k.razor:220-223` `DownloadFull` (~10,000 AD members;
note `LoadPreview:206` passes `limit: 25` and the download path passes none),
`AdminEventLog.razor:1132-1134`, `DefenderEndpointDevices.razor:735-737`.

**The GC configuration that turns churn into a ratchet.** `ExchangeAdminWeb.csproj` sets
none of `ServerGarbageCollection`, `ConcurrentGarbageCollection` or `GCHeapHardLimit` -
verified, the properties are absent - so the Web SDK default `System.GC.Server: true` applies
and appears in the published `runtimeconfig.json`. `web.config` uses `hostingModel="inprocess"`,
so this is w3wp's own heap on a 32 GB box with no container limit. Server GC allocates
per-core heaps and decommits lazily; with no hard limit it has no reason to give memory back.

**This is retention, not a leak.** The memory is reclaimable and would be returned under
pressure. That distinction matters: it means this cannot by itself exhaust the machine, but
it can sit at 13 GB indefinitely and make everything else - including another process, or a
diagnostic tool - fail for want of RAM. Which is exactly what happened on 2026-10-07.

## The measurement this plan waits on

Run the counters step of `capture-exchangeadminweb-memory.ps1` (elevated) against the
production worker and compare GC heap size against ~15 GB private bytes:

- **GC heap large (>8 GB):** managed retention. This plan is justified; proceed.

The reading must capture ALL of: process private bytes, managed GC heap size, Gen2 size, LOH
size and allocation rate - and preferably a gcdump object-type breakdown. Private bytes alone
cannot distinguish these cases, and "GC heap size" alone cannot tell LOH churn from a large
live object graph.
- **GC heap small (<2 GB):** the memory is native. This plan is aimed at the wrong thing.
  Discard it and investigate native sources - the long-lived singleton AD runspace at
  `Services/ADDirectorySearchService.cs:486-501` is the standing candidate.
- **In between:** both stories are partly true; sequence this after the leak fix and
  re-measure.

Do not approve this plan before that number exists.

## Scope, if justified

**S1 - a hard memory ceiling. NOT a first slice, and not automatic.** Review finding, and it
is right: `web.config` runs `hostingModel="inprocess"`, so a `GCHeapHardLimit` applies INSIDE
w3wp rather than around it. If the retained memory is still being leaked, or if the pressure
is native, a managed cap does not address the cause - it converts host starvation into
`OutOfMemoryException`, dropped circuits and possible app-pool instability. Trading a slow
machine for a failing app is not obviously the better outcome.

So: a heap limit is an OPERATIONAL OPTION requiring explicit owner and ops acceptance,
sizing against what else runs on the host, a staged rollout and a rollback. Preferably AFTER
the leak fix, the bounded queries and streaming downloads have had their effect measured -
not before.

**S2 - stream downloads instead of base64.** Replace the JS-interop string with a streamed
response - a minimal API endpoint or `IJSStreamReference` - so the payload is never held
whole, let alone at 2.67x. One page first as the pattern, then the rest one page per commit.

**S3 - cap the uncapped producers.** `Comms10k.DownloadFull` is the clearest: the preview
path already passes `limit: 25` and the download passes none. A download of 10,000 rows may
be exactly what the operator wants, so this is a bound on memory held at once, not on rows
delivered - streaming (S2) is what makes that possible.

## Out of scope

- The circuit leak and the unbounded job query; their own plans.
- Changing `System.GC.Server` to workstation GC. It would reduce footprint and cost
  throughput on a multi-core server. Raised as an open question rather than assumed.

## Verification

Standard gates. Beyond them, this plan's real verification is measurement, not tests:
private bytes before and after under comparable traffic. A green suite proves the downloads
still work; it proves nothing about memory.

Per-page manual acceptance: every changed download must be exercised - correct filename,
correct content type, correct bytes, and a large one to prove the streaming path.

## Owner gate

**Not a go/no-go yet.** The ask is: run the measurement. The go/no-go follows the number.
