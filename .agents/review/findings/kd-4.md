# kd-4: Intune Devices - the wipe's typed device-name confirmation is enforced only in the button's disabled expression

**Severity**: MEDIUM - a destructive action's confirmation lives only in the UI. `Components/Pages/IntuneDevices.razor` `WipeNameConfirmed` (about 404) disables the button; the wipe handler never re-reads the confirmation, so a stale render, a double-dispatch or a programmatic click wipes without the typed name matching.
**Status**: Open - released by the owner's 2026-10-23 evening ruling (`.agents/decisions.md`); approved under the 2026-10-21 go
**Branch**: -
**Commit**: -
**Reviewer**: implementer (recorded in `.agents/state.md` "Known defects", re-verified 2026-10-23 as of `d881ea58`)

## Evidence

As above. Trigger: the handler runs while the typed name no longer matches (device changed under the dialog, or the click raced the re-render). Predicted failure: a wipe of a device whose name the operator did not type.

## Approach (to be written by the implementing agent before code)

- The handler re-checks the typed name against the device it is about to wipe before any write and refuses with an audited denial otherwise; page is line-keyed (edit in place or re-anchor).
- Mutation: the handler check removed.

## Guard proof

Pending.