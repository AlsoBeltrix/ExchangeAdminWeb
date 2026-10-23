# aud-a23o: Exchange base - the on-prem size read turns an unparseable size into 0 and an archive failure into "no archive", two recipient reads run under Ignore/SilentlyContinue, and the dead group-membership method keeps the substring match

**Severity**: MEDIUM - the aud-a23h/a23k class in shared code. (1) `ExchangeServiceBase.GetOnPremMailboxSizeAsync`:
an unparseable `TotalItemSize` reads as 0 (`ParseSizeToGB ?? 0`) and any archive-read failure reads as
"no archive", so the ToCloud quota check in `MigrationService` can pass a mailbox whose size could not be
read. (2) `MigrationService` files a failed ToCloud size read as Ineligible ("Could not verify on-prem
mailbox size...") rather than Undetermined, and names "on-prem connection unavailable" as the cause when the
base returns null for ANY failure - a guess. (3) `HasCloudMailboxAsync` reads `Get-Mailbox` under
`-ErrorAction Ignore` and `OnPremRecipientExists` under `SilentlyContinue`: a failed read is "no mailbox".
(4) `CheckAdGroupMembership` has no caller since aud-a23j but still carries the substring match and the
Eligible-when-no-user return; a future caller inherits the defect.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23j-n agent, 2026-10-22, candidates 1, 2, 3 and 6 of its hand-back)

## Evidence

At `09d3bacb`: `Services/ExchangeServiceBase.cs` `GetOnPremMailboxSizeAsync` (around 521 and 797, the
`?? 0`; 538-541 the archive catch), `HasCloudMailboxAsync` (588-591), `OnPremRecipientExists` (649),
`CheckAdGroupMembership` (703-748); `Services/MigrationService.cs` around 199-240 (the quota check and the
Ineligible filing with the guessed cause).

## Approach (to be written by the implementing agent before code)

- The size read returns a typed outcome (sizes, or unreadable with the reason; archive not-found by
  structure is "no archive", any other archive failure is unreadable - the aud-a23k shape). The ToCloud quota
  check treats unreadable as Undetermined with the base's reason in the cmdlet's own words; never Ineligible,
  never a guessed cause.
- The two recipient reads run under Stop with `IsNotFound` (structure, never text) deciding "no mailbox";
  any other failure is a failed read the caller reports as such.
- `CheckAdGroupMembership` is removed (base-app change; the aud-a23j check replaced it).
- Tests through `MigrationStubExchange` and the base's stand-in; mutations at the call site: `?? 0` again;
  the archive catch again; `Ignore` again at one site.

## Guard proof

Pending.
