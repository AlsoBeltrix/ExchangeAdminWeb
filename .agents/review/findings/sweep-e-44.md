# sweep-e-44: the audit result field gained two values and refusals are filed six ways, undocumented (sweep item 44, documentation half)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 44: `result` gained "Cancelled"
(`AuditService.cs:26`/`:32`) and "Undetermined" (`:171`, and `ResultText`) beside Success, Failed and Ineligible;
decisions.md treats audit fields as a Splunk interface with a closed list; a Splunk search on `result="Failed"` misses
writes that may have landed and cancels; one refusal is filed six different ways.
**Status**: Fixed on branch (documentation half); awaiting review. The convention itself is open for the owner.
**Branch**: `fix/sweep-e` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-e`, from master `54dcfb40`)
**Commit**: `0a491cb3` (`fix(sweep-e, item 44): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built) - documentation only, as briefed

- README, Audit Logging: the closed list of `result` values (Success, Failed, Undetermined, Cancelled, and Ineligible for
  Migration eligibility checks only), each with its meaning, read from the code that writes them (`AuditService` result
  switches and `ResultText`, `FiledCancelled` at its call sites, Blocked Senders' `FiledUndetermined`); the Splunk
  consequence (`result="Failed"` misses Undetermined and Cancelled; search `result!="Success"`); the six refusal shapes in
  use today, with examples, including the refusal fields sweep-e item 41 added to two `*_Denied` rows and to Migration's
  ticket denials; and a search that finds every refusal. "`error` appears only on failure" corrected to "on every result
  but Success".
- No action name and no result value changed.

## Open for the owner (to ask separately, one decision at a time)

- **Adopt one refusal convention?** Context: a refusal is filed in six shapes (README lists them); a Splunk query for
  refusals must OR three patterns and still cannot tell shape 5 (own action, Failed, error text only) from a failed write.
  Action: pick one - for example, the operation's own action, `result` Failed, with `refusedAt` and `mutationAttempted`
  false on every refusal - and migrate the other shapes. Consequence: existing Splunk searches on `*_Denied` /
  `*_Refused` action names would stop matching new rows; dashboards need updating before the deploy that lands it.
  Recommendation: adopt it, keep the old action names for one release beside the new fields if the Splunk owner needs a
  transition. The Splunk owner should be shown the README section either way.

## Guard proof

None: documentation only, no behaviour change. `git diff --check` clean.

## Gates

Run once for the branch at its tip; see the sweep-e hand-back.
