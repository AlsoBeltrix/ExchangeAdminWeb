# aud-a28i: A concurrent allowlist save is overwritten, and the success audit records a stale before-state

**Severity**: MEDIUM - the editable-attribute allowlist is an authorization-adjacent store shared by
the dev and prod instances (Architectural Invariant 2). Two admins or two instances saving it at
nearly the same time: A reads the "before" rows, B saves a different allowlist, A's `SaveAllowlist`
then deletes and replaces the whole table in a later transaction. B's change disappears, and A's
success record is computed from the pre-B rows, so the Admin Event Log never shows who removed B's
attribute. Same class as aud-a33b / aud-a33c (decide under the write lock from current rows).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-3, `prompt-attr-batch-3.md`, 2026-10-22)

## Evidence

At `488e535e`: `Services/ADAttributeEditorService.cs` around lines 394 and 423 -
`SaveAllowlandAudit` takes its fresh read (`LoadAllowlistFromDisk`) outside any write transaction,
validates, then calls `SaveAllowlist`; `Services/Storage/AttributeEditorRepository.cs` around line
57 - the save deletes and re-inserts the table inside its own later `Write`. The read and the write
are two transactions; nothing checks that the rows are still as read.

## Approach (to be written by the implementing agent before code)

- Decide inside one store write: move the read-validate-replace into a single repository callback
  under `SqliteConfigStore.Write` (`BEGIN IMMEDIATE`, as aud-a33b's `ConvertToSids` does), so the
  before-state is read under the lock the replace holds. The validation refusals (corrupt store,
  denylist, pattern, blank/duplicate rows per aud-a26f) must still be audited as refusals before any
  write; the simplest shape is a callback that reads and validates under the lock and returns a
  refusal reason without writing.
- Or, if the page can carry it, compare-and-swap on the store's change token: the page captures the
  token when it loaded the rows; the save refuses (audited `refusedAt: ConcurrentChange`,
  `mutationAttempted: false`) when the token moved, telling the admin to reload and re-apply - the
  onus is on the admin; nothing is merged or guessed. Say in the record which was chosen and why;
  the lock shape alone does not protect an admin whose page view is stale, the token alone does not
  protect two instances. Both may be right.
- Tests in the aud-a33b style: two stores on one temp file; a second save landing between A's read
  and A's write; A's write does not drop B's row (or is refused and audited as a conflict); the
  success record's before-state matches what was actually replaced. Mutation at the call site: take
  the read outside the write again.

## Guard proof

Pending.
