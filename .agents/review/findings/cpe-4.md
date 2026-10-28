# cpe-4: `docs/AdminModuleSpec.md` still reads "Version: 2.0 / Last verified against code: commit cd83823 (2026-06-25)"

**Severity**: LOW, pre-existing - authority-order drift. `.agents/repo-guidance.md` ranks the spec as binding for
`Modules/` and `Components/Pages/` and says to check its version header against the csproj and flag drift.
**Status**: Open - logged, not approved
**Branch**: -
**Commit**: the commit that adds this record (S4 of `docs/CloudPasswordResetEmployeeId-Plan.md`, branch `feat/cpr-employee-id`)
**Reviewer**: implementer (found while drafting the plan, section 14; recorded at its S4)

## Evidence

At `714c4128`: `docs/AdminModuleSpec.md` lines 3-4 read "Version: 2.0" and "Last verified against code: commit cd83823
(2026-06-25)"; `ExchangeAdminWeb.csproj` holds `<VersionPrefix>2.36.0</VersionPrefix>`. The same drift was noted against
`2.24.0` in `.agents/review/module-development-platform-plan-r1.md` and not recorded as a finding there.

Nothing here shows the spec's content to be wrong; the header says it has not been checked since June.

## Approach

Re-verify the spec against the code and update its header, or record which parts are known stale. A docs-only change, but
the spec is a binding source, so the verification is the work. Owner decision on when; not authorised.
