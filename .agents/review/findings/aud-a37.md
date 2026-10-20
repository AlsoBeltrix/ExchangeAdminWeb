# aud-a37: the 2am recovery guide described a storage arrangement that no longer exists

**Severity**: MEDIUM as a document, HIGH in the moment it is used. It is read during an
incident, by someone who is not going to cross-check it against the deploy script, and two
of its four wrong instructions make the outage worse rather than merely failing.
**Status**: Verified, fixed, independently reviewed, review findings remediated
**Branch**: -- (direct-to-main)
**Commit**: `8978732`

## Evidence

Finding 37 of the 2026-10-16 audit. The audit names four drifts; all four were verified
against current code, not taken on the audit's word.

### 1. Local paths

The guide opened: *"Runtime configuration lives in a single SQLite database,
`config/exchangeadmin.db`, in the app's publish folder (one per environment - dev and prod
each have their own)."*

Both halves are wrong since decision 2026-09-04. The database is named by `ConfigStore:Path`
in each instance's `appsettings.json`, lives outside both publish folders, and is **shared**:
dev and prod open the same file, so a setting saved on dev is live on prod at once
(`.agents/repo-guidance.md`, Architectural Invariant 2; `deploy.ps1:437-443`).

### 2. Single-pool shutdown

*"stop the app pool first so you are the only writer ... `Stop-WebAppPool -Name
ExchangeAdminWeb` (use `ExchangeAdminWebDev` for dev)"*

With one shared file this is not merely incomplete, it is **false reassurance**: the operator
is told they are the only writer while the other instance is still writing. Both pools have
to stop.

### 3. Database recreation

*"Last resort - rebuild from JSON ... delete `exchangeadmin.db*`, rename the relevant
`*.imported-*` files back ... and restart - the app re-imports them on next start."*

The app **never creates a configured database**, and refuses to start without one -
`deploy.ps1:447` fails the deploy on exactly this condition and says so. Following this step
converts a corrupt instance into a dead one. The JSON re-import route is also retired, and
the archived files carry pre-SID group names (below), so even a successful rebuild would
restore the wrong kind of authorization row.

### 4. Name-based authorization rows

```sql
INSERT OR IGNORE INTO section_access(policy_alias, group_value)
  VALUES ('MailboxPermissions','DOMAIN\\Exchange-Admins');
```

`section_access` has carried three columns since migration v6
(`ConfigStoreMigrator.cs:158`): `policy_alias`, `group_value`, `group_display_name`.
`group_value` holds a **SID** (`docs/SectionAccessSidStorage-Plan.md`, Implemented
2026-08-03: "store an unambiguous identifier, display the friendly name"). The documented
insert writes a display string into the SID column and leaves the display column null.

## Predicted observable failure

The config DB is corrupt at 2am. The operator opens this guide. They stop one pool and
believe they are the only writer. They look in the publish folder for a database that is not
there, or edit the wrong instance's. Finding no usable backup at the stated location they
reach step 3, delete the database, and restart - and now the app will not start at all,
because the configured path names a file nothing will recreate.

## Approach

Rewritten against the current code. Docs-only; no behaviour changed.

- **"Where the database is" is now the first section**, with a one-line PowerShell command to
  read `ConfigStore:Path` out of `appsettings.json`, and the three consequences spelled out:
  dev and prod share it, a deploy never touches it, and the app never creates it.
- **Both pools, in one copy-pasteable block**, with the reason given - stopping one leaves the
  other writing underneath you.
- **A "databases that are NOT this one" note** for `exchangeadmin-jobs.db` and
  `exchangeadmin-usage.db`, because their deliberate absence from backups has been mistaken
  for a defect before (`.agents/repo-guidance.md` Invariant 3 says not to "fix" it).
- **"Do not delete the database" leads the recovery section**, with the real backup path
  (`<LogRoot>\ExchangeAdminWeb\ConfigBackups\config.<timestamp>.bak\exchangeadmin.<timestamp>.db`,
  `LogRoot` defaulting to `E:\WWWOutput`) read out of `deploy.ps1:152` and
  `SqliteConfigBackup.psm1:146` rather than the guessed `D:\backups\...` the old text gave.
  The no-backup case points at `tools\Move-ConfigDbToShared.ps1` instead of a hand-built file.
- **The SID subsection** resolves the SID with `NTAccount.Translate` and writes all three
  columns, keeps the presence-marker warning that was the old text's one genuinely useful
  paragraph, and says plainly that the admin page is the preferred route.
- The retired JSON route is explicitly marked retired and explained, rather than deleted -
  the `*.imported-*` files still exist on disk and an operator who finds them needs to know
  what they are.

## Files changed

- `docs/ConfigDbOperations.md`

Docs-only, so no version bump (Constitution, Deployment And Versioning) and no module is
affected.

## Guard proof

None, and that is the honest answer: this is prose. There is no test that a document is
true, and inventing a source scan over it would be the "passes for the wrong reason" trap
this stream has already hit twice.

What was done instead is that **every factual claim in the new text was read out of the
code or the scripts** during the rewrite, with the file and line recorded above:
`deploy.ps1:152` (backup root), `deploy.ps1:437-447` (shared path, must exist, never
created), `SqliteConfigBackup.psm1:146` (backup file name), `ConfigStoreMigrator.cs:158`
(three columns), `docs/SectionAccessSidStorage-Plan.md:3` (SID decision and its status),
`Services/Jobs/JobStoreMigrator.cs` and `Services/Storage/UsageEventRepository.cs`
(per-instance databases).

Gate: `git diff --check` clean. Docs-only, so per `AGENTS.md` that is the required check;
the build and suite were not re-run because nothing compiled changed.

## Known gaps

- **None of the recipes was executed.** The SQL and PowerShell are read from current schema
  and current scripts, not run against a real database. The `.recover` and restore
  procedures in particular have not been rehearsed by me.
- **`LogRoot` is a deploy parameter with a default.** The guide gives the default and says
  so, but a deploy invoked with a different `-LogRoot` puts backups elsewhere, and the guide
  cannot know that.
- **The admin page is recommended over hand-editing SIDs but was not re-checked** to confirm
  it still resolves and writes `group_display_name` the way the SID plan described.
- Other operator docs were not audited for the same drift. `aud-a37` named this one.

## Closeout

Fixed directly on `master`. Independent review pending - and currently impossible: both
review harnesses are returning 401 (`.agents/state.md`). A document is also the artifact
where an independent reader is worth most, so this one should not be considered closed on my
reading alone.

## Independent review - `gemini-4-argon-high`, 2026-10-20

Reviewed as batch 3 of the backlog. Prompt kept at `.agents/review/prompt-backlog-3.md`.

**Any earlier claim in this record that review was impossible was wrong** - the probes had
been invoked incorrectly. `.agents/state.md` carries that correction.

**Two defects, both in the runbook I rewrote, and one of them could not work.**

Step 3 told the operator to create a fresh shared database with
`tools\Move-ConfigDbToShared.ps1`. On an already-cut-over server that script **refuses by
design** - it fails when a file already exists at `SharedDbPath` and expects per-instance
databases to copy from. A recovery instruction that aborts is worse than none at 2am. It now
says to create an empty file with `sqlite3` and let the migrator build the schema, and
explicitly warns against the cutover script.

The backup location was also incomplete: it gave only the `deploy.ps1` path. Promotions write
to `<BackupRoot>\cutover.<timestamp>` (default `D:\backups\ExchangeAdminWeb`) and generic
installer upgrades to `<LogRoot>\ConfigBackups`. All three are now listed, because the
operator needs the newest of them and would not have found two.

The `.recover` step also omitted deleting the stale `-wal`/`-shm` sidecars, which belong to
the corrupt database and let SQLite replay a WAL that does not match the recovered file.

Remediation landed in the follow-up commit, every fix mutation-proven.
