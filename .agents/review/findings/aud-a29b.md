# aud-a29b: revocation is not observable at all while a circuit stays open

**Severity**: HIGH - an operator removed from an allowed AD group keeps every permission they
had, on every page, for as long as their browser tab is open. `aud-a29` closed the
administrative half of this on one page; this is the rest.
**Status**: Merged to master (`cb3be91`, `702f66f`, `0d30442`); codex review 2026-10-21 raised three findings, admitted as aud-a29c (HIGH), aud-a29d (HIGH), aud-a29e (MEDIUM) - fix in progress; live dev check owed
**Branch**: `worktree-agent-aede3a8e8d3e038a3`
**Commit**: `cb3be91` fix, `0d30442` off-switch (merged)

## Evidence

Raised by codex-commercial (gpt-6-astra, max) reviewing `d602cc5`, in answer to a direct
question about whether re-authorizing against a cached principal works.

`Program.cs:119` registers ordinary server components with no custom authentication-state
revalidator. `ServerAuthenticationStateProvider.GetAuthenticationStateAsync` therefore returns
the stored state task, and `WindowsPrincipal.IsInRole` tests the Windows access token rather
than asking the directory. `Authorization/GroupAuthorizationHandler` matches stored group SIDs
against the principal's claims (`:71`, `:117`, `:129`).

**Calling the authorization service again does not refresh group membership.** A Blazor Server
circuit can live for hours, so removing someone from an AD group does nothing until they
reload or the circuit drops.

This is app-wide. `aud-a29` added a per-click re-check to Admin Bulk Jobs and that re-check
does observe a **section-access** change, because that configuration is re-read from the
database. It cannot observe the directory change.

## Predicted observable failure

Someone leaves the team, or is removed from an admin group during an incident. Their open tab
continues to perform every action their former group allowed - across modules - and the audit
records each as a successful action by a named, then-unauthorized operator. Nobody reloads a
page to find out they no longer should have it.

## Options, for the owner

1. **A revalidating authentication state provider** - the framework's intended answer.
   `RevalidatingServerAuthenticationStateProvider` re-checks on an interval and closes the
   circuit when the principal is no longer valid. Fixes every page at once. Costs a periodic
   revalidation per circuit, and the interval is the window.
2. **A directory read per mutating action** - narrow, and only where it is added. One AD round
   trip on every click that changes something, on a path where the operator is already waiting.
   Does not help read-only disclosure.
3. **Accept it and shorten the circuit lifetime** - reduces the window without closing it.

Option 1 is the only one that makes the answer authoritative everywhere, and it is an app-wide
change to how authentication behaves. **It should not be chosen by an agent.**

## Plan (approved by the owner's EVERYTHING go; option 1 selected)

Selection: `.agents/decisions.md` 2026-10-21 ("Every open review follow-up is approved"), which
picks option 1 for this record and leaves the owner a veto before it lands.

**Exec summary.** While a tab is open, the app re-checks against Active Directory that the
operator is still in the groups their access comes from: once when the tab connects, then every
five minutes. If they were taken out of one, their account was disabled, or the check cannot be
completed, that tab stops responding at once and says "This session has ended - reload".
Reloading gives them whatever access they genuinely have now. What it gets: removing someone
from a group, or disabling them, takes effect on every page within five minutes instead of
whenever they next reload. What it costs: one directory read per open tab every five minutes,
plus one each time a tab connects. Biggest risk: it fails closed - if the directory cannot be
read, every open tab ends within one interval, and an operator whose access the directory read
cannot see (an account outside this forest, or a grant through a domain-local group in a domain
other than their own) is signed out at every check. If that happens, the check can be switched
off in configuration and the app restarted - no code change - and switching it off is always
logged and audited (added before review). Approving it authorises this change to how every page
authenticates; it changes nothing about who is allowed what.

**Design.**

- **Compared set.** `C` = every group SID an authorization gate can grant through right now:
  every `section_access` value (all sections, servicer keys included), every module-admin grant,
  and `Security:AllowedGroups` + `Security:AdminGroups` - SID values as they are, `DOMAIN\Name`
  values translated name->SID by the LSA translation `WindowsPrincipal.IsInRole` itself uses (a
  name that maps to nothing is skipped; any other translation failure is a lookup failure).
  Usable domain-account SIDs only (`SectionAccessGroupIdentity.IsUsableGroupSid`). Held `H` =
  `C` intersected with the circuit principal's group claims
  (`GroupMembershipChecker.ExtractGroupClaims`). SID comparison, case-insensitive, never names.
  `H` empty means the token grants nothing: valid, no directory call.
- **Directory read.** New seam `IOperatorMembershipDirectory` (Authorization/), production
  `OperatorMembershipDirectory` (Services/) over the existing
  `ISectionAccessDirectoryCommands` / `PowerShellDirectoryCommands` - no second LDAP client:
  `Get-ADUser -Identity <PrimarySid> -Properties tokenGroups -Server <dnsRoot>`, the server
  found from the operator's NetBIOS domain through the forest's `CN=Partitions` crossRefs
  (`SectionAccessGroupDirectory.ResolveDomainServer`, made internal; a success is cached per
  NetBIOS name, a failure evicts it). Nothing named, defaulted or configured. Fresh runspace per
  read, at most 4 at once, cancellation stops the running command (the `AdSiteDirectory` shape).
  `Enabled` must read as a bool and `tokenGroups` must yield at least one SID, or the read failed.
- **Verdict.** Account disabled -> `AccountDisabled`. Any SID in `H` missing from `tokenGroups`
  -> `MembershipChanged`, carrying those SIDs. Additions never invalidate. No usable PrimarySid,
  the compared set unresolvable, a throw, a cmdlet error, a timeout (60 s, queueing included) or
  any unexpected exception -> `LookupFailed`. All three invalidate.
- **When.** `RevalidatingServerAuthenticationStateProvider` loop every
  `Authentication:RevalidationInterval` (TimeSpan; default `00:05:00`; startup refuses a value
  that does not parse, is below `00:00:30`, or is above `01:00:00` - a bare `5` parses as five
  days). Plus one check in the background when a circuit opens (`CircuitHandler`): under Kerberos
  a reload presents the same group SIDs until Windows renews its ticket, so without it a removed
  operator would get a fresh interval with every reload.
- **Enforcement.** On an invalid verdict the circuit's `CircuitRevocation` is set first and once,
  the audit line is written, then the principal becomes anonymous (the framework's sign-out, or
  the same call on the open check). An anonymous principal alone is not enough here: pages are
  interactive islands under a static router, so nothing re-renders them, a page that authorized
  once keeps its cached user, and a SignalR reconnect (`ComponentHub.ConnectCircuit` ->
  `CircuitHost.SetCircuitUser`) hands the circuit the stale principal back. So (a) a
  `CircuitHandler` inbound-activity gate drops every inbound activity - events, JS interop,
  navigation, root-component updates - once the circuit is revoked, and (b) the provider
  re-implements `IHostEnvironmentAuthenticationStateProvider.SetAuthenticationState`, so a revoked
  circuit is never given an authenticated principal again.
- **What the operator sees.** `Components/Shared/SessionEndedNotice.razor`, an interactive island
  in `MainLayout` (no page is edited, so no ClickGateRegistry keys move), re-rendered by the
  cascading authentication state: a full-window notice "This session has ended", the reason in
  plain words with no group names, and a Reload link that does a full page load
  (`data-enhance-nav="false"`), which re-authenticates.
- **Audit.** One line per revoked circuit through `AuditService.LogModuleAction`: action
  `SessionRevalidation`, category `Authentication`, result Failed, actor and target the operator,
  the circuit's IP, `errorDetail` the reason in words, `extra` = `reason`
  (`MembershipChanged` / `AccountDisabled` / `LookupFailed`), `trigger` (`CircuitOpened` /
  `Interval`) and `removedGroupSids` (SIDs only - `LogSettingsChange` already records group SIDs).
  An audit failure is logged and never blocks the revocation. No admin email.
- **Registration.** Program.cs, after `AddInteractiveServerComponents()`: the interval resolved
  at startup (fatal on refusal, the `ConfigStorePath` pattern); singletons for the options, the
  directory, the inventory and the revalidator; scoped `CircuitRevocation`,
  `AddScoped<AuthenticationStateProvider, MembershipRevalidatingAuthenticationStateProvider>()`
  and `AddScoped<CircuitHandler, CircuitRevocationGate>()`. Negotiate, `GroupAuthorizationHandler`
  and every policy untouched. `ModuleAdminService` gains a read of every grant.
- **Docs.** `appsettings.json.sample` (an `Authentication` block and a note) and README
  "Configuration Details".

**Tests** (xUnit v3 + NSubstitute, fakes only, no directory probe): unchanged membership valid;
removed consulted SID invalidates; added SID valid; removed non-consulted SID valid; disabled
account invalidates; throwing resolver invalidates; timeout invalidates; missing PrimarySid
invalidates; a name-configured admin group is compared by its translated SID; translation
failure invalidates and an unmapped name is skipped; nothing held means no lookup; interval
default, floor, ceiling and unparseable; audit line with the right reason, once per circuit; the
gate drops activity only when revoked; the open check revokes; a reconnect after revocation stays
anonymous; the framework loop signs out on an invalid verdict; container lifetimes; the
production directory read through a fake command seam (routing, caching, parsing, every failure
shape, cancellation); Program.cs and MainLayout tripwires. Each guard is mutated at its call
site with md5 before and after, the named test seen failing, then restored and touched.

**Not covered, by design.** The Self-Service Groups DACL gate reads arbitrary groups out of AD
ACLs, so those are not in `C`. Work already running when a circuit is revoked is not cancelled.
A server-rendered page load under a stale Kerberos ticket still renders; the circuit it opens is
checked on open. Account expiry and lockout are not checked (lockout would let a password spray
end an operator's session).

**Addition before review (coordinator, 2026-10-21): an operator off-switch.** Known gaps 1 and 2
mean a wrong assumption about what PowerShell returns, or an operator whose access rides a
domain-local group from another domain, ends every affected session at its first check - and
without a switch the only remedy would be a code change. `Authentication:RevalidationEnabled`
(true or false; true when absent; anything else refuses startup). When false, the provider stays
registered but `ValidateAsync` returns valid before any lookup, so neither the interval loop nor
the open check reads the directory or revokes. Startup then logs one warning ("Revocation
revalidation is disabled by configuration ...") and writes one audit line: action
`SessionRevalidation`, category `Authentication`, reason `Disabled`, trigger `Startup`, actor
`System`, address `localhost`, target the key, result Failed (so its explanation is kept). The
interval is still validated while the switch is off.

## Files changed

- `Authorization/CircuitRevalidationSettings.cs` (new) - the key, default, bounds, lookup timeout.
- `Authorization/IOperatorMembershipDirectory.cs` (new) - the directory seam and its answer.
- `Authorization/AuthorizationGroupInventory.cs` (new) - the compared set `C`.
- `Authorization/CircuitMembershipRevalidator.cs` (new) - the verdict.
- `Authorization/CircuitRevocation.cs` (new) - per-circuit revocation state; operator messages.
- `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs` (new) - the provider.
- `Authorization/CircuitRevocationGate.cs` (new) - inbound-activity gate and the open check.
- `Services/OperatorMembershipDirectory.cs` (new) - the production `tokenGroups` read.
- `Services/SectionAccessGroupDirectory.cs` - `ResolveDomainServer` private -> internal (reused).
- `Services/ModuleAdminService.cs` - `GetAllGrantedGroups()`.
- `Components/Shared/SessionEndedNotice.razor` + `.razor.css` (new) - the notice.
- `Components/Layout/MainLayout.razor` - renders the notice (a layout, not a page: no
  ClickGateRegistry entries exist for it, so nothing re-anchors).
- `Program.cs` - interval resolved at startup; the registrations.
- `appsettings.json.sample`, `README.md` - the `Authentication:RevalidationInterval` key.
- `ExchangeAdminWeb.Tests/CircuitRevalidationTests.cs`,
  `ExchangeAdminWeb.Tests/OperatorMembershipDirectoryTests.cs` (new).
- Off-switch addition (its own commit): `CircuitRevalidationSettings.cs` (`EnabledKey`,
  `ResolveEnabled`, `CircuitRevalidationOptions.Enabled`),
  `MembershipRevalidatingAuthenticationStateProvider.cs` (the switch in `ValidateAsync`,
  `ReportIfDisabled`), `Program.cs` (resolve the key; report at startup), `appsettings.json.sample`,
  `README.md`, `CircuitRevalidationTests.cs`.

Not touched, on purpose: `ExchangeAdminWeb.csproj` (the base version bump is the supervisor's
at merge - this is a base-app change), `Modules/ModuleCatalog.cs`, any page under
`Components/Pages`, Negotiate, `GroupAuthorizationHandler` and every policy.

## Guard proof

Implemented as the Plan, with no change of scope. Nothing below reached a directory: the
directory seam is an NSubstitute fake, the production read runs over a scripted fake
`ISectionAccessDirectoryCommands`, and the section-access / module-admin stores are the real
services over a throwaway SQLite file.

| Test (`CircuitRevalidationTests`, 55 cases, unless noted) | Pins |
| --- | --- |
| `UnchangedMembership_StaysValid`, `AddedSid_StaysValid`, `RemovedSidThatNoGateConsults_StaysValid` | no false revocation: same groups, gained groups, or a lost group no gate reads |
| `RemovedConsultedSid_InvalidatesTheCircuit`, `DisabledAccount_InvalidatesTheCircuit` | the two revocations the finding is about |
| `ThrowingResolver_InvalidatesTheCircuit`, `ResolverThatThrowsBeforeReturningATask_InvalidatesTheCircuit`, `ResolverThatNeverAnswers_InvalidatesWhenTheLookupTimesOut`, `ADirectoryWithNoAnswer_IsALookupFailure`, `AnUnexpectedFailureInsideTheCheck_IsALookupFailure`, `PrincipalWithoutAnAccountSid_InvalidatesWithoutTouchingTheDirectory`, `NameTranslationFailure_InvalidatesTheCircuit` | fail closed on every failure shape |
| `OperatorHoldingNoConsultedGroup_IsNotLookedUp`, `TheLookupIsKeyedByTheAccountSidAndRoutedByTheAccountsOwnDomain` | no lookup when nothing is held; SID-keyed, domain-routed |
| `NameConfiguredAdminGroup_IsComparedByItsTranslatedSid`, `SidConfiguredAllowedGroup_IsConsultedWithoutTranslation`, `UnmappedNameConfiguredGroup_IsSkipped`, `ModuleAdminGrant_IsConsulted`, `TheInventoryHoldsOnlyUsableDomainSids` | what the compared set holds |
| `Invalidation_WritesOneAuditLineWithItsReason` (3 reasons), `AnAuditFailure_DoesNotStopTheRevocation`, `ARevocationIsRecordedOnceAndKeepsItsFirstReason`, `ARevokedCircuit_IsNotLookedUpAgain` | one audit line per circuit, every field; revocation before audit |
| `TheFrameworkLoop_SignsTheCircuitOutWhenRevalidationFails` | the real framework loop, on a 50 ms interval, signs out on a false verdict |
| `ARevokedCircuit_IsNeverGivenAnAuthenticatedPrincipalAgain`, `AnUnrevokedCircuit_TakesTheHostPrincipal` | a reconnect cannot restore the stale principal |
| `TheCircuitOpenCheck_RevokesAndSignsOut`, `_LeavesAValidSessionAlone`, `_SkipsAnUnauthenticatedCircuit`, `_FailsClosedWhenThereIsNoStateToCheck`, `_StopsQuietlyOnceTheCircuitIsGone` | the check on circuit open |
| `TheGate_DropsEveryInboundActivityOnceRevoked`, `TheGate_PassesActivityThroughWhileTheCircuitIsValid`, `TheGate_StartsTheCircuitOpenCheck`, `TheGate_LeavesOpenAloneWithoutTheRevalidatingProvider`, `TheGate_RunsAfterTheHandlersThatCaptureCircuitContext` | the inbound-activity gate |
| `TheIntervalDefaultsToFiveMinutes`, `AnIntervalWithinBoundsIsTaken`, `TheIntervalFloorIsEnforced`, `TheIntervalCeilingIsEnforced`, `AnUnparseableIntervalIsRefused` | the interval key |
| `ProgramCs_RegistersTheRevalidatingProviderAndTheGate`, `TheContainer_ResolvesTheRevalidatingProviderOverTheFrameworkDefault` | Program.cs text tripwire; a validated container picks this provider over the framework default and gives each circuit its own revocation |
| `TheLayout_RendersTheSessionEndedNoticeOnEveryPage`, `TheNotice_IsAnInteractiveIslandThatReloadsInFull`, `EveryReasonHasItsOwnPlainMessage` | the operator-facing notice |
| `OperatorMembershipDirectoryTests` (13 tests, 20 cases) | the production read: SID binding, partition routing, cache and its eviction, every `tokenGroups` shape, disabled, every answer that proves nothing (error, error beside a row, none, null row, two, no `Enabled`, no groups), a throwing command, a domain outside the forest, disposal, cancellation stopping the command, name translation |

Mutation at the call site of each guard, by a script that edits one line, takes the md5, rebuilds
(`dotnet build ExchangeAdminWeb.Tests.csproj -c Release`), runs only the named test, then restores
the original bytes, touches the file and re-takes the md5. Every restore was md5-equal to the
original; the restored tree then rebuilt and the two new classes passed 75/75. md5s are of the
pre-comment files (comments were added after this round, as briefed). Original md5s:
revalidator `F107634ED0AEBC4C01B5DBE801F67BB5`, settings `9F63CDB0EBC2FA04C364EBB63F31639B`,
provider `EF62CA53FCD0647EA0E8B35919A58131`, gate `92BFB40D04180B7B178C71F545F01A6D`, revocation
`01695107A74A4CACCE43461E4749B2B8`, inventory `EDD4FA1585407C791504C2947DDFE14F`, directory
`C5F8C0CD75B9A1DCE768C0B6308FFFF2`, `Program.cs` `0742BA0082213D50DC9F3338A6DC919E`,
`MainLayout.razor` `6EE3D00DFE87E0AFEEECE9B3DDF779EE`.

| # | Mutation (file) | md5 mutated | Named test that failed |
| --- | --- | --- | --- |
| M1 | `removed.Count > 0` -> `< 0` (revalidator) | `4D5555A72AA06AFAF2252D95865E3B96` | `RemovedConsultedSid_InvalidatesTheCircuit` |
| M2 | lookup-failure catch returns Valid (revalidator) | `AB405482C294322B8B811E88598D5283` | `ThrowingResolver_InvalidatesTheCircuit` |
| M3 | `WaitAsync(LookupTimeout, ct)` -> `WaitAsync(ct)` (revalidator) | `44E5624FD24C07521FC2B9C524B5F56C` | `ResolverThatNeverAnswers_InvalidatesWhenTheLookupTimesOut` (hit its 20 s timeout) |
| M4 | floor compared to zero (settings) | `6D6D19CB6C3B4FDD042D8A5C6764DE6B` | `TheIntervalFloorIsEnforced` (all 3 cases) |
| M5 | audit call skipped (provider) | `8E97C9D20C853D2954B39E6AE26631F3` | `Invalidation_WritesOneAuditLineWithItsReason` (all 3 reasons) |
| M6 | Program.cs registers `ServerAuthenticationStateProvider` again | `3823B086657F1C11BB17B16725AA1E00` | `ProgramCs_RegistersTheRevalidatingProviderAndTheGate` |
| M7 | gate always calls `next` (gate) | `1FFA76C04CC2A1D68763CF60BEDCBC2A` | `TheGate_DropsEveryInboundActivityOnceRevoked` |
| M8 | disabled check off (revalidator) | `797421D79B0C9EE7AC429F4FE83CF10F` | `DisabledAccount_InvalidatesTheCircuit` |
| M9 | open check not started (gate) | `A7929DF1E76486D12A3A586E3BA63EF3` | `TheGate_StartsTheCircuitOpenCheck` |
| M10 | host principal taken even when revoked (provider) | `77B590A88DC338E830C6146206864E27` | `ARevokedCircuit_IsNeverGivenAnAuthenticatedPrincipalAgain` |
| M11 | `<SessionEndedNotice />` removed (MainLayout) | `7DBDA231305810C5ED37477FF756AB02` | `TheLayout_RendersTheSessionEndedNoticeOnEveryPage` |
| M12 | empty `tokenGroups` accepted (directory) | `C6787A5B2D149203CAAEB460C580712D` | `AnAnswerThatProvesNothing...` ("no group memberships", "no tokenGroups at all") |
| M13 | `-Server` routing removed (directory) | `AB6D16A2C756F629F8696E0CA363591B` | `TheAccountIsReadBySidFromItsOwnDomainWithItsTransitiveGroups` |
| M14 | cmdlet error ignored (directory) | `A163BA116108DCC885A635BB5248FD9C` | `AnAnswerThatProvesNothing...` ("an account and an error") |
| M15 | unreadable `Enabled` read as enabled (directory) | `8BDB85E5DC25170661C5BB3CCC20BF8E` | `AnAnswerThatProvesNothing...` ("an unreadable enabled state") |
| M16 | cancellation does not call `RequestStop` (directory) | `A050880ED4DA5DA7E90E6997B7A9BA69` | `CancellationStopsTheRunningCommand` |
| M17 | every token group compared, not only consulted (revalidator) | `557C5AD0986ED0D2496E3B6E70DA3708` | `RemovedSidThatNoGateConsults_StaysValid` |
| M18 | missing account SID looked up anyway (revalidator) | `3096184CEDD4EA11228A330066DBEBB2` | `PrincipalWithoutAnAccountSid_InvalidatesWithoutTouchingTheDirectory` |
| M19 | configured names not translated (inventory) | `07B240F814591FE65FFC349CACABD1F1` | `NameConfiguredAdminGroup_IsComparedByItsTranslatedSid` |
| M20 | module-admin grants dropped (inventory) | `EEDE57983D13ABC0FEE35906A0AC76E2` | `ModuleAdminGrant_IsConsulted` |
| M21 | stored values not filtered to usable SIDs (inventory) | `09F7642AA202B9EF3ED2DBEC4C95EE85` | `TheInventoryHoldsOnlyUsableDomainSids` |
| M22 | revoked circuit re-validated (provider) | `CA490A5937E55452A14E53031C7A162F` | `ARevokedCircuit_IsNotLookedUpAgain` |
| M23 | second revocation overwrites the first (revocation) | `D4AD9B31EC51EC7761C775B11A24238A` | `ARevocationIsRecordedOnceAndKeepsItsFirstReason` |
| M24 | open check revokes but does not sign out (provider) | `4DE0820B35F0E344321601FCF386D340` | `TheCircuitOpenCheck_RevokesAndSignsOut` |
| M25 | nothing-held shortcut removed (revalidator) | `36A5624B12678CFB96415211EB82EEEC` | `OperatorHoldingNoConsultedGroup_IsNotLookedUp` |

Gates, on the final (commented) tree:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; no warning in any file this change
  touches.
- `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m`: the first run
  passed 4424 of 4424 executed and was then aborted by the hang collector on
  `Comms10kRefusalTests.C6ValidationWaitsKeepTheCapturedTicketVisibleAndRejectDelayedInput` - the
  pre-existing intermittent hang recorded in `.agents/state.md` (recorded there as hanging at an
  unmodified HEAD earlier the same day; its harness substitutes its own authentication provider,
  so it never constructs this one). Not re-run alone here. Rerun with
  `--filter "FullyQualifiedName!~C6ValidationWaitsKeepTheCapturedTicketVisibleAndRejectDelayedInput"`:
  **4562 passed, 3 skipped (the known AD skips), 0 failed, 4565 total**. The same in Release, with
  coverage collected.
- `tools/Test-CoverageFloor.ps1` on that Release run (local, so not CI's denominator):
  security-critical coverage 77.2% (1351 / 1749) against the 65.12% floor. New files: revalidator
  89%, provider 97%, settings, inventory, revocation, gate and seam 100%.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD` (after staging): clean. `tools/Test-AsciiOnly.ps1`: passed.

Not run: anything against a live directory (by instruction) and any browser check of the notice
- see Known gaps.

### Guard proof for the off-switch addition

| Test (`CircuitRevalidationTests`) | Pins |
| --- | --- |
| `DisabledRevalidation_NeverLooksUpAndNeverRevokes` | off: with a directory answer that would revoke, the interval path and the open check both stay valid; no membership read, no name translation, no revocation, no audit line, principal unchanged |
| `DisabledRevalidation_IsReportedAtStartupWithOneWarningAndOneAuditLine` | off: exactly one warning naming the key, exactly one audit line with every field (action, category, reason `Disabled`, trigger `Startup`, result, target, actor, detail) |
| `EnabledRevalidation_ReportsNothingAtStartup` | on: no warning, no audit line |
| `RevalidationIsOnUnlessSaidOtherwise` | options built without the flag are on |
| `TheOffSwitchDefaultsToOn` (3), `TheOffSwitchReadsTrueAndFalse` (5), `AnUnreadableOffSwitchIsRefused` (3) | the key: absent or blank is on; true/false in any case; anything else refuses startup |
| `ProgramCs_ResolvesTheOffSwitchAndReportsItAtStartup` | Program.cs resolves the key, passes it into the options, and reports after `builder.Build()` and before `app.Run()` |

"Enabled is unchanged": every pre-existing test in both classes still passes with the switch on.

Same mutation script and procedure as above; restored md5-equal each time; the restored tree
rebuilt and both classes passed 91/91. Pre-comment originals: provider
`FB6BD81F213C9D741D9E47189C86CB86`, `Program.cs` `A90590BE7B3EA97B2D859D9F94FAB85D`, settings
`61ACD8DE94E5AF41BB0576ADF0783A64`.

| # | Mutation (file) | md5 mutated | Named test that failed |
| --- | --- | --- | --- |
| M26 | the switch made inert: `if (!_options.Enabled && false)` (provider) | `2B8033A97B819C073EDAE8DDD0687CBE` | `DisabledRevalidation_NeverLooksUpAndNeverRevokes` |
| M27 | the startup report call removed (`Program.cs`) | `8A6F1202697FB9C93684CF3E55642CFD` | `ProgramCs_ResolvesTheOffSwitchAndReportsItAtStartup` |
| M28 | an absent key reads as off (settings) | `870F2414E035A2FEE539F3D468064C58` | `TheOffSwitchDefaultsToOn` (all 3 cases) |
| M29 | options default to off (settings) | `CF4FA8B8A7FA3402F638E67D27A4A93A` | `RevalidationIsOnUnlessSaidOtherwise` |

M27 is a source tripwire and proves only that the call is present and placed; that the call
reports is proved by `DisabledRevalidation_IsReportedAtStartupWithOneWarningAndOneAuditLine`.

Gates for the addition, on its final (commented) tree:

- `dotnet build ExchangeAdminWeb.slnx -c Release`: 0 errors; no warning in a touched file.
- `CircuitRevalidationTests` 71 passed (55 before + 16), `OperatorMembershipDirectoryTests` 20
  passed, both Program.cs tripwires passed.
- Full suite, `dotnet test ExchangeAdminWeb.slnx --blame-hang --blame-hang-timeout 10m` with the
  C6 theory excluded as before: **4578 passed, 3 skipped (known AD skips), 0 failed, 4581 total**.
- `dotnet format ExchangeAdminWeb.slnx --verify-no-changes --no-restore`: clean.
- `git diff --check HEAD` (after staging): clean. `tools/Test-AsciiOnly.ps1`: passed.

## Known gaps

- **Not run against a live directory.** By instruction nothing here probed AD, so the
  production read (`Get-ADUser -Properties tokenGroups` through a fresh runspace, the
  `CN=Partitions` routing, `Enabled` and `tokenGroups` arriving through the PowerShell adapter)
  is proved only through a fake command seam. Its first real run is the dev deploy. If those
  properties do not arrive the way the fakes assume, every session holding a consulted group
  ends at its first check with `LookupFailed` - loud and fail-closed, not silent - and the
  audit `error` says which read failed. Nor has the notice been seen in a browser: the repo has
  no component-rendering harness, so its markup is pinned by source tripwires only. Live check
  to run on dev: open a page as an operator with a section-access group, wait one interval,
  confirm no `SessionRevalidation` line; then remove that operator from the group (or disable a
  test account) and confirm the tab shows the notice within one interval, stops responding, and
  one audit line appears. **Mitigation:** set `Authentication:RevalidationEnabled` to `false` and
  restart - configuration, not a code change. Revocation is then off for everyone (the finding is
  open again) until it is set back; the startup warning and the `Disabled` audit line record it.
- **Cross-domain and cross-forest grants fail closed.** `tokenGroups` read from the account's
  own domain does not include domain-local groups of OTHER domains, or memberships through
  foreign security principals in another forest, though the Windows token does. An operator
  granted only that way is signed out at every check. An account outside the host's forest
  cannot be routed at all (no `CN=Partitions` entry) and is signed out at every check too. The
  alternative that sees exactly what the token sees is an S4U logon (`new WindowsIdentity(upn)`)
  - a different mechanism from the one this record was briefed to use, so not taken.
  **Mitigation:** the same off-switch. It is all-or-nothing - there is no per-operator exemption -
  so it trades the affected operators' availability for everyone's revocation until the grant is
  moved to a group the read can see.
- **Availability now depends on the directory read.** An ADWS or domain-controller outage ends
  every open session that holds a consulted group within one interval, and every new one when it
  connects. That is the fail-closed rule asked for; there is no retry before revoking. A long
  outage can be ridden out with the off-switch, on the same terms.
- **A stale Kerberos ticket survives a reload.** The check on connect stops the new circuit
  within the lookup's time, but the server-rendered page itself still renders with the stale
  token, and anything it shows is shown. **Narrowed by aud-a29c:** the new circuit now waits for
  that check - nothing in it initializes, renders or responds to a click until the directory has
  answered, and a circuit the check does not confirm is never initialized. The server-rendered
  page is unchanged: it runs no event handler (there is no circuit), but its initialization runs
  and its output is shown.
- **Not consulted:** the Self-Service Groups DACL gate's groups (arbitrary groups read from AD
  ACLs), and the off-circuit bulk-job snapshot (already documented as not observing mid-job
  revocation in `GroupMembershipChecker`).
- **Work already running is not cancelled** when its circuit is revoked; only new activity is
  refused.
- **The Admin Event Log category filter does not list `Authentication`;** the lines show under
  All. Adding it is a page edit this record does not make.

## Choices the owner can veto

1. **Account disabled ends the session**, beyond group membership. Disabling the account is the
   usual way someone leaves the team, which is the finding's own example. Expiry and lockout are
   not checked.
2. **A check on circuit open**, not only every interval: one directory read per page load that
   opens a circuit. Without it, under Kerberos, a removed operator gets a fresh interval per
   reload. **Changed by aud-a29c** (codex, 2026-10-21): the check no longer runs in the
   background - the circuit is held until it answers (at most the 60 s lookup timeout; a stuck
   check is revoked as `PendingCheckTimedOut` after twice that), and a circuit it does not confirm
   is never initialized, so it shows the error banner rather than the notice.
3. **The circuit is made unusable, not only signed out:** every inbound activity is dropped and
   a reconnect cannot restore the principal. The framework's sign-out alone does nothing visible
   on this app's pages and is undone by a reconnect.
4. **Interval bounds 00:00:30 to 01:00:00; the app refuses to start outside them** (or on an
   unparseable value). The ceiling was not asked for; it stops `5` (five days) silently
   disabling revocation.
5. **Lookup timeout 60 s, at most 4 directory reads at once, no retry** before revoking.
6. **The audit line lists the removed group SIDs** (SIDs only, as `LogSettingsChange` already
   records them), category `Authentication`, result Failed. **No admin email** - an outage would
   otherwise mail once per open tab.
7. **Compared set = every SID any gate grants through**, including module-admin grants and the
   servicer keys, not only the policy handler's sections - so revoking a servicer or module-admin
   membership also ends the session. **Changed by aud-a29d** (codex, 2026-10-21): the set read
   from the store at each check cannot be the only one compared - a grant removed from it simply
   vanished. Each circuit now also keeps the grants (setting and SID) it was confirmed through,
   from the open check on, growing as later checks confirm more; one that is no longer in the
   store ends the circuit (`GrantRemoved`) without a directory read, and its SID stays compared
   with the directory even when a reconnect's token stops carrying it. A store that cannot be
   read is a lookup failure, no longer an empty set.
8. **An operator off-switch exists** (`Authentication:RevalidationEnabled`, added at the
   coordinator's request before review). Anyone who can edit `appsettings.json` and restart the
   app pool can switch revocation off. It is never silent - one startup warning, one `Disabled`
   audit line - but nothing stops it either, and while it is off this finding is open again. Its
   value is the remedy for gaps 1 and 2 without a code change. Choices inside it: an unreadable
   value refuses startup; the interval is still validated while it is off; the audit line is filed
   Failed with actor `System` and address `localhost`.

## Reviewer comments

Source: codex-commercial / `gpt-6-astra` / max, 2026-10-19, finding 1 of the batched review of
`d602cc5`, `1a3c49f`, `87ec8cb`, `7799d6c`.

## Closeout

Fixed on branch `worktree-agent-aede3a8e8d3e038a3` under the owner's 2026-10-21 go (option 1):
the fix `cb3be91`, its record `702f66f`, then the off-switch addition in its own commit.
Pending: codex review of the commits, merge to `master` by the supervisor with the base version
bump and the index row, then the live dev check in Known gaps.
