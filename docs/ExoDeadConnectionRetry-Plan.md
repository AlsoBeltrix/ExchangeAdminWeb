# EXO Dead-Connection Auto-Retry — Plan

Status: In progress
Owner: Michael Coelho
Created: 2026-06-25
Tracking: CR-BUG-1 (`.agents/state.md` Queued work)

## Problem

The EXO connection pool (`Services/ExoConnectionPool.cs`) reuses open Exchange
Online sessions. On borrow (`BorrowAsync`, `ExoConnectionPool.cs:112-121`) it only
checks config generation + a 20-minute idle timeout — it never confirms the session is
still alive. Exchange Online can tear a session down server-side inside that window
(token expiry it can't recover, session limit, transient drop). The pool then hands out
a dead session; the first cmdlet throws:

> Exception calling "GetCurrentConnectionContext" ... "You must call
> Connect-ExchangeOnline before calling any other cmdlet."

The session is gone, so the V3 module's own token auto-reconnect cannot recover it
(token refresh needs a live session; there isn't one).

Observed: ConferenceRooms Apply CSV, 15/26 rooms succeeded; the failed room died on its
first EXO step (`Set-Place`) with the error above and **no steps committed**. The pool
already self-heals — the failed connection is discarded on the connection error, so the
*next* borrow gets a fresh one — but the one in-flight item still fails before recovery.

## Decision (owner, 2026-06-25)

1. **Fix by auto-retry on a fresh connection**, not a pre-borrow liveness probe. A probe
   would add a round-trip to Exchange on every healthy operation; retry costs nothing on
   the healthy path and only fires on the actual failure.
2. **Exactly one retry.** A stale pooled session is resolved by one fresh borrow. A
   persistent connect failure (bad cert, EXO down) must surface fast, not loop.
3. **Consolidate the retry in the pool** so all callers share one implementation and the
   duplicated borrow/return/discard pattern collapses to a single seam.
4. **Retry is restricted to read-only and single-write operations** (see Safety below).
   Multi-write operations keep today's behavior (discard, fail, user re-runs manually).

## Safety model — why retry must be gated (corrected after audit)

An earlier draft argued "each step is a single borrow, so retry is always safe." That
reasoning was **wrong** and was caught in review. The shared helpers
(`RunAsync` at `ExchangeServiceBase.cs:41`, `RunPooledQueryAsync` at
`ExchangeServiceBase.cs:98`) borrow **once for the whole delegate**, and a delegate may
run several cmdlets. If an early write commits and a later command then fails, blindly
re-running the whole delegate **repeats the committed write** — e.g. a second
`Add-MailboxPermission` throws "already exists," or a second `New-MigrationBatch`
duplicates a batch.

The pool cannot see inside a delegate, so it cannot detect "did an earlier command
already commit." Therefore retry eligibility is **declared by the call site**, which
knows its own write count. The mechanism:

- The retry helper defaults to **no retry (safe)**.
- Read-only and single-write call sites **opt in** to retry.
- Multi-write call sites do **not** opt in. A future new multi-write delegate that
  forgets to opt in gets the safe default, not a double-write. (This satisfies the
  `AGENTS.md` "side-effect ordering" failure class: the unsafe path is the default.)

Why this is sufficient for the observed bug: the failing Conference Rooms step,
`Set-Place` (`ConferenceRoomService.cs:372-389`), is a single write — it opts in and is
healed. Read paths (autocomplete, lookups, reports) are all eligible too.

Note: with eligibility gated this way, the breadth of the existing `IsConnectionError`
classifier (`ExchangeServiceBase.cs:703-707`, matches "connection"/"session"/"runspace")
is no longer a hazard — re-running a read or a single write is harmless regardless of
which connection error tripped it. We will still add the exact
"GetCurrentConnectionContext / must call Connect-ExchangeOnline" signature for clarity.

## Audit — eligibility of every pool delegate

Classified independently by two reviewers (internal Explore agent + Codex), agreeing on
the seven multi-write sites. Full detail retained in this commit's review notes.

ELIGIBLE for auto-retry (read-only or single-write):
- All read-only delegates: `GetRoomInfoAsync`, `ResolveRoomListAsync`,
  `DelegationReportService` lookups, `ExchangeIdentityResolver.ResolveToObjectIdAsync`,
  `HasCloudMailboxAsync`, `MessageTraceService.GetCloudMessageTraceAsync`, the
  `MigrationService` Get-* queries, and `PermissionValidator.TryExpandGroupAsync`
  (`Get-Recipient` + `Get-DistributionGroupMember`, both reads).
- Single-write delegates: `RemoveCalendarPermissionAsync` (`Remove-MailboxFolderPermission`),
  `ConferenceRoomService` Step 1 `Set-Place` (`ConferenceRoomService.cs:372`),
  `MessageTraceService.StartHistoricalSearchAsync`, and the single-cmdlet
  `MigrationService` actions (Complete/Stop/Start/Resume/Remove batch & user).

NOT eligible — keep today's behavior (the 7 multi-write delegates):
- `MailboxPermissionService.AddMailboxPermissionsAsync` (`Add-MailboxPermission` +
  `Add-RecipientPermission`) — re-add throws "already exists".
- `MailboxPermissionService.RemoveMailboxPermissionsAsync` (two Remove-*) — likely
  idempotent but not proven; excluded under the conservative rule.
- `CalendarPermissionService.SetCalendarPermissionAsync` (`Set-` then fallback `Add-`).
- `ConferenceRoomService` Step 3 timezone (`Set-MailboxRegionalConfiguration` +
  `Set-MailboxCalendarConfiguration`) — likely idempotent but excluded.
- `ConferenceRoomService.AddToRoomListAsync` (`New-DistributionGroup` +
  `Add-DistributionGroupMember`).
- `ConferenceRoomService.SetRoomTypeAsync` (6+ writes).
- `MigrationService.CreateMigrationBatchAsync` (`New-` + `Start-`/`Set-`).
- `MigrationService.ApproveMigrationUserAsync` (7 writes incl. non-idempotent
  `Resume-MoveRequest`).

The two "likely idempotent but excluded" cases can opt in later with their own proof if
they ever prove annoying; not now (owner decision, option 1).

## Where the bug lives — all 10 pool callers

The pool is shared by every Exchange feature, so this is a **pool-level** bug.
Conference Rooms merely exposed it (a 26-row batch is a long enough window for EXO to
drop a session). Callers:

- **9 services inherit `ExchangeServiceBase`** and run cmdlets through `RunAsync` /
  `RunPooledQueryAsync`: MailboxPermission, CalendarPermission, ConferenceRoom,
  MessageTrace, Migration, DelegationReport, OutOfOffice, RecipientLookup,
  ExchangeIdentityResolver.
- **`PermissionValidator` is the 1 outlier**: it does NOT inherit the base. It
  hand-writes its own `BorrowAsync` / `Return` / `Discard` dance in
  `TryExpandGroupAsync` (`PermissionValidator.cs:336-415`) because (a) it only needs two
  read cmdlets and doesn't want the base's on-prem/Delinea/module-ID baggage, and (b) it
  has a custom "couldn't be found = success, keep as literal match" rule
  (`PermissionValidator.cs:358-363`) the shared `Invoke` would throw on. Its delegate is
  read-only, so it is retry-eligible.

## Scope

In scope:
- `Services/ExoConnectionPool.cs` — add one shared "borrow-run-(optionally retry once)"
  helper. Borrow/return/discard/release and the single retry live here, once. The helper
  takes an explicit `allowRetry` (eligibility) argument; default = false (no retry).
- `Services/ExchangeServiceBase.cs` — `RunAsync` and `RunPooledQueryAsync` call the new
  helper instead of hand-writing borrow/try/finally, threading their tracker-based
  connection-error detection through it. Add an eligibility parameter so each call site
  declares read-only/single-write (eligible) vs multi-write (not).
- The ~23 eligible call sites opt in; the 7 multi-write sites do not.
- `Services/PermissionValidator.cs` — `TryExpandGroupAsync` routes its borrow through the
  new helper (eligible), keeping its "couldn't be found = success" handling in the
  delegate.
- Detection: extend `IsConnectionError` to also match the
  "GetCurrentConnectionContext" / "must call Connect-ExchangeOnline" signature.

Out of scope (not requested):
- Pre-borrow liveness probe.
- Retrying any multi-write delegate (the 7 above).
- Changing the 20-minute idle timeout or the cleanup timer.
- On-prem PSSession paths (separate runspaces, not the pool).
- Any change to what a caller treats as a *business* error.

## Approach

New pool helper runs a caller delegate on a borrowed runspace:
- On success → `Return`.
- On a connection-classified failure **and** `allowRetry == true` → `Discard`, borrow a
  fresh runspace, run the delegate **once more**; a second connection failure surfaces as
  today. No third attempt.
- On a connection-classified failure with `allowRetry == false` → `Discard` and surface
  (today's behavior).
- On a non-connection failure → `Return`/`Discard` per current semantics and surface; no
  retry.

Semaphore ownership moves entirely into the helper: each of `Return`/`Discard` already
releases one slot (`ExoConnectionPool.cs:144-166`), so the helper must release exactly
once per borrow across both attempts — no double-release. PermissionValidator's manual
`Return`/`Discard` calls are removed when it adopts the helper.

## Verification

- New unit tests in `ExchangeAdminWeb.Tests/` (alongside
  `ExchangeServiceBaseInvokeTests.cs`; add pool-level coverage):
  1. Eligible delegate: first borrow throws the dead-session error → retried on a fresh
     borrow → success. Assert the dead runspace was discarded, a second borrow happened,
     and the semaphore slot count is conserved (no leak/double-release).
  2. Eligible delegate: both attempts throw a connection error → failure surfaced, no
     third attempt.
  3. Non-eligible (multi-write) delegate: connection error → discarded, failure surfaced,
     **no retry** (assert exactly one borrow).
  4. Non-connection error → no retry (single attempt), failure surfaced as today.
  5. `PermissionValidator`: dead session on first borrow → retried → group expands; and
     "couldn't be found" → no retry, kept as literal match.
- Prove non-vacuous: revert the retry, confirm test (1) fails; restore. Also confirm
  test (3) fails if the multi-write site is wrongly opted in.
- `dotnet build ExchangeAdminWeb.slnx -c Release` then `dotnet test ExchangeAdminWeb.slnx`.
  Format check + `git diff --check HEAD`.

## Versioning

Shared infrastructure (not one module) changes, so bump the **app** version
(`<VersionPrefix>` + Assembly/File version in `ExchangeAdminWeb.csproj`). No single
module's catalog `Version` changes. (Confirm against `docs/ProjectConstitution.md`
§Deployment And Versioning at implement time.)

## Open questions

- None blocking. Eligibility default = no-retry (safe), opt-in per call site; retry
  count = 1; multi-write sites excluded (owner option 1). Flag if any should differ.
