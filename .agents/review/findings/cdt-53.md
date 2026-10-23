# cdt-53: docs/Containerization-Feasibility.md's pointers into app code may be stale

**Severity**: LOW - docs. The cdt-47 pass fixed the ops-script pointers; the pointers into `Program.cs`, `Services/*.cs` and `web.config` were not checked.
**Status**: Verified on branch; awaiting merge (docs only) - admitted 2026-10-23 evening from the cdt-36 agent's third hand-back; inside the released scope
**Branch**: `fix/cdt36` (worktree `D:\source\ExchangeAdminWeb-wt\cdt36`), after the cdt-52 commit `7f083cff`
**Commit**: the `docs(cdt-53)` commit on `fix/cdt36` that carries this record (not merged)
**Reviewer**: implementer (cdt-36 agent, candidate; not yet confirmed by the supervisor)

## Evidence

At `fb2b288f`. Trigger: a reader follows one. Predicted failure: lands on unrelated code.

Confirmed at `7f083cff`. The doc and every file it points into are as they were at `fb2b288f`. Each
line-number pointer into app code was checked against the current file. That covers `Program.cs`,
`Services/*.cs`, `web.config` and also `ExchangeAdminWeb.csproj`, the app's project file, which the
doc points into by line in the same way. Most of these pointers land on unrelated code:

| Doc line | Pointer | Meant to show | What is there now | Where it is now |
| --- | --- | --- | --- | --- |
| 61, 89 | `ExchangeAdminWeb.csproj:4` | the target framework | correct | 4, `<TargetFramework>` |
| 62, 235 | `Services/CredentialManagerService.cs:1,11-12` / `:11-12` | `using Windows.Security.Credentials;`, `new PasswordVault()` | correct | 1, 11 (`ReadCredential`) |
| 63, 233 | `Services/DelineaService.cs:28-46` | `CredentialManagerService.ReadCredential(_credentialTarget)` | the internal constructor. The quoted call no longer exists: the public constructor passes `CredentialManagerService.ReadCredential` in (22) and `LoadCredentials` calls `_readBootstrapCredentials(_credentialTarget)` (45) | 21-24, 43-58 |
| 65, 133 | `Program.cs:38-39` / `:39` | `AddAuthentication(...)` / `.AddNegotiate()` | `var adminGroups = builder.Configuration.GetSection("Security:AdminGroups")...` | 44-45 |
| 66, 131 | `web.config:6-8` | `AspNetCoreModuleV2`, `hostingModel="inprocess"` | correct | 6, 8 |
| 69 | `Services/ADDirectorySearchService.cs:496,551,560` | `iss.ImportPSModule("ActiveDirectory")`, `Get-ADForest`, `host:3268` | a brace, a doc comment, an LDAP filter | 618 (`GetOrCreateRunspace`), 673 and 682 (`ResolveGlobalCatalog`) |
| 70 | `Services/GroupManagementService.cs` (x15) | `Import-Module ActiveDirectory` with `-Credential` | the file has no `Import-Module ActiveDirectory`: it imports through `ActiveDirectoryModuleImport.Import` | its `CreateCredential` and `ActiveDirectoryModuleImport.Import` calls |
| 70 | `Services/SelfServiceGroups/SelfServiceGroupService.cs:947,1472-1478` | the AD import and the `PSCredential` | `return named;` and a loop over DNs | 1279 (`PrepareAdRunspace`), 1545-1551 (`CreateCredential`) |
| 72 | `Services/ExoConnectionPool.cs:422-443` | the runspace, `Import-Module ExchangeOnlineManagement`, `Connect-ExchangeOnline` | the pool lock | 163-168 (`DefaultConnectRunspace`), 765-767 and 791-794 (`CreateConnected`) |
| 72 | `ExchangeAdminWeb.csproj:30` | the `Microsoft.PowerShell.SDK` reference | `<InternalsVisibleTo Include="ExchangeAdminWeb.Tests" />` | 49 |
| 73, 312 | `Services/ExoConnectionPool.cs:507-529` | the `X509Store` lookups | a pool borrow | 963-985 (`FindCertificate`) |
| 74 | `Services/Storage/ConfigStorePath.cs:38-53` | the UNC and drive-relative refusals | the trim, a comment and the start of the UNC refusal | 50-55 and 61-65 (`Resolve`) |
| 326 | `Services/Storage/ConfigStorePath.cs:38-43` | the UNC refusal and its message | the trim and a comment | 50-55 |
| 328 | "Lines 49-53" (of `ConfigStorePath.cs`) | the refusal of "anything not fully qualified" | the UNC refusal. Stale in substance as well: since cdt-35 the rule is a drive letter, a colon and a separator (`IsDriveLetterPath`), stricter than "fully qualified", which also passes a `\??\UNC\` share | 61-65 |
| 74, 330 | `Program.cs:50-64` | the startup refusal | shifted: a comment, then the `try` | 56-67 |
| 75, 359-360 | `Program.cs:82,91,28` | the jobs and usage databases and the log file | a comment and two blank lines | 88 (`jobsDbPath`), 97 (`usageDbPath`), 34 (`WriteTo.File("logs/app-.log"`) |
| 76, 141 | `Program.cs:354-358` | `Application:PathBase`, `UsePathBase` | a circuit-handler registration | 496-500 |
| 213 | `Program.cs:375` | `.RequireAuthorization()` | a comment about the audit-log root | 519-521 (`app.MapRazorComponents<App>()`) |

## Approach

This is a doc check only, and it was written before the edit. Every pointer in the table becomes a
pointer by symbol or by quoted words that occur in the file named: a method, variable or element
name, or a literal. The pointers that are correct today are converted too, because a line number
is what rots. The doc's claims are not re-argued. Three substance fixes go only where the quoted
code itself has changed:

- **Delinea, rows 63 and 233.** The doc quoted `CredentialManagerService.ReadCredential(_credentialTarget)`,
  a call that no longer exists. It now reads that the public constructor passes
  `CredentialManagerService.ReadCredential` in and `LoadCredentials` calls it with `_credentialTarget`.
  "Every secret fetch then fails closed" becomes "every secret fetch retries the read once, then
  fails closed": `GetSecretFieldsAsync` and `GetCredentialsBySecretIdAsync` each call
  `LoadCredentials()` again, and return `null` if it is still empty.
- **Row 70.** "12 services, e.g. `Services/GroupManagementService.cs` (x15)" is a count and an
  occurrence count, and both are stale. `new PSCredential(` occurs in 13 files under `Services/`, and
  GroupManagementService has no `Import-Module ActiveDirectory`. The cell becomes a search pointer
  (`new PSCredential(` under `Services/`) with two examples, and no count, because the code owns the
  count. Each of those 13 files gets its credentials from Delinea or from `ModuleCredentialService`.
  `ActiveDirectoryModuleImport.Import` is named as the shared import.
- **Line 328.** "Lines 49-53 additionally refuse anything not fully qualified" becomes the
  drive-letter rule as coded (`IsDriveLetterPath`), with its reason kept: two instances must not
  believe they share one file while opening two.

Doc check after the edit:

- No `.cs:<digit>`, `.config:<digit>` or `.csproj:<digit>` pointer, and no "line(s) N" pointer into
  app code, remains.
- Each symbol or literal the edit introduces occurs in the file it names.
- `git diff --check` is clean.

Files: `docs/Containerization-Feasibility.md`, this record.

**After the edit**, as made: the table rows at 61-76, and the prose at 89-94, 132-134, 142-143,
214-216, 234-238, 314, 328-334 and 362-363. The doc is still LF and ASCII.

## Files changed

The file before the edit, at `7f083cff` (its blob, then the md5 of its committed LF content), and the
md5 after:

- `docs/Containerization-Feasibility.md`: blob `55077b387922`, md5 `C9E1B630F2BFA2DF329C68973725711B`; after `6EE3C4773C3AE69497C14C2D668398B2`
- this record: copied from master's (`e9db736f`) and filled in

## Guard proof

Not applicable (docs only). The doc check, run on the edited file:

- No `.cs:<digit>`, `.config:<digit>`, `.csproj:<digit>` or `.razor:<digit>` pointer remains.
  The only "lines N" pointer left is outside this finding's scope; see Known gaps.
- 42 symbols and literals were checked against the files they name, and all 42 occur. They
  include:
  - `<TargetFramework>` and the `Microsoft.PowerShell.SDK` `PackageReference`;
  - `LoadCredentials`, `_readBootstrapCredentials(_credentialTarget)` and
    `CredentialManagerService.ReadCredential)`, the method group the public constructor passes;
  - `name="aspNetCore"` and `<aspNetCore`;
  - `GetOrCreateRunspace` and `ResolveGlobalCatalog`;
  - `CreateCredential(` and `ActiveDirectoryModuleImport.Import` in both named services;
  - `CreateConnected(`, `DefaultConnectRunspace` and `FindCertificate(`;
  - `Resolve(`, `IsDriveLetterPath`, "is a network (UNC) path" and the quoted refusal sentence;
  - `ConfigStorePath.Resolve(`, `Log.Fatal(ex.Message);`, `jobsDbPath`, `usageDbPath`,
    `WriteTo.File("logs/app-.log"`, the `pathBase` line, `app.UsePathBase(pathBase);`,
    `app.MapRazorComponents<App>()` and `.RequireAuthorization();`.
- `git diff --check`: clean. No non-ASCII.

Not run: nothing applicable. No test reads this doc.

## Coder dispute

None.

## Known gaps

These are candidates, found and not fixed. They are outside this finding's scope, which is pointers
into app code.

- **An ops-script pointer that cdt-47 missed.** Line 425 reads: "Read its closing "Next steps"
  (lines 752-756)". It means `tools/Install-ExchangeAdminWeb.ps1`, but those lines now hold the
  `Resolve-ConfigStorePlan` call and the cdt-21/cdt-22 comments; the "Next steps" output is at 1178.
  cdt-47 searched for the `file:N` form, and this pointer is written as "lines N-M", so it was
  missed.
- **Counts in prose that the code owns, now stale:**
  - Line 258: "the 12 services that build a `PSCredential` for AD from a Delinea secret".
    `new PSCredential(` occurs in 13 files under `Services/`, one of them `PermissionValidator`.
  - Line 292: "15 source files import it by name". The AD module import now goes partly through
    the shared `ActiveDirectoryModuleImport.Import`.
  - Line 308: the list ending "`ExchangeServiceBase` all build one" omits `PermissionValidator`.
    It also counts `ExchangeServiceBase.ConnectOnPrem`, whose `PSCredential` opens an on-prem
    Exchange session, not an AD module call.

## Reviewer comments

None yet.

## Closeout

Pending: commit on `fix/cdt36`, merge into `master`, branch deletion.
