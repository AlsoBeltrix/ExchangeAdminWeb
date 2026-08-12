# ru-1: D2 (read alerting) was non-blocking, so the read phase could ship with the security-response notification clause unhonoured

**Severity**: HIGH -- the plan itself classifies this module as a security-response
surface, then permits shipping it with no administrator alerting and no owner ruling on
the subject.
**Status**: Verified
**Branch**: --
**Commit**: `5b7fbc0` (plan revision)

## Evidence

`docs/ProjectConstitution.md:79` requires an administrator alert from "a read module
classified as a security-response surface", and states that the classification "is a
deployment classification, not an automatic property of touching directory data".

`docs/RiskyUsersModule-Plan.md` D2 argued, correctly, that this module meets that clause
on its face -- "it is the first module in the repo that meets the clause on its face".
It then instructed audit-only implementation with no `EmailService` on the read path, and
listed D2 under `## Open questions` as "Blocks nothing".

Nothing in the plan connected the two. S7 ("Docs and version") could close, the plan
could be marked `Implemented`, and the module could deploy with the clause unhonoured and
the question still open.

## Predicted observable failure

The read phase ships to dev and prod. Operators triage Entra ID Protection risk data with
no administrator alert of any kind, and no recorded classification decision saying that is
intended. The gap is invisible: every test passes, the audit log looks complete, and the
only artifact recording that a decision was owed is an open question in a plan marked
`Implemented`.

## What

An interim default and a shipped answer were one line apart in the same document. The
plan reasoned its way to the right conclusion (this module meets the clause) and then
attached no gate to it.

`.agents/decisions.md` 2026-06-30 deferred read-alerting for every existing module on the
reasoning that this app's reads expose only address-book-visible data. That reasoning does
not transfer here, which D2 already said -- so this module cannot inherit the deferral.

## Approach

D2 is now a PRE-SHIP GATE rather than a non-blocking question. Audit-only remains the
development-time default for S1-S4 (it is the reversible direction), but S7 cannot close
and no phase may be marked `Implemented` until D2 is ruled and the ruled shape is built.

Three artifacts carry the gate so it cannot be lost by reading only one of them: the
`Status:` line, the D2 section, and `## Open questions`. AC17 and manual check 7 make the
ruled behaviour testable whichever way it goes -- including "alert on nothing", which
asserts `EmailService` is NOT reachable from the read path so a later edit cannot add one
silently.

An explicit owner ruling of "no alerts" honours the Constitution, because the clause asks
for a classification. What is not permitted is shipping with no ruling at all.

## Files changed

- `docs/RiskyUsersModule-Plan.md` -- `Status:` line, D2 section, AC17, manual check 7,
  `## Open questions` entry

## Guard proof

Not applicable: this finding is against a plan document and the fix is a plan revision.
The guard belongs to the implementation -- AC17 must carry a test asserting the ruled
alerting shape, provable by reverting that behaviour and watching it fail.

## Coder dispute (if any)

None. Admitted as stated. The reviewer's phrasing ("audit-only should not be the silent
interim default") was narrowed on admission: audit-only remains correct DURING S1-S4
development, because it is the reversible direction. What was wrong was the absence of a
gate between that default and shipping, which is what the revision adds.

## Known gaps

D2 is still unruled. This finding does not answer it; it makes the answer mandatory before
ship.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / frontier`
(grade: fallback -- frontier equals standard on this transport, owner-ruled 2026-08-03)

openreview over `d877294281f694ff3490af9cbedc5a2eb6ca68fa..a2c4c77ad41834b7899edb07e214088c51edd29e`,
verdict `acceptable_with_changes`, `capability_ok: true`, 2026-08-12.
