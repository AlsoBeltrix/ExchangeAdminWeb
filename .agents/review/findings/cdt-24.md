# cdt-24: deploy.ps1 turns a pool that does not run as a user into an empty custom identity, and icacls grants BUILTIN\BUILTIN

**Severity**: MEDIUM. When the target app pool already exists and `-ServiceAccount` is omitted,
`deploy.ps1` takes the pool's current `processModel.userName` as the account and then sets
`identityType` 3 (SpecificUser) with that name. A pool that runs as ApplicationPoolIdentity,
NetworkService, LocalService or LocalSystem has an empty `userName`, so the pool is re-pointed at a
specific user with no name - an identity IIS cannot start a worker as - and every following grant
(`Set-AclChecked` on the audit log, app log and config folders, and the certificate key) is
`icacls <path> /grant ":<rights>"`, which does not fail: it resolves the empty name to
`BUILTIN\BUILTIN` (the S-1-5-32 domain SID), grants THAT, and exits 0, so the "checked" helper
passes and the script prints "ACL set". The deploy carries on to "Deploy complete" with the
application unable to start and junk ACEs left on its folders. aud-a34 fixed this class in the
installer (an omitted parameter is not an instruction to change the identity); `deploy.ps1` was not
covered. Reachable whenever `deploy.ps1` targets a pool that was not created with a service
account - for example one the generic installer created with ApplicationPoolIdentity - and, under
Architectural Invariant 7, "the pools here always run as a service account" is not a safety
argument. Found while fixing cdt-19.
**Status**: Closed - codex group review (ops-batch-3, 2026-10-21) raised no defect in this change
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `5b7d3ac` (merged)
**Reviewer**: implementer (found by the cdt-11..19 agent; not yet confirmed by the supervisor)

## Evidence

`deploy.ps1` at `f92d0e8`:

```
271: $needsPassword = $false
272: $poolExists = Test-Path "IIS:\AppPools\$AppPoolName"
...
275:     $existingUser = (Get-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel).userName
276:     if (-not $ServiceAccount) {
277:         $ServiceAccount = $existingUser
...
314: Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.identityType    -Value 3
315: Set-ItemProperty "IIS:\AppPools\$AppPoolName" -Name processModel.userName        -Value $ServiceAccount
...
363: Set-AclChecked $auditLogFolder $ServiceAccount "(OI)(CI)M"
```

`identityType` is never read. `Set-AclChecked` (line 64) checks only icacls' exit code.

**Mechanism: no error at all**, reproduced under Windows PowerShell 5.1.20348. Scratch harness: the
real statements from `$needsPassword = $false` through "App pool configured", lifted from the
`f92d0e8` file by the 5.1 parser and run under `Stop` against an existing pool, with only the IIS
calls stubbed:

```
=== existing pool: ApplicationPoolIdentity (identityType 4, no user name), no -ServiceAccount
       Identity:  (credentials retained)
 OK  App pool configured
  processModel.identityType = [3]
  processModel.userName = []
the identity every ACL below is granted to: []
```

and icacls with that identity on a temporary directory:

```
icacls <dir> /grant ":(OI)(CI)M"  -> exit 0, "Successfully processed 1 files"
ACL afterwards: BUILTIN\BUILTIN:(OI)(CI)(M) ... (the inherited entries unchanged)
```

The normal case (an existing SpecificUser pool) behaves correctly in the same harness: the pool keeps
its user.

## Plan (approved by the owner's EVERYTHING go)

1. Read `processModel` once and decide whether the existing pool runs as a specific user:
   `identityType` read as text and matched against `SpecificUser` or `3` - the provider yields the
   name under PowerShell 7 and the number under Windows PowerShell 5.1, as the cutover script
   already records - and a non-blank `userName`.
2. With `-ServiceAccount` omitted and an existing pool that does not run as a specific user:
   `Write-Fail` before anything about the pool is changed, naming what IIS reports and the two ways
   on (pass `-ServiceAccount` with its password to move the pool to an account; or deploy that
   instance with the generic installer, which keeps a pool's identity).
3. With `-ServiceAccount` given and an existing pool that does not run as a specific user, the
   identity is changing even if a stale `userName` happens to match, so the password is required -
   `$needsPassword` is set, as for any other change.
4. Defence in depth: `Set-AclChecked` refuses an empty identity, because icacls would silently grant
   `BUILTIN\BUILTIN`.

Assumptions, recorded: refusing rather than moving the pool to a service account by default -
`deploy.ps1` has no account to move it to without being told; the refusal comes before "Configuring
app pool", so the pool is untouched, but the run has already created its backup directory and, under
`-Force`, copied `appsettings.json` there, so the message says "Nothing was deployed" as the script's
other refusals do.

Constraints: no plan mode in `deploy.ps1`; pure ASCII, no BOM, LF in the index, no ternary, no `??`,
no PS7-only parameters; parsed under Windows PowerShell 5.1. The ADI deploy script may name the
generic installer (the reverse is forbidden). No version bump: ops-script-only.

Files: `deploy.ps1`, `tests/ps/DeployInvariants.Tests.ps1`, this record.

Guards: a behavioural test running the real identity-and-pool statements (lifted by AST) with the IIS
calls stubbed: an existing pool reported as ApplicationPoolIdentity (as `4` and as its name), or as a
specific user with no name, is refused with no IIS change; an existing specific-user pool keeps its
user (as `3` and `SpecificUser`); an existing ApplicationPoolIdentity pool whose stale user name
matches `-ServiceAccount` still requires the password; and `Set-AclChecked` with an empty identity
throws without running icacls. Mutations: the refusal removed; the password requirement for a type
change dropped; the `Set-AclChecked` guard removed; the `f92d0e8` shape.

## Approach

As planned. In `deploy.ps1` after the fix:

- Lines 64-74, `Set-AclChecked`: a comment and the empty-identity refusal before the icacls call.
- Lines 279-301, the existing-pool branch: a comment; `processModel` read once into
  `$existingProcessModel`; `$existingRunsAsUser` (identityType `SpecificUser` or `3`, as text, and a
  non-blank `userName`); with no `-ServiceAccount`, the refusal when the pool does not run as a user,
  else the existing user kept as before; with `-ServiceAccount`, an identity change - and so the
  password requirement - when the pool does not run as a user or runs as a different one, the
  warning naming the identity type in the first case.

The same 5.1 harness as in Evidence, against the fixed file: the ApplicationPoolIdentity pool is
refused before any IIS change ("App pool ExamplePool exists but does not run as a specific user (IIS
reports identityType '4', user name ''), and this script configures every pool with a service
account. Pass -ServiceAccount and its password ... or deploy this instance with
tools/Install-ExchangeAdminWeb.ps1 ... Nothing was deployed."); an existing SpecificUser pool is
configured as before; an ApplicationPoolIdentity pool with a stale user name matching
`-ServiceAccount` now warns "App pool identity changing: identityType '4' -> CONTOSO\svc" and, under
`-NonInteractive` without a password, refuses for the missing password.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `never hands a pool, or icacls, an empty identity (cdt-24)` | Lifts the real statements from `$needsPassword = $false` through "App pool configured" by AST and runs them under `Stop` with `Test-Path`, `Get-ItemProperty`, `New-WebAppPool`, `Set-ItemProperty` and `icacls` stubbed: an existing pool reported as identityType `4`, `ApplicationPoolIdentity`, `2`, or `3`/`SpecificUser` with a blank user, and no `-ServiceAccount`, throws "does not run as a specific user ... Pass -ServiceAccount" with no IIS change recorded; `3` and `SpecificUser` with `CONTOSO\svc` keep that user (`processModel.userName=CONTOSO\svc`); `4` with a stale `CONTOSO\svc` and `-ServiceAccount CONTOSO\svc` throws "ServiceAccountPassword is required"; `Set-AclChecked -Identity ''` throws "empty identity ... BUILTIN\BUILTIN" and icacls is never called |
| `checks the native exit code after every icacls invocation` and `routes icacls through a checked helper that throws on failure` (existing) | Still green: the helper still checks `$LASTEXITCODE` after its icacls call |

Mutations on `deploy.ps1` (fixed md5 `F418D3AA4ABC95EA1D383D19958E8F97`, pre-comment; md5s of the
content as committed, LF), each applied by a script that fails unless every edit matches exactly
once, run against the whole `DeployInvariants.Tests.ps1`, then the original bytes restored,
touched, md5 re-confirmed; the restored file re-run green (102 passed). Post-comment md5
`7D404F85CF2973D029D79FA7C6652ECF` (102 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| F1 the refusal removed (the existing user taken unconditionally again) | `A7B6ADA26D90293A4568F07BDB46604A` | the cdt-24 test ("Expected like wildcard '*does not run as a specific user*Pass -ServiceAccount*' to match $null, because a pool reported as identityType '4' with user '' must not be given an empty identity") |
| F2 the type change no longer requires the password (`elseif ($existingUser -ne $ServiceAccount)`) | `FAD7FAA34915C3D7C1087AE0A2F2A3B4` | the cdt-24 test ("Expected like wildcard '*ServiceAccountPassword is required*' to match $null, because moving a pool to a specific user is an identity change, even when a stale user name matches") |
| F3 the `Set-AclChecked` guard removed | `9D043D7172F9712B766A67690076E0A6` | the cdt-24 test (the empty identity reached the icacls stub; the message was the exit-code one, not "empty identity ... BUILTIN\BUILTIN") |
| F4 pre-fix shape (`f92d0e8`) | `34F8413F9826EDEFA45A5DD502C293B8` | the cdt-24 test (as F1) |

A first F3 was rebuilt: `@(A + 'x', 'y')` parses the comma before the `+`, so its anchor swallowed
both strings, matched nothing, and the edit script refused it - again the exactly-once check doing
its job. Not run on a host: no real pool was reconfigured; IIS's reading of `identityType` is taken
from the cutover script's recorded observation, and the icacls behaviour was observed directly.
