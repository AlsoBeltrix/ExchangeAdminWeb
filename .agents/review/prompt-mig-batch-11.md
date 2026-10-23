# Defect review request: Migration denial targets, staged-action withdrawal, Recipient Lookup cancellation wording, bulk executor failure wording (kd-17, kd-13, kd-14, kd-15)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `9d9831b3` or later.
- The change is the range merged at `9d9831b3`: commits `a63b62bd` (kd-17), `b5f943c4` (kd-13), `e148826d` (kd-14), `6b3aa310` (kd-15),
  `ee5caf05` (records), on top of `86953582`. Review `git diff 86953582 ee5caf05 -- . ':!.agents'` and read the COMBINED
  `Components/Pages/Migration.razor` (the bulk denial rows with the "N batch(es)" / "N mailbox(es)" target and the new `targets`
  array; `WithdrawStagedMailboxAction` and the three batch-change routes; both bulk executors' outer catch through
  `ResolveFailure`; the page went 4353 -> 4368 lines and `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` was re-anchored),
  `Services/MigrationService.cs` (`RecordDenial` with optional `targets`), `Services/RecipientLookupService.cs` (location and
  on-prem size catches letting cancellation through, wording through the aud-a23i helper, the credential seam keyed by
  purpose), and the tests `MigrationDenialTests.cs`, `MigrationMailboxSelectionTests.cs`, `MigrationBulkRunFailureTests.cs`,
  `MigrationStubExchange.cs`, `RecipientLookupFailureTests.cs`, `RecipientLookupOnPremArchiveTests.cs`.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network.
- Records: `.agents/review/findings/kd-17.md`, `kd-13.md`, `kd-14.md`, `kd-15.md`; parents `aud-a68.md`, `kd-2.md`; the
  mig-batch-10 result that raised kd-17 is `.agents/review/mig-batch-10.result.txt`. ALREADY admitted and out of scope - do
  not re-raise: `kd-33` (no audit row when the action itself throws), `kd-34` (raw ex.Message at nine more Migration sites),
  `aud-a67` (7) (the 9 s bounds). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md` Known Failure Classes (1, 2) and the
  ClickGateRegistry line-keying rule; `docs/ProjectConstitution.md` (audit).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat ee5caf05`. Report whether both succeeded.

## Mandate

Hunt for observable defects. kd-17: the `targets` array - bounded (a 10,000-row bulk denial writing 10,000 names into one
audit row?), the names used (identities or display names; any address that should not be in an audit row), and whether
`RecordDenial`'s other callers' rows changed shape; kd-13: `WithdrawStagedMailboxAction` on each of the three routes - any
route (Back, pasted link, row click, tick) that still carries the staged action, and the D2(a) decision to leave a staged
BATCH action in place because it re-plans at Confirm - is that true for every batch action, or can one act on the old
batch's plan; the re-anchoring - sample five registered Migration.razor lines against the file; kd-14: a vault timeout
still lands as a warning (the agent says Delinea rethrows only a requested cancellation) - is an operator cancel during the
credential read now surfaced as a cancel or swallowed as a warning, and does the purpose-keyed seam change what the
production credential service receives; kd-15: `ResolveFailure` on the outer catch - does it classify the pool's Stop
exception as a sign-in failure and never as a per-mailbox failure, and does the outer catch still complete the activity
and leave the busy state consistent; tests passing for the wrong reason (`MigrationStubExchange` throwing a shape the pool
never throws; a withdrawal test that would pass with no withdrawal because the selection was already empty).

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.