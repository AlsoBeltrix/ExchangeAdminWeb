# cdt-41: The build carries known-vulnerability warnings (NU1902/NU1903) for three packages

**Severity**: MEDIUM - dependency hygiene. The Release build reports NuGet vulnerability advisories
(NU1902 moderate / NU1903 high) for `Microsoft.AspNetCore.Authentication.Negotiate` 10.0.7,
`SQLitePCLRaw.lib.e_sqlite3` 2.1.11 and `System.Security.Cryptography.Pkcs` 10.0.7 (as the aud-a28u/a26r
agent saw them on 2026-10-22; the exact set is whatever the current restore reports). They sit inside the
61-warning baseline, so no gate sees them.
**Status**: Merged `36da9789`; closed on guard proof (each downgrade fails the build); codex attr-batch-13 raised one HIGH, admitted as aud-a47
**Branch**: `worktree-agent-aca5b5b4f6295736c` (from `18530b58`)
**Commit**: `f42f23aa` (packages, audit settings and their comments, record); plan `5a6e5a5d`
**Reviewer**: implementer (surfaced by the aud-a28u/a26r agent, 2026-10-22, candidate 6 of its hand-back)

## Evidence

At `172902c2`: `dotnet build ExchangeAdminWeb.slnx -c Release` output, the NU1902/NU1903 lines;
`ExchangeAdminWeb.csproj` and `ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj` package references
(direct or transitive - `dotnet list package --vulnerable --include-transitive` names which).

## Approach (to be written by the implementing agent before code)

- Raise each affected package to the first version the advisory names as fixed (a direct reference where it
  is transitive, pinned with the reason in a comment); the app bundles its own SQLite engine, so the
  `e_sqlite3` bump must keep the bundled engine loading (the config-db tests cover it).
- Make the gate see a regression: `<NuGetAudit>` on with `<NuGetAuditLevel>moderate</NuGetAuditLevel>` and
  `<WarningsAsErrors>` including NU1902;NU1903 in the two csproj files (or `Directory.Build.props`), so a
  future advisory fails the build rather than hiding in the warning count. Record the new warning baseline
  count in the hand-back.
- No live call is involved; the gates are the proof. Mutation: downgrade one package and confirm the build
  fails.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 ("fix all the bugs ... EVERYTHING"); admitted from the aud-a28u/a26r hand-back (`d886a1f8`). One
commit `fix(deps): ... (cdt-41)`, third of three on branch `worktree-agent-aca5b5b4f6295736c` (after aud-a28z and
aud-a26u). The packages ship inside the app, so an app-wide change and a base app version change; the supervisor owns
the bump, and the version elements of the csproj are not touched here. The 2026-06-26 SQLite decision
(`.agents/decisions.md`: do not suppress NU1903; a supported update needs its own plan and verification, including
the config-store tests) is met by this plan and its gates, under the admitted finding - no advisory is suppressed.

Evidence at `18530b58`, 2026-10-22: `dotnet list ExchangeAdminWeb.slnx package --vulnerable --include-transitive`
against nuget.org; the dependency paths from each project's `obj/project.assets.json`; the per-version advisory data
from the nuget.org registration pages that restore had just cached (`%LOCALAPPDATA%\NuGet\v3-cache`, read locally,
scratch `cdt41/read-vulns.ps1`). The set is wider than the hand-back saw:

| Package | Resolved | Brought by | Advisories | First version clear of all |
|---|---|---|---|---|
| `Microsoft.AspNetCore.Authentication.Negotiate` | 10.0.7 | the app, directly; the tests through it | GHSA-2p3q-h3hg-jcqq and GHSA-8prm-248r-h957 (both cleared at 10.0.10), GHSA-vrrp-6v32-2p8g (cleared at 10.0.13); all high | 10.0.13 |
| `System.Security.Cryptography.Pkcs` | 10.0.7 | both, transitively: Microsoft.PowerShell.SDK 7.6.1, System.Management.Automation 7.6.1, Microsoft.Windows.Compatibility 10.0.5, MimeKit 4.16.0 (MailKit), MsgReader 6.0.11 | GHSA-846f-f764-76mx high, GHSA-2fmv-rv2c-59x7 moderate | 10.0.13 |
| `SQLitePCLRaw.lib.e_sqlite3` | 2.1.11 | both, transitively: Microsoft.Data.Sqlite 10.0.7 -> SQLitePCLRaw.bundle_e_sqlite3 2.1.11 | GHSA-2m69-gcr7-jv3q high (CVE-2025-6965) | 2.1.12 |
| `System.Security.Cryptography.Xml` | 10.0.6 | the tests only, transitively: Microsoft.PowerShell.SDK, Microsoft.Windows.Compatibility, System.ServiceModel.Primitives (the app's graph prunes it: the ASP.NET Core shared framework carries it) | eight, all high - five cleared at 10.0.10 (GHSA-cvvh-rhrc-wg4q, GHSA-g8r8-53c2-pm3f, GHSA-23rf-6693-g89p, GHSA-8q5v-6pqq-x66h, GHSA-mmjf-rqrv-855v), three at 10.0.13 (GHSA-r955-8q5j-3h83, GHSA-7hqp-7c5m-4vq6, GHSA-c399-9853-6jmj) | 10.0.13 |

The SQLite engine itself, not only the advisory's range (`.agents/state.md` noted on 2026-10-14 that the advisory
named no patched version): each package's win-x64 `e_sqlite3.dll`, loaded alone from the global packages folder
(scratch `cdt41/probe-engine.ps1`), answers `sqlite3_libversion()` 3.49.1 for 2.1.11 (source id 2025-02-18) and 3.53.3
for 2.1.12 (2026-06-26). CVE-2025-6965 is fixed in SQLite 3.50.2, so 2.1.12 carries the fix in its engine.
Microsoft.Data.Sqlite 10.0.13 would bring SQLitePCLRaw 2.1.13; it is not taken here - the brief is the first fixed
version of each affected package, and Microsoft.Data.Sqlite carries no advisory.

Warning baseline at `18530b58` (`dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`): 61 = 36 NU1903 + 4
NU1902 + 21 compiler (12 CS8604, 5 CS8602, 3 CS0649, 1 CS0414). Expected after: the 21 compiler warnings, no NU.

1. `ExchangeAdminWeb.csproj`: Negotiate `10.0.7` -> `10.0.13`; new direct references `System.Security.Cryptography.Pkcs`
   `10.0.13` and `SQLitePCLRaw.lib.e_sqlite3` `2.1.12`, each with a comment naming what brings it, the advisories, and
   when the pin can go (when what brings it asks for that version or later). A new PropertyGroup: `NuGetAudit` true,
   `NuGetAuditMode` all, `NuGetAuditLevel` moderate, `WarningsAsErrors` `$(WarningsAsErrors);NU1902;NU1903;NU1904` -
   NU1904 (critical) with them, since a gate that fails on a moderate or high advisory must fail on a critical one.
2. `ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj`: the same PropertyGroup; a new direct reference
   `System.Security.Cryptography.Xml` `10.0.13`, commented. Pkcs and e_sqlite3 reach it through the project reference.
3. The settings live in the two csproj files, not a `Directory.Build.props`: MSBuild's upward search would also find a
   props file at this root from every nested worktree under `.claude/worktrees/` that has none of its own, so a
   worktree on an older branch would silently take the main checkout's settings.
4. No code change. The engine under the config, jobs and usage databases and the `db` command-line mode moves from
   3.49.1 to 3.53.3. *Assumption, not probed*: dev and prod open one shared config database (Architectural Invariant
   2), so until both run this build two engine versions open it; SQLite's file format is compatible both ways while
   nothing uses a feature the older one lacks, and nothing here changes the schema.

Verification: the brief's gates; `dotnet list ExchangeAdminWeb.slnx package --vulnerable --include-transitive`
reporting nothing; the engine the built app loads read back (`sqlite_version()` through the test output's
Microsoft.Data.Sqlite); `Invoke-Pester tests/ps` against the Release build, whose db-mode backup, check and init
suites drive the app's own engine.

Mutation at the call site (`ExchangeAdminWeb.csproj`, md5 before and after, restored, md5 matched, touched, rebuilt):
M1 Negotiate back to `10.0.7` - the Release build fails, NU1903 an error. Extras: E1 the e_sqlite3 pin removed (2.1.11
again) - fails; E2 the Xml pin removed from the tests csproj - fails.

Files: `ExchangeAdminWeb.csproj`, `ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj`, this record.

## Fix

- `ExchangeAdminWeb.csproj`: `Microsoft.AspNetCore.Authentication.Negotiate` `10.0.7` -> `10.0.13`. A new ItemGroup after
  the package list, commented, with direct references `SQLitePCLRaw.lib.e_sqlite3` `2.1.12` and
  `System.Security.Cryptography.Pkcs` `10.0.13`, each with a comment naming what brings it, the advisories, and when the
  pin can go. A new PropertyGroup after the first, commented: `NuGetAudit` true, `NuGetAuditMode` all,
  `NuGetAuditLevel` moderate, `WarningsAsErrors` `$(WarningsAsErrors);NU1902;NU1903;NU1904`. The first PropertyGroup -
  `VersionPrefix`, `AssemblyVersion`, `FileVersion` among it - is untouched.
- `ExchangeAdminWeb.Tests/ExchangeAdminWeb.Tests.csproj`: the same PropertyGroup, commented; a direct reference
  `System.Security.Cryptography.Xml` `10.0.13`, commented. Its byte-order mark kept.
- The csproj comments are part of this commit: each pin's reason is the change (Approach: "pinned with the reason in a
  comment"), and the MD5s below are of these bytes.

What moved in the resolved graphs (scratch `cdt41/graph-diff.ps1`: each pair of csproj files - `18530b58`'s and this
commit's - restored in its own folder, the packages of each `project.assets.json` compared):
- the app's (113 packages before and after): Negotiate 10.0.7 -> 10.0.13, its `System.DirectoryServices.Protocols`
  10.0.7 -> 10.0.13, Pkcs 10.0.7 -> 10.0.13, `SQLitePCLRaw.lib.e_sqlite3` 2.1.11 -> 2.1.12;
- the tests' (154 before and after): those four, Xml 10.0.6 -> 10.0.13, and the Negotiate dependencies this graph does
  not prune - `Microsoft.AspNetCore.Connections.Abstractions`, `Microsoft.Extensions.Caching.Abstractions`,
  `.Caching.Memory`, `.DependencyInjection.Abstractions`, `.Features`, `.Logging.Abstractions`, `.Options` and
  `.Primitives` - 10.0.7 -> 10.0.13.

SQLitePCLRaw's managed packages (core, provider, bundle) stay 2.1.11: the lib package is the native engine alone and
declares no dependencies. The pair works - scratch `cdt41/probe-built.ps1` opens an in-memory database through the
Release output's own Microsoft.Data.Sqlite 10.0.7 and SQLitePCLRaw 2.1.11 assemblies, its
`runtimes/win-x64/native/e_sqlite3.dll` loaded (SHA-256 `B7385D722C83FB52142A00477A726723745916D22A555711EE89834C1111FB2E`,
the 2.1.12 package's): `sqlite_version()` 3.53.3, `sqlite_source_id()` "2026-06-26 20:14:12 d4c0e51e...", for the app's
output and for the tests'. `dotnet list ExchangeAdminWeb.slnx package --vulnerable --include-transitive`: "The given
project `ExchangeAdminWeb` has no vulnerable packages given the current sources.", and the same for
`ExchangeAdminWeb.Tests`.

Caller-visible change, by design: the app ships Negotiate 10.0.13, Pkcs 10.0.13, System.DirectoryServices.Protocols
10.0.13 and the SQLite 3.53.3 engine (was 3.49.1) under its config, jobs and usage databases and its `db` command-line
mode. A restore that resolves any package with a moderate, high or critical advisory now fails the build - locally, in
CI and in the deploy pipeline's build - instead of warning. The Release warning baseline drops from 61 to 21, none NU.

## Guard proof

Reproduction: the base tree built green with these packages - `dotnet build ExchangeAdminWeb.slnx -c Release
--no-incremental` at `18530b58`: 0 errors, 61 warnings, 40 of them these advisories (36 NU1903, 4 NU1902). That is the
finding: nothing failed.

Same driver as the other two rounds (session scratchpad `mutate/round-cdt41.ps1`, log `mutate/cdt41-mutations.log`):
the csproj's exact bytes saved, one literal replacement that must match exactly once, a Release build of the solution
(its restore included), the exact bytes restored in a `finally`, the MD5 re-checked and the mtime advanced. Here the
build must fail. After the round, a build on the restored bytes: exit 0, 0 errors, 21 warnings.

| Mutation (call site) | File, original MD5 | Mutant MD5 | Build |
|---|---|---|---|
| M1 Negotiate back to `10.0.7` | `ExchangeAdminWeb.csproj`, `BB7E9A1F22C86CA835EF96E2527203C5` | `9B903B20FD5DF1C6D0774716DA4250C0` | fails, exit 1, 6 errors: "error NU1903: Warning As Error: Package 'Microsoft.AspNetCore.Authentication.Negotiate' 10.0.7 has a known high severity vulnerability" - GHSA-2p3q-h3hg-jcqq, GHSA-8prm-248r-h957 and GHSA-vrrp-6v32-2p8g, in each project |
| extra E1 the e_sqlite3 pin removed (2.1.11 again) | `ExchangeAdminWeb.csproj`, `BB7E9A1F22C86CA835EF96E2527203C5` | `9689519C3E0B16FEA1519C2E59CCAFC3` | fails, exit 1, 2 errors: NU1903 `SQLitePCLRaw.lib.e_sqlite3` 2.1.11, GHSA-2m69-gcr7-jv3q, in each project |
| extra E2 the Xml pin removed | `ExchangeAdminWeb.Tests.csproj`, `D88547A7B585606CAB944B9992EAE260` | `6B5440321470B1DABC4C142CEA3A0635` | fails, exit 1, 8 errors: NU1903 `System.Security.Cryptography.Xml` 10.0.6, its eight advisories, in the tests project |

## Gates - 2026-10-22 (this commit's tree)

- `dotnet build ExchangeAdminWeb.slnx -c Release --no-incremental`: 0 errors, 21 warnings - 12 CS8604, 5 CS8602, 3 CS0649,
  1 CS0414, the 21 compiler warnings of the old baseline; no NU warning. The new baseline is 21 (was 61).
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` (Debug; its restore and build ran under the
  new audit settings with no NU line): exit 0, 5,862 passed / 3 skipped (the three known AD skips,
  `ADDirectorySearchServiceTests.*_WhenAdUnavailable_ReturnsEmpty`) / 0 failed, 5,865 total, 6m00s; nothing hung, no
  Sequence file - aud-a26u's tree's count, so every suite ran on the new engine and packages, the config-database ones
  (`SqliteConfigStoreTests`, `ConfigDbCliTests`, `ConfigDbMaintenanceTests`, `ConfigStoreMigratorTests`,
  `ConfigStorePathTests`) among them.
- `Invoke-Pester tests/ps` (Pester 6.2.0 imported first, pwsh 7.6) against the Release build: 478 passed, 0 failed, 0
  skipped, 0 not run (96 s) - the db-mode backup, check and init suites (`SqliteConfigBackup`, `JobStateWarning`,
  `MoveConfigDbToShared`, `InstallerConfigBackup`) running the built app's 3.53.3 engine.
- `dotnet list ExchangeAdminWeb.slnx package --vulnerable --include-transitive`: no vulnerable packages in either project.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: exit 0. `git diff --check HEAD`: exit 0.
  `tools/Test-AsciiOnly.ps1` after `git add -A`: exit 0.

Network: nuget.org only, for restore, the audit's data and `dotnet list --vulnerable`. No AD, EXO, Graph, DHCP or IIS
call, no deployment. No coder self-review verdict is claimed.

## Branch-tip gates - 2026-10-22 (`7698c096`)

As aud-a28z's record: Release build 0 errors / 21 warnings, no NU; the full suite 5,865 / 3 / 0, 5,868 total; Pester
478/0/0 against the Release build; `dotnet list ... --vulnerable --include-transitive` none in either project; format,
`git diff --check HEAD` and `tools/Test-AsciiOnly.ps1` exit 0; a dry `git merge-tree` against master `1410013e` reports no
conflict, and master changed neither csproj file.

## Closeout

Fixed on branch `worktree-agent-aca5b5b4f6295736c`, not merged, not pushed. The packages ship inside the app, so the base
app version is due; no module's own behaviour changed. The bump is the supervisor's, at merge: the csproj's version
elements were outside this brief and are untouched.

## Known gaps

- An advisory published against any package in either graph now fails every build - local, CI's build-test job (a
  docs-only push included) and the deploy pipeline's build - until the package is raised, or the owner accepts it by
  name (`NuGetAuditSuppress`). That is the gate's purpose; it has a cost the day an advisory lands.
- NuGet's audit needs its vulnerability data: with nuget.org unreachable, restore warns NU1900 and audits nothing, and
  the build passes. CI and this host reach it.
- The pins need upkeep. When Microsoft.Data.Sqlite asks for a newer `SQLitePCLRaw.lib.e_sqlite3` (10.0.13 asks for
  2.1.13), the 2.1.12 pin below it is a downgrade - NU1605, an error by default - and has to be raised or removed with
  that bump; a pin at or above what is asked for is redundant. Each comment says when it can go.
- SQLitePCLRaw's managed 2.1.11 beside the native 2.1.12 is a mixed family until Microsoft.Data.Sqlite brings 2.1.12 or
  later; the probe and the config-database suites run it, and the app reaches SQLitePCLRaw only through
  Microsoft.Data.Sqlite (no `.cs` file names `SQLitePCL`).
- *Assumption, not probed*: dev and prod open one shared config database (Architectural Invariant 2), so until both run
  this build two engines open it, 3.53.3 and 3.49.1. SQLite keeps its file format readable both ways while nothing uses a
  feature the older engine lacks, and nothing here changes the schema.
- `NuGetAuditLevel` moderate: a low-severity advisory (NU1901) is not reported.
