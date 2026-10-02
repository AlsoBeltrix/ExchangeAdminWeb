# gps-2: Four changed module pages still render their pre-sweep version

**Severity**: LOW - cosmetic/bookkeeping. No behaviour is wrong; the module-version signal the
repo relies on for screenshots and operator reports is stale on four pages.
**Status**: Verified
**Branch**: - (direct-to-main)
**Commit**: see Closeout

## Evidence
`Modules/ModuleCatalog.cs:133` and siblings. Commit `e5f9e5c` removed the authorization spinner
from `ExchangeOnlineConfig.razor`, `AdminSettings.razor`, `AdminEventLog.razor` and
`AdminBulkJobs.razor`. All four render `<ModuleVersion />` and all four ARE catalog modules, but
their descriptors were left at `ExchangeOnline` 1.0.1, `AdminSettings` 1.2.0, `AdminEventLog`
1.2.1 and `AdminBulkJobs` 1.0.0.

Trigger: deploy the range and open any of the four pages after authorization passes.

Predicted observable failure: the page shows a version that predates its own visible change,
defeating the signal the Constitution's versioning rule exists to provide.

**Root cause, and it is mine, not the sweep's.** When I enumerated which swept pages were
modules I EXCLUDED these four by assumption - I classified them as "admin/system pages" without
checking the catalog. Three facts falsified that in one query: each has a descriptor, each has a
`Version`, each renders `<ModuleVersion />`. `ExchangeOnlineConfig.razor`'s descriptor id is
`ExchangeOnline`, not the page name, which is why a name-keyed lookup would also have missed it.
`Home.razor` and `ModuleConfig.razor` were correctly excluded - no descriptor, no
`<ModuleVersion />` - so the exclusion list was not wrong in principle, only unverified.

## Approach
Bumped the four descriptors: ExchangeOnline 1.0.1 -> 1.0.2, AdminSettings 1.2.0 -> 1.2.1,
AdminEventLog 1.2.1 -> 1.2.2, AdminBulkJobs 1.0.0 -> 1.0.1. Base app version unchanged - it was
already bumped to 2.27.0 for the frame change earlier in this range.

## Files changed
- `Modules/ModuleCatalog.cs`

## Guard proof
NO AUTOMATED GUARD, and adding one would be dishonest. "This page changed, so its module version
must move" is not expressible as a test: nothing in the repo can see a future diff, and a pinned
version assertion guards the opposite direction (an accidental bump), not a missing one.

Manual check instead: `grep -l ModuleVersion Components/Pages/*.razor` lists the pages carrying
the signal; each must have a catalog descriptor, and the four named above now read 1.0.2, 1.2.1,
1.2.2 and 1.0.1. Confirmed by reading `Modules/ModuleCatalog.cs` at lines 133, 1216, 1232, 1257.
Build clean and the 268 catalog/module tests pass, which proves no pinned assertion contradicts
the new values - it does not prove the bumps were owed.

## Coder dispute
None. Verified before accepting: all four descriptors exist with the cited old versions, all four
pages render `<ModuleVersion />`, and all four pages are in `e5f9e5c`'s diff.

## Known gaps
The real defect class - a page changing without its module version following - remains unguarded
here and everywhere else in the repo. Out of scope for a LOW finding; worth raising separately
if the owner wants it closed.

## Reviewer comments
Round 1 - Change review, candidate finding 2 of 2.
Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard
  harness: codex-cli 0.159.0 (cache recorded 0.154.0; validated in this dispatch)
  base ad3c1e958b29c2eabd43822e1eac3242c86610a7
  head e5f9e5ce38fce7ac35f0e439b7554fbe5b530658
  capability proof: read `.agents/repo-guidance.md` OK; ran `git log --oneline ad3c1e9..e5f9e5c` OK
No reviewer verification round: repo policy makes those CRITICAL-only and owner-gated
(`.agents/decisions.md` 2026-08-31). LOW closes on the coder-side proof above.

## Closeout
Fixed on master. Raw reviewer output: `.agents/review/gps-sweep.result.json`.
