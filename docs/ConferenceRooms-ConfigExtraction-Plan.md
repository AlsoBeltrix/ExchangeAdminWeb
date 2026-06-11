# Conference Rooms — Config Extraction (remove ADI defaults) Plan

**Status:** Implemented (module 2.0.4)
**Date:** 2026-06-11
**Author:** Michael Coelho (via agent investigation)
**Module:** ConferenceRooms (`Modules/ModuleCatalog.cs`)

## Context

The module hardcoded ADI's environment as executable fallbacks in
`ConferenceRoomService.cs` — 13 `@analog.com` / `ad.analog.com` literals read via
`Cfg(key, fallback)`. If a config field was left blank, the code silently applied ADI
groups/OU/contacts. In ADI that was correct; in any other tenant it would silently
misconfigure rooms. This change extracts the real values into module config and removes the
literals, failing closed when required values are absent.

## What changed

- **Real values moved to module config.** The 12 environment-specific values now live in
  `config/module-config-ConferenceRooms.json` at the deployed instance's content root
  (dev: `D:\inetpub\ExchangeAdminWebDev\config\`). This file is **not** in source control and
  is excluded from deploy robocopy mirroring (Architectural Invariant #3), so it persists
  across deploys.
- **Code fallbacks removed.** `ConferenceRoomService.cs` getters are now `Cfg("Key")` with no
  ADI default. `Modules/ModuleCatalog.cs` field-description examples use generic
  `@example.com` placeholders.
- **Fail-closed preflight.** `SetRoomTypeAsync` aborts before any EXO mutation if any required
  group key is missing, with: *"Conference Rooms module is not configured. Set {keys} in
  Module Config."* (mirrors the `Comms10kService` precedent). `BuildTypePreview` adds a
  non-fatal warning in the same case. Pure helper `FindMissingRequiredGroups` is unit-tested.
- **Module version** bumped `2.0.3` → `2.0.4` (module-scoped; no app-version bump).

## Required vs optional config keys

- **Required** (preflight enforces, `RequiredGroupConfigKeys`): `DefaultArbiterGroup`,
  `ExecConfCoordinatorsGroup`, `ConfExecAdminsGroup`, `ConfExecVPsGroup`, `ConfAdminsGroup`,
  `ConfCEOGroup`, `ConfExceptionGroup`, `ADGTAdminsGroup`.
- **Optional:** `RoomListOU` (already guarded at the room-list step), the three contact-email
  keys (used only in cosmetic response text), `RestrictedMailTip`/`ExecMailTip` (generic
  built-in fallback text remains), `OnPremDelineaSecretId` (operator-set; no default).

## Operational note — real values are deploy-only

The real group/OU/contact values exist **only** in the deployed instance's
`config/module-config-ConferenceRooms.json` and are not in git. To avoid losing them:

- **Back them up** to the team's secure store alongside the module's Delinea secret IDs, so a
  rebuilt server can be re-seeded.
- The committed `config/module-config-ConferenceRooms.example.json` documents the **schema**
  (keys + placeholder values) only — never put real values there.
- If the deploy file is lost before backup, the original ADI values can be recovered from this
  repo's git history of `Services/ConferenceRoomService.cs` (pre-2.0.4).
- Promotion: `tools/promote-dev-to-prod.ps1` copies per-module config dev→prod (dev wins), so
  configuring dev and promoting carries the values to prod.

## Verification

- `rg "analog\.com" Services/ConferenceRoomService.cs Modules/ModuleCatalog.cs` → zero hits.
- `dotnet build -c Release` → 0 errors; full test suite passes incl. the new preflight test
  (guard-revert proven non-vacuous).
- Manual (dev EXO — state whether run): with config present, Set Room Type succeeds; with the
  config file removed, it aborts with the "not configured" message and writes nothing.
