# psl-4 - exceptional pooled queries lose connection classification

Status: Fixed on branch; awaiting merge. Commit `<pending>`.
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING") admits this record; implementer is a
worktree subagent, one commit `fix(exo-pool): ... (psl-4)`. Base-app shared code; no module.

Scope: `Services/ExchangeServiceBase.cs` (RunPooledQueryAsync only), `Services/ExoConnectionPool.cs`
(PooledOutcome carried fault, the core's existing ConnectionFailure branch). Tests: new
`ExchangeAdminWeb.Tests/ExchangeServiceBasePooledQueryTests.cs`, cases in `ExoConnectionPoolRetryTests.cs`.

Mechanism:
- RunPooledQueryAsync catches a query exception ONLY when its tracker already holds connection
  evidence, and returns a connection-fault outcome carrying the original exception
  (ExceptionDispatchInfo) with precheck = tracker precheck OR the thrown message's own precheck
  signature (what the core would have read from the throw).
- The core's existing ConnectionFailure branch: discard; the unchanged eligible-precheck retry
  (`allowRetry && attempt < 2 && precheck`); otherwise rethrow the carried exception, same object.
- No tracker evidence: the exception propagates exactly as today to the message-classifying catch.

Preserved: exception identity; RunAsync (never throws) untouched; thrown connection errors still
classified by message; non-connection throws still Return; one retry maximum; slot conservation.
No new retry trigger beyond the evidence the flagged-result path already retries on.

Proof: reproduction first through the real pool (connect seam, opened local runspaces whose
functions stand in for EXO cmdlets): HasCloudMailboxAsync and GetRoomInfoAsync eligible precheck
must discard and retry. Controls: midflight drop (discard, no retry, same exception), non-eligible
(discard, no retry), unclassified error (return, no retry), thrown precheck after tracker midflight
(retry, the current rule). Mutations at the call sites: old wrapper; no carried rethrow; carried
precheck dropped; thrown-message precheck dropped; catch filter always true.

## Executed reproduction - 2026-10-21

Branch `worktree-agent-a7f96055b25f98b57` from master `1633ad02546462b8102518c37db0b9d117d17277`.
Before any production edit, `ExchangeAdminWeb.Tests/ExchangeServiceBasePooledQueryTests.cs` ran the
real pool (temp SQLite config, connect seam handing out opened local runspaces whose functions
stand in for Get-Mailbox, Get-Place, Get-MailboxRegionalConfiguration and Disconnect-ExchangeOnline)
through the actual callers HasCloudMailboxAsync and ConferenceRoomService.GetRoomInfoAsync. The
injected shape is the record's: `Write-Error '<classifier text>' -ErrorAction Continue; throw
'optional fatal'`. Result 5 failed / 2 passed, 0 compiler errors (`out/psl-4-reproduction.log`):

- Eligible precheck, both callers: `Assert.Null` failed, `RuntimeException: optional fatal`
  surfaced on the first attempt; no discard, no retry.
- Precheck on both attempts: expected two SDK failures, got one (the retry never ran).
- Mid-flight drop, and precheck with allowRetry false: expected `["conn-1"]` destroyed, got `[]`;
  the suspect connection went back to the pool.
- Controls passed: an unclassified stream error returns the connection without retry; a precheck
  thrown by the query itself after a mid-flight tracker flag retries (the current thrown path).

## Fix

`RunPooledQueryAsync` catches a query exception only when its tracker already holds connection
evidence and returns `PooledOutcome<T>.ConnectionFault(ExceptionDispatchInfo.Capture(ex), precheck)`,
precheck being the tracker's flag OR the thrown message's own signature. The pool's existing
ConnectionFailure branch discards, keeps the unchanged retry test (`allowRetry && attempt < 2 &&
RetriablePrecheck`), and otherwise rethrows the carried exception, the same object. Without tracker
evidence the exception propagates exactly as before. RunAsync, PermissionValidator, the
message classifiers and the non-connection Return/Discard policy are unchanged. All allowRetry
pooled-query callers are read-only Get-* queries; the only allowRetry-false callers (Mailbox
Permissions grant/revoke) catch every throw, so for them nothing changes.

New tests: seven caller-through-pool cases (above) and four core cases in
`ExoConnectionPoolRetryTests` (carried precheck retries on a fresh borrow; carried non-precheck and
non-eligible faults discard and rethrow the original; a second carried precheck rethrows the second
exception with no third attempt).

## Guard proof

Compiled mutation round through a session-local driver (not committed): exact bytes saved, one
literal replacement, `dotnet test ExchangeAdminWeb.slnx -c Release --filter
FullyQualifiedName~ExchangeServiceBasePooledQueryTests|FullyQualifiedName~ExoConnectionPoolRetryTests`
(compiles; TRX-parsed outcomes), exact bytes restored in `finally`, MD5 re-checked, mtime advanced,
same filter rerun green. Every mutant: 0 `error CS`, both project outputs built, 22 tests.
Fixed (pre-comment) MD5s: ExchangeServiceBase.cs `41EB1F58847C1BD4395BA5CEB5082B79`,
ExoConnectionPool.cs `AD88E3F249DF2A07F38D3539008F5C4B`; every restore matched its fixed hash and
every restored rerun passed 22/22.

| Mutation (call site) | Mutant MD5 | Named tests failing | Assertion |
|---|---|---|---|
| old wrapper: no catch in RunPooledQueryAsync | `D02574C84FDDA1BC76E18C98528D103E` | HasCloudMailbox precheck retry, GetRoomInfo precheck retry, precheck both attempts, mid-flight discard, not-eligible discard (5) | Assert.Null, Assert.Equal |
| no carried rethrow in the pool's ConnectionFailure branch | `B555252B5076E755AD2A5D76AFDD6476` | precheck both attempts, mid-flight, not-eligible; core carried mid-flight, non-eligible, twice (6) | Assert.NotNull, Assert.Throws |
| carried precheck dropped (thrown signature only) | `5249F22CD36EFC4ECEC936E9A53CA31C` | HasCloudMailbox precheck retry, GetRoomInfo precheck retry, precheck both attempts (3) | Assert.Null, Assert.Equal |
| thrown-message precheck dropped (tracker only) | `2A5CCCFC384D4353B89EAD0358A0676A` | PooledQuery_ThrownPrecheckAfterTrackerMidFlight_StillRetriesOnce (1) | Assert.Null |
| catch filter removed (every throw carried) | `92A81B46074916BA8913DC6EC6956C6F` | HasCloudMailbox_UnclassifiedStreamErrorThenThrow_ReturnsConnectionWithoutRetry (1) | Assert.Empty |

Logs and receipts: `.agents/review/out/psl-4-mutation-<name>.log`, `psl-4-restored-<name>.log`,
`psl-4-receipt-<name>.log` (machine-local, ignored). Comments were added after the round;
final MD5s ExchangeServiceBase.cs `E863CC24E6C4AED59E06B9B832C4B3EA`, ExoConnectionPool.cs
`7CABECD538A09AD9130B70FAE1902710`; the focused rerun after them passed 58/58 (pool, invoke,
shared-config, Conference Rooms lifecycle and the new cases).

## Gates - 2026-10-21

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 0 errors, 40 existing warnings.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: the first run hit the
  known-flaky `Comms10kRefusalTests.C6ValidationWaitsKeepTheCapturedTicketVisibleAndRejectDelayedInput`
  theory (blame-hang inactivity abort after 4,315 passed / 0 failed). Rerun with
  `--filter "FullyQualifiedName!~C6ValidationWaitsKeepTheCapturedTicketVisibleAndRejectDelayedInput"`:
  exit 0, 4,498 passed / 3 skipped / 0 failed, 4,501 total, 5m04s. Other agents' suites shared
  the host at the time.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0 (workspace
  loading warnings only). `git diff --check HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the new
  test staged: exit 0.

No PowerShell source changed (no analyzer or Pester run). No live tenant, directory, push or
deployment. No coder self-review verdict is claimed. Versions: shared base-app code only; the
supervisor owns the bump. No module's own behaviour changed directly.
