# psl-1 - an optional Exchange command survives failure and replays

Status: Implemented and independently reviewed on master. Push awaits authority.
Severity: HIGH. Native Astra read-only audit, 2026-10-15, at
`b936c604482447c36f93010b6fae135dd71d6df9`.

## Evidence

ExchangeServiceBase.InvokeOptional (lines 268-273) calls ps.Invoke before clearing commands
and errors. A terminating exception skips both clears. ConferenceRoomService.SetCalendarPermission
uses it for Add-MailboxFolderPermission (line 1208), catches failure (1223), and returns to
SetCalendarPermissionsForType's next permission (1149 onward), reusing the same object.
The next invocation can execute the failed mutation again, including when its first call
changed permissions before throwing. The last permission can leak into timezone commands
(line 832). This was the read-only audit evidence; the executed reproduction is recorded below.

The same root also affects SetRoomTypeAsync's Get-Recipient (705): catch at 709 swallows
failure, then permission removal (724) or calendar processing (768) reuses the instance.
That replays a failed read inside later work; it is not a second queued-mutation finding.

The complete ownership inventory and limits are in `../powershell-replay-audit.md`.
The twenty existing remediation findings remain independently authorized. The owner separately
approved this extension on 2026-10-15; `.agents/decisions.md` records the authority.

## Approved repair and proof

`docs/PowerShellCommandLifecycle-Plan.md` owns scope, preserved error/tracker/retry contracts,
harmless SDK tests, versions and gates. Repair the one shared helper; do not mechanically
convert disposal-safe sites. Exercise the actual Conference Rooms sequence, including a
simulated mutation effect followed by a throw, and prove the old helper produces duplicate
calls. Preserve the original exception and RunAsync diagnostic context while cleaning up.

The owner approved one Sol implementation slice and independent Astra workflow review, after
C4's committed correction and before resuming C11. The implementation and executed proof follow.

## Declared slice scope - 2026-10-15

Implementation starts on clean master `76ba54599fdfbb3810b488fdc88478f4f79c268a`.
Both origin and github master were checked live at `885397981d7550afebf6518ef21b4fc54b062859`.
No branch or push is authorized. The known module-spec 2.0 / June 25 drift remains flagged;
the Constitution and current code supply the relevant contracts.

- `Services/ExchangeServiceBase.cs` - repair only InvokeOptional and its adjacent contract comments.
- `ExchangeAdminWeb.Tests/ExchangeServiceBaseInvokeTests.cs` - real-SDK optional invocation guards.
- `ExchangeAdminWeb.Tests/ConferenceRoomPowerShellLifecycleTests.cs` - reflect into the actual
  existing permission sequence; no production Conference Rooms seam or direct behavior edit.
- `ExchangeAdminWeb.csproj` - shared base app patch bump only.
- `docs/PowerShellCommandLifecycle-Plan.md`, `.agents/state.md`, `.agents/review/index.md`,
  `.agents/token-log.md` and this finding - implementation, verification and review records.

The actual Conference Rooms source, other helpers, retry eligibility, pool lifecycle and the
deferred psl-2/psl-3 findings stay outside this implementation slice.

## Cause and implementation

The failure mechanism is a terminating SDK throw: the old helper's clears are after
`ps.Invoke()`, so they are skipped. `SetCalendarPermission` catches that exception and the
existing `SetCalendarPermissionsForType` switch queues the next user's add on the same object.
The failed command is then part of that next invocation. Pool return/discard cleanup happens
too late to prevent replay inside the still-running operation. The caught `Get-Recipient` in
`SetRoomTypeAsync` is another continuation through this same helper, not a second mutation root.

`InvokeOptional` now clears both commands and error records in `finally`. Successful output
is returned unchanged and optional non-terminating errors remain deliberately consumed.
Connection/precheck classification runs while the error records are still available, including
classification of the thrown exception when no stream record exists. Before a terminating
failure is rethrown with `throw;`, its stream messages are attached to that same exception under
`PsErrorsDataKey`. `RunAsync` can therefore use its existing `ResolvePsErrors` path after cleanup.
No exception wrapping, retry policy, caller sequence, pool logic or other helper changed.

The shared base app moves `2.27.1` -> `2.27.2` (VersionPrefix, AssemblyVersion and FileVersion).
Conference Rooms has no direct production edit, so its independent module version is unchanged.

## Executed reproduction and regression coverage

The reproduction uses Microsoft.PowerShell.SDK 7.6.1 in an isolated, opened runspace. Reflection
calls the existing private `SetCalendarPermissionsForType` method for the actual Exception-room
sequence: Anonymous/Reviewer, Default/Reviewer, configured administrators/Editor. Local functions
named `Add-MailboxFolderPermission` and `Set-MailboxFolderPermission` record identity, user and
rights. The first add records its simulated effect and then throws once. The next users must
receive exactly their own add/set commands; the first permission step must retain its failure.
The config is the existing temporary SQLite fixture. No production test seam was added.

Before changing the helper, the compiled sequence produced two `Assert.Equal` failures:
the effect list contained Anonymous again where Default was expected, and the next command
was Add/Anonymous instead of Add/Default. The local log is
`.agents/review/out/psl-1-actual-reproduction.log`. The first stand-in attempt used advanced
functions, where SilentlyContinue consumed the injected throw; it was not accepted as replay
proof. The final stand-ins explicitly accept an inert ErrorAction parameter so the injected
throw genuinely terminates `ps.Invoke()`. Ordinary optional cmdlet errors are tested separately
with real `Write-Error -ErrorAction Continue`.

Sixteen new cases cover the actual sequence (2), successful output, optional non-terminating
errors, both cleanup collections on throw, next-call isolation, original exception identity and
two-message diagnostics, stream-based classification (6), and exception-only classification (2).
The original exception is compared by reference with the SDK's Failed-state event reason.
The diagnostic assertion uses `ResolvePsErrors`, the same consumer used by `RunAsync` after
the live stream is cleared. Business errors remain unclassified; a session drop classifies for
discard only; GetCurrentConnectionContext also classifies as a retriable precheck. Existing
pool tests continue to prove that only eligible prechecks retry and multi-write operations do not.

## Compiled mutation proof

All mutation runs compile through the solution, without `--no-build`:

`dotnet test ExchangeAdminWeb.slnx -c Release --filter <guard filter> --logger 'console;verbosity=normal' -- xUnit.MaxParallelThreads=4`

| Helper mutation | Guard filter / observed assertion failures | Result |
|---|---|---|
| Exact old helper from base HEAD | All InvokeOptional and ConferenceRoomPowerShellLifecycle tests; duplicate effect, wrong next command, uncleared state, missing diagnostics/classification | 11 failed / 5 controls passed |
| Omit error clear in finally | Optional error consumption, terminating error cleanup and all six stream classifier cases; Assert.Empty | 8 failed |
| Clear stream before each classification | Six stream classifier cases; four positive Assert.Equal failures, both business-error controls pass | 4 failed / 2 passed |
| Omit PsErrorsDataKey capture | Original exception/diagnostics case; Assert.IsType sees missing string array | 1 failed |
| Return empty output | Success and non-terminating-output cases; Assert.Equal and Assert.Single | 2 failed |
| Omit thrown exception from classification | Both exception-only classifier cases; Assert.True | 2 failed |
| Wrap the original exception | Original exception/diagnostics case; Assert.ThrowsAny rejects changed exception type | 1 failed |

These are 29 actual assertion failures across seven independently compiled mutation groups.
Every new case fails under at least one mutation. Each log was checked for `error CS`, other
build failures, both compiled project outputs, the expected test count and actual Assert failures.
The output probe was rerun after replacing its LINQ Single call with Assert.Single, so an empty
collection is an assertion failure rather than an incidental sequence exception.

The helper was saved and restored as exact bytes in `finally` after every probe. Both hashes
were checked and mtime advanced before compiling again. The unchanged fixed helper is:

- MD5: `790DE7C46BBE579E3FEB0C67BE9C8BDF`
- SHA256: `ECE1EC5B9EC63EB7AE6D7650DDEEFF3F25D6829281F837E306B6E72694371EC8`

| Mutation | Mutant MD5 | Mutant SHA256 |
|---|---|---|
| old-helper | `8133B17D4A891C1F8464B03BB9C31BD2` | `B08D1AB3E71562E40CF23E6CDC12D305B93688FC6F05C5D9C6B56A8D77FDB21C` |
| error-cleanup | `4279B799ECA79E074470D17C578267BE` | `D7CCF35C274BFFB853AA97F6CCF6664BF31230B026541D449651FE50607B5C14` |
| classification-order | `3A316D554E17AA4DAFE35627A0B98B21` | `AAF32CB50DAA240C41EE0C7F456D7F50B9A400B380B07B2ACFBD1943DBCB1B72` |
| diagnostics | `1DBCFAAC36888CB5035C3412B3159CFF` | `4F7A2524872638784018630B8263C8C9D2BBE3719EEE032FD6246A6E80EEFC52` |
| output | `75CB2E5D30AD9F9BA5D6CC35D6C0D0B4` | `35C44B347BBF56883E8C20841DEA95E0C23F614B8B270DD8BD2FF03080D2C6F9` |
| thrown-classification | `EBBB6E5CF1B6EE2403F520341E65A223` | `8D384EE5B7B82B564555B12A0DFD31D60AE0BDA0B8BE161EF1E766E90728DB11` |
| original-exception | `D66FDCEBA775270916ECB17842FDB6D7` | `A2AC22210C3B6C56607D2CE1A1652FA42141BB43BE1C21092D90D1C288DBEF50` |

Machine-local logs and restoration receipts are
`.agents/review/out/psl-1-mutation-<name>.log` and `psl-1-receipt-<name>.log`.
The final restored focused run passed 34/34 (all helper, sequence and existing pool retry tests),
with zero failures or compile errors: `psl-1-focused-restored.log`.

## Restored repository gate

Final restored gate, 2026-10-15:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: exit 0, 0 errors, 40 reported existing
  warnings, 2.88 seconds (`psl-1-release-build.log`).
- `dotnet test ExchangeAdminWeb.slnx -c Release -- xUnit.MaxParallelThreads=4`: exit 0,
  3,956 passed / 3 existing AD-unavailable skips / 0 failed, 3,959 total, 5m04s
  (`psl-1-release-test.log`). No redundant baseline full suite was run.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0;
  workspace-loading warnings only (`psl-1-format.log`).
- `git diff --check HEAD`: exit 0 (`psl-1-diff-check.log`).
- `tools/Test-AsciiOnly.ps1`: exit 0 with the new C# tests staged (`psl-1-ascii.log`).

Both restored build/test logs were checked for compiler errors; there were zero `error CS`
matches. All listed logs are machine-local under `.agents/review/out/`.

No shipped PowerShell source changed, so PSScriptAnalyzer/Pester are not applicable. The new
proof uses no EXO/AD imports, credentials, network connection or live backend mutation.
No live directory/tenant probe, deployment or push was performed.
Installed-cmdlet interoperability, actual remote partial failures and browser rendering remain
unverified. No coder self-review was performed; independent Astra workflow review is next.

## Review packet

### Mode

IMPLEMENTATION_REVIEW

### Original request

Owner selected "Approve the additional slice (Recommended)" for the shared optional Exchange
replay repair, after C4's committed correction and before resuming the original twenty findings
at C11. Use Sol 6.1 max implementation and independent Astra max review.

### Approved plan

`docs/PowerShellCommandLifecycle-Plan.md`, approved 2026-10-15.

### Commit range

`76ba54599fdfbb3810b488fdc88478f4f79c268a..HEAD` at this finding's implementation commit;
the parent supplies the resolved head to the independent reviewer.

### Claimed changes

- InvokeOptional always clears queued commands and errors, preserves successful output and
  deliberately consumes optional non-terminating errors.
- A terminating failure preserves its exception identity and diagnostic messages, with
  connection/precheck classification before cleanup.
- Real-SDK tests exercise the actual Conference Rooms continuation and helper contracts.
- Only the base app patch version changes, from 2.27.1 to 2.27.2.

### Constraints / out of scope

No other invocation rewrites, generic abstraction, SDK upgrade, pool/retry changes, module
production edits, psl-2/psl-3 fixes, live systems, push, deployment or branch operations.

### Design decisions

Keep the local helper and existing error-data key. Rethrow the same exception so caller
reporting and exception classification retain their current inputs. Reflection exercises the
existing Conference Rooms switch and catches without adding a production seam. The local
fault deliberately terminates at the SDK boundary regardless of optional ErrorAction handling;
separate tests preserve the intentional non-terminating-error behavior.

### Deferred work

Independent workflow review is pending. Original twenty-finding remediation retains its fifteen
remaining findings, next C11; psl-2/psl-3 remain separately deferred. Live cmdlet interoperability
and browser/manual acceptance were not performed.

### Review focus

Trace actual helper callers through caught-failure continuations, RunAsync diagnostic recovery,
tracker-based discard/precheck eligibility and pool return/disposal. Check the actual sequence
test's ability to observe a mutation effect before throw and the isolation of later commands.
Assess the whole affected workflow, beyond the helper diff. No self-review verdict is claimed.

### Verification run

The Restored repository gate section above owns the exact commands and receipts: build
0 errors; full suite 3,956 passed / 3 skipped / 0 failed; format/diff/staged ASCII passed.
Seven compiled mutation groups produced 29 intended assertion failures with verified
byte/hash/mtime restoration; the restored focused run passed 34/34. No self-review verdict
or live-environment acceptance is claimed.

## Independent workflow review - 2026-10-15

Native `/root/review_psl1`, `gpt-6-astra` at `max`, returned PASS with no in-scope findings for
`76ba54599fdfbb3810b488fdc88478f4f79c268a..22afbf912cb06c775f2679203b7463ff9ce4fcac`.
The checkout remained frozen and clean. No files were edited.

The reviewer traced helper callers, caught-failure continuations, RunAsync diagnostic recovery,
tracker classification and pool/retry lifecycle. Cleanup covers success and terminating failure;
optional nonterminating errors retain their intended handling. Output, original exception and
diagnostics are preserved, with classification before cleanup and unchanged retry eligibility.
The actual Conference Rooms sequence establishes one first effect, correct later commands and
preservation of the initial failure. The terminating stand-in and nonterminating tests cover
distinct SDK paths; no installed-cmdlet integration claim follows from them.

Independent verification freshly compiled the Release solution and passed all 34 focused cases.
The range diff check passed. Current helper hashes matched receipts; reconstructing the old
helper from the base in memory also matched its mutation hashes. All seven mutation logs had
the recorded 29 assertion failures, expected controls and compiled outputs, without compiler
errors. The reviewer inspected the recorded 3,956 passed / 3 skipped full gate and other checks;
the full suite and mutations were not repeated. Live cmdlets, remote failures and browser
behavior remain unverified.

A separate pre-existing pooled-query tracker-loss finding was confirmed outside this scope;
its dedicated follow-up record owns the details. It does not block psl-1 or authorize more code.
Next: resume the original approved remediation at C11. This commit remains local pending push.
