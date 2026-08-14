# idm-4: `.agents/state.md` anchors the Intune work at a commit five commits stale, and asserts an unpushed count that goes stale on every commit

**Severity**: MEDIUM - `state.md` is the repo's designated current-state entry point, so a wrong anchor there misdirects the next cold session rather than one reader.
**Status**: Verified
**Branch**: - (default-branch mode)
**Commit**: `cd4959e`

## Evidence

- `.agents/state.md` - the Intune entry says the plan was "revised through `b681185`", while the
  plan file's last content revision is `74c36b9` (the D3 ruling). Confirmed with
  `git log -1 -- docs/IntuneDeviceManagement-Plan.md`.
- `.agents/state.md` - "FOUR plans are written ... As of `b681185`".
- `.agents/state.md` - "SEVEN COMMITS ARE UNPUSHED ... local `master` is at `b681185`" and names
  the range `6aef9e3..b681185`.

All three were written before the D2 and D3 rulings landed, and none was revisited when the five
later commits (`c74c295`, `8913b7f`, `74c36b9`, `236b91b`, and the review-paperwork commits after
them) were made.

## Predicted observable failure

A cold session re-grounds from `.agents/state.md`, per `AGENTS.md` Session Startup, and reads the
Intune plan as last revised at `b681185`. Everything after that - both owner rulings, the wipe
options, the Entra slice, the fourth permission - sits outside the anchor it was told to trust.
The reader either misses them or, worse, diffs against `b681185` and concludes the extra commits
are unrecorded work. The unpushed count compounds it: seven was true when written and is wrong
after every subsequent commit, so a reader reporting outbound work reports the wrong set.

## What

Two different staleness bugs sharing one cause. The anchor is a **snapshot claim** in a file whose
whole job is to be current, and the count is a **derived number** recorded as a fact. The second
is the more interesting one: any exact commit count in a file that is itself committed is stale
the moment it lands, including when the very commit that writes it lands.

## Approach

- Repoint the plan anchor at `74c36b9`, the plan file's actual last content revision, which is
  stable because later commits touch `state.md` and the review records rather than the plan.
- Drop the exact unpushed count and the local-head SHA entirely. State the durable fact instead:
  both remotes are at `b4029c6`, and everything on local `master` after it is unpushed and
  docs-only. That statement cannot go stale by committing, which the count provably could.
  The reviewer offered updating the numbers or dropping the claim; dropping it is chosen because
  updating it recreates the defect on the next commit.
- Replace the "as of `<sha>`" on the four-plans line with the same non-brittle phrasing.

## Files changed

- `.agents/state.md` - Intune plan anchor, the four-plans as-of line, and the unpushed paragraph.

## Guard proof

None possible - this is record content. The check is that no line in `state.md` asserts a commit
count or a local head SHA that the next commit invalidates, which is verifiable by reading it.

## Coder dispute (if any)

None on either half. The reviewer's framing of the count as optionally-droppable is adopted in its
stronger form: dropped, not corrected.

## Known gaps

`state.md` still carries older stale lines elsewhere, already flagged in its own Blockers section
(the `Deployed:` paragraph saying `2.5.5`). Out of scope here and untouched - this finding is
about lines this work stream wrote.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade `fallback`; openreview over `6aef9e3..236b91b`, verdict `acceptable_with_changes`,
`capability_ok: true`, both SHAs echoed)

Harness: codex-cli 0.147.0, `codex exec`, read-only sandbox. UTC 2026-08-14.

Reviewer's material change: "Update .agents/state.md so the Intune plan/current-state anchors,
as-of SHA, unpushed commit count, and unpushed range reflect 236b91b or remove exact push/count
claims until freshness is reverified."
