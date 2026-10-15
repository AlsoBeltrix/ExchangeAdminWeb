# C5 - Submitter-owned Comms-10k durable jobs

Status: Implemented; initial independent review CHANGES REQUIRED/P1 corrected on coder-side proof. Push awaits authority.

## Cause and final scope

Comms-10k can enqueue a background replace while its submitter cannot access the
cross-module AdminBulkJobs page. The global status frame is transient, so a refusal,
partial replace or interrupted run can lose its only operator-visible explanation.
Successful submission also fabricates a directory refusal result.

Owner 2026-10-15 "approved. go." approves Revision 6 C5 and its required verification.
Approved plan: `docs/Comms10kDefenderRemediation-Plan.md`, C5. Concrete choices:
`.agents/review/cdr-c5-design.md`, read in full before changes. Base: clean master
`5d5db6f9c35f223598947f64287bfef1e44cee18`. Live origin/github master both
`885397981d7550afebf6518ef21b4fc54b062859`; no push or branch operation.

Add narrow repository/service reads and controls constrained by persisted module,
type and full authenticated nonblank Identity.Name, retaining domains and ignoring
owner casing. Filter before ordering/limits, expose all owned active jobs and bounded
terminal-history pages. Keep schema, retention and administrative API contracts.
Cancellation uses queued CAS / running flag and token, refuses terminal races and
retains the runner's single terminal notification. Deletion retains the terminal SQL
guard. Capture legitimate metadata for safe audits of every cancel/delete attempt.

Add a Your jobs panel to the existing page, outside credential-configuration early
return. Reauthorize live Comms policy for every load/detail/control, clear caches on
denial, and use constants/trusted identity for each scoped call. Subscribe before
load; marshal callbacks, guard disposal and stale continuations, separate jobs busy
state from upload state, and keep automatic refresh silent. Show job status/message,
ticket/times and the recorded Resolve/Replace stage status/message, including zero-row
failures. Submission gets a separate informational Submitted notice, never a fake
directory outcome. Preserve C3/C11 refusal/partial/interrupted/unknown reporting.

Job-record deletion is audit-only under the design guide's interpretation of the
Constitution's directory/identity/access notification scope, supported by existing
AdminBulkJobs behavior. This is not a new owner exemption. No duplicate replace email.

Base version 2.27.2 -> 2.27.3; Comms 1.4.11 -> 1.4.12. Update ProgressRegistry;
ClickGate re-anchor procedure applies, but Comms currently has no line-keyed entries.

## Boundaries

C2 enqueue snapshots/write-boundary authorization, C7 existing CSV/upload/download
reentry, C8 safe export/audit and C6 locked ticket remain separate. No AdminBulkJobs
permission widening, shared UI abstraction, protected-principal checks, locking,
write retries, rollback, SDK upgrade, live AD/tenant access, deployment or push.
psl-2/3/4 and cdr-followup-1 remain unapproved. Module-spec 2.0/June 25 drift versus
app 2.27.2 is flagged; Constitution/current implementation establish these contracts.

## Verification

Passed: real-SQLite scoped guards; compiled page/lifecycle/presentation guards;
compiled mutation proof with exact-byte MD5/SHA256 restoration and advanced mtimes;
Release solution build, format before full solution tests, diff and staged ASCII.
No browser rendering or live directory interoperability is claimed.

## Implementation

Repository-owned queries filter the persisted module/type/owner triple before stable
ordering and LIMIT/OFFSET. A connection-local JOB_OWNER collation uses .NET
OrdinalIgnoreCase, including non-ASCII identities, without schema changes or stripped
domains. Active jobs are uncapped; terminal pages are capped at 100 rows per request.
The page requests 11 for a ten-row history page and its next-page sentinel.

Scoped service methods preserve administrative entry points. The persisted triple is
immutable in update paths, so scoped job reads guard id-based stage reads and controls.
Cancellation reuses queued CAS / running token handling and returns the running flag
update's actual acceptance, refusing terminal races. The existing deletion SQL guard
still requires a terminal job. Both controls return only legitimate pre-action metadata
for audit, with no metadata on foreign or nonexistent IDs.

The page rechecks the actual Comms policy and trusted full Identity.Name for every
load, detail and control. Live denial clears cached jobs/details and the prior submission
notice. Jobs have a separate busy flag raised before awaits; generation/disposal checks
suppress older continuations, including a late denial after a newer successful load.
Initialization subscribes before loading and cannot subscribe after disposal. Callbacks
marshal through InvokeAsync, catch exceptions and load silently. Manual actions report
their result through the existing progress channel. Missing credentials no longer hide
the panel. Job lifecycle status is distinct from the recorded outcome, including payload
failure with no rows; original C3/C11 stage messages remain intact.

Audits include the full actor as Actor (the shared audit user field retains its established
SAM presentation), IP, distinct action, requested ID/result and available legitimate ticket,
status and submission metadata. Audit errors cannot reverse a completed control result.
The page sends no completion notification; the existing runner/processor owns that event.
Successful enqueue persists the same full identity used for ownership, clears the old
directory result, and renders Submitted with an informational durable-jobs pointer.

ProgressRegistry records the new handlers. The clickgate re-anchor playbook was read;
Comms10k has no line-keyed entries, so none require relocation. Existing ClickGate guards
passed. Existing C3/C4 page fixtures now provide a real authenticated full-name claim;
the C4 delayed enqueue fixture remains capable of the additional scoped refresh read.

## Compiled guard proof

All 59 new cases passed first, then failed intended Assert.* assertions against
compiled production regressions in five sequential groups. Every mutation run rebuilt
Release solution tests with --no-restore, a C5 filter and xUnit.MaxParallelThreads=4.
No mutation used --no-build. Each TRX was checked for a nonzero test exit, actual
assertion failures and no error CS; missing TRX or compiler failures stopped the probe.

| Probe | Compiled regression | Assertion failures / executed |
|---|---|---|
| scope-sql | remove persisted module/type/owner predicates and blank-scope refusal | 13 / 59 |
| control-guards | unbound history limit, active-only delete, accepted terminal cancel, false running-CAS success, duplicate queued notification, wrong token key | 16 / 59 |
| live-authorization | ignore live policy and authenticated nonblank submission identity | 14 / 59 |
| presentation-audit-reentry | stale enqueue actor, missing job/stage messages, removed busy guards, audit failure reversing success, hidden callback warning, retained failed-load cache | 11 / 59 |
| stale-and-disposed | accept obsolete generations, allow post-disposal initialization and leak the event subscription | 8 / 59 |

All 59 case names appear in the union of those 62 assertion failures. Real SQLite
tests cover independent wrong module, wrong type, wrong owner/domain, stripped SAM,
display-only and blank/whitespace identities across lists/detail/rows/cancel/delete.
They prove Unicode case handling without domain conflation, filtering before the page
limit/offset, all active jobs despite the shared history cap, bounded retained history,
terminal-only deletion/cascades, deterministic terminal races, queued cancellation's
single completion hook and running cancellation's actual worker token.

Page tests invoke compiled Razor handlers by reflection and inspect generated render
frames, without a component host or browser. Actual catalog/group authorization over
SQLite proves live section SID revocation and module disablement for every job read
and control. Further guards cover trusted identity at enqueue, generic foreign/missing
refusals, audit metadata, audit failure isolation, successful Submitted notice,
missing credentials, all recorded outcome forms including zero rows, stale success and
denial continuations, detail races, initialization/unsubscription/disposal, reentry,
separate upload/jobs busy flags, failed loads and silent versus reported refresh.
An unattached component exercises callback marshalling failure through its caught warning.

Every production mutation restored saved fixed bytes in finally, verified MD5 and
SHA256, and advanced mtime beyond the mutated source. Final restored hashes:

| Source | MD5 | SHA256 |
|---|---|---|
| Components/Pages/Comms10k.razor | `4E9CCD5EF982907FD016984D72A5D653` | `F100D136805777B304719F4419041195CCBDD9A154282C8262EA8B8E92B29AA7` |
| Services/Jobs/BulkJobRepository.cs | `2DB75A4F9EEC6944CCB74AC6180DAE3E` | `756559B574C3F9B3940F1D184BBDE9BB844FF67E43E38D156983546F490D51C6` |
| Services/Jobs/BulkJobService.cs | `C6FE30B6DBC0ED54B44A16516D1D486E` | `3B7DD9D3B526EE38512F9BA3753BFACAA51FAD96727BA2A95CC90AD6F1D3707B` |

The restored focused Release run passed 686/686, including Comms, bulk-job repository,
runner and wiring tests plus ProgressRegistry/ClickGate. Earlier focused passes were
623/623 before new tests and 411/411 after fixture corrections. The first new-test
attempt found fixture compile errors (target-typed read-only dictionary), then twelve
fixture assertion failures (SID-only policy data and local versus UTC date); those
were corrected before the clean baseline and production-regression proofs.

Machine-local supplemental evidence is gitignored under `.agents/review/out/cdr-c5/`:
baseline/restored-focused logs and TRX, five named mutation logs/TRX and receipts.json.
The script is `.agents/review/out/cdr-c5-mutations.ps1`; final gate logs use the
`cdr-c5-` prefix in `.agents/review/out/`. This record owns the durable evidence.

# Review Packet

## Mode

IMPLEMENTATION_REVIEW

## Original Request

Owner approved all twenty Revision 6 findings with "approved. go." C5's named ruling:
"any case where a user can start a job that ends up here but not see it or manage it
is a failure." Implement submitter-owned Comms-10k job outcomes and controls on the
existing page, retaining AdminBulkJobs' separate permission and all reporting safeguards.

## Approved Plan

`docs/Comms10kDefenderRemediation-Plan.md`, approved Revision 6 C5, with concrete design
choices in `.agents/review/cdr-c5-design.md`.

## Commit Range

`5d5db6f9c35f223598947f64287bfef1e44cee18..5d999713e0d1d1525ee55a0413cb869d79c5ea43`

## Claimed Changes

- Narrow persisted module/type/full-owner reads, bounded terminal history, all active
  jobs and scoped race-aware controls while keeping administrative APIs.
- Live-authorized in-page jobs panel, stage outcomes, safe audits and lifecycle guards;
  separate Submitted notice and trusted enqueue identity.
- Base app 2.27.3 and Comms-10k 1.4.12, registry/docs updates and executable guards.

## Constraints / Out Of Scope

The Boundaries section owns the exclusions. No change to AdminBulkJobs permission,
directory writer, outcome enum, job schema/retention or completion notification scope.
No self-review, new test framework, live integration, deployment, push or branch action.

## Design Decisions

Connection-local OrdinalIgnoreCase collation preserves full identity domains and Unicode
casing without schema migration. Common persisted-scope reads guard subsequent id controls
because repository update paths cannot mutate module/type/submitter. SQL still owns
terminal deletion and cancellation CAS. The full audit Actor preserves identity even
though the shared audit user field keeps its established SAM rendering. Job-record
deletion is audit-only under the design guide's stated constitutional interpretation,
not a new owner exemption. Completed is never treated as the directory success result.

## Deferred Work

At the initial handoff, independent Astra max review was the next gate; its CHANGES REQUIRED
verdict and P1 correction are recorded below. C2 then implements fresh enqueue
snapshots/write-boundary authorization; C7, C8, C6 and Defender remain in approved plan
order. psl-2/3/4 and cdr-followup-1 are recorded but unapproved. Browser rendering and
installed AD-cmdlet interoperability remain unverified.

## Review Focus

Review the whole submitting and retained-job workflow, all callers, persisted ownership
boundaries, current policy/identity checks, race-aware controls, audit fidelity and
page lifecycle. Challenge stale data, inaccessible IDs, terminal races, payload failures
with no rows and preservation of C3/C11 reporting. Assess scope and evidence independently.

## Verification Run

Compiled mutation and restored focused results above. Final restored gate:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 40 existing warnings / 0 errors.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0,
  existing workspace-loading warning only; run before the full tests.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`:
  exit 0, 4,031 passed / 3 skipped / 0 failed, 5 m 06 s. A TRX logger was added for the
  receipt. This was the only full-suite run; no baseline full suite was run.
- `git diff --check HEAD`: exit 0, repeated after final bookkeeping.
- `tools/Test-AsciiOnly.ps1`: exit 0 with new source/tests staged.

All three mutated production files still match the exact MD5/SHA256 restoration receipts.
No shipped PowerShell changed, so Pester/PSScriptAnalyzer were not required or run.
Browser rendering, live AD/tenant access and installed-cmdlet interoperability were not
run. The baseline token meter ran; its Claude-only scope excludes this native Sol slice,
so no slice usage is attributed. No self-review was performed.

The normal configured git commit uses the existing global pre-commit wrapper, which
chains an executable local hook when present (none is present). No hook trust,
configuration or bypass was changed. Direct master work requires no branch closeout.
The initial implementation remained local under push policy ask; the independent review
and its required correction follow below.

## Independent workflow review - CHANGES REQUIRED, 2026-10-15

Astra at max independently reviewed
`5d5db6f9c35f223598947f64287bfef1e44cee18..5d999713e0d1d1525ee55a0413cb869d79c5ea43`.
Verdict: **CHANGES REQUIRED**, one **P1/HIGH** cache-invalidation root with two compiled
reproductions. This was not a clean approval.

1. Load actor A's retained jobs, switch AuthStateProvider to authenticated/authorized B,
   then invoke ToggleJobDetails(A-id), including Details and Hide branches. The scoped
   GetOwnedJob correctly refuses and details clear, but activeOwnedJobs/finishedOwnedJobs
   remain A's and jobsAllowed stays true. Compiled render frames still include A's ID and
   private recorded reason. AuthorizeJobsAsync never binds cached state to the actor;
   ToggleJobDetails changes only details.
2. With cached list/detail, deny live policy then invoke ExecuteReplace. Submission
   correctly refuses, but jobsAllowed remains true and cached metadata still renders.

Persisted scope checks remained correct; the reviewer demonstrated no unauthorized
persisted read or mutation. All other approved scope boundaries remain preserved.
Both probes used compiled handlers/render frames and temporary SQLite with controlled
authentication fixtures. No source edits, browser, hosted circuit or live systems were used.

The reviewer independently ran the 59/59 focused cases and verified five compiled
mutation groups / 62 assertions covering all 59, exact hashes/mtimes, the full 4,031-pass /
3-skip receipt and the 686-pass focused receipt.

## P1 correction scope declared before code edits

Baseline: clean master `5d999713e0d1d1525ee55a0413cb869d79c5ea43`. Both remotes' master
remain `885397981d7550afebf6518ef21b4fc54b062859`, checked live again on 2026-10-15.
Original C5 approval authorizes this correction. Bind jobs caches to the authenticated
full actor using OrdinalIgnoreCase; a changed actor invalidates lists, details, submission
notice, paging and older generations. Share the jobs-denial cleanup with ExecuteReplace
for authorization or identity denial. Preserve current submission approval capture and
all persisted scope/control behavior. Add compiled reproductions plus positive ownership,
casing, paging and stale-publication guards. No C2 snapshot/write mechanics or C7 upload
reentry work. Comms-10k 1.4.12 -> 1.4.13; base remains 2.27.3 (page-only behavior).

The finding is P1/HIGH, not CRITICAL, so the correction closes on coder-side guard proof
under the existing repo policy. No second review round is requested or launched. Run
focused checks, compiled mutation proof with exact-byte/hash/mtime restoration and one
restored final gate before the normal correction commit. Next is C2 after that gate.

## P1 correction implementation and compiled proof

The earlier implementation packet and hashes describe `5d99971`; these correction
receipts supersede the page hash and Comms version, while shared service/repository
behavior and the base version remain unchanged.

Jobs caches now have an actor binding compared with OrdinalIgnoreCase. Accepting a
different authenticated full identity clears lists, details, submission notice,
paging/next-page state, job action feedback and jobs authorization state, and advances
the generation. The current authorized operation receives the new generation; older
continuations retain their obsolete one and cannot publish or clear the new actor's
data. Case-only changes, including Unicode casing, retain the same owner's state.

ExecuteReplace uses the same denial cleanup for policy or identity denial. It binds
an allowed actor before either the incomplete-file refusal or the enqueue/Submitted
notice, so both paths discard the previous actor's panel, and a legitimate new notice
survives the following load. The frozen C4 confirmation and submission inputs are
unchanged. No C2/C7 or shared storage/service behavior was added. Comms is 1.4.13;
the base stays 2.27.3. No new UI handler was introduced; ProgressRegistry remains
accurate, and the clickgate re-anchor procedure found no Comms line-keyed entries.

Fourteen new compiled cases cover the two review reproductions, Details and Hide,
same-SAM owners in different domains, refused foreign controls, the new owner's
subsequent load, older actor successes/denials, ASCII and Unicode case preservation,
section SID revocation, module disablement, unauthenticated/blank/whitespace/display-only
identity denial, pending older publication after submission denial, and actor switching
on both incomplete-file refusal and successful submission.

All 14 passed, then failed intended Assert.* assertions in three sequential Release
solution probes. Each rebuilt production and tests (never --no-build), checked for no
error CS and inspected its TRX for actual assertion failures and exit 1.

| Probe | Compiled regression | Assertion failures / executed |
|---|---|---|
| original-reviewed-page | restore the entire reviewed Comms10k.razor from HEAD | 11 / 14 |
| casing-and-submission-binding | use case-sensitive cache ownership; omit enqueue-side actor binding | 4 / 14 |
| denial-without-generation-invalidation | clear denial state without invalidating older loads | 6 / 14 |

The union covers all 14 new case names (21 assertion failures). The first probe
reproduces both independently reported paths against the actual original page.
The last probe shows that clearing visible metadata alone does not suppress a
previously authorized delayed load after a submission refusal.

Every probe restored saved fixed bytes in finally, verified MD5 and SHA256, and
advanced the page mtime beyond its mutated version. Restored Comms10k.razor:
MD5 `B6C6DF1ABEEC28C1E2053153A07AFDA0`;
SHA256 `9507559ABFFFCB12167BDB74B3A6914832FCD307F086E68D94EB5751F592E9B9`.
The corrected focused run passed 425/425; after all restores the broader affected
workflow passed 700/700, including existing Comms, bulk jobs, ProgressRegistry,
ClickGate and C4 submission-capture coverage.

Supplemental ignored evidence: `.agents/review/out/cdr-c5-correction/` holds baseline,
three mutation and restored-focused logs/TRX plus receipts.json; the script is
`.agents/review/out/cdr-c5-correction-mutations.ps1`. Final-gate logs use the
`cdr-c5-correction-` prefix. No browser, hosted component circuit, live AD/tenant
operation or second review was used.

## P1 correction final gate and closure

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 40 existing warnings / 0 errors.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0,
  existing workspace-loading warning only; completed before the full suite.
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`:
  exit 0, 4,045 passed / 3 skipped / 0 failed, 5 m 11 s. A TRX logger captured the receipt.
  This was the correction's only full-suite run; no baseline full suite was run.
- `tools/Test-AsciiOnly.ps1`: exit 0 with the new cache-test source staged.
- `git diff --check HEAD`: exit 0, repeated after final bookkeeping.

The restored production page still matches its exact MD5/SHA256 receipts. No shipped
PowerShell changed; Pester/PSScriptAnalyzer were not required or run. Browser rendering,
hosted circuit and live AD/tenant integration were not run. The token meter ran against
the committed baseline; its Claude-only scope excludes this native Sol correction.
No self-review was run. The normal configured correction commit changes no hooks,
configuration or trust settings; no branches, deployment or push were performed.

The independent review was CHANGES REQUIRED, not a clean approval. Its P1/HIGH finding
is now resolved on coder-side guard proof under the noncritical correction policy, with
no second review round. C5 is closed locally; C2 is the next approved slice.
