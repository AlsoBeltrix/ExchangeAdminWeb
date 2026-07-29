# Message Analysis Export Download Link + Reports Page -- Plan

Status: **Draft -- awaiting owner ruling on D3 (see Owner Gate).** Decisions D1 and
D2 are ruled. No code is written until this plan is Approved.
App version at draft: `2.3.30` -> `2.4.0` on implementation (shared infrastructure:
first HTTP endpoint in the app, new shared download seam).
Module: `MessageTrace` `1.2.1` -> `1.3.0` (module behavior change; both bumps fire,
per `docs/ProjectConstitution.md` "Deployment And Versioning").
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.
Supersedes: `docs/MessageTraceDetail-Plan.md` decisions 5 (zip attached), 6
(recipient gating), and the "owner decision B -- no server file-share link" clause.
That plan stays as history; this one is the current intent for the export delivery
path only. Its decisions 1-4 and 7 are unchanged.

## Problem / Goal

Today the Message Analysis detail export is emailed as a **zip attachment** to a
recipient set the operator cannot choose: the authenticated user's claim address plus
`Email:AdminNotificationEmail` (`Services/EmailService.cs:437-496`,
`ResolveMessageTraceRecipients` at `:483`). Three defects follow from that shape:

1. **Wrong recipients.** Every configured admin receives the actual trace data for
   every export, whether or not they requested it. Owner direction: admins should not
   receive the results.
2. **No arbitrary recipient.** The original rationale (an operator-typed address is an
   exfiltration path) only holds because the *payload* travels in the mail. If the
   mail carries a **login-gated link** instead of the file, the address becomes a
   notification target, not a data channel, and the gate moves to the app's existing
   Windows authentication. Arbitrary recipients then become safe.
3. **Leaked reasoning in the UI.** `Components/Pages/MessageTrace.razor:399` renders
   `Emailed reports are sent only to <strong>@DestinationDisplay()</strong> - never a
   typed-in address.` The trailing clause explains a threat model the operator cannot
   see (there is no address input on the page) and must not ship.

Goal: deliver the export as a **login-gated download link**, allow any notification
recipient, stop mailing trace data to admins, and give operators a **Downloadable
Reports** page listing past exports with a ticket-prompted download.

## Owner Decisions

### D1 -- Retention: out-of-process (RULED)

> "B. cleanup is handled out of this process by a scheduled task."
> "the task only deletes files older than 30 days. that's enough time. you can even
> put in the email that the file is available until N date."

The app does **not** delete export files. An existing scheduled task on the host
removes files older than 30 days from the audit log root. Consequences that bind this
plan:

- The download endpoint and the reports page must treat a missing file as a normal,
  expected state ("expired"), never an error or an unhandled exception.
- The notification email states an availability date, computed as
  `submittedAtUtc + Export:RetentionDays` (new config key, default `30`). The value is
  **descriptive only** -- it mirrors the external task's policy and enforces nothing.
  It must be documented as such wherever it is read, so a future agent does not
  mistake it for the deletion mechanism.
- `BulkJobs:RetentionDays` already defaults to 30 (`Services/Jobs/BulkJobService.cs:65`),
  so the job record and the file expire on comparable schedules. They are independent
  clocks; neither may assume the other.

### D2 -- Authorization: module access, not per-user ownership (RULED)

> "message trace isn't privileged and anyone who can log in and is able to access the
> message analysis module should be able to download it."

The download gate is the existing `MessageTrace` catalog policy. **No ownership
check**: an operator with module access may download any export, not only their own.
The ticket number is an **audit prompt, not an authorization control** -- it is
recorded, never validated. This also settles the URL form: because module access is
the gate, a guessable identifier is acceptable and the job ID is used directly (owner:
"it doesn't matter"). No unguessable token is introduced; adding one would imply a
capability-URL security model this design explicitly does not use.

### D3 -- Email recipients: default set when the operator types nothing (OPEN -- BLOCKS SLICES 3 AND 4)

See the Owner Gate at the end of this document. Slices 1, 2 and 5 do not depend on it
and may proceed once this plan is Approved.

## Non-Goals

- **No change to what the export contains.** CSV columns, ordering, the 50-message
  cap, the 10-message live threshold, and the bulk-job routing are untouched
  (`docs/MessageTraceDetail-Plan.md` decisions 1-4).
- **No in-app file deletion or retention sweep.** D1 places this out of process. Do
  not add a pruner, a hosted timer, or a startup cleanup for export files.
- **No ServiceNow validation or writeback** on the ticket field. It is plain audit
  metadata (Constitution, "External Integrations" and "Never Do").
- **No new catalog module.** The reports page belongs to the existing `MessageTrace`
  module and reuses its policy. Adding a module would create a second access gate for
  data the owner has ruled sits behind the MessageTrace gate.
- **No unguessable-token / capability-URL scheme.** Ruled out by D2.
- **No change to the live (1-10 message) in-browser download path**, beyond what slice
  4 states explicitly.

## Current-State Facts The Implementation Depends On

Verified against the tree at draft time. Re-read before editing (Known Failure Class
number 4: never trust remembered file contents).

| Fact | Evidence |
|------|----------|
| The app has **no HTTP controllers and no minimal-API endpoints**. Only Razor components are mapped. | `Program.cs:236-238` |
| `options.FallbackPolicy` **denies every endpoint that declares no authorization metadata** (`RequireAssertion(_ => false)`). | `Modules/ModuleCatalog.cs:72-74` |
| The `MessageTrace` policy alias is `"MessageTrace"`, fail-closed, dynamic group-backed. | `Modules/ModuleCatalog.cs:225`, `:94-97` |
| The page already declares `@attribute [Authorize(Policy = "MessageTrace")]`. | `Components/Pages/MessageTrace.razor:7` |
| Exports are written to `<logRoot>\ExchangeAdminWeb\MessageTraceExports\` as `MessageTraceDetail_{job.Id}_{job.SubmittedAtUtc:yyyyMMdd-HHmmss}.csv`, fail-soft (errors logged and swallowed). | `Services/Jobs/MessageTraceDetailJobProcessor.cs:106-112`, `:133-149` |
| `AuditLogRoot.Require(config)` is the only sanctioned way to resolve the log root and fails loud when unset. | `Services/Jobs/MessageTraceDetailJobProcessor.cs:137` |
| The job record carries `Id`, `ModuleId`, `JobType`, `SubmittedBy`, `SubmittedByDisplay`, `SubmittedIp`, `Ticket`, `SubmittedAtUtc`, `FinishedAtUtc`, `Status` and the row counts -- everything the reports page needs except the search criteria. | `Services/Jobs/BulkJobModels.cs:46-108` |
| The job payload (`PayloadJson`) holds the selected `MessageTraceResult` rows and `UserEmail`. It is the only place a "what the trace is" description can come from. | `Services/Jobs/MessageTraceDetailJobPayload.cs:18-27` |
| `BulkJobRepository.GetRecentFinished(limit)` is **not filtered by module or job type** and is capped by `BulkJobs:RecentJobLimit` (default 25). It cannot back the reports page as written. | `Services/Jobs/BulkJobRepository.cs:284-295`, `Services/Jobs/BulkJobService.cs:64,172` |
| `Application:PathBase` defaults to `/ExchangeAdminWeb` and is patched per environment by promotion. Any absolute URL in an email must be built from config, never hardcoded. | `Program.cs:217-221`, `tools/promote-dev-to-prod.ps1:169,188` |
| `EmailService` has **no** base-URL or public-URL configuration today. | no match for `BaseUrl`/`PublicUrl` in `Services/EmailService.cs` |
| The existing in-browser download is a JS interop blob (`downloadFile`), entirely client-side; it persists nothing server-side. | `Components/App.razor:54-68`, `Components/Pages/MessageTrace.razor:927-968` |

## Constitution Conflict To Record, Not To Silently Resolve

`docs/ProjectConstitution.md` "Never Do": *"Do not write durable state into locations
that deployment or log pruning scripts delete."*

This plan makes an externally-pruned directory the backing store for a link the app
hands out. That is a real tension with the rule, and the implementing agent must not
treat it as an oversight to "fix" by adding in-app retention -- D1 forbids that.

The tension is resolved as follows, and this reasoning must be reproduced in the code
comment on the download handler:

- The export file is a **convenience artifact, not durable state**. The authoritative
  durable records are the audit event (`MessageTrace_DetailExport`, written with the
  saved path at `Services/Jobs/MessageTraceDetailJobProcessor.cs:171-173`) and the job
  record. Neither depends on the file surviving.
- Every consumer of the file -- the endpoint, the reports page, the email -- is
  required to render its absence as an ordinary "expired" outcome. Nothing breaks when
  the scheduled task removes it.

If a future change makes the file authoritative for anything, this exemption lapses
and the Never-Do rule applies again.

## Approach

Four moving parts, in dependency order.

1. **A download endpoint** -- the app's first HTTP endpoint. A minimal API `MapGet` in
   `Program.cs`, explicitly `.RequireAuthorization("MessageTrace")`. Explicit is
   mandatory, not stylistic: the fallback policy (`ModuleCatalog.cs:72-74`) denies any
   endpoint that declares nothing, so an unadorned route would 403 for everyone.
2. **A resolver service** owning the export directory, the filename convention,
   path-traversal rejection and existence checks -- so the endpoint and the reports
   page share one implementation and one set of tests.
3. **An email that links instead of attaching**, with a configurable recipient.
4. **A Downloadable Reports page** at `/message-analysis/reports`, listing terminal
   `MessageTrace_DetailExport` jobs with who / when / what / ticket, and a download
   action that prompts for a ticket number before navigating.

### Why a minimal API and not a Razor page returning a file

A Blazor Server component cannot stream a file response; the existing pattern
(`downloadFile` JS interop, `Components/App.razor:54`) base64-encodes the whole payload
over the SignalR circuit. That is acceptable for a small in-memory CSV and unacceptable
for an arbitrary stored export, and it cannot be the target of a link in an email. A
real HTTP GET is the only shape that satisfies "a link in an email that requires
login".

## Slices

One commit per slice. Commit each before starting the next (`AGENTS.md` Git Safety;
`.agents/repo-guidance.md` Earned Practices).

### Slice 1 -- `MessageTraceExportStore` (resolver + safety)

New `Services/MessageTraceExportStore.cs`, registered scoped in `Program.cs`.

```
string   DirectoryPath { get; }                  // <logRoot>\ExchangeAdminWeb\MessageTraceExports
string   FileNameFor(string jobId, DateTime submittedAtUtc)
bool     TryResolve(string jobId, DateTime submittedAtUtc, out string fullPath)
DateTime ExpiresAtUtc(DateTime submittedAtUtc)   // submittedAtUtc + Export:RetentionDays (default 30)
```

Requirements:

- Resolve the root only via `AuditLogRoot.Require(_config)`. Do not re-derive it.
- **Reject any `jobId` that is not a 32-character hex GUID "N" string** before it
  touches the filesystem. Job IDs are assigned as GUID "N"
  (`Services/Jobs/BulkJobModels.cs:48`), so this is a total whitelist, not a blacklist.
  It closes path traversal at the parse step rather than by sanitising.
- After composing the path, assert `Path.GetFullPath(candidate)` starts with
  `Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar`. Belt and braces: the
  whitelist should make this unreachable, and it is the guard that survives a future
  change to the ID format.
- `TryResolve` returns `false` for a missing file. It does not throw and does not
  create the directory.
- Move the filename construction out of `MessageTraceDetailJobProcessor:106-107` and
  the directory construction out of `SaveToLogPath:138` to call this store, so the
  writer and the readers cannot drift apart. Keep `SaveToLogPath`'s fail-soft
  try/catch exactly as it is -- a save failure must still never break the job.

### Slice 2 -- The download endpoint

In `Program.cs`, after `MapRazorComponents`:

```
app.MapGet("/exports/message-trace/{jobId}", handler)
   .RequireAuthorization("MessageTrace");
```

Handler behavior:

- Look up the job via `BulkJobService.GetJob(jobId)`. Return `404` when it is absent,
  when `job.ModuleId != "MessageTrace"`, or when
  `job.JobType != MessageTraceDetailJobPayload.JobType`. This stops the endpoint
  becoming a generic reader for any module's job artifacts.
- `TryResolve` the file. On miss return **`410 Gone`** with a plain-text body naming
  the expiry date -- not `404`, so an expired export is distinguishable from a bad link
  in both the UI and the logs.
- On hit, stream it: `Results.File(path, "text/csv", downloadFileName)`. Stream from
  disk; do not read the file into memory.
- **Audit every attempt, hit and miss**, via
  `AuditService.LogLookupAction(performedBy, ip, "MessageTrace_ExportDownload", target: $"job {jobId}", success, errorDetail, ticketNumber)`.
  The `ticket` query-string value (slice 5) is recorded verbatim and never validated.
  An audit failure must not fail the download (Constitution, "Auditing And Tracing").
- Carry the Constitution-conflict comment from the section above.

The endpoint is the app's first, so also confirm by inspection that it sits after
`UseAuthentication`/`UseAuthorization` in the pipeline and inside the `UsePathBase`
scope, and that adding it does not disturb the Razor component mapping.

### Slice 3 -- Email: link instead of attachment (BLOCKED ON D3)

`Services/EmailService.cs`:

- Change `SendMessageTraceResultAsync` to take a **download URL** and an **expiry
  date** in place of `byte[] zipBytes` / `zipFileName`.
- Delete `ResolveMessageTraceRecipients` (`:483-496`) and its call site. Replace it
  with the recipient set D3 selects. **Admins are removed from the trace-data path
  either way** -- that part is owner-ruled independent of D3.
- Rewrite the body (`:455-470`): drop "attached as a zip file"; add the link and a line
  stating the export is available until the expiry date. HTML-encode every interpolated
  value, matching the existing `h(...)` helper at `:453`.
- Delete `ZipSingleFile` from `MessageTraceDetailJobProcessor.cs:151-162` and the
  `System.IO.Compression` using at `:2` once nothing references them.

Absolute-URL construction: there is no base-URL config today. Add
`Application:PublicBaseUrl` (e.g. `https://host/ExchangeAdminWeb`). When it is unset,
**log a warning and send the mail with a relative path**, plainly labelled as such --
do not guess a scheme and host, and do not fail the send. The job has already completed
at this point; a mail-formatting decision must never change the job result
(`OnJobCompletedAsync` is documented fail-safe at `:89-90`). Add the key to `README.md`'s
config table (`README.md:532-533`) and to `tools/promote-dev-to-prod.ps1` alongside the
existing `Application:PathBase` patch, so dev and prod do not silently share one URL.

### Slice 4 -- Remove the leaked UI string; wire the recipient input (BLOCKED ON D3)

`Components/Pages/MessageTrace.razor`:

- Delete the `- never a typed-in address` clause at `:399`. Under D3 the surrounding
  sentence is either rewritten to describe the chosen recipient or removed along with
  `DestinationDisplay()` (`:916-925`); which one depends on D3, so this slice lands
  after it.
- Add the recipient input if D3 calls for one, with client-side format validation only.
  Do not add domain allow-listing -- the link is login-gated, which is the entire point
  of the redesign.
- Add a "Downloadable Reports" link to the reports page from the Trace Search tab.
- Leave `DownloadSelectedDetails()` (`:927-968`) untouched. The live 1-10 path stays an
  in-browser blob and continues to persist nothing; the reports page therefore lists
  **emailed/bulk exports only**. State that on the page so the absence of live
  downloads reads as intended rather than as a bug.

### Slice 5 -- Downloadable Reports page

New `Components/Pages/MessageTraceReports.razor`, `@page "/message-analysis/reports"`,
`@attribute [Authorize(Policy = "MessageTrace")]`.

- New `BulkJobRepository.GetFinishedByType(string moduleId, string jobType, int limit)`
  plus a `BulkJobService` passthrough. **Do not reuse `GetRecentFinished`**: it is
  unfiltered and capped at `BulkJobs:RecentJobLimit` (25) across all modules, so a busy
  ConferenceRooms day would hide every Message Analysis export.
- Columns: submitted (local time), submitted by (`SubmittedByDisplay` falling back to
  `SubmittedBy`), message count (`TotalRows`), ticket, status, expiry date, and a
  Download action.
- "What the trace is": derive a short descriptor from `PayloadJson` -- message count
  plus the first message's sender/recipient/subject, truncated. Deserialization must be
  wrapped: a payload that fails to parse renders as "(unavailable)" and never breaks the
  row or the page.
- Rows whose file no longer resolves render as **Expired** with the action disabled.
  Resolve via `MessageTraceExportStore.TryResolve` at render time -- do not assume the
  file is present because the job record is.
- Download action: prompt for a ticket number, then navigate to
  `{endpoint}?ticket={UrlEncode(ticket)}`. Per D2 the ticket is audit-only; an empty
  ticket must still be allowed to proceed (the module's ticket field is optional today
  -- `MessageTrace.razor:23`) and is recorded as empty.

## Tests

`ExchangeAdminWeb.Tests/`. New services require tests before the work stream is done
(`.agents/repo-guidance.md` Verification).

**`MessageTraceExportStoreTests`** (slice 1)
- `FileNameFor` round-trips with the exact name `MessageTraceDetailJobProcessor` writes
  today -- pin the current format string so a rename cannot orphan existing files.
- `TryResolve` returns `false` for a missing file and does not throw.
- Traversal is rejected for `..\..\windows\win.ini`, an absolute path, a rooted UNC
  path, and a jobId containing a directory separator. Assert **rejection**, not merely
  a `false` return, so a future refactor cannot satisfy the test by failing to find the
  traversed file.
- `ExpiresAtUtc` honours a configured `Export:RetentionDays` and defaults to 30.

**Endpoint tests** (slice 2)
- 404 for an unknown job, for a job from another module, and for a MessageTrace job of
  a different `JobType`.
- 410 with the expiry date when the job exists but the file does not.
- The audit call fires on both the hit and the miss path, with the supplied ticket.
- Prefer testing the extracted handler function directly rather than standing up a
  `WebApplicationFactory`; the repo has no integration-test host today and this plan
  does not introduce one.

**`EmailServiceTests`** (slice 3)
- The body contains the link and the expiry date, and no attachment argument.
- With `Application:PublicBaseUrl` unset, a relative link is produced and a warning is
  logged -- the send is not skipped.
- Recipient resolution matches D3, and **no admin address appears** in the recipient
  set. Assert the admin exclusion explicitly; it is the security-relevant half.

**Reports page tests** (slice 5)
- `GetFinishedByType` filters by module and job type and does not return other modules'
  jobs.
- A malformed `PayloadJson` yields "(unavailable)" instead of throwing.

**Non-vacuity proof (required):** for each new guard -- the traversal rejection, the 410
path, the admin exclusion, the module/type filter -- revert the guard, confirm the
matching test fails, restore, confirm green. Record the observed failure per guard. A
test that passes with its guard removed is vacuous and must be replaced.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` (always target the `.slnx`; bare `dotnet test`
  runs zero tests)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD` (scope to changed paths if the pre-existing unstaged
  `.gitignore` whitespace still makes the repo-wide check return non-zero)
- ASCII gate: `tools/Test-AsciiOnly.ps1`
- `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps` if
  `tools/promote-dev-to-prod.ps1` is touched in slice 3.
- **Manual, post-deploy, cannot be automated** -- state plainly if not run:
  1. Run an 11+ message export; confirm the mail arrives with a link and no attachment.
  2. Click the link while signed in: the CSV downloads.
  3. Open the link in a browser with no Windows credentials: authentication is
     demanded, not the file served.
  4. Sign in as an account **without** MessageTrace access: 403.
  5. Delete the file from `MessageTraceExports\` and re-click: the "expired" page, not
     an exception.
  6. Confirm `MessageTrace_ExportDownload` audit events appear for the hit, the miss and
     the 403, each carrying the typed ticket.

## Open Questions

- **OQ-1 (non-blocking, inherited from `docs/MessageTraceNullRow-Plan.md`):** why
  `Get-MessageTraceV2` emits a null pipeline row. Unrelated to this work; listed so it
  is not lost.
- **OQ-2 (non-blocking):** the live 1-10 download path persists nothing, so those
  exports never appear on the reports page. Slice 4 labels this on the page. Whether
  live downloads should also be persisted is a separate question, deliberately not
  decided here.

---

## Owner Gate -- D3

**Context.** The export email is changing from a zip attachment to a login-gated
download link, so the recipient address becomes only a notification target. Admins stop
receiving trace data in every option below; that part is already settled. What is not
settled is who receives the notification when the operator types nothing.

**Question.** When an operator submits an export and leaves the recipient box empty,
who gets the notification email?

**Options.**

- **(a) Default to the operator, box editable.** The operator's own address is
  pre-filled and can be replaced or added to. *Changes:* one input, pre-populated;
  matches today's behavior for anyone who ignores the box.
- **(b) No default, recipient required.** The operator must type at least one address
  before submitting. *Changes:* an extra required field and a validation error on an
  empty submit; nobody is ever mailed by accident.
- **(c) Always the operator, plus optional extras.** The operator's address is always
  included and cannot be removed; typed addresses are added to it. *Changes:* the
  requester always keeps a copy for their own record, at the cost of an unremovable
  recipient.

**Recommendation: (a).** It preserves current behavior for the common case (the
requester wants their own export), makes the arbitrary-recipient capability available
without forcing it, and adds no new failure mode on submit. (b) adds friction to the
normal path; (c) removes a choice the link-based design no longer needs to take away.

**Blocked until ruled:** slice 3 (email) and slice 4 (the UI string and recipient
input). Slices 1, 2 and 5 are unaffected and can proceed as soon as this plan is
Approved. Silence authorizes nothing.
