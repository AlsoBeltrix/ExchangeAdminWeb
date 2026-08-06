# Protected-Principal Gap Fix Plan

Status: **Draft - owner approved the sequencing** (2026-08-06: fix these before the exec-support
servicer feature). Slices not yet started.

Two holes in the protected-principal control, both live in production. Found by review while
inventorying enforcement sites for `docs/ProtectedPrincipalBreakGlass-Plan.md`. Neither is a
regression from recent work; both have been there since the modules were written.

## GAP A - Blocked Senders performs an unchecked mutation

`Components/Pages/BlockedSenders.razor:273` calls
`BlockedSenderService.UnblockSenderAsync` (`Services/BlockedSenderService.cs:48-64`), which runs
`Remove-BlockedSenderAddress` against an operator-supplied address.

There is **no protected-principal check of any kind** on this path. The page re-authorizes the
OPERATOR at `:271` ("Reauthorized") and audits the outcome, which is why the omission is easy to
miss on a read: the module looks careful. What it never does is check the TARGET.

Unblocking a sender changes a principal's mail-flow state, so it is a mutating operation whose
target is a principal, and every other such module gates it.

**Fix:** resolve the address and run the protection check before the write, using the same shape as
the modules that do it correctly, and refuse with the standard message. Because the target here is
an SMTP address that may well be cloud-only, this must use the EXO-fallback resolver from the start
(see GAP B).

## GAP B - cloud-only principals are never checked in six places

`ProtectedPrincipalService.CheckAsync` does not resolve; it matches an already-resolved
`ResolvedDirectoryPrincipal` (`:131`). So protection depends entirely on how the caller resolved
the target.

Two resolvers exist:

- `ResolveWithExchangeFallbackAsync` (`:309`) - AD, then Exchange when AD says NotFound. Handles
  aliases and cloud-only recipients. **Correct.**
- `ResolveWithStatusAsync` (`:226`) / `ResolveDirectoryPrincipalAsync` (`:283`) - AD only. Returns
  NotFound for any cloud-only object, because a cloud-only object is in AD under no address.

A caller that resolves AD-only and proceeds on NotFound never calls `CheckAsync` with a real
principal, so **a cloud-only protected user row is never compared against the target**. The
protected row exists, the module reads it, and the target sails through.

**Measured split, regenerated from current code 2026-08-06** (codex's review found three of these;
there are six):

| Resolver | Site |
|---|---|
| Fallback - correct | `Services/ConferenceRoomProtectionGate.cs:67` |
| Fallback - correct | `Services/GroupManagementService.cs:49` |
| Fallback - correct | `Services/PermissionValidator.cs:124` (Mailbox / Calendar / OOF) |
| **AD-only - GAP** | `Components/Pages/Comms10k.razor:319` |
| **AD-only - GAP** | `Components/Pages/EmergencyDisable.razor:246` |
| **AD-only - GAP** | `Components/Pages/MfaReset.razor:252` |
| **AD-only - GAP** | `Services/AccountLockoutRemediationService.cs:421`, `:447` |
| **AD-only - GAP** | `Services/M365GroupManagementService.cs:183` |
| **AD-only - GAP** | `Services/MigrationService.cs:268` |

This is the same defect class as GAP 4 in `.agents/state.md` - the secondary-alias bypass closed on
2026-07-30 by routing resolution through Exchange. That fix was applied to the three gates above
and never propagated to the rest.

**Why it matters specifically for the exec-support work:** the whole point of that feature is
servicing VIP mailboxes. If a VIP is cloud-only, six modules currently treat them as unprotected
already - so the protection the new feature is meant to grant controlled access to does not
uniformly exist yet.

**MFA Reset is the sharpest case.** It is a Graph-based module operating on cloud identities, so
cloud-only targets are its normal input, and resolving them AD-only means the protection check is
close to inert there.

## Design

One change, applied six times: switch the AD-only resolution to `ResolveWithExchangeFallbackAsync`
and handle its four statuses the way the corrected gates already do.

The status handling is the part to get right, and it is already written down - copy
`PermissionValidator.ValidateTargetMailboxAsync` (`Services/PermissionValidator.cs:120-166`):

- `Resolved` -> run `CheckAsync`, refuse if protected.
- `Ambiguous` -> refuse; the identity matches several directory objects.
- `Unavailable` -> refuse; a directory that did not answer is not evidence of absence.
- `NotFound` -> both directories answered and neither has it. Only here may the caller proceed on
  the basis that there is nothing to protect - and each site must be re-read to confirm that is
  sound for its own operation.

**A cloud-only resolved principal has a null DN by design.** Group, OU and pattern rules are
evaluated from an on-prem DN, so they are inapplicable rather than skipped; the user rows still
apply. That is existing, deliberate behaviour
(`docs/ProtectedPrincipalResolution-Plan.md`) and needs no change here - but an implementer must
not "fix" the null DN.

## Slices

One module per commit, each independently revertible. GAP A first because it is a total absence
rather than a partial one.

1. **Blocked Senders** - add the check (GAP A), fallback resolver from the start.
2. **MFA Reset** - highest cloud-only exposure.
3. **M365 Group Management** - also cloud-native.
4. **Migration**.
5. **Account Lockout Remediation** - two call sites, both in one commit; note the module is
   currently disabled in this environment (`.agents/state.md`), so it is lowest urgency but must
   not be skipped or it becomes the one that is wrong when it is re-enabled.
6. **Comms-10k**.
7. **Emergency Disable** - uses the legacy `ResolveDirectoryPrincipalAsync` wrapper, which collapses
   NotFound and Unavailable into a bare null and so cannot distinguish "no such user" from "AD is
   down". Moving it to the fallback resolver fixes a second fail-open in the same edit.

## Verification

Per `.agents/repo-guidance.md`: build, `dotnet test ExchangeAdminWeb.slnx`, format check,
`git diff --check HEAD`.

Per slice, a test proving a **cloud-only protected principal is refused** by that module - the
exact case that passes today. Non-vacuity proven by reverting the resolver change and watching it
fail.

Live check on dev, once: pick a real cloud-only mailbox, add it as a protected user row, and
confirm each fixed module refuses it. Automated tests cannot prove the resolver reaches Exchange.

## Non-goals

- Changing what is protected, or the protection rules themselves.
- The exec-support servicer feature (`docs/ProtectedPrincipalBreakGlass-Plan.md`) - that follows
  this, per owner sequencing.
- Refactoring `CheckAsync` to resolve internally. Tempting, since the whole gap comes from callers
  choosing a resolver, but it changes a signature used by every enforcement site and would make
  this a wide change instead of six narrow ones. Worth considering later, on its own.
