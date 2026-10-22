# aud-a28s: PermissionValidator still reads ExcludedUsers fail-open after its probe

**Severity**: MEDIUM - the aud-a28p fail-open shape on a second path (Known Failure Class 3). When
the central protected-principal config is absent and the Mailbox Permissions `ExcludedUsers` list is
the protection source, `PermissionValidator.EnsureInitializedAsync` answers its presence and
corruption reads (around lines 637-638) and then reads the list through `GetConfiguredExclusions()`
(around 649) via `ModuleConfigService.GetValue`, whose `ReadModuleConfig` catches a read failure and
returns an empty dictionary. The validator initialises with an empty excluded-user set, so a mailbox
listed in `ExcludedUsers` is treated as not protected and Mailbox Permissions, Calendar Permissions
and Out of Office writes can proceed until the cache is invalidated.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
**Commit**: -
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (attr-batch-10, `prompt-attr-batch-10.md`, 2026-10-22)

## Evidence

At `7b65d27e`, `Services/PermissionValidator.cs` `EnsureInitializedAsync` and `GetConfiguredExclusions`;
`Services/ModuleConfigService.cs` `ReadModuleConfig` (fail-open) versus `TryGetModuleConfig`
(aud-a26k). Compare aud-a28p's fix in `ProtectedPrincipalService.LoadEffectiveConfig`: one read
through `TryGetModuleConfig`, false means refuse.

## Approach (to be written by the implementing agent before code)

- `EnsureInitializedAsync` reads the Mailbox Permissions module config ONCE through
  `TryGetModuleConfig`; if it cannot read, set `_initFailed` and deny every validation (the
  validator's existing fail-closed path), never "no protected users"; parse `ExcludedUsers` from that
  same dictionary so the probe and the list are one read. Any other `GetValue` read on this gate
  gets the same treatment.
- Tests: a module-config store that answers the probe reads and fails the exclusion read makes
  `ValidateTargetMailboxAsync` refuse; an empty list over a healthy store still passes; the existing
  corrupt-store refusal still holds. Mutation at the call site: read the list fail-open again.

## Guard proof

Pending.
