# aud-a5c: a domain-qualified lockout target still resolves to the wrong domain's account

**Severity**: HIGH - it is the root `aud-a5` aimed at and missed. A5 made the session match
exact; this makes the exact match land on the wrong person's SID, so the end result is
unchanged: the wrong account is signed out, and the protected-principal check was made against
the wrong account too.
**Status**: DEFERRED by owner ruling 2026-10-19 - module parked; see Closeout
**Branch**: -
**Commit**: -

## Evidence

Raised by codex-commercial (gpt-6-astra, max) reviewing `cf3ed28`, as finding 4, and explicitly
labelled an existing defect that fix left unresolved.

Two steps, both in `Services/AccountLockoutRemediationService.cs`:

1. `NormalizeUser` takes `LastIndexOf('\\')` and keeps the tail, so an operator typing
   `A\alex` into a scoped sweep becomes `alex`. The domain they supplied is discarded.
2. The resolver then searches by that bare name with no `-Server` and no forest-wide search
   (`Services/ProtectedPrincipalService.cs:563`). `Get-ADUser` is documented to default to the
   domain of the computer running it.

So on an application host in domain B, `A\alex` resolves to `B\alex`. `aud-a5` then reads
**B's** SID correctly, from B's own domain, and matches B's session exactly. Precision applied
to the wrong selection.

Reading from the domain that owns the already-chosen DN - which `aud-a5` does, and which
`aud-a3` established as the rule - cannot repair a selection that was already wrong.

**The discovery path throws away the answer.** Event 4740 carries `TargetSid`, the locked-out
account's SID, in its event data. `ReadLockoutEvents` extracts `TargetUserName` and the caller
computer and ignores it. `aud-a5`'s own record claimed "the lockout event gives a bare name and
no domain, so the event stream cannot supply it" - that is wrong, and this finding corrects it.

## Predicted observable failure

Two domains in one forest each have an `alex`. An operator types `A\alex`, or clicks through
from a 4740 event for `A\alex`. The app resolves `B\alex`, checks protection against `B\alex` -
so a protected `A\alex` is never seen as protected - and signs `B\alex` out of their session.
Every audit row names `B\alex`, so the trail is internally consistent and wrong.

## Approach - NOT BUILT

Two halves, and the first is most of the value:

1. **Discovery: take the SID from the event.** `TargetSid` is in 4740's data. Carry it on
   `AccountLockoutEventRow` and use it directly, skipping name resolution entirely for the
   path that produces most targets. Verify the property name and position against a real event
   rather than assuming the index - **this needs an owner go for an AD read**, per the standing
   instruction not to probe Active Directory without one.
2. **Typed input: stop discarding the domain.** Where an operator supplies `A\alex` or a UPN,
   resolve it as the qualified identity they gave - not by stripping it. Where they supply a
   bare name that is ambiguous across the forest, **refuse and say so**. That is the owner's
   standing ruling: "the onus is on the user to provide accurate data. If they do not, they
   need to know that and resolve it themselves. We do not guess."

`NormalizeUser` is used for event matching as well as targeting, so changing it is not a
one-liner: the matching side wants a bare name, the targeting side must not have one. Separate
the two rather than retuning the shared helper.

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19, finding 4 of the `cf3ed28` review.

## Closeout

Not started.

**DEFERRED 2026-10-19 by owner ruling** (`.agents/decisions.md`): Account Lockout
Remediation has never been used and is parked, left registered and disabled. This finding is
not abandoned and stays accurate as handover - it is simply not to be worked. If the module is
ever enabled, this is live again.
