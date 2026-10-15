# psl-4 - exceptional pooled queries lose connection classification

Status: Open follow-up; outside the approved replay repair and original remediation.
Severity: MEDIUM. Native `/root/review_psl1`, `gpt-6-astra` at max, 2026-10-15.
Evidence head: `22afbf912cb06c775f2679203b7463ff9ce4fcac`; the affected wrapper and pool
paths are unchanged from base `76ba54599fdfbb3810b488fdc88478f4f79c268a`.

## Evidence and consequence

ExchangeServiceBase.RunPooledQueryAsync (lines 114-116 at the evidence head) builds its
flagged outcome only after query returns. A query exception skips that construction and
loses the invocation tracker's connection/precheck classification. ExoConnectionPool's
RunWithRetryCoreAsync (317-332) then classifies only the thrown exception's message.

Trigger: an uncaught optional query first emits a session/precheck error into its error
stream, then throws an unrelated message. The existing helper tests exercise the shape
Write-Error 'GetCurrentConnectionContext requires authentication'; throw 'optional fatal'.
InvokeOptional sets the tracker, but the pool takes the nonconnection exception path,
returns the suspect connection and skips an otherwise eligible precheck retry.
Actual uncaught consumers include HasCloudMailboxAsync (ExchangeServiceBase.cs:494) and
GetRoomInfoAsync's Get-Place call (ConferenceRoomService.cs:87).

This is a source-confirmed pre-existing defect, not a psl-1 regression or command replay.
The helper failure shape was exercised locally; a full caller-through-pool reproduction
was not run. No live connection or remote failure was exercised.

## Proposed follow-up

Scope independently after current authorized work. Carry existing tracker classification
across exceptional query exits while preserving exception identity and current retry rules.
First add a caller-through-pool guard for stream-only classification, asserting discard and
eligible precheck retry, with midflight and noneligible controls. Mutation-prove that guard
before claiming repair. No implementation or new retry policy is approved by this record.
