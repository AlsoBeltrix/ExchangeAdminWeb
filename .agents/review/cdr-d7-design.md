# D2 / D7 implementation guidance - bounded enrichment

Native `/root/design_defender_deadline`, `gpt-6-astra` at `max`, provided read-only
design advice on 2026-10-15. These are implementation choices within approved Revision 6,
not new owner rulings. No edits, tests or live queries occurred in the design pass.
Re-read symbols when implementing; the recorded line references were at `13e47f7`.

## Dependency and per-call results

Implement D2 immediately before D7, after D4, in its own finding commit. D7 needs
per-call failure/stop data; retaining a shared diagnostic field or creating a second mutable
stop field would preserve the race. This changes agent-facing sequencing only; the approved
scope and executive summary remain unchanged.

D2 should replace `_lastBatchFailureReason` and Boolean returns with a small immutable
`HuntingBatchResult(bool Succeeded, string Reason)` for both posture and observer calls.
Keep aggregation local to GetDiscoveryEnrichmentAsync and retain singleton registration.
The design pass found only dependency/immutable test-configuration fields besides the
diagnostic field; D2 must verify the sweep and prove concurrent ListDevicesAsync isolation.
D7 can extend the result with a systemic-stop indicator without another structural rewrite.

## One deadline, all enrichment stages

Start one deadline in ListDevicesAsync after inventory completion, before hunting setup.
It covers credential creation, token acquisition, posture, observers and site lookup.
Use a centrally named four-minute default, matching the existing single-request ceiling,
with an internal clock/budget seam for deterministic tests. Never restart the budget per
batch or stage. A hunting authorization failure stops hunting; site lookup may still use
the remaining time. A deadline must return the already-fetched inventory with unavailable
enrichment, preserving successful values and current degraded-filter behavior.

Propagate the same CancellationToken through actual I/O using optional parameters so
unaffected callers retain their semantics:

- Hunting client factory and BuildClientAsync into DelineaService.GetSecretFieldsAsync.
- Delinea token-lock wait, auth request/retry, secret request/forced-refresh retry and
  response reads. Its blanket catch must propagate cancellation requested by that token.
- DefenderApiClient token-lock wait, token request, API request and response reads. Preserve
  its existing TimedOut result for an independent HTTP timeout, while propagating requested
  cancellation to the enrichment orchestration boundary.

Do not only apply WaitAsync to HTTP setup and leave unobserved, uncancelled network work.
Stop known systemic credential/authentication setup failures and HTTP 401/403 from either
hunting pass. Continue existing handling of isolated 429/5xx, malformed batches and
observer-specific 400 responses. Do not classify arbitrary exception messages.

Mark unprocessed posture batches unavailable. If posture succeeded before its observer
stopped, keep posture values and mark only the observer unavailable. Successful empty
responses remain distinct from failed ones. If counts include unattempted batches, revise
the existing TotalBatches "attempted" and page "lookups failed" wording accordingly.

Carry observer/stop diagnostics separately: on the final observer's deadline, every posture
batch can still be successful, and the existing Succeeded factory clears Reason. A deadline
must not disappear from the page merely because posture succeeded. Retain row-level
unavailable cells and existing Site/Seen-by filter escape behavior.

## Site worker ownership and the cancellation limit

ApplySites currently calls synchronous AdSiteDirectory.ReadSubnets, which uses the lazy
PowerShellDirectoryCommands runspace Open and Invoke. None has an independent bound.
Ignoring that stage would leave the already-fetched inventory waiting indefinitely.

Add an opt-in asynchronous site path accepting the same deadline token. Keep synchronous
section-access callers unchanged. One worker owns its entire commands/runspace lifetime;
allow at most one active worker per AdSiteDirectory instance. Admission waits consume the
caller's remaining deadline. The worker returns subnet data only, never mutating device
rows after the caller has returned.

On cancellation, request a nonblocking PowerShell stop, prevent subsequent commands and
stop awaiting the site result. If native shutdown stalls, the worker retains ownership,
observes eventual completion/failure and disposes later; admission remains occupied until
cleanup finishes. Repeated searches must not accumulate abandoned tasks/runspaces.

This bounds the wait before returning inventory and requests cooperative native cancellation.
It does not prove that physical AD activity ends at the deadline. Installed PowerShell
reference documentation identifies Stop as synchronous, StopAsync as a stop request whose
completion can still wait, and CloseAsync as invalid while Opening. Awaiting stop/cleanup
or disposing on the response cancellation path can undo the bound. Do not claim a hard
native-operation cutoff or introduce a process rewrite or privilege change.

## Verification and version scope

Use controlled continuations and a test clock/budget, not timing races, for:

- One shared deadline across setup, batches, observers and site admission; no timer reset.
- Token propagation through credential retries and API token waits; no later request.
- Posture/observer 401/403 early stop; isolated failures retain successful batches.
- Final-observer deadline reason, exact unavailable coverage and retained inventory.
- Stalled site opening, invocation and cleanup: inventory returns, no next command runs,
  and a second search cannot create another worker.
- Two concurrent ListDevicesAsync calls retain their own reasons (D2).

Mutation-prove the guards, including missing token propagation, restarted timer, removed
systemic stop, missing skipped-row marking, lost observer reason, bypassed worker admission
and the shared-field race. Required full per-slice gates still apply.

D2 bumps Defender. D7 bumps both Defender and the base app because its cancellation paths
touch shared Delinea and directory execution. DefenderApiClient is also used by
CloudSignInService despite its module-local comment; preserve and test those callers.
Page banner changes must follow ClickGate re-anchoring and ProgressRegistry guidance.
