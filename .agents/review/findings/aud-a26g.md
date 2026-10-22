# aud-a26g: The attribute editor's Set-ADUser write keeps a HadErrors branch its own comment calls unreachable

**Severity**: LOW - dead code, fails closed. `ADAttributeEditorService`'s `Set-ADUser` write (around
line 1243 at `15de2fcf`) is invoked with `-ErrorAction Stop`, so an error is thrown and leaves no
record in the stream; the `if (ps.HadErrors)` branch after it cannot fire on the write's own error.
It can fire on a stale record an earlier command left (psl-2/psl-3 class), in which case a clean
write would be reported as a failure. The comment above it already says it is unreachable. Same
pattern aud-a2o removed from the Conference Rooms group write.
**Status**: Merged to master (`f0cccea8`); closed on guard proof; codex review pending
**Branch**: `worktree-agent-ad10b5ab24c8ce164`
**Commit**: `748d358d` (comments `ee00d337`; plan `1aeaa315`; merged `f0cccea8`)
**Reviewer**: implementer (surfaced by the aud-a2o agent, 2026-10-22, candidate 3 of its hand-back)

## Evidence

At `15de2fcf`, `Services/ADAttributeEditorService.cs`, the `Set-ADUser` write in `PerformSave`:
`-ErrorAction Stop`, then `if (ps.HadErrors) ...` with a comment calling it unreachable.

## Approach (to be written by the implementing agent before code)

- Remove the branch as aud-a2o did: try/catch/finally, the thrown error is the write's error, the
  `finally` clears the command and the error stream before the next command, and the aud-a28 /
  a28c audit of a failed write stays exactly as it is. Comment says why (measured: an error
  promoted by `-ErrorAction Stop` throws and leaves zero records).
- Tests through `AttributeEditorHarness`: a stand-in `Set-ADUser` that writes a non-terminating
  error (its own `-ErrorAction Continue`) and applies the change is not taken for a failed write; a
  thrown error is still the audited failure (aud-a28c). Mutation at the call site: restore the
  branch.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the aud-a2o hand-back. One
commit `fix(ad-attribute-editor): the Set-ADUser write is judged by its throw alone, as the group
writes are (aud-a26g)`, second of three on this branch. Behaviour: AD Attribute Editor (module
version; the supervisor owns the bump). Evidence re-read at `374718b3`,
`Services/ADAttributeEditorService.cs` PerformSave: Set-ADUser with `-ErrorAction Stop` (1437-1442);
`try { ps.Invoke(); } catch { the aud-a28c failure record; return }` (1449-1458); `ps.Commands.Clear()`
(1459); `if (ps.HadErrors)` taking the stream's first record as the write's error, recording it and
returning failed (1461-1466).

1. **The branch goes; the write is try/catch/finally**, the shape of aud-a2o's room-list write and
   GroupManagementService's member writes: `try { ps.Invoke(); } catch (Exception ex) { the failure
   record and result, unchanged (aud-a28c) } finally { ps.Commands.Clear(); ps.Streams.Error.Clear(); }`.
   The thrown error is the write's error. Why the branch could not fire on the write's own error: an
   error a command writes under `-ErrorAction Stop` is thrown out of Invoke and leaves no record in the
   stream (measured for aud-a2o, pwsh 7.6.6; pinned on this SDK by the harness test below). It could
   fire only on an error written past the preference - a nested command's own `-ErrorAction
   Continue`, which a compiled cmdlet such as Set-ADUser does not run (*assumption, not probed*) - or
   on a record an earlier command left (psl-2: HadErrors stays true while one remains); then a clean
   write was reported and audited as failed while the directory held the change, and a failed record
   offers no undo. With the branch gone `-ErrorAction Stop` on the write is load-bearing - a
   non-terminating refusal is seen only because it is thrown - so it is pinned (M4). The finally's
   error-stream clear has no reader today, the write being the session's last command; it keeps a
   command added after it from inheriting the write's record.
2. Comments after the mutation round.

Tests. Harness (`AttributeEditorHarness.cs`, `LocalAd`), both default off:
`SetReportsErrorWithoutThrowing` (a message) - the stand-in Set-ADUser applies the change, then writes
the message with its own `-ErrorAction Continue`, so the caller's `-ErrorAction Stop` does not throw
it; `SetFailureNonTerminating` - `SetFailure` is reported through `$PSCmdlet.WriteError` and nothing is
applied, a compiled cmdlet's non-terminating refusal, thrown only by the caller's `-ErrorAction Stop`.
ADAttributeEditorSaveTests: (a) an error the write reports without a throw is not its error - success,
the change applied once, one Success `ADAttributeEditor_Update` with its old/new values, one success
notice, the reported text nowhere in the result; (b) a refusal the write throws is the audited failure
(aud-a28c), terminating (`SetFailure`) and non-terminating under Stop (`SetFailureNonTerminating`)
alike - failed, the refusal's text in the error, nothing applied, one Failed `ADAttributeEditor_Update`
naming the change with neither refusal field (the write was tried), one failure notice. Harness
self-test: in a harness session under `-ErrorAction Stop`, Set-ADUser with the first knob does not
throw, sets HadErrors, leaves exactly one record carrying the message and applies the change; with the
second it throws and applies nothing. Conditions asserted, not wording.

Mutations at the call site (as aud-a28l): M3 the HadErrors branch restored (the lines as at
`374718b3`); extra M4 `-ErrorAction Stop` dropped from the write; extra H1 the harness's first knob
ignored - (a) then passes either way, and only the self-test stops it.

Files: `Services/ADAttributeEditorService.cs`, `ExchangeAdminWeb.Tests/AttributeEditorHarness.cs`,
`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`, this record.

## Fix

`Services/ADAttributeEditorService.cs`, PerformSave, the Set-ADUser write: `try { ps.Invoke(); } catch
(Exception ex) { the aud-a28c failure record and result, unchanged } finally { ps.Commands.Clear();
ps.Streams.Error.Clear(); }`. The `if (ps.HadErrors)` branch after it is removed, and with it the
comment's claim that failures "never reach the HadErrors branch below". The success record still
follows the write and is reached only when Invoke did not throw.

Harness (`ExchangeAdminWeb.Tests/AttributeEditorHarness.cs`, `LocalAd`), both default off:
`SetFailureNonTerminating` - with `SetFailure`, the stand-in Set-ADUser reports it through
`$PSCmdlet.WriteError` and applies nothing; `SetReportsErrorWithoutThrowing` - it applies the change,
then writes the message with its own `-ErrorAction Continue`. The terminating `SetFailure` is the same
error record as before, built once.

Tests (`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`, 4 cases):
`AnErrorTheWriteReportsWithoutThrowing_IsNotTakenForAFailedWrite`,
`AWriteTheDirectoryRefuses_IsTheAuditedFailure_ThrownEitherWay` (terminating; non-terminating under
Stop) and the knob self-test `TheHarness_SetADUser_ReportsAnErrorWithoutThrowing_AndRefusesWithoutTerminating`
(under Stop: the reported error is not thrown, HadErrors, one record, the change applied; the
non-terminating refusal is thrown and applies nothing; without Stop the same refusal is not thrown -
the knob is genuinely non-terminating, which is what lets M4 bite).

Behaviour: no outcome a real cmdlet is known to produce changes. A save whose write did not throw is
recorded and reported as a success even if an error record sits in the stream; one that threw is the
audited failure, as before.

## Guard proof

No separate pre-fix reproduction: M3 below is the write exactly as at `374718b3` (the fix reverted at
its call site), and it fails the one test that names the defect.

Same driver and conditions as aud-a28l (`a26g-mutations.log`), filter
`FullyQualifiedName~ADAttributeEditorSaveTests|FullyQualifiedName~ADAttributeEditorNotificationTests|FullyQualifiedName~ADAttributeEditorUndoRefusalTests|FullyQualifiedName~ReversedOperationsTests|FullyQualifiedName~AttributeEditorPageSaveAuditTests|FullyQualifiedName~ADAttributeEditorLookupTests|FullyQualifiedName~ADAttributeEditorPatternTests|FullyQualifiedName~EventLogUndoRefusalTests`
(73 tests). Every mutant built with 0 errors; each restore matched its original MD5. After the round, a
rebuild on the restored bytes and the filter: 73/73 green.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Named test failing |
|---|---|---|---|
| M3 the HadErrors branch restored - the write as at `374718b3` | `ADAttributeEditorService.cs`, `35D6C756F2A774CE2D86F18957B63FBC` | `F01CB09F6D7CC5AE41E088EA6BE6A337` | `AnErrorTheWriteReportsWithoutThrowing_IsNotTakenForAFailedWrite` (the applied write reported failed with the reported text) |
| extra M4 `-ErrorAction Stop` dropped from the write | `ADAttributeEditorService.cs`, `35D6C756F2A774CE2D86F18957B63FBC` | `E6B1DAD96CBD5A60867CBE61ED082156` | `AWriteTheDirectoryRefuses_IsTheAuditedFailure_ThrownEitherWay(nonTerminating: True)` - the refusal no longer thrown, the save reported and audited as a success with nothing written; the terminating case passes, as it must |
| extra H1 the harness's `SetReportsErrorWithoutThrowing` ignored (`if ($false -and ...)`) | `AttributeEditorHarness.cs`, `CCB3D018E42FFCCBF19314C33EBE7FEC` | `3FFB6B3656D6977A0C5365D2B634C534` | `TheHarness_SetADUser_ReportsAnErrorWithoutThrowing_AndRefusesWithoutTerminating` only - the M3 test still passes, having no error to see: the wrong-reason pass only the self-test stops |

M4's case was confirmed by a single re-run of that mutant with the theory's cases listed: True failed,
False passed; restored to `35D6C756F2A774CE2D86F18957B63FBC`. Unchanged through the round:
`ADAttributeEditorSaveTests.cs` `5549C6D9165D3805B936431B8486E33E`. The code comments are written after
the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings, all pre-existing, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is
  written): exit 0, 5,282 passed / 3 skipped (the three known AD skips) / 0 failed, 5,285 total,
  5m14s; nothing hung, no Sequence file. aud-a28l's tree: 5,278 / 3 / 0, 5,281 total; the
  four new cases are this record's.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. No coder self-review
verdict is claimed.

## Known gaps

- *Assumption, not probed* (no directory call is made here): the real Set-ADUser, a compiled cmdlet,
  writes no error that `-ErrorAction Stop` leaves unthrown. Were one ever reported, it is now left
  unread and the save recorded as a success; PerformSave has no read-back after the write to settle it,
  unlike the Conference Rooms write aud-a2o changed.
- The finally's error-stream clear has no reader today - the write is the session's last command - so
  no test can see it; it is there for a command added after the write.
- The re-read Get-ADUser before the write has no HadErrors check either and relies on `-ErrorAction
  Stop` the same way; unchanged here.

## Comments after the round

Written after the round, in `ee00d337`, with aud-a28l's and aud-a26i's; the same check as aud-a28l
(only comment and blank lines changed between `4a538d40` and `ee00d337`). This record's files:
`Services/ADAttributeEditorService.cs` `35D6C756F2A774CE2D86F18957B63FBC` -> `933676D6B9174FFDC8917AA04882D0C7`
(the write's why and the finally's; the file also carries aud-a28l's remark);
`ADAttributeEditorSaveTests.cs` `5549C6D9165D3805B936431B8486E33E` -> `06101AD9E4D8308C723EA064F6BF13C0`.

## Branch-tip gates - 2026-10-22 (`ee00d337`: all three fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 61 warnings, all pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is
  written): exit 0, 5,285 passed / 3 skipped (the three known AD skips) / 0 failed, 5,288 total,
  5m22s; nothing hung, no Sequence file. Baseline at `374718b3`: 5,273 / 3 / 0, 5,276 total; the
  twelve new cases are this branch's (aud-a28l five, aud-a26g four, aud-a26i three). The 316
  `ClickGate*` cases are among those passed.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
