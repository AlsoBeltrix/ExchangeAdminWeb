# aud-a28s: PermissionValidator still reads ExcludedUsers fail-open after its probe

**Severity**: MEDIUM - the aud-a28p fail-open shape on a second path (Known Failure Class 3). When
the central protected-principal config is absent and the Mailbox Permissions `ExcludedUsers` list is
the protection source, `PermissionValidator.EnsureInitializedAsync` answers its presence and
corruption reads (around lines 637-638) and then reads the list through `GetConfiguredExclusions()`
(around 649) via `ModuleConfigService.GetValue`, whose `ReadModuleConfig` catches a read failure and
returns an empty dictionary. The validator initialises with an empty excluded-user set, so a mailbox
listed in `ExcludedUsers` is treated as not protected and Mailbox Permissions, Calendar Permissions
and Out of Office writes can proceed until the cache is invalidated.
**Status**: Merged to master (`0bcb92c0`); closed on guard proof; codex review pending (batched with aud-a28r/a26p)
**Branch**: `worktree-agent-aaa429d3a8979950e`
**Commit**: `f99a0a13` (fix, tests, record); comments `6e8861f6`; plan `6a2bb13f`; merged `0bcb92c0`
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

## Executed reproduction - 2026-10-22

Branch `worktree-agent-aaa429d3a8979950e`, the fix's tests in place and `Services/PermissionValidator.cs` as it is at
`ce8b3bce` (`git hash-object` equal to the blob `2b571b58`, MD5 `8E28690F2ADC523A18A25D868C9237AB`), build 0 errors:
`PermissionValidatorExclusionsReadTests` 3 failed / 2 passed. The bulk grant against a mailbox on ExcludedUsers, with the
store answering two reads (the presence read and the probe) and failing the list's, was written (`SuccessCount` 1: the
list's read failed open to "no protected users"); with the store answering one read it was refused, but as the list
unavailable (the probe's read was the one that failed), not as protected; and one load read the store three times. The
store unreadable from its first read was refused and the empty-list control passed - both as they must against the old
code too, which refused a store whose presence read failed. The fix was then written over that file.

## Fix

`Services/PermissionValidator.cs`: `TryGetConfiguredExclusions(out string[] exclusions)` replaces
`GetConfiguredExclusions` - one read of the MailboxPermissions rows through `ModuleConfigService.TryGetModuleConfig`,
false when they cannot be read, otherwise the `ExcludedUsers` value split as before. `EnsureInitializedAsync` calls it
once, in place of the `HasModuleConfigFile` presence read and the `IsModuleCorrupt` probe: false takes the corrupt branch
unchanged (`_initFailed`, so every validation refuses); true carries the list into the load. No caller changed.

Tests: new `ExchangeAdminWeb.Tests/PermissionValidatorExclusionsReadTests.cs` (5 cases): the store answering its first one
or two reads and failing every later one, each through the Mailbox Permissions CSV bulk loop (refused as protected, one
`BulkAdd_ExcludedTarget` record carrying that refusal, no `Add-MailboxPermission`, no connection borrowed); the store
unreadable from its first read (an unlisted target refused as the list unavailable, `IsUserExcludedAsync` true); the empty
list over a healthy store (allowed, bound to the mailbox Exchange names); one module-config read per load and none from
the cache. As planned, no departure.

Caller-visible change, by design: none while the store reads. When the MailboxPermissions rows cannot be read the
validator refuses every target as the protected-user list unavailable, as it did whenever the probe caught it; the window
in which the presence read and the probe answered and the list's read did not - which left every listed mailbox
unprotected for the cache lifetime - is gone. A module with no MailboxPermissions rows still reads as an empty list.

## Guard proof

Session-local driver (`mutate.ps1` in the scratchpad's `a28s-work`): back up, mutate by exact text (one match required),
MD5, `dotnet build` of the test project, `dotnet test` filtered to
`FullyQualifiedName~PermissionValidator|FullyQualifiedName~WriteIdentityTests|FullyQualifiedName~OutOfOfficeRequestCaptureTests|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ProtectedPrincipalExclusionsReadTests`
(224 tests), restore from the backup, set the write time, MD5 re-confirmed. Fixed-file MD5: `Services/PermissionValidator.cs`
`2457C3EC5269C8C488A4F4C5AFE92CDD`; every mutant built with 0 errors and the file was restored to that MD5 each time. After
the round, the restored tree rebuilt and the same filter: 224/224 green.

| # | Mutation (call site) | Mutant MD5 | Named test failing |
|---|---|---|---|
| 1 | `EnsureInitializedAsync`: the list read fail-open again - the one read kept as the probe, the list read through `GetValue` after it | `379B73AA54C5412EE5CBB441A05D59A3` | `AStoreThatFailsAfterItsFirstReads_DoesNotUnprotectAListedMailbox(answered: 1)` (the row written: `SuccessCount` 1), `OneLoad_ReadsTheModuleConfigOnce_AndACachedValidationNotAtAll` (2 reads, 1 expected) |
| E1 | extra, `TryGetConfiguredExclusions`: rows that cannot be read taken as an empty list (`return true`) | `670030FD7787DA1DFF6B99125619E8A9` | `AStoreThatCannotBeRead_RefusesEveryTarget_AsTheListUnavailable` (an unlisted target allowed: `Error` null) |

The code comments are written after the round, in their own commit, so the MD5s above are this commit's bytes.

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; 61 warnings, all pre-existing, none in a touched file.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug, as the gate is written): exit 0,
  5,623 passed / 3 skipped (the three known AD skips) / 0 failed, 5,626 total, 5m10s. No hang. (Baseline at
  `ce8b3bce`: 5,618 / 3 / 0, 5,621, 5m4s; the five added are this class's.)
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

No PowerShell source, AD module, directory, Exchange Online, Delinea, SMTP, push or deployment: Exchange is the harness's
stand-in cmdlets and a stand-in resolver, and no case here opens a session in its passing state. No coder self-review
verdict is claimed.

## Known gaps

- The list is cached for the 30-minute lifetime or until the change token moves, and a failed load is retried after
  `FailedLoadRetry`, as before.
- `TryExpandGroupAsync` asks `ExoConnectionPool.IsConfigured`, whose ExchangeOnline config reads are fail-open `GetValue`s
  in another file: a failure confined to that read keeps a listed group a literal for the load (its members unprotected)
  while the resolver's later read of the same config answers. Handed back as a candidate, not fixed here.
- `GetPreventSelfGrant` reads `PreventSelfGrant` through the fail-open `GetValue` and on a read failure falls back to
  appsettings `Security:PreventSelfGrant`, a fallback the Constitution allows only when the module config is absent. It
  is the self-grant gate, not this one; handed back as a candidate.
- Only the Mailbox Permissions write is driven. Calendar Permissions (its bulk loop, its page) and Out of Office (its
  page) reach the same `ValidateTargetMailboxAsync` and its one load, so the refusal is theirs too, but no test here
  drives their writes.
- The failing store stands in for a store fault; nothing ran against a damaged database.
