# ppsvc-1: on an unconfigured store, the servicer bypass defaulted to Security:AllowedGroups

**Severity**: HIGH — the most privileged grant in the app defaulted to its widest audience. On a
server where section access had never been configured, every member of the legacy app-wide
`AllowedGroups` was authorised to act on protected principals, silently, with no grant ever made.
**Status**: Verified
**Commit**: `628b257`
**Branch**: — (default-branch mode)

Found by `codereview codex` (gpt-5.5-dzs @ xhigh, standard) over `a378785..025a5c6`, before the
servicer editor had ever been used. Verified against the code before fixing, and found to be
**worse than reported**.

## Evidence

- `Services/SectionAccessService.cs:68` (pre-fix) — with no section-access source,
  `GetGroupsForSection` returns `_allowedGroups` unless the section is fail-closed.
- `Services/SectionAccessService.cs:53-60` — `BuildFailClosedSet` is built **only** from catalog
  `MainPermission` and `GranularPermissions` aliases. A `ProtectedServicer:<moduleId>` key is
  neither, by design, so it was never in the set.
- `Services/ProtectedPrincipalServicerService.cs:104` — `Evaluate` calls
  `GetGroupsForSection(key)` directly.

Trigger: a server where section access has never been configured (`SectionAccessSource.None`) and
`Security:AllowedGroups` is populated. That is the state of a fresh install, and of any deploy
before the first Access save.

## Predicted observable failure

`Evaluate` receives the AllowedGroups list instead of an empty one, finds the caller in it, and
returns `Allow`. A member of a broad group can unblock a protected sender in Blocked Senders, and
the audit record attributes it to a servicer group that nobody ever granted.

## What the review got right, and what it understated

The review framed this as the admin page pre-populating the editor and an admin then saving the
fallback groups as a real grant. That is true and is one route.

**It is not the only route, and not the worst one.** `Evaluate` reads the same method, so the
bypass was live on an unconfigured store **with no admin involvement at all** — nobody had to open
the page, and there would have been no stored row to find afterwards. The reviewer's proposed fix
(make `ProtectedServicer` keys fail-closed in `SectionAccessService`) is correct precisely because
it addresses the service rather than the page; the page-only remedy it also suggested would have
left the real hole open.

## Approach

`GetGroupsForSection` now consults `IsFailClosed(section)`, which is the catalog-derived set **or**
any key under `ProtectedPrincipalServicerService.SectionKeyPrefix`.

Prefix-matched rather than enumerated: the keys are constructed per module at runtime, so there is
no list that could be kept in step. Any future key under the prefix is fail-closed by construction
— the right default for a capability whose entire purpose is to override a protection.

Fixed in the service, not the page: the page is one caller of several, and the gate is the one that
matters.

## Files changed

- `Services/SectionAccessService.cs` — `IsFailClosed` helper; `GetGroupsForSection` uses it.
- `ExchangeAdminWeb.Tests/ProtectedPrincipalServicerAdminUiTests.cs` — 2 tests.

## Guard proof

`AServicerKey_NeverFallsBackToAllowedGroups` and
`OnAnUnconfiguredStore_NoOneCanServiceProtectedPrincipals`.

Behavioural, not source-level: both build a real `SectionAccessService` over an empty, unconfigured
SQLite store with a populated `AllowedGroups`, and the second goes end to end through
`Evaluate` — the decision the gates actually consult, so it fails if the hole is reachable by any
route rather than only by the store read.

The first also asserts that an ordinary non-fail-closed section **still** gets the fallback, so the
fix cannot pass by disabling the legacy behaviour wholesale.

Reverting `IsFailClosed` to the bare `_failClosedSections.Contains` fails both; restoring passes 10.
The revert was confirmed applied to the file before the verdict was trusted. 197 tests across the
servicer, section-access and group-authorization suites pass with the fix, so no existing
authorization behaviour moved.

## Coder dispute (if any)

None on the defect. One correction to its framing, above: the review located it at the admin page,
and it was in the service.

## Known gaps

`Security:AllowedGroups` and `AdminGroups` remain name-based rather than SID-based, so the
cross-domain ambiguity recorded under `sidf-1` still applies to whatever they cover. Pre-existing
and out of scope here; this finding removes the servicer key from their reach entirely.

## Reviewer comments

`Reviewer: codex / @azure-openai-eus2-global/gpt-5.5-dzs / xhigh / standard` (codex-cli 0.146.1,
generation pass over `a378785906c735b3ac4d785ad2ee29fbf9d81287..025a5c6d8b5c2938b73c788ff19d28b63b3d27fc`).
`capability_ok: true`, both SHAs echoed correctly. Verdict `findings` (1) — this one. No other
finding was raised, and the whole-store-replace hazard the prompt pointed at was examined and not
faulted.
