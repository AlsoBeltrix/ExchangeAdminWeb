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

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); admitted at `ce8b3bce`. One
fix commit `fix(permission-validator): the ExcludedUsers list is read once, fail-closed (aud-a28s)` on
`worktree-agent-aaa429d3a8979950e`, then a comments-only commit. Behaviour: the shared `PermissionValidator` that Mailbox
Permissions, Calendar Permissions and Out of Office all reach (base app version); no module version moves; the
supervisor owns the bump.

Evidence re-read at `ce8b3bce`: `PermissionValidator.EnsureInitializedAsync` 621-722 asks
`_moduleConfig.HasModuleConfigFile("MailboxPermissions")` (637: one read of the presence marker,
`ModuleConfigRepository.HasModule` 52-61) and `IsModuleCorrupt("MailboxPermissions")` (637: one read of the rows,
`TryReadModule` 33-45), then reads the rows a third time through `GetConfiguredExclusions` 63-72 (649) ->
`ModuleConfigService.GetValue` 60-64 -> `ReadModuleConfig` 260-272, which logs a read failure and returns an empty
config. A failure of that third read is "no excluded users": the load completes (`_initFailed = false`, 702),
`CheckExclusionAsync` 117-151 answers NotExcluded at 129-130, and `ValidateTargetMailboxAsync` 395-428 binds the write
and allows it - for the 30-minute cache lifetime, or until the change token moves.

1. **One read, fail-closed.** `GetConfiguredExclusions` becomes `TryGetConfiguredExclusions(out string[] exclusions)`
   over `ModuleConfigService.TryGetModuleConfig` 75-76 (aud-a26k), the shape of aud-a28p's `TryGetLegacyExclusions`:
   false when the MailboxPermissions rows cannot be read; otherwise the `ExcludedUsers` value split as today (the
   read's dictionary is case-insensitive, as `GetValue`'s was). `EnsureInitializedAsync` calls it once, in place of the
   presence read and the probe at 637: false takes the existing corrupt branch unchanged (638-647: empty sets,
   `_initFailed = true`, stamped with the token, logged, retried after `FailedLoadRetry`), so every validation refuses -
   `ValidateTargetMailboxAsync` with "Access denied: Protected-user list is unavailable. Contact your administrator."
   (397-401), `IsUserExcludedAsync` as excluded; true carries that one read's list to 649. The presence read goes: it
   only gated the probe, a module with no rows reads as an empty list through the one read, and an unreadable store is
   refused whether or not the module was ever configured. One read per load where there were three.
2. **The other reads on this gate**, enumerated; none is a fail-open `GetValue` in this file, so none changes here.
   `HasCentralConfig` (244) treats an unreadable protected-principal store as configured and routes to the fail-closed
   load; `LoadEffectiveConfig` reads the same rows once, fail-closed, since aud-a28p; the servicer decision denies on a
   read failure (`ProtectedPrincipalServicerService.Evaluate`); every resolver lookup refuses when Exchange cannot
   answer (aud-a2b). Two reads are handed back as candidates rather than folded in: `TryExpandGroupAsync`'s
   `_exoPool.IsConfigured` (728) reads the ExchangeOnline config through `GetValue` inside `ExoConnectionPool` (another
   file; a persistent failure already fails the load closed at the resolver's own `IsConfigured` check, and only a
   failure confined to the expansion's read leaves a listed group's members literal-only), and `GetPreventSelfGrant`
   (74-81) is the self-grant gate's read (`ValidateSelfGrant*`), which this load never reaches.
3. **Registries.** No page, handler or markup changes: ClickGateRegistry and ProgressRegistry are untouched.

Tests, new `ExchangeAdminWeb.Tests/PermissionValidatorExclusionsReadTests.cs`: a real `PermissionValidator` whose module
config sits on `ExchangeWriteHarness`'s database file through a store that answers a set number of reads and fails every
later one (`FailsAfterReadsStore`, aud-a28p's fixture, copied into the class as the suite's other failing stores are),
its protected-principal and section-access stores on the same file with nothing configured, its own Exchange pool left
unconfigured (listed entries stay literals and nothing is borrowed during the check) and a stand-in resolver. Through
the Mailbox Permissions CSV bulk loop (`MailboxPermissionService.ProcessMailboxPermissionsCsvAsync` 190, validation
250-271; the harness's pool and stand-ins): a target on ExcludedUsers with the store answering its first one or two
reads (the fix's one read; the presence read and the probe before it) is refused as protected - one
`BulkAdd_ExcludedTarget` record carrying that refusal, no `Add-MailboxPermission`, no connection borrowed. At the
validator: the store unreadable from its first read refuses an unlisted target as the list unavailable (the
corrupt-store refusal, which held before the fix and must still hold); an empty list over a healthy store allows, bound
to the mailbox Exchange names (the control); one load reads the module-config store once, and a cached validation reads
it not at all. Mutations at the call site: (1) `EnsureInitializedAsync` reads the list fail-open again - the one read
kept as the probe and the list read through `GetValue` after it; extra (E1) `TryGetConfiguredExclusions` takes rows it
could not read as an empty list. Plus an executed reproduction against `ce8b3bce`'s file.

## Guard proof

Pending.
