# aud-a54: Recipient Lookup reads Get-Mailbox under Ignore and swallows the cloud archive statistics failure

**Severity**: LOW/MEDIUM - the aud-a23h/a23o class in Recipient Lookup. `RecipientLookupService` runs its own
`Get-Mailbox -ErrorAction Ignore`, so a failed cloud read shows as "not a mailbox", and wraps the cloud
archive statistics read in `catch { }`, so any failure shows as no archive. aud-a23o fixed the on-prem size
path this service consumes; these two cloud reads are its own.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23r/o/q/p agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At `d690a6c1`: `Services/RecipientLookupService.cs` lines 50-53 (the `Ignore` read) and 99-101 (the bare
catch); `Services/ExchangeServiceBase.cs` `ReadUnlessNotFound` / `IsNotFound` (aud-a23o, the shape to use).

## Approach (to be written by the implementing agent before code)

- Both reads run under Stop through `ReadUnlessNotFound`: not-found by structure is "no mailbox" / "no
  archive"; any other failure is shown on the lookup as a warning naming the read and the reason (the aud-a23o
  wording), never as an absence.
- Tests through the Recipient Lookup stand-in; mutations at the call site: `Ignore` again; the bare catch
  again.

## Guard proof

Pending.
