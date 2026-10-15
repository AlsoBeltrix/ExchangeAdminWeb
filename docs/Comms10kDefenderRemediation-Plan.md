# Comms-10k and Defender Remediation - Plan

Status: **APPROVED Revision 6; implementation in progress, owner 2026-10-15: "approved. go."**
Approval covers all twenty findings and the Sol 6.1 implementation / independent Astra review
arrangement recorded in `.agents/decisions.md`. Five review rounds on the Portkey codex
harness (rounds 1-3 "do not approve", 4-5 "keep, amend"), then one round on codex-commercial /
`gpt-6-astra` / max, which found ten further defects - several more serious than anything in
Revisions 1-5 - and judged the sequencing wrong. **This revision is reordered around the write
path.** Performance work moved last.

Review outputs are machine-local and gitignored (`.agents/review/out/`), so **the findings below
are the record** - do not expect to read the envelopes.

## Exec summary

**What this does.** Fixes the module that replaces the membership of a company-wide group, then
the device inventory module. Twenty findings.

**Why it was reordered.** The last review found the write path itself unsafe in ways the earlier
plan never touched. The worst: **a failed write can be executed twice, and the step that exists
to verify the result can be what executes it.** Next to that, caching a filter to make a page
faster is not a priority.

**The three that matter most, none of them in Revisions 1-5:**

1. A retry after a failed membership clear can issue a **second** clear, because the failed call
   leaves its command queued on a reused object. The verification read then runs on that same
   queue and performs the pending write.
2. Pinning the group by GUID - the fix already ruled on - **does not finish the job**: every
   actual write still addresses the group by its name-path, so a moved or replaced group is
   still reachable.
3. The replace **empties the group and then refills it**. If it dies in between, the list is
   empty or partial, and the restart report says "Members written: 0" when the true number is
   unknown. Nobody is told, because the person who submitted it cannot see job outcomes - which
   is why **C5 is no longer deferred.**

**What it costs.** Twenty slices in a fresh session. Manual dev checks for the group pin and the
Defender open-without-fetch.

**The behaviour change being authorised**, unchanged from Revision 1: a file with any unmatched
address can no longer be applied; fix the file and re-upload. **Say now if that path should be
kept.** One wrinkle found late - the resolver currently manufactures false "ambiguous" results
for a single person (C10). Fixing that is a prerequisite, or good files get rejected.

**Also deliberate:** the GUID is hardcoded, which the environment-neutrality rule forbids. The
owner waived it. Comms-10k will not work in another forest.

**What approving authorises.** The twenty slices below. Not the codebase-wide comment audit, and
not the carried-forward items in the last section, which are recorded and not fixed.

---

## Owner rulings

| Id | Ruling | Date |
|---|---|---|
| C1 | Pin the target by ObjectGUID; delete the config field. Neutrality waived | 2026-10-15 |
| C3/C4 | **Refuse, write nothing, report.** *"The onus is on the user to provide accurate data... We do not guess."* | 2026-10-15 |
| C5 | A user who starts a job must be able to see and manage it | 2026-10-15 |
| C2, C6, D1-D5 | Fix | 2026-10-15 |
| D1 | No fetch on open; **no filter requirement** - a blank Search still pulls everything | 2026-10-15 |
| D6 | Separate plan; local instances fixed by the slice that touches them | 2026-10-15 |
| C7-C11, D7-D10 | Approved with Revision 6 | 2026-10-15 |

Durable rulings: `.agents/decisions.md`, 2026-10-15.

## Constraints

- **The Comms-10k exemption.** `docs/ProjectConstitution.md` exempts this module from BOTH
  protected-principal checks because its target is "one broadcast list whose membership grants
  access to nothing". The real group is a Universal **Security** group. C1 makes "one target"
  true in code; **no code can prove the access claim** - it rests on the owner ruling. **Do not
  widen or remove the exemption** (owner, 2026-09-30), and **do not reintroduce locking or
  protected-principal checks contrary to recorded decisions.**
- Environment neutrality waived for C1's GUID only.
- Module-scoped version bumps; no base app bump unless a slice touches shared code.
- **One finding per commit.** Full gate before each: Release build, `dotnet test
  ExchangeAdminWeb.slnx`, `dotnet format --verify-no-changes`, `git diff --check`,
  `tools/Test-AsciiOnly.ps1`.
- Every new test mutation-proven: revert the fix, watch it fail, restore, md5-verify, `touch`.
  **Grep builds for `error CS`; never tail them** - a tailed build hid 35 errors on 2026-10-14
  and `--no-build` then reported a stale pass.
- `DefenderEndpointDevices.razor` is ClickGate-registered; registered controls sit above line
  474. An edit below moves only `ExpectedLineCount`; above needs
  `.agents/playbooks/clickgate-reanchor.md`. `Comms10k.razor` has no line-keyed entries.
- **Every file:line below is as of `5f79340` and drifts as slices land. Re-locate by symbol.**
- **Verify each finding against the code before fixing it.** Only C9 and C1's DN gap were
  independently confirmed by the plan author; the rest are review-sourced with citations. A
  finding that does not reproduce is one to record as such, not to fix.

---

# Part 1 - the Comms-10k write path

## C9 - a failed write stays queued and can run twice

**The most serious defect found. Do this first.**

`Services/Comms10kService.cs:506` - `ClearMembers` does `ps.AddCommand(...)`, `ps.Invoke()`,
then `ps.Commands.Clear()`. `ErrorAction Stop` makes failures terminating, so a throw skips the
`Clear()` and leaves the command on the pipeline. The same `PowerShell` object is reused, so:

- the retry calls `AddCommand` again and **invokes two clears**;
- after retries are exhausted, `ReadBackMembers` appends its read to the same retained pipeline
  and **executes the pending mutation** - the step whose whole purpose is to verify without
  writing.

Fix: clear command state in a `finally`, or use a fresh `PowerShell` per operation while keeping
the runspace. Apply to every operation on that object, not only `ClearMembers`.

**Test:** the existing pure-writer tests cannot see this. Exercise the real SDK wiring with
harmless commands that throw, and assert the next operation's pipeline holds only its own
command.

## C1 - pin the target by ObjectGUID, and bind every write to it

**Target:** `87663f48-10a7-44b3-8da3-0a7e4cc3de0b` - `CN=Comms-10k,OU=Automated
Groups,OU=Recipients,OU=Analog,OU=Exchange,DC=ad,DC=analog,DC=com`, Universal Security.

- Replace the `TargetGroupName` config read at `Services/Comms10kService.cs:28` with the
  constant; make the "not configured" throw a fail-closed throw when the GUID does not resolve.
- `QueryTargetGroup` looks up by name today - invert to GUID, failing closed on zero or multiple.
- Delete the `TargetGroupName` field from `Modules/ModuleCatalog.cs:712`.
- `Components/Pages/Comms10k.razor:43` - "Set TargetGroupName in Admin Settings" is no longer
  actionable; name the fixed target and say it is not configurable.
- **The lookup is not the hole. `Comms10kService.cs:509`, the add and the read-back all address
  the group by `target.DistinguishedName`.** A GUID lookup followed by DN-addressed writes still
  hits whatever occupies that DN. **Pass the GUID to every group operation.**
- Dead `Comms10k/TargetGroupName` config rows stay; the schema rule is additive-only.

**Tests:** refusal when the GUID does not resolve; no code path reads a group name from config;
**every write command carries the GUID**. **Manual, on dev:** the module resolves its group and
lists members. No automated test can prove the GUID resolves in the real forest - say so.

## C10 - the resolver manufactures ambiguity from one person

**Prerequisite for C3.** `Comms10kService.QueryBatchCandidates:340` pools candidates without
de-duplicating directory objects, and `Comms10kAddressResolver.Resolve:213` treats multiple rows
as multiple matches. One person whose UPN sits in one 500-address batch and whose `mail` sits in
another returns the same object twice: both addresses become "Ambiguous: 2 matches".

Today that silently excludes a valid person. **After C3 it rejects the entire file.** Fixing C3
without this turns a quiet bug into a loud one.

Fix: de-duplicate by object identity before deciding ambiguity. An address-to-distinct-identity
index also removes the current full candidate scan per address.

**Test:** the same person reached by two different addresses in two different batches resolves
once and is not ambiguous. The existing duplicate-person test supplies one candidate and cannot
see this.

## C3 - a partial resolve writes nothing, and says so in the audit

`Comms10kReplaceProcessor.ResolveAsync:113` sets `_resolvedDns` even when rejecting, returns
`Partial`, and `ReplaceAsync:147` writes the smaller set while auditing on `result.Success`.
Root cause one level down: `Comms10kService.ResolveEmailsAsync:285` returns `Success = true`
with rejections present.

- **Change the meaning at the service**, or guard every caller. Patching only the processor and
  the page leaves the next caller free to reintroduce partial writes.
- Set `_resolvedDns` only when every address resolved. Any rejection is `Failed`.
- The page stops offering the partial path: Replace only with zero rejections; otherwise fix the
  file and re-upload, using the errors CSV already reachable from every state.
- **The refusal must be audited.** The null-`_resolvedDns` return at
  `Comms10kReplaceProcessor.cs:135` sits **before** the only mutation audit, so implementing the
  refusal literally replaces a misleading success record with **no record**. Write one failed
  mutation audit for every authorization, resolution or fingerprint refusal.
- **The completion notification must carry the reason.** `OnJobCompletedAsync:193` builds from
  `_result`, which a refusal never sets, so it would say "did not complete". Note this is the
  ADMIN notification - the uploader learns the reason via C5, not here.

**Tests:** one rejection blocks the write; a failed mutation audit exists; the notification names
the reason. **Assert `ExecuteReplaceAsync` was never called.**

**Additional C1 review case (2026-10-15):** if the initial lookup succeeds but the second
runspace's Open or Import-Module throws, the exception escapes ExecuteReplaceAsync, skips
the processor's result assignment and mutation audit, and yields a generic completion reason.
Cover thrown service-preparation failures as well as returned refusals: one failed audit and
the concrete failure reason in completion. Evidence and the control-flow limit are in `cdr-c1`.

## C4 - the job writes the approved set or nothing

The concrete identity, confirmation, primary-member refusal and older-payload choices are in
`.agents/review/cdr-c4-design.md` (Astra design pass, 2026-10-15). In particular, preserve the
GUID through the actual member add, not only the fingerprint; C1 pins the group alone.

Carry a fingerprint of the approved set in `Comms10kReplaceJobPayload`. The job re-resolves (keep
that - the payload's remarks give the reason) and compares.

- **Hash object GUIDs, not DNs.** A rename or move between validate and run changes the DN; the
  invariant ruled on is "the same people".
- **Normalise and sort before hashing** - membership is a set. (Revision 5 justified this by
  claiming the resolver guarantees no ordering. **That was false** - it preserves input order.
  The conclusion stands; the reason does not.)
- **Use `SHA256.HashData` or equivalent.** Revision 5 said "no .NET hash API", which was wrong -
  cryptographic APIs are fine. What is banned is process-randomised object/string hashing, which
  would never match across the circuit and the runner.
- **Primary-group exception:** `Comms10kReplaceWriter.Derive:316` adds existing primary-group
  members to the expected set and can report success with people absent from the upload. State
  the exception explicitly or refuse before writing.

**Tests:** a different same-size set refuses; **the same GUIDs with a changed DN still writes**;
an identical set writes.

## C11 - truthful reporting when a replace is interrupted

`Comms10kReplaceWriter.ExecuteSequence:233` **clears the membership and then adds in batches**,
so a failure or process death leaves the group empty or partial. That was accepted for speed and
is not being revisited. The reporting is what is unacceptable: restart reconciliation uses a
fresh processor whose notification reports **"Members written: 0"** when `_result` is absent
(`Comms10kReplaceProcessor.cs:214`). The real number is unknown.

Report an interrupted replace as interrupted with an unknown written count, never as zero.

**Test:** a job reconciled after restart reports unknown, not zero.

**Additional reproduction from C9's independent Astra review (2026-10-15):** the same written-count
reporting problem occurs without a restart. `Comms10kReplaceWriter.Derive` sets `MembersWritten`
to the requested set's count even when clear succeeds and the first add fails. For a request
of 10,000 and a confirmed empty read-back, the notification, audit and service log can therefore
claim 10,000 written. Cover this ordinary interrupted-write case in C11 too: distinguish the
requested count from actual/unknown results throughout those outputs. Preserve the existing
honest outcome and final-count reporting. Evidence is recorded in `cdr-c9`'s independent review.

## C5 - the submitter can see and manage their own job

Concrete service, ownership, paging and lifecycle choices are recorded in
`.agents/review/cdr-c5-design.md` (Astra read-only design pass, 2026-10-15). They stay within
this approved scope; re-read the touched symbols at implementation time.

**No longer deferred.** C11 is why: the list can be left broken and the person responsible cannot
currently discover it. Owner ruling: *"any case where a user can start a job that ends up here
but not see it or manage it is a failure."*

- `Services/Jobs/BulkJobService` already has module- and submitter-scoped reads. **But
  `CancelJob` and `GetRows` are id-only and `DeleteJob` is module-scoped** - decide the scoping
  rule for each rather than inheriting the admin panel's cross-module reach.
- **Do not widen `AdminBulkJobs`' permission**; its fail-closed gate exists for aggregation.
- Build a Comms-10k view filtered to `SubmittedBy == current user` and that job type, showing
  outcome and rows. Extract a shared component only when a second module needs it.
- A successful enqueue currently renders as `RefusedBeforeAnyChange` with the headline "Nothing
  was changed" (`Comms10k.razor:680`, `:719`). Fix that wording while here.

## C2 - re-check authorization at the point of write

Concrete capture and command-dispatch boundaries are in `.agents/review/cdr-c2-design.md`
(Astra design pass, 2026-10-15). A processor check before service preparation is not the final
write gate; distinguish denial before dispatch from a partly executed replace.

`ConferenceRoomBulkProcessor:78` re-evaluates `JobAuthorizationSnapshot` per row;
`Comms10kReplaceProcessor` has none, though `BulkJob.AuthSnapshotJson` already exists.

- **Honest scope:** the snapshot "does NOT detect mid-job group-membership revocation" (its own
  summary). It catches the authorizing group being removed from the section's allowed set. Do
  not describe it as catching a revoked user.
- **Capture at enqueue**, from the current principal and current section groups - not at page
  initialization as ConferenceRooms does, or the snapshot goes stale across a long session.
  Reuse the type and the re-evaluation, not the capture point.
- **Add the module-enablement check.** `JobAuthorizationSnapshot.IsStillAuthorized:87` tests
  groups only, while live authorization also rejects disabled modules
  (`Authorization/GroupAuthorizationHandler.cs:55`). Queue a job, disable Comms-10k, leave the
  groups alone: the write currently proceeds.

**Tests:** a job whose authorizing group left the allowed set refuses; a job whose module was
disabled refuses; an absent snapshot refuses. Each writes a failed audit (C3).

## C7 - stale state and re-entry across every handler sharing the busy flag

Three handlers mutate page fields across awaits with nothing reliably stopping a second entry,
and the fix must cover the handlers that merely *lower* the shared flag too.

1. **`HandleFileUpload` never raises `isLoading`**, while the `InputFile` is gated only on it.
   It RESETS the shared fields at its start and writes them across awaits, so a second entry
   wipes the first run's state mid-parse. **Capturing into locals cannot fix this.** It must
   refuse re-entry as its **first statement, before any reset**; parse into local collections and
   publish in one assignment at the end; and raise `isLoading`.
2. **`ValidateEmails`** raises the flag, yields, then reads `ticketNumber` and `parsedEmails`.
3. **`ExecuteReplace`** trusts the disabled button, then enqueues from mutable fields. Add a
   server-side re-entry refusal and re-check `resolveResult.Success`, zero rejections **and a
   non-empty resolved set** in the handler, not in the markup.
4. **`LoadPreview`, `DownloadFull`, `DownloadRejections`** also own `isLoading` without refusing
   entry while busy - an overlapping read can finish and **lower the flag while a validation or
   submission is still running**, reopening the guarded handlers. Apply the entry guard to every
   operation that owns the flag.

**Tests:** a second upload mid-parse is refused; a partial parse never publishes (today a CSV
failure after valid rows leaves the prefix available to validate); an overlapping download does
not lower the flag under a running submit.

**Additional parser case from C10 review (2026-10-15):** the first occurrence is trimmed before
being stored in parsedEmails, so its rejected CSV text loses original whitespace while duplicate
rejections retain it. Preserve raw/query values during the local-collection parser work, with
unchanged matching semantics and consistent original-text reporting. `cdr-c10` records the
static reproduction; include a behavioral regression with a whitespace-bearing unmatched row.

## C8 - downloads: audit safely, and do not export executable cells

Two defects in the same two methods.

- **Audit after delivery.** `DownloadFull:318` and `DownloadRejections:409` invoke the browser
  download and THEN audit, inside one `try` whose `catch` reports failure. The file is already
  delivered; an audit throw reports a successful export as failed. Repo rule violation; the
  remedy `SafeAudit` already exists and Defender uses it.
- **Formula injection.** Both use raw `CsvWriter.WriteRecords` (`Comms10k.razor:313`, `:405`);
  CsvHelper does not enable injection protection by default, so a rejected address of `=1+1`
  exports as spreadsheet-executable content. **The repo already has `Services/CsvExport.cs:17`
  `CsvExport.Write` with the protection, and Defender uses it.** Reuse it. This matters most on
  the rejections export, whose entire content is attacker-supplied.

**Tests:** an audit that throws leaves the download reported successful; a formula-leading field
is neutralised.

## C6 - the ticket is stale, not unvalidated

**Revision 5 said an edited ticket could reach the job unvalidated. That is wrong** - the handler
submits `validatedTicket` (`Comms10k.razor:619`). The real defect is quieter: the operator edits
the field after Validate, the screen shows the new ticket, and the job, audit and notification
carry the old one.

Either lock the field after Validate, or re-read and re-validate at Confirm, refusing on failure.
Locking is smaller and matches the module's refuse-rather-than-guess posture.

**Test:** the displayed ticket and the submitted ticket cannot disagree.

---

# Part 2 - Defender correctness

## D8 - two parsers silently corrupt ordinary data

- `DefenderEndpointDeviceService.ParseDevice:1632` reads `osBuild` with a string-only parser
  while the Defender API returns a number - so normal build values vanish from the detail panel
  and the CSV.
- `AdSiteDirectory.SiteNameFromDn:104` splits at the first comma without honouring DN escaping,
  so `CN=New York\, Building A,...` becomes `New York\` - corrupting the displayed site and the
  Site filter that matches on it.

**Tests:** a numeric `osBuild` survives to the CSV; a site name containing an escaped comma reads
whole and filters correctly.

## D10 - a malformed row can make a capped page look complete

`TryReadPage:1616` silently skips non-object entries, and pagination then compares the reduced
count against `$top`. A full capped page containing one malformed entry looks like a short final
page, so the run reports Complete having stopped early.

**Test:** a capped page with a malformed entry does not terminate pagination.

## D9 - an export failure escapes with no result and no audit

`DownloadCsvAsync:1064` has a `finally` but no `catch`, so a throw leaves no handled result and no
failed-export audit. Same class as C8 on the other module.

## D4 - audit what was evaluated, not just what was asked

- `DescribeFilters:1117` takes `DefenderDeviceFilters`, which has no Site or Seen-by - those are
  page fields applied after the fetch and they decide what the CSV contains.
- **The lookup audit records `loaded.Devices.Count` before that filtering**, so a five-row
  displayed result can audit 100 devices.
- `FilteredDevices` deliberately keeps rows whose enrichment is unavailable, so "applied Site=X"
  alone conceals that some rows could not be evaluated.
- The **export** audit must read a stored applied snapshot, not the live `form` - reading `form`
  at export reintroduces the stale-form class the page's comments exist to prevent.

Record: the requested filter snapshot, which filters were evaluated versus degraded, and the
final displayed/exported count.

## D7 - bound the enrichment work

Implementation guidance, including actual cancellation propagation, site-worker ownership
and its native-cancellation limit, is in `.agents/review/cdr-d7-design.md`. D2 now precedes
D7 so per-call diagnostics exist before stop/deadline results are added.

`ListDevicesAsync:549` waits for enrichment before returning anything. The batch loop continues
after failures and each hunting request has a four-minute timeout (`Program.cs:154`). At the
100,000-device ceiling, 100 consecutive timeouts is roughly **400 minutes** before the
already-fetched inventory is returned. There is no overall deadline, no cancellation and no early
stop on systemic authorization failure.

Bound it: an overall deadline plus an early stop when failures are systemic, then return the
inventory with enrichment marked unavailable. **Preserve the existing `(unavailable)` row
behaviour** - that is the mechanism this protects, not one to replace.

## D1 - do not read the inventory on open

`OnAfterRenderAsync:852` calls `LoadAsync()` on first render while the form says "Set what you are
looking for, then Search". Remove it.

- **The `result == null` branch renders nothing** - two comment blocks and blank output. **Add a
  real empty state** saying nothing has been searched for yet; that is what makes the page read as
  a working form rather than one that failed. Both comments there become false.
- **Do not change the default filter semantics.** The default is `OnboardingStatus =
  CanBeOnboarded`, not blank. Changing it is a separate, unapproved behaviour change.
- No filter is required to press Search (owner ruling).
- **Both registries:** `ProgressRegistry.cs:1258` exempts `OnAfterRenderAsync` because "the
  initial load is LoadAsync" - false after this. `ClickGateRegistry.cs` records the deferred load
  and carries `ExpectedLineCount = 1213`; adding an empty state moves it.

## D2 - the shared failure reason

`Program.cs:210` registers the service as a singleton; `DefenderEndpointDeviceService.cs:936`
keeps `_lastBatchFailureReason` as a mutable instance field written and read per run. Two
concurrent runs interleave and one reports the other's reason. Return it from
`TryEnrichBatchAsync` instead; delete the field. **Sweep the class for the same shape** - a
singleton with one such field usually has more.

**Test:** two concurrent `ListDevicesAsync` calls each report their own reason.

## D3 - one operation at a time

`LoadAsync:865` raises `isLoading` only after starting and nothing refuses a second entry. The
page's own remarks say a load overlapping an export can replace rows under an audit record, so the
guard covers both directions: `LoadAsync` refuses while busy **including during an export**, and
`DownloadCsvAsync` refuses an overlapping entry.

---

# Part 3 - performance, last

## D5 - stop recomputing

1. `AdSiteSubnetMap.FindSite:64` parses every subnet inside the per-device loop while `ApplySites`
   calls it per device - millions of redundant parses per load. Prepare the parsed subnet list
   once per load. **Measure the real subnet count before quoting one.**
2. `FilteredDevices:645` is uncached and read from six sites, three in markup that re-render on
   any state change. Compute once; **invalidate on `result`, `appliedSite` and `appliedSeenBy` -
   all three.** A stale cache is worse than recomputing.

**Behaviour must not change**: the `seenByWasEvaluated` escape and the Site `(unavailable)` escape
both survive. **Revision 1 said the existing filter tests must pass untouched - wrong.** They are
source-shape assertions; a correct refactor may require rewriting them. The invariant is that the
behaviour they describe still holds; re-derive each and say so.

**Test:** a subnet is parsed once per load, not once per device - a behavioural count assertion.

---

## D6-local - stale comments in files these slices touch

The codebase-wide audit is a separate plan. These are in code these slices edit:

- `DefenderEndpointDeviceService.cs:39` - says an AD site-map read failure "FAILS THE RUN (plan
  Q10)"; `ApplySites` marks the column instead. Changed by `mde-1`; the comment was left.
- Same file, `:833` - "One query per refresh, never one per device" predates batching.
- `DefenderEndpointDevices.razor:952` - says twenty-seven CSV columns; the header has 29.
- **The "replaces are not serialised" comments in `Comms10kService`, `ModuleCatalog`,
  `Comms10kReplaceSequenceTests` and `README.md:223` ARE TRUE AND MUST NOT BE "CORRECTED".**
  Revisions 2-5 said to narrow them because the job runner is single-threaded. **The runner gate
  is process-local and the jobs databases are instance-local, so dev and prod can overlap on the
  same AD group.** Deleting that warning would replace a true statement with a false assurance.
  Leave them; if anything, make the cross-instance case explicit.

---

## Sequencing

**Part 1 in order: C9, C1, C10, C3, C4, C11, C5, C2, C7, C8, C6.** C9 first because it can write
twice. C10 before C3 because C3 turns its quiet bug into a loud one. C11 and C5 adjacent - a
truthful report is worth little if nobody can see it.

**Then Part 2: D8, D10, D9, D4, D2, D7, D1, D3.** Correctness before the open-on-load change, so
the page is right when it starts being used deliberately. Agent sequencing adjustment,
2026-10-15: D2 supplies the per-call result needed by D7's systemic-stop/deadline reporting;
keep their fixes and proofs in separate commits. Scope and executive summary are unchanged.

**Then Part 3: D5.** Last, and only once everything above is green.

## Verification

Per-slice gate as under Constraints. One full-suite run at the final head. PSScriptAnalyzer and
Pester only if a `.ps1`/`.psm1` changed - none expected.

**Not covered by any of this:** that any page renders - there is no bUnit harness. The group pin
and the Defender open-without-fetch need manual checks on dev.

## Carried forward - recorded, NOT fixed here

- **Continuation-URL host check.** `DefenderApiClient.cs:257` concatenates relative input with the
  base URL without checking the resulting host. No operator-controlled route to supply one was
  found; a conditional boundary defect, not a demonstrated exploit.
- **Page-initialization-only authorization.** Defender authorizes at `:832` and Search does not
  re-authorize; Comms-10k reads are the same. Distinct from the acknowledged inability to see
  Windows token changes.
- **The physical-location workflow is unproven.** `BuildObserverQuery:151` assumes observer values
  stringify and join to a device id; no acceptance check establishes it. The forest read has never
  run here, `SeenBy()` has never returned data here, and whether AD site names locate anything is
  unsettled. **Probing AD is forbidden without a direct owner go.**
- **The codebase-wide comment audit (D6)** - separate plan, agent fan-out.

## Approval and implementation

The owner approved Revision 6 and the proposed dispatch arrangement on 2026-10-15:
"approved. go." The partial-file refusal was already accepted in the decision log and is
included in this approval. C7-C11 and D7-D10 are approved. No scope decision remains open.

Use one fresh `gpt-6.1-sol` implementer at `max` per slice, sequentially, with independent
`gpt-6-astra` review at `max`. Review the affected workflow, including its callers and lifecycle;
do not restrict the reviewer to the diff or prime it with earlier clean conclusions. Keep one
writer in this checkout and commit each verified finding before the next. Work directly on
`master`, matching the existing stream. Push, deployment and live-directory access are not
part of this implementation approval.

| Finding | Implementation | Evidence |
|---|---|---|
| C9 | Implemented / independently reviewed (`f530c62`) | [cdr-c9](../.agents/review/findings/cdr-c9.md) |
| C1 | Implemented / independently reviewed (`00f453e`) | [cdr-c1](../.agents/review/findings/cdr-c1.md) |
| C10 | Implemented / independently reviewed (`0fb9f78`) | [cdr-c10](../.agents/review/findings/cdr-c10.md) |
| C3 | Implemented / independently reviewed (`f0d79b0`) | [cdr-c3](../.agents/review/findings/cdr-c3.md) |
| C4 | Implemented / independent review P2 corrected (coder proof) | [cdr-c4](../.agents/review/findings/cdr-c4.md) |
| C11 | Implemented / independently reviewed (`b0c535e`) | [cdr-c11](../.agents/review/findings/cdr-c11.md) |
| C5 | Implemented / local gate passed; independent review pending | [cdr-c5](../.agents/review/findings/cdr-c5.md) |
| C2 | Pending | |
| C7 | Pending | |
| C8 | Pending | |
| C6 | Pending | |
| D8 | Pending | |
| D10 | Pending | |
| D9 | Pending | |
| D4 | Pending | |
| D7 | Pending | |
| D1 | Pending | |
| D2 | Pending | |
| D3 | Pending | |
| D5 | Pending | |
