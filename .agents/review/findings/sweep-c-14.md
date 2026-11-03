# sweep-c-14: Migration's Excluded AD Groups check finds the user by any of its addresses, names a shared group name, runs warm (sweep item 14)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 14: on every ToCloud check with the setting
non-empty (it is set on this host, to one group), the check looked the user up by `UserPrincipalName -eq '<typed address>'`
only - a mailbox whose UPN differs from its address (shared and resource mailboxes most often) read "no directory user in
the forest has the user principal name ..." and was Undetermined; a configured name two domains hold refused every check;
a failure cached for the run read as each row's own; and each check opened a fresh runspace and imported the module.
**Status**: Fixed on branch; awaiting review
**Branch**: `fix/sweep-c` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-c`, from master `72611fc2`)
**Commit**: see `git log master..fix/sweep-c` (`fix(sweep-c, item 14): ...`) | **Reviewer**: implementer (candidate)

## Evidence

- At `72611fc2`: `Services/MigrationService.cs` `CheckExcludedGroups` (`:306-366`): `CreateDirectoryRunspace` + `Import-Module`
  per check, `Get-ADUser -Filter "UserPrincipalName -eq ..." -Server <gc>`, `ResolveExcludedGroup` throwing on two or more
  groups, `DirectoryRead<T>.Get()` repeating the cached failure verbatim.

## Approach (as built)

- **User by address, one read.** `Get-ADUser -LDAPFilter (|(userPrincipalName=x)(mail=x)(proxyAddresses=smtp:x))
  -Server <gc:3268>` (`MigrationService.AddressFilter`, LDAP-escaped with `ProtectedPrincipalService.EscapeLdapFilter`;
  proxyAddresses compares without case, so the primary `SMTP:` matches). User objects only. None -> Undetermined "no
  directory user in the forest carries the address ... as its user principal name, mail or proxy address"; several ->
  Undetermined naming each DN. Then the unchanged read in the user's own domain for memberOf, and the exact DN match.
  The DN-substring match of the old build is not back.
- **A name several domains hold.** `ResolveExcludedGroup` returns every group (`ExcludedGroup(Domain, Dn)`); a member of any
  is Ineligible with "Member of excluded group: <name> (<DN>, in <domain>; the excluded group name '<name>' is held by N
  groups across the forest (<domains>); set it by DN or SID to name one)"; a member of none passes the AD step with a
  Warning saying the same; one Warning log line either way. None found, or a domain that cannot be asked, is still
  Undetermined.
- **Cache names its cause.** `DirectoryRead<T>.Get()`: the first row gets the failure; every later row gets it with "(read
  once for this run, at the first row that needed it; a new check reads again)". Still per run.
- **Warm session.** `MigrationService.DirectorySessions` (internal virtual) = `ProtectedPrincipalService.DirectorySessions`,
  the protection check's warm pool (`DirectorySessionPool`, module imported once per session); leased for the check,
  `Fail()`ed on a throw; a lease that times out is "no directory session was free", one that cannot open keeps the old
  "the ActiveDirectory module could not be loaded: ..." text. Reads run as the app pool identity, as before.
  `CreateDirectoryRunspace` is gone. Timed: `DirectoryReadOperation.BeginCheck("Excluded-group check", address)`, every
  read counted.
- **Help (Known Failure Class 5).** The setting's description (Module Configuration) and a README bullet under Exchange
  Migration. **Version.** Migration 1.26.1 -> 1.27.0 with a note.
- **Stand-in.** `MigrationStubExchange`: Get-ADUser `-LDAPFilter` reads each term (UPN, mail, proxy), one shared
  `DirectorySessionPool` over the stand-in, `DirectoryUserWithAddresses`.

## Where the code goes beside the sweep's fix shape

- The brief's "reported by name with the domains instead of refusing silently": the refusal was not silent (it named the
  domains) but it refused every check. Now it is reported and the check completes - a member of any same-named group
  excluded (the fail-closed reading), a member of none passing.

## Guard proof

| Revert | Failing test |
|---|---|
| M1 filter back to UPN only | `AUserWhoseUpnIsNotTheAddress_IsFoundByItsMailOrProxyAddress` (mail, proxy address) |
| M2 a shared name refuses | `ANameTwoDomainsHold_AMemberOfEither_IsExcluded_NamingTheGroupAndTheDomains`, `ANameTwoDomainsHold_AMemberOfNeither_PassesTheCheck_WithAWarningNamingTheDomains` |
| M3 a fresh session per check | `TheCheck_RunsInAWarmSession_TheModuleImportedOnceForSeveralChecks` |
| M4 repeated failure not named | `AForestThatCannotBeReadInARun_IsTheSameAnswerForEveryRow` |

## Gates

Run once for the branch at its tip: see the sweep-c report. Not run: a live check (the sweep's check 1, and the
`(Get-ADForest).Domains | % { Get-ADGroup ... }` read it names) - owner's, at the dev deploy.
