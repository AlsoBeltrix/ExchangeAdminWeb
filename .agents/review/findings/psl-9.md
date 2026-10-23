# psl-9: The Exchange Online pool's module import clears only the commands, so a leftover error record makes a new connection's id unidentified and the connection is left to time out

**Severity**: MEDIUM - the psl-7/psl-8 class on the Exchange Online module import. `ExoConnectionPool.CreateConnected`
imports ExchangeOnlineManagement and clears only `ps.Commands`; `ReadConnectionIds` then tests
`ps.Streams.Error.Count > 0` and takes a record the import left for its own failure, so the new connection's
id goes unidentified and the connection is left to time out (the aud-a6b disconnect-by-id path cannot find
it).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); folded into the aud-a60/a61/a53/a59 agent's scope
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the psl-8/a2w/a2x agent, 2026-10-23, its "Left for a follow-up")

## Evidence

At `909ec552`: `Services/ExoConnectionPool.cs` `CreateConnected` around 746-750 (the import and the
commands-only clear) and `ReadConnectionIds` around 711 (`ps.Streams.Error.Count > 0`).

## Approach (to be written by the implementing agent before code)

- The import runs under Stop with `try { ps.Invoke(); } finally { ps.Commands.Clear(); ps.Streams.Error.Clear(); }`
  (the `ActiveDirectoryModuleImport.Import` shape; a shared helper if one fits both modules, else the same
  three lines), so `ReadConnectionIds` reads only its own records.
- Test through the pool's runspace seam: an import that reports a non-terminating error, then the
  connection id is read; mutation at the call site: the stream clear dropped.

## Guard proof

Pending.
