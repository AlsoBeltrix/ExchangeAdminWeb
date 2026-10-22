# aud-a28c: an attribute save whose re-read throws leaves no audit record

**Severity**: MEDIUM (proposed). Same class as `aud-a28` - an attempt with no durable audit - on
the path aud-a28 did not reach: a failure that throws instead of returning.
**Status**: Merged to master (`5e55e6aa`); closed on guard proof; codex attr-batch-1 clean
**Branch**: `worktree-agent-acad2a289bd002e11`
**Commit**: `df6a378d` (merged `5e55e6aa`)
**Reviewer**: implementer (found while fixing aud-a26b/aud-a28b, 2026-10-21)
**Evidence head**: `ca6575a21e48596848ca9521d4683424f10cf577`

## Evidence

`ADAttributeEditorService.PerformSave` opens a session, imports the AD module and re-reads the
target by DN, all before the write, and none of it inside a try:

```csharp
ps.AddCommand("Import-Module").AddParameter("Name", "ActiveDirectory").AddParameter("ErrorAction", "Stop");
ps.Invoke();
...
ps.AddCommand("Get-ADUser").AddParameter("Identity", target.DistinguishedName) ... .AddParameter("ErrorAction", "Stop");
var reReadResult = ps.Invoke();
```

(740-759 at the evidence head.) Under `-ErrorAction Stop` every failure there THROWS: the module
missing on the host, a domain controller that cannot be reached, access denied, and - the
ordinary one - `Get-ADUser -Identity <DN>` for an object that has been moved or deleted since the
lookup, which is not-found and therefore an exception. The exception leaves PerformSave, SaveAsync
and Task.Run unaudited. The branch written for exactly that case,
`if (reReadResult.Count == 0) { ... LogAudit(... "Target object no longer exists or could not be
re-read.") }` (761-766), is unreachable for it: `-Identity` never returns an empty result for a
missing object, it throws.

What callers did with the exception: the page's outer catch showed the message and sent a
failure notice, and wrote no audit record; the undo's catch wrote its own undo record. So a
forward edit of an account that had just been moved - a routine event - left nothing in the audit
log. The write was never attempted, so nothing was mis-mutated; what is missing is the record
that someone tried, which is the aud-a28 failure exactly.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"; further defects in this module fixed as
their own commit and record). One commit `fix(ad-attribute-editor): ... (aud-a28c)`. Module
behaviour: AD Attribute Editor (the supervisor owns the version bump).

Scope: PerformSave's pre-write section - session open, module import, credential, re-read - goes
inside one try. Its catch writes the failed `ADAttributeEditor_Update` record the existing
pre-write failures already write (no changes; the reason as the error) and returns a failed
result, the established shape of the Set-ADUser catch below it ("catch, audit, and return a
failed result"). Because aud-a26b made SaveAsync announce every result it returns, the failure is
now also announced by SaveAsync; the callers' catches no longer see these exceptions, and their
other handling is unchanged. The write, its catch, the GUID and boundary checks and the
`Count == 0` branch are untouched.

Proof: reproduction first through `AttributeEditorHarness` - a principal whose DN no longer
resolves, and an Import-Module stand-in that fails - asserting a failed result, one failed audit
record carrying the reason, one failure notice and no write. Mutation: the new catch made to
rethrow (the exception escapes again).

## Executed reproduction - 2026-10-21

Branch `worktree-agent-acad2a289bd002e11` after psl-6 (`b7ef9d39`); service MD5
`0FC516343C93A12955E68B638C115AB7`. Two new cases in `ADAttributeEditorSaveTests`, result 2 failed
/ 10 passed, 0 compiler errors (`acad2a-a28c-reproduction.log` in the session scratchpad). Both
failed by the exception leaving SaveAsync - no result, so no record:

- a principal whose DN (`CN=Alex Morgan,OU=Leavers,...`) no longer resolves:
  `CmdletInvocationException : Cannot find an object with identity: 'CN=Alex Morgan,OU=Leavers,DC=example,DC=test'`;
- an Import-Module that fails: `CmdletInvocationException : The specified module 'ActiveDirectory'
  was not loaded because no valid module file was found in any module directory.`

## Fix

`Services/ADAttributeEditorService.cs`, PerformSave: session open, module import, credential and
re-read inside one try; the catch writes `LogAudit(target, [], ..., false, "Target object could
not be re-read before the write: <reason>")` and returns that failed result. The write, its own
catch, the `Count == 0`, GUID and boundary checks are unchanged. Tests: two cases appended to
`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`; harness: `LocalAd.ImportFailure`.

Caller-visible change, by design: the page now shows the reason through its failure branch (its
catch no longer sees this exception), and an undo whose save hits it reports `saveResult.Error`
through its save-failure branch (undo record written, as before) instead of "Unexpected error
during undo: ...". The notice now comes from SaveAsync with an Error row.

## Guard proof

Same session-local driver as aud-a26b, filter
`FullyQualifiedName~ADAttributeEditorSaveTests|FullyQualifiedName~ADAttributeEditorNotificationTests`,
fixed-file MD5 `23748328529DBAE43BE81DA5B6599649`, restored to it after each; after the round a
rebuild and the filter, 21/21 green. Every mutant built with 0 errors.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| the new catch disabled (`when (ex is null)`: the exception escapes again) | `BFE983D625C166EF6AEBB7CE2D29C02F` | `ATargetMovedSinceTheLookup_FailsAudited_InsteadOfThrowing` (both new cases) |
| the catch's `LogAudit` deleted | `E6D8956FD9C39149DBE27B98E0993C59` | `AHostWithoutTheDirectoryModule_FailsAudited_InsteadOfThrowing` (both new cases) |

## Gates - 2026-10-21

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 40 warnings (incremental), all
  pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: exit 0, 4,524
  passed / 3 skipped / 0 failed, 4,527 total, 5m14s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. No coder
self-review verdict is claimed; the record was opened by the implementer, so independent review
is the supervisor's call.

## Known gaps

- Exceptions raised in SaveAndAuditAsync before PerformSave (the protection check, the credential
  fetch) would still escape unaudited. Assumption, from a partial read only: both are written to
  return a failure result on their error paths rather than throw. Not verified; not reached by
  this record.
- An undo whose target moves between its own lookup and the save's re-read was not driven.
