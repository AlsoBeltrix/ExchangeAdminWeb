# aud-a26k: Settings pages - a no-op concurrent change is refused, two store writers have no caller, the directory-read secret reads fail-open, and the drain failure uses the generic action

**Severity**: LOW - usability, dead surface and labelling; no wrong write. (1) Section access refuses
`ConcurrentChange` even when the other change already equals this save's target (the admin is told
to reload and re-apply a change that is already in place). (2) `SaveEnablement(map)` and
`SaveSectionAccess(map)` have no app caller since aud-a28k; only tests use them, and they still
write whole maps. (3) Admin Settings shows the directory-read secret id from a fail-open read, so an
unreadable store looks the same as an unset one (it is no longer written back blank since aud-a28j,
so the harm is display). (4) The drain-failure record (aud-a26h) uses the generic settings-change
action; a reviewer filtering by action cannot tell it from a settings change.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: implementer (surfaced by the aud-a28j/a28k/a26h agent, 2026-10-22, candidates 3 to 6 of its hand-back)

## Evidence

At the aud-a28j/a28k/a26h branch tip `ade2a1d7`: `Components/Pages/ModuleConfig.razor`
`SaveSectionAccessAsync` (the per-alias compare); the section-access and enablement stores'
map-writing methods; `Components/Pages/AdminSettings.razor` (the secret-id read); `Components/Pages/ExchangeOnlineConfig.razor`
(the `ExchangeOnlineConfig:ConnectionPool` failure record's action).

## Approach (to be written by the implementing agent before code)

- Concurrent change equal to the target: treat as a no-op for that alias (record it as such), not a
  refusal; refuse only when the current row differs from both what the page loaded and what it
  wants to write.
- Remove the callerless map writers (or make them internal to the audited path) and move their tests
  to the per-row paths; a tripwire refuses a production caller.
- Secret-id read: distinguish unreadable from unset on the page ("could not be read" with the
  remediation per aud-a26f/a26h text) and refuse a save that depends on it while unreadable.
- Drain failure: a dedicated audit action (new `AuditService` method; add it to
  `UsageTelemetryServiceTests`' every-public-audit-method list) so it filters apart.
- Tests through `SettingsPageHarness`; pages `NotYetConverted` in `ClickGateRegistry` - confirm.
  Mutations at the call site: refuse the equal change again; make a map writer public again; read the
  secret fail-open again; use the generic action again.

## Guard proof

Pending.
