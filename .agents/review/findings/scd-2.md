# scd-2: The cache watcher could miss the first cross-process change

**Severity**: HIGH - a change committed on dev between prod's cache load and prod's
first watcher poll would be recorded as the baseline and never reloaded; for the
extended-log minimum level, which has no TTL, until the next unrelated config write.
**Status**: Verified (plan revised; docs-only - no code exists yet)
**Branch**: -
**Commit**: lands in the same commit as this record and the plan revision; read it
from `git log -1 -- .agents/review/findings/scd-2.md`.

## Evidence

`docs/SharedConfigDb-Plan.md` at `8bb46a4`: section 6 S2 `HasChangedSince(ref
lastSeen)` "returns true when the stored token differs from lastSeen (and updates it)"
placed on the cache-hit path; section 8 test `HasChangedSince_FirstCall_SeedsAndReturnsFalse`.
Caches are loaded independently of the watcher: `Services/ExtendedLogService.cs:52`
(constructor load), `Services/ProtectedPrincipalService.cs:291-294` (TTL hit returns
the cached config), `Services/ADAttributeEditorService.cs:126-143`.

## Predicted observable failure

Prod starts, loads the log level (token 41). Dev changes the level (token 42). Prod's
first log line polls the watcher, which seeds 42 and returns false. Prod keeps the old
level until another write moves the token to 43.

## What

Plan defect: the baseline token was tied to the first poll rather than to the load it
protects.

## Approach

Plan revised: the watcher exposes `CurrentToken()` (unthrottled) and
`HasChangedSince(long loadedToken)`; every reader reads the token BEFORE loading and
records it with the loaded value (and after its own save), so a write racing the load
moves the token past the recorded one and is detected on the next check. A failed
`CurrentToken()` records `Unknown`, which always compares as changed. New settled
item, AC4, a section 4 row, four watcher tests and a per-reader "change before first
poll" test plus an out-of-band log-level test.

## Files changed

- `docs/SharedConfigDb-Plan.md` - section 1, AC4, section 4, section 6 (S2 sketch),
  section 8, section 10.

## Guard proof

Docs-only. `<Reader>_DetectsAChangeMadeBeforeItsFirstPoll` bites at S2. `git diff
--check` clean on the fold commit.

## Coder dispute (if any)

None.

## Known gaps

None.

## Reviewer comments

Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier
  (grade fallback; same dispatch as scd-1)
Harness: codex-cli 0.152.1. Reviewed SHA `8bb46a4`, base `e36e798`, capability_ok
true, verdict `acceptable_with_changes` (material change 2). Dispatched 2026-09-04;
envelope at `.agents/review/scd.result.json`.
