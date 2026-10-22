# aud-a28d: an undo refused before its save - a protected target included - leaves no audit record

**Severity**: MEDIUM (proposed). Same class as `aud-a28`, in the service aud-a28 did not sweep,
and it includes the case the Constitution names: a protected-principal denial that is not audited.
**Status**: Merged to master (`5e55e6aa`); codex review pending
**Branch**: `worktree-agent-acad2a289bd002e11`
**Commit**: `db140a29` (merged `5e55e6aa`)
**Reviewer**: implementer (found while fixing aud-a26b, 2026-10-21)
**Evidence head**: `ca6575a21e48596848ca9521d4683424f10cf577`

## Evidence

`ADAttributeEditorUndoService.ExecuteUndoAsync` (Services/ADAttributeEditorUndoService.cs at the
evidence head) has eight refusal returns before it calls SaveAsync, and none writes an audit
record:

| Line | Refusal |
|---|---|
| 166-167 | no target identity in the record |
| 169-170 | module not enabled |
| 172-174 | no changed attributes in the record |
| 189-194 | the target cannot be looked up |
| 196-201 | the target now resolves to a different object (identity proof, A9) |
| 205-209 | the protected-principal check failed |
| 221-225 | the target is protected and the operator holds no servicer grant |
| 237-250 | an attribute changed since the original edit (conflict) |

The five inside the operation scope complete the operation TRACE as failed; the trace is
diagnostic, with different retention and readers. Its caller, `AdminEventLog.razor`, shows
`result.Error` and audits nothing. So "who tried to reverse a change to this protected account,
and was stopped?" - asked of the audit log - has no answer, which is the aud-a28 finding for
SaveAsync and Emergency Disable, here. The Constitution's protected-principal rule says the
operation "must refuse, fail closed, and audit the denial"; the undo refuses and fails closed.

Only the save-failure and exception paths write the undo record (`LogUndoAudit`), and only
SaveAsync's own refusals are recorded on the way (as `ADAttributeEdit_Refused`, aud-a28).

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"; further defects in this module fixed as
their own commit and record). One commit `fix(ad-attribute-editor): ... (aud-a28d)`. Module
behaviour: AD Attribute Editor (the supervisor owns the version bump).

Scope: ExecuteUndoAsync's eight refusal returns each write the undo record first - the existing
`LogUndoAudit`, failed, with the refusal reason as the error - carrying `refusedAt` (the step)
and `mutationAttempted: false`, aud-a28's two fields, so a refused reversal is distinguishable
from one that reached the directory and failed. No refusal message, return value or control flow
changes. Audit only, as aud-a28 was: these refusals are not announced, which is unchanged
(recorded below as a known gap, not silently decided).

Proof: reproduction first through `AttributeEditorHarness`, each refusal driven for real and its
record read back; the save-failure record keeps no `refusedAt`. Mutation: one refusal's audit call
removed (the protected-target one); and the `mutationAttempted` flag dropped.

## Executed reproduction - 2026-10-21

Branch `worktree-agent-acad2a289bd002e11` after aud-a28c (`df6a378d`); undo service MD5
`8F7D2577FA4F2CD01E3F619439E84378` (as aud-a26b left it). New
`ExchangeAdminWeb.Tests/ADAttributeEditorUndoRefusalTests.cs`, each refusal driven end to end
through the harness: 8 failed / 1 passed, 0 compiler errors (`acad2a-a28d-reproduction.log` in the
session scratchpad). Every refusal failed the same way - `Assert.Single() Failure: The collection
was empty`: the audit log held nothing at all, the protected-target denial included. The control
(a reversal that reached the directory and failed, recorded without the refusal fields) passed.

## Fix

`Services/ADAttributeEditorUndoService.cs`: each of the eight refusal returns calls the existing
`LogUndoAudit` first - failed, the refusal message as the error, no serviced note - with a new
optional `refusedAt` (`AuditRecord`, `ModuleEnablement`, `Lookup`, `IdentityCheck`,
`ProtectedPrincipalCheck`, `ConflictCheck`) that adds `refusedAt` and `mutationAttempted: false`.
The record without a target names `(none)`; the two refusals that run before the changed
attributes are read record an empty attribute list. Messages, return values, trace completions
and control flow are unchanged. Tests: the new file (9 cases); harness:
`CreateUndoService(moduleEnabled)`.

**Sequenced after aud-a26c.** Every refusal record names the operation it tried to reverse
(`originalOperationId`), and the Admin Event Log used to mark any operation so named as
"Reversed" and hide its Undo button. Found before this commit, while it was still uncommitted
and already suite-green (4,533 / 3 / 0): landing it first would have made every refused undo -
a protected target's included - mark its edit "Reversed". The change was set aside (copies in the
session scratchpad, no stash, no history rewrite), aud-a26c landed (`9cfd996c`), and this change
was restored byte-identical on top of it (service MD5 `2888ED19FB043C813341A71DFFC2371A`). The
protected-target test now also asserts that its refusal record does not mark the edit reversed.

## Guard proof

Same session-local driver as aud-a26b, filter
`FullyQualifiedName~ADAttributeEditorUndoRefusalTests`, fixed-file MD5
`2888ED19FB043C813341A71DFFC2371A`, restored to it after each; after the round a rebuild and the
filter, 9/9 green. Every mutant built with 0 errors. Run twice - before the change was set aside,
and again on the final bytes on top of aud-a26c - with identical results.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| the protected denial's `LogUndoAudit` deleted | `4C01DBCD65606B8FF9837B7A47BE5649` | `AProtectedTarget_IsRefused_AndTheDenialIsAudited` |
| `mutationAttempted` dropped | `640447CB6BB1563B9EDD846F5BED67BA` | `AProtectedTarget_IsRefused_AndTheDenialIsAudited` (all 8 refusals) |
| the conflict refusal's `LogUndoAudit` deleted | `AE5900CDAFAF7DD2C5DA64A92188B4A8` | `AValueChangedSinceTheEdit_IsRefused_AndAudited` |
| the identity refusal's `LogUndoAudit` deleted | `EBA721E4EF9AB8950EE2A3012FA7BFE1` | `ATargetThatNowResolvesElsewhere_IsRefused_AndAudited` |
| the refusal fields never written (`false && refusedAt is not null`) | `FEE5756DD6984DE9E5C3B135CF7FCF18` | `ARecordWithNoTarget_IsRefused_AndAudited` (all 8 refusals) |
| the refusal fields written on every record (`\|\| true`) | `35335A6A9BC8CC70EA8B960B00EA071B` | `AReversalThatReachedTheDirectoryAndFailed_IsNotMarkedAsARefusal` |

## Gates - 2026-10-22

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 40 warnings (incremental), all
  pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`, on top of aud-a26c:
  exit 0, 4,542 passed / 3 skipped / 0 failed, 4,545 total, 5m35s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the new files staged: exit 0.

No PowerShell source, AD module, directory, push or deployment. No coder self-review verdict is
claimed; the record was opened by the implementer.

## Known gaps

- The refusals are audited, not announced. That matches aud-a28 (which added audit only), but it
  is now asymmetric with the forward path, where a26b's SaveAsync announces every refusal because
  the page always did. Whether a refused UNDO should notify administrators - a protected-target
  attempt especially - is an owner decision; not made here.
- The lookup, identity and protection refusals were driven through the harness's stand-ins and a
  fake protection decision; no directory was involved.
