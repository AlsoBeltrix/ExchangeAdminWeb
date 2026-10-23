# psl-9: The Exchange Online pool's module import clears only the commands, so a leftover error record makes a new connection's id unidentified and the connection is left to time out

**Severity**: MEDIUM - the psl-7/psl-8 class on the Exchange Online module import. `ExoConnectionPool.CreateConnected`
imports ExchangeOnlineManagement and clears only `ps.Commands`; `ReadConnectionIds` then tests
`ps.Streams.Error.Count > 0` and takes a record the import left for its own failure, so the new connection's
id goes unidentified and the connection is left to time out (the aud-a6b disconnect-by-id path cannot find
it).
**Status**: Fixed on branch, not merged, not pushed; approved under the owner's 2026-10-21 "EVERYTHING" go within the 2026-10-23 stopping rule (MEDIUM); folded into the aud-a60/a61/a53/a59 agent's scope
**Branch**: `worktree-agent-a83ff1596fd6eee17` (from master `a7300dee`)
**Commit**: plan `4614080e`; fix - the commit that carries this text (after aud-a59's `7328135e`)
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21), within the 2026-10-23 stopping rule (MEDIUM); folded into this
agent's scope by the coordinator on 2026-10-23, the record's text taken from master (`cb20834a`, byte for byte). Branch
`worktree-agent-a83ff1596fd6eee17` (from master `a7300dee`; `Services/ExoConnectionPool.cs` is the same file at `909ec552`):
this plan's commit, then one fix commit `fix(exo-pool): ... (psl-9)`, last of the agent's five findings, after aud-a59.
Behaviour: the shared `ExoConnectionPool` (base app version); no module version moves; the bump is the supervisor's.

Evidence re-read at `a7300dee`: `CreateConnected` 746-750 imports ExchangeOnlineManagement under `-ErrorAction Stop`, then
clears `ps.Commands` only. Under Stop the cmdlet's own failure throws and leaves no record, but a record written past the
preference stays in the stream. `ReadConnectionIds` 703-728 - the snapshot taken before the connect - clears only the
commands too, then reads `ps.Streams.Error.Count > 0` (711) as its own failure and answers null; `NewConnectionId(null, ...)`
(679) refuses an unreadable snapshot, so the connection is created with no id (the warning at 794-796), and `DestroyRunspace`
disposes it without `Disconnect-ExchangeOnline -ConnectionId` (aud-a6b): its server session is left to time out. The snapshot
after the connect is clean - the connect's own path clears the streams (780) before it.

1. **The import clears what it leaves.** In place: `try { ps.Invoke(); } finally { ps.Commands.Clear(); ps.Streams.Error.Clear(); }`
   under Stop - the `ActiveDirectoryModuleImport.Import` shape - so `ReadConnectionIds` reads only its own records. The same
   three lines, not a shared helper: `ActiveDirectoryModuleImport` imports the ActiveDirectory module by name and its file is
   another agent's, and a generic helper with one caller would be a second shape rather than one.
2. **Two seams for the connect step**, each defaulting to what it does today: the session's runspace (`ConnectRunspace`: the
   `InitialSessionState.CreateDefault` runspace with the Bypass policy, not yet opened) and the certificate's thumbprint
   (`CertificateThumbprint`: `FindCertificate(subject).Thumbprint`). The seam the pool already has (`connect`) replaces the whole
   step, so a test of the import's clear needs these.

Tests, new `ExchangeAdminWeb.Tests/ExoConnectionPoolConnectTests.cs`: a pool over `ExchangeWriteHarness`'s config connecting
through a local runspace of stand-in `Import-Module`, `Get-ConnectionInformation`, `Connect-ExchangeOnline` and
`Disconnect-ExchangeOnline` (the ids a list; the connect adds one): an import that writes a non-terminating error - the new
connection carries the id that appeared, no "could not identify" warning, and the drain disconnects it by that id; the
control, an import that writes nothing, the same.

Mutation at the call site: M1 the stream clear dropped (the import's `finally` clears the commands only). Extra: E1 the import
back to its old two lines.

Files: `Services/ExoConnectionPool.cs`, `ExchangeAdminWeb.Tests/ExoConnectionPoolConnectTests.cs` (new), this record.

Caller-visible change: none in text. A connection whose module import left an error record past Stop is identified, and
disconnected by its own id when it is retired, where it was disposed and left to time out.

## Fix

- `Services/ExoConnectionPool.cs` `CreateConnected`: the import under Stop inside `try { ps.Invoke(); } finally {
  ps.Commands.Clear(); ps.Streams.Error.Clear(); }`; the session from `ConnectRunspace()` (default `DefaultConnectRunspace`, the
  runspace it built before) and the thumbprint from `CertificateThumbprint(certSubject)` (default
  `FindCertificate(subject).Thumbprint`, as before). Nothing else in the step changed.
- New `ExchangeAdminWeb.Tests/ExoConnectionPoolConnectTests.cs`:
  `ANewConnection_IsIdentified_AndDisconnectedByItsOwnId_WhateverTheImportLeft` (the import writing an error record past Stop,
  and not): the borrowed connection's id is `c-new`, the one that appeared; the connect was handed the organization and the
  seam's thumbprint; no "could not identify" warning; the drain disconnects `c-new` by id and no disconnect fails.

## Guard proof

The aud-a60 driver and protocol (the aud-a60 record's Guard proof; log `psl9-mutations.log`). Filter
`FullyQualifiedName~ExoConnection|FullyQualifiedName~ExoConfigFallback|FullyQualifiedName~ExchangeIdentityResolver|FullyQualifiedName~ExchangeServiceBase`
(203 tests). Fixed-file MD5: `Services/ExoConnectionPool.cs` `4916D3F09DEE89129F9A4197A924BF12`. Both mutants built with 0
errors and both restores matched. After the round, the restored tree rebuilt and the filter: 203/203 green - and again after the test took `TestContext.Current.CancellationToken` for its borrow (the format gate's xUnit1051), the service file untouched.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| M1 | the stream clear dropped: the import's `finally` clears the commands only | `116EDD57737DA927ABD669E10198DD1C` | 1 of 203: `ExoConnectionPoolConnectTests.ANewConnection_IsIdentified_AndDisconnectedByItsOwnId_WhateverTheImportLeft(importWritesAnError: True)` - no id, so no disconnect by id |
| E1 | extra: the import back to its old two lines (`ps.Invoke(); ps.Commands.Clear();`) | `DEA64ABFCE50E5CE885C9632E3647051` | 1 of 203: the same row |

The control row (`importWritesAnError: False`) passes under both mutants: only a record the import leaves behind loses the
id. No separate pre-fix reproduction: E1 is the import as it stood.

## Known gaps

- Labelled assumption, as psl-7's record has it: the real module's import writes no error past Stop; were one written, the
  session goes on with the module loaded and the record cleared. Nothing ran against the module or a tenant: the step ran over
  stand-in functions in a local runspace.
- `ReadConnectionIds` still clears only the commands before its own read; the import's clear is what keeps it to its own
  records, as the coordinator's fix places it.
