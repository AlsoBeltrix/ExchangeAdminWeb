# aud-a70: Permission pages - the on-premises success records are unguarded, and the README and Admin Settings hint texts do not name the directory-read credential dependency

**Severity**: LOW - recorded under the 2026-10-23 stopping rule, not dispatched. (1) `ExecuteOnPrem`'s
success records (Mailbox 717-718 and 723-724; Calendar 665-668) sit in no guard; since aud-a28q only a
missing writer can throw there, and when it does the catch says "The change was written, but a step after
the write failed..." with `Success=false` and records a Failed row beside the success rows. (2) `README.md`
526 ("Self-grant Prevention") does not say group grants now need the protected-principal directory-read
credential (aud-a60). (3) `Components/Pages/AdminSettings.razor` 282 and 289 (the credential's placeholder
and hint: "Required when groups or OUs are configured" / "Read-only AD credential for group membership and
OU checks") should say it is also needed for Mailbox and Calendar Permissions grants to groups.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; held by the 2026-10-23 stopping rule (LOW) for after the first dev deploy
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a60/a61/a53/a59/psl-9 agent, 2026-10-23, candidates 2-4 and its follow-up)

## Evidence

At `c89f639c`: `Components/Pages/MailboxPermissions.razor` 717-724; `Components/Pages/CalendarPermissions.razor`
665-668 (both line-keyed, edit in place); `README.md` 526; `Components/Pages/AdminSettings.razor` 282, 289.

## Approach (to be written by the implementing agent before code)

- The success records go through the aud-a53 guard (the result stands; a dropped row is said).
- The README section and the two hint texts name the credential dependency and what is refused without it.
- Mutations at the call site: one guard removed; the README sentence dropped (a source check).

## Guard proof

Pending.

## Also held (aud-a71/a69/a72 agent, 2026-10-23) - LOW

(4) `ExecuteOnPrem`'s failure row carries neither the servicer note nor the written identity (Mailbox 745-747,
Calendar 688-690). (5) `SubmitSingle` refusals before the write still return with no audit row: the
location-read failure (Mailbox 461 / Calendar 431), the "Unknown" location, and the on-prem authorization
refusal. (6) aud-a69 residual: without the directory-read credential, a grantee Exchange names is looked up
as the app pool; a name Exchange Online and the directory resolve to different objects, hidden from the app
pool, on the on-prem write path, would not be compared - refusing every grant without the credential would
close it (an owner call). (7) The operator's own lookups still run as the app pool; with a UPN-only sign-in
(which Negotiate never produces) a hidden account reads as "no account" and no group is checked. (8) A
dynamic group's members are as of Exchange's last calculation. (9) Latent: an `activity.Complete` that
throws inside `SubmitSingle`'s catch still escapes the handler (the result and the audit row are set first).
