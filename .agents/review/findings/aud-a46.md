# aud-a46: Extended log leftovers - the consumer's append has the half-line hazard, a failed read shows "no diagnostics", the dropped-entry count is shown nowhere, and queue overflow is not counted

**Severity**: LOW - diagnostics only. (1) `ExtendedLogService`'s consumer writes each line through a
`StreamWriter` over `FileMode.Append`, so it has the aud-a28z part-3 half-line hazard; the Admin Event
Log's `ParseExtendedLine` drops a merged line. (2) `GetEntries` has a bare catch that returns an empty list
on any read failure, so the event log shows "no diagnostics" instead of "the diagnostics could not be read".
(3) `DroppedEntryCount` (aud-a28z) is exposed but shown on no page. (4) The bounded queue's DropOldest
overflow is not counted (the channel's itemDropped callback would do it).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28z/a26u/cdt-41 agent, 2026-10-22, candidates 2, 3 and 4 of its hand-back)

## Evidence

At `36da9789`: `Services/ExtendedLogService.cs` around 261-263 (the append), 216-218 (`GetEntries` bare
catch), `DroppedEntryCount`, the bounded channel's options; `Components/Pages/AdminEventLog.razor`
`ParseExtendedLine` around 824 and the diagnostics section; `Components/Pages/AdminSettings.razor` (where a
health figure belongs - NotYetConverted).

## Approach (to be written by the implementing agent before code)

- The consumer writes one line per unbuffered write with the aud-a28z truncate-back and tail repair (share
  the JSONL writer's helper rather than copy it).
- `GetEntries` returns a typed outcome (entries, or failed with a reason); the event log shows the failure.
- The dropped-entry count (including DropOldest overflow via `itemDropped`) is shown on the Admin Settings
  diagnostics area with one line of text.
- Tests through `ExtendedLogServiceTests` and the pages' reflection drivers; mutations at the call site: the
  buffered append again; the bare catch again; the overflow callback removed.

## Guard proof

Pending.
