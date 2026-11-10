# trace-one-server: an on-prem message trace silently searched one server when the transport-server lookup failed or was empty

**Severity**: MEDIUM - each on-premises transport server holds its own tracking log, so a search of one server misses
every message the others handled, and the result looked complete: no warning, no log line. An operator could conclude
a message did not exist. Read-only; nothing is written anywhere, which is why it is not HIGH.
**Status**: Fixed on branch; awaiting merge
**Branch**: `fix/p0-b-phase0-mediums` (worktree `D:\source\ExchangeAdminWeb-wt\p0-b`, from master 4cb7942b)
**Commit**: 7bd72e0a (the warning and the log line, tests, page note, README), a2e27252 (version) |
**Implementer**: Claude (supervised agent)
**Reviewer**: Fable 5.2 audit 2026-11-10, confirmed by Gemini 4 Argon (High) through agy; orchestrator triage
**Plan**: `docs/SharedPiecesAndSlop-Plan.md` slice 0.5 (defect #2)

## Evidence (master 4cb7942b)

- `Services/MessageTraceService.cs:570-572` (`GetOnPremMessageTraceAsync`): `servers = GetOnPremTransportServers(...)`;
  `if (servers.Length == 0) servers = [string.Empty];` - an empty list becomes one query with no `-Server`, which
  the remote session answers from the server it is connected to.
- `:668-691` (`GetOnPremTransportServers`): a bare `catch` cleared the pipeline and returned an empty array; a lookup
  that returned nothing returned an empty array too. Neither was logged, and nothing was added to the response.

## Fix

- `GetOnPremTransportServers` says why it has no servers (`out string? noServersBecause`): the exception's message,
  or "no servers returned". With no session it asks nothing and gives no reason, as before.
- `GetOnPremMessageTraceAsync`, when the list is empty for a reason: logs a warning - "The on-prem transport servers
  could not be listed ({Reason}); message tracking searched the connected server only" - and adds to the result:
  "On-prem message tracking searched one server only: the list of transport servers could not be read. Messages
  handled by other servers may be missing." The search runs exactly as it did.
- The warning travels the way the trace's other warnings do: into the merged response's `Warnings`, which the page
  lists in the yellow box above the results. No export of a trace result carries warnings today (the summary CSV and
  the detail exports are built from the rows), so none was changed.
- No new setting.
- Help: the page's opening note is now two sentences, 187 characters - "Analyze message headers, then trace the same
  message across Exchange Online and on-premises mail flow. A warning above the results says when only one
  on-premises server could be searched." The page had no note or hover tip about the on-premises search to rewrite;
  this is the one line that describes it. Same line of the file, so no line moved and the click-gate registry needed
  no re-anchoring (its tests pass). README: the Message Analysis bullet on what a trace queries.
- App code: +33 / -12 lines (`git diff --numstat a2e27252~2 7bd72e0a -- Services Components`); seven of the removed
  lines are finding IDs taken out of the touched method's trailing comments.

## What follows from it being a warning (existing behaviour of every trace warning, now reached by this one)

1. The page files a trace with any warning as `MessageTrace` **Failed** in the audit log, the warnings as its detail
   (`MessageTrace.razor`, `auditSuccess = response.Error == null && !response.Warnings.Any()`). A search that fell
   back to one server was filed Success; it is now filed Failed with the one-server text.
2. A trace with no rows and a warning shows the warning text as its error (red box) instead of the empty-result
   message (`GetMessageTraceAsync`, "Results.Count == 0 && Warnings.Count > 0"). So a one-server search that found
   nothing now reads as that warning, not as "no messages".

Both are how "On-prem message tracking skipped" and "Some on-prem message tracking queries failed" already behave.
Named for the owner because they change an audit row's result, for the stated defect and no other reason.

## Tests

`MessageTraceOnPremServerLookupTests` (new, 3): the real `GetMessageTraceAsync` over the service's seams - a stand-in
on-premises session whose server lookup throws, names none, or names two, and whose tracking query records the
server it was sent to and answers one message; the cloud half answers nothing.

- `WhenTheServerLookupFailsOrNamesNone_TheConnectedServerIsStillSearched_AndTheResultSaysOneServerOnly` (2: throws;
  none): one query, to no named server, its message in the result, no error; the warning once; one Warning log line
  with the reason (the exception's text; "no servers returned").
- `WhenTheServerLookupNamesTheServers_EachIsSearched_AndNothingIsWarned`: both servers queried, both messages in the
  result, no warning, no error, nothing logged at Warning or above.

No test deleted or changed.

## Guard proof (reverted, targeted run of 271 tests, restored)

| Reverted | Failed | Tests that failed |
|---|---|---|
| The whole fix (`MessageTraceService.cs` as at master) | 2 | `WhenTheServerLookupFailsOrNamesNone_...` (throws), (no servers) |
| The warning is not added to the result | 2 | the same two |
| The reason is not logged | 2 | the same two |

`WhenTheServerLookupNamesTheServers_...` passes throughout: it pins what must not change. Restored: 271 of 271 pass
(full gates in the hand-back).

## Left alone, and why

- The lookup still runs `Get-TransportService -ErrorAction SilentlyContinue`, so an error the remote side reports
  without failing the call arrives as an empty list and is logged "no servers returned", not with its text. Reading
  that text means changing how the lookup is invoked; the plan asks for the fallback to be said, not rebuilt.
- A null session (nothing to ask, and the queries then return nothing) is unchanged and unwarned: it is not a
  search of one server.
- The warning does not carry the reason; the log does. The reason can be a remote exception's text.
- `GetMessageDetailAsync`'s on-premises read does not use the server list; untouched.
- Finding IDs were stripped from the trailing comments of `GetOnPremMessageTraceAsync` and nowhere else.
- Version: Message Analysis 1.8.13. No base bump: no shared code changed.
