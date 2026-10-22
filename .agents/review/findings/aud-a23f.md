# aud-a23f: A migration batch whose row cannot be read is dropped from the list with only a log warning

**Severity**: MEDIUM - a silent omission from an operator-facing list (Known Failure Class 2: per-item
failures must be aggregated and shown, never swallowed). `GetMigrationBatchesAsync` catches a
per-row exception (for example a count that `Convert.ToInt32` cannot read) and skips the batch,
logging a warning; the page shows the remaining batches as if the list were complete, so an
operator looking for that batch concludes it does not exist.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23d/a7i agent, 2026-10-22, candidate 3 of its hand-back)

## Evidence

At `4c54621b`, `Services/MigrationService.cs` `GetMigrationBatchesAsync`: the per-row try/catch that
logs and continues; the result carries only the batches that parsed.

## Approach (to be written by the implementing agent before code)

- The result carries, beside the batches, the rows that could not be read: the batch identity as far
  as it was read (name or id) and the reason. The page shows them as a visible warning row or banner
  ("N batches could not be read: <name>: <reason>") rather than omitting them; the CSV lists them too.
  Nothing is guessed about the unreadable fields; the aud-a23d `ReportedTime` marker already covers
  unreadable times - this covers the other fields (counts, status, type).
- Read each field defensively through the same pattern (a typed reader that returns a marker, not a
  throw), so one bad field does not lose the whole row; only a row with no identity at all goes to
  the unreadable list.
- `Migration.razor` has line-keyed `ClickGateRegistry` entries and `ExpectedLineCount` 4377: edit in
  place or re-anchor per the playbook. Tests: a stub batch with an unreadable count still appears
  with the marker; a row that throws is listed as unreadable with its reason and the page shows the
  warning; the CSV carries it. Mutation at the call site: swallow and skip again.

## Guard proof

Pending.
