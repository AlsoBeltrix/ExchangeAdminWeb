# mt-detail-slice5: message-trace detail-export bulk job processor (slice 5)

**Severity**: n/a — slice-landing review of a new bulk-job processor + payload + detail seam + its tests
**Status**: Verified — accepted (codex CLI, gpt-5.5-dzs/xhigh/std)
**Branch**: `master` (committed directly per repo policy; no per-finding branch)
**Commit**: `7a307ac` (slice 5), base `e1751af`

## Evidence
`Services/Jobs/MessageTraceDetailJobProcessor.cs` — new `IBulkJobProcessor`
(CountRows / ProcessRowAsync / OnJobCompletedAsync + SaveToLogPath / ZipSingleFile
/ Audit helpers). `Services/Jobs/MessageTraceDetailJobPayload.cs` — new payload.
`Services/Jobs/IMessageTraceDetailSource.cs` — new narrow detail seam.
`Services/MessageTraceService.cs` — now implements `Jobs.IMessageTraceDetailSource`.
`Program.cs` — registry entry + scoped registrations. Tests:
`ExchangeAdminWeb.Tests/MessageTraceDetailJobProcessorTests.cs` (6 tests).

## Predicted observable failure
Without per-row detail retention the completion step would re-fetch (doubling
cloud cost) or silently drop messages; without fail-soft per-row mapping a single
fetch failure would abort the batch (Known Failure Class #2, success-aggregation);
without the exfiltration gate the emailed zip could reach an operator-typed
address. The core guards: `ProcessRow_FetchError_IsFailedRow_NotThrow` (a fetch
error becomes a Failed row, never a throw), `OnJobCompleted_RetainsFailedDetail_NotDropped`
and `OnJobCompleted_UnprocessedMessage_StillInReport` (no requested message is
dropped from the export), and `OnJobCompleted_BuildsReport_SavesToLogPath_AndEmails`
(saved under the audit log root; emailed only to the resolved user + admins).
Guards: `dotnet build ExchangeAdminWeb.slnx -c Release` (0 errors),
`dotnet test ExchangeAdminWeb.slnx --filter FullyQualifiedName~MessageTraceDetailJobProcessorTests` (6/6).

## What
Fifth slice of the Message Analysis detail work stream (plan task 5). Adds the
off-circuit bulk path that produces the per-message delivery-detail export above
the live threshold: one bulk-job row per selected message, each fetching one
message's full detail via the narrow `IMessageTraceDetailSource` seam, then a
single completion step that assembles the CSV, saves it under the audit log root,
zips it in-memory, and emails the zip to the authenticated user + configured
admins only.

## Approach
`MessageTraceDetailJobProcessor` implements `IBulkJobProcessor`. `CountRows` =
selection count. `ProcessRowAsync` fetches one message's detail (fail-soft; the
seam sets `Error` rather than throwing), retains it in a per-row instance field
(`ConcurrentDictionary<int, MessageTraceDetail>`) BEFORE mapping a fetch error to
a Failed row — so a failed fetch is aggregated as a per-item failure yet its
Error-carrying detail still reaches the export. `OnJobCompletedAsync` reassembles
the details in the operator's selected order (filling any unprocessed index with
an explanatory placeholder), builds the CSV via the pure
`MessageTraceDetailReport.BuildCsv`, saves it under
`<AuditLogRoot>\ExchangeAdminWeb\MessageTraceExports\` (fail-loud via
`AuditLogRoot.Require`), zips it in-memory (decision B: no wwwroot/file-share), and
calls the virtual `EmailService.SendMessageTraceResultAsync` with
`payload.UserEmail` (never an operator-typed address). The runner resolves the
processor once per job in a fresh DI scope, so the instance field is valid for the
job's lifetime and re-fetching (double cloud cost) is avoided. The seam mirrors
the established `IConferenceRoomBulkOperations` pattern for unit testing without
live EXO.

## Files changed
- `Services/Jobs/IMessageTraceDetailSource.cs` — narrow per-message detail seam (new).
- `Services/Jobs/MessageTraceDetailJobPayload.cs` — job payload (new).
- `Services/Jobs/MessageTraceDetailJobProcessor.cs` — the processor (new).
- `Services/MessageTraceService.cs` — implements the detail seam (class decl only).
- `Program.cs` — registry entry + two scoped registrations.
- `ExchangeAdminWeb.Tests/MessageTraceDetailJobProcessorTests.cs` — 6 tests (new).

## Guard proof
Forcing `ProcessRowAsync` to always return Success (removing the
`detail.Error` -> Failed mapping) makes exactly
`ProcessRow_FetchError_IsFailedRow_NotThrow` FAIL (1 failed / 5 passed); restore
-> 6 pass. Build 0 errors, added lines ASCII-clean (`tools/Test-AsciiOnly.ps1`
EXIT=0), `dotnet format` no changes, `git diff --check HEAD` clean.

## Coder dispute (if any)
None.

## Known gaps
The UI that submits this job (checkboxes + select-all + threshold controls) and
the live per-row screen drill-in are later slices (6-7); this slice is the
processor + payload + seam only. The runner wiring (`BulkJobService`
resolve-per-job, sequential rows, fail-safe completion) is pre-existing and not
re-tested here — only the processor's own contract is covered against a fake seam
+ NSubstitute email/audit. Live EXO fetch + real SMTP/zip delivery is a
manual-validation-on-dev standing gap.

## Reviewer comments

Accepted (round 1), no material issue, no comments. Reviewer: codex CLI
(codex exec, gpt-5.5-dzs/xhigh/std). Verdict envelope:
`{"verdict":"accepted","guard_confirmed":true,"capability_ok":true,"reviewed_sha":"7a307ac767d004c3f2bce6b4f267f0109a271010","base_sha":"e1751afbc27328d43cd896e124138a696d134108","comments":[]}`.
SHAs match dispatch; acceptance criteria satisfied fail-closed
(`guard_confirmed` and `capability_ok` both literally true). The reviewer
built its own isolated tree at the head SHA (git clone/worktree to TEMP were
blocked by Git-for-Windows shell-helper errors and apply_patch's project-root
restriction; it fell back to `git archive` extraction + PowerShell string
replacement for the guard mutation, then reran the confirming pass at the
csproj level after the solution-level temp build hung — reviewer-side
environment adaptation, not a code issue). Guard proof confirmed: mutating
`ProcessRowAsync` to always return Success made
`ProcessRow_FetchError_IsFailedRow_NotThrow` FAIL (1 failed / 5 passed);
restore -> 6/6. Capability build EXIT=0. ASCII hygiene clean.
