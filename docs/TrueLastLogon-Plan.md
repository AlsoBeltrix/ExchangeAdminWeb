# True Last Logon -- One Person, Every Source

Status: **DRAFT, awaiting owner approval.** Queue item 17. Written against app `2.24.0`.

Replicates `C:\Users\mcoelho\Desktop\Get-TrueLastLogon-Commercial.ps1` as a module under
Identity & Access.

**Scope is ONE USER AT A TIME (owner, 2026-09-29).** No list input, no CSV, no tenant sweep,
no background job. That removes most of the script: the `-InputFile` / `-CsvInputFile` paths,
`-TenantSweepThreshold`, `-DetailAutoThreshold`, `-DCThrottle` tuning for large lists, and the
whole bulk cost model. What is left is the part that is actually hard to get right.

## Why this cannot be one query

Two facts drive the entire design, and both are the reason the script exists rather than
someone just reading a single attribute.

1. **`lastLogon` does not replicate.** Each DC records the logons it personally handled and
   tells no other DC. Ask one DC and you get that DC's answer, which can be months behind the
   truth. The only correct answer is the MAXIMUM across every DC in the domain.
   (`lastLogonTimestamp` does replicate but is deliberately imprecise -- up to 14 days stale by
   design -- so it cannot answer "did this person log on last Tuesday".)

2. **The cloud's cheap answer under-reports.** Graph's `signInActivity` is a materialized
   aggregate, documented as lagging up to ~6 hours, and it can return null for an account that
   genuinely signed in. The script's own measurement, 2026-08-20: of 557 accounts
   `signInActivity` called dormant, re-checking the next day found 9 with sign-ins.

   The raw sign-in log is live and authoritative but retains only ~30 days. **Neither source is
   a superset of the other**, so the answer is the later of the two.

## The safety property this module must not lose

The script's most important output is not a date. It is **how much to trust the absence of
one**, carried in `Cloud_Verified`:

| Value | Means | Safe to act on? |
| --- | --- | --- |
| `LogVerified` | `signInActivity` and the raw log agree | Yes |
| `ActivityOnly` | `signInActivity` alone said dormant | **No** -- not evidence of dormancy |
| `LogOnly30d` | `signInActivity` failed; only ~30 days checked | Only within that window |
| `Unverified` | Neither source answered | No |

And separately: **`Cloud_Status = FAILED` means Graph never answered. It is NOT dormancy.**

A module that shows "Last sign-in: never" without saying which of these produced it is more
dangerous than the script, because a UI makes it look authoritative. **The verification state
is a first-class field on screen, not a tooltip.**

For a single user the script's `-DetailAutoThreshold` (25) means detail and dormancy
verification always run. **So this module always runs the accurate mode** -- there is no
"fast but unverified" path to choose, and `-VerifyDormant` has no UI equivalent. One user
costs roughly ten seconds for the log query.

## On-prem: ask every DC, and say which ones did not answer

Per the script's performance model, adapted to one user:

- **Enumerate DCs at runtime.** `Get-ADDomainController -Filter *`, the pattern already used in
  `Services/DhcpAuthorizationService.cs:177-210`. **No domain name in source** -- invariant 7.
  The script's `-Domain ad.analog.com` default does not port.
- **Preflight TCP:389** with a short timeout (script default 2s) in parallel, so an unreachable
  DC costs two seconds rather than an LDAP timeout.
- **Query the reachable DCs concurrently** for this one user's `lastLogon`.
- **Report skipped DCs by name.** The script is emphatic and it is right: *a DC that was not
  queried can hide a more recent logon.* A result computed from 30 of 37 DCs is a different
  claim from one computed from all 37, and the operator must see which they have. This is the
  on-prem equivalent of `Cloud_Verified` and it fails the same way if dropped.

## Cloud: credential, and why the "which app reg" question dissolved

Owner, 2026-09-29: reuse the existing Graph credential rather than create a dedicated app reg.

**There is no single shared Graph registration to reuse, and that turns out not to matter.**
Nine modules each declare their own `GraphDelineaSecretId` config field -- M365 Group
Management, MFA Reset, Cloud Password Reset, Named Locations, Emergency Disable, Risky Users,
Defender Endpoint Devices, Intune Devices, Service Health -- and the owner points each at a
Secret Server secret at deploy time. Which app registration sits behind a given secret is a
runtime configuration fact, not a source fact.

So this module follows the same convention: **it declares its own `GraphDelineaSecretId`**, and
the owner points it at a secret whose registration carries the permissions below. That is the
established pattern and it needs no decision now.

Permissions the registration behind that secret must hold:

| Permission | For |
| --- | --- |
| `AuditLog.Read.All` | `signInActivity`, and the raw sign-in log query |
| `User.Read.All` | reading `signInActivity` on a user |

**`AuditLog.Read.All` appears nowhere in this codebase today**, so no existing module's
registration is known to carry it. It is a Graph app role and needs a Privileged Role
Administrator or Global Administrator to consent. Treat it as a deployment prerequisite, the
same as every other module's secret.

`signInActivity` requires `AuditLog.Read.All` **in addition to** a user-read scope, and
`User.ReadBasic.All` is **not** sufficient for it -- the narrower version of the lesson Risky
Users learned at `ModuleCatalog` 1.4.1. When either scope is missing, the module must surface
the 403 **naming the exact missing permission**, as Risky Users does, rather than reporting an
empty or dormant result. A missing permission rendering as "never signed in" is the single
worst failure this module can have.

## What the page shows

One input (UPN or sAMAccountName), one result. Fields, all named:

- **True last logon** -- the maximum across every source, with which source produced it.
- **On-prem** -- the max `lastLogon` and the DC that held it; DCs skipped, by name; or
  "Not checked" when the on-prem half is unavailable, never "Never".
- **Cloud interactive / non-interactive** -- dates, each with its source.
- **Verification** -- the table above, stated plainly.
- **Sign-in detail** -- IP, app, resource, location, CA result, from the log query that a
  single-user lookup always runs.

"Not checked" and "Never" are different answers and must never share a rendering. That
distinction is the same class as the migration module's empty-state defect and is called out
here so it is designed in rather than found on dev.

## Slices

| Slice | What |
| --- | --- |
| S1 | `TrueLastLogonService`: runtime DC enumeration, TCP preflight, concurrent `lastLogon` read, skipped-DC reporting. Unit tests over the max-and-skip logic. |
| S2 | Cloud half: `signInActivity` + raw sign-in log, the later-of-two rule, and the four `Cloud_Verified` states. |
| S3 | Module descriptor, page, permission, click gating, audit. |

Each slice is a commit with its own module version bump. S3 is the only one that touches
`ModuleCatalog.cs` for registration; the base app version bumps only if shared code changes.

## Verification

Build, full suite, format, `git diff --check`. New service requires tests
(repo-guidance Verification). Every new test mutation-proved.

**A live check is mandatory before this is called done**, and cannot be automated here: one
account known to have logged on recently, and one known-dormant account, compared against the
script's own output for the same two users. The module and the script must agree, and if they
disagree the script is right until proven otherwise.

## Open question -- one, and it is a sequencing call

**Is on-prem in scope on day one, or does cloud ship first?**

The slice table above assumes on-prem first (S1), because it is the half that makes the answer
TRUE: `lastLogon` is the only source that sees on-prem-only activity, and a cloud-only answer
for a user who logs on to a workstation and never touches a cloud app reads as dormant when
they are not. That is the dangerous direction.

Cloud-first would ship something usable sooner and is a defensible call -- but only if the
page states plainly that on-prem was not checked, and never presents a cloud date as "true
last logon". The script already models this: `-CloudOnly` reports `OnPrem_LastLogon` as
"Not checked" rather than "Never", and that distinction would have to be honoured from the
first commit rather than retrofitted.

**Recommendation: on-prem first.** It is the larger half, but shipping the half that can call
an active user dormant is how this module would cause the exact harm it exists to prevent.

The credential question that was here has been answered from the repo -- see the cloud section.
The prerequisite (`AuditLog.Read.All` consented on whichever secret this module is pointed at)
stands, but it is a deployment step like every other module's, not a design decision.
