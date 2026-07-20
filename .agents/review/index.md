# Review status

Workflow: see `.agents/playbooks/codereview.md`.
Per-finding detail: see `.agents/review/findings/<id>.md`.

## Legend
- `[ ]` Admitted, open (passed intake triage; not yet started)
- `[~]` In progress / pending review
- `[x]` Verified (awaiting owner-gated merge / implementation)
- `[!]` Contested — declined, disputed, or ruled invalid; awaiting owner adjudication
- `[-]` Declined at intake (kept for the record; no work)

## Findings

| ID          | Severity | Impact (one line)                                             | Status | Branch | Reviewer |
|-------------|----------|---------------------------------------------------------------|--------|--------|----------|
| pp-finder-1 | HIGH     | Protected room editable via single-room Finder (no PP gate)   | `[x]`  |        | codex/gpt-5.5-dzs/xhigh/std (finding+plan r1-3); codex-commercial/gpt-5.6-sol/max/frontier (consolidation r4) |

Notes:
- Finding **confirmed real** (round 1) and the fix **plan reviewed & accepted**
  (rounds 2-3 page-seam; round 4 consolidation C2-G). Implementation of code is
  **not started** — blocked on explicit owner go (docs plan is Reviewed-accepted).
- Scratch dispatch artifacts (`*.prompt.txt`, `*.schema.json`, `*.result.json`) are
  left untracked pending the owner's commit-vs-clean decision.
