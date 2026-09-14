# Decision Rationale Archive

## Archived 2026-09-14 (drift sweep)

The first seven decisions have operative canonical homes linked from `.agents/decisions.md`.
Their original entries follow verbatim. The final SQLite entry is a superseded factual
receipt; its unresolved upgrade work remains live in state and decisions.

### 2026-09-11 - No ADI-specific assumption anywhere, including in reasoning

Status: Active. Scope: app-wide. Owner, verbatim: *"you cannot hard-code any ADI-specific
ANYTHING into ANYWHERE in this app."*

Said in response to an argument, not to a line of code, and that is the point of the entry. The
Cloud Password Reset owner lookup binds only the domain the app host is joined to. I proposed
closing that finding on the grounds that ADI's forest is safe for it: the forest root holds no
mailboxes, all users and mailboxes live in the user domain, and the app host is joined to the
user domain. No domain name appeared in any source file; the assumption lived entirely in the
justification.

**That is still a hard-coded ADI dependency and it is rejected.** An unenforced environment
assumption is worse than a named constant, because a named constant is greppable and a silent
assumption is not. The code does not check it, no test can fail on it, and it becomes false the
moment a server is re-joined or a domain grows mailboxes -- with no signal.

The rule, stated so it is testable:

1. No source file names an ADI domain, host, OU, group or address as behaviour. Test fixtures
   and explanatory comments are exempt; anything that steers a decision is not.
2. **A safety argument may not depend on this environment's shape.** If the answer to "why is
   this safe?" contains a fact about ADI's forest, its host membership, or its naming, the fix
   is not done. Correctness must follow from what the code enforces, on any tenant or forest.
3. Directory scope is discovered at runtime from the host's own forest membership, never named,
   defaulted or configured.
4. Where an environment fact cannot be avoided, the code must **verify it and fail closed**, not
   assume it.

Consequence recorded the same day: the Cloud Password Reset owner lookup becomes a global-catalog
(forest-wide) lookup, with two-or-more matches anywhere in the forest refusing as `Ambiguous`.
See `docs/CloudPasswordReset-Plan.md` (S0 findings) for the binding S1 requirement. Related but
narrower existing rules: Architectural Invariant 1 (the installer is environment-neutral) and
Invariant 3's runtime-config separation. This entry generalises the principle to the whole app.

### 2026-08-31 - Reviewer verification rounds: CRITICAL-only, and only on explicit owner approval

Status: Active. Repo-level override of the codereview playbook's per-finding verification
half (the playbook defers to repo invariants by its own terms; the toolkit-owned playbook
file is unchanged).

Owner ruling 2026-08-31: "critical only, and only with my explicit approval", after the
cost question was raised directly. Evidence for the ruling: no verification round in this
repo's history has ever overturned a coder-probe-proven fix; two rounds failed outright on
the codex transport; the 2026-08-31 fsr-1 round burned a large dispatch and returned
nothing.

What changes: generation (defect-hunt) passes are unchanged. Per-finding reviewer
verification dispatches happen ONLY for CRITICAL findings, and each such dispatch needs
its own explicit owner go - never automatic, whatever the playbook's tier routing says.
Every other finding closes on the coder-side guard proof (revert -> guard fails ->
restore -> passes) recorded in its finding doc.

Applied the same day: fsr-1 (HIGH) closed on its recorded coder proof.

### 2026-08-31 - Self-service is never gated by Protected Group Targets (reverses pgwt AC4)

Status: Active. Reverses AC4 and the self-service half of T2 in
`docs/ProtectedGroupWriteTarget-Plan.md` (S3, commit `1f8f863`); the plan carries a
matching Revision 2026-08-31 note. The GroupManagement (admin-module) target gate is
unchanged and stands.

Owner ruling 2026-08-31, during dev validation of the deployed feature: "protecting
groups from someone who can edit them in ADUC is just inconvenient, not secure" and
"if I own a group, I expect to be able to edit it here. period."

What changes: `SelfServiceGroupService` no longer consults the Protected Group Targets
list; a group owner can always edit the groups they own in Self-Service Groups. The
S3 gate and its tests come out; SelfServiceGroups module version bumps.

Boundary clarification recorded with the ruling: a Protected Group Target secures the
group only against writes made through the app's privileged credential (the admin
module), because self-service eligibility already means the user holds write rights on
the group in AD itself and can bypass the app with ADUC. App-side gating of such users
is inconvenience, not security; real protection for a group is DACL hygiene in AD,
which no app setting can substitute for.

### 2026-07-21 - Adding a new module does not bump the base app version

Status: Active

Adding a new module bumps only that module's own `Version` in `Modules/ModuleCatalog.cs`;
it does not bump the base app version (`<VersionPrefix>` / `AssemblyVersion` / `FileVersion`
in `ExchangeAdminWeb.csproj`). A new module is not a shared-infrastructure change. The two
independent versioning rules (shared/app-wide -> base app version; module-scoped behavior ->
module version) are otherwise unchanged; this only carves out module *addition* from the
shared-change rule.

Reason:
A new module is self-contained -- it adds a catalog descriptor, its own service/page, and its
own module version. Bumping the app-wide version for it overstates the blast radius of the
change and couples every new module to an app-version increment that signals nothing about
shared behavior. Owner directed this 2026-06-26; recorded and applied 2026-07-21.

Supersedes: the prior reading of Constitution Section "Deployment And Versioning" under which any
app-wide addition (including a new module) bumped the base app version. Applied to
`docs/ProjectConstitution.md` Section "Deployment And Versioning" and `.agents/repo-guidance.md`
Section "Versioning".

### 2026-06-17 - Credentials live in the deployment's PAM solution, not hardcoded to Delinea

Status: Active

Decision:
The Constitution's Credential Isolation rule is generalized: every password or privileged
credential (now explicitly including SMTP and ServiceNow service passwords, not only
directory/Exchange/Graph secrets) must come from the deployment's PAM/secret-management
solution and must never sit as plaintext in `appsettings.json` or other config files.
Delinea Secret Server remains the only backend implemented today, and the existing field
names (`DelineaSecretId`, `GraphDelineaSecretId`) stay, but code and docs must not treat
Secret Server as the only *possible* backend. A future deployment may add another (e.g.
CyberArk) or a Windows-protected/encrypted store.

Scope guard:
Do NOT build a new PAM integration (CyberArk etc.) speculatively. Do keep the
credential-resolution seam generic enough that adding a backend later does not require
touching every module. This is a principle/wording change, not an implementation task.

Reason:
Owner direction 2026-06-17. The current deployment configures neither an SMTP nor a
ServiceNow password, so there is no live plaintext exposure today; this records the
intended rule so future deployments and future developers do not hardcode Delinea or
park secrets in config files. Resolves the ProdReadiness `[creds]` medium findings as a
posture decision rather than a code change.

Supersedes:
The Constitution's prior absolute "must come from Delinea Secret Server" phrasing in
§Credential Isolation, which is now the "only backend implemented today" rather than the
only permitted backend.

### 2026-06-10 - Adopt the standard `.agents/` governance layout

Status: Active

Decision:
This repo now uses `AGENTS.md` as the canonical agent guidance, with current state in
`.agents/state.md`, durable decisions in `.agents/decisions.md`, and machine-readable
maps in `.agents/repo-map.json` and `.agents/artifact-manifest.json`. `CLAUDE.md` is a
thin pointer shim that includes `AGENTS.md` so the Claude Code harness keeps working.

Note (2026-08-05, drift sweep):
The two machine-readable maps named above no longer exist. `.agents/repo-map.json` and
`.agents/artifact-manifest.json` were removed by the governance refresh at `9d26b5f`
(2026-07-20) as artifacts matching no shipped version. The rest of the decision stands;
the layout is `AGENTS.md` + `.agents/state.md` + `.agents/decisions.md` +
`.agents/repo-guidance.md`. Dangling pointers to the removed maps were cleared from
`.agents/state.md` and `.agents/repo-guidance.md` in the same sweep.

Reason:
Establishes one canonical guidance location and one discoverable current-state entry
point, reducing drift between harness-specific files and durable repo memory.

Supersedes:
The standalone `CLAUDE.md` agent guide. Its content moved into `AGENTS.md`; the engineering
rulebook `docs/ProjectConstitution.md` was left in place and remains the highest
engineering authority.

### 2026-06-10 - `docs/ProjectConstitution.md` remains the highest engineering authority

Status: Active

Decision:
The Constitution is not migrated or restated in `AGENTS.md`. `AGENTS.md` points to it and
defers to it on all whole-app engineering rules (authorization, credential isolation,
auditing, protected principals, module system, deployment and versioning, the never-do
list).

Reason:
Avoids duplicating a competing copy of the engineering rules. One canonical source per
truth.

### 2026-06-26 - SQLite native-lib advisory CVE-2025-6965: tracked, no action available yet

Status: Active (re-check periodically)

Decision:
The build emits `NU1903` for `SQLitePCLRaw.lib.e_sqlite3` 2.1.11
(CVE-2025-6965 / GHSA-2m69-gcr7-jv3q, High). This is **accepted and tracked, not fixed**,
because there is no patched package to move to: 2.1.11 is the latest published version of
the native lib, and the advisory lists patched version "None" as of 2026-06-26. The flaw is
an upstream SQLite engine bug (aggregate-function handling, fixed in SQLite 3.50.2) not yet
rolled into a released `e_sqlite3` build. It reaches this app only transitively via
`Microsoft.Data.Sqlite` 10.0.7.

Practical risk here is low: exploitation needs attacker-controlled SQL containing malicious
aggregate expressions, and the SqliteConfigStore is a single-writer, app-controlled config DB
with hand-written queries — no untrusted SQL is executed. Severity is High in the abstract;
exposure for this usage is not.

Action when a fix ships: bump `Microsoft.Data.Sqlite` (and/or pin a patched
`SQLitePCLRaw.lib.e_sqlite3`) to a version carrying SQLite ≥ 3.50.2, rebuild, run the full
suite incl. config-store tests, then drop this note. Do NOT suppress `NU1903` in the
meantime — keep the advisory visible.

Reason:
Owner direction 2026-06-26 (document & track) after research confirmed no patched package
exists. Recorded so the recurring build warning is a known, assessed item rather than noise,
and so a future session does not waste effort attempting a non-existent version bump.
