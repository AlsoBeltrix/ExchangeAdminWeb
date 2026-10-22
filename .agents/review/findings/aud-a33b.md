# aud-a33b: the SID data migration can restore a grant that was revoked meanwhile

**Severity**: HIGH - it restores access an administrator removed, silently, and the only
signal is that someone who should have lost a permission still has it. Pre-existing.
**Status**: Merged to master (`3ce560bd`); closed on guard proof; codex storage-batch-1 clean
**Branch**: `worktree-agent-a801aa638651e79ad`
**Commit**: `82fc9b88` (merged `3ce560bd`)

## Evidence

Raised by codex-commercial (gpt-6-astra, max) reviewing `4327d1c`, as finding 1, and
explicitly labelled pre-existing.

`Services/SectionAccessSidMigration.cs` reads the section-access grants, resolves the legacy
display names to SIDs - a directory round trip, so not fast - and then **replaces the table
from the snapshot it read before that resolution** (`:73`, `:115`,
`Services/Storage/SectionAccessRepository.cs:269`). The write transaction does not revalidate
the source rows.

Dev and prod share one database (`.agents/decisions.md` 2026-09-04). So while legacy names
remain anywhere in the table, an administrator changing grants through the other instance can
have those changes overwritten by this migration completing - **including a grant they just
revoked being written back**.

This is the read-then-write shape `aud-a33` fixed in the schema migrator, in the data
migration beside it.

## Predicted observable failure

An administrator removes someone's access to a section during an incident. The other instance
restarts, its SID migration finishes a resolution it began before that change, and the
revoked grant reappears. Nothing reports anything; the access-control page simply shows the
old grant again.

## Approach (as filed)

Re-read the grants inside the write transaction and reconcile against what was resolved,
rather than replacing wholesale from the earlier snapshot. Rows that changed since the
snapshot must be left alone and the migration retried for them on the next start - it is
already a retryable, AD-dependent step by design, which is why it is separate from the schema
migration.

Fail closed in favour of the newer data: a grant that is absent now was removed by somebody,
and re-adding it is the harm.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21, relayed by the supervising session: "fix all the bugs ... EVERYTHING."

**Mechanism.** The directory resolution stays outside any transaction - no I/O under the
write lock. The write becomes a per-row compare-and-swap inside ONE store write transaction,
which is `BEGIN IMMEDIATE`, so the re-read and every write it decides run under the write lock
and no writer - the other instance included - can commit between them.

- `SectionAccessRepository.ReplaceAllWithSids` (wholesale `DELETE` + insert from the snapshot)
  is replaced by `ConvertToSids(IReadOnlyList<SidConversion>)`. Per conversion (alias, value
  as read, SID resolved), against the table as it is under the lock:
  - (alias, value) present: delete it, upsert (alias, SID) with the existing ON CONFLICT
    display-name merge. **Converted.**
  - absent, (alias, SID) present: the row is already in the state this conversion produces -
    the other instance's migration, or an administrator re-picking the same group. No write,
    not a conflict. **Already converted.**
  - absent, (alias, SID) absent: removed or changed by someone after the read. **Skipped and
    returned; never re-inserted.**
  - Rows the migration did not read are never touched (no wholesale delete), so a grant added
    meanwhile survives; a name among them converts on a later start.
  - Presence marker re-asserted only when something converted.
- Identity compared: key existence, in SQL, under the columns' NOCASE collation. (policy_alias,
  group_value) is the whole of a grant; the only other column is the cosmetic display name.
  Rowids are reassigned by every whole-table save, so they are no version. The change token is
  not used: it moves on any config write and misses writers that bypass the store; existence
  is exact for every writer.
- `SectionAccessSidMigration.Run` passes the rows whose stored value differs from the SID
  (names; a SID stored with stray whitespace, as before). Any skip: a warning naming every
  skipped row, new status `SkippedChangedRows`. Nothing converted and nothing skipped:
  `AlreadyMigrated`.
- Planner unchanged. All-or-nothing still governs resolution - one unresolvable row writes
  nothing. A row another writer changed is not a resolution failure: it is that writer's, so
  the rest of the plan still applies.
- `SqliteConfigStore.Write` states `BeginTransaction(deferred: false)`. The provider's
  parameterless call already issues `BEGIN IMMEDIATE` (aud-a33 review, finding 3); stated
  because this fix rests on it, and pinned by a test.
- Raced no-op (every row already converted): the transaction commits nothing but the token
  bump every store write carries, exactly as `ImportIfMissing` returning false does today.
  Readers reload once. Accepted rather than adding a commit-or-rollback store primitive and
  touching all eleven test fakes of `IConfigStore`.

**Files.** `Services/Storage/SectionAccessRepository.cs`, `Services/SectionAccessSidMigration.cs`,
`Services/Storage/SqliteConfigStore.cs`; `ExchangeAdminWeb.Tests/SectionAccessSidMigrationTests.cs`,
`ExchangeAdminWeb.Tests/SqliteConfigStoreTests.cs`.

**Tests.** Real SQLite temp database. "The other instance" is a second store over a second
connection factory on the same file. The window is the fake directory's lookup - where the
real window is, so no production seam is added.

- `AGrantRevokedAfterTheReadIsNotRestored`
- `AGrantNobodyTouchedIsStillMigratedWhenAnotherRowChanged`
- `ARowChangedToADifferentValueIsSkippedAndReported` (status, table, warning names the row)
- `AGrantAddedAfterTheReadIsKept`
- `TwoInstancesMigratingTogether_TheLaterOneReportsNothingSkipped`
- `SqliteConfigStoreTests.Write_HoldsTheWriteLockBeforeTheCallbackReads`

**Mutations** (at the call site; md5 before, mutated, restored; touch; rebuild; named test
fails; restore green): M1 source-present check removed; M2 already-converted classification
removed; M3 `Run` calls the pre-fix wholesale replacement (`SaveAll` of the plan); M4
over-correction - convert nothing when anything was skipped; M5 `Write` opens
`deferred: true`.

**Version.** Base app (shared storage and the authorization store); no module bump.
Supervisor bumps at merge.

## Guard proof

As built, two departures from the plan, both additive: `Services/Storage/IConfigStore.cs`
gains doc remarks on `Write` (the decide-inside-the-callback contract; no code), and one more
test, `ARevokedGrantAlreadyHeldAsASidIsNotRestoredEither`, added after the first mutation round
because the revoked-grant test covered a name row while a part-migrated table holds SIDs too.
Its mutation run is on the post-comment source and is recorded separately below.

| Test | What it pins |
| --- | --- |
| `SectionAccessSidMigrationTests.AGrantRevokedAfterTheReadIsNotRestored` | the finding: a name grant revoked through the other instance during resolution stays revoked; status `SkippedChangedRows` |
| `SectionAccessSidMigrationTests.ARevokedGrantAlreadyHeldAsASidIsNotRestoredEither` | the same for a row already holding a SID, which the old rewrite restored too |
| `SectionAccessSidMigrationTests.AGrantNobodyTouchedIsStillMigratedWhenAnotherRowChanged` | the check is per row: untouched names still convert, with display names |
| `SectionAccessSidMigrationTests.ARowChangedToADifferentValueIsSkippedAndReported` | a re-pointed grant: old row not restored, replacement kept, the warning names `BlockedSenders / 'ExchangeWebAdmins'` |
| `SectionAccessSidMigrationTests.AGrantAddedAfterTheReadIsKept` | no wholesale delete: a grant written meanwhile survives |
| `SectionAccessSidMigrationTests.TwoInstancesMigratingTogether_TheLaterOneReportsNothingSkipped` | both instances starting together: the later one reports `AlreadyMigrated`, no warning |
| `SqliteConfigStoreTests.Write_HoldsTheWriteLockBeforeTheCallbackReads` | the store's write transaction refuses another writer before the callback has read anything (IMMEDIATE) |

The window is driven, not hoped for: the fake directory runs "the other instance" (a second
store over its own connection factory on the same file) inside its first lookup, and each test
asserts that write happened.

Mutations at the call site, pre-comment sources. Originals: `SectionAccessRepository.cs`
`37b4c9f7f3845a852963029f37778a2b`, `SectionAccessSidMigration.cs`
`12d09d8ab7a9cc08a1be77b44f34638f`, `SqliteConfigStore.cs` `4e7b62d9d826f32d49d5af00d179040c`.
Each run: mutate, md5, `dotnet test` (rebuild) on the a33b classes, restore from a copy, touch,
md5 re-confirmed equal to the original. All restored, rebuilt, 29/29 green.

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| M1 `ConvertToSids`: source-still-present check removed (convert regardless) | `5ae56a49d49f3a1d0c63cb2103f9cef7` | `AGrantRevokedAfterTheReadIsNotRestored` ("Item found in collection": the revoked `-677335` SID written back), `AGrantNobodyTouched...` (`BlockedSenders` carries `-677335`), `ARowChanged...` (3 rows, expected 2), `TwoInstances...` (`Migrated`, expected `AlreadyMigrated`) |
| M2 `ConvertToSids`: already-converted classification removed (a vanished source always counts as skipped) | `e2382ce76d7798337f1c10f0eb46cd82` | `TwoInstances...` (`SkippedChangedRows`, expected `AlreadyMigrated`) |
| M3 `Run` call site: the pre-fix wholesale replacement - `SaveAll` of every planned row | `e62736f8eeb5b031ab698815e3af9906` | `AGrantRevoked...` (`-677335` found), `ARowChanged...` (the administrator's `-901234` replacement wiped), `AGrantAdded...` (1 row, expected 2), `AGrantNobodyTouched...` (`-677335` back), `TwoInstances...` (`Migrated`). Collateral, not proof: three display-name tests, because the `SaveAll` stand-in writes no plan display names |
| M4 `ConvertToSids`: over-correction - convert nothing when any row would be skipped | `6d266f0b6ac7efeec28c29c540ea5947` | `AGrantNobodyTouched...` (`"IAM"` left as an inert name), `ARowChanged...` |
| M5 `SqliteConfigStore.Write`: `BeginTransaction(deferred: true)` | `8f124e734a51aa4a6de2f11d502630b7` | `Write_HoldsTheWriteLockBeforeTheCallbackReads` ("Another connection began a write inside the store's write transaction") |

Post-comment, for the test added after the round: `SectionAccessSidMigration.cs` original
`bc2cbb600bab65b6555746bd8d5b597f`, M3 again -> `6e6c86f4890e6194dee955f5670b59e8`, failed
`ARevokedGrantAlreadyHeldAsASidIsNotRestoredEither` ("Collections differ at index 0": the revoked
`-586078` back), restored, touched, md5 re-confirmed `bc2cbb60...`.

M5 pins a property the pre-fix code already had - the provider's parameterless
`BeginTransaction()` is IMMEDIATE - so it is a regression guard for what this fix rests on, not
proof of a change. Writing it surfaced a provider quirk recorded in the test: with
`PRAGMA busy_timeout = 0; BEGIN IMMEDIATE;` as ONE command, the BEGIN's `SQLITE_BUSY` was not
surfaced - the call returned normally with no transaction open.

Gates, on this commit's tree: Release build 0 errors (61 warnings, unchanged from the baseline,
none in these files); full suite 4494 passed / 3 known AD skips / 0 failed, with
`Comms10kRefusalTests.C6ValidationWaitsKeepTheCapturedTicketVisibleAndRejectDelayedInput`
excluded by `--filter` - in the unfiltered run it hung, `--blame-hang` killed the testhost after
10 minutes' inactivity with 4285 passed and 0 failed, which is the pre-existing intermittent hang
`.agents/state.md` records; `dotnet format --verify-no-changes` exit 0; `git diff --check HEAD`
clean; `tools/Test-AsciiOnly.ps1` passed.

## Known gaps

- Behaviour change, cosmetic, not pinned: rows already holding a SID are no longer rewritten,
  so a display name stored against one now survives a migration run. The old rewrite inserted
  them with a null display name into an emptied table, wiping it.
- A raced no-op (every row already converted by the other instance) still commits the token
  bump every store write carries. Readers on both instances reload once. Deliberate, see Plan.
- Rows another writer added as NAMES stay names (inert, so denied) until the next start
  converts them. The current admin page stores only SIDs, so this needs an older build or a
  hand edit.
- Not fixed, same class as one already recorded: `Components/Pages/ModuleConfig.razor` saves
  section access by re-reading the table, overwriting the aliases it is editing with the
  state it loaded when the page opened, and writing the result through the whole-table
  `SaveAll`. A newer write to those aliases - this migration's SIDs included - is overwritten
  (names come back, inert until the next start). That is the page-snapshot shape of the
  whole-module save in `docs/GraphSecretRemapScope-Plan.md` candidate 1, "Needs its own plan."
- Two in-process stores on one file stand in for two processes; nothing here ran against the
  real shared database.

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19, finding 1 of the `4327d1c`
review.

## Closeout

Fixed on `worktree-agent-a801aa638651e79ad`; awaiting merge and its index row (supervisor).
