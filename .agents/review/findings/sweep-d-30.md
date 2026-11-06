# sweep-d-30: M365 Groups read a guest's address or an alias as "not found" (sweep item 30)

**Severity**: MEDIUM - `.agents/review/live-risk-sweep-2026-10-30.md` item 30: adding a member by name is
`GET /users/{x}?$select=id` (`M365GroupManagementService.cs:303`), which takes a UPN, so a guest's address or a member's
alias reads "not found"; the lookup needs Graph User.Read.All (`:311`, `ModuleCatalog.cs:818`).
**Status**: Fixed on branch (code half); the permission stays a deploy-time check; awaiting review
**Branch**: `fix/sweep-d` (worktree `D:\source\ExchangeAdminWeb-wt\sweep-d`, from master `6ea8963b`)
**Commit**: `f51a9fb0` (`fix(sweep-d, item 30): ...`) | **Reviewer**: implementer (candidate)

## Approach (as built)

- `ResolveDirectoryObjectIdAsync`: on a 404 for an address (contains `@`), `ResolveByAddressAsync` asks
  `/users?$filter=mail eq '<a>' or proxyAddresses/any(p:p eq 'smtp:<a>')&$select=id&$count=true&$top=10` with
  `ConsistencyLevel: eventual` (an advanced query). The address is an OData literal (a quote doubled). Exactly one user
  must match: ids are de-duplicated (one user can match by mail and by proxy), the count is the directory's
  `@odata.count` when it gives one (`$top` caps the list, not the matches); none -> "'<a>' was not found in the
  directory as a user principal name, a mail address or a proxy address."; several -> "'<a>' matches N directory users
  by mail or proxy address ... Enter the user principal name of the one you mean.". A 403 on either lookup keeps
  "Not permitted to look up '<a>' ... User.Read.All ...". A name without `@`, and a found UPN, make no second request.
- `GraphTokenClient.GetWithStatusAsync(endpoint, headers, ct)`: a new overload taking extra request headers; it refuses
  an `Authorization` header (the client's own). The existing overload delegates to it. Shared code; base app stays
  `2.36.0` (undeployed).
- Help: the GraphDelineaSecretId field help, both identity placeholders ("UPN, email or alias address to add..."), and a
  README M365 bullet saying what an address may be, plus User.Read.All in its Requires line. M365GroupManagement version:
  the `1.9.1` bump of sweep-d-22 covers this group.

## Where the code goes beside the fix shape

- None. Assumption, unverified: eventual consistency means a user created moments earlier may not be found by address
  yet; the message names the address and the operator can retry or use the UPN.

## Guard proof (`M365AddressFallbackTests`)

| Mutation | Failing tests |
|---|---|
| A1 no fallback | `AnAddressThatIsNotAUpn_IsFoundByMailOrProxyAddress`, `TheSameUserMatchedByMailAndByProxy_IsOneMatch`, `SeveralMatches_AreRefused_NamingTheCount` (3), `NoMatch_IsNotFound_NamingTheAddress`, `A403OnTheFallback_KeepsTheNotPermittedMessage`, `AQuoteInTheAddress_IsEscapedForOData` |
| A2 first match wins | `SeveralMatches_AreRefused_NamingTheCount` (3) |
| A3 no ConsistencyLevel header | `AnAddressThatIsNotAUpn_IsFoundByMailOrProxyAddress` |
| A4 fallback 403 reported generically | `A403OnTheFallback_KeepsTheNotPermittedMessage` |
| A5 @odata.count ignored | `SeveralMatches_AreRefused_NamingTheCount(count 12)` |
| A6 no de-duplication | `TheSameUserMatchedByMailAndByProxy_IsOneMatch`, `AnAddressThatIsNotAUpn_...` |

`A403OnTheDirectRead_KeepsItsMessage_AndAsksNothingMore`, `ANameThatIsNotAnAddress_IsNotLookedUpByMail`,
`AFoundUserIsNotLookedUpAgain` and `TheHeaderOverload_RefusesToReplaceTheClientsOwnAuthorization` pin behaviour around
the change; the six existing `M365DirectoryReferenceTests` pass unchanged.

## Gates

Run once for the branch at its tip; see the sweep-d hand-back. Not run: a live Graph lookup.

## Manual checks for the deploy list (from the sweep)

- In Entra, App registrations > the module's app > API permissions: confirm User.Read.All (Application).
- After the deploy: add a guest by their own address to a scratch M365 group, then by an alias of a member.
