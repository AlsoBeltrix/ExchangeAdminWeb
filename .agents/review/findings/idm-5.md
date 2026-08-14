# idm-5: The idm finding records carry placeholder commit metadata while the index reports them closed

**Severity**: LOW - traceability only; no behaviour is affected, but the review trail cannot answer which revision closed which finding.
**Status**: Verified
**Branch**: - (default-branch mode)
**Commit**: `365c7fb`

## Evidence

- `.agents/review/findings/idm-1.md:6` - `**Commit**: <filled in below>`.
- `.agents/review/findings/idm-2.md:6` - same placeholder.
- `.agents/review/findings/idm-3.md:6` - same placeholder.
- `.agents/review/index.md` marks all three `[x]`, and each row names the fixing commit - so the
  index is complete while the per-finding files, which are the detailed record, are not.

Confirmed by `git log -1 -- <file>` per record: idm-1 landed in `89ea0d4`, idm-2 in `f5946b2`,
idm-3 in `28f848c` - exactly the mapping the reviewer proposed, checked rather than accepted.

## Predicted observable failure

A later reader opening `.agents/review/findings/idm-2.md` to learn how the wipe-body finding was
closed finds a placeholder where the commit should be, and has to reconstruct it from `git log`
or from the index. The per-finding record is the artifact the playbook designates as the durable
detail; a placeholder in it makes the record look drafted-and-abandoned rather than closed.

## What

The template's `Commit` field is filled after the commit exists. I wrote all three records before
committing any of them and never went back. The index rows were written later and did carry the
SHAs, which is why nothing looked wrong.

## Approach

Backfill the three fields from `git log` per file. Also fill `idm-4` and this record on the same
principle - a record written before its commit gets its field completed in that commit's own
change, not in a later sweep.

## Files changed

- `.agents/review/findings/idm-1.md:6` - `89ea0d4`
- `.agents/review/findings/idm-2.md:6` - `f5946b2`
- `.agents/review/findings/idm-3.md:6` - `28f848c`

## Guard proof

None possible or warranted - this is record content, not behaviour. The check is reading the
three files and confirming no placeholder remains, which is what the fixing commit does.

## Coder dispute (if any)

None. Verified per file with `git log`.

## Known gaps

Nothing structural stops this recurring; the template invites it by placing the field before the
commit exists. Not worth a mechanism for two records, recorded here so a third occurrence is
recognised as a pattern rather than an accident.

**And it recurred immediately, in this record.** The first draft of this file filled its own
`Commit` field with `d0f7a4e` - a SHA that never existed, written before the commit was made.
That is a worse instance of the defect than the one being fixed: a placeholder is visibly
unfinished, whereas an invented SHA looks like a record and resolves to nothing. Corrected to
`365c7fb` in a follow-up commit. The rule this actually earns is narrower than "remember to
backfill": **never write a SHA you have not read back from `git`** - leave the field visibly
empty until the commit exists.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade `fallback`; openreview over `6aef9e3..236b91b`, verdict `acceptable_with_changes`,
`capability_ok: true`, both SHAs echoed)

Harness: codex-cli 0.147.0, `codex exec`, read-only sandbox. UTC 2026-08-14.

The reviewer supplied the correct commit for each of the three records unprompted, which is the
part worth noting: it read the range rather than only the diff.
