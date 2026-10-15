# C5 - Submitter-owned Comms-10k durable jobs

Status: Implemented on master with the restored local gate passed; independent Astra workflow review pending.

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

`5d5db6f9c35f223598947f64287bfef1e44cee18..HEAD`

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

Independent Astra max workflow review is the next gate. C2 then implements fresh enqueue
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
The implementation remains local under push policy ask; independent Astra max workflow
review is pending, and C2 follows its gate.
