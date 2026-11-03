# sweep-c-17: one stale or ambiguous protected-group entry no longer refuses every protected check (sweep item 17)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 17: since aud-a3b (ef0664e9) a protected-group
entry the directory could not bind to exactly one group - a deleted group's SID, a renamed/moved group's DN, a name two
domains share - marked every check incomplete, and an incomplete check with no other match refuses: every change in every
module that runs the check was refused, with "Group membership check was incomplete due to expansion errors", naming nothing.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-c` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-c`, from master `72611fc2`)
**Commit**: see `git log master..fix/sweep-c` (`fix(sweep-c, item 17): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- At `72611fc2`: `ProtectedGroupMembership.Evaluate` -> `Resolve` -> `One(...)`: zero -> "no such group", two or more ->
  "{n} groups match", both `incomplete = true`; `ProtectedPrincipalService.CheckGroupMembershipAsync` refused
  `expansionHadErrors && matches.Count == 0` with the unnamed text. Plan B.2/B.5 (`docs/ProtectedPrincipalIdentifiers-Plan.md`)
  kept unmigrated Group rows "enforced as today", and the S2 page help said such an entry "fails every check closed".
- **Protected Group Targets** (`GroupTargets`, `objectGUID|DN`): checked only by `CheckWriteTarget`, directory-free, GUID first
  then DN (`ProtectedGroupTargetEntry.Matches`). A stale target entry cannot make any check incomplete; a renamed or moved
  group still matches by GUID; a deleted one protects nothing, as the page's "not in AD" tip already says. Nothing to fix
  there - the sweep's "Protected Group Targets" names the protected **Groups** list, whose lines it cites.

## Approach (as built)

- **Per-entry isolation in the evaluator.** `ProtectedGroupMembership.Candidates` returns every group an entry names
  (`Resolve` keeps exactly-one for the identifier pass). `Evaluate(..., configuredEntries)`:
  - several groups -> the members of each are protected (the fail-closed reading; a superset of the pre-aud-a3b host-domain
    lookup), logged at Warning with their DNs;
  - none, for a configured entry -> protects nobody (no one can be in a group that does not exist), named in `Result.Stale`
    and a Warning on every check; the check goes on without it;
  - none, for a group the caller just read (self-grant, self-service deny trustees: `configuredEntries` false) -> still
    incomplete: the read raced a change;
  - a read that throws, a domain the forest lacks, a DN naming no domain -> still incomplete, now with the entry in
    `Result.Problems`.
- **Writes to that group.** For a protected-group entry the group itself is protected by the same comparison (the target's
  own SID); an entry naming no group has no group a write could target - a group that exists under that DN, SID or name
  would have been found.
- **Banner names the entry.** `ProtectedPrincipalService.IncompleteReason`: "Group membership check was incomplete -
  protected group '<label>': <problem>; .... Cannot confirm target is not protected." (labels as the page shows them, via
  `LabelGroupProblems`); the old text remains only when no cause was recorded.
- **Help (Known Failure Class 5).** Admin Settings "not in AD" Group tip and "not converted" Group tip rewritten (same line
  count; Admin Settings has no ClickGate line entries); README Protected Principals paragraph.
- **Versions.** None: the evaluator is shared (base 2.36.0 undeployed, no bump); the Admin Settings change is help text.

## Where the code goes beside the plan's and the sweep's text

- **This changes behaviour the approved plan's B.2/B.5 and S2's help described** ("an unresolvable Group row ... fails every
  check closed"). B.2's reason was not to make unmigrated rows *inert* during an old-build save or a directory blip; they are
  still evaluated on every check, and a blip (a read that throws) still refuses. Only the directory's definite answer "no
  such group" now protects nobody - which is what the build before the October weekend did (a DN passed unread matched
  nobody). Supervisor: the owner may want to see this one line.
- A foreign-forest SID reads as "no such group" at this forest's catalog and so protects nobody; the check could never read
  a foreign forest's membership, so no protection the pre-weekend build gave is lost.

## Guard proof

| Revert | Failing test |
|---|---|
| M1 a configured stale entry back to incomplete | `AConfiguredEntryNamingNoGroup_ProtectsNobody_IsNamed_AndTheCheckIsComplete` (3), `AConfiguredStaleEntry_DoesNotCancelAMatchOnAnotherEntry`, `AnUnmigratedGroupRowNamingNoGroup_ProtectsNobody_BeforeAndAfterAPass_AndIsLeftWithItsReason` |
| M2 several groups back to incomplete | `BareName_SharedByTwoDomains_ProtectsTheMembersOfEach`, `BareName_SharedByTwoDomains_IsACompleteAnswer_ForATargetInNeither` |
| M3 refusal back to the unnamed text | `AGroupRowWhoseLookupFails_StillFailsTheCheckClosed_NamingTheRow` |
| M4 the service passes `configuredEntries: false` | `AnUnmigratedGroupRowNamingNoGroup_...` |

## Gates

Run once for the branch at its tip: see the sweep-c report. Not run: any live directory read.
