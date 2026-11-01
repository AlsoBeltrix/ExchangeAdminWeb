# cdt-62: the installer casts the pool's identityType to an integer, the exact failure of the 2026-09-08 cutover

**Severity**: HIGH when run - sweep item 11. `tools/Install-ExchangeAdminWeb.ps1` about 920-921: `[int]$existingProcessModel.identityType`;
the cutover failed on this cast ("Cannot convert value SpecificUser to type System.Int32", commit 7ba6b255) while deploy.ps1
matches the text (about 502-504). Not on the pipeline's path; any elevated plan or apply over an existing pool throws before
changing anything. No test covers it.
**Status**: Fixed on branch, verified; closes on guard proof
**Branch**: `fix/sweep-b` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-b`, from master `81ae0762`)
**Commit**: `244fd2a0` (the fix); `f9ed946c` (the comment fix) |
**Reviewer**: implementer (candidate)

## Approach

- Match the text as deploy.ps1 does, then map to 3 or 4; Pester with a stand-in returning "SpecificUser"; mutation: the cast
  restored. Parse under 5.1.

## The fix (implementer, 2026-10-31; branch `fix/sweep-b` from master `81ae0762`)

- **`tools/AppPoolControl.psm1`**: new exported `ConvertTo-AppPoolIdentityType -IdentityType <value> -AppPoolName <name>`.
  The value is matched as text - as `deploy.ps1` (about 504) and `tools/Move-ConfigDbToShared.ps1` (about 208) match it - and
  mapped to the number IIS documents: `LocalSystem`/0, `LocalService`/1, `NetworkService`/2, `SpecificUser`/3,
  `ApplicationPoolIdentity`/4. The name, the number and the number written as text all map (an enum value too: its text is
  its name). Anything else throws, naming the value and the pool, "Nothing was changed": a value that cannot be placed is
  never read as "not a custom identity", which would send `Resolve-AppPoolIdentityPlan` to ApplicationPoolIdentity - A34 by
  another road. Before, such a value threw the cast's own error; now it throws a refusal in words.
- **`tools/Install-ExchangeAdminWeb.ps1`** (the existing-pool read, about 921): `[int]$existingProcessModel.identityType`
  becomes `ConvertTo-AppPoolIdentityType -IdentityType $existingProcessModel.identityType -AppPoolName $AppPoolName`. The
  module is already imported above it (about 859); `Resolve-AppPoolIdentityPlan` receives the same integer as before for a
  numeric report. Nothing else in the installer moved; it stays standalone (Invariant 1: no deploy.ps1 coupling, the shared
  module only) and pure ASCII, its line endings as they were, and every step's `-PlanOnly` behaviour is unchanged (Invariant 4 - this is the read both modes
  make). `deploy.ps1` is not touched.
- No version bump (ops script only, as aud-a34 and aud-a35). No help text described the cast.

## Guard proof

New `Describe 'cdt-62: the pool identity is read as text'` in `tests/ps/AppPoolControl.Tests.ps1` (20 cases):

| Test | Pins |
| --- | --- |
| `ConvertTo-AppPoolIdentityType` maps (10 cases) | `SpecificUser`, 3, '3' -> 3; `ApplicationPoolIdentity`, 4 -> 4; `LocalSystem`, 0 -> 0; `LocalService` -> 1; `NetworkService`, 2 -> 2 |
| refuses (4 cases) | 'SomethingNew', 7, '' and $null throw, naming the value and the pool, "Nothing was changed" |
| the installer's own read (6 cases) | **the finding**: the installer's `if ($poolExists) { ... Get-ItemProperty ... -Name processModel ... }` statement, lifted from its source by the parser and run unchanged (aud-a38b's technique) over a stand-in `Get-ItemProperty`: one such read exists; a pool reported as "SpecificUser" and as 3 both read as 3 with its user, and the A34 plan keeps that account (PreserveExisting, ACLs to it); "ApplicationPoolIdentity" and 4 read as 4; an unplaced value refuses before anything is changed |

Mutations by `bin/scratch/mutate-pester.ps1` (apply, run `tests/ps/AppPoolControl.Tests.ps1` - 126 cases, all green on the
fixed tree - restore from a saved copy, touch, MD5 compare; log `bin/scratch/cdt62-mutations.log`):

| # | Mutation | File, fixed MD5 | Mutant MD5 | Failed (of 126) | Restored |
| --- | --- | --- | --- | --- | --- |
| C1 (the brief's) | the `[int]` cast restored at the installer's read | installer `4F932DC417E19A27D448FF8E81B50FE1` (first `B3DE49C58F8293544F1C7016B4AA42B3`, before the comment fix below) | `93C02571C023BED77EC6F68364B9669B` (first `D77FD200DC6FB4D245CB3C3832D9BE5C`) | 3, both times: "SpecificUser", "ApplicationPoolIdentity" (the cast's "Cannot convert value" error), and the refusal (the cast's error, not the refusal's words); the integer forms pass, as they must | equal |
| C2 | the converter no longer matches the name `SpecificUser` | module `17770DC5D477D5049D840FE5D76D91A6` | `DA49EE90BF0A0004EBFB25DB04C46FB3` | 2: the converter's SpecificUser case and the installer's | equal |

**Caught by the full Pester gate, fixed in a second commit:** the fix commit's installer comment said the value is matched
"as deploy.ps1 does", and Invariant 1's guard (`DeployInvariants.Tests.ps1`, "is standalone: never references deploy.ps1")
matches that name anywhere in the installer, comments included. The comment now names no other script; C1 and the 5.1 checks
were run again on the final bytes.

**Windows PowerShell 5.1** (`powershell.exe` 5.1.20348, `bin/scratch/cdt62-ps51.log`): the installer, the module and the test
file parse with 0 errors; the module imports and maps "SpecificUser" [String] -> 3, 3 [Int32] -> 3, "3" -> 3,
"ApplicationPoolIdentity" -> 4, 4 [Int32] -> 4, "NetworkService" -> 2, and refuses "SomethingNew" in words; the installer's
lifted read run under 5.1 reads "SpecificUser" and 3 both as identityType 3 with the user.

Gates on this tree: `Invoke-ScriptAnalyzer -Path . -Recurse -Severity Error` 0 errors (the three touched files keep master's
warning counts: module 2, installer 47, tests 2 - the stand-in carries its suppressions); `tools/Test-AsciiOnly.ps1` passed;
`git diff --check` clean. Full `Invoke-Pester tests/ps` (6.2.0) on the final tree: 554 passed, 0 failed, 0 skipped (master
534; this row +20) - the first full run, at `244fd2a0`, failed the one Invariant 1 case above. Branch-tip gates: the canonical
copy is in `kd-91.md`.

## Known gaps

- No IIS was touched and the installer was not run, by instruction (the session is not elevated). That the provider reports
  the name under PowerShell 7 and the number under 5.1 is the cutover's record (7ba6b255) and deploy.ps1's comment; both
  forms map, so either way the read works. The sweep's one-line elevated check ("SpecificUser [System.String]") would
  confirm the form on this host.
