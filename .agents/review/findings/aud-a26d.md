# aud-a26d: A no-op save sends an administrator notice but writes no audit row

**Severity**: LOW - nothing is mutated, but administrators receive an `ADAttributeEditor_Update`
notice that no audit event can be reconciled to.
**Status**: Merged to master (`20ef8984`); closed on guard proof; codex review pending
**Branch**: `worktree-agent-a4f347c5f99ae060f`
**Commit**: `511df4f8` (merged `20ef8984`)
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-1, `prompt-attr-batch-1.md`, 2026-10-22)

## Evidence

At `8586b047`, `Services/ADAttributeEditorService.cs`, `PerformSave`:

```
929            var changes = ComputeChanges(proposedValues, currentValues, allowlist);
930            if (changes.Count == 0)
931                return new(true, "No changes to apply.", changes);
```

That return is before `LogAudit`. Since aud-a26b, `SaveAsync` announces whatever it returns, so a
no-op save sends the administrators an update notice whose `Changed` row reads "(none - the
directory already held the requested values)" while the Admin Event Log has no row for it.

## Approach (to be written by the implementing agent before code)

Pick one and say why in the record: write a successful no-op audit row (empty
`changedAttributes`, `mutationAttempted: false`) so the notice reconciles, or stop announcing
no-op saves. The recommended choice is the audit row: the Constitution's rule is that every
attempt leaves a durable record, and the aud-a26b design is that every outcome is announced
once; both stay true. Tests through `AttributeEditorHarness`: a no-op save leaves exactly one
audit row and one notice, and the row and the notice name the same operation. Mutation: remove
the no-op audit call.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted by codex attr-batch-1. One commit
`fix(ad-attribute-editor): a save that finds nothing to change is audited, so its notice has a row
(aud-a26d)`, after aud-a28e on the same branch. Module behaviour: AD Attribute Editor (the
supervisor owns the version bump). Evidence re-read at `a64416fa`: PerformSave 929-931, as quoted.

**Choice: the audit row.** Every attempt leaves a durable record (Constitution, Auditing) and
SaveAsync announces every result it returns, once (aud-a26b); the row keeps both true. Not
announcing no-ops would bring back a special case in the dispatch aud-a26b made unconditional, and
would still leave the attempt itself unrecorded.

Scope: the `changes.Count == 0` branch writes one record through `AuditService.LogADAttributeEdit`
before it returns - `ADAttributeEditor_Update`, Success, empty `changedAttributes`, no old_/new_
pairs, `mutationAttempted: false` (aud-a28's field: nothing was sent to the directory) and a
`detail` saying no change was needed because the directory already held the requested values. It
carries the target GUID and the servicer note the success record carries. A small helper of its
own rather than LogAudit, so the operation trace does not log `AttributeWriteCompleted` for a write
that never happened. The returned result is unchanged. Undo: `CanUndo` needs an old_/new_ pair, so
the row offers no Undo button; an undo whose save finds nothing left to change gets the same two
rows a real undo gets (this one and the undo record).

Tests, in `ADAttributeEditorSaveTests`, through `AttributeEditorHarness`: a no-op save -> nothing
written, exactly one audit record and exactly one notice, the two naming the same operation (action,
target, ticket, success), the record saying no mutation was attempted; a real change made after it
still leaves the normal row (old_/new_ pairs, no `mutationAttempted`). Mutation: the no-op audit
call deleted.

## Executed reproduction - 2026-10-22

Branch `worktree-agent-a4f347c5f99ae060f` after the plan commit (`6b74d40d`); service MD5
`23748328529DBAE43BE81DA5B6599649`, the same run as aud-a28e's (`reproduction.log` in the session
scratchpad's `a28e-a26d-work`). `ASaveThatFindsNothingToChange_IsAudited_AndItsNoticeMatchesTheRow`
failed `Assert.Single() Failure: The collection was empty` - the notice was sent, and the audit log
held nothing; the control `ARealChange_StillLeavesTheNormalRow` passed. (The case's one later line,
that the row carries a `detail`, sits below the assertion that failed.)

## Fix

`Services/ADAttributeEditorService.cs`, PerformSave: the `changes.Count == 0` branch calls a new
`LogNoChangeAudit` before its unchanged return. That writes `AuditService.LogADAttributeEdit` -
`ADAttributeEditor_Update`, Success, no changes - with `mutationAttempted: false`, a `detail` ("No
change was needed: the directory already held the requested values, so nothing was written."), the
target GUID, and the servicer note when one applies. SaveAsync's own remark already listed the no-op
among the results "audited by SaveAndAuditAsync"; it now is. Tests: two cases appended to
`ExchangeAdminWeb.Tests/ADAttributeEditorSaveTests.cs`.

Caller-visible change, by design: a save that finds nothing to change now adds one Success row to
the Admin Event Log (no Undo button: `CanUndo` needs an old_/new_ pair). An undo whose save finds
nothing left to change now also leaves that row beside its undo record, as a real undo leaves its
update row.

## Guard proof

Same session-local driver as aud-a28e (`mutate.ps1`), filter
`FullyQualifiedName~ADAttributeEditorSaveTests|FullyQualifiedName~ADAttributeEditorNotificationTests`,
on top of aud-a28e's commit (`5d2a27ba`). Fixed-file MD5 `9AD84960A7B447378A5DF16F8254BB9D`, restored
to it after each; every mutant built with 0 errors. After the round a rebuild and the attribute-editor
filter: 144/144 green.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| the no-op audit call (`LogNoChangeAudit`) deleted | `B22DB96D515BF9E3D04C4B600D92F747` | `ASaveThatFindsNothingToChange_IsAudited_AndItsNoticeMatchesTheRow` (`Assert.Single`: no record) |
| extra: `mutationAttempted` dropped from the no-op record | `5E8A1763830577056A37C9CCDEC1590C` | `ASaveThatFindsNothingToChange_IsAudited_AndItsNoticeMatchesTheRow` (`KeyNotFoundException`) |

The code comments are written after the round, in their own commit, so the MD5s above are this
commit's bytes.

## Gates - 2026-10-22

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, 59 warnings (incremental), all
  pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`, on top of aud-a28e:
  exit 0, 5,051 passed / 3 skipped / 0 failed, 5,054 total, 5m11s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, page, AD module, directory, SMTP, push or deployment. No coder self-review
verdict is claimed.

## Known gaps

- The undo case above (a save that finds nothing left to change) is reachable only when the
  directory changes between the undo's own lookup and the save's re-read; it was reasoned from the
  code, not driven.
- No directory or SMTP was exercised; the stand-in cmdlets and the substituted EmailService are the
  evidence.
