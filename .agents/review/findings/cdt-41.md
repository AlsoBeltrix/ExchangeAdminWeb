# cdt-41: The build carries known-vulnerability warnings (NU1902/NU1903) for three packages

**Severity**: MEDIUM - dependency hygiene. The Release build reports NuGet vulnerability advisories
(NU1902 moderate / NU1903 high) for `Microsoft.AspNetCore.Authentication.Negotiate` 10.0.7,
`SQLitePCLRaw.lib.e_sqlite3` 2.1.11 and `System.Security.Cryptography.Pkcs` 10.0.7 (as the aud-a28u/a26r
agent saw them on 2026-10-22; the exact set is whatever the current restore reports). They sit inside the
61-warning baseline, so no gate sees them.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; in progress (plan below)
**Branch**: `worktree-agent-aca5b5b4f6295736c`
**Commit**: -
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

## Guard proof

Pending.
