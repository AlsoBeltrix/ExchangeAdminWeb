# aud-a33c: the Graph key remap can overwrite a value the other instance just saved

**Severity**: MEDIUM - a configuration change made on one instance can be silently reverted by
the other starting up. Pre-existing.
**Status**: Open - not started
**Branch**: -
**Commit**: -

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

## Approach - NOT BUILT

Re-read and re-decide inside the write transaction, and make the upsert conditional on the
destination still being what the decision was based on. The remap is a one-time key rename, so
skipping when the destination already holds a newer value is the correct outcome, not a
failure.

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19, finding 4 of the `4327d1c`
review.

## Closeout

Not started.
