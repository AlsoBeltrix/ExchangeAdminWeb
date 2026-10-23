# aud-a52: Self-grant check - a nested group membership still passes, and every grant to a cloud-only group is refused outright

**Severity**: MEDIUM (both parts). (1) `PermissionValidator`'s group branch (aud-a43) checks the primary group
and a direct `member=<operator DN>` link only; an operator in a group that is itself a member of the granted
mail-enabled security group is allowed, receives access through nesting, and no refusal is recorded. The
aud-a43 record named nested membership a known gap; under the EVERYTHING go a known gap on an authorization
check is a finding. (2) The same branch refuses any Exchange group recipient with no matching on-premises
group as "could not be completed"; a Microsoft 365 group or any cloud-only group never has one, so every
Mailbox Permissions and Calendar Permissions grant to such a group now fails before Exchange can write - a
functional block introduced by aud-a43.
**Status**: Merged `81809833`; closed on guard proof; codex review pending
**Branch**: `worktree-agent-a80f49ad9ee618412` (from master `132d6e97`)
**Commit**: plan `43c47893`; fix `f7921ce8` (after aud-a50 parts 1, 3, 2; before its part 4); comments `dcfec67c`
**Reviewer**: codex / gpt-5.5-dzs / xhigh / standard (perm-batch-5, 2026-10-22, candidates 1 and 2)

## Evidence

At `132d6e97`: `Services/PermissionValidator.cs` around 783 (direct member and primary group only) and 773
(the cloud-only refusal); `Services/SelfGrantDirectory.cs` (the member-link read);
`Services/ProtectedGroupMembership.cs` `Evaluate` (the transitive shape already in the repo,
`member:1.2.840.113556.1.4.1941`).

## Approach (to be written by the implementing agent before code)

- Transitive membership for a directory group grantee: the LDAP_MATCHING_RULE_IN_CHAIN filter against the
  group's own domain (reuse the protected-group transitive shape; call it, do not copy it), plus the
  primary-group handling; an incomplete read refuses; a nested member is a self-grant.
- Cloud-only group grantee: when Exchange names a group the directory does not hold, the membership check
  asks Exchange (`Get-DistributionGroupMember` / the unified group's members through the existing EXO
  session) for the operator's membership, transitive where the cmdlet allows; a check that cannot answer
  refuses as incomplete; one that answers "not a member" allows. Nothing names the environment.
- Tests through `SelfGrantDirectoryStandIn` (a nested chain; a cloud-only group with and without the operator)
  and `ExchangeWriteHarness`; mutations at the call site: direct-only membership again; the outright
  cloud-only refusal again.

## Plan (approved by the owner's EVERYTHING go)

Owner go 2026-10-21 (`.agents/decisions.md` 2026-10-21); folded into the aud-a50 agent by the supervisor on 2026-10-22,
record taken from master `1a9d34ac`. Branch `worktree-agent-a80f49ad9ee618412` (from `132d6e97`): one fix commit
`fix(permission-validator): ... (aud-a52)`, after aud-a50 parts 1, 3 and 2 and before its part 4, so the validator work is
contiguous; its comments go in the branch's comments-only commit. Behaviour: the shared `PermissionValidator`,
`PowerShellProtectedGroupDirectory` and `IIdentityResolver` (base app version) and what a Mailbox Permissions or Calendar
Permissions grant refuses (both module versions); the bumps are the supervisor's.

Evidence re-read at `132d6e97`:
- `PermissionValidator.CompareInDirectory` 783-801: each group the grantee names is tested by `IsPrimaryGroupOf` (826-835)
  or `SelfGrantDirectory.HasDirectMember` (a `(member=<operator DN>)` read in the group's own domain): a member of a group
  nested in the granted group is allowed and nothing is recorded.
- 773-778: every group recipient Exchange names with no on-premises group carrying its address is refused as incomplete,
  whatever Exchange says of its sync state (`ResolvedRecipient.ExistsOnPrem`) - so every grant to a cloud-only group.
- `ProtectedGroupMembership.Evaluate` 193-312 answers transitive membership across the forest: the target and its primary
  group as seeds, global ancestry in the target's own domain and universal ancestry at the catalog by the in-chain rule,
  each entry bound to exactly one group in its own domain, a domain-local group asked in that domain, any read that fails
  marking the answer incomplete. Its callers (`ProtectedPrincipalService`, `ResourceDomainDenies`) pass it a
  `PowerShellProtectedGroupDirectory`, whose every read carries a credential; the self-grant reads run under the app
  pool's identity (aud-a43) in a session that already holds the forest's catalog.
- `TryExpandGroupAsync` (1004-1097) is the repo's Exchange expansion of a group: `Get-Recipient`, then
  `Get-DistributionGroupMember -ResultSize Unlimited`, through the pool. `IIdentityResolver` has one method.

1. **Nested membership - Evaluate, called.** The group branch calls `ProtectedGroupMembership.Evaluate` once, with every
   group the grantee names (their DNs) as the entries and the operator's DN as the target, over the self-grant session's
   own reads: `PowerShellProtectedGroupDirectory` gains `UnderSessionIdentity(PowerShell, string globalCatalog)` - the same
   reads with no `-Credential`, the catalog the session discovered - and `SelfGrantDirectory` hands one out. A match (the
   operator in a group the grantee names, by any nesting the evaluator reads, or as or through their primary group)
   refuses as a self-grant; an answer that is incomplete with no match refuses as incomplete ("whether you are a member of
   the group '<x>' could not be read"). `IsPrimaryGroupOf` and `HasDirectMember` (with `DirectMemberFilter`) are removed:
   the evaluator's seeds carry the primary group, and its entry read is the group read in its own domain. The
   credentialed constructor and every read it makes are unchanged: a flag set only by the new factory drops the
   credential, not a null credential.
2. **Cloud-only groups - Exchange asked.** A group Exchange names that the directory does not hold: synced
   (`ExistsOnPrem` true) stays refused as incomplete - its membership is the directory's and the directory did not find
   it; cloud-only goes to Exchange. New `IIdentityResolver.ReadGroupMembersAsync(group)` (throws when the membership could
   not be read in full, never an empty list for one), implemented by `ExchangeIdentityResolver` through the pool as
   `ResolveRecipientAsync` is (the configured guards, `RunPooledQueryAsync`, read-only so retry-eligible) over new static
   `ExchangeGroupMembers.Read`: `Get-Recipient` for the group's canonical address gives its `RecipientTypeDetails`; a
   Microsoft 365 group (`GroupMailbox`) is read with `Get-UnifiedGroupLinks -LinkType Members -ResultSize Unlimited`, a
   distribution group, mail-enabled security group or room list with `Get-DistributionGroupMember -ResultSize Unlimited`.
   Transitive where Exchange holds the membership: a nested group Exchange reports as cloud-only is read the same way
   (each group once, at most 50); one it reports as synced is listed and not expanded - its membership is the
   directory's. Any other kind of group, a nested group with no address or no sync state, an error, or more groups than
   the bound throws. The validator: Exchange holding no recipient for the operator refuses as incomplete (there is nobody
   to compare the members with); a read that throws, or answers nothing at all, refuses as incomplete; a member that is
   the operator's recipient - a shared non-blank object id, or their primary address or their account's - refuses as a
   self-grant; a member that cannot be compared (no address, and no object id both sides hold) refuses as incomplete; a
   synced group among the members is found in the directory by its address (not found: incomplete) and evaluated with
   the rest in the one `Evaluate` call above - the directory holds its whole membership, nested groups Exchange does not
   list included; otherwise the grant is allowed.

Tests. `SelfGrantDirectoryStandIn` gains `Get-ADGroup` (no `Credential` parameter, so a credentialed read is a binding
error), the in-chain and bit-and rules, group scopes (`GroupScope`, `groupType`) and each domain's Domain Users; the
in-chain rule follows only the links the server asked holds (a domain's own groups; universal groups at the catalog).
`PermissionValidatorSelfGrantTests`:
- nested: a chain in the operator's domain; a domain-local group of another domain holding a group of the operator's
  domain; a universal chain across domains; the operator's primary group nested in the granted group - each refused; a
  chain without the operator allowed; a nested read that fails refused as incomplete. aud-a43's direct, primary-group,
  other-domain and unreadable cases carried over, their assertions following the reads to `Get-ADGroup` and the evaluator's
  filters, and a group of another domain holding the operator declared domain-local, as Active Directory requires.
- cloud-only: the operator a member by address and by object id, refused; not a member, allowed; the read throwing or
  answering nothing, incomplete; Exchange holding no recipient for the operator, incomplete; a member that cannot be
  compared, incomplete; a synced group nested in it that holds the operator in the directory, refused; one the directory
  does not hold, incomplete; a synced group Exchange names that the directory does not hold, incomplete as before.
New `ExchangeAdminWeb.Tests/ExchangeGroupMembersTests.cs`: the reader over a local runspace of stand-in `Get-Recipient`,
`Get-DistributionGroupMember` and `Get-UnifiedGroupLinks` (advanced functions: an unexpected parameter is a binding error):
a group's members; a Microsoft 365 group's through `Get-UnifiedGroupLinks -LinkType Members`; `-ResultSize Unlimited` on
every read; a nested cloud group read; a cycle read once; a synced nested group listed, not read; each failure throws.
And `ExchangeIdentityResolver.ReadGroupMembersAsync` over a pool whose Exchange Online rows cannot be read, or that is not
configured: throws, nothing connected; over `ExchangeWriteHarness`'s pool: the members its stand-ins report.

Mutations at the call site (the aud-a50 protocol): N1 direct-only membership again - aud-a43's primary-group-or-direct-link
test back in place of `Evaluate`, its two helpers restored (`PermissionValidator.cs` and `SelfGrantDirectory.cs`); N2 the
outright cloud-only refusal again - aud-a43's block back for every group the directory does not hold. Extras: N3 an
incomplete answer taken as "not a member"; N4 the synced groups nested in a cloud-only group not evaluated in the
directory; N5 the reader reading no nested group; N6 the operator with no Exchange recipient allowed.

Files: `Services/PermissionValidator.cs`, `Services/SelfGrantDirectory.cs`, `Services/PowerShellProtectedGroupDirectory.cs`,
`Services/IIdentityResolver.cs`, `Services/ExchangeIdentityResolver.cs`, `Services/ExchangeGroupMembers.cs` (new),
`ExchangeAdminWeb.Tests/SelfGrantDirectoryStandIn.cs`, `ExchangeAdminWeb.Tests/PermissionValidatorSelfGrantTests.cs`,
`ExchangeAdminWeb.Tests/ExchangeGroupMembersTests.cs` (new), this record.

Labelled assumptions (Exchange Online documentation, not probed - no live call is made): `Get-UnifiedGroupLinks -LinkType
Members` lists a Microsoft 365 group's members and `Get-DistributionGroupMember` a distribution or mail-enabled security
group's direct members, each row carrying `PrimarySmtpAddress`, `ExternalDirectoryObjectId`, `RecipientType`,
`RecipientTypeDetails` and `IsDirSynced`; `RecipientTypeDetails` `GroupMailbox` names a Microsoft 365 group; a synced
group's whole membership is the on-premises directory's.

## Fix

- `Services/PermissionValidator.cs`: `CompareInDirectory` is `CompareInDirectoryAsync`, each directory read still off the
  caller's thread. The DNs of the groups the grantee names - and, for a cloud-only group, of the synced groups nested in
  it - go to one `ProtectedGroupMembership.Evaluate(groups, account.DistinguishedName, directory.Groups, _logger)`: a match
  refuses as a self-grant (the matched groups logged); an incomplete answer, or a throw out of the call, refuses as
  incomplete with aud-a43's text. `IsPrimaryGroupOf` is removed. A group Exchange names that the directory does not hold:
  synced, refused as incomplete as before; cloud-only, new `CompareInExchangeGroupAsync` - no Exchange recipient for the
  operator: "Exchange Online holds no recipient for you, so whether you are a member of the group '<x>' could not be
  checked"; `IIdentityResolver.ReadGroupMembersAsync(<the group's canonical address>)` through a fresh scope, where a throw
  or a null answer refuses as incomplete; a member that is not a group and is the operator's recipient
  (`IsTheOperatorsRecipient`: a shared non-blank object id, or the primary address of their recipient or their account)
  refuses as a self-grant; one that cannot be compared (`CanBeCompared`: no address, and no object id on both sides)
  refuses as incomplete; each synced group among the members - none looked up when the operator has no directory
  account - is found by `FindNamedBy(address, address)` as a group carrying the address (none, or a search that fails,
  refuses as incomplete) and joins the evaluation.
- `Services/PowerShellProtectedGroupDirectory.cs`: `UnderSessionIdentity(ps, globalCatalog)` (a blank catalog throws): the
  catalog preset, and a flag only it sets, under which `Run` passes no `-Credential`. The credentialed constructor and
  every read it makes are unchanged.
- `Services/SelfGrantDirectory.cs`: `Groups`, the session's `UnderSessionIdentity` reads over its own runspace and
  catalog; `HasDirectMember` and `DirectMemberFilter` removed.
- `Services/IIdentityResolver.cs`: `ExchangeGroupMember(PrimarySmtpAddress, ExternalDirectoryObjectId, RecipientType,
  RecipientTypeDetails, IsDirSynced)`, `IsGroup` when the RecipientType carries "Group"; `ReadGroupMembersAsync(group)`.
- `Services/ExchangeIdentityResolver.cs` `ReadGroupMembersAsync`: `ResolveRecipientAsync`'s guards (unreadable, not
  configured: throw), then `RunPooledQueryAsync(... ExchangeGroupMembers.Read(ps, group, pipeline => Invoke(pipeline,
  tracker)), allowRetry: true)`.
- New `Services/ExchangeGroupMembers.cs` `Read`: `Get-Recipient -Identity` (exactly one row), then each group by its
  `RecipientTypeDetails` - `GroupMailbox` with `Get-UnifiedGroupLinks -LinkType Members -ResultSize Unlimited`;
  `MailUniversalDistributionGroup`, `MailUniversalSecurityGroup`, `MailNonUniversalGroup`, `RoomList` with
  `Get-DistributionGroupMember -ResultSize Unlimited`; any other kind throws. Every row is a member; a member that is a
  group with no sync state throws, a synced one is not read, a cloud-only one (an address required) is read once
  (visited by address); more than `MaxGroupsRead` (50) groups throws.

Tests: `SelfGrantDirectoryStandIn` - `Get-ADGroup` (groups only; no `Credential` parameter), the in-chain and bit-and
rules, group scopes (`GroupScope`, `groupType`), each domain's Domain Users (`DomainUsers`, `OtherDomainUsers`), `Searches`
over both cmdlets, the failure keys per cmdlet. `PermissionValidatorSelfGrantTests` +18:
`AGroupHoldingTheOperatorThroughAnotherGroup_IsRefused` (5 chains), `AGroupNestingOnlyOthers_IsAllowed`,
`ANestedMembershipThatCannotBeRead_IsRefused_AsIncomplete`, `ACloudOnlyGroupHoldingTheOperator_IsRefused_ExchangeAskedForItsMembers`
(by address, by object id), `ACloudOnlyGroupWithoutTheOperator_IsAllowed`, `ACloudOnlyGroupWhoseMembersCannotBeRead_IsRefused_AsIncomplete`
(throws, no answer), `ACloudOnlyGroup_WhenExchangeHoldsNoRecipientForTheOperator_IsRefused_AsIncomplete`,
`ACloudOnlyGroupMemberThatCannotBeCompared_IsRefused_AsIncomplete` (2), `ASyncedGroupInACloudOnlyGroup_IsAskedOfTheDirectory`
(the operator inside, and not), `ASyncedGroupInACloudOnlyGroup_ThatTheDirectoryDoesNotHold_IsRefused_AsIncomplete`; aud-a43's
cases carried over - `AGroupTheOperatorIsADirectMemberOf_IsRefused` (the decisive read now the evaluator's, the group of
another domain domain-local), `TheOperatorsPrimaryGroup_IsAGroupTheyAreAMemberOf` (the forest's own Domain Users),
`AGroupWithoutTheOperator_IsAllowed_AndItsMembershipIsAskedInItsOwnDomain` (its reads at its domain),
`AMembershipThatCannotBeRead_IsRefused_AsIncomplete` (the `Get-ADGroup` failure keys), and
`AGroupExchangeNamesThatTheDirectoryDoesNotHold_IsRefused_AsIncomplete` now
`ASyncedGroupExchangeNamesThatTheDirectoryDoesNotHold_IsRefused_AsIncomplete` (a synced group; Exchange not asked). New
`ExchangeGroupMembersTests` (16): a distribution or security group's members; a Microsoft 365 group's through
`Get-UnifiedGroupLinks -LinkType Members`; a nested cloud group and a Microsoft 365 group under it read; a cycle read once;
a synced nested group listed, not read; seven ways the read throws (not found, no kind, a dynamic group, a nested group
without an address or a sync state, a member read that fails, 51 groups); 50 groups read; and the resolver - through the
harness's pool (`-ResultSize Unlimited`, one connection), over an Exchange Online not configured and over rows that cannot
be read (throws, nothing connected). `ProtectedGroupMembershipTests.TheCredentialedReads_KeepTheirCredential_AndTheSessionIdentityReads_CarryNone`.

Caller-visible change, by design: a Mailbox Permissions or Calendar Permissions grant, single or bulk, to a group that
holds the operator through any nesting the forest's in-chain reads find, or through their primary group nested in it, is
refused as a self-grant; such reads that cannot be made refuse as incomplete. A grant to a group only Exchange Online holds
is no longer refused outright: it is allowed when Exchange's members of it (and of the cloud groups nested in it, the
synced ones read in the directory) do not include the operator, refused as a self-grant when they do, and refused as
incomplete when they cannot be read or compared or Exchange holds no recipient for the operator. Each group grantee costs
the evaluator's reads (the operator and their primary group in their own domain, ancestry there and at the catalog, each
group in its own domain) where it cost two; a cloud-only one costs one Exchange borrow with a read per group in its
nesting, at most 50.

## Guard proof

The aud-a50 driver and protocol (that record's Guard proof). Filter
`FullyQualifiedName~PermissionValidator|FullyQualifiedName~ExchangeGroupMembers|FullyQualifiedName~PermissionPagesSelfGrant|FullyQualifiedName~ProtectedGroupMembership|FullyQualifiedName~ResourceDomainDenies|FullyQualifiedName~GroupMemberNesting|FullyQualifiedName~MailboxPermission|FullyQualifiedName~CalendarPermission|FullyQualifiedName~ExchangeIdentityResolver`
(409 tests for N1-N6, 410 once the credential guard was added for N7 and N8). Fixed-file MD5s:
`Services/PermissionValidator.cs` `037364B38955C7026009754697B0E8FD`, `Services/SelfGrantDirectory.cs`
`A1070A2D646391EFD5CB8C9B23EA3A05`, `Services/PowerShellProtectedGroupDirectory.cs` `9477ED43438D5C27A9B84762A4C36744`,
`Services/ExchangeGroupMembers.cs` `F1F7ABAC2A17B0A7869722F02B0B6283`. Every mutant built with 0 errors and every restore
matched. After the round, the restored tree rebuilt and the filter: 410/410 green.

| # | Mutation (call site) | Mutant MD5 | Named tests failing |
|---|---|---|---|
| N1 | direct-only membership again: aud-a43's primary-group-or-direct-link loop over the named groups in place of `Evaluate`, `IsPrimaryGroupOf` and `HasDirectMember`/`DirectMemberFilter` restored | `PermissionValidator.cs` `95114405415127B3B5799FF7A25F8D70`, `SelfGrantDirectory.cs` `46D79D4EA64B0BEA6C100883C96EED79` | 13 of 409: all five `AGroupHoldingTheOperatorThroughAnotherGroup_IsRefused` chains and `ASyncedGroupInACloudOnlyGroup_IsAskedOfTheDirectory(operatorInside: True)` - allowed; `ANestedMembershipThatCannotBeRead_IsRefused_AsIncomplete` - allowed (no universal read made); the three `AGroupTheOperatorIsADirectMemberOf_IsRefused` rows and `AGroupWithoutTheOperator_IsAllowed_AndItsMembershipIsAskedInItsOwnDomain` - the evaluator's reads not made; two `AMembershipThatCannotBeRead_IsRefused_AsIncomplete` rows (the member read fails, the domain cannot be reached) - the `Get-ADGroup` reads they fail are not made, so allowed |
| N2 | the outright cloud-only refusal again: aud-a43's block for every group the directory does not hold | `5E344AFA5E8E6B0DDAB27D2DCA4E25FC` | 11 of 409, every cloud-only case: the outright refusal where the operator is not a member (`ACloudOnlyGroupWithoutTheOperator_IsAllowed`, `ASyncedGroupInACloudOnlyGroup_IsAskedOfTheDirectory(operatorInside: False)`), and its text in place of each other case's refusal |
| N3 | extra: an incomplete `Evaluate` answer taken as "not a member" (its refusal removed) | `9779520E8074B7B615C7728AD7FB2FDD` | 4 of 409: the three `AMembershipThatCannotBeRead_IsRefused_AsIncomplete` rows and `ANestedMembershipThatCannotBeRead_IsRefused_AsIncomplete` - allowed |
| N4 | extra: the synced groups nested in a cloud-only group not evaluated (`groups.AddRange(synced);` removed) | `4E887632038A700082F12BE49A69815C` | 1 of 409: `ASyncedGroupInACloudOnlyGroup_IsAskedOfTheDirectory(operatorInside: True)` - allowed |
| N5 | extra, `ExchangeGroupMembers.Read`: no nested group read (the enqueue removed) | `844FE55A8FD28EE9C5217701AA4F4102` | 4 of 409: `ACloudGroupNestedInIt_IsReadToo`, `AGroupNestedInItsOwnMember_IsReadOnce`, `AsManyGroupsAsTheBound_AreRead`, `AMembershipThatCannotBeReadWhole_Throws(shape: "more groups than the bound")` |
| N6 | extra: the operator with no Exchange recipient allowed past a cloud-only group | `0452C0EB5DC89317753E6093685DC55C` | 1 of 409: `ACloudOnlyGroup_WhenExchangeHoldsNoRecipientForTheOperator_IsRefused_AsIncomplete` |
| N7 | extra, `PowerShellProtectedGroupDirectory.Run`: the credentialed reads lose their credential | `F88B84F433B4E0EE6F615338227F0F5F` | 1 of 410: `ProtectedGroupMembershipTests.TheCredentialedReads_KeepTheirCredential_AndTheSessionIdentityReads_CarryNone` |
| N8 | extra, the same: the session-identity reads pass a credential too | `9AD07DD0ACD11A281048CE93D8E2DB27` | 14 of 410: that test, and every case whose answer needs a group read (the stand-in declares no `Credential`, so each read fails to bind and the check is incomplete) - the five nested chains, `AGroupNestingOnlyOthers_IsAllowed`, the three direct rows, `AGroupWithoutTheOperator_...`, `TheOperatorsPrimaryGroup_...`, both `ASyncedGroupInACloudOnlyGroup_IsAskedOfTheDirectory` rows |

No separate pre-fix reproduction: N1 and N2 are the code as it stood at `132d6e97`, each at its call site.

Before the commit, the whole suite over this commit's tree (Debug, `dotnet test --no-build` on the test project): 6,180
passed / 3 skipped (the known AD skips) / 0 failed, 6,183 total.

## Comments after the round

In the branch's comments commit `dcfec67c`, checked comment-only as aud-a50 records it. This record's files, their
code-line MD5s equal before and after: `Services/PermissionValidator.cs` (58 lines, with aud-a50's)
`20B028FDC08C8FB53E3F75C6EC19B474`, `Services/SelfGrantDirectory.cs` (8) `FE115BC2685DCEDF3950BAC866AD6AB8`,
`Services/PowerShellProtectedGroupDirectory.cs` (12, one of them the removal of a summary line rewritten)
`D01492844F91F5DF80C3FF0CB08F79D6`, `Services/IIdentityResolver.cs` (16) `5C8CA633AF2F91FAD63B2EDEB3297C42`,
`Services/ExchangeIdentityResolver.cs` (2) `8D872F8A4C804E235E8F0D5080FD2A42`, `Services/ExchangeGroupMembers.cs` (28)
`9439A783EF97418F5C55FFCB65918597`. The stale remarks the fix left - aud-a43's note that a group holding the operator only
through another group is not detected, and that a group Exchange names must be one the directory holds - are corrected
there.

## Gates - 2026-10-22 (branch tip `dcfec67c`)

As aud-a50 records them: Release build 0 errors, 19 warnings (the baseline, none in a touched file); `dotnet test` 6,195
passed / 3 skipped (the known AD skips) / 0 failed, 6,198 total, 5m39s, nothing hung; format, `git diff --check HEAD` and
the ASCII lint exit 0; the pages 808 and 762 lines. No AD, Exchange Online, Graph, DHCP or IIS call: the forest stand-in,
stand-in resolvers, and stand-in Exchange cmdlets behind the harness's pool or a local runspace. No coder self-review
verdict is claimed.

## Closeout

Fixed on branch `worktree-agent-a80f49ad9ee618412`, not merged, not pushed. Due at merge: the base app version - the shared
`PermissionValidator`, `PowerShellProtectedGroupDirectory` (also the protected-principal check's and the self-service DACL
check's reads; their credentialed path is unchanged and pinned), `IIdentityResolver` and `ExchangeIdentityResolver`, and the new
`ExchangeGroupMembers` - and the Mailbox Permissions and Calendar Permissions module versions: what a grant to a group is
refused or allowed on. The bumps are the supervisor's.

## Known gaps

- Labelled assumptions, as the plan records: the Exchange Online cmdlets' members and their properties as documented, and
  a synced group's whole membership as the on-premises directory's. Nothing ran against a tenant or a forest.
- A member Exchange lists without an address is compared by object id, so an operator whose recipient Exchange reports
  without an object id cannot be told apart from it and the grant is refused as incomplete.
- An operator Exchange Online holds no recipient for cannot grant to a cloud-only group: refused as incomplete.
- A cloud-only group of any kind the reader does not expand (a dynamic distribution group, an unreported kind), or one
  nesting more than 50 groups, is refused as incomplete.
- A group Exchange calls cloud-only that the directory still holds under its address is read in the directory, as a held
  group, not in Exchange.
- The evaluator's `DOMAIN\name` resolution is never reached by the self-grant check, whose entries are DNs; under the
  session's identity it would run without a credential.
