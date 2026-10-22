# aud-a28i: A concurrent allowlist save is overwritten, and the success audit records a stale before-state

**Severity**: MEDIUM - the editable-attribute allowlist is an authorization-adjacent store shared by
the dev and prod instances (Architectural Invariant 2). Two admins or two instances saving it at
nearly the same time: A reads the "before" rows, B saves a different allowlist, A's `SaveAllowlist`
then deletes and replaces the whole table in a later transaction. B's change disappears, and A's
success record is computed from the pre-B rows, so the Admin Event Log never shows who removed B's
attribute. Same class as aud-a33b / aud-a33c (decide under the write lock from current rows).
**Status**: Fixed on branch; closed on guard proof; awaiting merge and review
**Branch**: `worktree-agent-aa5d4735f85b48351`
**Commit**: `ff6823b8` (comments `87660ef8`)
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

## Fix

`Services/Storage/AttributeEditorRepository.cs`:

- `ReplaceAllowlist(decide)`: one `_store.Write` (`BEGIN IMMEDIATE`). Inside it, the rows and the
  presence marker are read on the write's own transaction and handed to `decide`; a returned list
  replaces the table (delete, insert, mark present) and is read back in the same transaction; a null
  throws the private `NothingWritten`, which `SqliteConfigStore.Write` turns into a rollback - change
  token included - and which is caught at once. Returns `AllowlistReplaceOutcome(Replaced, Before,
  After)`.
- `ReadAllowlistSnapshot()`: the rows and the marker read together, an unreadable store as
  `AllowlistSnapshot.Unreadable` rather than an exception. The two row/marker readers gain
  transaction-aware overloads. New records `AllowlistSnapshot` and `AllowlistReplaceOutcome`.

`Services/ADAttributeEditorService.cs`:

- `SaveAllowlistAndAudit(attributes, loadedVersion, performedBy, ip)`: every decision is made inside
  ReplaceAllowlist's `decide` by `Refuse` - corrupt store (`AllowlistConfig`), a version that no
  longer matches the rows under the lock (`ConcurrentChange`), then aud-a26f's `BlankRow` and
  `DuplicateName`, `Denylist`, `PatternCheck`. The success record's before-state is the rows the write
  replaced (`outcome.Before`); the result carries the version the write left (`outcome.After`). The
  refusal, failure and success records are written outside the transaction, one per attempt.
- `LoadAllowlistForEditing()` returns `AllowlistForEditing(Attributes, Version)` from one fresh read.
  `AllowlistVersion` is a SHA-256 over the stored rows (all columns, name order) and the presence
  marker. The store evaluation LoadAllowlistFromDisk did is now `Evaluate(snapshot)`, shared by the
  load, the editing load and the save; the out-parameter overload aud-a28f added is gone, since the
  save reads inside its write.

`Components/Pages/ModuleConfig.razor`: LoadAllowlist loads through `LoadAllowlistForEditing` and keeps
its version; the save passes it and, when the save goes through, keeps the version it returns.

Tests: new `ExchangeAdminWeb.Tests/AllowlistConcurrentSaveTests.cs` (6 cases); `AttributeEditorHarness`:
`CreateOtherInstance()` (a second service over a second store on the same file) and
`SwitchableStore.BeforeWrite` (a hook run once as the next write opens); the existing allowlist tests
pass the version their load returned (`AllowlistSaveAuditTests.Loaded`/`Save`, two
`ADAttributeEditorServiceTests` cases).

No pre-fix reproduction: the new tests use the new load and the version parameter, so they do not
compile against the old service; the first mutation below puts the defect back at its call site.

Caller-visible change, by design: an allowlist save from a page loaded before anyone else saved the
allowlist - another admin, or either instance - is refused ("changed after this page loaded it ...
reload the page and re-apply your changes") and recorded as `ConcurrentChange`, where it used to
overwrite the other save. A refused save writes nothing to the database at all. Saving twice from one
page still works.

## Guard proof

Same session-local driver as aud-a28g (`mutate.ps1`), filter
`FullyQualifiedName~AllowlistConcurrentSaveTests|FullyQualifiedName~AllowlistSaveAuditTests|FullyQualifiedName~ADAttributeEditorServiceTests|FullyQualifiedName~AllowlistBannerTests`
(109 tests). Fixed-file MD5s: `Services/Storage/AttributeEditorRepository.cs`
`712A8F9B3271E45C71E15BC388F740DE`, `Services/ADAttributeEditorService.cs`
`42E0E4CDC1C5A04BFB7E04757408E3DC`, `Components/Pages/ModuleConfig.razor`
`FEBB7508F8E0B81B156673BE5C7A1E1D`; each restored to its MD5 after every mutant, and every mutant built
with 0 errors. After the round, a rebuild on the restored bytes and the filter widened to
SqliteConfigStoreTests, ProgressRegistryTests and PageAuthorizationRecheckTests: 159/159 green.

| Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|
| the read taken outside the write again (`var readOutside = _store.Read(...)` before `_store.Write`, the callback deciding on it) | `B0EB8A4347B565818B138B105A0D023C` | `ASaveLandingInsideASave_BeforeItsWrite_IsNotOverwritten` (the other instance's row overwritten) |
| extra: the version compare deleted (`if (false && ...)`) | `C84368C7C4169F9E192EC5CA13AF8F11` | `ASaveFromAPageLoadedBeforeAnotherSave_IsRefused_AndTheOtherSaveSurvives`, `ASaveLandingInsideASave_BeforeItsWrite_IsNotOverwritten`, `ARefusedConflict_WritesNothing_NotEvenTheChangeToken` |
| extra: a refusal commits the write transaction instead of rolling it back | `C44071C97E853CA7F96CB3CF968607D2` | `ARefusedConflict_WritesNothing_NotEvenTheChangeToken` |
| extra, page: the version its own save returned is not kept | `A06C088C748265256CD8DB0CCB1557B1` | `ThePage_SavesAgainstTheVersionItLoaded_AndKeepsTheOneItsSaveReturns` |

Under the first mutant the stale-page case still passes, as it must: the read outside the write still
sees the other save when it lands before the read, so the compare refuses it. Only a save landing
between the read and the write - the window the finding names, driven by the hook - gets through.

The code comments are written after the round, in their own commit, so the MD5s above are this
commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; warnings all pre-existing, none in a
  touched file.
- `dotnet test ExchangeAdminWeb.slnx -c Release --no-build --blame-hang --blame-hang-timeout 10m`:
  exit 0, 5,215 passed / 3 skipped / 0 failed, 5,218 total, 5m12s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0.
  `git diff --check`: exit 0. `tools/Test-AsciiOnly.ps1` with the files staged: exit 0.

No PowerShell source, AD module, directory, Delinea, SMTP, push or deployment. No coder self-review
verdict is claimed.

## Known gaps

- The version is the allowlist's whole content, so a page loaded before any other allowlist save is
  refused even when that save touched rows this admin did not edit. Deliberate: the admin reloads and
  re-applies; nothing is merged or guessed.
- Two in-process stores on one temp file stand in for the two instances; nothing ran against the real
  shared database.
- `SaveAllowlist` (public, aud-a28e's backstop) still writes in its own transaction with no version
  and no record. Nothing in the app calls it any more; the tests that do are about its pattern check.
- The page's section-access save has the same page-snapshot shape for its store (aud-a33b's recorded
  known gap): it is not touched here.

## Branch-tip gates - 2026-10-22 (`87660ef8`: all four fixes and the comments)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors, warnings all pre-existing.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is
  written): exit 0, 5,215 passed / 3 skipped (the three known AD skips) / 0 failed, 5,218 total,
  5m38s. No hang.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check
  HEAD`: exit 0. `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.
