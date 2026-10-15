# Optional Exchange command replay - Plan

Status: **APPROVED - owner 2026-10-15: "Approve the additional slice (Recommended)".**

## Exec summary

**What this does.** Fixes one confirmed path where a failed calendar-permission change can
run again during the next operation. The broader audit found that many similar-looking
calls already discard or clean up their failed command objects.

**What it gets you.** A failed optional Exchange operation cannot leave work queued for the
next operation, while existing error reporting and retry rules keep their meaning.

**Cost and risk.** One additional implementation slice, with regression proof, the full local
gate and independent review. The main risk is changing how the shared runner handles
optional errors or connection failures; the tests and review must cover those contracts.

**What approval authorizes.** Implement this one confirmed replay fix at the next committed
slice boundary, then continue the already-approved Comms-10k/Defender work. Use fresh Sol 6.1
at max and independent Astra at max. This does not authorize a mechanical rewrite of every
PowerShell call, the separate Licensing/DHCP diagnostic fixes, a push, deployment or live probe.

## Evidence and scope

Finding: `.agents/review/findings/psl-1.md`. The read-only audit and complete invocation
inventory are in `.agents/review/powershell-replay-audit.md`, pinned to `b936c604`.
InvokeOptional leaves failed commands queued; Conference Rooms catches and continues using
the same instance. Reproduce that path before repair. Similar lexical patterns with disposal
or explicit cleanup are not admitted defects and are outside this change.

Proposed production scope: `Services/ExchangeServiceBase.cs`, plus the smallest Conference
Rooms test seam only if required to exercise its actual sequence. Extend existing
`ExchangeServiceBaseInvokeTests` and add sequence coverage in the existing test project.
No generic app-wide abstraction, SDK upgrade, retries added to multi-write operations,
unrelated module changes or script edits are planned.

## Preserved contracts

- Successful InvokeOptional returns its output and deliberately consumes optional
  non-terminating errors, as it does today.
- Classify connection/precheck errors before clearing their evidence.
- On terminating failure, preserve diagnostics for RunAsync through PsErrorsDataKey,
  then rethrow the original exception with throw, without wrapping or replacing it.
- Commands and errors are cleared on every exit. Do not clear another operation's data
  before its intended consumer has inspected it.
- Preserve existing tracker classification, pool lifecycle and retry eligibility.

## Implementation and proof

1. Verify the pinned finding against current code; declare the final files in psl-1.
2. Repair InvokeOptional locally with unconditional cleanup and the preserved contracts.
3. Add harmless real-SDK tests for success, non-terminating errors, terminating errors,
   subsequent-call isolation, original exception/diagnostic preservation and classification.
4. Exercise the actual Conference Rooms permission sequence using local functions named
   Add-MailboxFolderPermission and Set-MailboxFolderPermission. The first add records its
   simulated effect and then throws once. It must execute once; later users must receive
   only their intended commands and the first failure must remain reported.
5. Restore the old helper and prove duplicate/wrong calls through compiled assertion
   failures. Separately prove error cleanup and classification-before-clear guards. Restore
   exact bytes, verify hashes and advance mtime; no stale-binary or compile-failure proof.
6. Bump the base app for the shared helper. Bump Conference Rooms too only if its module
   behavior is changed directly; do not mechanically bump every helper consumer.
7. Run the restored Release solution build/full tests, format verification, diff check and
   applicable staged ASCII lint. No Pester/analyzer requirement unless a script changes.
8. Commit one finding with its proof, state/index/plan/token records and Constitution review
   packet. Independent Astra review must trace actual callers, error handling and pool cleanup.

Use the existing Microsoft.PowerShell.SDK 7.6.1 and local stand-ins only; no AD/EXO module
import, credentials, backend traffic or live acceptance is part of this plan.

## Approval and implementation status

The owner approved this additional slice at the next safe committed boundary, using the
Sol/Astra arrangement in the executive summary. C4's review correction is committed at
`6d061fdc66837d5be9f674a36b0f44b8441c723e`; implement and review psl-1 next, then resume C11.
The original twenty findings retain their separate approval and ordering.
The Licensing and DHCP diagnostic issues, psl-2/psl-3, are recorded separately and excluded.
Implementation is next. Branch policy remains direct master; push requires separate
authority, and any work-branch closeout must follow the git playbook if that policy changes.
