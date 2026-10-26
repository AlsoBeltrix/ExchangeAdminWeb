# Defect review request: the activity's cancellation token runs from the Recipient Lookup page to the vault (kd-37)

You are reviewing, not implementing. Do not modify any file.

## Coordinates

- Repository: `D:\source\ExchangeAdminWeb` (your working directory). Branch `master`, head `9ffc653c` or later.
- The change is the range merged at `9ffc653c`: commits `9bcb5e3c` (kd-37) and `f4fd3800` (record), on top of `f9fa2733`.
  Review `git diff f9fa2733 f4fd3800 -- . ':!.agents'` and read the COMBINED `Components/Pages/RecipientLookup.razor`
  (passing `activity.CancellationToken`), `Services/RecipientLookupService.cs` (`GetRecipientInfoAsync`),
  `Services/ExchangeServiceBase.cs` (the location and on-premises size reads and their two `GetModuleCredentialsAsync`
  calls), `Services/ModuleCredentialService.cs` (both methods and its `DelineaService` call), `Services/DelineaService.cs`,
  and the tests `RecipientLookupCancellationTests.cs` (new) plus the existing stand-ins the new optional parameters
  touched. The agent also fixed twenty xUnit1051 warnings and one CS9107 the new parameters raised.
- You may read any file and run read-only git commands. One command per call. No writes, no builds, no network, no vault.
- Records: `.agents/review/findings/kd-37.md`; parents `kd-14.md`, `aud-a68.md`; the mig-batch-11 result that raised
  kd-37 is `.agents/review/mig-batch-11.result.txt`. ALREADY admitted and out of scope - do not re-raise: `kd-57` (the
  wording and result classification of a cancelled lookup), `kd-58` (the Exchange Online reads take no token at all),
  `kd-59` (the other modules' credential fetches pass no token). Repo rules: `AGENTS.md`; `.agents/repo-guidance.md`
  Known Failure Classes (1, 2); `docs/ProjectConstitution.md` (audit, credential isolation).

## Capability proof

Before anything else, read `AGENTS.md` and run `git show --stat 9bcb5e3c`. Report whether both succeeded.

## Mandate

Hunt for observable defects. The seven hops: name each signature and confirm the token actually reaches the vault rather
than stopping at a default parameter - any hop that takes a token and then calls the next without it; the warning-versus-cancel
rule (kd-14): an unasked vault timeout stays a warning while a requested cancel throws - is that distinction made by the
token's own state rather than by exception type alone, and can a vault timeout that happens to coincide with a cancel be
misreported either way; the optional parameters: does any PRODUCTION caller now pass `default` where it holds a real
token, and does any path capture the token once and reuse it after the activity has completed; credential isolation -
does a cancellation path leave a secret, a connection or a disposable unreleased (`DelineaService`'s HTTP client, the
credential object itself), and is anything secret now visible in a cancellation message or an audit row; the activity
and audit on cancel: written exactly once, the page left in a consistent state, no half-rendered result; Known Failure
Class 1 on the cancellation path (a state write that survives the cancel); and the tests - the stand-in vault waits for
a cancel its caller will issue, every wait bounded at 60 s, five mutation rounds: does the suite still fail if the token
is dropped at a hop the rounds did not cover, and does any new test assert on timing rather than on the cancellation.

## Result contract

Either "clean" with the reasons you looked and found nothing, or candidate findings. For each: title; file and line;
triggering input or state; predicted observable failure; severity (LOW / MEDIUM / HIGH / CRITICAL) tied to impact; a
better approach. No style preferences, no out-of-scope work.