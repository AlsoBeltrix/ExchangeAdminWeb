# aud-a4: Self-Service Groups ignores denies that apply through a group

**Severity**: HIGH - an explicit Deny of member-write is overridden by an individual Allow, and
the privileged module credential then performs the write.
**Status**: Verified
**Branch**: -- (direct-to-main)
**Commit**: `<pending>`

## Evidence

Finding 4 of the 2026-10-16 repository audit, verified against the code before any change.

`Services/SelfServiceGroups/SelfServiceGroupService.CallerCanManageMembers` read the group's
real DACL and then evaluated it like this:

```csharp
foreach (var ace in aces)
{
    var sid = ace.Properties["Sid"]?.Value?.ToString();
    if (!string.Equals(sid, callerSid, StringComparison.OrdinalIgnoreCase))
        continue;
    ...
}
```

Every ACE whose trustee is not the caller's own SID is skipped, so a Deny carried by a **group
the caller belongs to** is never seen. Windows evaluates a DACL against the whole token.

An administrator who denies member-write to `Contractors` and allows it to a person
individually gets the allow. The gate returns true and the module's privileged AD credential
performs the membership write.

The gate guards every path - the owned-group listing, the search, the member listing and the
membership change - so this is the single decision that authorises self-service writes.

## Predicted observable failure

A contractor whose account carries an explicit group-level Deny on a sensitive group's
`member` attribute sees that group in Self-Service Groups, opens it, and adds or removes
members. The directory's own Deny said no; the app never looked at it.

## Approach

**The decision moved out of the service and into `GroupMembershipAce.GrantsMemberWrite`, pure
and unit-testable.** That is not tidying - it is the finding. The file's own header already
said trustee and Allow/Deny matching were "decided by the caller (the live loop)", and the
live loop needs a domain controller, so none of it had ever been tested. That is exactly where
the defect sat. The header is corrected to match.

The evaluator takes the caller's SID **and their group SIDs**, builds the applicable trustee
set, and:

- returns false on any applicable Deny conveying member-write;
- returns true only if some applicable Allow conveys it;
- **applies Deny precedence regardless of ACE order.** A canonical DACL lists denies first but
  `GetAccessRules` guarantees no ordering, and guessing wrong in this direction grants access,
  so every applicable entry is examined before answering.

The service now projects each ACE to a primitive `Entry` and calls it. Anything that is
neither Allow nor Deny is dropped rather than guessed at.

Token groups come from the live principal's claims via
`GroupMembershipChecker.ExtractGroupClaims` - the same source every other authorization path
uses. The read entry points take them as a required parameter so a caller cannot forget;
`ChangeMemberAsync` derives them from its `actingUser` and **refuses outright when that is
null**, because an empty token set makes group denies invisible, which is the permissive
direction.

## Files changed

- `Services/SelfServiceGroups/GroupMembershipAce.cs`
- `Services/SelfServiceGroups/SelfServiceGroupService.cs`
- `Components/Pages/SelfServiceGroups.razor`
- `ExchangeAdminWeb.Tests/GroupMembershipAceTests.cs`
- `ExchangeAdminWeb.Tests/ClickGateRegistry.cs` (`ExpectedLineCount` 1204 -> 1208)

## Guard proof

Six tests against the extracted evaluator. Three pin the finding, three pin the opposite
direction so the first three cannot be passing merely because everything now denies:

| Test | What it pins |
| --- | --- |
| `GroupDeny_BeatsAnIndividualAllow` | the finding itself |
| `GroupDeny_BeatsAnIndividualAllow_EvenWhenTheAllowComesFirst` | order independence |
| `AGroupAllow_IsHonoured` | widening works in the granting direction too |
| `AnotherPrincipalsDeny_IsNotApplicable` | a deny aimed at somebody else does not refuse the caller |
| `SelfMembershipRights_StillDoNotConveyMemberWrite_ThroughAGroup` | widening trustees did not widen RIGHTS - Self (0x08) still never qualifies |
| `NoApplicableTrustee_Denies` | fail closed with no caller identity |

Mutation: deleting the loop that adds the caller's group SIDs to the applicable set **fails
three of them**. File restored byte-identical (md5 verified) and touched.

**A mutation run that proved nothing, recorded because it nearly passed as evidence:** the
first attempt chained `grep -c ... && touch && build`, and `grep -c` returning zero exits
non-zero, so the chain stopped and the test ran against the *previous* binary. It reported a
clean pass. The same trap as the probe that did not compile - a mutation that is never built
is not a mutation.

`ClickGateRegistry` re-anchored: the page grew four lines, `TheRegisteredLineCountStillMatchesTheFile`
caught it, and the highest registered control for this page is line 452 against an `@code`
start of 454 - so every registered line sits above the edit and only the count moved. Verified
rather than assumed.

## Coder dispute

None.

## Known gaps

**Claims are the token as this app sees it, not as the DC computes it.** `ExtractGroupClaims`
reads the Negotiate principal's group SIDs, which is what Windows put in the token - but a
domain-local group from another domain, or a primary-group relationship, may be absent from
it. That is the same family as `aud-a3b`, and it means a Deny carried by such a group is still
invisible. Narrowing that needs an authoritative membership read, not a claims read.

The evaluator is pure and fully tested; **the projection from `PSObject` into `Entry` is
not** - it needs a domain controller. A mistake there (a renamed property, a changed
`AccessControlType` spelling) would silently produce entries with null SIDs, which the
evaluator skips, and the result is fail-closed rather than permissive. Checked for direction,
not covered by a test.

Nothing here has run against a real directory.

## Reviewer comments

Source: the 2026-10-16 audit, finding 4. Verified by the implementer before changing anything.
Independent review of the fix pending.

## Closeout

Fixed directly on `master`. Not pushed.
