# aud-a26n: Settings pages - the EXO connection save and the secret write still overwrite unseen changes, masked secret fields never show a change, repository whole writers stay public, clearing the secret shows the fallback, and two comments are wrong

**Severity**: LOW - the aud-a28k/a28n class on two remaining writes plus labelling and dead surface.
(1) The Exchange Online connection save writes all three keys whatever changed, so a key changed
elsewhere and not edited here is reverted. (2) The directory-read secret id is written with no
compare against the value the page loaded, so a concurrent change to it is overwritten. (3)
Module-config records mask secret fields as `Key=***` on both sides, so a changed secret field never
shows as added or removed in the audit (the reviewer cannot see that it changed at all). (4) The
repository-level whole writers for module config, module admins and protected principals stay public
with no tripwire (the aud-a26k tripwire covers the service layer only). (5) With the appsettings
fallback set, clearing the secret id writes an empty value and the page then shows the fallback
again, so the admin cannot tell the clear took. (6) Comments in `ModuleConfig.razor` (~696) and
`AdminSettings.razor` (~346) say dictionary copies keep the store's case-insensitive comparer; on
.NET 10 they do not (harmless today).
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28n/a26k agent, 2026-10-22, candidates 2 to 7 of its hand-back)

## Evidence

At `3ef12e60`: `Components/Pages/ExchangeOnlineConfig.razor` `SaveExoConfig` (`UpdateModuleValues`
with all three keys); `Components/Pages/AdminSettings.razor` `SaveProtectedPrincipals` (the secret
write) and the clear path; `Services/ModuleConfigService.cs` masking in the settings-change record;
`Services/Storage/ModuleConfigRepository.cs`, `ModuleAdminRepository.cs`, `ProtectedPrincipalRepository.cs`
public whole writers; the two comments.

## Approach (to be written by the implementing agent before code)

- EXO connection save and the secret write: per-key decisions under the lock against the loaded
  value (aud-a28n's `SaveChangedValues`), refusing `ConcurrentChange` naming the key, no-op when
  already applied.
- Masked fields: record `Key=***` only on the side that has a value, and a `changedSecretKeys` list
  naming which secret keys changed without their values.
- Repository whole writers: internal, with the tripwire extended to the repository layer.
- Clearing the secret: the page shows the stored state (cleared) distinctly from the appsettings
  fallback that applies at runtime ("not set in the database; the configured fallback applies").
- Comments: use a case-insensitive comparer explicitly where the copy is made, or correct the
  comment.
- Tests through `SettingsPageHarness` and two stores on one temp file; mutations at the call site:
  write all three keys again; write the secret without compare; mask both sides again; make a
  repository writer public again; show the fallback for a cleared secret again.

## Guard proof

Pending.
