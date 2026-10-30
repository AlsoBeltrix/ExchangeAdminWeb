# Live-risk sweep, 2026-10-30

Read-only review of master at `280ff007`, asked for after the 2026-10-29/30 dev deploy. That deploy hit three failures in
code that had never run against the real environment: cdt-61 (a 5.1 IIS property), kd-87 (tokenGroups by SID) and
kd-88 (an unanchored LDAP walk). The owner asked: "what other short-sighted changes were made? I don't want to spend
days testing and debugging."

This file answers two things:
- environment dependencies (A-G);
- judgement (H-O): trades, narrow fixes, new refusals, harness-shaped code, inconsistency, noise, scope creep, churn.

Nothing was changed, built or run, and no AD, Exchange, Graph, IIS or Secret Server call was made.

## Range

The brief said 2026-10-20. The records say otherwise:
- **Dev** last ran a build of about `107cfe4f` (2026-10-14 12:49). Its DLL was written 12:53 at FileVersion 2.27.1.0 (`.agents/machines.md`, re-measured 2026-10-23), and nothing was deployed between then and 2026-10-29.
- **Prod** runs `32d28f96` (2026-10-09), per `D:\inetpub\ExchangeAdminWeb\deployment-manifest.json`.
- **Size of the range:**
  - `107cfe4f..280ff007` is 1,270 commits: 188 production files and about 32,600 inserted lines.
  - Prod sees 1,311 commits.
- **Dev now** runs `8557a15a` (manifest, built 2026-10-30 12:17Z).

## What has already run live on dev (new build, since 2026-10-29 13:55)

From `D:\inetpub\ExchangeAdminWebDev\logs\app-20261029.log` and `app-20261030.log`.

Ran:
- Page loads and group-claim authorization.
- The page-request and circuit access re-check after the kd-87 fix:
  - first page after a restart: about 3.1 s;
  - a page with an answer under 5 min old: 6 ms;
  - circuit open: about 0.3 s.
- The `/migration`, `/admin-settings` and `/mailbox-permissions` pages.
- Four Exchange Online pool connections, and one idle sweep with targeted disconnects (0 failures).
- One Mailbox Permissions grant, which got as far as the protected-group check (kd-88): Get-Recipient, AD resolution, the msExchMailboxGuid proof for one migrated mailbox, and three Secret Server reads.
- One silent sign-out (item 5).
- The deploy pipeline's `-Dev` path, twice.

Never run:
- any write;
- Recipient Lookup, Calendar Permissions, Out of Office;
- a Migration check;
- Conference Rooms, Comms-10k, Group Management, Cloud Password Reset, Defender;
- any bulk job;
- any email;
- `-Prod`, `-PlanOnly`, the promotion, the cutover and the installer.

## Baseline volumes (prod, old build, 2026-10-26..29)

These figures size the estimates below.
- About 11 operators a day and 1,800-2,500 page authorizations a day.
- Mailbox Permissions grants: 42 in four days (34 FullAccess+SendAs, 8 FullAccess).
- Calendar sets: 2.
- Migration: 6 eligibility checks and 6 batch creations. Prod ran 105 on-prem ToCloud size reads in October, and 82 of them hit "No archive mailbox found".
- About 3 MFA resets a day and 1 Comms-10k replace.
- About 10 Exchange Online connects a day.
- 86-146 Secret Server secret reads a day.

## Method and limits

- **Reviewers:** twelve parallel read-only reviewers.
  - Eight by area: directory core, directory modules, Exchange pool, Exchange reads, Exchange modules, Graph/vault, ops scripts, app level.
  - Three cross-cutting: consistency, churn/scope, harness.
  - One harvest of every labelled assumption in the records changed since 2026-10-21.
- **Verification:** every HIGH item below was re-read at `280ff007` by the coordinator. MEDIUM and LOW lines are as the reviewers quoted them, with a spot check.
- **Local read-only evidence used:**
  - the dev and prod app logs, and the trace JSONL under `E:\WWWOutput\ExchangeAdminWeb`;
  - both deployment manifests;
  - the app pool entries of applicationHost.config, credentials redacted;
  - a Get-Acl of the dev DLL and folders;
  - selected appsettings keys by grep;
  - the cached Exchange Online module proxy;
  - two keys of the shared config database, read with `grep -a`.
- **Risk scale:**
  - HIGH: likely to fail or refuse on the real environment, or already seen failing.
  - MEDIUM: plausible.
  - LOW: unlikely or cosmetic.
- **Categories:** each item is tagged with its letters, A-O as in the brief.
- **Probes:** the read-only probes are for the owner to run. The standing order "do not probe Active Directory without a direct go" bound this review, not him.

## Findings, ordered by risk

### HIGH

**1. [A F H I] HIGH - kd-88 blocks every write in the app, and its walk has two more callers than the record names.**
- **Why:**
  - Every mutating module calls `ProtectedPrincipalService.CheckAsync`, 25 call sites in 17 modules, which runs the same evaluator (`Services/ProtectedPrincipalService.cs:1557-1558`). Examples: `Components/Pages/MfaReset.razor:388`, `Services/EmergencyDisableService.cs:200`, `Services/LicensingUpdatesService.cs:452` (per bulk row) and `Services/ConferenceRoomProtectionGate.cs:172`.
  - Two more callers run the walk with the operator as the target:
    - the self-grant check, whenever the grantee names a group: `Services/PermissionValidator.cs:887-888` `ProtectedGroupMembership.Evaluate(entries, account.DistinguishedName, directory.GroupsUnder(credential), _logger)`;
    - Self-Service Groups deny settling: `Services/SelfServiceGroups/ResourceDomainDenies.cs:106-107`.
  - A walk holds one of the two slots of `_adThrottle = new(2, 2)` (`ProtectedPrincipalService.cs:192`) for its whole run. While two walks run, every other module's resolution gives up after 30 s (`:447`), and the group check answers "AD service is busy. Please try again." (`:1469`).
- **Effect today:** every write in every module waits about 2 minutes and is refused, and a 100-row bulk file takes about 4 hours and refuses everything.
- **Fix shape:**
  - Put the kd-88 fix inside `ProtectedGroupMembership` / `IProtectedGroupDirectory` so all three callers get it.
  - Point its tripwire at all three: no production use of `ContainsAnyFilter` or `UniversalContainsAnyFilter`.
  - Assumption to settle with it: reading another account's tokenGroups needs that right in the target's domain, and kd-87 proved it only for the app pool reading the operator.
- **Check:** check 5.

**2. [B J] HIGH - Exchange Online's "no such object" is no longer recognised, so Migration checks and on-premises permission targets are refused.**
- **Why:**
  - Since aud-a23h/a23o, a not-found is recognised only from the error record's structure: `Services/ExchangeServiceBase.cs:516` `if (record.CategoryInfo.Category == ErrorCategory.ObjectNotFound || NamesNotFound(record.CategoryInfo.Reason))`. Line `:520` skips the identifier test for an error raised by a throw.
  - The module on this host throws its not-found as bare text. Prod `app-20261012.log` 12:08:23 and dev `app-20261023.log` 04:35:13 both show `RuntimeException: ||The operation couldn't be performed because object '...' couldn't be found on '...'` over an inner `System.Exception`, with no exception type.
  - The cached module proxy sets Reason to 'Exception' unless the response carries an x-ExceptionType header, and its error id has no FailureCategory.
  - The test stand-ins model the old remoting form: `ExchangeAdminWeb.Tests/MigrationStubExchange.cs:195-199` `[FailureCategory=Cmdlet-ManagementObjectNotFoundException]`.
  - The code says so itself at `ExchangeServiceBase.cs:488-491`: "ASSUMPTION, unverified (no live call)".
- **Effect, if the logged shape is what the cmdlets send:**
  - **Migration:** every eligibility check of a mailbox not already in a batch stops at `Services/MigrationService.cs:90-93` (`Get-MigrationUser ... ReadUnlessNotFound`) and reads "Could not be determined". The page then says "Nothing can be created for it until a check succeeds" (`Components/Pages/Migration.razor:91`).
  - **Permissions on on-prem mailboxes:** every Mailbox or Calendar Permissions or Out of Office change on an on-premises mailbox is refused at the location read (`ExchangeServiceBase.cs:882-885`) with "Exchange Online could not be asked: ..." (`:843`).
  - **Approval:** approving a mailbox that has a batch of its own refuses (`MigrationService.cs:1360`).
  - **Conference Rooms:** a Room Finder row for a building without a list refuses (`Services/ConferenceRoomService.cs:423`), while the preview still shows the "new" badge (`Components/Pages/ConferenceRooms.razor:1259`, badge `:204`).
  - **Recipient Lookup:** it says "Could not be read" for an unknown address and for every cloud mailbox without an archive (`Services/RecipientLookupService.cs:96`, `:192`, `:212`).
- **Fix shape:** also accept Exchange's own sentence, as the resolver has since July (`Services/ExchangeIdentityResolver.cs:11` `NotFoundFragment = "couldn't be found"`), or ask with -Filter so that absence comes back as an empty result.
- **Commits:** 5b5936eb, 033a1a54 (aud-a23h), 990a02ab (aud-a23o), 4e2c4e79 (aud-a54), fefb3e6a (aud-a2ac).
- **Check:** checks 1 and 2.

**3. [B J] HIGH (unverified) - on-premises "this mailbox has no archive" must now look like a not-found through remoting, or every ToCloud check of a no-archive mailbox is Undetermined.**
- **Why:**
  - The read is `ExchangeServiceBase.cs:782` `Get-MailboxStatistics -Identity $Identity -Archive -ErrorAction Stop | Select-Object TotalItemSize`. Line `:788` catches only `when (IsNotFound(ex))`.
  - Anything else becomes `:794` "Get-MailboxStatistics -Archive failed: ...", and `MigrationService.cs:225-229` turns that into `MigrationStatus.Undetermined`.
  - The old build caught every throw. "No archive" is a state of an existing mailbox, not a missing object. Two reviewers recall on-premises Exchange filing it under InvalidArgument with a task exception, not a NotFound type (assumption).
  - This is independent of item 2: fixing item 2 does not fix it.
- **Effect:** about 80% of prod's October ToCloud reads hit this case, and none of those mailboxes could get a batch.
- **Commit:** 990a02ab (aud-a23o).
- **Check:** check 3.

**4. [F H] HIGH against the owner's "near-instant" - with kd-88's walk fixed, a grant still costs 4-9 Secret Server reads and a fresh AD session per check.**
- **Why:**
  - **Protected check:** it fetches the directory-read credential at three places (`ProtectedPrincipalService.cs:437`, `:788`, `:1458`). DelineaService caches the token, never the secret. At `:1458` it fetches and builds a runspace before `:1546` returns for a target with no DN.
  - **Self-grant check:** it opens a fresh runspace, imports the AD module and runs Get-ADForest per check (`PermissionValidator.cs:670` `SelfGrantDirectory.Open(...)`; `Services/SelfGrantDirectory.cs:96-97`, `:192`), then fetches the secret again (`PermissionValidator.cs:811`).
  - **On-premises grants:** both checks run again at the write (`Components/Pages/MailboxPermissions.razor:431` and `:715`; `Components/Pages/CalendarPermissions.razor:406` and `:669`).
  - **Bulk files:** they pay the self-grant session per row (`Services/MailboxPermissionService.cs:377`, `Services/CalendarPermissionService.cs:375`).
- **Measured on dev, 10-30 08:23:** 2.66-4.06 s per secret read, plus 1.1 s for the OAuth token.
- **Reviewers' estimates:**
  - a cloud Add takes about 15-17 s with only the walk fixed, or about 8 s with kd-88 done in full;
  - an on-premises Add takes 25-30 s;
  - a 200-row bulk file gains 17-27 minutes.
- **What kd-88 covers:** its approach item 2 covers the protected check's three reads only, not the self-grant run or the at-write re-run.
- **Fix shape:**
  - fetch one credential per operation and pass it from ValidateTargetMailboxAsync into the self-grant run and the at-write re-run;
  - use one directory session;
  - return before the fetch when there is nothing to look up;
  - answer "is the operator in this group" from the operator's own token where the group is a security group (dev's token carries 336 group SIDs).
- **Check:** check 5.

**5. [A G K] HIGH, seen on dev - session revalidation can sign an operator out with no warning line and no audit row.**
- **Evidence:** `D:\inetpub\ExchangeAdminWebDev\logs\app-20261030.log:1886-1897`, at 09:45:36:
  - "[ERR] An error occurred while revalidating authentication state";
  - `System.ObjectDisposedException ... 'Microsoft.Win32.SafeHandles.SafeAccessTokenHandle' ... at System.Security.Principal.WindowsIdentity.get_Name() at ...MembershipRevalidatingAuthenticationStateProvider.Revoke(...)` (log: line 296);
  - then "User Unknown carries 0 group claim(s)", and "User Unknown denied access to ..." for every module.
- **Why:**
  - `Authorization/MembershipRevalidatingAuthenticationStateProvider.cs:296` `var account = user?.Identity?.Name;` runs after `TryRevoke` (`:293`), and its throw skips both the warning (`:300`) and the audit line (`:304`).
  - The framework's loop then signs the circuit out.
  - The check reads the same name at `Authorization/CircuitMembershipRevalidator.cs:296`, inside a try whose catch (`:173` and the bare catch after `:296`) turns any throw into a lookup failure.
  - Assumption: the circuit keeps a WindowsIdentity whose token was disposed with the HTTP request that made it. The log shows `/_blazor` requests about every 90 s, which looks like long polling.
- **Effect:** an authorized operator's page empties mid-work, and a reload fixes it. The defect is in no record, and tests cannot see it because test principals are never disposed.
- **Fix shape:** capture the SID and the name as strings when the circuit starts, and never read the WindowsIdentity again.
- **Until fixed:** set `Authentication:RevalidationEnabled=false`, the designed off-switch, which is audited at startup.
- **Check:** after a working day, `Select-String D:\inetpub\ExchangeAdminWebDev\logs\app-*.log -Pattern 'error occurred while revalidating','User Unknown denied'`.

**6. [A B J] HIGH - a grant to a cloud mailbox is refused when the on-premises object does not carry its mailbox GUID, or lives in another domain.**
- **Why:**
  - aud-a2c accepts the target only when exactly one AD object holds Exchange's ExchangeGuid in msExchMailboxGuid: `ProtectedPrincipalService.cs:674` `return await ProveMailboxBackingAsync(identity, recipient, canonical);`.
  - Otherwise `:736-738` refuses with "no Active Directory object holds this mailbox GUID in msExchMailboxGuid (a remote mailbox whose ExchangeGuid was never stamped on-premises, or an object outside the searched domain)".
  - The search at `:837-841` has no -Server, so only the app's default domain is searched.
  - Assumption (standard hybrid behaviour): a mailbox made in the cloud for a synced user (New-RemoteMailbox, never moved) has no on-premises ExchangeGuid until someone runs Set-RemoteMailbox -ExchangeGuid, and Entra Connect does not write it back.
- **Effect:**
  - The operator sees "Access denied: Protected-principal identity resolution is unavailable. Contact your administrator." (`PermissionValidator.cs:368`), or "Protection check unavailable." in Conference Rooms (`ConferenceRoomProtectionGate.cs:167`). The cause appears only in a WRN line.
  - It hits Mailbox and Calendar Permissions, Out of Office and every Conference Rooms room write. New rooms and recent hires are the likely cases.
  - The one migrated mailbox tried on dev passed this step in under 3 s.
- **Fix shape:** owner decision. Either prove the link with something every synced mailbox carries, or downgrade "no holder" to a warning until the count is known.
- **Commits:** 7f1a0e6e (aud-a2c), 1ad872f3 (aud-a2b).
- **Check:**
  - check 3, for the count;
  - on today's dev build, a grant on a recently created cloud mailbox. This step runs before the kd-88 walk, so it gives a refusal in about 10 s and the WRN text above.

**7. [E F H] HIGH, seen on dev - Exchange Online connections are made one at a time, and a borrower queued for one never looks at the pool again.**
- **Evidence, 10-30:**
  - On `/mailbox-permissions` one operator made four overlapping borrows (searches in the two RecipientAutocomplete boxes, `Components/Shared/RecipientAutocomplete.razor:113`).
  - The first borrow took the pooled connection. The other three connected one after another, 9.8, 16.7 and 25.5 s after asking, although connections came back at 12:21:54Z and 12:22:03Z. Sources: the trace JSONL at 12:21:45-12:22:23Z, and the app log at 08:21:56, 08:22:04 and 08:22:14.
  - On the old build, four parallel connects finished within 1.1 s of each other.
- **Why:**
  - `Services/ExoConnectionPool.cs:361` takes from the pool before the gate.
  - `:909` `if (!_connectGate.Wait(ConnectGateTimeout, cancellationToken))` serialises every Connect-ExchangeOnline, so that the new connection's id can be told apart (aud-a6b).
  - After the gate, `:397` connects without looking at the pool again.
  - The comment at `:790-791` assumes "the pool reuses connections so this is not the common path". In fact the pool is empty after every restart and every idle sweep; on 10-30 all four connections were retired at 08:45.
- **Cost:**
  - The k-th concurrent cold borrower waits about k x 9 s.
  - Slower connects give "Exchange service is busy establishing connections. Please try again shortly." (`:910-911`).
  - Older keystroke results can land last.
- **Fix shape:** call TryTake again once the gate is held, and cancel superseded autocomplete searches.
- **Commits:** 154ed2fc, f9a78594 (aud-a6b).

**8. [G J] HIGH - module-admin grants stored as names stopped working on dev and will on prod, and the list the owner asked to see first was never produced.**
- **Why:**
  - The rule: `Services/ModuleAdminService.cs:54` `var usableGrants = GroupMembershipChecker.UsableSidsOnly(GetModuleAdmins(moduleId));`. The remark at `:43` reads "A legacy name-valued grant therefore stops authorizing the moment this ships".
  - The old picker stored a SamAccountName or display name. Nothing migrates those, and `GetUnusableGrants()` (`:70`) has no production caller.
  - `.agents/review/findings/aud-a1.md:130-131`: "The non-resolving-grant list has NOT been produced, and the owner ruled it must reach him before this ships." It is not on the state checklist.
  - Dev and prod share the database, so dev enforces this today. Prod's old build still honours the names.
- **Effect:** a delegated module admin who is not a global admin loses the nav link and the Module Configuration page for the module, with no message.
- **Fix:** re-pick each group in Module Configuration, which stores its SID.
- **Commits:** 5059c6fa, c5ca5bfa (aud-a1).
- **Check:** check 4.

**9. [A G M] HIGH - Group Management's new manager notice almost certainly never sends, and files a Failed audit row on every change.**
- **Why:**
  - `Services/GroupManagementService.cs:324-326` reads objectClass `as System.Collections.IEnumerable` and needs an element equal to "user".
  - The AD module returns ObjectClass as a string; this same file reads it that way at `:1211`. A string enumerates as characters, so "u", "s", "e", "r" never match.
  - The tests only scan source text (`GroupManagerNotificationTests.cs:126-160`).
- **Effect on every successful add, remove or batch:**
  - a WRN "managedBy ... is not a user (objectClass: u,s,e,r); refusing to notify it.";
  - a Failed `<action>_ManagerNotNotified` row (`Components/Pages/GroupManagement.razor:870-872`);
  - no mail;
  - a secret read and a fresh AD session (about 4-8 s) anyway.
- **Also:**
  - A group with no manager files that Failed row on every change.
  - Once this is fixed, managers will get the administrator template "[Admin] <action> - SUCCESS - Ticket #..." with the operator's IP address (`Services/EmailService.cs:184`, `:213`). It is not gated by NotifyUsersOnPermissionGrant.
- **Fix shape:**
  - read the value with `?.ToString()` as `:1211` does, or use SelfGrantDirectory's `ObjectClassOf`;
  - file no Failed row for a group with no manager.
- **Commit:** 541edc4d (A25).
- **Check:**
  - read-only probe: `(Get-ADObject -Identity (Get-ADUser $env:USERNAME).DistinguishedName -Properties objectClass).objectClass.GetType().FullName`. If it prints System.String, the defect is confirmed.
  - or check 5's Group Management add.

**10. [F G H] HIGH where used at scale - Defender's 4-minute enrichment deadline also cancels the Site column.**
- **Why:**
  - `Services/DefenderEndpointDeviceService.cs:100` sets `DefaultEnrichmentDeadline = TimeSpan.FromMinutes(4)`, and `:554` builds one budget.
  - The hunting enrichment (`:557`) and `:560` `ApplySitesAsync(devices, enrichmentBudget.Token)` share that budget. Once the hunting batches spend it, the site read (`Services/AdSiteDirectory.cs:57`) is cancelled and every Site reads "(unavailable)".
  - The Site filter keeps "(unavailable)" rows (`Components/Pages/DefenderEndpointDevices.razor:694-705`), so a site search returns the whole inventory.
  - Site is the module's primary output (decisions 2026-10-14).
  - This applies when IncludeDiscoverySources is on. For 27k-40k devices that is 54-82 hunting calls in 240 s, which needs 4.4 s each or less (unmeasured).
- **Fix shape:** read and match sites outside the budget (one directory read plus in-memory matching).
- **Commits:** 421588eb, a4c01925 (cdr-d7).
- **Check:**
  - With discovery sources on, Search with no filters, and look for "Site details could not be completed for every device."
  - Per-call time is in the INF "Received HTTP response headers after NNNNms" lines for runHuntingQuery.

**11. [D] HIGH when run - the installer casts the pool's identityType to an integer, which is the exact failure of the 2026-09-08 cutover.**
- **Why:**
  - `tools/Install-ExchangeAdminWeb.ps1:920-921` does `$existingIdentityType = [int]$existingProcessModel.identityType`.
  - The cutover failed on this cast with "Cannot convert value SpecificUser to type System.Int32" (commit 7ba6b255 message), while deploy.ps1 matches the value as text (`deploy.ps1:502-504`).
  - It is not on the pipeline's path. Any elevated plan or apply over an existing pool throws before changing anything.
  - No test covers it.
- **Fix:** match the text as deploy.ps1 does, then map it to 3 or 4.
- **Commit:** b071fd62 (aud-a34).
- **Check (elevated powershell.exe):** `Import-Module WebAdministration; $m = Get-ItemProperty 'IIS:\AppPools\ExchangeAdminWebDev' -Name processModel; '{0} [{1}]' -f $m.identityType, $m.identityType.GetType().FullName`. "SpecificUser [System.String]" confirms it.

**12. [D H] HIGH impact, a security exposure rather than a failure - elevated scripts now run the app DLL from folders any authenticated user can modify.**
- **Why:**
  - The in-app backup replaced sqlite3.exe on a stated premise (`tools/SqliteConfigBackup.psm1:21-22`): "the assembly is the build the caller is about to deploy or already runs, so no new principal can put code in this path".
  - On this host, `D:\inetpub\ExchangeAdminWebDev\ExchangeAdminWeb.dll` grants `NT AUTHORITY\Authenticated Users` Modify, inherited from `D:\` (Get-Acl, 2026-10-30).
  - The promotion runs `dotnet.exe <dev folder>\ExchangeAdminWeb.dll db ...` elevated (`tools/promote-dev-to-prod.ps1:410`, `:253`, `:279`), as do the cutover, the installer and deploy.ps1.
  - So the aud-sqlite3-path hole (a user-writable binary run as administrator) has moved rather than closed. The same grant already lets any authenticated user replace the binaries both pools run, and edit the shared authorization database.
  - The safety argument rests on this host's ACLs (Invariant 7).
- **Fix:** owner decision. Either break the inherited grant under `D:\inetpub`, `D:\backups` and `E:\WWWOutput`, or refuse to run the tool from a folder non-admins can write.
- **Commit:** b1577f7e (S2).
- **Check:** `(Get-Acl D:\inetpub\ExchangeAdminWebDev\ExchangeAdminWeb.dll).Access`.

### MEDIUM

**13. [B] MEDIUM, every write depends on it - every Exchange write and pre-write read now names the mailbox by its ExchangeGuid.**
- **Where it is used:** `Services/ValidatedMailbox.cs:51` `public string Identity => ExchangeGuid.ToString("D");` (labelled an assumption at `:45-49`) flows into:
  - Get-Mailbox read-back (`ExchangeServiceBase.cs:564-566`);
  - Add/Remove-MailboxPermission and -RecipientPermission (`MailboxPermissionService.cs:93/107/156/169`; on-prem `:534/:543/:603/:616`);
  - `"<guid>:\Calendar"` (`CalendarPermissionService.cs:68`);
  - the auto-reply cmdlets (`OutOfOfficeService.cs:57/106`);
  - Set-Place and `Add-DistributionGroupMember -Member` (`ConferenceRoomService.cs:669-670`, `:870-872`);
  - Get-Place under SilentlyContinue (`:106-108`), where a miss blanks the audit's old values.
- **Confidence:**
  - The RecipientIdParameter cmdlets are the least certain; aud-a2d calls them "lower confidence".
  - Send As was in 34 of prod's 42 grants of 10-26..29.
  - Only the test harness has accepted the form (`ExchangeWriteHarness.cs:508`, `:804`).
- **Effect if wrong:** every change is refused with "couldn't be found", naming a GUID the operator never typed.
- **Commits:** fb449b5e, 6241bb20, 73787287, 404c856a (aud-a2d).
- **Check:** check 2.

**14. [A J] MEDIUM - Migration's Excluded AD Groups check now refuses unless the typed address is the user's UPN and each configured group resolves in exactly one domain.**
- **Lines:**
  - `MigrationService.cs:321-322` `Get-ADUser -Filter "UserPrincipalName -eq '<typed address>'" -Server <gc>`;
  - `:328` "no directory user in the forest has the user principal name ...";
  - `:401-404` `Get-ADGroup -Identity <configured> -Server <each domain>`, with refusals at `:417-418` and `:427`;
  - a failure is cached for the whole run (`:350-351`).
- **When it runs:**
  - On every ToCloud check while the setting is non-empty (`:122-125`). It is set here, to one group name.
  - The old build matched by DN substring.
  - Shared and resource mailboxes, the recent prod migrations, are the likeliest to have a UPN that differs from their mail (assumption).
- **Cost:** each check also opens a fresh runspace with the AD import (`:308-316`).
- **Commits:** dd74db81 (aud-a23r), 5cf71c1e (aud-a23j).
- **Check:**
  - check 1 covers one mailbox;
  - `(Get-ADForest).Domains | % { try { Get-ADGroup -Identity '<configured group>' -Server $_ | select DistinguishedName } catch {} }` must return exactly one row.

**15. [A G J] MEDIUM - revalidation fails closed on one failed lookup, and a hung directory call ends every new tab.**
- **How it works:**
  - Each open tab is re-checked at connect and every 5 min, and each page needs an answer under 5 min old.
  - Lookups time out at 60 s (`Authorization/CircuitRevalidationSettings.cs:54`).
  - One LookupFailed revokes with no retry (`MembershipRevalidatingAuthenticationStateProvider.cs:160`).
  - At most 4 lookups run at once (`Services/OperatorMembershipDirectory.cs:57`), each in a fresh runspace with the AD import (`Services/PowerShellDirectoryCommands.cs:167-177`).
- **Effect:**
  - ADWS held one call for 2:00 on 10-30. Four such calls end every new tab with "Your access could not be re-checked against Active Directory just now, so this session was ended as a precaution." (`Authorization/CircuitRevocation.cs:101-102`).
  - Pages get a standalone 403 (`Middleware/PageRequestRevalidationMiddleware.cs:193`).
- **Fix shape:** when a fresh read fails, accept an answer recorded within the interval, as page requests already do.
- **Commits:** cb3be915, 59e973ec, 2678735e (aud-a29b, aud-a29g).

**16. [N O] MEDIUM, judgement - session revalidation (aud-a29b option 1) was adopted on the general go, although its record says the choice is not an agent's.**
- **The record:**
  - `.agents/review/findings/aud-a29b.md:47-48` says "It should not be chosen by an agent."
  - `.agents/decisions.md` (2026-10-21) says "the go selects option 1 ...; the owner may veto before it lands".
  - No explicit owner yes is recorded.
- **What it brought:**
  - It is the largest production commit in the range (cb3be915, about 1,100 lines), plus five follow-ups.
  - It produced kd-87 and item 5.
  - Its off-switch and interval ceiling go beyond the record's approach (`aud-a29b.md:375`, `:395`).
- **Fix shape:** ask the option-1 question directly; meanwhile, see closing item 1.

**17. [A J] MEDIUM - one stale or ambiguous Protected Group Targets entry now refuses every protected check in every module.**
- **Lines:**
  - `Services/ProtectedGroupMembership.cs:372` requires a DN to resolve, `:386` covers a bare name, and `:413` sets "no such group" / "{n} groups match".
  - All of these end in `ProtectedPrincipalService.cs:1482` "Group membership check was incomplete due to expansion errors. Cannot confirm target is not protected."
- **What changed:** before, a DN passed unread, and a bare name was looked up in the host domain only.
- **Triggers:** a renamed, moved or deleted group's DN; a name two domains share; a foreign-forest SID.
- **What the operator sees:** the banner never names the entry; only the WRN at `:267` does.
- **Commit:** ef0664e9 (aud-a3b).
- **Check:** check 4.

**18. [G J] MEDIUM - the self-grant refusal fires for any group the operator is in, worded as if he had named himself.**
- **Lines:** `PermissionValidator.cs:896-899` refuses on any match, and `:617` says "Access denied: You cannot grant permissions to yourself."
- **Scope:** direct, nested and primary-group membership all count. Granting a mailbox to your own team's group is routine (assumption).
- **Fix shape:** name the group in the refusal, and decide whether membership should refuse at all.
- **Commits:** 4cc5756b (aud-a43), f7921ce8 (aud-a52).

**19. [A] MEDIUM likelihood, destructive if wrong - Comms-10k clears the broadcast list, then adds members as `<GUID=...>` values.**
- **Lines:**
  - `Services/Comms10kService.cs:598` `var values = batch.Select(guid => $"<GUID={guid:D}>").ToArray();`;
  - `:602` `Set-ADGroup -Add @{member=values}`;
  - ordered by `Services/Comms10kReplaceWriter.cs:238-240` (`clear();` first).
- **Confidence:**
  - MS-ADTS permits the form. Whether the AD module and ADWS pass it through has not been observed (`cdr-c4.md:157-159`).
  - The live 7.9 s measurement used DN values, and the stand-in accepts any string.
- **Effect if refused:** the first live replace leaves the list empty, and every re-run repeats it.
- **Fix shape:** add and verify one GUID before the clear, or fill first and remove after.
- **Commit:** eb6c285b.
- **Check, before the first live replace:**
  - download the membership CSV;
  - on a scratch group run `Set-ADGroup <scratch> -Add @{member="<GUID=$((Get-ADUser $env:USERNAME).ObjectGUID)>"}`, then `Get-ADGroup <scratch> -Properties member`.

**20. [J I] MEDIUM - Comms-10k now replaces only when every address resolves, and an authorization re-check between the clear and each add can leave the list partial.**
- **Full resolution required:** `Services/Jobs/Comms10kReplaceProcessor.cs:125`, with the page message "The file must resolve completely with no rejected rows before Replace. Fix the file and re-upload." (`Components/Pages/Comms10k.razor:804`). This was owner-approved (C3), and matching is exact UPN or mail only.
- **Re-check after the clear:** `Comms10kService.cs:496-499` `RequireAuthorization()` runs as `beforeDispatch` (`:512-513`) after the clear.
- **Fix shape:** check once before the clear, then run to completion.
- **Commits:** f0d79b03, b79914cf.

**21. [M F] MEDIUM - bulk permission runs now mail the trustee and the owner for every applied row, from inside the loop.**
- **Lines:** `MailboxPermissionService.cs:420-421` `PermissionChangeNotifier.NotifyAffectedAsync(...)`, and the same at `CalendarPermissionService.cs:427`.
- **Volume:**
  - Email:NotifyUsersOnPermissionGrant is true on both instances.
  - A 200-row file can send 400 mails, one after another (+0.3-1.5 s each, estimate).
  - Exchange's warning-only no-ops count as applied (`MailboxPermissionService.cs:198-199`; assumption about Exchange), so people are mailed "granted" for nothing.
  - Before this change there was one administrator summary.
- **Fix shape:** one digest per recipient after the loop, or go back to the summary.
- **Commit:** 904e5023 (A24).

**22. [I] MEDIUM - a late member load can switch M365 Groups back to the previous group, and the next member operation then acts on it.**
- **Lines:**
  - `Components/Pages/M365GroupManagement.razor:445-449` (`selectedGroup = details;` after an await, with no token);
  - `:710` `var group = selectedGroup;` in RunMemberOpAsync;
  - the same publish-after-await shape in `Components/Pages/GroupManagement.razor:516`, `:593-594`, `:717-718`, `:997-999`, `:1146-1148`.
- **Background:** kd-19's monotonic token fixed this shape in Self-Service Groups only.
- **Fix shape:** compare a token after the await, and refuse Select while a load runs.

**23. [B I M] MEDIUM, pre-existing shape - on-premises calendar grants send a try/catch script that the Exchange endpoint refuses, and are now filed "could not be confirmed" and mailed to administrators.**
- **The script:** `CalendarPermissionService.cs:523-529` sends `try { Set-MailboxFolderPermission ... } catch { Add-MailboxFolderPermission ... }` through `Invoke-Command -Session` to the Microsoft.Exchange endpoint. That endpoint is in no-language mode and accepts a single pipeline (assumption; `MailboxPermissionService.cs:486`'s comment relies on the same restriction).
- **New since the dev build:** the refusal's text contains "runspace", so `ExoConnectionPool.cs:543` classes it as a dead session and the outcome is filed unknown, with an `_UNCONFIRMED` admin mail (7576cea7).
- **Usage:** no on-prem calendar operation appears in the retained prod logs.
- **Fix shape:** two single-command invocations, with the folder name taken from on-prem Get-MailboxFolderStatistics.

**24. [B J] MEDIUM - approving a mailbox that has a batch of its own needs that batch to report exactly "Synced".**
- **Lines:** `MigrationService.cs:1386`, `:1496`; `MigrationBatchActionPlanner.cs:146-147`.
- **Effect:** single-user batches are the common case (`Migration.razor:2068`). A status such as SyncedWithErrors refuses with "...Nothing was changed."
- **Commits:** dd74db81, 2249d828.
- **Check (EXO, read-only):** `Get-MigrationUser -ResultSize Unlimited | ? Status -eq NeedsApproval | % { Get-MigrationBatch -Identity $_.BatchId } | select Identity,Status`.

**25. [B] MEDIUM - cloud calendar grants now fall back from Set to Add only on one wording.**
- **Lines:** `CalendarPermissionService.cs:94` (`NamesNoPermissionEntry`) and `:245` "no existing permission entry".
- **What changed:** the old build fell back on any failure. Every first-time delegate now depends on Exchange's wording.
- **Commit:** 0e2c6344 (kd-43).
- **Check:** grant Reviewer on a test cloud mailbox to a user who has no entry.

**26. [B G] MEDIUM - times may all read "(time zone not reported)", and the Out of Office window is now written as a UTC instant.**
- **Display:** `Models/LookupModels.cs:374-375` labels any time that is not UTC. The module deserializes results from CliXml, which likely yields Kind Local (assumption). Migration, Recipient Lookup and Check Status would then show the server clock with that label.
- **Out of Office write:** `OutOfOfficeService.cs:92` converts the typed time with the browser's zone, and `:121` passes a Kind-Utc value. If V3 does not carry that value as the same instant, the window shifts by the operator's offset.
- **Commits:** 539f2ccf, 022014aa, 58ccceac, 9b3080ee.
- **Check:**
  - check 2's Kind line;
  - schedule one test mailbox and compare OWA with `Get-MailboxAutoReplyConfiguration <x> | Select StartTime,EndTime`.

**27. [A G] MEDIUM - an attribute-editor save now counts as done only if its read-back matches, judged by unprobed type assumptions.**
- **Lines:** `ADAttributeEditorService.cs:1382` "*Assumption, not probed (DC affinity)*", `:1698` `if (read is bool)`, `:1593` "...was not confirmed: read back by objectGUID ...".
- **What it assumes:**
  - Boolean values come back as System.Boolean, Integer as Int32/Int64;
  - DN syntax is used only by manager, assistant, secretary and seeAlso;
  - the re-read, the write and the read-back all reach one DC.
- **Effect if wrong:** a mismatch files a landed save as failed, with no Undo and a failure mail. The allowlist has three Integer rows, so the integer path runs from day one.
- **Commits:** 73eeb7da, 7b7acc41 (aud-a26j/l).
- **Check:** on a test account, save one extensionAttribute and one msExchRecipientDisplayType value. Expect an Undo button and no "was not confirmed".

**28. [F H] MEDIUM - a Room Finder row now fetches the directory credential up to four times, each time into a fresh AD session.**
- **Lines:** `ConferenceRoomService.cs:1787`, `:1895`, `:2095`, `:2212` `LookupDirectoryCredentialAsync(...)`.
- **Cost:**
  - About 17 AD calls and two Exchange membership reads per row, plus the protected check's reads.
  - Estimate: +7-10 s per synced-list row over the old build, about 25-30 s per row, about 45 min per 100 rows.
- **Fix shape:** one credential and one runspace per row or per job.
- **Commits:** 587d7d5b, 7d863547, df084c68, a38df458 (aud-a2f/j/q/l).

**29. [C F H] MEDIUM, off by default - Cloud Password Reset's employee-ID fill-in will be refused by Graph, and each reset makes four vault reads.**
- **The save:**
  - `CloudPasswordResetService.cs:1041` / `:1049` PATCHes employeeId. The documented grants include no user write (plan assumption A1).
  - On refusal the reset still goes out, and "[Admin] ... employee ID - FAILED" is mailed even when the reset succeeded (`CloudPasswordReset.razor:542-560`).
- **Cost per reset:**
  - The Graph secret and a token are fetched on every call (`:282`, `:300`; called at `:315`, `:897`, `:1011`), about 11-16 s.
  - The owner and number searches run twice (`:580`, `:591`). `ADEmployeeIdLookup.cs:110` `(employeeID=...)` is a domain-wide search (unindexed is an assumption) under the 30 s lock the pickers share (`:139`).
  - The admin alert is sent between the save and the password write (`CloudPasswordReset.razor:551` before `:560`).
- **Commits:** 527d1ed1, 714c4128, a8f70274.
- **Check before switching it on:**
  - the app's granted Graph permissions and directory roles;
  - `Get-ADObject -SearchBase (Get-ADRootDSE).schemaNamingContext -LDAPFilter '(lDAPDisplayName=employeeID)' -Properties searchFlags` (bit 1 set means indexed).

**30. [C] MEDIUM - M365 Groups: adding a member by name now needs Graph User.Read.All, and a guest or alias address reads "not found".**
- **Lines:** `M365GroupManagementService.cs:303` `GET /users/{x}?$select=id` and `:311` "Not permitted to look up ..."; `Modules/ModuleCatalog.cs:818`.
- **Not a regression:** adds by UPN failed before (aud-a10).
- **Commits:** 679b9bdc, 30e893bd.
- **Check:** in Entra, App registrations > the module's app > API permissions: confirm User.Read.All (Application).

**31. [C] MEDIUM - Defender: a 429 does not stop the hunting loop.**
- **Lines:** `DefenderEndpointDeviceService.cs:1006-1007` and `:1056-1057`. Only 401 and 403 stop the loop.
- **Effect:** each later failure greys 1,000 devices.
- **Fix:** stop on 429.
- **Check:** runHuntingQuery responses ending "- 429" in the app log.

**32. [G M] MEDIUM - Emergency Disable's notice now goes only to the module's NotifySecurityTeam setting.**
- **Lines:** `EmergencyDisableService.cs:1155`, `:1181-1182`.
- **Effect:**
  - Administrators no longer receive the notice.
  - Addresses split on commas only (`EmailService.cs:176`), so a semicolon list fails with "SECURITY TEAM NOT NOTIFIED".
  - A blank setting shows "NO security-team recipient is configured ..." (`EmergencyDisable.razor:33`).
- **Commit:** acad0c2e (A27).
- **Check:** Module Configuration > Emergency Disable > NotifySecurityTeam.

**33. [G N] MEDIUM, owner decision - Self-Service Groups now lets rights held through a group grant access.**
- **Lines:** `Services/SelfServiceGroups/GroupMembershipAce.cs:141` `granting.Add(groupSid);`. The search by name has no ownership clause (`AdOwnershipFilter.cs:75`).
- **Effect:**
  - Members of a delegated group that can write `member` (helpdesk, Account Operators, Domain Admins) can now open and change any such group.
  - They do so with the module's credential, no ticket, and no Protected Group Targets check (the module's ruled exemption).
  - This is the faithful evaluation aud-a4 asked for, but its reach was never put to the owner.
- **Commit:** a5b62160.

**34. [D H J] MEDIUM, ran on dev - the serving-pool check reads every site, application and virtual directory on the server, and refuses on any oddity anywhere.**
- **Lines:**
  - `tools/AppPoolControl.psm1:469` `foreach ($site in @(Get-Website))`, and `:479` `Get-WebConfiguration ... -ErrorAction Stop`;
  - `:481` "IIS listed no root virtual directory for ...";
  - `:388` "...holds both quote characters, so no XPath filter can name it."
- **Effect:** another team's application on this server with a missing root virtual directory, an odd name or a read error refuses every deploy, promotion and install stop. This class of refusal has already blocked one deploy (cdt-61).
- **Fix shape:** read only the nodes that could reach this instance's folder, and warn on the rest.
- **Commits:** c3c4b685, f6b86a46, 40e578f0 (aud-a38f/i/j), bc549d68.

**35. [G] MEDIUM, certain and harmless - `-Prod` and `-Prod -PlanOnly` refuse today with "Dev is stale".**
- **Why:** `tools/deploy-pipeline.ps1:175` compares HEAD with the dev manifest, and `:182` throws. The manifest says `8557a15a`; HEAD `280ff007` touches only `.agents/`.
- **Effect:** any docs commit forces a full dev redeploy (a pool stop) before a plan can print.
- **What to do now:** redeploy dev at the final head right before `-Prod`.
- **Fix shape:** compare build inputs only, or let `-PlanOnly` continue with a warning.

**36. [D I] MEDIUM - plan mode means something different in each script, so the owed AC7 plan run cannot catch a cdt-61-class failure on prod.**
- **Lines:**
  - `tools/promote-dev-to-prod.ps1:266`: "a plan line only; the dry run reads nothing from IIS".
  - The cutover's plan runs the IIS checks, but exits at `tools/Move-ConfigDbToShared.ps1:725` on this host after a live `db check`.
  - deploy.ps1 has no plan mode.
- **Check for prod (elevated, read-only, from the repo):** `Import-Module WebAdministration; Import-Module .\tools\AppPoolControl.psm1 -Force; Assert-AppPoolServesPath -Name 'ExchangeAdminWeb' -PhysicalPath 'D:\inetpub\ExchangeAdminWeb' -NestedNodes Refuse; Get-AppPoolOtherApplication -Name 'ExchangeAdminWeb' -PhysicalPath 'D:\inetpub\ExchangeAdminWeb'`. Expect one node, then nothing.

**37. [D I] MEDIUM - functions in the script modules do not inherit the caller's "Stop", so an unreadable appsettings.json reads as "no key".**
- **Lines:**
  - `tools/SqliteConfigBackup.psm1:179-180`: when appsettings cannot be read, the backup targets the stale per-instance `config\exchangeadmin.db` instead of the shared one. That is fail-open.
  - `:392`: a failed `Move-Item -Force` still returns success.
  - `tools/AppPoolControl.psm1:59`: a failed `Get-WebAppPoolState` gives $null, and the run ends 60 s later.
  - `:472`, `:635` call `Get-WebApplication` without `-ErrorAction Stop`.
- **Fix:** add `-ErrorAction Stop` to each read, or set a module-scope "Stop".

**38. [D F] MEDIUM, not run on prod - the stop waits 60 s while IIS allows 90 s, and two scripts treat a failed copy differently.**
- **Stop timing:**
  - `tools/AppPoolControl.psm1:51` waits 60 s, and `:94` says "Refusing to deploy over a running application".
  - The promotion stops prod outside its try (`promote-dev-to-prod.ps1:417-421`).
  - A stop of 60-90 s therefore ends the run with prod still stopping and not restarted.
- **Failed copy:** robocopy >= 8 leaves the pool stopped in deploy.ps1 (`:853`), but rolls back and restarts in the promotion (`:462-469`).
- **Commits:** dd8ee1a7, 25e71878.

**39. [D N] MEDIUM if relied on - `-NarrowPublishAcl` cannot narrow anything on this host.**
- **Why:** `tools/Install-ExchangeAdminWeb.ps1:979` / `:271` use `icacls /grant` without `:r`, which adds to existing rights. The pool identity keeps Modify by inheritance (item 12).
- **Consequence:** the open owner decision on making it the default would pass whatever the modules do.
- **History:** it shipped inside a "ci:" commit (c1ada75f) and has never run against IIS.

**40. [J L I] MEDIUM - a ticket containing an invisible character is refused as if it were missing, while the button stays enabled.**
- **Lines:** `Services/TicketPresence.cs:59` (Control or Format category), with the message "Ticket number is required" (`:41`).
- **Trigger:** a pasted zero-width space, BOM, soft hyphen or bidi mark.
- **Why the button stays enabled:** submit buttons still test `!string.IsNullOrWhiteSpace(ticketNumber)` (e.g. `MailboxPermissions.razor:258`).
- **Scope creep:** kd-48 was about bidi-only tickets, but the fix refuses a format character anywhere.
- **Fix shape:** strip invisible characters, or say that the ticket contains one.
- **Commits:** 2554e7b6, c5500452.

**41. [I] MEDIUM - after the kd-7/18/24/45/48 sweeps, several ticket gates are still markup-only or still IsNullOrWhiteSpace.**
- **No server-side check:**
  - `ADAttributeEditor.razor:216`; the service has no ticket step.
  - `BlockedSenders.razor:123`.
  - `LicensingUpdates.razor:205`, with `LicensingUpdatesService.cs:136`.
  - `OutOfOffice.razor:220`, with `:377`.
- **Still IsNullOrWhiteSpace:**
  - `AdminEventLog.razor:1146`: the undo's only gate, with the refusal unrecorded.
  - `Migration.razor:3333` and `:3312`: the only gate for every batch and mailbox action.
- **ServiceNow:** none of these is validated against ServiceNow when it is enabled.
- **Fix shape:** `TicketPresence.Refusal(t) ?? await ServiceNow.ValidateTicketAsync(t)` at the server, with a refusal row.

**42. [I] MEDIUM - write handlers that are their own click target still have no entry busy guard; kd-72 and kd-74 fixed five pages only.**
- **Handlers without the guard:**
  - Mailbox Permissions: `MailboxPermissions.razor:350` and `:556`. A bulk CSV, with its per-row mails, runs again.
  - Calendar Permissions: `CalendarPermissions.razor:346`, `:533`.
  - Conference Rooms: `ConferenceRooms.razor:1297`, `:1643` queue a second job.
  - M365 Groups: `M365GroupManagement.razor:503`, where a create is sent twice.
  - Named Locations: `NamedLocations.razor:342`, which can create two Conditional Access named locations.
  - Also `DhcpAuthorization.razor:285`, `:372`; `GroupManagement.razor:546`, `:677`; `MfaReset.razor:289`; `AdminBulkJobs.razor:323`, `:377`.
- **Intune and Risky Users** (`IntuneDevices.razor:942`, `RiskyUsers.razor:826`): the stale second Confirm throws and ends the circuit.
- **Trigger:** a second click within one round trip. The shape is pre-existing.
- **Fix:** make `if (<flag>) return;` the first statement.

**43. [I] MEDIUM - two pages still parse a config Boolean with bool.Parse during render; aud-a58 fixed Home only.**
- **Lines:** `MailboxPermissions.razor:22` and `CalendarPermissions.razor:22`.
- **Effect:** a value such as "yes" breaks both pages for every user. Both instances hold `true` today.
- **Fix:** `ConfigBoolean.Read(...)` as at `Home.razor:42`.

**44. [G M L] MEDIUM - the audit `result` field gained two values, and one refusal is now filed six different ways.**
- **New values:**
  - "Cancelled" (`Services/AuditService.cs:26`/`:32`) and "Undetermined" (`:171`). At the dev build the values were Success, Failed and Ineligible.
  - decisions.md treats audit fields as a Splunk interface with a closed list, and its proposed shape "awaits a yes".
  - A Splunk search on result="Failed" now misses writes that may have landed, and misses cancels.
- **Refusal conventions:**
  - `*_Refused` with the refusal fields;
  - the same action, filed Failed with the fields;
  - `*_Denied` with no fields (`BlockedSenders.razor:388`, `MailboxPermissions.razor:418`, `CalendarPermissions.razor:392`, `OutOfOffice.razor:462`);
  - `_Denied` with mutationAttempted only (`MigrationService.cs:998`);
  - Failed with error text only (DHCP, Named Locations, M365, Group Management, Self-Service, MFA Reset, Intune, Risky Users);
  - the outcome in the action name (`MailboxPermissions.razor:740`).
- **Fix:** publish the closed list, tell the Splunk owner, and adopt one refusal convention.

**45. [O] MEDIUM - the records disagree with the code and with each other.**
- **state.md:**
  - `.agents/state.md:11` "Master is deployable" and `:42` "Open: nothing marked [ ]" predate kd-87 and kd-88, and the index lists kd-88 twice.
  - `state.md:198-200` says the credential is needed for grants "to a directory-held group". Since aud-a70 (6), every Mailbox and Calendar grant needs it (`PermissionValidator.cs:811-822`, `:1091`).
  - The "Labelled live assumptions" list omits aud-a2ac/a2ad and Recipient Lookup's A4, which rest on item 2's recogniser.
- **Constitution and rulings:**
  - `docs/ProjectConstitution.md:80` says every access change must notify the affected user, but kd-44's reversal sends the unconfirmed notice to administrators only. The carve-out exists only in decisions.md.
  - aud-a2e's repo-wide conflict (Constitution:95 against `ProtectedPrincipalEntryValidator.cs:85-91`: cloud-only principals cannot be protected) was closed under a Conference-Rooms-only ruling.
- **Finding statuses:** the aud-a2ab/a2z/a2aa/a2ac/kd-66 status lines read "In progress" against an index that says Closed/Reversed.

**46. [G] LOW-MEDIUM - the leave prompt promises "A bulk job is not affected" (`Components/Shared/InFlightWorkGuard.razor:107`), but neither pool overrides IIS's 20-minute idleTimeout.**
- **Effect:** once the last tab closes, a long job can be stopped and marked Interrupted at the next start. The behaviour is pre-existing; the new line restates the promise.
- **Check:** `& "$env:windir\system32\inetsrv\appcmd.exe" list apppool "ExchangeAdminWeb" /text:processModel.idleTimeout`.

### LOW

- **47. [A]** Revalidation compares the token with tokenGroups read in the account's own domain. That attribute lacks another domain's domain-local groups, so an operator from another domain granted through one is signed out at every check (`CircuitMembershipRevalidator.cs:271`). Every operator in the prod log signs in from one domain.
- **48. [D J]** The app now refuses to start in three cases (`Program.cs:150-163`):
  - a built-in or well-known group in Security:AllowedGroups or Security:AdminGroups;
  - RevalidationInterval outside 00:00:30-01:00:00;
  - RevalidationEnabled set to anything other than true or false.

  Dev and prod hold identical group lists, dev started, and neither file has an Authentication section.
- **49. [D]** The shared log root while prod runs the old build:
  - prod writes without the lock file (`107cfe4f:Services/JsonlLogService.cs:77`), so a row can still split, which is no worse than before;
  - a prod Admin Settings save can overwrite a dev save, because prod lacks the aud-a28k fix.

  Until both run 2.36.0, change settings on one instance only and deploy prod soon after dev.
- **50. [D]** Cleared: dev's SQLite 3.53.3 and prod's 3.49.1 share one file. The schema is v6 at both builds and at HEAD, there are no new tables or columns, and a rollback is safe.
- **51. [F]** The SQLite busy wait drops from about 35 s to 5 s. Under contention a save says "Not saved: ... Try again in a moment; your changes are still on this page." (`SettingsSaveAudit.cs:80`). The ConfigStore:Path rule is stricter (`ConfigStorePath.cs:50`), and both instances comply.
- **52. [F]** The extended log now appends through the lock (about 11 ms each) and drops the oldest entry beyond 1,024 queued. Its dispose may wait 25.6 s, longer than ANCM's shutdown default (assumption) (`ExtendedLogService.cs:13`, `:67`, `:323`).
- **53. [A]** Every global-catalog read uses the first GC in the forest's list, with no site awareness or failover (`PowerShellProtectedGroupDirectory.cs:184`, `SelfGrantDirectory.cs:192`). If that GC is down, every self-grant and group check refuses. Probe: `(Get-ADForest).GlobalCatalogs | Select-Object -First 3`.
- **54. [F I]** Each DOMAIN\Name protected entry costs an uncached Get-ADRootDSE plus a Partitions search per check (`PowerShellProtectedGroupDirectory.cs:62`). The self-grant credential fetch has no 60 s back-off (`PermissionValidator.cs:1125`). The protection checks take no cancellation token (`ProtectedPrincipalService.cs:259`, `:611`).
- **55. [B]** Recipient Lookup's on-prem Get-Recipient needs the module account's RBAC (plan A3, `RecipientLookupService.cs:440`). If it is refused, every on-prem mailbox reads "Could not be determined". An on-prem lookup still opens two sessions and fetches twice (`:310`, `:262`); that is not a regression.
- **56. [G]** Recipient Lookup display oddities:
  - quotas hidden as "The database default (not read)" when UseDatabaseQuotaDefaults is true (`RecipientLookup.razor:329`);
  - ForwardingSmtpAddress shown first, although Exchange delivers to ForwardingAddress;
  - GUID-like Send On Behalf names;
  - errors naming `<guid>:\Calendar`.
- **57. [F]** A cloud-only group as grantee reads its whole membership, up to 250 nested groups (`ExchangeGroupMembers.cs:35`, `:127-129`).
- **58. [B]** Exchange Online connection housekeeping:
  - the targeted disconnect succeeded 4 of 4 on dev, but that it leaves sibling connections alive has not been observed (aud-a6b);
  - a connection whose id was unread is disposed with no disconnect (`ExoConnectionPool.cs:985-998`);
  - Get-ConnectionInformation runs twice per connect inside the gate (`:847`).
- **59. [J]** The location read now fails closed on any Exchange Online error (`ExchangeServiceBase.cs:843`). The pool's settings read fails closed per borrow with an ERR (`ExoConnectionPool.cs:276`, `:303`). Both are proportionate.
- **60. [G]** Bulk jobs:
  - a cancel of a job that is no longer running returns false (`BulkJobService.cs:240`);
  - a Room Finder row stopped before Set-Place is audited but not counted (`:672`);
  - a processor that cannot be built marks the job Interrupted with a DI message (`:590`);
  - a shutdown during a retry wait logs a spurious "Bulk job pump loop faulted" (`:550`).
- **61. [J]** Latent and pre-existing: dormant ServiceNow looks up every non-REQ ticket in `incident` (`ServiceNowService.cs:97`), so CHG, RITM and SCTASK tickets would be refused once validation is on. A mistyped ServiceNow:Enabled now reads as on.
- **62. [C]** Graph list and provisioning issues:
  - Named Locations and M365 member lists now fail whole when a later page fails, with no retry (`NamedLocationsService.cs:72`, `M365GroupManagementService.cs:365-368`);
  - the Graph provisioning script names the Delinea field "Client ID" (`tools/New-ExchangeAdminWebGraphApps.ps1:14`), while the services read "Application ID" (`RiskyUsersService.cs:54`, `IntuneDeviceService.cs:82`).
- **63. [F]** Pre-existing: MFA Reset fetches its secret and a token for each method it deletes (`MfaResetService.cs:114`).
- **64. [G]** Defender:
  - it no longer searches on open: "No search has been run." (`DefenderEndpointDevices.razor:261`);
  - the audit field Devices is now the count after the page's filters (`:924`, `:1220`), so Splunk searches on it change meaning.
- **65. [G]** Config typos now fail silently: a mistyped Email:SmtpUseSsl reads as on, and a mistyped NotifyUsersOnPermissionGrant reads as off (`EmailService.cs:55`, `:59`).
- **66. [D]** Account Lockout Remediation (module disabled):
  - logoff refuses a session when GetOwnerSid fails for any one of its processes, and the protected csrss.exe may fail it for every session (`AccountLockoutRemediationService.cs:942-943`, `:1010`);
  - the SID lookup strips the domain and searches the host domain only (`:1102`, `:701`).

  Probe it before enabling the module.
- **67. [G M]** Attribute editor:
  - no edit made before this deploy can be undone: "Cannot undo: this audit record predates object-identity capture" (`ADAttributeEditorUndoService.cs:516`);
  - the editor mails administrators on every save outcome (`ADAttributeEditorService.cs:1081`);
  - the Undo's conflict lookup is not pinned to the save's DC (`:1226`).
- **68. [E]** Defender's site reads queue one at a time (`AdSiteDirectory.cs:57`). A cancelled read keeps its slot until AD answers.
- **69. [G]** Delegation Report: an unreadable calendar now shows an error and files Failed (`DelegationReportService.cs:83-89`), and the Check button enables as you type.
- **70. [M]** "Refusing to discard migration reports for a blank batch name" (`MigrationReportStore.cs:237`, called from `Migration.razor:4017`) fires whenever the open batch goes to none, including when you leave the page (seen on dev at 08:21:32). It is noise; log it at Debug.
- **71. [O]** Conference Rooms after the room-list reversal:
  - the preview swallows the lookup throw (`ConferenceRooms.razor:1259`) and shows "new" for a row the apply will refuse, which is kd-66's shape again;
  - `ModuleCatalog.cs:1146` carries an orphaned note fragment;
  - `ConferenceRoomService.cs` grew from 1,643 to 3,088 lines.
- **72. [O]** No scar:
  - the deploy revert and reinstatement (23e8e5ca, e0808859, 1b2e015e) is byte-identical;
  - kd-1 is fully reverted (708770ef);
  - the merge renumbering of module versions (Conference Rooms 2.6.7 to 2.19.11) has no user impact.
- **73. [D]** Script details:
  - the start check accepts the first "Started" poll (`AppPoolControl.psm1:245`);
  - `gitDirty = 'unknown'` reads as a dirty tree (`deploy.ps1:145`);
  - the installer's IIS: drive guard is missing under PowerShell 7 (`:911`);
  - the cdt-59 fix is missing at `deploy.ps1:372`/`:696`;
  - there are four copies of the sharing report;
  - `deploy.ps1:893`/`:898` print two always-irrelevant warnings.
- **74. [I]** Reads that still look complete when they are not:
  - single-page or capped reads shown as complete: `RiskyUsersService.cs:282`, `ServiceHealthService.cs:179`/`:186-187`, `M365GroupManagementService.cs:76` (`$top=50`), `GroupManagementService.cs:170` (`.Take(100)`);
  - reads that throw leave no audit row: `DelegationReport.razor:178-181`, `OutOfOffice.razor:344-346`, `BlockedSenders.razor:207-210`;
  - tokens are not passed on: `Comms10kReplaceProcessor.cs:85`, `ConferenceRoomBulkProcessor.cs:162`.
- **75. [L]** Wording drift:
  - six missing-ticket messages;
  - "Authorization denied." in 35 places against "Authorization denied" in 16;
  - four protected-target wordings;
  - partial and unconfirmed results red on five pages but amber on Conference Rooms;
  - "Cancelled by operator." (`BulkJobService.cs:709`) against "Cancelled by the operator. Nothing was written.";
  - "ok" against "succeeded";
  - "(unavailable)", "-", "(none)", "N/A" for a missing value;
  - credential-failure texts that tell the operator to configure a Secret ID that is already set (`ADAttributeEditorService.cs:1005`, `LicensingUpdatesService.cs:95`, `EmergencyDisableService.cs:245`).
- **76. [K]** Code shaped to the test harness:
  - test seams on production singletons: `PermissionValidator.cs:31/:38/:44`, `CircuitMembershipRevalidator.cs:112/:330`, `OperatorMembershipDirectory.cs:77`, `DelineaService.cs:26-28`, `ExoConnectionPool.cs:177/180`, `CloudPasswordResetService.cs:268`, `DefenderEndpointDeviceService.cs:98-101`;
  - interfaces kept only as seams: IGroupGrantDirectory, IOrganizationalUnitDirectory, IBulkJobCancelRequests;
  - code that runs only in tests: `GetUnusableGrants`, the stopped-pipeline guard `ExoConnectionPool.cs:55`, the host-stopping branch `ConferenceRoomBulkProcessor.cs:195`, the dead `MatchesDnToProtectedGroup` at `ProtectedPrincipalService.cs:1583`;
  - code shaped around line-keyed ClickGateRegistry entries: `ConferenceRoomService.cs:2920-2921`, `SelfGrantRefusalRecord.cs:12`;
  - PowerShellWarmUp, added for a race "never reproduced" (`PowerShellWarmUp.cs:20`).

  None of this changes run-time behaviour. The risk is that the tests over these seams say nothing about items 1-13: every one of today's three failures was a stand-in that accepted what the real system refuses, and the opt-in live tests cover only ADDirectorySearchService search and validate.
- **77. [N]** Scope beyond the findings:
  - the -AllowRepoint chain (cdt-36, then 44/45/49/51/55), for a path the pipeline never takes;
  - the revalidation off-switch and interval ceiling (item 16);
  - -NarrowPublishAcl (item 39);
  - cdt-46's Add-Type P/Invoke, compiled per session for 8.3 and junction spellings (`AppPoolControl.psm1:333`).

  The reviewers found the rest finding- or plan-driven.

## The five checks to run first, in order

1. **Dev, Migration page, one minute.** Run a ToCloud eligibility check on one on-premises mailbox that has no archive.
   - Pass: Eligible, or a real reason.
   - Fail, item 2: "Could not be determined" with "Get-MigrationUser failed: ||The operation couldn't be performed ...".
   - Fail, item 3: "Get-MailboxStatistics -Archive failed".
   - Fail, item 14: "no directory user in the forest has the user principal name" or "the excluded group ... could not be resolved".
   - Only an audit row is written.
2. **Exchange Online, read-only, two minutes**, in a session using the app's module version. Items 2, 13 and 26.
   ```
   $g = (Get-Mailbox '<test mailbox>').ExchangeGuid.Guid; $r = (Get-Mailbox '<test room>').ExchangeGuid.Guid
   foreach ($c in {Get-Mailbox -Identity $g}, {Get-Recipient -Identity $g}, {Get-RecipientPermission -Identity $g | Select -First 1},
                  {Get-MailboxFolderPermission -Identity "$($g):\Calendar" | Select -First 1}, {Get-MailboxAutoReplyConfiguration -Identity $g},
                  {Get-Place -Identity $r}) { try { & $c | Out-Null; 'ok' } catch { 'FAIL ' + $_.Exception.Message } }
   try { Get-Mailbox -Identity 'nobody-zz9@<your domain>' -ErrorAction Stop } catch { '{0} | {1} | {2}' -f $_.CategoryInfo.Category, $_.CategoryInfo.Reason, $_.FullyQualifiedErrorId }
   (Get-Recipient '<test mailbox>').WhenCreated.Kind
   ```
   - Six "ok" lines: item 13 is safe.
   - The not-found line must show ObjectNotFound, or a Reason ending NotFoundException, or item 2 is confirmed.
   - "Utc": item 26 is safe.
3. **On-premises Exchange shell, read-only, two minutes.** Items 6 and 3.
   ```
   (Get-RemoteMailbox -ResultSize Unlimited | Where-Object { $_.ExchangeGuid -eq [guid]::Empty }).Count
   try { Get-MailboxStatistics -Identity '<on-prem mailbox with no archive>' -Archive -ErrorAction Stop | Out-Null; 'row' } catch { '{0} | {1} | {2}' -f $_.CategoryInfo.Category, $_.CategoryInfo.Reason, $_.FullyQualifiedErrorId }
   ```
   - The count is how many cloud mailboxes item 6 refuses for every grant, Out of Office change and room write.
   - The second line must show ObjectNotFound or a Reason ending NotFoundException. Otherwise item 3 blocks every ToCloud check of a no-archive mailbox.
4. **Dev, Module Configuration and Admin Settings, five minutes.** Items 8 and 17.
   - For each module, open Module admins and hover each entry. An entry whose tooltip is not an `S-1-5-21-...` SID no longer grants anything; re-pick it before prod.
   - Then look at Admin Settings > Protected principals. Every stale badge will refuse every protected check in the app once kd-88 is fixed.
5. **After the kd-88 fix is on dev, fifteen minutes.** Items 1, 4, 5, 9 and 18.
   - Time four Mailbox Permissions grants on a test mailbox:
     - to a user;
     - to a group you are not in;
     - to a group you are in (expect "You cannot grant permissions to yourself");
     - on an on-premises mailbox.
   - Add one Group Management member to a test group whose managedBy is a user.
   - Then in the dev log:
     - count `GET .../api/v1/secrets/` lines per grant (the target is 1);
     - search for "u,s,e,r", "error occurred while revalidating", "no Active Directory object holds this mailbox GUID" and "could not be resolved to exactly one directory group".

## What I would revert or simplify before prod

1. **Turn session revalidation off on prod** until the silent sign-out (item 5) is fixed and you have said yes to it yourself (items 15 and 16). Set `"Authentication": { "RevalidationEnabled": false }` in prod's appsettings.json.
   - Cost: one line. Access is then decided at sign-in, as on 2.27.1, and the app logs and audits at startup that the re-check is off.
2. **Recognise Exchange's "not found" by its sentence again** (item 2), as the recipient resolver has done since July.
   - Cost: a few lines and tests. Without it, Migration cannot create batches.
3. **One credential fetch and one directory session per grant** (items 1, 4 and 18):
   - drop the second run of both checks at the on-premises write;
   - answer "is the operator in this group" from the operator's own sign-in token instead of a directory walk.
   - Cost: medium. It is kd-88's items 2 and 3 carried into the self-grant check, and it removes the third caller of the walk.
4. **The mailbox-GUID proof** (item 6): revert 7f1a0e6e (aud-a2c), or let "no holder" pass with a warning, until check 3 has counted the mailboxes it refuses.
   - Cost: one commit and its tests. It reopens a narrow hole: an AD account carrying the owner's address could stand in for the mailbox. Your call.
5. **Exchange Online connect gate** (item 7): look in the pool again after the gate.
   - Cost: about five lines and one test.
6. **Comms-10k** (item 19): go back to member DNs, the form measured live, or add and verify one GUID before clearing the list.
   - Cost: small. It is the only change in the range that can empty a list on its first run.
7. **Bulk-run notifications** (item 21): go back to the one administrator summary, or send one digest per person after the loop.
   - Cost: small.
8. **Group Management manager notice** (item 9): read objectClass as a string, and stop filing a Failed row for a group that has no manager.
   - Cost: two lines and a real (not source-scan) test.
9. **Serving-pool check** (item 34): look only at the IIS nodes that can reach this instance's folder, and warn on the rest.
   - Cost: small to medium. It removes the class of refusal that blocked the first deploy.
10. **Defender** (item 10): read sites outside the 4-minute budget.
    - Cost: a few lines.
