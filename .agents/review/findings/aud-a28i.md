# aud-a28i: A concurrent allowlist save is overwritten, and the success audit records a stale before-state

**Severity**: MEDIUM - the editable-attribute allowlist is an authorization-adjacent store shared by
the dev and prod instances (Architectural Invariant 2). Two admins or two instances saving it at
nearly the same time: A reads the "before" rows, B saves a different allowlist, A's `SaveAllowlist`
then deletes and replaces the whole table in a later transaction. B's change disappears, and A's
success record is computed from the pre-B rows, so the Admin Event Log never shows who removed B's
attribute. Same class as aud-a33b / aud-a33c (decide under the write lock from current rows).
**Status**: Plan written; in progress
**Branch**: `worktree-agent-aa5d4735f85b48351`
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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted by codex attr-batch-3
(`a96b1a7c`) and assigned to this branch by the supervisor. One commit `fix(module-config): an
allowlist save decides inside its own write, and a save from a stale page is refused (aud-a28i)`,
after aud-a26f. Behaviour: the AD Attribute Editor service and repository (module version) and the
Module Configuration page (app-shared); the supervisor owns both bumps. Evidence re-read at
`22833c0d`: SaveAllowlistAndAudit 379-454 reads at 394 (`LoadAllowlistFromDisk`, its own read),
validates, and writes at 423 through SaveAllowlist 337-356 -> `AttributeEditorRepository.SaveAllowlist`
57-71, a separate write transaction. The page's LoadAllowlist (`ModuleConfig.razor` 1600-1620)
shows the cached GetAllowlist and keeps nothing to compare the store against.

**Chosen: both.** The lock alone makes the before-state honest but still lets a page loaded before
another admin's save replace it: the stale view is the page's, not the read's. A compare alone, made
outside the write, leaves a window between the compare and the write that the other instance can
land in. So the save decides inside one write, and compares against what the page loaded.

1. **One write.** New `AttributeEditorRepository.ReplaceAllowlist(decide)`: inside one `_store.Write`
   (`BEGIN IMMEDIATE`, aud-a33b) it reads the rows and the presence marker, hands them to `decide`,
   and either replaces the table (delete, insert, mark present) with the rows `decide` returns or,
   when `decide` returns null, throws a private exception so the transaction rolls back - a refusal
   writes nothing, not even the change-token bump (IConfigStore.Write's documented rollback). It
   returns the rows as read and, after a replace, as read back. SaveAllowlistAndAudit makes every
   decision inside `decide` - corrupt store, concurrent change, then aud-a26f's blank and repeated
   rows, the denylist and the pattern check, in that order - and the success record's before-state
   is the rows the write replaced. Records stay outside the transaction, one per attempt, as
   aud-a28f wrote them.
2. **Compare.** The version is a SHA-256 over the stored rows (every column, in name order) and the
   presence marker - the allowlist's own content, not the store's change token, which moves on every
   config write by either instance (a section-access save would make every open allowlist page
   refuse) and misses a writer that bypasses the store (aud-a33b's reasons). New
   `LoadAllowlistForEditing()` returns the validated list and the version of the same fresh read; the
   page keeps the version and passes it to SaveAllowlistAndAudit, which refuses a save whose version
   no longer matches the rows under the lock as `ConcurrentChange` (`mutationAttempted: false`): the
   allowlist changed since the page loaded it; reload and re-apply. Nothing merged or guessed. A
   successful save returns the new version and the page keeps it, so a second save from the same
   page is not refused.
3. **Registries.** ClickGateRegistry: `ModuleConfig.razor` NotYetConverted; nothing re-anchors. No
   handler added.

Tests. New `ExchangeAdminWeb.Tests/AllowlistConcurrentSaveTests.cs`, aud-a33b style: "the other
instance" is a second ADAttributeEditorService over a second store on the same temp database file.
A page loaded before B saved -> A's save refused `ConcurrentChange`, one record, B's row kept, A's row
not written; B's save landing inside A's save, before A's write (a hook on A's store runs B's save as
A's write opens) -> the same; a page that reloads after B's save -> A's save goes through and its
record's before-state holds B's row (what the write replaced); two saves from one page in a row ->
both go through. Existing allowlist tests pass the version their load returned. Mutations at the
call site: the read taken outside the write again (the snapshot read before `_store.Write` opens);
extra - the version compare deleted.

## Guard proof

Pending.
