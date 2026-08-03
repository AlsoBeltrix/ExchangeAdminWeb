# sid-2: Startup SID migration skips legacy file-backed section access

**Severity**: MEDIUM - on a legacy upgrade the authorization table stays in the pre-migration
identity model for the whole process lifetime, which after sid-1 means denying legitimate
users until someone restarts.
**Status**: Verified
**Branch**: -- (default-branch mode)
**Commit**: `019b814`

## Evidence

`Program.cs:223-224` runs `SectionAccessSidMigration`. The legacy `config\sectionaccess.json`
import runs in the `SectionAccessService` constructor
(`Services/SectionAccessService.cs:35-36`), and that service is registered at `Program.cs:83`
as a singleton -- so it is not constructed until something first resolves it, which nothing in
the startup block does.

Trigger: upgrade a host that has an empty SQLite `section_access` table and an existing
`config\sectionaccess.json`. The migration reads zero rows and returns `AlreadyMigrated`
(`Services/SectionAccessSidMigration.cs:81-84`); the legacy import then fires on the first
request that touches authorization, writing NAME rows after the migration has already run.

## Predicted observable failure

For the rest of that process lifetime the table holds names. Combined with the sid-1 fix, which
makes non-SID rows inert, every user configured only through the legacy file is DENIED until an
administrator restarts the app -- and the restart is not obviously the remedy, because nothing
in the UI says the rows are unmigrated.

Without the sid-1 fix the same ordering leaves name-based access open instead, which is the
original defect. Either way the ordering is wrong.

## What

Two independent startup steps that write the same table run in the wrong order, and the
dependency is invisible: one of them runs as a side effect of a constructor, so its timing is
decided by whoever happens to resolve the singleton first.

## Approach

Resolve `SectionAccessService` immediately before running the migration, so the legacy import
completes first and the migration sees the imported rows. One line plus the explanation of why
the order matters -- a comment is load-bearing here precisely because the dependency is a
constructor side effect that no signature reveals.

The alternative -- moving the import out of the constructor -- is a larger change to a
fail-closed path with its own corrupt-file semantics, and it is not needed to close this.

## Files changed

- `Program.cs` -- resolve `SectionAccessService` before `SectionAccessSidMigration.Run()`, with
  the ordering rationale recorded at the call site.

## Guard proof

`SectionAccessSidMigrationTests.LegacyImportedRowsAreMigratedInTheSameStartup`, which drives the
real `SectionAccessService` constructor over a temp `sectionaccess.json` and then runs the
migration. Removing the resolve (or running the migration first) makes it FAIL with the rows
still holding names; restoring makes it PASS.

## Coder dispute (if any)

None. Verified against `Program.cs` and the registration line before fixing.

## Known gaps

The legacy import remains a constructor side effect, so a future startup step that writes
`section_access` could reintroduce an ordering bug. Noted rather than fixed: making the import
explicit is a separate change to a fail-closed path.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard`
Harness: codex-cli 0.146.0 (`codex exec`, generation pass).
Range: `b872861db2580e7b3edb00be5c8ab15b7b1a21f4..0a50d01812c95a13824e3db2a05873f33500a65f`
(both SHAs echoed correctly). `capability_ok: true`. Verdict: **findings** (2).
Timestamp: 2026-08-03T18:20Z.

Routing note: as for sid-1 -- T1 matched, the frontier pin 404s, so this pass ran at standard.
