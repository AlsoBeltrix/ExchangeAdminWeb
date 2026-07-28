# Message Analysis — Per-Message Delivery Detail Plan

**Status:** Implemented (2026-07-27 — all 9 slices landed and codex-reviewed accepted; see Implementation status below)
**Date:** 2026-07-27
**Target version:** MessageTrace module `Version` bump only (1.1.1 -> 1.2.0); no base app bump
**Module:** `MessageTrace` ("Message Analysis"), route `message-analysis`

## Context

The Message Analysis Trace Search returns a flat summary list and a bulk CSV
(`Components/Pages/MessageTrace.razor` `ExportCsv`, 13 columns). It shows one
status per message, not the per-hop delivery trail that explains WHY a message
was deferred/failed/quarantined/expanded. Collapsing the whole event sequence
into one line is the core defect: an operator cannot diagnose a delivery problem
from a single EventId.

Two separate losses of on-prem detail, verified in `Services/MessageTraceService.cs`:

1. **At the query** (`:360`): `Get-MessageTrackingLog | Select-Object` trims to 11
   fields. Dropped there and never leaving PowerShell: `Source`, `SourceContext`,
   `RecipientStatus` (the reason text, e.g. "550 5.7.1 ..."), `ConnectorId`,
   `ClientHostname`, `EventData`.
2. **At the dedupe** (`:279-284`): a single message produces MANY event rows
   (RECEIVE -> SUBMIT -> DELIVER/FAIL/DEFER/...); the `GroupBy(...).Select(g =>
   g.First())` collapses them to one row per message+recipient, discarding the
   trail.

Backends are asymmetric:

- **On-prem** (`Get-MessageTrackingLog`): the full event trail is ALREADY
  returned by the query run today; it is trimmed then collapsed. Recovering it
  costs ZERO extra backend queries.
- **Cloud** (`Get-MessageTraceV2`, `:112-116`): returns only the summary. The
  per-message trail requires a SEPARATE `Get-MessageTraceDetailV2` call per
  message. This is the only per-message cost, and the reason detail is capped.

## Owner decisions (approved in chat 2026-07-27)

1. **On-prem: always full detail.** Never collapse; keep every event row plus the
   reason fields (`Source`, `SourceContext`, `RecipientStatus`). Free.
2. **Screen: click-to-retrieve, any size.** The default results list is unchanged;
   the user clicks a single message to fetch and show its trail (one call). This
   is available regardless of result-set size.
3. **Row selection.** Add a checkbox per results row plus a **select-all**
   checkbox. Select-all ticks at most the **first 50** rows (never more than the
   email cap).
4. **Threshold rule (drives the action controls):**
   - **1-10 selected:** live retrieval allowed -> download the file (or email if
     the email option is chosen).
   - **11-50 selected:** live/download is disabled; **email is the only option**.
   - UI states plainly: "Only 10 messages can be retrieved live; more than 10 must
     be emailed."
5. **Email path.** When email is chosen, the detail pull runs as an off-circuit
   **bulk job** (the existing `IBulkJobProcessor` / `BulkJobService` runner used by
   ConferenceRooms), so it survives a dropped browser connection. Cap **50**. On
   completion the assembled file is **zipped and attached** to the email (owner
   decision B — no server file-share link; we do not store trace data in wwwroot
   and will not introduce a new shared path). Recipients: the **logged-in
   operator's own mailbox** (from the captured `userEmail` claim,
   `MessageTrace.razor:448-451`) AND the configured admins (EmailService
   `_adminEmail`). The file is also saved to the audit log path.
6. **Recipient gating.** The email destination is NOT an operator-typed address.
   It is fixed to the logged-in user's own mailbox + admins. Rationale: emailing
   trace data (senders/recipients/subjects/IPs) to an arbitrary typed address is a
   data-exfiltration path; locking it to the authenticated identity removes it.
7. **Save to log path.** The generated file (pre-zip) is written under
   `AuditLogRoot.Require(config)` -> `<logRoot>\ExchangeAdminWeb\MessageTraceExports\`
   (same rooting pattern as `EmergencyDisableService` snapshots), for the audit
   record.

## Scope of changes

### New service method — `Services/MessageTraceService.cs`

`GetMessageDetailAsync(MessageTraceResult message, CancellationToken)` returning a
new `MessageTraceDetail`. Routes by `message.Backend`:

- **`OnPrem`:** re-run `Get-MessageTrackingLog` scoped to the one message
  (`-MessageId`, its `-Start`/`-End` window, its server) and DO NOT collapse:
  widen the `Select-Object` to include `Source`, `SourceContext`,
  `RecipientStatus`, and return every event row ordered by timestamp. Reuses the
  existing on-prem connect + throttle + credential path; a scoped repeat of a
  query the module already runs (no new cmdlet/permission). Per-message re-query
  is chosen over threading raw events through the summary list so the list query
  stays cheap and unchanged.
- **`ExchangeOnline`:** call `Get-MessageTraceDetailV2` via `RunPooledQueryAsync`
  (read-only, `allowRetry: true`), keyed by the already-captured `MessageTraceId`
  + `RecipientAddress`. Map each event (Date, Event, Action, Detail, Data). Handle
  the "cmdlet not recognized" outdated-module case as `GetCloudMessageTraceAsync`
  does (`:164-172`).

Both paths fail-soft: on error, `MessageTraceDetail.Error` is set; the row and the
rest of the page/job are unaffected. A cloud message aged out of the trace window
returns empty events with an explanatory message, not an exception.

### New model — `Models/LookupModels.cs`

```
public class MessageTraceDetailEvent
{
    public DateTime Date { get; set; }
    public string Event { get; set; } = "";   // EventId (on-prem) / Event (cloud)
    public string Action { get; set; } = "";   // cloud Action; "" on-prem
    public string Detail { get; set; } = "";   // Detail / SourceContext / RecipientStatus
    public string Source { get; set; } = "";   // Source (on-prem) / "" cloud
}

public class MessageTraceDetail
{
    public MessageTraceResult Summary { get; set; } = default!;
    public List<MessageTraceDetailEvent> Events { get; set; } = new();
    public string? Error { get; set; }
}
```

### Detail-file assembly — pure testable helper

A pure static builder (e.g. `MessageTraceDetailReport`) takes a list of
`MessageTraceDetail` and produces the export text (CSV): per message a header
block of its summary fields, then its full event trail, `CsvEscape` applied.
Extracted so it is unit-testable without EXO/UI/JS. Used by both the download path
and the email/bulk-job path so the two produce identical content.

### New email method — `Services/EmailService.cs`

`SendMessageTraceResultAsync(string userEmail, byte[] zipBytes, string zipFileName,
int messageCount, string ticket, string performedBy)`: sends to `userEmail` + the
configured admins, HTML body summarizing the job (count, ticket, performer, IP,
timestamp), with the zip attached via MimeKit `BodyBuilder.Attachments.Add`
(no attachment helper exists yet; add one). Mirrors the existing
Send*NotificationAsync family; `virtual` for test-seaming. Failure logged, never
throws into the job result.

### New bulk-job processor — `Services/Jobs/`

`MessageTraceDetailJobPayload` (the selected messages + ticket + requesting
userEmail) and a `MessageTraceDetailJobProcessor : IBulkJobProcessor`
(`ModuleId = "MessageTrace"`): `CountRows` = selected message count (<= 50);
`ProcessRowAsync` fetches one message's detail via `GetMessageDetailAsync`
(fail-soft per row -> a Failed row, never aborts the batch);
`OnJobCompletedAsync` assembles the report from the per-row details, saves it to
the log path, zips it, and calls `SendMessageTraceResultAsync`. Registered in the
`BulkJobProcessorRegistry` alongside the ConferenceRooms processor. Per-row and
completion honor the fail-safe/aggregation rules the runner already enforces.

Note: the runner persists only row outcome (`BulkJobRow`), not the fetched detail
payload. The processor must retain the fetched details for the completion step —
either by re-fetching in `OnJobCompletedAsync` (rejected: doubles cloud cost) or
by holding them in the processor instance for the job's DI scope. The processor is
resolved once per job in a fresh scope (per `IBulkJobProcessor` docs), so an
instance field accumulating the per-row `MessageTraceDetail` is valid and is the
chosen approach; document it in the processor.

### UI — `Components/Pages/MessageTrace.razor`

- Per-row **Details** action (`:380-391`): click -> `GetMessageDetailAsync`,
  spinner, then render the trail (sub-table: Date, Event, Action/Source, Detail)
  in an expandable region. One open at a time is sufficient. Available at any
  result-set size (decision 2).
- Per-row **checkbox** + **select-all** (decision 3; select-all caps at first 50).
- Action controls driven by selected count (decision 4): 1-10 -> "Download
  details" enabled (and an "Email instead" choice); 11-50 -> download disabled,
  "Email details" the only action, with the stated message. An **email option**
  (checkbox or the forced email button) submits the bulk job; the destination is
  shown read-only as the logged-in user's mailbox + admins (decision 6) — no
  address textbox.
- On submit of an email job: enqueue via `BulkJobService`, show the standard
  job-submitted confirmation; the file arrives by email off-circuit.
- Audit via `Audit.LogLookupAction` (as the trace run does, `:525`): live detail
  fetch, download, and email-job submission each audited, action `MessageTrace`,
  target = message id(s), carrying the ticket.

### Module version — `Modules/ModuleCatalog.cs`

`MessageTrace` `Version` "1.1.1" -> "1.2.0" (module-scoped behavior change;
Constitution Deployment And Versioning). No base app version bump. (Confirm
whether this reuses the existing bulk-job facility only — if any shared/app-wide
file changes, the app version rule fires too; expected: module-only.)

## Non-goals

- No detail added to the existing bulk `ExportCsv` summary (unchanged).
- No operator-typed email destination; no server file-share link; no new shared
  path or wwwroot storage.
- No new module, route, permission, or config field. Uses the existing
  `MessageTrace` access permission, on-prem `DelineaSecretId`, admin-email config,
  audit log root, and the bulk-job runner.
- No change to the summary list query or its existing fields.

## Failure behavior

- Live/download detail failure sets `MessageTraceDetail.Error`, shown inline;
  never blanks the list (Known Failure Class #2, no silent success).
- Email/bulk-job: one message's detail failing is a Failed row, aggregated, never
  aborts the batch; the emailed report notes per-message failures. A job
  interrupted by a recycle is reported Interrupted (runner behavior), never
  silently resumed.
- Email send failure is logged and does not change the job's recorded result.

## Test plan

xUnit in `ExchangeAdminWeb.Tests/` (new/rewritten service logic requires tests
before "done"). Backends covered at the mapping/routing seam, matching existing
`MessageTraceService` tests:

- Backend routing: `OnPrem` routes to on-prem detail; cloud to
  `Get-MessageTraceDetailV2`; unknown backend -> clear error, not a throw.
- On-prem detail preserves ALL event rows for one message (no collapse), ordered
  by timestamp, and includes the reason fields — contrast the collapsing summary
  path.
- Cloud "cmdlet not recognized" -> outdated-module error string, not a crash;
  aged-out message -> empty events + message, not an exception.
- Error paths set `MessageTraceDetail.Error`, leave `Events` empty.
- Report builder (pure): header block + trail per message, `CsvEscape` applied,
  multi-message ordering.
- Threshold logic (pure helper): selection count -> allowed action
  (1-10 download-or-email; 11-50 email-only; select-all caps at 50). Non-vacuous.
- Bulk-job processor: `CountRows` = selection; per-row fail-soft -> Failed row not
  a throw; `OnJobCompletedAsync` builds report + calls the email method (verified
  via NSubstitute on a virtual email seam) + writes to a temp log path.

Each shipped test proven non-vacuous (revert fix -> test fails -> restore ->
pass). Live behavior against a real EXO tenant / on-prem transport server, actual
email/zip delivery, and the Blazor selection UI are manual-validation-on-dev (no
dev tenant — standing gap).

## Verification

`dotnet build ExchangeAdminWeb.slnx -c Release`; `dotnet test
ExchangeAdminWeb.slnx`; `tools/Test-AsciiOnly.ps1`; `dotnet format
ExchangeAdminWeb.slnx --verify-no-changes --no-restore`; `git diff --check HEAD`.

## Task breakdown

1. Model: `MessageTraceDetail` + `MessageTraceDetailEvent` in `LookupModels.cs`.
2. Service: `GetMessageDetailAsync` (both backends, fail-soft, on-prem no-collapse
   + reason fields); xUnit for routing / no-collapse / error paths, non-vacuous.
3. Report builder: pure `MessageTraceDetailReport` + threshold helper; xUnit.
4. Email: `SendMessageTraceResultAsync` + MimeKit attachment helper; xUnit on the
   virtual seam.
5. Bulk-job: payload + `MessageTraceDetailJobProcessor` + registry registration
   (zip + save-to-log-path + email in `OnJobCompletedAsync`); xUnit on the
   processor against a substitute email/detail seam.
6. UI: per-row Details drill-in + inline trail + audit (live path).
7. UI: checkboxes + select-all (cap 50) + threshold-driven action controls
   (download vs email-only) + email-job submit; download path uses the report
   builder + `downloadFile` interop.
8. Module version 1.1.1 -> 1.2.0; count-guards if any.
9. Verification + manual-validation note (live EXO/on-prem detail, email/zip
   delivery, and UI deferred — no dev tenant).

Each slice codex-reviewed as it lands (owner standing convention: codex headless
CLI, default model/effort).

## Implementation status (2026-07-27)

All nine slices landed on `master`, each committed and codex-reviewed accepted
(records under `.agents/review/findings/mt-detail-slice*.md`, index rows in
`.agents/review/index.md`):

1. Models (`MessageTraceDetail` + `MessageTraceDetailEvent`) — accepted (ade48c1).
2. Service `GetMessageDetailAsync` (both backends, fail-soft, on-prem no-collapse)
   + tests — reopened then accepted (b00c5b7).
3. Pure `MessageTraceDetailReport` (CSV builder + threshold helper) + tests —
   accepted (2df0f48).
4. `EmailService.SendMessageTraceResultAsync` + zip attachment + recipient
   resolver + tests — accepted (2575467).
5. Bulk-job: payload + `MessageTraceDetailJobProcessor` + registration + tests —
   accepted (slice `7a307ac`, review `a5f3fa7`).
6. UI: per-row Details drill-in + inline trail + live-path audit — reopened
   (stale-response race) then fixed (`6960887`) and accepted.
7. UI: checkboxes + select-all (cap 50) + threshold-driven download/email +
   bulk-job submit — accepted (`99cf4a1`).
8. Module version 1.1.1 -> 1.2.0 — accepted (`e7ce73c`).
9. This verification + manual-validation note.

Automated verification (slice 9): `dotnet build ExchangeAdminWeb.slnx -c Release`
0 errors; `dotnet test ExchangeAdminWeb.slnx` full suite green; `dotnet format
--verify-no-changes` and `git diff --check HEAD` clean; `MessageTrace.razor`
verified pure ASCII.

### Manual validation still required on dev (no dev tenant — standing gap)

Automation covers the pure builder, the threshold helper, the email
method/resolver (via the virtual seam), and the bulk-job processor (against a fake
detail seam). The following need a live dev run and were NOT executed here:

- Live per-row **Details** drill-in against a real EXO tenant and a real on-prem
  transport server: confirm the on-prem trail shows every event (no collapse) with
  reason fields, and the cloud path issues exactly one `Get-MessageTraceDetailV2`
  per click.
- **Download details** (1-10 selected): the file downloads and its content matches
  the emailed report for the same selection.
- **Email details** (1-50 selected): the bulk job runs off-circuit, the zipped CSV
  is delivered only to the authenticated mailbox + configured admins (never a
  typed address), and the pre-zip CSV is saved under
  `<AuditLogRoot>\ExchangeAdminWeb\MessageTraceExports\`.
- Blazor selection UI behavior: checkbox/select-all cap at 50, threshold-driven
  enable/disable, and selection reset on a new trace.
