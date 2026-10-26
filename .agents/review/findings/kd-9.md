# kd-9: ClickGateRegistry.cs still says tiers 2-4 are "audited, NOT approved for conversion"

**Severity**: LOW - a stale statement in a code file. `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` about line 75 says tiers 2-4 are audited and not approved for conversion; the owner approved them 2026-10-12 (`.agents/decisions.md`). It is a code file, so correcting it is a code change, which is why it sat in `.agents/state.md` Blockers.
**Status**: Verified on branch; awaiting merge and codex review
**Branch**: `fix/kd-misc` (worktree `D:\source\ExchangeAdminWeb-wt\kd-misc`, from master `eb017102`)
**Commit**: fix `14edf320` (comments and unread strings; no module version)
**Reviewer**: implementer (recorded in `.agents/state.md` Blockers, re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. No behaviour; a wrong comment that steers the next agent away from approved work.

## Approach

Re-read at `eb017102`. `ExchangeAdminWeb.Tests/ClickGateRegistry.cs`, `NotYetConverted`:

- `:55-60` - "Approved for conversion, in the order of docs/ClickGatingAudit-Plan.md Revision 1. All nine tier-1 pages
  ... are converted and live in Pages above. Nothing below this line is approved for conversion without an owner go."
- `:75` - "// Tiers 2-4: audited, NOT approved for conversion. Do not convert without an owner go."
- `:70`, `:72`, `:76-92` - the nineteen tier 2-4 entries' reason strings, each "tier N, not approved".

The ruling: `.agents/decisions.md` 2026-10-12, ruling 3, "Queue 9, click-gating: tiers 2, 3 and 4 are APPROVED. The
remaining 19 pages."; `docs/ClickGatingAudit-Plan.md`'s Status header says the same ("TIERS 2, 3 AND 4 ARE NOW
APPROVED - owner 2026-10-12 ... and are NOT STARTED").

What changes: the two comments are reworded to that ruling and point at the plan; the nineteen reason strings read
"tier N, approved 2026-10-12, not started" (BlockedSenders, MessageTrace and RiskyUsers keep the rest of their text).
The strings are data rather than comments, so this goes one step past the record's "comment" wording, deliberately:
they make the same stale claim, and no assertion reads them - `ClickGateTests` reads only `NotYetConverted.Keys` and
`ContainsKey` (`ExchangeAdminWeb.Tests/ClickGateTests.cs:68`, `:87`) - so the change is still behaviour-free. The
nineteen pages, their tiers and the dictionary's keys are unchanged.

Declared files: `ExchangeAdminWeb.Tests/ClickGateRegistry.cs`, this record.

Mutation: none possible; there is no behaviour to guard (below).

## Files changed

- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` - in `NotYetConverted`: the tier-1 comment (it said nothing below it was
  approved), the tiers 2-4 comment, the nineteen tier 2-4 reason strings ("tier N, approved 2026-10-12, not started"), and
  one tense in the CloudPasswordReset comment ("conversion was then the nine-page tier-1 scope ... this page is in none
  of its four audited tiers" - the page landed after the audit, so the 2026-10-12 approval of the audited tiers does not
  name it either). Keys, tiers and every other entry unchanged.
- This record.

The diff, in substance (see the commit for the exact text):

```
- // Approved for conversion, in the order of docs/ClickGatingAudit-Plan.md Revision 1. ... Nothing below this
- // line is approved for conversion without an owner go.
+ // Tier 1, approved 2026-09-18 and converted in the order of docs/ClickGatingAudit-Plan.md Revision 1: ...
+ // Tiers 2, 3 and 4 below - the remaining nineteen pages - were approved by the owner on 2026-10-12
+ // (.agents/decisions.md; the plan's Status header) and are not started; read the plan's Revision 1 before converting one.
- // Tiers 2-4: audited, NOT approved for conversion. Do not convert without an owner go.
+ // Tiers 2-4: audited, and APPROVED for conversion (owner, 2026-10-12; docs/ClickGatingAudit-Plan.md).
+ // Not started. A page leaves this list when its conversion lands with its entry in Pages.
- ["ADAttributeEditor.razor"] = "tier 2, not approved",            (and 18 more like it)
+ ["ADAttributeEditor.razor"] = "tier 2, approved 2026-10-12, not started",
```

## Guard proof

Not applicable: no behaviour changed, so there is nothing a test could bite on. The reason strings are never read by an
assertion (`ClickGateTests` reads `NotYetConverted.Keys` and `ContainsKey` only, `:68` and `:87`), and no script reads
them (`tools/`, `tests/ps` checked). Run to show nothing moved: `FullyQualifiedName~ClickGate` 317 passed, and the full
suite at `14edf320` 6720 passed / 0 failed.

## Coder dispute

None.

## Known gaps

- `docs/ClickGatingAudit-Plan.md`'s "Pending decisions" section (`:332-338`) and Revision 1's re-scope section ("Tiers 2,
  3 and 4 remain unapproved.", `:619`) still read as they did before the 2026-10-12 ruling; its Status header says the
  ruling supersedes them and the document keeps its history sections as written. Docs, outside this finding.

## Reviewer comments

None yet (codex review pending, dispatched by the supervising session).

## Closeout

Pending: merge into master, codex review, branch deletion. Branch-tip gates at `d79e1b79` (the branch's last code
commit): Release build 0 errors / 19 warnings (all pre-existing, none in a changed file);
`dotnet test ExchangeAdminWeb.slnx -- xUnit.MaxParallelThreads=4` 6720 passed / 0 failed / 0 skipped;
`dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`, `git diff --check HEAD` and
`tools/Test-AsciiOnly.ps1` clean. PSScriptAnalyzer and Pester not run: no PowerShell file changed.
