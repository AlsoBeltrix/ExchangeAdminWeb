# Conference Rooms — Synced-Room Set-Mailbox Failure + Error-Bleed Fix Plan

**Status:** Implemented (rev 2). Build 0 errors; 365/365 tests pass; guard-revert verified
(3 base-Invoke + 1 on-prem-coupling test fail with fixes reverted, pass when restored).
Manual EXO check on a live synced room NOT run (no tenant access) — see Verification.
**Date:** 2026-06-11
**Author:** Michael Coelho (via agent investigation)
**Module:** ConferenceRooms (`Modules/ModuleCatalog.cs` — currently `Version = "2.0.2"`)
**Shared infra:** `Services/ExchangeServiceBase.cs` (`Invoke`) — used by all modules.
**Reporter:** Shaun Hogan (screenshot `D:\BugReports\calendar issue\RoomTypeError.jpeg`)
**Owner decisions (2026-06-11):** Fix scope = **both** (Conference Rooms + shared base);
operator feedback = **informational step** for the synced-room cloud-skip case.

## Context

Setting a Room Type on `Wilm6-F1-AV2Test.Conf@analog.com` failed. The error:

> The operation on mailbox "…" failed because it's out of the current user's write
> scope. The action 'Set-Mailbox', 'CustomAttribute9', can't be performed on the object
> "…" because the object is being synchronized from your on-premises organization. This
> action should be performed on the object in your on-premises organization.

The room is **DirSync'd** (mastered on-premises, synced to EXO). `CustomAttribute9` on a
synced object **cannot be written in the cloud**; it must be written on-prem via
`Set-RemoteMailbox`. The screenshot shows the *same* `Set-Mailbox` error repeated on every
subsequent calendar-permission step — those permission ops are cloud-side and should
succeed on a synced room, so something is amplifying one expected failure into total
failure.

## Root cause (verified in code)

### Defect A — cloud `CustomAttribute9` write treated as hard failure

`ConferenceRoomService.SetRoomTypeAsync` Step 5 (`ConferenceRoomService.cs:486-502`) runs:

```
Set-Mailbox -Identity <room> -CustomAttribute9 <tag> [-MailTip <tip>] -ErrorAction Stop
```

The working reference `D:\source\ConfRoomScript\SetupRoomType.ps1` (e.g. lines 198-199,
241-242, 283-284) runs the cloud write as **best-effort** —
`Set-CMailbox … -ErrorAction SilentlyContinue` — and performs the authoritative write
on-prem with `Set-RemoteMailbox … -CustomAttribute9 …`. The web app already has the
on-prem write (Step 9, `SetRemoteMailboxAsync`, `ConferenceRoomService.cs:542-543` /
`:918-1006`), but Step 5 uses `-ErrorAction Stop`, so the expected, ignorable cloud
rejection for a synced room becomes a terminating error.

### Defect B — error/command bleed in shared `ExchangeServiceBase.Invoke`

`Invoke(ps, tracker)` (`ExchangeServiceBase.cs:143-168`) throws on both the
`RuntimeException` path (`:150-155`) and the `HadErrors` path (`:157-164`) **without
clearing `ps.Commands` or `ps.Streams.Error`** — the `ps.Commands.Clear()` at `:166` is
only reached on the success path.

`Set-Mailbox -ErrorAction Stop` raises a *terminating* error, so `ps.Invoke()` throws and
`Invoke` rethrows with the command **still queued**. Step 5's `catch` (`:499-502`) records
the failure but does not clean the pipeline either. Step 6
(`SetCalendarPermissionsForType` → `SetCalendarPermission`, `:879-916`) then does
`ps.AddCommand("Add-MailboxFolderPermission")…` — **appending** to a pipeline that still
contains the failed `Set-Mailbox`. Each subsequent invoke re-runs `Set-Mailbox`, which
throws again before the new command's intent matters. Net effect: every permission row
reports the identical `Set-Mailbox` error and **no later step actually executes**.

This matches the repo's documented failure classes (AGENTS.md → Known Failure Classes #1
side-effect/exception-flow ordering, and the "stale references" discipline). It is a
shared-base defect that can bite any module running multi-step pipelines on one pooled
runspace.

## Fix

### 1. Conference Rooms — Step 5 best-effort cloud write (`Services/ConferenceRoomService.cs`)

**Capture-then-clear (review finding #2).** Do NOT use the existing `InvokeOptional` for
Step 5: it clears `ps.Streams.Error` before returning (`ExchangeServiceBase.cs:180`), so the
caller could not inspect the cloud-write error to classify it. Instead, capture the error
text *before* cleanup. Two acceptable implementations:
  - a new base helper `InvokeBestEffort(ps, tracker, out IReadOnlyList<string> errors)` that
    runs `ps.Invoke()`, snapshots `ps.Streams.Error` messages, sets the connection-error
    flag, then clears commands + error stream and returns the snapshot; or
  - a local `try/finally` in Step 5 that snapshots `ps.Streams.Error` then clears
    `ps.Commands` + `ps.Streams.Error`.
  Prefer the base helper (reusable, keeps cleanup centralized).

- Run the cloud `Set-Mailbox -CustomAttribute9/-MailTip` with `-ErrorAction SilentlyContinue`
  via that best-effort path. The authoritative write remains Step 9 on-prem
  `Set-RemoteMailbox`.
- **Classify the captured error (review finding #2 — do not blanket-swallow):**
  - No error → record the existing success step.
  - Error matches `IsOnPremMasteredWriteError` (synced-room signal: `out of the current
    user's write scope` / `being synchronized from your on-premises organization`) → record
    a **non-error informational** step `"CustomAttribute9/MailTip set on-prem (synced room —
    cloud write skipped)"` AND set a local flag `cloudAttrDeferredToOnPrem = true` (drives
    finding #3 below).
  - Any **other** error → record it as a **failed/warning** step with the real message. It is
    NOT silently swallowed. (The reference script's blanket `SilentlyContinue` is improved on
    here deliberately: unexpected cloud failures stay visible.)
- Net for a synced room: ✓ Set-CalendarProcessing, ℹ CustomAttribute9/MailTip on-prem,
  ✓ each permission, ✓ Set-RemoteMailbox (on-prem) — no spurious red cascade.

### 1a. Synced-room success now DEPENDS on the on-prem write (review finding #3 — critical)

If Step 5 deferred the attribute to on-prem (`cloudAttrDeferredToOnPrem == true`), then the
on-prem write (Step 9) is the ONLY path that sets `CustomAttribute9`/`MailTip`. Today
`SetRemoteMailboxAsync` records `Success = true` with "Skipped — on-prem not configured"
when `_onPremServerUri` is blank (`ConferenceRoomService.cs:953-961`), and the final
aggregation only fails on `Steps.Where(s => !s.Success)` (`:578-585`). So without this fix, a
synced room with on-prem unconfigured would skip the cloud write (informational "success")
AND skip the on-prem write ("success") → the attribute is written **nowhere** while the
operation reports **full success**. That is the repo's documented silent
success-aggregation failure class (AGENTS.md → Known Failure Classes #2).

Required behavior when `cloudAttrDeferredToOnPrem == true`:
  - The on-prem write must actually run and succeed. If `_onPremServerUri` is blank, or
    `Set-RemoteMailbox` fails, the corresponding step must be recorded `Success = false`
    (not the current skipped-as-success), so the final aggregation fails the operation with
    a clear message (e.g. "Room is on-prem mastered; CustomAttribute9/MailTip could not be
    written cloud-side and the on-prem write was unavailable/failed.").
  - When the room is NOT synced (cloud write succeeded), the existing
    "Skipped — on-prem not configured = success" behavior is unchanged — on-prem is genuinely
    optional there.
  - Implementation note: pass the `cloudAttrDeferredToOnPrem` flag into
    `SetRemoteMailboxAsync` (or have it return a structured result the caller evaluates) so
    the skip-as-success branch is only taken when the cloud write already succeeded.

### 2. Shared base — make `Invoke` self-contained on failure (`Services/ExchangeServiceBase.cs`)

- In `Invoke(ps, tracker)`, **clear `ps.Commands` and `ps.Streams.Error` before throwing on
  ANY `ps.Invoke()` exception** — not only `RuntimeException` (review finding #4: the
  stale-pipeline risk is exception-type-agnostic). Practically: wrap the invoke in
  try/catch that, on any exception, captures detail, sets the connection flag if applicable,
  clears commands + error stream, then rethrows. Apply the same clear-before-throw to the
  `HadErrors` branch.
- **Preserve error detail via structured exception data (review finding #4, preferred over
  string-folding):** attach the captured `ErrorRecord` messages (primary + any secondary) to
  `InvalidOperationException.Data` (e.g. `ex.Data["PsErrors"] = string[]`). Keep the primary
  message as the exception `Message` for human readability.
- **`RunAsync` interaction (`:62-77`):** its `catch` currently re-reads `ps.Streams.Error`,
  which will be empty after the fix. Update it to read `ex.Data["PsErrors"]` for
  `primary`/`detail`, falling back to `ex.Message` when absent. This keeps
  `PermissionResult.Detail` populated. No success-path change.
- This does not alter the success path, the pool borrow/return reset (`ExoConnectionPool`
  already clears on borrow/return), or any cmdlet semantics.

### 3. Versioning (two independent bumps — Constitution §Deployment And Versioning)

- **Shared infrastructure changed** (`ExchangeServiceBase.Invoke`) → bump base app version:
  `<VersionPrefix>` `2.3.4` → `2.3.5`, plus `AssemblyVersion` and `FileVersion`
  `2.3.4.0` → `2.3.5.0` (csproj `:8,11,12`; `InformationalVersion` tracks `VersionPrefix`).
- **Module behavior changed** (Conference Rooms Step 5) → bump ConferenceRooms module
  `Version` `2.0.2` → `2.0.3` (`Modules/ModuleCatalog.cs`).
- Both rules fire independently; this change touches both layers, so both bump.

## Blast-radius audit — base `Invoke` callers (review finding #5)

The base `Invoke`/`InvokeOptional` helpers are called by (non-exhaustive but verified list):
`ConferenceRoomService`, `CalendarPermissionService`, `MailboxPermissionService`,
`MessageTraceService`, `DelegationReportService`, `ExchangeIdentityResolver`,
`RecipientLookupService`, `MigrationService`, `OutOfOfficeService`. (The earlier audit
under-listed these — corrected here; the previous "only five services" wording overclaimed.)

A caller is only at risk from the stream-clearing change if it (a) calls base `Invoke`,
(b) catches the throw, and (c) reads `ps.Streams.Error` for **text** in that catch. Findings:
  - `PermissionValidator`, `ProtectedPrincipalService` read error text post-throw — but use
    **raw `ps.Invoke()`**, not the base helper. Not affected.
  - `CalendarPermissionService`, `MailboxPermissionService`, `MessageTraceService` call base
    `Invoke` but their only `Streams.Error` use is `.Clear()` (a no-op once the base clears),
    and they read text from `ex.Message`. Not affected.
  - The other base-`Invoke` callers above were not observed to read `ps.Streams.Error` for
    text in a catch. External review (GPT) independently found no breaking caller pattern.
  - The single genuine coupling is base `RunAsync` (`:62-77`), handled by the structured
    `ex.Data` change in Fix 2.
- **Residual (honest):** this audit covers `Services/` + `RecipientAutocomplete.razor`. A
  page/component elsewhere reading `ps.Streams` post-throw on a base call would be missed;
  no evidence of one exists. The proof-tests (below) are what convert this from reasoning to
  evidence.

## Files to modify

- `Services/ConferenceRoomService.cs` — Step 5 capture-then-classify; on-prem-deferred
  success coupling (1a); `IsOnPremMasteredWriteError` predicate.
- `Services/ExchangeServiceBase.cs` — `Invoke` clears commands/errors before throwing on any
  exception; structured `ex.Data["PsErrors"]`; `RunAsync` reads `ex.Data`; optional
  `InvokeBestEffort` helper.
- `Modules/ModuleCatalog.cs` — ConferenceRooms `Version` 2.0.2 → 2.0.3.
- `ExchangeAdminWeb.csproj` — `VersionPrefix`/`AssemblyVersion`/`FileVersion` 2.3.4 → 2.3.5.
- `ExchangeAdminWeb.Tests/` — new tests (below).

## Tests (required before "done")

The `Invoke` helpers are static and take a `PowerShell` instance, so they are exercisable
with an in-process runspace (no EXO). New `ExchangeServiceBaseInvokeTests.cs`:

1. **Command queue cleared after a terminating error** — build a pipeline whose command
   throws (e.g. `throw` via a script, or a cmdlet with `-ErrorAction Stop` on bad input);
   assert `Invoke` throws **and** `ps.Commands.Count == 0` afterward, so a subsequently
   added command runs in isolation. (Reproduces Defect B; fails on current code.)
2. **Error stream cleared after throw** — after a thrown `Invoke`, `ps.Streams.Error` is
   empty, so the next step does not inherit stale errors.
3. **Thrown exception carries structured detail** — `ex.Data["PsErrors"]` holds the primary
   (and any secondary) error messages; `ex.Message` holds the primary text.
4. **Non-`RuntimeException` invoke failure also clears the pipeline** (finding #4: the
   cleanup is exception-type-agnostic).
5. **Success path unchanged** — a clean command returns results and clears commands as today.

Conference Rooms (EXO-dependent live behavior cannot be unit-tested here; cover the pure
logic):

6. `IsOnPremMasteredWriteError(string message)` returns true for the real synced-room error
   text, false for unrelated errors (drives the informational-vs-failed branch).
7. **On-prem-deferred success coupling (finding #3)** — extract the aggregation decision into
   a pure/testable shape so a unit test can assert: when the cloud write was deferred to
   on-prem AND the on-prem write was skipped/failed, the overall result is **failure**, not
   success. (This is the highest-value new test — it guards the silent-success class the
   review caught.)

Guard discipline (AGENTS.md): for tests #1/#2/#7, confirm they fail with the corresponding
fix reverted, then pass with it restored.

## Verification

- `dotnet build -c Release` → 0 errors.
- `dotnet test` → all existing + new tests pass (rebuild the **test project** explicitly;
  a stale Debug-only test binary previously caused a vacuous Release `--no-build` run —
  run the freshly built test executable and confirm the new test count).
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore`; `git diff --check HEAD`.
- **Manual (EXO live — not automatable here):** re-run Set Room Type on a synced room
  (`Wilm6-F1-AV2Test.Conf@analog.com`); expect calendar processing + all permissions
  succeed, the CustomAttribute9/MailTip step shows the informational on-prem message, and
  `Set-RemoteMailbox (on-prem)` succeeds. State explicitly whether this manual check was run.

## Out of scope

- Room Finder tab and the building-room-list work (separate, already-implemented change in
  the tree).
- Broader audit of every module's multi-step pipeline for other latent bleed sites — the
  base-class fix neutralizes the class; a module-by-module sweep is not requested here.
- Changing how `Set-RemoteMailbox` (on-prem) is invoked — it already performs the
  authoritative attribute write.

## Open questions for owner

- None. Scope and feedback style decided (both fixes; informational step).

## Revision History

- **2026-06-11 (rev 2):** Revised after external (GPT) review. Changes:
  - #2 — Step 5 must **capture** the cloud-write error before clearing the stream (existing
    `InvokeOptional` clears too early); classify and only treat the known synced-room message
    as informational, all other cloud errors stay visible as failed/warning.
  - #3 (critical) — when the cloud write is deferred to on-prem, overall success now
    **depends on the on-prem write succeeding**; the current "Skipped — on-prem not
    configured = success" branch must record failure in that case to avoid silent
    write-nowhere success. Added §1a and test #7.
  - #4 — base `Invoke` cleanup made exception-type-agnostic; error detail preserved via
    structured `ex.Data["PsErrors"]` (and `RunAsync` reads it) instead of string-folding.
  - #5 — corrected/expanded the blast-radius audit; removed the "only five services"
    overclaim.
  - #6 — versioning was already in §3 (it was absent only from the external review packet).
