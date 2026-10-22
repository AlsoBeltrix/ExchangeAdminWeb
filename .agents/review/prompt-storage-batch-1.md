# Defect review request: storage races under the write lock (aud-a33b, aud-a33c) and the pool borrow across Dispose (aud-a6e)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `5eb67528` or later.
- Storage: commits `82fc9b88` (aud-a33b) and `8c0f1721` (aud-a33c), forked from `1633ad02`, merged at
  `3ce560bd`. Review `git diff 1633ad02 8c0f1721`, and read the COMBINED
  `Services/Storage/SectionAccessRepository.cs`, `Services/SectionAccessSidMigration.cs`,
  `Services/Storage/SqliteConfigStore.cs`, `Services/Storage/ModuleConfigRepository.cs` on master.
- Pool: commit `fc95609e` (aud-a6e), direct to master. Review `git show fc95609e` and read
  `Services/ExoConnectionPool.cs` as it stands.
- You may read any file and run read-only git commands (`git show`, `git diff`, `git log`,
  `git grep`). One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/aud-a33b.md`, `aud-a33c.md`, `aud-a6e.md`; parents `aud-a33.md`,
  `aud-a6c.md`, `aud-a6d.md`; `docs/GraphSecretRemapScope-Plan.md`. Repo rules: `AGENTS.md`;
  `.agents/repo-guidance.md` Architectural Invariant 2 (one shared config database, dev and prod
  open the same file; additive-only migration) and "Known Failure Classes" (1, 2, 3);
  `docs/ProjectConstitution.md` authorization rules (section access is the authorization store;
  fail closed).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 3ce560bd`. Report whether
both succeeded.

## Mandate

Hunt for observable defects. a33b: two instances (dev and prod, separate processes on one
SQLite file) running the SID conversion together or while an admin edits section access -
any interleaving that restores a revoked grant, drops an added one, or converts a row whose
value changed; the `BEGIN IMMEDIATE` claim (is the lock really held before the callback's
first read; what happens on `SQLITE_BUSY` with the configured busy timeout; a transaction that
commits only the token bump); the `SkippedChangedRows` status (is a skip fail-closed for
authorization, and is it surfaced where an admin will see it); rows already holding a SID and
their display names. a33c: the unlocked fast path versus the locked decision - any path that
writes a stale old value over a newer destination, or resurrects a moved secret; the
delete-old-row ordering; the token bump on a no-op. a6e: the re-check placement after the slot
wait - a borrow that passes the re-check and then Dispose runs (is there still a window to
connect from a stopped pool); the slot release on refusal; `ObjectDisposedException` versus the
pool's `ConnectionFault` contract for callers; any remaining path that connects after
`DestroyAllPooled`. And tests that pass for the wrong reason (two in-process stores standing in
for two processes - what does that not model).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For
each: title; file and line; triggering input or state; predicted observable failure;
severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a better approach. No style
preferences, no out-of-scope work.
