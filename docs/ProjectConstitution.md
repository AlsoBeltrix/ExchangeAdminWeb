# ExchangeAdminWeb Project Constitution

This document defines the non-negotiable engineering rules for AI-assisted work on ExchangeAdminWeb. It is higher authority than module plans, implementation notes, and ad-hoc chat summaries. The module developer guide explains how to build modules; this document defines what must remain true across the whole application.

## Purpose

ExchangeAdminWeb is an internal administration portal for privileged directory, Exchange, Graph, DHCP, and operational workflows. The app favors explicit configuration, isolated privileges, fail-closed security behavior, clear auditability, and conservative changes over clever automation.

Any AI agent working in this repository must preserve those properties unless the human explicitly approves a change to this document.

## Task Modes

Every significant request should be treated as one of these modes. If the human states a mode, obey it exactly.

- `REVIEW_ONLY`: Inspect code, plans, commits, or docs. Do not change files.
- `DIAGNOSE_ONLY`: Investigate behavior using code, logs, config, and commands. Do not change files.
- `PLAN_ONLY`: Produce or revise a plan. Do not implement.
- `IMPLEMENT`: Make the requested change, verify it, and report results.
- `VERIFY_FIXES`: Review a remediation commit against earlier findings.

If the mode is not stated, infer conservatively from the wording. "Review", "evaluate", "validate", and "diagnose" do not authorize code changes.

## Core Invariants

### Authorization

- Every page with privileged functionality must have a catalog-backed policy.
- Every mutating operation must re-check authorization immediately before the write.
- UI hiding is not security. Direct URL access and direct event invocation must still be denied.
- Module permissions are the gate for that module. Do not reintroduce a global `AllowedGroups` base gate.
- Admin Settings is gated by `AdminGroups` only.
- Fail-closed module permissions deny access when required section access is absent, empty, corrupt, or unreadable.
- Delegated module admins may manage only the modules delegated to them.

### Credential Isolation

- Every password or privileged credential the app uses must come from the deployment's
  PAM (privileged-access / secret-management) solution — never stored as plaintext in
  `appsettings.json` or other config files. This includes service-integration passwords
  such as SMTP and ServiceNow, not only directory/Exchange/Graph secrets. The only
  exception is an operation that is explicitly read-only and approved for ambient
  Windows identity.
- The PAM backend is a deployment choice, not a hardcoded assumption. **Delinea Secret
  Server is the only backend implemented today**, and the concrete field names below
  (`DelineaSecretId`, `GraphDelineaSecretId`) reflect that. Code and docs must not assume
  Secret Server is the *only possible* backend — a future deployment may add another
  (e.g. CyberArk) or, at minimum, a Windows-protected/encrypted store. Do not build a new
  PAM integration speculatively; do keep the credential-resolution seam generic enough
  that adding one does not require touching every module.
- Module credentials are per-module. Do not borrow another module's secret.
- Graph modules use per-module Graph app credentials. Use `GraphDelineaSecretId` for Graph app secrets.
- AD/on-prem modules use per-module AD or on-prem Exchange secrets. Use `DelineaSecretId` for those credentials.
- Shared infrastructure credentials, such as protected-principal directory-read access, must be explicitly named and scoped. Do not hide them in unrelated module config.
- Never log secret values, OAuth response bodies, bearer tokens, passwords, certificate private-key details, or raw PAM/Secret Server auth responses.

### Configuration

- Runtime operational config (module enablement, section access, module settings, protected principals, editable attributes) lives in the SQLite store at `config/exchangeadmin.db`. There are no hand-authored JSON config fragments for these stores.
- Module-specific settings belong to that module's config, not global `appsettings.json`, unless explicitly retained as upgrade fallback.
- Per-module config corruption must affect only that module unless a shared store is actually corrupt.
- If a config store is corrupt or unreadable, fail closed. Do not silently fall back to stale defaults.
- Legacy appsettings fallback is allowed only for upgrade compatibility and only when the module-specific config is absent.
- Config writes must be transactional (SQLite). The atomic temp-file pattern is retired for DB-backed stores.
- Deployment and promotion scripts must preserve runtime config, logs, and state files intentionally. `config/` is excluded from robocopy mirroring; the DB lives there and is backed up via verified online backup before any deploy.
- Startup must not perform destructive writes to the config store. Non-destructive seeding (`INSERT … ON CONFLICT DO NOTHING`) of missing module rows is permitted; overwriting existing rows at startup is forbidden.

### Auditing And Tracing

- Every successful or failed user-facing mutation must write an audit event.
- Audit failure must not make an already-completed operation appear to have failed. Log audit failures separately.
- Audit entries must include actor, IP, action, target, result, and ticket number when the workflow asks for a ticket.
- Operation traces are for diagnostic steps. Audit logs are for who did what.
- UI-visible event logs must not expose backend secrets, raw exception bodies from auth systems, or excessive diagnostic noise by default.
- Long-running or multi-step backend operations should use `OperationTraceService` for start/step/complete visibility.

### Notifications

- Every mutating action (any create, write, delete, or change to a user, mailbox, group, identity state, access state, password, token/session state, or directory attribute) must send an administrator notification via `EmailService`.
- Every security-sensitive read (any module whose purpose is to surface security-relevant data — e.g. lockout/sign-in/audit lookups, protected-object inspection) must send an administrator alert.
- Any change to a user's permissions or access must additionally notify the affected user, not only administrators.
- Notification is in addition to the mandatory audit event, never a substitute for it.
- Notification failure must not change or mask the backend operation result. Catch notification exceptions and log them separately.

### Protected Principals

- Protected-principal checks must fail closed when protection config or required lookup data is unavailable.
- Group protection must be transitive.
- OU and group checks must be based on resolved directory objects, not substring matches.
- Mutations against directory principals should bind to immutable identifiers such as GUID/DN and re-read before write when practical.
- Never bypass protected-principal checks in privileged modules unless the bypass is narrowly scoped, documented, and required for compensation cleanup.

### Module System

- `ModuleCatalog` is the source of truth for module ID, display name, route, category, permissions, dependency, version, and config fields.
- Adding a module should update the catalog, DI registration, page/service files, tests, docs, and module version as applicable.
- Disabled parent modules must cascade-disable dependent modules at runtime, not only in navigation.
- Config-only modules may expose configuration pages but should not appear as normal operational modules.
- Optional modules must be disabled by default unless they are part of the shipped core behavior.

### External Integrations

- ServiceNow integration is out of scope unless the human explicitly includes it in the task.
- Ticket fields are plain audit metadata unless ServiceNow validation or writeback is explicitly requested.
- Microsoft Graph permissions and app registrations are per module unless an explicit shared parent module owns the configuration.
- Exchange Online auth is owned by the Exchange Online parent module and the shared EXO pool.

### Deployment And Versioning

- Shared infrastructure changes bump the base app version.
- Module-scoped behavior changes bump that module's version in `ModuleCatalog`.
- Deployment scripts must support dry-run behavior for destructive promotion steps.
- Production promotion should preserve prod appsettings and prod-specific config values unless explicitly told to overwrite them.

## Code Change Discipline

- Read the relevant existing code before changing it.
- Prefer established local patterns over new abstractions.
- Keep changes scoped to the task.
- Do not refactor unrelated code while fixing a bug.
- Do not add nice-to-have features or speculative future-proofing unless requested.
- Do not revert user changes unless explicitly instructed.
- When deleting dead code, verify there are no real runtime references.
- Use structured parsers/APIs for structured data.
- Avoid string-built shell commands for filesystem operations.

## Review Packet Requirement

Any handoff from a coding agent to a review agent must include a review packet. The reviewer should not rely on commit messages alone.

Required format:

```markdown
# Review Packet

## Mode
REVIEW_ONLY / VERIFY_FIXES / PLAN_REVIEW / IMPLEMENTATION_REVIEW

## Original Request
Verbatim or close paraphrase of the human's request.

## Approved Plan
Path to the approved plan, or "none".

## Commit Range
abc123..HEAD

## Claimed Changes
- What changed.

## Constraints / Out Of Scope
- What must not be changed or considered.

## Design Decisions
- Intentional choices that may look unusual in the diff.
- Why those choices were made.

## Deferred Work
- Known issues not fixed in this slice.

## Review Focus
- Specific risks the coder believes changed.
- What the reviewer should check first.
- What can be ignored for this pass.

## Verification Run
- Build result.
- Test result and count.
- Format result.
- Additional checks or manual validation.
```

The reviewer must first verify intent alignment:

1. Does the code satisfy the original request?
2. Did it stay within the approved plan?
3. Did it add out-of-scope behavior?
4. Did it preserve the core invariants above?

Only after that should the reviewer perform ordinary bug, security, and maintainability review.

## Planning Rules

Use a written plan before implementing:

- new modules
- authorization changes
- credential or Delinea changes
- deployment scripts
- persistence/storage changes
- logging/audit/trace changes
- Exchange service or shared infrastructure refactors
- destructive or high-blast-radius workflows

Small bug fixes, UI polish, docs updates, tests, and obvious compile/runtime fixes do not require a formal plan unless the human asks for one.

## Verification Baseline

For implementation work, run the strongest practical subset of:

- `dotnet build ExchangeAdminWeb.csproj --no-restore`
- `dotnet test ExchangeAdminWeb.Tests\ExchangeAdminWeb.Tests.csproj --no-restore`
- `dotnet format ExchangeAdminWeb.csproj --verify-no-changes --no-restore`
- `git diff --check HEAD`
- targeted manual checks or script dry runs for deployment work

If a verification step cannot be run, report why.

## Blockers

Agents should keep working without human input when the next safe step is clear. Human input is required only when:

- business intent is ambiguous and competing choices have materially different outcomes
- a security or production-risk decision needs acceptance
- credentials, access, or external systems are unavailable
- the human explicitly requested a pause or review gate

Collect blocker questions together. Do not interrupt for one low-value clarification at a time.

## Never Do

- Do not sneak ServiceNow validation/writeback into a feature unless requested.
- Do not introduce cross-module credential reuse.
- Do not make permissions depend on both a module group and a global base group.
- Do not log secrets or raw authentication failure bodies.
- Do not make corrupt config silently fall back to defaults.
- Do not use module UI visibility as the only security control.
- Do not write durable state into locations that deployment or log pruning scripts delete.
- Do not change production deployment behavior without dry-run support and rollback consideration.
- Do not claim a feature is implemented when only a plan exists.
