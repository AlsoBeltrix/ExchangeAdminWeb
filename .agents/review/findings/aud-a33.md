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

**That is not harmless, because not every step is idempotent.** Of the six steps, five consist
only of `CREATE TABLE IF NOT EXISTS` statements - fourteen of them in total - and survive a
repeat. The sixth does not:

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
interleaving, and the idempotent steps absorb most of what they do produce.

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

## Independent review, 2026-10-19 (codex-commercial / gpt-6-astra / max) - CHANGES REQUIRED

The in-lock re-read was confirmed to close the race, across processes and not merely threads,
and `PRAGMA user_version` was confirmed transactional - the reviewer checked SQLite's own
`pragma.c` rather than taking it on trust. Five findings otherwise. Two were mine and are
repaired; two are pre-existing and are split out; one is a correction to this record.

### 1. MEDIUM, introduced - a no-op migration now took a write lock - FIXED

I removed the `current == TargetVersion` fast return, so the overwhelmingly common case -
nothing to do - reached `BeginTransaction` and only then discovered it. A competing writer
could delay it or fail it, and that exception leaves `Program.cs` startup.

**The mutation run makes the cost concrete: with the fast path removed, the no-op migration
took 34 seconds against a held write lock instead of 0.6.** That is the provider's 30-second
command timeout, not the 5-second `busy_timeout` I would have assumed.

### 2. MEDIUM - the loop's exit bypassed the newer-schema check - FIXED

Compatibility was validated only against the unlocked read at the top. A database that became
newer *while this instance was migrating* left the loop with no check at all, so a version
this build cannot serve was rejected on one path and accepted on the other. The check is now
its own method and both paths reach it.

### 3. LOW, and material - two factual errors in this record - CORRECTED

- **"DEFERRED to IMMEDIATE" was wrong.** Microsoft.Data.Sqlite 10.0.7's parameterless
  `BeginTransaction()` already issues `BEGIN IMMEDIATE`. The transaction was never deferred,
  so **the locked re-read was the entire fix** and this record claimed a second mechanism that
  did not exist. `deferred: false` is kept as an explicit statement of the requirement, and
  the comment now says so.
- **The step count was wrong.** There are six migration steps containing fourteen
  `CREATE TABLE` statements; the `ALTER TABLE` is step six, not "step fourteen".

### 4 and 5. Pre-existing, split out

`aud-a33b` - `SectionAccessSidMigration` reads grants, resolves names, then replaces the table
from that earlier snapshot without revalidating under the write lock, so a grant **revoked
through the other instance can be restored**. The reviewer rates it HIGH and it is an
authorization defect, not a migration inconvenience.

`aud-a33c` - `ModuleConfigRepository.RemapKey` decides whether to copy before opening its
write transaction, then applies an unconditional conflict update, so it can overwrite a value
the other instance saved meanwhile.

Both predate this commit; filed rather than absorbed.

### Repair guard proof

| Test | What it pins |
| --- | --- |
| `Migrate_OnAnAlreadyCurrentDatabase_DoesNotWaitForAWriter` | the fast path, with another connection holding the write lock |
| `Migrate_WhenAnotherInstanceMakesTheSchemaNewerMidRun_StillChecksCompatibility` | the loop's exit validates too |

Both mutations - removing the fast path, removing the in-loop check - fail their own test and
nothing else. Source restored byte-identical (md5 verified), touched, rebuilt.

### Carried, not fixed

- `JobStoreMigrator` has the same stale-counter shape. Its single step is idempotent and its
  database is per-instance, so there is no equivalent failure today; the reviewer flags it as
  needing correction before any non-idempotent step is added there. Noted here rather than
  given its own finding, because there is nothing currently wrong.
- The seam test proves the stale-counter case but not contention handling, cross-process
  behaviour, or partial-step rollback. It would also pass with the re-read moved outside the
  lock, since the competing migration has already finished by then.
- Sustained contention can still throw out of startup; the locking prevents two corrected
  migrators running the same step, not every possible busy failure.
