# psl-3 - DHCP discovery errors survive into a later mutation report

Status: Fixed on branch; awaiting merge. Commit `<pending>`.
Severity: MEDIUM. Native Astra read-only audit, 2026-10-15, at
`b936c604482447c36f93010b6fae135dd71d6df9`.

DhcpAuthorizationService.DiscoverDomainController (line 192) clears commands after a failed
discovery attempt but retains errors while trying a fallback. A later authorization or
deauthorization error consumer (102/158) can include those stale discovery errors.
This is retained diagnostic state, not command replay or proof that a successful write fails.

Evidence is control flow only; `../powershell-replay-audit.md` owns the overall inventory.
Before repair, exercise failed discovery, successful fallback and a later distinct failure
with harmless SDK commands; prove only the last operation's diagnostics belong in its report.
Preserve diagnostic consumption before cleanup. No code/tests changed or live service ran.
Next proposed action: scope this independently after the current authorized work.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING") admits this record; implementer is a
worktree subagent, one commit `fix(dhcp): ... (psl-3)`. Module behaviour: DHCP Authorization.

SDK fact (see psl-2): `HadErrors` after an invocation is true while `Streams.Error` holds any
record, stale ones included, so a retained discovery record also makes a SUCCESSFUL later
authorization report "Failed to authorize". The reproduction must show both that and the
mixed report.

Scope: `Services/DhcpAuthorizationService.cs` only. Smallest seam: AuthorizeServerAsync and
DeauthorizeServerAsync keep their gates and runspace creation; the body that runs on the
runspace (import, discovery, mutation) moves verbatim into internal overloads taking the
PowerShell. Test: new `ExchangeAdminWeb.Tests/DhcpAuthorizationPowerShellLifecycleTests.cs`
with local functions standing in for Import-Module, Get-ADDomainController and Invoke-Command.

Mechanism, the operations being the import, each discovery attempt, and the mutation:
- Each discovery attempt clears its error records in `finally`, after its catch has logged the
  exception, on success and failure alike (its commands are already cleared on every path).
- The import needs no clear of its own: discovery always runs next with no consumer in between,
  so the first attempt's `finally` drops the import's records (a separate clear would be
  unprovable by mutation; psl-2 showed the same shape in ResolveUsers).
- The mutation's HadErrors consumer is unchanged and now sees only the mutation's records;
  every one of its own records is still joined into the report.

Proof: reproduction first on the seam-only refactor, for both authorize and deauthorize: failed
discovery, successful fallback, then a distinct mutation failure (report must hold only the
mutation's text) and then a clean mutation (must succeed); soft records on a successful
discovery or fallback and import noise must not fail a clean mutation; controls that all of
the mutation's own records are reported and nothing replays. Mutations at the call sites:
each attempt's error clear removed; a clear inserted before each change's consumer (ordering).

## Executed reproduction - 2026-10-21

Branch `worktree-agent-a7f96055b25f98b57`, after psl-4 and psl-2. The seam-only refactor came first:
AuthorizeServerAsync and DeauthorizeServerAsync keep their gates and runspace; the body that runs on
it moved into internal AuthorizeServer/DeauthorizeServer overloads (`git diff -w`: only the
`creds.Value.x` to `creds.x` rename and the new method boundaries). On that unfixed sequence,
`ExchangeAdminWeb.Tests/DhcpAuthorizationPowerShellLifecycleTests.cs` ran both operations against
local functions named Import-Module, Get-ADDomainController and Invoke-Command (the stand-in records
the cmdlet inside the script block and never executes it). Result 10 failed / 2 passed, 0 compiler
errors (`out/psl-3-reproduction.log`), identically for authorize and deauthorize:

- Discovery writes `discover context` then throws; the fallback succeeds; the change writes its own
  `remote change refused`. Report: `Failed to authorize: discover context; remote change refused`,
  as the audit predicted.
- Same discovery, then a CLEAN change: `Failed to authorize: discover context`. The successful
  change was reported as failed, which the audit had not established.
- A successful discovery with a soft record, an empty discovery then a fallback with a soft record,
  and import noise each turned a clean change into `Failed to authorize: <that record>`.
- Control passed: the change's own two records are both reported. (The call-sequence check that
  nothing replays sits after the message assertion in the first test, so it ran only post-fix.)

## Fix

Purely additive, inside DiscoverDomainController only: each attempt clears its error records in
`finally`, after its catch has logged, on success and failure alike (commands were already cleared
on every path and are unchanged). The change's HadErrors consumer, its message texts, the gates and
the fallback order are unchanged. No import clear was added: discovery always runs straight after
the import with no consumer in between, so the first attempt's `finally` drops the import's
records; the import-noise tests guard exactly that.

## Guard proof

Compiled mutation round through the same session-local driver as psl-4 (not committed): exact
bytes saved, one literal replacement, `dotnet test ExchangeAdminWeb.slnx -c Release --filter
FullyQualifiedName~DhcpAuthorizationPowerShellLifecycleTests|FullyQualifiedName~DhcpAuthorizationCsvTests`
(compiles; TRX-parsed outcomes, 15 tests), exact bytes restored in `finally`, MD5 re-checked, mtime
advanced, same filter rerun green (15/15 after every restore). Every mutant: 0 `error CS`, both
project outputs built. Base MD5 `8A719C658AE3A979958897793ECC250D`; seam-only (reproduction)
`401189149B0269388883C29146522834`; fixed, pre-comment `3D8A21EE3EBD112B6317BDD8D1CB4E4F`.

| Mutation (call site) | Mutant MD5 | Named tests failing (both operations each) | Assertion |
|---|---|---|---|
| first attempt's `finally` clear removed | `FDF1CBF87DC9A93B34188D9709A9CCF4` | DiscoveryThatSucceedsWithARecord_ThenCleanChange_Succeeds, ImportNoise_ThenCleanChange_Succeeds (4) | Assert.True: `Failed to ...: discover soft` / `import noise` |
| fallback attempt's `finally` clear removed | `0720EC56975C78C9ED2F11C8CF371F25` | EmptyDiscovery_FallbackThatSucceedsWithARecord_ThenCleanChange_Succeeds (2) | Assert.True: `Failed to ...: filter soft` |
| clear inserted before the authorize consumer (ordering) | `F4AD1015B64E109B9056D4F9EA92B53A` | FailedDiscovery_Fallback_ThenChangeFailure_ReportsOnlyTheChangesErrors, TheChangesOwnRecords_AreAllReported (authorize) | Assert.Equal |
| clear inserted before the deauthorize consumer (ordering) | `54C92D6C62B7D9BB5342EFF7C92CE2B0` | the same two (deauthorize) | Assert.Equal |

Redundancy, stated plainly: the record's own scenario (failed discovery, then the fallback) is
fixed by EITHER attempt's clear, so neither single-attempt mutation fails those two tests; they
fail only with both clears gone (the reproduction). Each clear still has its own named failures
above. Comments were added after the round; final MD5 `B3FB7C14F949565E31BFCD3CE7FADF6C`,
focused rerun 15/15. Logs and receipts: `.agents/review/out/psl-3-*` (machine-local, ignored).

## Gates - 2026-10-21

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 0 errors, 40 existing warnings.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: the first run hit the
  known-flaky `Comms10kRefusalTests.C6ValidationWaitsKeepTheCapturedTicketVisibleAndRejectDelayedInput`
  theory again (blame-hang inactivity abort after 4,333 passed / 0 failed). Rerun with
  `--filter "FullyQualifiedName!~C6ValidationWaitsKeepTheCapturedTicketVisibleAndRejectDelayedInput"`
  on the psl-3-only tree (the uncommitted psl-5 work was set aside first, byte copies checked by
  MD5): exit 0, 4,516 passed / 3 skipped / 0 failed, 4,519 total, 4m54s.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the new test staged: exit 0.

No PowerShell source changed (no analyzer or Pester run). No AD/DHCP module import, domain
controller, remote command, push or deployment. No coder self-review verdict is claimed. Module
behaviour changed: DHCP Authorization (the supervisor owns its version bump and the index row).
