# aud-a52: Self-grant check - a nested group membership still passes, and every grant to a cloud-only group is refused outright

**Severity**: MEDIUM (both parts). (1) `PermissionValidator`'s group branch (aud-a43) checks the primary group
and a direct `member=<operator DN>` link only; an operator in a group that is itself a member of the granted
mail-enabled security group is allowed, receives access through nesting, and no refusal is recorded. The
aud-a43 record named nested membership a known gap; under the EVERYTHING go a known gap on an authorization
check is a finding. (2) The same branch refuses any Exchange group recipient with no matching on-premises
group as "could not be completed"; a Microsoft 365 group or any cloud-only group never has one, so every
Mailbox Permissions and Calendar Permissions grant to such a group now fails before Exchange can write - a
functional block introduced by aud-a43.
**Status**: In progress (folded into the aud-a50 agent's scope); plan committed
**Branch**: `worktree-agent-a80f49ad9ee618412`
**Commit**: -
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

## Guard proof

Pending.
