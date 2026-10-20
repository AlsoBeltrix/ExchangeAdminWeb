# aud-a34: re-running the installer silently took the app's identity away

**Severity**: HIGH. Nothing fails at install time. The app comes back up as a different
security principal and then cannot reach the credential store, the certificate private key,
the directory or the shared config database - several layers away from the command that
caused it.
**Status**: Verified, fixed, independently reviewed, review findings remediated
**Branch**: -- (direct-to-main)
**Commit**: `b071fd6`

## Evidence

Finding 34 of the 2026-10-16 audit, verified against the code.

`tools/Install-ExchangeAdminWeb.ps1` set the pool identity with a two-branch choice driven
by whether a parameter was present:

```powershell
if ($ServiceAccount) {
    Set-ItemProperty ... processModel.identityType -Value 3
    Set-ItemProperty ... processModel.userName -Value $ServiceAccount
    ...
} else {
    Set-ItemProperty ... processModel.identityType -Value 4
}
```

`identityType 4` is `ApplicationPoolIdentity`. The installer documents itself as supporting
an **Update** path, and an update is run against a pool that already exists. So an operator
updating an instance without repeating `-ServiceAccount` moved the pool off its domain
service account and onto a local virtual account - because they did not mention it.

**The second half is worse than the first.** `$appPoolIdentity` was computed from the same
parameter:

```powershell
$appPoolIdentity = if ($ServiceAccount) { $ServiceAccount } else { "IIS AppPool\$AppPoolName" }
```

and every `Set-DirectoryAcl` below - publish, logs, config, the shared config directory -
and the certificate private-key grant all use it. So the run also granted Modify to the
virtual account and left nothing for the account that had been running the app. Both halves
had to move together; fixing only the identity would have preserved the account and then
ACLed the wrong principal, which is the same outage by a longer route.

## Predicted observable failure

An operator updates an instance to a new build:
`.\tools\Install-ExchangeAdminWeb.ps1 -NonInteractive`. The install reports success. The app
restarts as `IIS AppPool\ExchangeAdminWeb`. Delinea lookups fail, so no module can fetch a
credential; the Exchange Online certificate's private key is unreadable; AD binds fail; and
if `ConfigStore:Path` points at a shared directory the account can no longer write there, so
the app cannot start at all. None of it points back at a missing command-line parameter.

## Approach

**An omitted parameter is not an instruction to change anything.** The same shape the
installer already uses for `-RegenerateAppSettings`, which does not rewrite
`appsettings.json` unless told to.

`Resolve-AppPoolIdentityPlan` in `tools/AppPoolControl.psm1` turns the inputs into one of
three outcomes, where there used to be two:

| Situation | Action | ACLs granted to |
| --- | --- | --- |
| `-ServiceAccount` given | `SetCustom` | that account |
| Existing pool on a custom identity, no `-ServiceAccount` | `PreserveExisting` | **the existing account** |
| Same, plus `-ResetAppPoolIdentity` | `SetApplicationPoolIdentity` | the virtual account, with a warning naming what breaks |
| Fresh pool, or already on ApplicationPoolIdentity | `SetApplicationPoolIdentity` | the virtual account, silently |

`PreserveExisting` deliberately issues no `Set-ItemProperty` at all. The installer then takes
`$appPoolIdentity` from `$identityPlan.IdentityForAcl`, so the ACLs follow the decision
rather than the parameter.

The decision is a pure function of its inputs, which is the only reason it is testable: the
surrounding code needs IIS. The installer reads `processModel.identityType` and
`processModel.userName` once, before deciding anything, and passes them in.

`-ResetAppPoolIdentity` is the explicit opt-in, and its warning names the account being
dropped and the four things that stop working.

### Constraints respected

- The module import moved **above** the `-PlanOnly` guard, because a dry run has to report
  the identity decision too (Architectural Invariant 4). A `Write-Plan` line states that an
  existing custom identity is kept.
- Still pure ASCII and still LF; the edit script asserts both, plus that the installer never
  names the deploy script (Invariant 1, enforced by `DeployInvariants.Tests.ps1`).

## Files changed

- `tools/AppPoolControl.psm1` - `Resolve-AppPoolIdentityPlan`
- `tools/Install-ExchangeAdminWeb.ps1` - the switch, the pre-read, the decision, the ACL
  identity, the help text
- `tests/ps/AppPoolControl.Tests.ps1`

No version bump; ops-script-only, same precedent as `aud-a35`.

## Guard proof

The decision function is driven directly. The call site is a source guard, because the code
around it needs IIS.

| Test | What it pins |
| --- | --- |
| `keeps an existing custom identity instead of resetting it` | **the finding** |
| `ACLs the account the app actually runs as, not the virtual account` | the second half, independently |
| `tells the operator how to change it on purpose` | the message names both switches |
| `sets ApplicationPoolIdentity without comment` | a pool already on type 4 is not warned about - nothing is being taken away |
| `defaults to ApplicationPoolIdentity when no account is given` | a fresh install is unchanged |
| `uses the named account when one is given` | the ordinary custom case |
| `switches to a different named account` | changing identity still works |
| `resets to ApplicationPoolIdentity when explicitly asked, and says what breaks` | the opt-in, and that the warning names the dropped account |
| `acts on the decision, not on whether -ServiceAccount was passed` | the call site, and that the old unconditional `else` is gone |
| `grants the folder ACLs to the identity the plan chose` | the two halves cannot drift apart |
| `offers the reset as an explicit switch` | the parameter exists |

| Mutation | Result |
| --- | --- |
| `PreserveExisting` branch disabled (`if ($false)`) | FAIL - 3 tests |
| identity preserved but `IdentityForAcl` set to the virtual account | FAIL - 1 test |
| old unconditional `else { identityType 4 }` pasted back | FAIL - 1 test |

Each mutation confirmed applied before the run - the vacuous round recorded on `aud-a35`
made that check non-optional. Both sources restored byte-identical (md5 verified).

Gate: ASCII lint passed; `Invoke-Pester tests/ps` 179 passed / 0 failed; PSScriptAnalyzer 0
errors; both files parse clean; `git diff --check` clean. No .NET run - no C# or Razor
changed.

## Known gaps

- **No IIS was touched.** That `processModel.identityType` is 3 for SpecificUser and 4 for
  ApplicationPoolIdentity is taken from IIS documentation and from the code being replaced,
  not observed here. If that mapping is wrong, `PreserveExisting` never triggers and the
  behaviour silently reverts to the defect.
- **No installer run was performed**, on a fresh install or an update. The call site is read.
- **Identity types other than 3 and 4 are treated as "not a custom identity"** - LocalSystem
  (0), LocalService (1) and NetworkService (2) would be reset to ApplicationPoolIdentity
  without warning. That is a narrowing of the finding, which named the service-account case;
  whether those deserve the same protection is unexamined.
- **The existing password handling was not revisited.** `$needsPassword` still triggers only
  when `-ServiceAccount` is supplied, which is correct for the three live paths, but the
  interaction between a preserved identity and a rotated password is untested.
- `PSAvoidUsingPlainTextForPassword` and the other analyzer warnings on this installer are
  pre-existing and untouched.

## Closeout

Fixed on `master` as `b071fd6`.

## Independent review - `codex`, 2026-10-20

Reviewed as batch 2 of the backlog, with the other three deployment findings. Prompt kept at
`.agents/review/prompt-backlog-2.md`.

**This record previously said review was "currently impossible".** That was wrong: every
probe had passed `--model gpt-5.5`, stripping the Portkey routing prefix out of the
configured model slug. `.agents/state.md` carries the correction.

**One MEDIUM defect: `-PlanOnly` lied.** It forced `$poolExists = $false`, so the identity
plan could never see an existing custom identity and always reported ApplicationPoolIdentity
and the virtual account's ACLs - the opposite of what an apply run would do. A dry run is
for deciding whether to apply, so a dry run that describes different behaviour is worse than
none. Plan mode now imports WebAdministration (read-only) and reads the pool, and says so
plainly when IIS is unavailable rather than silently assuming a new pool.

**A false claim in this record is corrected**: it said the dry run reports the identity
decision. It did not.

Apply-mode behaviour was found clean: the existing custom identity is preserved, 0/1/2/4 are
treated as non-custom, fresh pools default to ApplicationPoolIdentity, the ACLs follow
`IdentityForAcl`, and a preserved identity does not demand a password.

Remediation landed in the follow-up commit, each fix mutation-proven - including a
"present but dead" mutation, because a guard that only checks a condition exists would pass
with its refusal turned into a warning. The identity-type mapping in
*Known gaps* is the single assumption most worth an independent check.
