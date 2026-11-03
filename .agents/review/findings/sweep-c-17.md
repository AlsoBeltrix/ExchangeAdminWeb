# sweep-c-17: one stale or ambiguous protected-group entry no longer refuses every check unnamed (sweep item 17)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 17: since aud-a3b (ef0664e9) a protected-group
entry the directory could not bind to exactly one group - a deleted group's SID, a renamed/moved group's DN, a name two
domains share - marked every check incomplete, and an incomplete check with no other match refuses: every change in every
module that runs the check was refused, with "Group membership check was incomplete due to expansion errors", naming nothing.
**Status**: Fixed on branch; awaiting review. Rebuilt to `docs/ProtectedPrincipalIdentifiers-Plan.md` K.3 by ppi-s6; the
first build (`db0813b9`) is superseded on the branch before merge (decision 2026-11-03 "Identifier conversion is a one-time
command-line step, not a startup pass").
**Branch**: `fix/sweep-c` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-c`, from master `72611fc2`)
**Commit**: `db0813b9` (first build, superseded); the K.3 shape in `fix(ppi-s6): ...` (see `ppi-s6.md`) | **Reviewer**: implementer (candidate)

## Evidence

- At `72611fc2`: `ProtectedGroupMembership.Evaluate` -> `Resolve` -> `One(...)`: zero -> "no such group", two or more ->
  "{n} groups match", both `incomplete = true`; `ProtectedPrincipalService.CheckGroupMembershipAsync` refused
  `expansionHadErrors && matches.Count == 0` with the unnamed text.
- **Protected Group Targets** (`GroupTargets`, `objectGUID|DN`): checked only by `CheckWriteTarget`, directory-free, GUID first
  then DN (`ProtectedGroupTargetEntry.Matches`). A stale target entry cannot make any check incomplete. Nothing to fix there.

## What the first build did (superseded)

`db0813b9` isolated each entry inside the evaluator: a name several groups carry protected the members of each, a configured
entry naming no group protected nobody (`Result.Stale`, `Evaluate(configuredEntries)`), and the refusal named the entry. The
owner ruled that plumbing out (K.2): names are not to be resolved by the new build at all.

## The K.3 shape (as built by ppi-s6)

- **No name is resolved at check time.** The evaluator compares SIDs (and DNs its self-grant and self-service callers have just
  read). A stored Group row with no identifier companion that is not a SID is never looked up: the check is incomplete with
  "protected group '<label>': not converted - run db convert-identifiers". So "a name two domains share" cannot arise at check
  time; `db convert-identifiers` refuses such a row by name, writing nothing, until it is re-picked.
- **The banner names the entry** (kept from the first build): `ProtectedPrincipalService.IncompleteReason` -
  "Protected-principal check was incomplete - protected group '<label>': <problem>; .... Cannot confirm target is not
  protected." The unnamed text remains only when no cause was recorded.
- **A group_sid row no group carries any more** (a deleted group) and a read that fails are still incomplete, named, and refuse a
  check that found no other match: `Result.Stale` and `configuredEntries` are gone, as K.3 orders. The cure is to remove or
  re-pick the named entry.
- **Help.** Admin Settings "not in AD" tips and README rewritten to say the above (ppi-s6).

## Guard proof

See `ppi-s6.md`: `ASidEntryNamingNoGroup_IsIncomplete_AndNamed`, `ANameEntry_IsNeverLookedUp_ItIsIncomplete_NotConverted`,
`AGroupSidRowWhoseLookupFails_StillFailsTheCheckClosed_NamingTheRow`, `ARowStoredByNameOnly_RefusesTheCheck_NamingIt_...`.
The first build's four guard tests were removed with the branches they pinned.

## Gates

Run once for the branch at its tip: see the ppi-s6 report. Not run: any live directory read.
