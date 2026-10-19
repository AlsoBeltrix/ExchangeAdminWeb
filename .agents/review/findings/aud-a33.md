# aud-a33: two instances starting together can abort one of them on a repeated migration step

**Severity**: HIGH - the failure mode is an instance that will not start. It lands at deploy
time, when both instances are most likely to restart close together.
**Status**: Verified and fixed; review pending
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 33 of the 2026-10-16 audit, verified against the code.

`Services/Storage/ConfigStoreMigrator.Migrate` read the schema version with **no lock held**:

```csharp
var current = GetUserVersion(connection);
...
for (var version = current; version < Migrations.Length; version++)
{
    using var transaction = connection.BeginTransaction();   // DEFERRED
```

`BeginTransaction()` is deferred, so it takes only a read lock until its first write. Dev and
prod open the **same database file** (`.agents/decisions.md` 2026-09-04), so both instances can
read the same version, both enter the same step, and the loser's statement runs against a
schema that already has the change.

**That is not harmless, because not every step is idempotent.** Fourteen are
`CREATE TABLE IF NOT EXISTS`, which would survive a repeat. One is not:

```sql
ALTER TABLE section_access ADD COLUMN group_display_name TEXT;
```

SQLite rejects a duplicate column, the exception leaves `Migrate`, and that instance does not
start.

## Predicted observable failure

A deploy restarts both instances within the same second. One comes up; the other throws
`SQLite Error 1: 'duplicate column name: group_display_name'` during startup and stays down,
with nothing wrong with the database it was reading.

## Approach

Two changes, and both are needed.

1. **The transaction is IMMEDIATE** (`BeginTransaction(deferred: false)`), so the write lock is
   taken when the transaction opens rather than at its first write. The instances serialise
   instead of overlapping.
2. **The version is re-read inside that lock.** Serialising alone is not enough - the loser
   still holds the stale number it read outside. Re-reading is what makes it see the winner's
   work and skip it. The loop is now `while (true)` driven by that in-lock read.

The unlocked read at the top stays, as the fast path for the usual case where there is nothing
to do.

## Files changed

- `Services/Storage/ConfigStoreMigrator.cs`
- `ExchangeAdminWeb.Tests/ConfigStoreMigratorTests.cs`

## Guard proof

**The first attempt at a guard did not work, and that is worth recording.** A test running four
migrators concurrently against a fresh database passed against the *mutated* code as well -
so it proved nothing. From a fresh database the threads do not reliably produce the breaking
interleaving, and the fourteen idempotent steps absorb most of what they do produce.

The race needs one instance to finish **between** another's unlocked read and its loop, and
nothing else opens that window deterministically. So the migrator carries an
`AfterVersionRead` test seam - internal, null in production - and the guard drives exactly
that sequence: read the version, let another migrator complete the whole migration, then
continue with the stale number.

| Test | What it pins |
| --- | --- |
| `Migrate_ResumesWhenAnotherInstanceAdvancedTheVersionFirst` | the actual race, deterministically |
| `Migrate_FromTwoInstancesAtOnce_DoesNotAbortEitherStartup` | four real concurrent migrations all reach the target |

The second is kept for what it is - a smoke test that concurrent startup works - and **not**
counted as proof of the fix, because it demonstrably does not discriminate.

| Mutation | Result |
| --- | --- |
| use the stale `current++` instead of the in-lock re-read | FAIL - `SQLite Error 1: 'duplicate column name: group_display_name'` |

That is the predicted failure verbatim, not an assertion message standing in for it. Source
restored byte-identical (md5 verified), touched, rebuilt.

Gate: Release 0 errors; suite 4333 passed / 3 known AD skips / 0 failed; format and
`git diff --check` clean. Base app version 2.28.0 -> 2.28.1 (shared storage infrastructure).

## Known gaps

- `IMMEDIATE` serialises writers within SQLite's locking, but nothing here was tested against
  two **processes** on two hosts sharing the file over a network path. The deployment has both
  instances on one host, so this is the shape that matters, but it is untested at that level.
- The busy timeout is whatever `SqliteConnectionFactory` configures; a long migration could in
  principle make a second instance wait past it. Not exercised.
- Nothing here ran against the real shared database.

## Closeout

Fixed directly on `master`. Independent review pending.
