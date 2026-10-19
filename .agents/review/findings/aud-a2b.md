# aud-a2b: Three remaining ways a protected mailbox can be reached

**Severity**: HIGH - each lets an operation proceed against a mailbox the configuration
protects.
**Status**: Open - not approved for implementation
**Branch**: -
**Commit**: -

## Evidence

Raised by codex-commercial / `gpt-6-astra` / max while reviewing `efa08c5` (`aud-a2`). All
three are **pre-existing**. `aud-a2` fixed which identity is compared; these are about the
comparison reaching the wrong object, or not happening at all. Filed together because they
share one root: **nothing establishes that the object checked and the object written are the
same object.**

The fourth finding of that review was a regression `aud-a2` introduced and is fixed in its
repair commit, not carried here.

### 1. Legacy excluded-users does string-only comparison after a failed lookup

`Services/PermissionValidator.cs:94`, `:425`. Without central configuration, protection falls
back to `MailboxPermissions/ExcludedUsers`. If object-ID resolution fails during
initialisation, the incomplete cache is accepted with `_initFailed = false`. With no IDs
obtained, later target checks degrade to string comparison - so a secondary alias passes, for
up to the 30-minute cache lifetime, including after the directory has recovered. A failed
target-ID lookup also falls through to "not excluded".

**This contradicts `aud-a2`'s own comment that the directory is "always asked"** - true of the
central path, false of this one.

### 2. An AD match does not identify the Exchange write target

`Services/ProtectedPrincipalService.cs:440`, `:563`, `Services/MailboxPermissionService.cs:52`.
A typed name can be the `sAMAccountName` of an unprotected, non-mail-enabled AD account **and**
the Exchange alias of a protected mailbox. AD answers first and returns the unrelated account;
a `Resolved` status stops Exchange being consulted. The check passes. The write then hands the
original string to Exchange, which resolves aliases, and lands on the protected mailbox.

### 3. Unknown sync metadata is treated as permission to skip rules

`Services/ExchangeIdentityResolver.cs:114`, `Services/ProtectedPrincipalService.cs:535`. When
AD misses an alias and Exchange returns missing or unparseable `IsDirSynced`, the mapper
reports cloud-only. The resulting principal has no DN and substitutes primary SMTP for UPN, so
a genuinely synced mailbox can miss protection configured by its distinct UPN or by on-premises
membership. The comments calling this "conservative" are wrong - unknown metadata produces the
permissive result.

## Predicted observable failure

A non-servicer grants themselves access to a protected VIP mailbox by typing a name that AD
resolves to a harmless account and Exchange resolves to the VIP (hole 2). Or, during a
directory wobble, by typing any alias while the excluded-user cache holds no object IDs
(hole 1).

## Approach - not designed

The unifying requirement: **the identity that passes the check must be the identity that is
written**, and it must be established by whichever directory will perform the write. Options
to weigh:

- Resolve against Exchange for Exchange-targeted operations rather than preferring AD, or
  require both to agree before accepting `Resolved`.
- Carry the resolved identity forward into the write instead of re-passing the typed string,
  so the two cannot diverge.
- Fail closed on unknown `IsDirSynced` rather than defaulting to cloud-only.
- For hole 1, treat an incomplete excluded-user cache as unavailable rather than usable.

## Also recorded - operator-facing diagnostics that name the wrong cause

Not security defects; they send support in the wrong direction.

- `Services/PermissionValidator.cs:199` still says Group/OU/Pattern rules are configured when
  an address-only configuration fails to resolve.
- `:206` says neither directory has the recipient, which is false when Exchange found a synced
  recipient and the follow-up canonical-address AD lookup returned `NotFound`
  (`ProtectedPrincipalService.cs:501`). The denial is correct; the explanation is not.

## Known gaps

Source-level traces, not live reproductions. The reviewer said so explicitly and I have not
reproduced them either - **probing Active Directory or the live tenant is forbidden without a
direct owner go.** Hole 2 in particular depends on how this tenant's aliases and
`sAMAccountName`s actually overlap, which only the real directory can answer.

## Reviewer comments

`Reviewer: codex-commercial / gpt-6-astra / max`, reviewing `efa08c5`. Findings 3, 4 and 5 of
5, plus the diagnostics note. Raw output: `.agents/review/out/aud-a2.txt` (machine-local,
gitignored).

Finding 2 of that review - Out of Office checking one mailbox and writing another - is **not**
recorded here: it is already planned as `A7` in `docs/RepoAudit-Remediation-Plan.md`, and the
reviewer reached it independently.

## Closeout

Pending owner approval. Recorded under the 2026-10-19 ruling that everything needing
remediation gets planned and fixed.
