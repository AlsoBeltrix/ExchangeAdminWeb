# Comms-10k and Defender Remediation - Plan

Status: **DRAFT Revision 2 - awaiting owner approval.** Revision 1 was reviewed by codex on
2026-10-15 and returned "do not approve as written"; this revision takes every correction. Raised
by two whole-module reviews the same day. Twelve findings, eleven owner-ruled; a thirteenth
(codebase-wide comment audit) is excluded and gets its own plan.

## Exec summary

**What this does.** Closes twelve defects in two modules. The big one: Comms-10k can be pointed at
any AD group by a config field, and it skips the app's protection checks because it is assumed to
target one harmless list. This pins it to that one group permanently. The rest are a job that can
quietly drop people and still record success, a job that can write a different list from the one
you approved, Defender reading the device inventory before you have asked for anything, a shared
variable that lets two operators see each other's errors, and a subnet lookup doing millions of
redundant parses per load.

**What it gets you.** Comms-10k stops being able to touch anything except the group it exists for,
and writes nothing on a guess - if what it is about to write is not what you approved, it writes
nothing and says so. Defender opens instantly, and gets faster when it does read.

**What it costs.** Twelve slices plus tests, in a fresh session. Two manual checks on dev, because
no automated test can prove the group GUID resolves in the real forest or that a page renders.

**The biggest risk, and it is a behaviour change you are authorising.** Today Comms-10k lets you
proceed with a partially-matching file - "10,000 of 10,500 matched, 500 will be excluded, Replace".
Your ruling that the app must refuse rather than guess removes that path. After this, a file with
any unmatched address cannot be applied at all; you fix the file and re-upload. **Say so now if you
want that path kept.**

**A correction to what you were told when you ruled on C2.** You were told the risk was a job
writing after your access is revoked. That is wrong, and the code says so in its own words: the
job authorization snapshot "does NOT detect mid-job group-membership revocation". What it actually
catches is an admin removing the authorizing group from Comms-10k's allowed-group configuration
between submit and write. Narrower than described. Still worth the small cost, and it brings
Comms-10k to parity with ConferenceRooms, but you ruled on a wider claim than the facts support.

**Also deliberate:** pinning the group by GUID hardcodes an ADI-specific value in source, which
your own environment-neutrality rule forbids. You exempted yourself from that rule. Comms-10k will
not work in another forest after this.

**What approving authorises.** The twelve numbered slices below, nothing else. Not the
codebase-wide comment audit, not a bulk-jobs redesign beyond what C5 names.

---

## Owner rulings, 2026-10-15

| Id | Finding | Ruling |
|---|---|---|
| C1 | Target group is a free config field; no type or identity check | Pin by ObjectGUID; delete the config field |
| C2 | Background replace never re-checks authorization | Fix; low priority, cheap. **Scope corrected - see exec summary** |
| C3 | Partial resolve writes a smaller set and audits success | **Refuse, write nothing, report** |
| C4 | Job may write a set the operator never approved | **Refuse, write nothing, report** |
| C5 | Submitter may be unable to see or manage their own job | Must be fixed; redesign in scope if needed |
| C6 | Ticket edited after Validate is ignored | Fix |
| C7 | Enqueue reads the address list after awaits | **Not yet ruled** - found by plan review, same class as C4 |
| D1 | Page fetches the inventory on open | No fetch on open. **No filter requirement** - a blank Search still pulls everything |
| D2 | Singleton service holds a per-run failure reason in a field | Fix |
| D3 | No guard against two concurrent loads | Fix |
| D4 | Audit omits Site and Seen-by | Fix |
| D5 | Subnet re-parsing per device; filter recomputed per render | Fix |
| D6 | Comments across the codebase are unreliable | **Separate plan.** Exception: stale comments inside code a slice touches are fixed by that slice |

C3/C4 ruling verbatim: *"the onus is on the user to provide accurate data. If they do not, they
need to know that and resolve it themselves. We do not guess."* Durable rulings are in
`.agents/decisions.md`, 2026-10-15.

---

## Constraints

- **The Comms-10k exemption is narrower than its justification.** `docs/ProjectConstitution.md`
  exempts this module from protected-principal member AND write-target checks because it targets
  "one broadcast list whose membership grants access to nothing". The real group is a Universal
  **Security** group. C1 is what makes that premise true in code rather than in configuration.
  **Do not widen the exemption and do not remove it; it is an owner ruling, 2026-09-30.**
- Environment neutrality (`.agents/repo-guidance.md` invariant 7) is waived for C1's GUID only.
- Module-scoped version bumps only; no base app bump unless a slice touches shared code.
- One finding per commit. Full gate before each: Release build, `dotnet test
  ExchangeAdminWeb.slnx`, `dotnet format --verify-no-changes`, `git diff --check`,
  `tools/Test-AsciiOnly.ps1`.
- Every new test mutation-proven. Restore with `git apply -R` / `cp` plus `touch`, md5-verify.
  **Grep builds for `error CS`; do not tail them** - a tailed build hid 35 errors on 2026-10-14
  and `--no-build` then reported a stale pass.
- `Components/Pages/DefenderEndpointDevices.razor` is ClickGate-registered. Registered controls
  are all above line 474 (`@code` start). An edit below moves only `ExpectedLineCount`; an edit
  above needs `.agents/playbooks/clickgate-reanchor.md`. `Comms10k.razor` has no line-keyed
  entries.
- **Line numbers in this plan are as of `8f61ae3` and will drift as slices land.** Re-locate by
  symbol, not by line.

---

## Slice C1 - pin the target group by ObjectGUID

**Target:** `87663f48-10a7-44b3-8da3-0a7e4cc3de0b` - `CN=Comms-10k,OU=Automated
Groups,OU=Recipients,OU=Analog,OU=Exchange,DC=ad,DC=analog,DC=com`, Universal Security.

GUID over name, DN or SamAccountName because it survives rename and move.

- `Services/Comms10kService.cs` - the `TargetGroupName` config read becomes the constant; the
  "not configured" throw becomes a fail-closed throw when the GUID does not resolve.
- `QueryTargetGroup` looks the group up by name today and reads DN/GUID back. Invert: look up by
  GUID, fail closed when the lookup returns nothing or more than one.
- `Modules/ModuleCatalog.cs` - delete the `TargetGroupName` config field.
- `Components/Pages/Comms10k.razor` - "Set TargetGroupName in Admin Settings" is no longer
  actionable; name the fixed target and say it is not configurable.
- Existing `Comms10k/TargetGroupName` config rows become dead. Leave them; the schema rule is
  additive-only (`.agents/repo-guidance.md` invariant 2).

**Tests:** the service refuses when the GUID does not resolve; the catalog no longer exposes the
field; no code path reads a group name from config.

**Manual, on dev:** confirm the module resolves its group and lists members. **No automated test
can prove the GUID resolves in the real forest** - say so in the commit rather than implying
coverage.

## Slice C3 - a partial resolve writes nothing

`Services/Jobs/Comms10kReplaceProcessor.ResolveAsync` returns `Partial` when addresses are dropped
**and still sets `_resolvedDns`**, so `ReplaceAsync` writes the smaller set while its mutation
audit is keyed to `result.Success`. The row says Partial; the audit says success.

- Set `_resolvedDns` **only when every uploaded address resolved**. Any rejection is `Failed`, not
  `Partial`, naming the count and reason. `ReplaceAsync`'s existing `_resolvedDns is null` branch
  then refuses - it already exists and already says "nothing was written".
- The page stops offering the partial path. Replace is offered only when there are no rejections
  at all; otherwise the page says fix the file and re-upload, with the errors CSV that is already
  reachable from every state.

**Tests:** one rejection blocks the write. **Assert `ExecuteReplaceAsync` was never called** -
that is the assertion that matters, not the status string.

## Slice C4 - the job writes the approved set or nothing

**Revision 1 proposed comparing the confirmed COUNT. That is too weak and was rejected at review:
the same count with different people passes.**

Carry a fingerprint of the approved resolved set in `Comms10kReplaceJobPayload` - an ordered,
normalised hash of the resolved DNs the operator confirmed. The job still re-resolves (keep that;
the payload's own remarks give the reason and it remains true), then compares its own resolved set
against the fingerprint. Any difference is a refusal, not a write.

A fingerprint rather than the DN list itself for the reason the payload already states: ten
thousand distinguished names is a large durable record of something derivable in seconds. A hash
is small and answers the only question being asked.

**Tests:** a job whose re-resolve yields a different set of the same size refuses; an identical
set writes. Assert `ExecuteReplaceAsync` was never called on the refusal path.

## Slice C7 - capture the payload above the first await

**Found by the plan review, not yet owner-ruled.** `Comms10k.razor.ExecuteReplace` captures
`validatedTicket` into a local above its `await Task.Yield()`, then reads the fields
`parsedEmails` and `uploadedFileName` several awaits later when building the payload. A queued
upload event landing in that window enqueues a different list than the one approved - and because
it can be the same size, C4's fingerprint is what catches it, which is why both are needed.

The page applies this discipline to the ticket and not to the payload. Capture both fields into
locals beside the ticket.

**Test:** fields mutated after the yield do not reach the payload.

## Slice C2 - re-check the section's allowed groups at the point of write

**Scope, corrected at review:** `Services/Jobs/JobAuthorizationSnapshot` re-checks the submitter's
captured group claims against the section's CURRENT allowed-group set. Its own summary says it
"does NOT detect mid-job group-membership revocation", and neither does the live model, so this is
parity rather than a new guarantee. **Do not describe this slice as catching a revoked user.**

`ConferenceRoomBulkProcessor` reads `JobAuthorizationSnapshot.FromJson(job.AuthSnapshotJson)` and
re-evaluates per row before writing. `Comms10kReplaceProcessor` has no authorization code at all,
while `BulkJob.AuthSnapshotJson` already exists and is already persisted.

- Populate `AuthSnapshotJson` when Comms-10k enqueues.
- Evaluate it before `ExecuteReplaceAsync`, failing closed on an absent or unreadable snapshot.

Follow ConferenceRooms' shape exactly; do not invent a second pattern.

**Tests:** a job whose authorizing group is no longer in the section's allowed set refuses and
writes nothing; an absent snapshot refuses.

## Slice C6 - the ticket at submit must be a validated ticket

**Revision 1 said "re-read the ticket at Confirm". That is wrong and was rejected at review:** the
ticket is ServiceNow-validated at Validate, so re-reading an edited value at Confirm would carry an
unvalidated ticket into the job, audit and notification.

Two acceptable shapes; the implementing agent picks and records why:

- **(a)** Lock the ticket field once Validate has run, so the validated value is the only one that
  can be submitted. Changing it requires re-validating.
- **(b)** Re-read at Confirm and re-validate before enqueue, refusing on a failed validation.

(a) is smaller and matches the module's "refuse rather than guess" posture.

**Test:** a ticket edited after Validate cannot reach the payload unvalidated.

## Slice C5 - the submitter can see and manage their own job

**Owner ruling: "any case where a user can start a job that ends up here but not see it or manage
it is a failure."** Redesign in scope.

**This slice is not approved for implementation by this plan.** It needs a design note first -
that was Revision 1's weakest point and the review named it. What the note must settle, with the
facts the review surfaced:

- `Services/Jobs/BulkJobService` already has module-scoped and submitter-scoped read APIs, so the
  read side is largely there.
- **But `CancelJob` and `GetRows` are id-only and `DeleteJob` is module-scoped.** "Manage" cannot
  be bolted on without deciding the scoping rule for each, or a Comms-10k view inherits the admin
  panel's cross-module reach.
- **Do not widen `AdminBulkJobs`' permission** - its fail-closed gate exists for cross-module
  aggregation and that reasoning stands.

Options: (a) a Comms-10k-only view filtered to `SubmittedBy == current user` and that job type;
(b) a shared "my jobs" component for any submitting module. Recommend (a), extracting later.

## Slice D1 - do not read the inventory on open

`Components/Pages/DefenderEndpointDevices.razor.OnAfterRenderAsync` calls `LoadAsync()` on first
render, while the form text says "Set what you are looking for, then Search". Remove the
auto-load; the page opens on the form with no result.

**Precision, corrected at review:** the default form is not blank - `OnboardingStatus` defaults to
`CanBeOnboarded`. The auto-load is therefore a large default search, not literally every device.
**Do not change the default filter semantics in this slice**; that would be a separate behaviour
change and is not approved.

**No filter is required to press Search** (owner ruling). A blank Search pulling everything is the
supported way to get everything.

The `result == null` branch already renders the no-result state; its comment explains the deferred
first load and will be false - rewrite it.

**Test:** the page does not call the service on first render. **Honest limit:** a lifecycle
assertion, not a rendered-page one; there is no bUnit harness.

## Slice D2 - the shared failure reason

`Program.cs` registers `DefenderEndpointDeviceService` as a singleton.
`Services/DefenderEndpointDeviceService` declares `_lastBatchFailureReason` as a mutable instance
field, written inside `TryEnrichBatchAsync` and read immediately after. Two concurrent runs
interleave and one reports the other's reason.

Return the reason from `TryEnrichBatchAsync` instead of storing it. Delete the field.

**Sweep the rest of the class for the same shape before calling this done.** A singleton with one
mutable run-scoped field usually has more than one.

**Test:** two concurrent `ListDevicesAsync` calls with different batch failures each report their
own reason.

## Slice D3 - one load at a time

`LoadAsync` raises `isLoading` only after it has started; nothing refuses a second entry. A
double-click or an overlapping dispatch runs two reads racing to replace `result`.

Add a server-side re-entry guard that refuses the second call outright. The browser's copy of a
`disabled` attribute is one round trip stale - the stated reason the filters are snapshotted
server-side, and it applies here.

**Test:** a second `LoadAsync` while the first is in flight does not reach the service.

## Slice D4 - audit what actually narrowed the result, on both paths

`DescribeFilters` takes `DefenderDeviceFilters`, which carries no Site or Seen-by - those are page
fields applied after the fetch, and they decide what the CSV contains. Both lookup audit calls
therefore describe a wider question than the operator asked.

**Added at review: the CSV export audit has the same gap and is worse.** The export audits the
action and a row count only, while the rows it writes are the filtered set. Include the applied
filters there too.

Mark Site and Seen-by as client-side in the record so it does not imply Defender was asked for
them.

**Tests:** a lookup audit and an export audit for a run with Site and Seen-by set both name them.

## Slice D5 - stop recomputing

Two independent costs:

1. `Services/AdSiteSubnetMap.FindSite` loops the subnets and calls `Parse(subnet)` inside the
   loop; `ApplySites` calls `FindSite` once per device. The cost is **parsed subnets per device** -
   with a tenant-sized inventory and a forest-sized subnet list that is millions of redundant
   parses per load. Parse once into a prepared list and pass that to the matcher. **Measure the
   real subnet count before quoting one in the commit; this plan does not assert it.**
2. `Components/Pages/DefenderEndpointDevices.razor.FilteredDevices` is an uncached property
   scanning the whole result, read from six sites including three in markup that re-render on any
   state change. Compute once and **invalidate on `result`, `appliedSite` and `appliedSeenBy` -
   all three.** A stale cached filter is worse than the current recomputation.

**Behaviour must not change.** The `seenByWasEvaluated` escape and the Site `(unavailable)` escape
both survive.

**Revision 1 said the existing filter tests must pass untouched. That is wrong and was rejected at
review:** those guards are source-shape assertions about `FilteredDevices`, so a correct caching
refactor may legitimately require rewriting them. The invariant is that the *behaviour* they
describe still holds - re-derive each assertion against the new shape and say so in the commit.

**Test:** a subnet is parsed once per load, not once per device. A behavioural count assertion,
not a source scan.

## Slice D6-local - stale comments in the code these slices touch

The codebase-wide audit is a separate plan. **Its carve-out was too broad** - these are in code
these slices edit and are fixed with the slice that touches them:

- `Services/DefenderEndpointDeviceService` - the subnet-map remarks still say an AD read failure
  "FAILS THE RUN (plan Q10)"; `ApplySites` marks the column instead. Changed by `mde-1`; the
  comment was left asserting the opposite.
- Same file - "One query per refresh, never one per device" predates batching; the code issues a
  posture and an observer request per batch.
- `Components/Pages/DefenderEndpointDevices.razor` - the CSV projection comment says twenty-seven
  columns; the header has twenty-nine.

Fix each in the slice that touches its file. Do not start a wider sweep here.

---

## Sequencing

C1 first - it is what stops the module touching the wrong thing. Then C3, C4, C7 (C4 and C7 are
two halves of one guarantee and should land adjacent), C2, C6. C5 only after its design note.
D1-D5 are independent of the Comms work and of each other; do D2 before D5 so the concurrency fix
is not written on top of a caching change. D6-local rides its slice.

## Verification

Per-slice gate as under Constraints. After the last slice, one full-suite run at the final head.
`Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps` only if a `.ps1` or `.psm1`
changed - none is expected.

**Not covered:** that any page renders. No bUnit harness exists. The Comms-10k target pin and the
Defender open-without-fetch both need a manual check on dev.

## Pending decisions

1. **The partial-file path is being removed.** Confirm or veto at approval.
2. **C7 has not been ruled on** - it was found by the plan review. It is the same class as C4 and
   the plan assumes the same answer; say if not.
3. **C5 needs a design note before implementation** and is not approved by this plan.
