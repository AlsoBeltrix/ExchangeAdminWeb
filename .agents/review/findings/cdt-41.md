# cdt-41: The build carries known-vulnerability warnings (NU1902/NU1903) for three packages

**Severity**: MEDIUM - dependency hygiene. The Release build reports NuGet vulnerability advisories
(NU1902 moderate / NU1903 high) for `Microsoft.AspNetCore.Authentication.Negotiate` 10.0.7,
`SQLitePCLRaw.lib.e_sqlite3` 2.1.11 and `System.Security.Cryptography.Pkcs` 10.0.7 (as the aud-a28u/a26r
agent saw them on 2026-10-22; the exact set is whatever the current restore reports). They sit inside the
61-warning baseline, so no gate sees them.
**Status**: Admitted; approved under the owner's 2026-10-21 "EVERYTHING" go; not started
**Branch**: -
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

## Guard proof

Pending.
