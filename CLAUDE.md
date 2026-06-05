# Project Rules — ExchangeAdminWeb

## Deployment
- **NEVER deploy.** Do not run dotnet publish, do not push to IIS, do not touch the deploy pipeline. Only Michael runs deployments via `tools/deploy-pipeline.ps1`.
- Dev environment uses `-AppAlias ExchangeAdminWebDev -AppPoolName ExchangeAdminWebDev -PublishPath D:\apps\ExchangeAdminWebDev -PathBase /ExchangeAdminWebDev`.

## Task Modes
- **Respect task modes.** REVIEW_ONLY and PLAN_ONLY do not authorize code changes. Do not start coding until explicitly told to implement.
- When asked to assess or review, produce analysis only. Wait for "begin implementation" or equivalent.

## Work Continuity
- **Do not stop working without reason.** Keep going unless genuinely blocked on a decision only the user can make. Do not ask "should I continue?" — just continue.

## Versioning
- Bump the version in `NavMenu.razor` sidebar on every commit.
- **Module version vs app version:** Only bump the base app version (`VersionPrefix` in .csproj) when base-level infrastructure changes. Module-only changes get a module version bump in `ModuleCatalog.cs`, not the app version.
- **Use patch versions for fixes.** Bug fixes, CSS cleanup, and minor corrections are patch bumps (e.g. 2.3.0 → 2.3.1). Minor version bumps (2.3 → 2.4) are for meaningful new features or significant changes. Do not over-increment.

## UI / Styling
- **No gradients.** Ever. Flat solid colors only for backgrounds, sidebars, everything.
- Light mode body should be soft off-white (#f4f5f7), not blinding white.

## Security / Operations
- Never log secret values, OAuth response bodies, bearer tokens, passwords, certificate private-key details, or raw Delinea auth responses.
- Do not introduce cross-module credential reuse. Module credentials are per-module via Delinea Secret Server.
- Do not sneak ServiceNow validation/writeback into a feature unless requested.
- Per-module config corruption must affect only that module.

## Communication
- When asked a question ("why is X like this?"), answer the question first. Do not immediately make code changes. A question is not an instruction.
