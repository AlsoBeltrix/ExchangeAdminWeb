# aud-a23p: Migration - three wording leftovers: "protected principal" over an unverified exclusion at the partial-exclusion sites, the ExcludedADGroups description does not say what a value must be, and the mailbox-load banner gives no reason

**Severity**: LOW - operator messages. (1) aud-a23l fixed the all-excluded message; where a batch IS created
with some targets excluded, the NOTE in `WithProtectionOutcome`, the success formatter's Detail header, the
two page exclusion banners, the exclusion audit rows ("Excluded - protected principal, not migrated") and
the notification notes still call every excluded target a protected principal even when its check did not
answer. (2) The `ExcludedADGroups` module setting description says "Comma-separated AD groups" but not that
each value must be an identity `Get-ADGroup -Identity` resolves (aud-a23j), nor that a DN cannot be entered
because the setting splits at commas. (3) The mailbox-load banner says "Unable to load mailboxes" with no
reason; the reason reaches only the activity text.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a23j-n agent, 2026-10-22, candidates 4, 7 and 8 of its hand-back)

## Evidence

At `09d3bacb`: `Services/MigrationService.cs` around 765 (`WithProtectionOutcome` NOTE) and 692 (the Detail
header); `Components/Pages/Migration.razor` around 198 and 368 (banners), 2065 and 2211 (audit errorDetail),
2074 and 2220 (notification notes), 1041 (the load banner) - line-keyed by `ClickGateRegistry`, 4377 lines,
edit in place only; `Modules/ModuleCatalog.cs` around 435 (the setting description - the supervisor owns the
module `Version` lines in that file, the description text is this finding's).

## Approach (to be written by the implementing agent before code)

- The partition's fourth list (aud-a23l `IsProtected`/unverified) drives every site: "excluded - protected
  principal" and "excluded - could not be verified as unprotected" are counted and named apart in the NOTE,
  the Detail header, the banners, the audit rows and the notification notes.
- The setting description names what a value must be and that DNs cannot be entered.
- The load banner carries the reason (the aud-a23i error-text helper).
- Tests through `MigrationStubExchange` and the page's reflection driver; mutations at the call site: one
  site's single wording again; the banner without the reason.

## Guard proof

Pending.
