# aud-a2x: Conference Rooms - every credential refusal says "not configured (set the AD Delinea Secret ID)" even when the ID is set and the Delinea lookup failed

**Severity**: LOW - operator guidance. All four directory credential refusals in `ConferenceRoomService`
carry the same text, so when the secret ID is configured and the lookup itself failed (an incomplete
secret, an HTTP error) the operator is sent to set an ID that is already set.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; plan written, in progress
**Branch**: `worktree-agent-a56481d2c0a45a8cb`
**Commit**: -
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

## Guard proof

Pending.
