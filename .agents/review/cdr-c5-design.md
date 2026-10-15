# C5 implementation guidance - submitter-owned Comms-10k jobs

Read-only design advice from native collaboration agent `/root/design_job_visibility`,
dispatched as `gpt-6-astra` at `max` under the approved Revision 6 workflow, 2026-10-15.
No code, tests, live access or external changes were performed. These are implementation
choices within approved C5, not additional owner rulings. Re-read symbols when implementing.

## Boundary and service contracts

Use the existing Comms10k page and policy, without changing AdminBulkJobs access or adding a
shared UI component. Require a currently authenticated principal with a nonblank full
`Identity.Name`; retain its domain and compare ownership case-insensitively. Display names,
stripped SAM names and the existing `Unknown` fallback are not ownership identities.

Every list, detail, rows, cancellation and deletion operation must constrain the persisted job
by all three of module (`Comms10kReplaceProcessor.ModuleName`), type
(`Comms10kReplaceJobPayload.JobType`) and submitted identity. Perform live module authorization
on list/detail/refresh too, and immediately before mutations. Do not trust the rendered job or
its membership in a previously loaded list. Inaccessible and nonexistent IDs get the same
generic refusal, without exposing foreign metadata. No admin override in this view.

Add narrow scoped BulkJobService methods while retaining the existing admin APIs. Module,
type and submitted identity are immutable in the repository update paths, so a common
persisted-job scope check can guard the existing operations. Page code supplies module/type
constants and the trusted principal's name, not user-provided scope.

Repository filtering must happen BEFORE ordering and LIMIT. Filtering a capped admin/module
history afterwards can hide the submitter's jobs. Show all own active jobs and provide bounded
paging through retained finished history, retaining current retention settings and schema.

## Control behavior and lifecycle

- Cancellation is for queued/running jobs, including the display-only Stalled state. Reuse the
  runner's cancellation mechanism and queued compare-and-swap. Say `Cancellation requested`:
  Comms has two stages, and cancellation cannot undo or abort an in-flight clear/refill.
  A terminal-state race must not be presented as a successful cancellation.
- Deletion is for terminal jobs only; preserve BulkJobRepository.Delete's SQL guard. It
  removes the durable job/rows, not audit history. Capture legitimate metadata before deletion,
  refresh the list and clear deleted details.
- Reuse AdminBulkJobs' subscribe/InvokeAsync/dispose pattern: check disposal after awaits,
  subscribe before initial loading, handle callback exceptions and unsubscribe on disposal.
  Event IDs are refresh hints and confer no access. Automatic refresh remains silent; a
  manual Refresh action provides feedback.
- Keep the jobs-control busy state separate from upload `isLoading`, guard handlers before
  their first await, and prevent stale refresh/detail continuations from publishing. Clear
  cached lists/details when authorization is denied; report load failures as failures.
- Place the jobs panel outside the service-configuration early return. Missing credentials
  must not hide persisted outcomes. Live Comms10k authorization, including enablement, still
  gates the panel.

## Submission and outcomes

Use the same trusted identity for enqueue and ownership. A successful enqueue gets a separate
informational `Submitted` notice and reveals the new job in this page's `Your jobs` panel.
Do not fabricate a `RefusedBeforeAnyChange` directory result to represent submission, and do
not extend that outcome enum for a queue lifecycle state.

Comms records two stage rows, Resolve and Replace, not one row per group member. Show the
recorded row status/message, job message, ticket and timestamps. The Replace row holds the
detailed outcome. `Completed` alone does not mean success: a payload failure can complete
with zero rows. Preserve C3/C11's refusal, interrupted, partial and unknown reporting.

## Audit and notifications

Safely audit every successful or failed cancel/delete attempt with actor/IP, a distinct action,
requested ID, result and legitimate job metadata/ticket. Audit failure cannot reverse the
displayed result of a completed operation. Do not reveal foreign-job metadata through denial
audits returned to the caller.

Keep cancellation's existing terminal administrator notification owned by the runner/processor;
do not add a duplicate replace-completion email. Use audit-only removal for the job record.
Authority distinction: this latter choice is an interpretation of the Constitution's stated
notification scope (directory/identity/access mutations), supported by AdminBulkJobs.RemoveJob;
it is NOT a separately granted owner exception. Record deletion changes none of those objects,
while the broader user-facing-mutation audit rule clearly applies.

## Verification and versions

Extend the existing real-SQLite repository/service tests. Independently reject incorrect
module/type/owner and blank ownership; preserve domains and accept casing differences. Prove
filtering precedes the history limit, foreign details/controls have no effects, deletion stays
terminal-only, and queued cancellation sends a single completion notification. Add focused
page wiring/lifecycle guards for live authorization and scoped calls. Mutation-prove all new
guards. A small pure presentation helper is appropriate if needed to test Submitted and
failed/partial/interrupted/zero-row outcomes; there is no bUnit rendering harness.

Shared repository/service behavior requires a base app version bump as well as the Comms
module bump. Update the Comms ProgressRegistry for new handlers; the page has no line-keyed
ClickGate entries. No live directory check is part of this implementation authority.
