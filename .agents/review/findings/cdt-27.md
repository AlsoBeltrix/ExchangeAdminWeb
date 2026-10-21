# cdt-27: deploy.ps1 leaves the service account's password in unmanaged memory, never zeroed

**Severity**: LOW. `deploy.ps1`'s `SecureString-ToPlain` copies the app pool account's password out
of its `SecureString` into an unmanaged BSTR (`Marshal.SecureStringToBSTR`) to read it, and never
zeroes or frees that BSTR. The plaintext stays in the elevated deploy process's unmanaged memory until
the process ends - in any crash or memory dump of it - for no purpose. The documented pattern is to
`ZeroFreeBSTR` the pointer in a `finally`, which the installer's own copy of this helper
(`ConvertTo-PlainText`) already does. The managed string handed to `Set-ItemProperty` is a copy .NET
cannot zero; this fix does not and cannot remove that one. Found while fixing cdt-24.
**Status**: Fixed on branch; awaiting merge
**Branch**: `cdt-11-14-installer-deploy` (worktree `agent-a1b04b356d91eeed2`)
**Commit**: `<pending>`
**Reviewer**: implementer (found by the cdt-11..19 agent; not yet confirmed by the supervisor)

## Evidence

`deploy.ps1` at `23e57e5`:

```
113: function SecureString-ToPlain {
114:     param([securestring]$Secure)
115:     [System.Runtime.InteropServices.Marshal]::PtrToStringAuto(
116:         [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure))
117: }
...
349:     $plainPassword = SecureString-ToPlain $ServiceAccountPassword
```

The pointer `SecureStringToBSTR` returns is passed straight into `PtrToStringAuto` and dropped; no
reference to it survives to free it. `tools/Install-ExchangeAdminWeb.ps1:181-190` is the same helper
written correctly:

```
184:     $bstr = [System.Runtime.InteropServices.Marshal]::SecureStringToBSTR($Secure)
185:     try {
186:         return [System.Runtime.InteropServices.Marshal]::PtrToStringAuto($bstr)
187:     } finally {
188:         [System.Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr)
189:     }
```

**Mechanism: nothing observable** - a leak has no error and no output, which is why it survived. Not
reproduced by reading process memory; the defect is that the code drops the only pointer that could
free it, visible in the two lines above.

## Plan (approved by the owner's EVERYTHING go)

1. `SecureString-ToPlain` takes the installer's shape: keep the pointer, read the string in a `try`,
   `ZeroFreeBSTR` it in the `finally`. Same return value; the one caller is unchanged.

Assumption, recorded: the managed `$plainPassword` copy is left as it is - .NET strings are immutable
and cannot be zeroed, and `Set-ItemProperty` needs a string; reducing that exposure would need a
different API, not a fix to this helper.

Constraints: as cdt-24 (5.1, ASCII, LF, no plan mode, no version bump). Files: `deploy.ps1`,
`tests/ps/DeployInvariants.Tests.ps1`, this record.

Guards: a behavioural test that the lifted helper still returns the plaintext of a `SecureString`
(round trip), and a structural one that the pointer from `SecureStringToBSTR` is kept in a variable
and handed to `ZeroFreeBSTR` in the `finally` of the `try` that reads it - the leak itself is not
observable from a test, so the shape is what can be pinned, and the record says so. Mutations: the
`ZeroFreeBSTR` removed; the `23e57e5` shape.

## Approach

As planned: `deploy.ps1` lines 113-124 keep the pointer in `$bstr`, read the string with
`PtrToStringAuto($bstr)` in a `try`, and `ZeroFreeBSTR($bstr)` in its `finally` - the installer's
shape, plus a three-line comment saying why. The one caller (`deploy.ps1:356`,
`$plainPassword = SecureString-ToPlain $ServiceAccountPassword`) is unchanged and gets the same
string.

## Guard proof

| Test (`tests/ps/DeployInvariants.Tests.ps1`) | Pins |
| --- | --- |
| `reads the password out of its SecureString and zeroes the unmanaged copy it made (cdt-27)` | Lifts `SecureString-ToPlain` by AST and round-trips a `SecureString` through it: the exact plaintext comes back. Then the shape: an assignment keeps the `SecureStringToBSTR($Secure)` result in a variable, a `try` after it reads `PtrToStringAuto` of that variable, and that `try`'s `finally` hands the same variable to `ZeroFreeBSTR`. The leak itself is not observable from a test, so the shape is what is pinned |

Mutations (fixed md5 pre-comment `deploy.ps1` `0C8CE8611407EB0A9F5449008497395E`; md5s of the
content as committed, LF), each applied by a script that fails unless every edit matches exactly
once, run against `DeployInvariants.Tests.ps1`, then the original bytes restored, touched, md5
re-confirmed. The pre-comment file ran green before the round (105 passed). Post-comment md5
`01398B82E8BDEC052F691026C8999508` (105 passed; the full gate run 324 passed).

| Mutation | md5 after mutation | Failed |
| --- | --- | --- |
| Z1 the `ZeroFreeBSTR` removed (the `finally` left holding `$null = $bstr`) | `D3D16B5DD57058CAA8703AB344F0DA43` | the cdt-27 test ("Expected regular expression 'ZeroFreeBSTR\(\$bstr\)' to match ..., because the unmanaged copy is zeroed and freed on every path, but it did not match") |
| Z2 the `23e57e5` shape (the whole file back to its pre-fix commit) | `727A6E123E4E8B7E096FCE134BFF976B` | the cdt-27 test ("Expected a value, because the pointer must be kept to be freed, but got $null or empty") |
| Z3 the string read back with `PtrToStringAnsi` - proves the round trip is not decoration | `6EE8BB2D581AA3A0258E25DB7D6DF1C0` | the cdt-27 test ("Expected: 'example-Passw0rd!' But was: 'e'") |

Z3 was added after the comment went in, to show the round-trip assertion bites on its own (Z1 and Z2
fail on the shape assertions; the old helper returned the right string too). It ran against the
pre-comment file like the others; the commented file was then restored, its bytes confirmed by
SHA-256, and re-run green.
