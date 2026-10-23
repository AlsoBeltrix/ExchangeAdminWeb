# aud-a65: A sustained rotation failure grows the active JSONL file without bound after one warning

**Severity**: MEDIUM - audit visibility under a persistent fault. Since aud-a56 a row is never dropped for
a rotation failure: it is appended to the full active file and one warning is given per failure streak.
With rotation failing for days (a SIEM tailer holding the file without delete sharing; a folder refusing
delete) there is no ceiling and no second warning until a rotation succeeds, so the active audit or trace
file grows until the log volume fills, after which writes fail as ordinary file-write failures and
visibility degrades anyway.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); folded into the aud-a62/a63 agent's scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-16, 2026-10-23)

## Evidence

At `d2f6f2ec`: `Services/JsonlFileAppender.cs` around line 85 (the `rotationFailed` callback and the
append past the limit); `Services/JsonlLogService.cs` around 159 (the one-warning streak);
`Services/ExtendedLogService.cs` (the same shape).

## Approach (to be written by the implementing agent before code)

- Keep "never drop the row". Add an escalation: the rotation-failure warning repeats on a schedule while
  the streak lasts (every N MB of growth past the limit, or every M minutes, whichever first), naming the
  current size and the amount over the limit; the streak's state (since when, how far over) is exposed on
  the Admin Event Log diagnostics area beside the dropped-entry count (aud-a46/a49).
- Preferably a bounded overflow pattern: when the active file is more than K times its limit and
  rotation still fails, the writer opens `<active>.overflow-<n>.jsonl` (a new file needs no delete right
  and no move) and appends there; the readers include overflow files for the date; the overflow files are
  rotated into the normal chain when rotation succeeds again. Record which of the two (escalation only, or
  escalation plus overflow) was built and why.
- Tests: a persistent rotation failure with growth past the limit re-warns and (if built) spills to an
  overflow file the reader includes; mutation at the call site: the re-warn schedule removed.

## Guard proof

Pending.
