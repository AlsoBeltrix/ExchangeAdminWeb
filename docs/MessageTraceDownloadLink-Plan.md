# Message Analysis Export Delivery: Reports Page + Notification Link -- Plan

Status: **Draft -- awaiting owner approval and a ruling on D4 (see Owner Gate).**
Decisions D1, D2 and D3 are ruled. No code is written until this plan is Approved.
Independently reviewed 2026-07-29 (openreview, codex-commercial / gpt-5.6-sol / max,
range `68bfd25..1e98eaf`): four findings, all repaired in this revision -- see
`.agents/review/findings/mt-export-delivery-plan.md`.
App version at draft: `2.3.30` -> `2.3.31` on implementation (no shared
infrastructure change; the new page and store belong to the MessageTrace module and
the only shared touch is a DI registration).
Module: `MessageTrace` `1.2.1` -> `1.3.0` (module behavior change).
Authority: subordinate to `docs/ProjectConstitution.md`, `AGENTS.md`,
`docs/AdminModuleSpec.md`. On conflict the higher source wins.
Supersedes: `docs/MessageTraceDetail-Plan.md` decisions 5 (zip attached to the mail)
and 6 (recipient fixed to user + admins). That plan stays as history; this one is the
current intent for the export **delivery** path only. Its decisions 1-4 and 7 (live
threshold, selection UI, bulk-job routing, save-to-log-path) are unchanged.

## Problem / Goal

The Message Analysis detail export is emailed as a **zip attachment** to a recipient
set the operator cannot choose: the authenticated user's claim address plus
`Email:AdminNotificationEmail` (`Services/EmailService.cs:437-496`,
`ResolveMessageTraceRecipients` at `:483`). Three defects follow:

1. **Wrong recipients.** Every configured admin receives the actual trace data for
   every export, whether or not they asked for it. Owner direction: admins do not get
   the results.
2. **No arbitrary recipient.** The original rationale (an operator-typed address is an
   exfiltration path) holds only because the *payload* travels in the mail. If the mail
   carries a **login-gated link** instead of the file, the address is a notification
   target rather than a data channel, and the gate moves to the app's existing Windows
   authentication. Arbitrary recipients then become safe.
3. **Leaked reasoning in the UI.** `Components/Pages/MessageTrace.razor:399` renders
   `Emailed reports are sent only to <strong>@DestinationDisplay()</strong> - never a
   typed-in address.` The trailing clause explains a threat model the operator cannot
   see -- there is no address input on the page -- and must not ship.

Goal: stop mailing trace data, allow any notification recipient, and deliver exports
through a **Downloadable Reports** page inside the app, gated by the existing
MessageTrace module policy and prompting for a ticket number at download time.

## Owner Decisions

### D1 -- Retention: out-of-process (RULED)

> "B. cleanup is handled out of this process by a scheduled task."
> "the task only deletes files older than 30 days. that's enough time. you can even
> put in the email that the file is available until N date."

The app does **not** delete export files. An existing scheduled task on the host
removes files older than 30 days from the audit log root. Consequences that bind this
plan:

- The reports page must treat a missing file as a normal, expected state ("expired"),
  never an error or an unhandled exception. **Expired is not the only reason a file can
  be absent** -- see the Failed-vs-Expired rule under slice 2, which openreview F1
  forced into this plan.
- The notification email states an availability date, computed as
  `submittedAtUtc + 30 days`. **Thirty is a constant in `MessageTraceExportStore`, not a
  config key.** It is **descriptive only** -- it mirrors the external task's policy and
  enforces nothing. Comment it at the declaration so a future agent does not mistake it
  for the deletion mechanism.
- **Do not make it configurable.** The first draft of this plan added a global
  `Export:RetentionDays`. openreview F4 killed it on two grounds, both correct: a
  MessageTrace-only setting in a global key violates `docs/ProjectConstitution.md:59`
  ("Module-specific settings belong to that module's config, not global
  `appsettings.json`"), and -- the sharper objection -- because the app never deletes,
  a configurable value is a second source of retention truth that can silently disagree
  with the scheduled task. Setting it to 60 while the task deletes at 30 promises the
  operator a file that is already gone. Relocating it to MessageTrace module config was
  considered and rejected: it satisfies the Constitution while preserving exactly the
  drift the finding is about. If the host task's schedule ever changes, change the
  constant in the same commit that changes the task.
- `BulkJobs:RetentionDays` already defaults to 30 (`Services/Jobs/BulkJobService.cs:65`),
  so the job record and the file expire on comparable schedules. They are independent
  clocks; neither may assume the other. A file whose job record has been pruned is
  unreachable through the page -- acceptable, because the page enumerates job records,
  not directory contents (see "Enumeration source" below).

### D2 -- Authorization: module access, not per-user ownership (RULED)

> "message trace isn't privileged and anyone who can log in and is able to access the
> message analysis module should be able to download it."

The gate is the existing `MessageTrace` catalog policy. **No ownership check**: an
operator with module access may download any export, not only their own. The ticket
number is an **audit prompt, not an authorization control** -- it is recorded, never
validated.

### D3 -- Delivery mechanism: a Razor page, not an HTTP endpoint (RULED)

> "why does it need to be an http endpoint? can't we have a razor page enum the files
> and expose them?"

It does not. The prior draft of this plan called for the app's first minimal-API
endpoint; the owner correctly rejected the premise. The app already serves file
downloads from a Razor page: `DownloadSelectedDetails()`
(`Components/Pages/MessageTrace.razor:927-968`) reads a CSV into memory, base64-encodes
it, and hands it to the `downloadFile` JS helper (`Components/App.razor:54-68`), which
builds a Blob client-side. The reports page reuses that mechanism unchanged.

Two claims in the prior draft that did not survive scrutiny and must not be
reintroduced as justification for an endpoint:

- *"The circuit transfer is unacceptable for a stored export."* Overstated. Base64
  inflates the payload by roughly a third, and the export is a CSV of at most 50
  messages (`MessageTraceDetailReport.EmailMax`). The live path already does exactly
  this for up to 10. The difference is not material at this size.
- *"A link in an email cannot target a Razor page."* False. The Negotiate challenge
  happens on the initial page request, before the Blazor circuit starts, so the login
  gate is identical either way.

What an endpoint would have bought: a one-click download straight from the email link.
What it would have cost: the app's first HTTP endpoint, which must opt into
authorization correctly against a deny-by-default fallback
(`Modules/ModuleCatalog.cs:72-74`) and carries its own traversal and streaming
handling. One saved click does not justify new security surface in an app where all
~28 routes are Razor pages with `[Authorize]` attributes.

**The email therefore links to the reports page, not to a file.** This is strictly
better for the audit requirement, and that is a consequence worth stating plainly: a
direct file link carrying `?ticket=` in the URL would produce an audit record with an
empty ticket whenever someone clicked the emailed link, because nobody types a ticket
into a URL. Routing through the page means the ticket is **prompted for at download
time**, so the owner's "download link requiring a ticket number as the audit check"
is actually enforced rather than nominal.

### D4 -- Email recipients: the default set when the operator types nothing (OPEN -- BLOCKS SLICE 3)

See the Owner Gate at the end of this document. Slices 1, 2 and 4 do not depend on it
and may proceed once this plan is Approved.

## Non-Goals

- **No HTTP endpoint, controller, or minimal API.** Ruled out by D3. An implementing
  agent that finds the circuit transfer distasteful must raise it, not add a route.
- **No change to what the export contains.** CSV columns, ordering, the 50-message
  cap, the 10-message live threshold, and the bulk-job routing are untouched
  (`docs/MessageTraceDetail-Plan.md` decisions 1-4).
- **No in-app file deletion or retention sweep.** D1 places this out of process. Do not
  add a pruner, a hosted timer, or a startup cleanup for export files.
- **No ServiceNow validation or writeback** on the ticket field. It is plain audit
  metadata (Constitution, "External Integrations" and "Never Do"). Requiring the field to
  be non-blank (F2) is a presence check, not validation -- do not let it grow into one.
- **No configurable retention value.** Thirty days is a constant mirroring the host
  scheduled task (D1, F4). Do not add `Export:RetentionDays`, a module-config equivalent,
  or any other knob.
- **No new catalog module.** The reports page belongs to the existing `MessageTrace`
  module and reuses its policy. A new module would create a second access gate for
  data the owner ruled sits behind the MessageTrace gate.
- **No change to the live (1-10 message) in-browser download path**, beyond what slice
  4 states explicitly.

## Constitution Conflict To Record, Not To Silently Resolve

`docs/ProjectConstitution.md` "Never Do": *"Do not write durable state into locations
that deployment or log pruning scripts delete."*

This plan makes an externally-pruned directory the backing store for a link the app
hands out. That is a real tension with the rule, and the implementing agent must not
treat it as an oversight to "fix" by adding in-app retention -- D1 forbids that.

(This section was present in the first draft, dropped in the D3 rewrite, and restored
after openreview. The D3 change from an HTTP endpoint to a Razor page does not touch the
tension: the page still hands out links backed by a pruned directory.)

The tension is resolved as follows, and this reasoning must be reproduced in the code
comment on the download handler:

- The export file is a **convenience artifact, not durable state**. The authoritative
  durable records are the audit event (`MessageTrace_DetailExport`, written with the
  saved path at `Services/Jobs/MessageTraceDetailJobProcessor.cs:171-173`) and the job
  record. Neither depends on the file surviving.
- Every consumer of the file -- the reports page and the email -- is required to render
  its absence as an ordinary outcome, never an error. Under openreview F1 that outcome
  is now **two** states, not one: **Expired** (written successfully, since pruned) and
  **Failed** (never written). Nothing breaks when the scheduled task removes a file, and
  a write error is never disguised as retention.

If a future change makes the file authoritative for anything -- a compliance record, an
input to another feature, the only copy of something not reconstructible by re-running
the trace -- this exemption lapses and the conflict must be re-opened with the owner
rather than re-resolved here.

## Current-State Facts The Implementation Depends On

Verified against the tree at draft time. Re-read before editing (Known Failure Class
number 4: never trust remembered file contents).

| Fact | Evidence |
|------|----------|
| The app serves downloads from Razor pages via base64 + a JS Blob helper. There is no server-side streaming path today. | `Components/App.razor:54-68`, `Components/Pages/MessageTrace.razor:950-954` |
| Every route is a Razor page; there are no controllers or minimal APIs. | `Program.cs:236-238`, `@page` scan across `Components/Pages/` |
| `options.FallbackPolicy` denies any endpoint declaring no authorization metadata. Razor components are exempt because `MapRazorComponents<App>().RequireAuthorization()` stamps the default policy on them. | `Modules/ModuleCatalog.cs:60-74`, `Program.cs:236-238` |
| The `MessageTrace` policy alias is `"MessageTrace"`, fail-closed, dynamic group-backed. | `Modules/ModuleCatalog.cs:225`, `:94-97` |
| The trace page declares `@attribute [Authorize(Policy = "MessageTrace")]`. | `Components/Pages/MessageTrace.razor:7` |
| Exports are written to `<logRoot>\ExchangeAdminWeb\MessageTraceExports\` as `MessageTraceDetail_{job.Id}_{job.SubmittedAtUtc:yyyyMMdd-HHmmss}.csv`, fail-soft (errors logged and swallowed). | `Services/Jobs/MessageTraceDetailJobProcessor.cs:106-112`, `:133-149` |
| `AuditLogRoot.Require(config)` is the only sanctioned way to resolve the log root and fails loud when unset. | `Services/Jobs/MessageTraceDetailJobProcessor.cs:137` |
| The job record carries `Id`, `ModuleId`, `JobType`, `SubmittedBy`, `SubmittedByDisplay`, `SubmittedIp`, `Ticket`, `SubmittedAtUtc`, `FinishedAtUtc`, `Status` and row counts. | `Services/Jobs/BulkJobModels.cs:46-108` |
| The job payload (`PayloadJson`) holds the selected `MessageTraceResult` rows and `UserEmail` -- the only source for a "what the trace is" descriptor. | `Services/Jobs/MessageTraceDetailJobPayload.cs:18-27` |
| `BulkJobRepository.GetRecentFinished(limit)` is **not** filtered by module or job type and is capped by `BulkJobs:RecentJobLimit` (default 25). It cannot back the reports page as written. | `Services/Jobs/BulkJobRepository.cs:284-295`, `Services/Jobs/BulkJobService.cs:64,172` |
| `Application:PathBase` defaults to `/ExchangeAdminWeb` and is patched per environment by promotion. Any absolute URL in an email must come from config, never hardcoded. | `Program.cs:217-221`, `tools/promote-dev-to-prod.ps1:169,188` |
| `EmailService` has **no** base-URL or public-URL configuration today. | no `BaseUrl`/`PublicUrl` match in `Services/EmailService.cs` |
| `LogLookupAction(performedBy, ip, action, target, success, errorDetail, ticketNumber)` is the audit entry point the module already uses for reads. | `Services/AuditService.cs:189-196`, `Components/Pages/MessageTrace.razor:956` |

## Enumeration Source: Job Records, Not The Directory

The owner's phrasing was "a razor page enum the files". The page enumerates **job
records filtered to this module and job type**, then resolves each one's file --
not `Directory.GetFiles`. This is a deliberate departure from the literal wording and
the reason is load-bearing:

- The metadata the owner asked for ("the basics for who and what the trace is") exists
  only in the job record and its payload. A filename carries a GUID and a timestamp and
  nothing else, so a directory listing cannot render who ran the trace, the ticket, or
  what was searched.
- A directory listing would surface any file that happens to sit in that folder,
  including anything a future feature writes there. Enumerating typed job records keeps
  the page's contents defined by construction.

The cost, stated plainly: a file whose job record has aged out of the jobs DB becomes
invisible to the page even if the file survives on disk. Both retention windows default
to 30 days (D1), so the gap is small, and the file remains reachable on disk for an
administrator. Accepted.

## Approach

Three moving parts.

1. **A resolver service** (`MessageTraceExportStore`) owning the export directory, the
   filename convention, jobId validation, and existence checks -- one implementation,
   one set of tests, shared by the writer and the page.
2. **A Downloadable Reports page** at `/message-analysis/reports`, listing terminal
   `MessageTrace_DetailExport` jobs with who / when / what / ticket, with a download
   action that prompts for a ticket number and then serves the file through the
   existing JS Blob helper.
3. **An email that links to that page** instead of attaching a zip, with a configurable
   recipient and a stated availability date.

## Slices

One commit per slice. Commit each before starting the next (`AGENTS.md` Git Safety;
`.agents/repo-guidance.md` Earned Practices).

### Slice 1 -- `MessageTraceExportStore` (resolver + safety)

New `Services/MessageTraceExportStore.cs`, registered scoped in `Program.cs`.

```
string   DirectoryPath { get; }                  // <logRoot>\ExchangeAdminWeb\MessageTraceExports
string   FileNameFor(string jobId, DateTime submittedAtUtc)
bool     TryResolve(string jobId, DateTime submittedAtUtc, out string fullPath)
DateTime ExpiresAtUtc(DateTime submittedAtUtc)   // submittedAtUtc + RetentionDays (const 30)
```

Requirements:

- Resolve the root only via `AuditLogRoot.Require(_config)`. Do not re-derive it.
- **Reject any `jobId` that is not a 32-character hex GUID "N" string** before it
  touches the filesystem. Job IDs are assigned as GUID "N"
  (`Services/Jobs/BulkJobModels.cs:48`), so this is a total whitelist, not a blacklist,
  and it closes path traversal at the parse step rather than by sanitising.
- After composing the path, assert `Path.GetFullPath(candidate)` starts with
  `Path.GetFullPath(DirectoryPath) + Path.DirectorySeparatorChar`. Belt and braces: the
  whitelist should make this unreachable, and this is the guard that survives a future
  change to the ID format.
- `TryResolve` returns `false` for a missing file. It does not throw and does not create
  the directory.
- Move the filename construction out of `MessageTraceDetailJobProcessor:106-107` and the
  directory construction out of `SaveToLogPath:138` to call this store, so the writer and
  the reader cannot drift apart. Keep `SaveToLogPath`'s fail-soft try/catch -- a save
  failure must still never fault the job (`OnJobCompletedAsync` is documented fail-safe,
  `:89-90`) -- but the **caller must now branch on its null return**. See the next
  subsection: swallowing the failure was correct while the mail carried the data, and
  becomes a defect the moment the file is the only copy.

Note for the implementer: the traversal guard is defence in depth even though every
jobId on the read path comes from a job record rather than from user input. It is cheap,
and it is the difference between a bug in a future caller being a bug and being a file
disclosure.

### A Failed Save Must Never Produce A "Ready" Email (openreview F1)

This is the finding the plan's first two drafts both got wrong, and the reasoning must
survive into the code, not just this document.

Today `SaveToLogPath` logs and swallows (`MessageTraceDetailJobProcessor.cs:144-148`)
and the processor then emails **unconditionally** (`:112`, `:116-118`). That was
correct: the zip travelled *in* the mail, so a save failure cost only the archive copy
and the operator still received their data.

This plan removes the attachment. The saved file becomes the **sole** delivery. The same
swallowed catch would then turn a disk-full or permissions failure into a
"your export is ready" email pointing at nothing, which the reports page would render as
**Expired** -- indistinguishable from ordinary 30-day retention. The operator would
conclude they waited too long, and the export would be unrecoverable.

The signal already exists and simply does not reach the operator: `Audit` at
`MessageTraceDetailJobProcessor.cs:164-174` already writes `success: savedPath is not
null` with the detail `"log save failed"`.

Binding rules for the implementation:

- The processor branches on `savedPath`. Non-null: the ready-and-linked email of slice 3.
  Null: an explicit **failure** notice naming the ticket and the message count, saying
  the export could not be stored and must be re-run. **Never a ready-with-link mail.**
- The audit call stays as it is; it is already correct.
- The job result is unchanged either way -- the job still completes. A save failure is a
  delivery failure, not a job failure, and mail formatting must never change a job
  result.
- The reports page distinguishes **Failed** (the job record says the save failed) from
  **Expired** (the save succeeded and the file has since been removed). Retention must
  never be blamed for a write error. Source: the existing `MessageTrace_DetailExport`
  audit row is the durable record of which happened; if the page cannot cheaply read
  that, carry a save-failed marker in the job's `Message` field
  (`Services/Jobs/BulkJobModels.cs:104`) and say in the code comment which source was
  chosen and why.

### Slice 2 -- Downloadable Reports page

New `Components/Pages/MessageTraceReports.razor`, `@page "/message-analysis/reports"`,
`@attribute [Authorize(Policy = "MessageTrace")]`, `@rendermode InteractiveServer`.

- New `BulkJobRepository.GetFinishedByType(string moduleId, string jobType, int limit)`
  plus a `BulkJobService` passthrough. **Do not reuse `GetRecentFinished`**: it is
  unfiltered and capped at `BulkJobs:RecentJobLimit` (25) across all modules, so a busy
  ConferenceRooms day would hide every Message Analysis export.
- Columns: submitted (local time), submitted by (`SubmittedByDisplay` falling back to
  `SubmittedBy`), message count (`TotalRows`), what was traced, ticket, status, expiry
  date, download action.
- "What the trace is": derive a short descriptor from `PayloadJson` -- message count plus
  the first message's sender/recipient/subject, truncated. Deserialization must be
  wrapped: a payload that fails to parse renders "(unavailable)" and never breaks the row
  or the page.
- Rows whose file no longer resolves render as **Expired** or **Failed**, action
  disabled, per the Failed-vs-Expired rule above. Resolve via
  `MessageTraceExportStore.TryResolve` at render time -- never assume the file exists
  because the job record does.
- **Download action:** prompt for a ticket number (an inline field or a small modal --
  match whatever the module already does for its ticket input rather than inventing a
  pattern), then read the file and hand it to `downloadFile` exactly as
  `DownloadSelectedDetails()` does at `:950-954`. Read with
  `File.ReadAllBytesAsync`; do not re-encode the CSV text, since the file was written
  UTF-8 without BOM (`MessageTraceDetailJobProcessor:141`) and a round-trip through
  string could change that.
- **Re-check the file at click time, not only at render.** A row rendered minutes ago may
  point at a file the scheduled task has since removed. A click on a vanished file shows
  the expired state and refreshes the row; it must not throw.
- Audit every download attempt, success and failure, via
  `LogLookupAction(currentUser, clientIpAddress, "MessageTrace_ExportDownload", target: $"job {jobId}", success, errorDetail, ticket)`.
  Per D2 the ticket is **recorded, never validated** -- no ServiceNow lookup, no
  authorization weight. An audit failure must not fail the download (Constitution,
  "Auditing And Tracing").
- **A non-blank ticket is required to download** (openreview F2). Trim, and refuse an
  empty value with inline validation before the file is read. The first draft allowed a
  blank through by analogy with the module's optional ticket field
  (`MessageTrace.razor:23`), which confused two separate questions: D2 settles
  *validation* (never), it does not settle *presence*. The owner's word was a download
  "requiring a ticket number as the audit check", and a blank ticket makes that check
  nominal -- the exact failure mode D3 cites as the reason to route through a page
  instead of an emailed `?ticket=` URL.
  *Assumption, reversible:* the owner may prefer the blank-permitted behavior for
  consistency with the search page's optional field. That is a one-line change and does
  not block this slice; it is flagged in the state entry rather than held as a gate.
- Add the page to the Trace Search tab as a link, and confirm it does **not** need a
  `ModuleCatalog` nav entry -- it is a sub-page of an existing module, not a new one.

### Slice 3 -- Email: link to the reports page, no attachment (BLOCKED ON D4)

`Services/EmailService.cs`:

- Change `SendMessageTraceResultAsync` to take a **reports-page URL** and an **expiry
  date** in place of `byte[] zipBytes` / `zipFileName`.
- Delete `ResolveMessageTraceRecipients` (`:483-496`) and its call site; replace it with
  the recipient set D4 selects. **Admins leave the trace-data path either way** -- that
  half is owner-ruled independent of D4.
- Rewrite the body (`:455-470`): drop "attached as a zip file"; state that the export is
  ready, link to the reports page, and give the availability date. Note that a ticket
  number will be requested at download. HTML-encode every interpolated value via the
  existing `h(...)` helper (`:453`).
- Delete `ZipSingleFile` (`MessageTraceDetailJobProcessor.cs:151-162`) and the
  `System.IO.Compression` using (`:2`) once nothing references them.

Absolute-URL construction: no base-URL config exists today. Add
`Application:PublicBaseUrl` (e.g. `https://host/ExchangeAdminWeb`).

**When unset, omit the hyperlink entirely** -- state in prose where the export is (the
Downloadable Reports page in the app, named via `Application:Name`), and log a warning.
Do not guess a scheme and host, and do not fail the send.

openreview F3 caught the first draft here: it fell back to a **relative** path, which an
email client cannot resolve against any origin, so every deployment without the new key
would receive a dead hyperlink. A dead link is worse than no link -- it reads as a broken
app and gives the operator nothing to act on. The reviewer's proposed remedy (require and
validate an absolute HTTPS URL) was **not** adopted verbatim, because failing the send
conflicts with fail-safe completion: the job has already finished at this point
(`MessageTraceDetailJobProcessor.cs:89-90`) and a mail-formatting problem must never
change a job result. Omitting the link satisfies the finding without that cost.

Add the key everywhere config is authored, or fresh installs inherit the degraded path:
`README.md`'s config table (`README.md:532-533`);
`tools/Install-ExchangeAdminWeb.ps1:439-443` (the `Application` block, beside `PathBase`
and `ContactEmail`); `deploy.ps1:735-738` (same block); and
`tools/promote-dev-to-prod.ps1` beside the existing `Application:PathBase` patch, so dev
and prod do not silently share one URL.

### Slice 4 -- Remove the leaked UI string; wire the recipient input (BLOCKED ON D4 for the input only)

`Components/Pages/MessageTrace.razor`:

- Delete the `- never a typed-in address` clause at `:399`, and update the submission
  confirmation at `:409` which likewise promises a zipped report by email.
- Under D4 the surrounding sentence is either rewritten to name the chosen recipient or
  removed along with `DestinationDisplay()` (`:916-925`).
- Add the recipient input if D4 calls for one, with client-side format validation only.
  Do not add domain allow-listing: the data is behind the login gate, which is the point
  of the redesign.
- Leave `DownloadSelectedDetails()` (`:927-968`) untouched. The live 1-10 path stays an
  in-browser blob that persists nothing, so the reports page lists **emailed/bulk exports
  only**. Say so on the page, so the absence of live downloads reads as intended rather
  than as a bug.

## Tests

`ExchangeAdminWeb.Tests/`. New services require tests before the work stream is done
(`.agents/repo-guidance.md` Verification).

**`MessageTraceExportStoreTests`** (slice 1)
- `FileNameFor` round-trips the exact name `MessageTraceDetailJobProcessor` writes today
  -- pin the current format so a rename cannot orphan existing files.
- `TryResolve` returns `false` for a missing file and does not throw.
- Traversal is rejected for `..\..\windows\win.ini`, an absolute path, a rooted UNC path,
  and a jobId containing a directory separator. Assert **rejection**, not merely a
  `false` return, so a refactor cannot satisfy the test by simply failing to find the
  traversed file.
- `ExpiresAtUtc` returns `submittedAtUtc + 30 days`. Pin the constant in the test, so a
  change to the host scheduled task's window cannot drift the app's promise silently.

**Save-failure tests** (slice 1, the openreview F1 guard -- the highest-value test here)
- With the export directory unwritable, the processor sends the **failure** notice and
  **not** the ready-and-linked email. Assert on which email was sent, not merely that one
  was: a test that only counts sends passes with the defect present.
- The job still completes in that case (a delivery failure is not a job failure).
- With the save succeeding, the ready email is sent and the failure notice is not.

**Repository / page-logic tests** (slice 2)
- `GetFinishedByType` filters by module and job type and returns no other module's jobs.
- `GetFinishedByType` is not bounded by `BulkJobs:RecentJobLimit` -- seed more than the
  recent-job limit and assert they are all returned. This is the specific bug that
  reusing `GetRecentFinished` would cause.
- A malformed `PayloadJson` yields "(unavailable)" instead of throwing.
- A job whose save failed renders **Failed**, and a job whose save succeeded but whose
  file is gone renders **Expired**. Assert the two are distinguishable; collapsing them
  is the F1 defect.
- A blank ticket does not download (F2). Assert the file is never read, not merely that
  a message is shown.
- Extract the descriptor, expiry, and state-classification logic into testable methods
  rather than leaving them inline in markup; the repo has no bUnit harness and this plan
  does not add one.

**`EmailServiceTests`** (slice 3)
- The body contains the reports-page link and the expiry date, and no attachment
  argument is passed.
- With `Application:PublicBaseUrl` unset, **no hyperlink is emitted** (assert no `href`
  and no bare relative path in the body), the prose fallback is present, a warning is
  logged, and the send is not skipped (F3).
- With it set, the body contains the absolute URL.
- Recipient resolution matches D4, and **no admin address appears** in the recipient set.
  Assert the admin exclusion explicitly; it is the security-relevant half.

**Non-vacuity proof (required):** for each new guard -- the save-failure branch,
Failed-vs-Expired, the required ticket, traversal rejection, the unfiltered-limit
assertion, the malformed-payload fallback, the no-relative-link rule, the admin
exclusion -- revert the guard, confirm the matching test fails, restore, confirm green.
Record the observed failure per guard. A test that passes with its guard removed is
vacuous and must be replaced.

## Verification

- `dotnet build ExchangeAdminWeb.slnx -c Release`
- `dotnet test ExchangeAdminWeb.slnx` (always target the `.slnx`; bare `dotnet test` runs
  zero tests)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`
- `git diff --check HEAD` (scope to changed paths if the pre-existing unstaged
  `.gitignore` whitespace still makes the repo-wide check return non-zero)
- ASCII gate: `tools/Test-AsciiOnly.ps1`
- `Invoke-ScriptAnalyzer -Path . -Recurse` and `Invoke-Pester tests/ps` -- slice 3 now
  touches `tools/Install-ExchangeAdminWeb.ps1`, `deploy.ps1`, and
  `tools/promote-dev-to-prod.ps1`, so this is required, not conditional. Keep
  `Install-ExchangeAdminWeb.ps1` environment-neutral (`.agents/repo-guidance.md`
  Architectural Invariant 1): it gains the key with a blank default, never an ADI host.
- **Manual, post-deploy, cannot be automated** -- state plainly if not run:
  1. Run an 11+ message export; confirm the mail arrives with a link and no attachment.
  2. Follow the link while signed in: the reports page lists the export.
  3. Download it: the ticket prompt appears; the CSV arrives and opens correctly.
  4. Follow the link with no Windows credentials: authentication is demanded.
  5. Sign in as an account **without** MessageTrace access: access denied, no listing.
  6. Delete the file from `MessageTraceExports\` and retry: the row shows Expired and the
     action is disabled; clicking a row whose file vanished after render does not throw.
  7. Confirm `MessageTrace_ExportDownload` audit events record the hit, the miss, and the
     typed ticket.
  8. Make the export directory unwritable and run an export: the mail is the **failure**
     notice with no link, the row shows Failed (not Expired), and the job still completes
     (openreview F1).
  9. With `Application:PublicBaseUrl` unset, confirm the mail contains prose and no
     hyperlink at all -- specifically no bare `/message-analysis/reports` (F3).

## Open Questions

- **OQ-1 (non-blocking, inherited from `docs/MessageTraceNullRow-Plan.md`):** why
  `Get-MessageTraceV2` emits a null pipeline row. Unrelated to this work; listed so it is
  not lost.
- **OQ-2 (non-blocking):** the live 1-10 download path persists nothing, so those exports
  never appear on the reports page. Slice 4 labels this on the page. Whether live
  downloads should also be persisted is a separate question, deliberately not decided
  here.
- **OQ-3 (non-blocking, from openreview F2):** the required-ticket rule is this plan's
  reading of the owner's word "requiring", not an explicit ruling. If the owner prefers a
  blank-permitted ticket for consistency with the search page's optional field, it is a
  one-line change in slice 2. Not held as a gate.
- **OQ-4 (non-blocking, from openreview F1):** the Failed-vs-Expired distinction adds a
  page state the owner did not ask for. It exists because the alternative is silently
  mislabelling a write error as retention. Flagged in case the owner considers it scope
  the delivery-path plan should not carry.

---

## Owner Gate -- D4

**Context.** The export email is changing from a zip attachment to a link to the reports
page, so the recipient address becomes only a notification target -- the data stays
behind the login gate. Admins stop receiving trace data in every option below; that part
is settled. What is not settled is who receives the notification when the operator types
nothing.

**Question.** When an operator submits an export and leaves the recipient box empty, who
gets the notification email?

**Options.**

- **(a) Default to the operator, box editable.** The operator's own address is pre-filled
  and can be replaced or added to. *Changes:* one pre-populated input; matches today's
  behavior for anyone who ignores the box.
- **(b) No default, recipient required.** The operator must type at least one address
  before submitting. *Changes:* an extra required field and a validation error on empty
  submit; nobody is ever mailed by accident.
- **(c) Always the operator, plus optional extras.** The operator's address is always
  included and cannot be removed; typed addresses are added to it. *Changes:* the
  requester always keeps a copy for their own record, at the cost of an unremovable
  recipient.

**Recommendation: (a).** It preserves current behavior for the common case (the requester
wants their own export), makes the arbitrary-recipient capability available without
forcing it, and adds no new way to fail a submit. (b) adds friction to the normal path;
(c) removes a choice the link-based design no longer needs to take away.

**Blocked until ruled:** slice 3 (email) and the recipient-input half of slice 4. Slices
1, 2, and the string removal in slice 4 are unaffected and can proceed as soon as this
plan is Approved. Silence authorizes nothing.
