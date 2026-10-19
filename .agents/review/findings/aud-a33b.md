# aud-a33b: the SID data migration can restore a grant that was revoked meanwhile

**Severity**: HIGH - it restores access an administrator removed, silently, and the only
signal is that someone who should have lost a permission still has it. Pre-existing.
**Status**: Open - not started
**Branch**: -
**Commit**: -

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

## Approach - NOT BUILT

Re-read the grants inside the write transaction and reconcile against what was resolved,
rather than replacing wholesale from the earlier snapshot. Rows that changed since the
snapshot must be left alone and the migration retried for them on the next start - it is
already a retryable, AD-dependent step by design, which is why it is separate from the schema
migration.

Fail closed in favour of the newer data: a grant that is absent now was removed by somebody,
and re-adding it is the harm.

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19, finding 1 of the `4327d1c`
review.

## Closeout

Not started.
