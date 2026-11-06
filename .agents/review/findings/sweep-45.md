# sweep-45: the records disagreed with the code and with each other (sweep item 45)

**Severity**: MEDIUM, records - `.agents/review/live-risk-sweep-2026-10-30.md` item 45.
**Status**: Fixed on master (records only, no code); awaiting the end-of-work review
**Branch**: none - record-only change made by the supervising session directly on master, 2026-11-06
**Commit**: see `git log --oneline -1 -- .agents/review/findings/sweep-45.md` | **Reviewer**: supervisor (records)

## Each sub-point, checked against the records as they stood on 2026-11-06

- **state.md "Master is deployable" / "Open: nothing marked [ ]" / kd-88 listed twice.** Already gone: neither sentence
  is in `state.md` any more, and `index.md` has one kd-88 row (the second match the sweep saw is sweep-e-28's text).
  No change.
- **state.md credential note "to a directory-held group".** Already gone from `state.md`. No change.
- **"Labelled live assumptions" omits aud-a2ac/a2ad and Recipient Lookup's A4.** Added one line to the list naming all
  three and the recogniser they rest on (kd-89 restored it, sweep item 2).
- **Constitution line 80 vs the kd-44 reversal.** The carve-out lived only in `.agents/decisions.md` (2026-10-27, item
  5). The Constitution sentence now carries it as an exception: a change whose outcome could not be confirmed
  notifies administrators only. No code change; this records the ruling where the Constitution's own rule is read.
- **Constitution line 95 vs `ProtectedPrincipalEntryValidator.cs` (aud-a2e conflict).** The sentence said a cloud-only
  principal "must be protected by address, never by group membership", from the 2026-07 name-matched design. Since
  `docs/ProtectedPrincipalIdentifiers-Plan.md` (Implemented 2026-11-03) protected rows are on-premises SIDs and
  objectGUIDs and the check reads the target's on-premises SID (ruling 2026-11-03), so a cloud-only recipient cannot be
  named by any row; the validator says so when an entry is not found in AD. The sentence is rewritten to state that
  structural limit. This edits the highest-authority document to match an owner-approved plan, not to match code;
  flagged in `state.md` for the owner to confirm on return. The owner's 2026-10-26 ruling that nothing about protected
  principals is brought back for Conference Rooms is respected: this is not a Conference Rooms question.
- **Finding status lines "In progress" against index rows Closed/Reversed** (aud-a2ab, aud-a2z, aud-a2aa, aud-a2ac,
  kd-66): each status line now matches its index row, keeping the earlier text after "Earlier text:" for provenance.
  aud-a2ad had a finding file but no index row and a status that still read "awaiting merge" although `56cc4153` is on
  master; it now has an index row and a status naming the merge and the end-of-work review.

## Not done

- No code or test changed, so no guard proof. `git diff --check HEAD` clean; ASCII lint clean.
