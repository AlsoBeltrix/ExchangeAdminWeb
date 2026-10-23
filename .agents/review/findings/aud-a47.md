# aud-a47: Two app instances writing one JSONL audit file can overwrite or truncate each other's rows; the writer is not single-writer by construction

**Severity**: HIGH - loss of mandatory audit evidence for successful admin actions. Dev and prod use the
same `Audit:LogRoot` default/sample root (`deploy.ps1`, `tools/deploy-pipeline.ps1`), so both instances
write `<root>\ExchangeAdminWeb\exchangeadmin_YYYYMMDD.jsonl`. The aud-a28z writer opens with
`FileShare.ReadWrite | FileShare.Delete`, seeks to EOF, writes on a read/write handle and on failure
truncates back to the pre-write length. With two writers: A records its start offset, B appends and returns
`Written`, then A writes at the stale offset over B's row, or A fails and `SetLength(start)` truncates B's
completed row; B's trace still says AuditWritten Success and the Admin Event Log later misses or drops the
row. The `db` command-line mode exits before logging, so the second writer is another web instance. The
aud-a28z record states "one writer per JSONL file" as an unprobed assumption; nothing enforces it.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; folded into the aud-a45/a46 agent's scope
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-13, 2026-10-22)

## Evidence

At `2df48c36`: `Services/JsonlLogService.cs` around line 136 (open mode, the EOF seek, the truncate-back,
the tail repair); `deploy.ps1` line 14 and `tools/deploy-pipeline.ps1` line 52 (the shared log root);
`Program.cs` (`db` mode exits before logging); `.agents/review/findings/aud-a28z.md` Known gaps ("one
writer per JSONL file").

## Approach (to be written by the implementing agent before code)

- Single-writer by construction: every path selection, rotation, tail repair, write and truncate-back on a
  JSONL file runs under a cross-process named mutex derived from the file's full path (`Global\` scope, so
  two IIS app pools on one host share it), acquired with a bounded wait; a wait that times out is a dropped
  row reported through the aud-a28z recovery path (never a write at a stale offset). The offset is read
  AFTER the mutex is held, never before. The extended log's consumer (aud-a46) uses the same helper.
- The same for the Admin Event Log reader's repair, if it has one.
- Document in the operator-facing setup (README deployment section and the deploy sample) that dev and prod
  may share a log root only because the writer serialises across processes; if the owner prefers distinct
  roots, that is a deploy-config change, not code.
- Tests: two writers in one process on one file through the helper (the mutex is cross-process, so one
  process exercises it), interleaved failures, the file parses to exactly the rows reported Written; a
  wait that times out is a dropped row. Mutation at the call site: the mutex removed; the offset read before
  the mutex again.

## Guard proof

Pending.
