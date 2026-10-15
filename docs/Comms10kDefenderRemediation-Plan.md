# Comms-10k and Defender Remediation - Plan

Status: **DRAFT - awaiting owner approval.** Raised by two whole-module reviews on 2026-10-15
(`.agents/review/out/c10k-whole.txt`, `.agents/review/out/mde-whole.txt`). Eleven findings, all
owner-ruled 2026-10-15. A twelfth (codebase-wide comment audit) is excluded and gets its own plan.

## Exec summary

**What this does.** Closes eleven defects in two modules. The big one: Comms-10k can be pointed at
any AD group by a config field, and it skips the app's protection checks because it is assumed to
target one harmless list. This pins it to that one group permanently. The rest are a background job
that can write after your access is revoked, a job that can quietly drop people and still record
success, a shared variable in Defender that lets two operators see each other's errors, Defender
pulling 27,000 devices before you have asked for anything, and a subnet lookup doing about 4.5
million redundant parses per load.

**What it gets you.** Comms-10k stops being able to touch anything except the group it exists for.
Nothing gets written on a guess - if the job's own lookup disagrees with what you approved, it
writes nothing and tells you. Defender opens instantly instead of reading the whole tenant, and
gets faster when it does read.

**What it costs.** Roughly nine slices plus tests, in a fresh session. One manual check on dev for
the Comms-10k target pin, because no automated test can prove the GUID resolves in the real forest.

**The biggest risk, and it is a behaviour change you are authorising.** Today Comms-10k lets you
proceed with a partially-matching file - "10,000 of 10,500 matched, 500 will be excluded, Replace".
Your ruling that the app must refuse rather than guess removes that path. After this, a file with
any unmatched address cannot be applied at all; you fix the file and re-upload. That is the
intended consequence of the ruling, and it is the one thing here that changes what you can do day
to day. **Say so now if you want that path kept.**

**Also deliberate:** pinning the group by GUID hardcodes an ADI-specific value in source, which
your own environment-neutrality rule forbids. You exempted yourself from that rule on 2026-10-15.
Comms-10k will not work in another forest after this.

**What approving authorises.** The nine numbered slices below, nothing else. Not the comment audit,
not a bulk-jobs redesign beyond what C5 names.

---

## Owner rulings, 2026-10-15

| Id | Finding | Ruling |
|---|---|---|
| C1 | Target group is a free config field; no type or identity check | Pin by ObjectGUID; delete the config field |
| C2 | Background replace never re-checks authorization | Fix; low priority, cheap |
| C3 | Partial resolve writes a smaller set and audits success | **Refuse, write nothing, report** |
| C4 | Job re-resolves and may write a set the operator never approved | **Refuse, write nothing, report** |
| C5 | Submitter may be unable to see or manage their own job | Must be fixed; redesign in scope if needed |
| C6 | Ticket edited after Validate is ignored | Fix |
| D1 | Page fetches the whole tenant on open | No fetch on open. **No filter requirement** - a blank Search still pulls everything |
| D2 | Singleton service holds a per-run failure reason in a field | Fix |
| D3 | No guard against two concurrent loads | Fix |
| D4 | Audit omits Site and Seen-by | Fix |
| D5 | ~4.5M subnet parses per load; filter recomputed per render | Fix |
| D6 | Comments across the codebase are unreliable | **Out of scope here** - separate plan, agent fan-out |

The C3/C4 ruling verbatim: *"the onus is on the user to provide accurate data. If they do not, they
need to know that and resolve it themselves. We do not guess."*

---

## Constraints

- **The Comms-10k exemption is narrower than its justification.** `docs/ProjectConstitution.md`
  exempts this module from protected-principal member AND write-target checks because it targets
  "one broadcast list whose membership grants access to nothing". The real group is a Universal
  **Security** group (owner's `get-adgroup`, 2026-10-15). C1 is what makes the exemption's premise
  true in code rather than in configuration. **Do not widen the exemption and do not remove it; it
  is an owner ruling, 2026-09-30.**
- Environment neutrality (`.agents/repo-guidance.md` invariant 7) is waived for C1 only, by owner
  ruling 2026-10-15. Every other slice stays neutral.
- Module-scoped version bumps only; no base app bump unless a slice touches shared code.
- One finding per commit. Full gate before each: Release build, `dotnet test
  ExchangeAdminWeb.slnx`, `dotnet format --verify-no-changes`, `git diff --check`,
  `tools/Test-AsciiOnly.ps1`.
- Every new test mutation-proven. Restore with `git apply -R` / `cp` plus `touch`, md5-verify.
  **Grep builds for `error CS`; do not tail them** - a tailed build hid 35 errors on 2026-10-14 and
  `--no-build` then reported a stale pass.
- `Components/Pages/DefenderEndpointDevices.razor` is ClickGate-registered. Registered controls are
  all above line 474 (`@code` start). An edit below that moves only `ExpectedLineCount`; an edit
  above it needs `.agents/playbooks/clickgate-reanchor.md`. `Comms10k.razor` has no line-keyed
  entries ("tier 3, not approved" in `ClickGateRegistry.cs`).

---

## Slice C1 - pin the target group by ObjectGUID

**Target:** `87663f48-10a7-44b3-8da3-0a7e4cc3de0b` - `CN=Comms-10k,OU=Automated
Groups,OU=Recipients,OU=Analog,OU=Exchange,DC=ad,DC=analog,DC=com`, Universal Security.

GUID over name, DN or SamAccountName because it survives rename and move. SID survives both too but
is domain-scoped; the GUID is the object's identity.

- `Services/Comms10kService.cs:28` reads `TargetGroupName` from module config - replace with the
  constant. `:69` throws "not configured"; that message becomes wrong and is replaced by a
  fail-closed throw when the GUID does not resolve.
- `QueryTargetGroup` (around `:673`) looks the group up by name and reads DN/GUID back. Invert: look
  up by GUID, fail closed when the lookup returns nothing or more than one.
- `Modules/ModuleCatalog.cs:712` - delete the `TargetGroupName` config field.
- `Components/Pages/Comms10k.razor:43` - "Set TargetGroupName in Admin Settings" is no longer
  actionable; replace with a message naming the fixed target and saying it is not configurable.
- Existing `Comms10k/TargetGroupName` config rows become dead. Leave them; the schema rule is
  additive-only (`.agents/repo-guidance.md` invariant 2).

**Tests:** the service refuses when the GUID does not resolve; the catalog no longer exposes the
field; no code path reads a group name from config.

**Manual, on dev:** confirm the module still resolves its group and lists members. **No automated
test can prove the GUID resolves in the real forest** - say so in the commit rather than implying
coverage.

## Slice C3+C4 - refuse rather than write a set the operator did not approve

One mechanism, shipped together; C3 alone leaves C4's mismatch unhandled.

Today `ResolveAsync` (`Services/Jobs/Comms10kReplaceProcessor.cs:95`) returns `Partial` when
addresses are dropped **and still sets `_resolvedDns`**, so `ReplaceAsync` writes the smaller set
and its mutation audit is keyed to `result.Success`. The row says Partial; the audit says success.

- `ResolveAsync` sets `_resolvedDns` **only when every uploaded address resolved**. Any rejection is
  `Failed`, not `Partial`, naming the count and reason. `ReplaceAsync`'s existing `_resolvedDns is
  null` branch then refuses - it already exists and already says "nothing was written".
- **C4:** add the operator-confirmed count to `Comms10kReplaceJobPayload`. The job still re-resolves
  - keep that; the payload's own remarks give the reason and it remains true - then compares. A
  count differing from what was confirmed is a refusal, not a write.
- The page stops offering the partial path. `Components/Pages/Comms10k.razor` renders Replace when
  `resolveResult.ResolvedDns.Count > 0` and warns "N address(es) will be excluded". Replace is now
  offered only when there are no rejections at all; otherwise the page says fix the file and
  re-upload, with the errors CSV that `mde-7` made reachable from every state.

**Tests:** one rejection blocks the write; a confirmed-count mismatch blocks the write; a clean set
still writes. **Assert `ExecuteReplaceAsync` was never called** on both refusal paths - that is the
assertion that matters, not the status string.

## Slice C2 - re-check authorization at the point of write

`Services/Jobs/ConferenceRoomBulkProcessor.cs:78` reads
`JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson)` and re-evaluates before writing.
`Comms10kReplaceProcessor` has no authorization code at all, while `BulkJob.AuthSnapshotJson`
(`Services/Jobs/BulkJobModels.cs:80`) already exists and is already persisted.

- Populate `AuthSnapshotJson` when Comms-10k enqueues (`Comms10k.razor` around `:624`/`:653`).
- Evaluate it in `Comms10kReplaceProcessor` before `ExecuteReplaceAsync`, failing closed on an
  absent or unreadable snapshot.

Follow ConferenceRooms' shape exactly; do not invent a second pattern.

**Tests:** a job whose snapshot no longer authorizes refuses and writes nothing; an absent snapshot
refuses.

## Slice C6 - the ticket at submit, not at validate

`Comms10k.razor` captures `validatedTicket` at Validate (around `:596`) and the job, audit and
notification use it (around `:619`), while the field stays editable (`:86`). Re-read the ticket when
Confirm is pressed, above the first await - the same discipline Defender uses for its filter
snapshot.

**Test:** a ticket changed between Validate and Confirm reaches the payload.

## Slice C5 - the submitter can see and manage their own job

**Owner ruling: "any case where a user can start a job that ends up here but not see it or manage it
is a failure."** Redesign is in scope if that is what it takes.

The durable outcome lives in the bulk-jobs store, surfaced by `Components/Pages/AdminBulkJobs.razor`
- fail-closed behind its own permission because it aggregates every module's jobs across
section-access boundaries (`ModuleCatalog.cs` around `:1407`). The status bar drops the result once
the job finishes.

**Do not widen AdminBulkJobs' permission** - its fail-closed gate exists for that aggregation and
the reasoning is sound. Choose between:

- **(a) Per-module job view.** Comms-10k shows the submitter their own jobs and outcomes on the
  Comms-10k page, filtered to `SubmittedBy == current user` and that job type. Smallest change, no
  new permission surface, generalises later.
- **(b) A shared "my jobs" component** doing the same, dropped into any module that submits jobs.
  More work now, less duplication later.

Recommend (a), extracting the component only when a second module needs it. **This slice needs its
own design note before implementation** - it is the only one here that is not a defined edit.

## Slice D1 - do not read the tenant on open

`Components/Pages/DefenderEndpointDevices.razor:852` - `OnAfterRenderAsync` calls `LoadAsync()` on
first render, while the form text at `:56` and `:64` says "Set what you are looking for, then
Search". Remove the auto-load; the page opens on the form with no result.

**No filter is required to press Search** (owner ruling). A blank Search pulling the whole tenant is
the supported way to get everything.

The `result == null` branch at `:250` already renders the no-result state; its comment explains the
deferred first load and will be false - rewrite it.

**Test:** the page does not call the service on first render. **Honest limit:** a lifecycle
assertion, not a rendered-page one; there is no bUnit harness.

## Slice D2 - the shared failure reason

`Program.cs:210` registers `DefenderEndpointDeviceService` as a singleton.
`Services/DefenderEndpointDeviceService.cs:936` declares `_lastBatchFailureReason` as a mutable
instance field, written inside `TryEnrichBatchAsync` and read at `:888` immediately after. Two
concurrent runs interleave and one reports the other's reason.

Return the reason from `TryEnrichBatchAsync` instead of storing it - an out parameter or a small
result record. Delete the field.

**Sweep the rest of the class for the same shape before calling this done.** A singleton with one
mutable run-scoped field usually has more than one.

**Test:** two concurrent `ListDevicesAsync` calls with different batch failures each report their
own reason.

## Slice D3 - one load at a time

`LoadAsync` (`:865`) snapshots and raises `isLoading` only after it has started; nothing refuses a
second entry. A double-click or an overlapping dispatch runs two tenant reads racing to replace
`result`.

Add a server-side re-entry guard that refuses the second call outright. The browser's copy of a
`disabled` attribute is one round trip stale - that is the stated reason the filters are snapshotted
server-side, and it applies here.

**Test:** a second `LoadAsync` while the first is in flight does not reach the service.

## Slice D4 - audit the filters that actually narrowed the result

`DescribeFilters` (`:1117`) takes `DefenderDeviceFilters`, which carries no Site or Seen-by - those
are page fields (`appliedSite`, `appliedSeenBy`) applied after the fetch, and they decide what the
CSV contains. Both audit calls (`:905`, `:927`) therefore describe a wider question than the
operator asked.

Pass both to `DescribeFilters` alongside the filter record, marked as client-side so the record does
not imply Defender was asked for them.

**Test:** an audit record for a run with Site and Seen-by set names both.

## Slice D5 - stop recomputing

Two independent costs:

1. `Services/AdSiteSubnetMap.cs:64` - `FindSite` loops the subnets and calls `Parse(subnet)` inside
   the loop; `ApplySites` calls `FindSite` once per device. 167 sites x 26,811 devices is ~4.5M
   parses per load, all recomputing the same 167 results. Parse once into a prepared list and pass
   that to the matcher.
2. `Components/Pages/DefenderEndpointDevices.razor:645` - `FilteredDevices` is an uncached property
   scanning the whole result, read from six sites including three in markup that re-render on any
   state change. Compute once per load beside `result`; invalidate when `result` or the applied
   filters change.

**Behaviour must not change.** `mde-6`'s `seenByWasEvaluated` escape and the Site `(unavailable)`
escape both survive. The existing filter tests must pass untouched - if one needs editing, the
change altered behaviour and is wrong.

**Test:** `Parse` is called once per subnet per load, not once per subnet per device. A behavioural
count assertion, not a source scan.

---

## Sequencing

C1 first - it is what stops the module being able to touch the wrong thing. Then C3+C4, C2, C6. C5
last of the Comms-10k work because it needs a design note. D1-D5 are independent of all of it and of
each other; do D2 before D5 so the concurrency fix is not written on top of a caching change.

## Verification

Per-slice gate as under Constraints. After the last slice, one full-suite run at the final head,
plus `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps` only if a `.ps1` or
`.psm1` changed - none is expected.

**Not covered by any of this:** that any page renders. No bUnit harness exists. The Comms-10k target
pin and the Defender open-without-fetch both need a manual check on dev.

## Pending decisions

1. **The partial-file path is being removed** - see the exec summary. Confirm or veto at approval.
2. **C5 option (a) or (b).** Recommend (a). Needs a design note before implementation.
