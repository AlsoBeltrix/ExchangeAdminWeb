# aud-a2x: Conference Rooms - every credential refusal says "not configured (set the AD Delinea Secret ID)" even when the ID is set and the Delinea lookup failed

**Severity**: LOW - operator guidance. All four directory credential refusals in `ConferenceRoomService`
carry the same text, so when the secret ID is configured and the lookup itself failed (an incomplete
secret, an HTTP error) the operator is sent to set an ID that is already set.
**Status**: Complete; merged `9293b77a`; codex cr-batch-6 clean (its one LOW is the version bump, deferred by design to the consolidated closing bump)
**Branch**: `worktree-agent-a56481d2c0a45a8cb`
**Commit**: `295c4d19` (fix, tests, record); plan `ee4c8c5d`; comments `4e0f61a1` (shared with psl-8, aud-a2w)
**Reviewer**: implementer (surfaced by the aud-a2v agent, 2026-10-22, candidate 2 of its hand-back)

## Evidence

At `6ac190f7`: `Services/ConferenceRoomService.cs`, the four credential lookups and their refusal texts
(aud-a2v added the log lines around them); `Services/ModuleCredentialService.cs` (what the lookup
returns, and whether it distinguishes "no ID" from "lookup failed").

## Approach (to be written by the implementing agent before code)

- The credential lookup's outcome distinguishes no ID configured from a lookup that failed (if the
  credential service already does, consume it; if not, surface the distinction without changing other
  callers' behaviour).
- Two refusal texts: "not configured (set the AD Delinea Secret ID)" only when no ID is set; otherwise
  "the AD credential could not be read from Delinea: <reason category>; nothing was written".
- Tests through the `NoCredentialFor` seam (aud-a2v) extended with a failed-lookup variant; mutation at
  the call site: one text for both again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("ALL the bugs includes ALL the bugs ... EVERYTHING", `.agents/decisions.md` 2026-10-21); the
admitted approach above and the supervising session's brief. One fix commit, third of three on branch
`worktree-agent-a56481d2c0a45a8cb` (after psl-8 and aud-a2w). Line numbers are the base `e9886df7`'s.

**What the lookup says today.** `ModuleCredentialService.GetCredentialsAsync` (19-35) returns null in three cases it
does not tell apart: the module's config unreadable (`IsModuleCorrupt`, logged "module config is corrupt"), no valid
`DelineaSecretId` (logged with the purpose), and `DelineaService.GetCredentialsBySecretIdAsync` returning null for a
configured ID - no bootstrap credential, an HTTP failure, a secret missing its username, password or domain, an
exception, each logged by `DelineaService` (132-230). `ExchangeServiceBase.GetModuleCredentialsAsync` (435-444, a file
owned by another agent) passes that null on. So the credential service does not distinguish them, and a caller cannot.

**Rule.** The operator is sent to set the AD Delinea Secret ID only when none is set. Otherwise the refusal says the
credential could not be read and why, in a category - the module's configuration unreadable, or Delinea returning no
credential for the ID that is set - and points to the application log for Delinea's own reason, which it already
logs. A refusal stays a refusal; nothing is written that was not before; the log lines keep their shape.

1. **The lookup's outcome** - `Services/ModuleCredentialService.cs`, without changing any caller:
   - `public enum ModuleCredentialFailure { None, ConfigUnreadable, SecretIdNotSet, NotReturned }` and `public readonly
     record struct ModuleCredentialLookup((string username, string password, string domain)? Credentials,
     ModuleCredentialFailure Failure)`, beside the service.
   - `LookupCredentialsAsync(moduleId, purpose)`: today's body, each return carrying its failure; the same log lines in
     the same order. `GetCredentialsAsync` becomes `(await LookupCredentialsAsync(moduleId, purpose)).Credentials` - its
     38 callers see the same values and the same lines.
   - Not `DelineaService`: its every null return is already logged with the secret and the reason; carrying a finer
     category out of it would change a service every module uses, and the operator's next step - read the log, or fix
     the secret - is the same for each.
2. **Conference Rooms consumes it** - `Services/ConferenceRoomService.cs`:
   - New `protected virtual Task<ModuleCredentialLookup> LookupDirectoryCredentialAsync(string purpose)`: with a credential
     service, its `LookupCredentialsAsync(ModuleId, purpose)`. With none (`_moduleCredentials` null - a host that
     registered none, as the base class allows), the base's `GetModuleCredentialsAsync(purpose)`, which logs that and
     returns none, read as `SecretIdNotSet` - the refusal this file always gave for it. A virtual, as the base's lookup
     is, for the tests.
   - The four lookups (1598, 1698, 1896, 2007) call it; on no credential each builds its refusal from
     `DirectoryCredentialRefusal(failure)`, new, internal static, pure - the clause each path's text opens with:
     - `SecretIdNotSet`: "On-prem AD credential is not configured for Conference Rooms (set the AD Delinea Secret ID in
       Module Config)" - so each path's text is today's to the byte;
     - `NotReturned`: "The on-prem AD credential for Conference Rooms could not be read from Delinea: the AD Delinea
       Secret ID is set, but Delinea returned no credential for it (the application log has its reason)";
     - `ConfigUnreadable`: "The on-prem AD credential for Conference Rooms could not be read: the module's configuration
       could not be read, so neither could its AD Delinea Secret ID (the application log records it)".
   - Each path keeps its own tail, after the clause, as today: ". City/State/Country could not be written." (the
     City/State/Country preflight and write), ", and room list '<list>' is synchronized from on-premises, so the room
     cannot be added to it." (the room-list preflight), ". The room could not be added to the on-prem room list." (the
     add). The preflights then append " Nothing was written."; the writes, after Set-Place, inside the partial, do not
     (aud-a2r) - so the approach's "; nothing was written" is said only where it is true. The approach's two texts are
     the two shapes, "not configured" and "could not be read (<category>)".
   - Each path's line is aud-a2v's, unchanged in shape - "no AD credential was returned for {Purpose}: {Refusal}" - its
     refusal now in the category's words.

**Tests** (no directory, Exchange, Delinea or network call):
- New `ExchangeAdminWeb.Tests/ModuleCredentialLookupTests.cs`, `ModuleCredentialService` over real module config
  stores: an unreadable store (a throwing `IConfigStore`) - `ConfigUnreadable`; no ID, "0", "abc" - `SecretIdNotSet`; an
  ID set and Delinea returning none - `NotReturned` (a `DelineaService` whose bootstrap credential reader returns none,
  so it refuses before any request; its HTTP client throws if anything is sent); each with no credential, and
  `GetCredentialsAsync` returning null for each, as before.
- `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`: the `Service` takes `CredentialFailureFor` (a purpose
  text) and `CredentialFailure`, overriding `LookupDirectoryCredentialAsync` for a matching purpose and otherwise
  leaving the lookup to the base, which reaches the existing seam (`NoCredentialFor`, now `SecretIdNotSet`'s case);
  and it takes an optional real `ModuleCredentialService`.
  - `ACredentialThatCouldNotBeRead_IsRefusedWithoutSendingTheOperatorToSetTheId` (the four paths x `NotReturned`,
    `ConfigUnreadable`): each path's outcome as aud-a2v's theory asserts it, the message the category's clause and the
    path's tail (and " Nothing was written." on the preflights only), "set the AD Delinea Secret ID" nowhere in it; one
    entry carries "no AD credential was returned for <purpose>: <refusal>", a Warning, the path's line.
  - `TheCredentialServicesOwnAnswer_ChoosesTheWords` (no secret ID; an ID set and Delinea returning none; the module's
    config unreadable), through a real `ModuleCredentialService` and the default lookup, the City/State/Country
    preflight: refused, nothing written, each in its words.
  - aud-a2v's `ACredentialTheLookupDoesNotReturn_IsLoggedOnce_NamingTheRoomTheListAndThePurpose` (no secret ID) stays as
    it is: today's text to the byte.

**Mutations, at the call sites** (md5 before and after, the named failing tests, restored, touched, rebuilt, md5
matched, green; filter `ConferenceRoom|ModuleCredentialLookup`): M1 the brief's "one text for both again" -
`DirectoryCredentialRefusal` answering the not-configured clause whatever the failure; M2a-d each path's lookup taking the
not-configured clause instead of its failure's, in turn; M3 `LookupCredentialsAsync` reporting `SecretIdNotSet` for a
configured ID Delinea returned none for; M4 the default lookup going to the base's `GetModuleCredentialsAsync` even with a
credential service (the outcome dropped on the way). Comments after the round, checked code-identical.

**Files.** `Services/ModuleCredentialService.cs`, `Services/ConferenceRoomService.cs`,
`ExchangeAdminWeb.Tests/ModuleCredentialLookupTests.cs` (new), `ExchangeAdminWeb.Tests/ConferenceRoomWriteIdentityTests.cs`,
this record. `ModuleCredentialService` is shared: a base app version change as well as the Conference Rooms one.

## Fix

- `Services/ModuleCredentialService.cs`: `ModuleCredentialFailure` (`None`, `ConfigUnreadable`, `SecretIdNotSet`,
  `NotReturned`) and `ModuleCredentialLookup` (the credential, and the failure) beside the service;
  `LookupCredentialsAsync(moduleId, purpose)` is the old body with each return carrying its failure - the same two log
  lines in the same places, Delinea asked as before. `GetCredentialsAsync` is `(await LookupCredentialsAsync(moduleId,
  purpose)).Credentials`: its callers get the same values and the same lines.
- `Services/ConferenceRoomService.cs`, on top of aud-a2w (`06c68f89`):
  - `protected virtual Task<ModuleCredentialLookup> LookupDirectoryCredentialAsync(string purpose)`, new, beside
    `OpenDirectoryRunspace`: the credential service's `LookupCredentialsAsync("ConferenceRooms", purpose)`; with no credential
    service, the base's `GetModuleCredentialsAsync(purpose)` - which logs that fault and returns none - read as
    `SecretIdNotSet`, this file's refusal for it as before.
  - `internal static string DirectoryCredentialRefusal(ModuleCredentialFailure failure)`, new, pure: the clause each path's
    refusal opens with - `NotReturned` "The on-prem AD credential for Conference Rooms could not be read from Delinea: the
    AD Delinea Secret ID is set, but Delinea returned no credential for it (the application log has its reason)";
    `ConfigUnreadable` "The on-prem AD credential for Conference Rooms could not be read: the module's configuration could
    not be read, so neither could its AD Delinea Secret ID (the application log records it)"; otherwise today's "On-prem AD
    credential is not configured for Conference Rooms (set the AD Delinea Secret ID in Module Config)".
  - The four lookups call `LookupDirectoryCredentialAsync` and build their refusal as the clause and their own tail, so
    for no secret ID each refusal is today's to the byte. Each line, outcome, step and later statement is unchanged.
- Tests: `ModuleCredentialLookupTests.cs`, new - `TheLookup_SaysWhyItReturnedNoCredential_AndTheOldLookupReturnsNoneAsBefore`
  (5) and, for any test, `OfflineCredentials` (a real `ModuleCredentialService` whose Delinea service has no bootstrap
  credential and whose HTTP client fails and counts any request) and `UnreadableModuleConfigStore`.
  `ConferenceRoomWriteIdentityTests.cs`: the `Service` takes `CredentialFailureFor` / `CredentialFailure` (overriding
  `LookupDirectoryCredentialAsync` for a matching purpose, the base otherwise) and an optional real credential service;
  `NoCredentialFor`'s comment says what it now stands for; a new section -
  `ACredentialThatCouldNotBeRead_IsRefusedWithoutSendingTheOperatorToSetTheId` (8),
  `TheCredentialServicesOwnAnswer_ChoosesTheWords` (3). 16 new cases. No existing assertion changed: aud-a2v's credential
  theory, through `NoCredentialFor`, still reads today's text to the byte.

**Operator-visible changes.**

- A Room Finder row refused because the module's AD credential could not be read says why: "The on-prem AD credential for
  Conference Rooms could not be read from Delinea: the AD Delinea Secret ID is set, but Delinea returned no credential for
  it (the application log has its reason)..." when the ID is set and Delinea returned none, or "... could not be read: the
  module's configuration could not be read, so neither could its AD Delinea Secret ID (the application log records it)..."
  when the module's config is unreadable - on each of the four directory paths, each with its own tail, and so in the row's
  audit and admin notification. Only when no secret ID is set does it say, as before, "set the AD Delinea Secret ID in
  Module Config". The paths' warning lines carry the new words.
- Nothing else: every other module's lookup, every outcome, write, step, audit field and notification is unchanged.

## Guard proof

| Test | What it pins |
| --- | --- |
| `ModuleCredentialLookupTests.TheLookup_SaysWhyItReturnedNoCredential_AndTheOldLookupReturnsNoneAsBefore` (an unreadable module configuration; no secret ID; "0"; "not-a-number"; an ID Delinea returns none for) | part 1: the lookup's failure is `ConfigUnreadable`, `SecretIdNotSet` (three) and `NotReturned`, with no credential; `GetCredentialsAsync` returns null for each, as before; the service's own lines exactly as before (one per lookup where it refuses, none where Delinea does), at Error; no request sent |
| `ConferenceRoomWriteIdentityTests.ACredentialThatCouldNotBeRead_IsRefusedWithoutSendingTheOperatorToSetTheId` (the four paths x `NotReturned`, `ConfigUnreadable`) | part 2 through the seam: each path's outcome as aud-a2v's theory - the preflights refused with nothing written, their message the clause, the path's tail and " Nothing was written."; the writes partial after Set-Place, no `Set-ADUser` / `Set-ADGroup`, the step's `Error` the refusal, "Nothing was written" nowhere; "set the AD Delinea Secret ID" in no message; one Warning, the path's line, "no AD credential was returned for <purpose>: <refusal>", and the only entry carrying the refusal |
| `ConferenceRoomWriteIdentityTests.TheCredentialServicesOwnAnswer_ChoosesTheWords` (no secret ID; an ID Delinea returns none for; an unreadable module configuration) | parts 1 and 2 joined, no seam: a real credential service behind the service's own lookup; the City/State/Country preflight refused, nothing written, in the clause for what the credential service found - "not configured (set ...)" only for no ID; neither test seam asked; no request sent |
| `ConferenceRoomWriteIdentityTests.ACredentialTheLookupDoesNotReturn_IsLoggedOnce_NamingTheRoomTheListAndThePurpose` (aud-a2v's, unchanged) | no secret ID: each path's refusal today's to the byte |

**Mutations** (`scratchpad/round/round.ps1`, rows `rows-a2x.ps1`, log `round-a2x.log`; 2026-10-23 07:27-07:33), filter
`ConferenceRoom|ModuleCredentialLookupTests` (378 cases), the projects rebuilt each row, failures read from the TRX. md5
before = after: `Services/ConferenceRoomService.cs` `36fd048e1666bac94ef6ea74b54796cf`, `Services/ModuleCredentialService.cs`
`84ba60eef71ceb2a3e1a9ae71da1ee4d` (each restored from the git index, the fix staged, and touched).

| # | Mutation (call site) | md5 mutated | Named test failing | Result |
| --- | --- | --- | --- | --- |
| M1 | the brief's "one text for both again": `DirectoryCredentialRefusal` answering the not-configured clause whatever the failure | `8fd83dd00b01eff2a8d155483099ae63` | `ACredentialThatCouldNotBeRead_...` (all eight); `TheCredentialServicesOwnAnswer_...` (the ID Delinea returns none for; the unreadable configuration) | FAIL, 10 of 378: "On-prem AD credential is not configured ..." where the category's clause belongs. PASS: no secret ID, whose words those are |
| M2a | the City/State/Country preflight's lookup taking the not-configured clause, not its failure's | `00049e87aa562b14980a0e7218d69330` | `ACredentialThatCouldNotBeRead_...` (that preflight, both failures); `TheCredentialServicesOwnAnswer_...` (both failures; it goes through that preflight) | FAIL, 4 |
| M2b | the same, the City/State/Country write | `c5d3a7697f1455bc20c27f06e8978060` | `ACredentialThatCouldNotBeRead_...` (the write, both) | FAIL, 2 |
| M2c | the same, the room-list preflight | `fb9484e36595c0b50e9a0dcdb105e47d` | the same (the room-list preflight, both) | FAIL, 2 |
| M2d | the same, the room-list add | `1aa1598b5e2801be0b7d81a3460d9d91` | the same (the room-list add, both) | FAIL, 2 |
| M3 | `LookupCredentialsAsync` reporting `SecretIdNotSet` for an ID Delinea returned none for | `ModuleCredentialService.cs` `4a066c7c86486c43b014a4c3bc82e709` | `ModuleCredentialLookupTests...` (an ID Delinea returns none for); `TheCredentialServicesOwnAnswer_...` (the same) | FAIL, 2: `SecretIdNotSet`, and the not-configured words |
| M4 | the service's lookup going to the base's `GetModuleCredentialsAsync` even with a credential service - the outcome dropped on the way | `29459b78325da7c86f6dbbf0c9ef9def` | `TheCredentialServicesOwnAnswer_...` (all three) | FAIL, 3: the credential service never asked, the test's base seam answered a credential, the row went on |

No whole-file revert row: the tests name members this fix adds (`ModuleCredentialFailure`, `ModuleCredentialLookup`,
`LookupCredentialsAsync`, `LookupDirectoryCredentialAsync`), so the files as at `06c68f89` do not compile against them; M1,
M2a-d, M3 and M4 are that revert's parts, each at its call site. After the last row the restored tree rebuilt and
`ConferenceRoom*`, `ModuleCredential*`, `Delinea*`, `DirectoryModuleImportTests`, `EmergencyDisable*`, `SelfService*` and
`ClickGate*` passed, 855/855. Test files at the round: `ConferenceRoomWriteIdentityTests.cs`
`93d7f0791240e69461612de506cd9bff`, `ModuleCredentialLookupTests.cs` `1a687605616dc3c99a3cf9eea788bd58` (their comments
written with the tests).

## Known gaps

- The category is coarse by design (plan, part 1): "Delinea returned no credential" covers a missing bootstrap
  credential, an HTTP failure, a secret missing a field and an exception alike; `DelineaService` logs which, and the
  message points there. Carrying the finer reason out would change the Delinea service every module uses.
- Only Conference Rooms consumes the outcome. Other modules' refusals for a missing credential keep their own words, and
  several of them also say "Check the DelineaSecretId configuration" whatever the reason (e.g. `SelfServiceGroupService`,
  `GroupManagementService`); the same class, outside this finding - a candidate in the hand-back.
- With no credential service at all - a host that registered none; not this app's - the service still says "not
  configured", as before: there is nothing to say why. The base logs it.
- The `NotReturned` and `ConfigUnreadable` refusals on the two writes, after Set-Place, are reached only through the seam:
  live they need the write's lookup to fail after its preflight's succeeded (aud-a2v's gap, unchanged).

## Closeout

Fixed on branch `worktree-agent-a56481d2c0a45a8cb`, not merged, not pushed. A module-scoped behaviour change (Conference
Rooms: the words of the four credential refusals when the ID is set) and a shared-code change (`ModuleCredentialService`
gains `LookupCredentialsAsync` and its result types, its old method unchanged in behaviour): the Conference Rooms module
version and the base app version are due at merge - not made here, `ExchangeAdminWeb.csproj` and
`Modules/ModuleCatalog.cs` being outside this brief.

## Comments, branch-tip gates and the merge check

Shared by the branch's three findings, recorded once in `psl-8.md` ("Comments after the round", "Branch-tip gates"): the
comments commit `4e0f61a1`, checked code-identical - this record's are `LookupDirectoryCredentialAsync`'s,
`DirectoryCredentialRefusal`'s and the lookups' notes in `ConferenceRoomService`, and `ModuleCredentialService`'s two;
the Release build, the full suite (5,758 passed, 0 skipped, 0 failed at `4e0f61a1`), this commit's tree less the
live-directory classes (`295c4d19`: 5,712 / 0 / 0), format, `git diff --check`, the ASCII lint, and the merge with master
`909ec552` (clean).
