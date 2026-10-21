# psl-2 - licensing rows can report an earlier row's PowerShell error

Status: Fixed on branch; awaiting merge. Commit `<pending>`.
Severity: MEDIUM. Native Astra read-only audit, 2026-10-15, at
`b936c604482447c36f93010b6fae135dd71d6df9`.

LicensingUpdatesService.ApplyChanges (line 368) clears commands after a row failure but
retains error records before continuing. ResolveUsers (265) does likewise. If a later write
returns non-terminating errors, its consumer at line 359 can select an earlier row's error.
This is diagnostic attribution across rows, not command replay. The audit does not establish
that a subsequent successful write is reported as failed.

Evidence is control flow only; `../powershell-replay-audit.md` owns the overall inventory.
Before repair, add an actual-SDK multi-row failure reproduction with distinct error messages,
prove the later result carries the wrong row's message, and define error cleanup after each
operation has consumed its own diagnostics. No code or tests changed; no live service ran.
Next proposed action: scope this independently after the current authorized work.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING") admits this record; implementer is a
worktree subagent, one commit `fix(licensing): ... (psl-2)`. Module behaviour: Licensing Updates.

SDK fact the fix rests on (checked before design): `Commands.Clear()` and a new `Invoke()` leave
`Streams.Error` intact, and `HadErrors` after an invocation is true while the stream holds ANY
record, stale ones included. A retained record therefore both names the wrong row and makes a
later clean write look failed. The reproduction must show both.

Scope: `Services/LicensingUpdatesService.cs` only. Smallest seam: ResolveUsers and ApplyChanges
keep creating their runspace; the body that runs on it (module import, credential use, row loop)
moves verbatim into an internal overload taking the PowerShell, marked a test seam like
EvaluateProtectionAsync. Test: new `ExchangeAdminWeb.Tests/LicensingUpdatesPowerShellLifecycleTests.cs`
with local functions standing in for Import-Module, Get-ADUser and Set-ADUser.

Mechanism, the operation being the module import and then each row (its consumers: the catch's
`ex.Message`, the Set-ADUser HadErrors branch):
- The import clears commands and error records after its invocation (no consumer reads them;
  a terminating import still throws out as today). ApplyChanges only, as executed: in
  ResolveUsers nothing reads the stream, so the first row's `finally` already drops the import's
  records; a separate import clear there failed its mutation (not load-bearing) and was removed.
- Each row clears its error records in `finally`, after its own consumers ran, so the next row's
  HadErrors and first record describe only that row (its commands are already cleared on every
  path today, so that code is left alone). Same in ResolveUsers (no stream consumer there today;
  the change keeps the stream from accumulating across rows).
- Row texts, statuses, the protection gate and in-row ordering are unchanged.

Proof: reproduction first on the seam-only refactor: multi-row failure with distinct messages
(later row must carry its own message), failed row then clean write (must be Success), import
noise then clean write (Success), plus controls that a row's own non-terminating errors are
still reported and no command replays. Mutations at the call sites: row-level error clear
removed (ApplyChanges, ResolveUsers), import error clear removed, a clear inserted before the
consumer (ordering control).

## Executed reproduction - 2026-10-21

Branch `worktree-agent-a7f96055b25f98b57`, after psl-4 (`c557fa343c13d810acd1ca2f5f6555a35745d112`).
The seam-only refactor came first (each private method keeps its runspace; its body moved verbatim
into an internal overload taking the PowerShell). On that unfixed sequence,
`ExchangeAdminWeb.Tests/LicensingUpdatesPowerShellLifecycleTests.cs` ran the real ResolveUsers and
ApplyChanges against local functions named Import-Module, Get-ADUser and Set-ADUser in an opened
runspace. Result 5 failed / 1 passed, 0 compiler errors (`out/psl-2-reproduction.log`):

- Row one's Set-ADUser writes `row one context` then throws; row two's writes its own
  non-terminating `row two failed`. Row two was reported with `row one context`: the earlier
  row's message, as the audit predicted.
- Same row one, then a CLEAN Set-ADUser for row two: row two was reported `Error`, not `Success`,
  although its write ran. This goes beyond the audit (which did not establish it): the stale record
  alone makes `HadErrors` true.
- A non-terminating record from the module import made the first, clean write report `Error`.
- ResolveUsers kept a failed row's record, and the import's, on the stream after returning.
- Control passed: a row's own two non-terminating records still report its first one.

## Fix

Purely additive; commands handling, row texts, statuses, the protection gate and in-row order are
unchanged. In ApplyChanges the module import now clears its error records with its commands, and
each row clears its error records in `finally`, after that row's own consumers (the catch's
`ex.Message`, the Set-ADUser HadErrors branch) have run, so the next row's `HadErrors` and first
record describe only that row. ResolveUsers gets the same per-row `finally`; it has no stream
consumer, so that one clear also drops the import's records and no import clear is needed there
(one was tried and its mutation survived, so it was removed). The test seam is the two internal
overloads, documented as such like EvaluateProtectionAsync.

## Guard proof

Compiled mutation round through the same session-local driver as psl-4 (not committed): exact
bytes saved, one literal replacement, `dotnet test ExchangeAdminWeb.slnx -c Release --filter
FullyQualifiedName~LicensingUpdatesPowerShellLifecycleTests|FullyQualifiedName~LicensingUpdatesServiceTests|FullyQualifiedName~LicensingUpdatesServicerTests`
(compiles; TRX-parsed outcomes, 30 tests), exact bytes restored in `finally`, MD5 re-checked, mtime
advanced, same filter rerun green (30/30 after every restore). Every mutant: 0 `error CS`, both
project outputs built. Base MD5 `844DB6AC735D682C95B68D386978C3AA`; seam-only (reproduction)
`6B1F340A773C3F644A979B0201B7F6AC`; fixed, pre-comment `06E412614714EE65B4916671B53F11D8`.

| Mutation (call site) | Mutant MD5 | Named tests failing | Assertion |
|---|---|---|---|
| ApplyChanges row `finally` clear removed | `4146BDC0824CD4BA29CA064C59E3B93D` | ApplyChanges_LaterRowFailure_ReportsItsOwnErrorOnly (got `row one context`), ApplyChanges_CleanWriteAfterAFailedRow_IsReportedSuccess (got `Error`) | Assert.Collection / Equal |
| ApplyChanges import clear removed | `29F3748B917B0DD571E8DD4DDB9E961E` | ApplyChanges_ImportNoise_DoesNotFailTheFirstWrite | Assert.Equal |
| ResolveUsers row `finally` clear removed | `251E0762D925D34F6491F487A21C275C` | ResolveUsers_FailedLookupThenFoundUser_LeavesNoRecordsBehind, ResolveUsers_ImportNoise_LeavesNoRecordsBehind | Assert.Empty |
| clear inserted before the Set-ADUser consumer (ordering) | `D71D6008619BCC1E6718F24E9D4EE2F8` | ApplyChanges_ARowsOwnNonTerminatingErrors_AreStillItsReportedError, ApplyChanges_LaterRowFailure_ReportsItsOwnErrorOnly (both got `Set-ADUser failed.`) | Assert.Collection |

A first round on an earlier fixed version (`FE639C8DC78C8A362E8846F40D42750A`) also probed an import
clear inside ResolveUsers; that mutant passed 30/30, so the line was not load-bearing and was
removed. The four mutations above were then rerun on the final pre-comment file. Comments were
added after the round; final MD5 `55ADF4AEAF4316F49B634C77395F18DA`, focused rerun 30/30. Logs and
receipts: `.agents/review/out/psl-2-*` (machine-local, ignored).

## Gates - 2026-10-21

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 0 errors, 40 existing warnings.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: exit 0, 4,507 passed /
  3 skipped / 0 failed, 4,510 total, 5m08s (the C6 theory ran and passed this time; no filter).
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the new test staged: exit 0.

No PowerShell source changed (no analyzer or Pester run). No AD module import, directory, push or
deployment. No coder self-review verdict is claimed. Module behaviour changed: Licensing Updates
(the supervisor owns its version bump and the index row).
