# aud-a33c: the Graph key remap can overwrite a value the other instance just saved

**Severity**: MEDIUM - a configuration change made on one instance can be silently reverted by
the other starting up. Pre-existing.
**Status**: Fixed on branch; awaiting merge
**Branch**: `worktree-agent-a801aa638651e79ad`
**Commit**: `<pending>`

## Evidence

Raised by codex-commercial (gpt-6-astra, max) reviewing `4327d1c`, as finding 4, and labelled
pre-existing.

`Services/Storage/ModuleConfigRepository.RemapKey` reads the source and destination values and
decides whether to copy **before** opening its write transaction (`:130`), then applies an
unconditional upsert (`:149`). Between the decision and the write, the other instance - which
shares this database - can save a new destination value, and the upsert overwrites it with the
older one.

Same read-then-write shape as `aud-a33` and `aud-a33b`.

## Predicted observable failure

An administrator saves a Graph setting on prod. Dev restarts, its remap decides from a
snapshot taken before that save, and the setting reverts. The audit shows the administrator's
save succeeded, because it did.

## Approach (as filed)

Re-read and re-decide inside the write transaction, and make the upsert conditional on the
destination still being what the decision was based on. The remap is a one-time key rename, so
skipping when the destination already holds a newer value is the correct outcome, not a
failure.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21, relayed by the supervising session: "fix all the bugs ... EVERYTHING."

**Mechanism.** Decide inside the write transaction, from rows read through it.

- `ModuleConfigRepository.RemapKey` keeps its unlocked read only as a fast path: no row under
  the old key -> return false without opening a write transaction. That is every module on
  every start once the rename is repaired; `Migrate_IsIdempotent_SecondRunIsNoOp` already pins
  that it bumps no token.
- Otherwise, inside ONE store write transaction (`BEGIN IMMEDIATE`, pinned by aud-a33b's
  `Write_HoldsTheWriteLockBeforeTheCallbackReads`): re-read the module's rows through the
  transaction and decide from them. Old row gone -> return false (another writer moved or
  removed it). Old value usable and destination empty -> copy the CURRENT old value. Then
  delete the old row (unchanged rule, `docs/GraphSecretRemapScope-Plan.md`). Nothing from the
  unlocked read reaches the write.
- "Upsert conditional on the destination still being what the decision was based on" holds by
  construction: the decision and the write share one lock, so no writer can change the
  destination between them. No second, SQL-level condition: it would mask the in-transaction
  decision under mutation, so neither guard could be proven alone.
- `ReadModule` gains an optional transaction, named on the read inside the write as every
  command in these repositories names its transaction. **Corrected as built:** this bullet
  first gave the reason as "this provider refuses a command without one on a connection with
  a pending transaction". That was wrong - with the assignment removed, all ten
  `GraphSecretKeyMigrationTests` still pass. It is convention, and the code comment says so.
- Raced no-op (old row gone by the time the lock is held): returns false; the transaction
  carries only the token bump every store write carries, as in aud-a33b.

**Files.** `Services/Storage/ModuleConfigRepository.cs`;
`ExchangeAdminWeb.Tests/GraphSecretKeyMigrationTests.cs`.

**Tests.** Real SQLite temp database, through the startup entry point `MigrateGraphSecretKeys`.
The window between the unlocked decision and the write is opened by a test decorator over
`IConfigStore` that runs "the other instance" - a second store over its own connection factory
on the same file - immediately before handing the write to the real store. No production seam.

- `Migrate_DoesNotRevertAGraphSecretSavedMeanwhile_WhenTheStaleRowIsStillThere`: the other
  instance's config page saves the new key and, because the page saves every key it loaded,
  carries the stranded old row along. The saved value must stand; the old row goes.
- `Migrate_DoesNotResurrectAMovedSecret_WhenTheOldRowIsGone`: the other instance saved the
  module without the old key (its own remap already ran; the Graph secret was then rotated).
  Nothing is copied, the remap reports no change, the rotation stands.

**Mutation** (call site; md5 before, mutated, restored; touch; rebuild; named tests fail;
restore green): M1 the in-transaction re-read replaced by the unlocked read.

**Version.** Base app (shared repository, startup pass in `ModuleConfigService`); no module
bump. Supervisor bumps at merge.

## Guard proof

Built as planned, plus the correction to the plan's `ReadModule` bullet above. The test helper
`CreateService` now takes `IConfigStore` rather than `SqliteConfigStore` so the decorator can be
passed; existing callers are unchanged.

| Test (`ExchangeAdminWeb.Tests/GraphSecretKeyMigrationTests.cs`) | What it pins |
| --- | --- |
| `Migrate_DoesNotRevertAGraphSecretSavedMeanwhile_WhenTheStaleRowIsStillThere` | the finding: a Graph secret the other instance saved between the decision and the write stands; the stale old row is still removed and the module reported as changed |
| `Migrate_DoesNotResurrectAMovedSecret_WhenTheOldRowIsGone` | the old row gone by the time the lock is held: nothing copied, the rotated value stands, the module not reported |
| `Migrate_IsIdempotent_SecondRunIsNoOp` (existing) | still green: the unlocked fast path opens no write and bumps no token |

The window is driven, not hoped for: the decorator runs "the other instance" (a second store
over its own connection factory on the same file) immediately before the remap's write reaches
the real store, and each test asserts that write happened.

Mutation at the call site, on `Services/Storage/ModuleConfigRepository.cs` (original md5
`a68e6a84ee3e857b6ab377f6cd6e2ba2`, pre-comment), run, restored from a copy, touched, restored
md5 re-confirmed equal to the original, rebuilt, 34/34 green on the Graph and module-config
classes:

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1 the in-transaction re-read replaced by the unlocked read (`var current = unlocked;`) | `0edd6858f8331f7a3a50081402ce28d8` | `Migrate_DoesNotRevertAGraphSecretSavedMeanwhile_WhenTheStaleRowIsStillThere` (expected `"saved-on-prod"`, actual `"123"`), `Migrate_DoesNotResurrectAMovedSecret_WhenTheOldRowIsGone` (expected `"rotated-456"`, actual `"123"`) |

That is the predicted failure verbatim - the stale old value written over the other instance's
save - not an assertion standing in for it.

Gates, on this commit's tree (on top of aud-a33b): Release build 0 errors (61 warnings,
unchanged from the baseline, none in these files); full suite 4496 passed / 3 known AD skips /
0 failed, with `Comms10kRefusalTests.C6ValidationWaitsKeepTheCapturedTicketVisibleAndRejectDelayedInput`
excluded by `--filter` - in the unfiltered run it hung again, `--blame-hang` killed the testhost
after 10 minutes' inactivity with 4287 passed and 0 failed (the pre-existing intermittent hang
`.agents/state.md` records); `dotnet format --verify-no-changes` exit 0; `git diff --check HEAD`
clean; `tools/Test-AsciiOnly.ps1` passed.

## Known gaps

- A raced no-op (the old row moved by the other instance's own remap between the unlocked read
  and the lock) returns false but commits the token bump every store write carries; readers on
  both instances reload once. Deliberate, as in aud-a33b.
- Not fixed, already recorded: the config page's whole-module save from the snapshot it loaded
  on open can itself overwrite a newer write (`docs/GraphSecretRemapScope-Plan.md` candidate 1,
  "Needs its own plan"). This fix stops the startup remap reverting a save; it does not make two
  page saves safe against each other.
- Two in-process stores on one file stand in for two processes; nothing here ran against the
  real shared database.

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19, finding 4 of the `4327d1c`
review.

## Closeout

Fixed on `worktree-agent-a801aa638651e79ad`; awaiting merge and its index row (supervisor).
