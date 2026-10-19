<!-- push-policy: conditional -->
Push without asking once ALL THREE hold for the work in hand. Owner ruling 2026-10-19.

1. **Complete** - the work stream or slice is finished, not paused mid-change. The working
   tree is clean and every change is committed.
2. **Reviewed** - the repo's review protocol has run to its end for this work and its findings
   are closed, not merely raised. For a code-review stream that means rounds until clean.
   Self-review does not count (`AGENTS.md`).
3. **Consensus** - no contested finding, no unanswered owner gate, and no reviewer verdict
   still outstanding on the range being pushed.

If any of the three is false, ask. In particular, ask when: a review round is still in flight,
a finding is `[!]` contested, an owner decision named in a plan is unanswered, or the range
contains work the owner has not approved.

Pushing remains an outward-facing action. Nothing here authorizes a merge, a branch deletion,
a history rewrite, a force-push or a deploy - each of those still needs its own explicit
authority per `AGENTS.md` Git Safety and `.agents/playbooks/git.md`.

Push to both remotes (`github` and `origin`) unless told otherwise; they are expected to match.
