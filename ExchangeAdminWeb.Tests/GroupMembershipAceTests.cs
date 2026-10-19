using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the security-critical ACE classifier (plan docs/SelfServiceGroupManagement-Plan.md section 6.3,
/// list-time eligibility). The trap these tests exist to catch: the AD <c>member</c> attribute's schema
/// GUID is SHARED with the Self-Membership validated write, so classifying "manager can edit members"
/// MUST key on the rights BITS (WriteProperty / GenericWrite / GenericAll), never on the ObjectType
/// name. A Self (0x08) ACE on the member GUID must NOT qualify, and a WriteProperty ACE scoped to some
/// OTHER attribute must NOT qualify.
/// </summary>
public class GroupMembershipAceTests
{
    // ActiveDirectoryRights flag values, verified against System.DirectoryServices.
    private const int GenericAll = 0x000F01FF;
    private const int GenericWrite = 0x00020028;
    private const int WriteProperty = 0x00000020;
    private const int Self = 0x00000008;
    private const int ReadProperty = 0x00000010;

    private static readonly Guid Member = GroupMembershipAce.MemberAttribute;
    private static readonly Guid OtherAttribute = new("aaaaaaaa-0000-0000-0000-000000000000");

    [Fact]
    public void GenericAll_conveys_member_write_when_the_ace_is_not_object_scoped()
    {
        // REWRITTEN for aud-a4b finding 5. This used to assert "regardless of object type",
        // which is wrong and was defending the defect: an OBJECT-SPECIFIC ace keeps its scope
        // even when the mask is a composite one. Expanded rights do not discard the ACE's
        // ObjectType, so GenericAll against an unrelated attribute does NOT convey member-write
        // - and the gate was treating it as though it did.
        Assert.True(GroupMembershipAce.ConveysMemberWrite(GenericAll, Guid.Empty));
        Assert.False(GroupMembershipAce.ConveysMemberWrite(GenericAll, OtherAttribute));
        Assert.True(GroupMembershipAce.ConveysMemberWrite(GenericAll, Member));
    }

    [Fact]
    public void ThePropertySetContainingMember_ConveysMemberWrite()
    {
        // aud-a4b finding 4. The member attribute belongs to the Membership property set, and
        // an ACE scoped to the set applies to every attribute in it. The classifier accepted
        // only the attribute's own GUID, so a Deny on the property set was skipped entirely
        // and a following Allow authorised the write.
        Assert.True(GroupMembershipAce.ConveysMemberWrite(
            WriteProperty, GroupMembershipAce.MembershipPropertySet));
    }

    [Fact]
    public void GenericWrite_conveys_member_write_when_the_ace_is_not_object_scoped()
    {
        // Same rewrite and same reason as GenericAll above (aud-a4b finding 5).
        Assert.False(GroupMembershipAce.ConveysMemberWrite(GenericWrite, OtherAttribute));
        Assert.True(GroupMembershipAce.ConveysMemberWrite(GenericWrite, Guid.Empty));
        Assert.True(GroupMembershipAce.ConveysMemberWrite(GenericWrite, Member));
    }

    [Fact]
    public void WriteProperty_on_member_attribute_conveys_member_write()
    {
        // This is exactly what the "Manager can update membership" checkbox grants.
        Assert.True(GroupMembershipAce.ConveysMemberWrite(WriteProperty, Member));
    }

    [Fact]
    public void WriteProperty_on_all_properties_conveys_member_write()
    {
        Assert.True(GroupMembershipAce.ConveysMemberWrite(WriteProperty, Guid.Empty));
    }

    [Fact]
    public void WriteProperty_scoped_to_another_attribute_does_not_convey_member_write()
    {
        Assert.False(GroupMembershipAce.ConveysMemberWrite(WriteProperty, OtherAttribute));
    }

    [Fact]
    public void Self_on_member_attribute_does_not_convey_member_write()
    {
        // The trap: Self-Membership shares the member GUID but only lets the trustee add/remove
        // THEMSELVES. It must never be classified as full member-write.
        Assert.False(GroupMembershipAce.ConveysMemberWrite(Self, Member));
    }

    [Fact]
    public void ReadProperty_on_member_attribute_does_not_convey_member_write()
    {
        Assert.False(GroupMembershipAce.ConveysMemberWrite(ReadProperty, Member));
    }

    [Fact]
    public void No_rights_does_not_convey_member_write()
    {
        Assert.False(GroupMembershipAce.ConveysMemberWrite(0, Member));
        Assert.False(GroupMembershipAce.ConveysMemberWrite(0, Guid.Empty));
    }

    // ----- Audit finding A4: the DACL is evaluated against the whole token -----

    private const string Caller = "S-1-5-21-1111111111-2222222222-3333333333-1101";
    private const string Contractors = "S-1-5-21-1111111111-2222222222-3333333333-1200";
    private const string Stranger = "S-1-5-21-9999999999-8888888888-7777777777-1300";

    private static GroupMembershipAce.Entry Allow(string sid, bool inherited = false, bool inheritOnly = false) =>
        new(sid, IsDeny: false, Rights: 0x00000020, ObjectType: GroupMembershipAce.MemberAttribute,
            IsInherited: inherited, InheritOnly: inheritOnly);

    private static GroupMembershipAce.Entry Deny(string sid, bool inherited = false, bool inheritOnly = false) =>
        new(sid, IsDeny: true, Rights: 0x00000020, ObjectType: GroupMembershipAce.MemberAttribute,
            IsInherited: inherited, InheritOnly: inheritOnly);

    [Fact]
    public void GroupDeny_BeatsAnIndividualAllow()
    {
        // The finding. An administrator denies member-write to Contractors and allows it to the
        // person directly. The old loop only ever looked at the caller's own SID, saw the
        // allow, and let the privileged module credential perform the write.
        var aces = new[] { Deny(Contractors), Allow(Caller) };

        Assert.False(GroupMembershipAce.GrantsMemberWrite(aces, Caller, new[] { Contractors }));
    }

    [Fact]
    public void GroupDeny_BeatsAnIndividualAllow_EvenWhenTheAllowComesFirst()
    {
        // GetAccessRules makes no ordering guarantee, so the answer must not depend on it.
        var aces = new[] { Allow(Caller), Deny(Contractors) };

        Assert.False(GroupMembershipAce.GrantsMemberWrite(aces, Caller, new[] { Contractors }));
    }

    [Fact]
    public void AGroupAllow_IsHonoured()
    {
        // The same widening must work in the granting direction, or the fix would simply deny
        // more and the deny tests above would pass for the wrong reason.
        var aces = new[] { Allow(Contractors) };

        Assert.True(GroupMembershipAce.GrantsMemberWrite(aces, Caller, new[] { Contractors }));
    }

    [Fact]
    public void AnotherPrincipalsDeny_IsNotApplicable()
    {
        // Only the caller's own SID and their groups apply. A deny aimed at somebody else must
        // not refuse them.
        var aces = new[] { Deny(Stranger), Allow(Caller) };

        Assert.True(GroupMembershipAce.GrantsMemberWrite(aces, Caller, new[] { Contractors }));
    }

    [Fact]
    public void SelfMembershipRights_StillDoNotConveyMemberWrite_ThroughAGroup()
    {
        // Widening the trustee set must not widen the RIGHTS test: Self (0x08) shares the
        // member attribute's GUID and still never qualifies.
        var selfOnly = new GroupMembershipAce.Entry(
            Contractors, IsDeny: false, Rights: 0x00000008, ObjectType: GroupMembershipAce.MemberAttribute,
            IsInherited: false, InheritOnly: false);

        Assert.False(GroupMembershipAce.GrantsMemberWrite(new[] { selfOnly }, Caller, new[] { Contractors }));
    }

    [Fact]
    public void NoApplicableTrustee_Denies()
    {
        Assert.False(GroupMembershipAce.GrantsMemberWrite(new[] { Allow(Stranger) }, Caller, null));
        Assert.False(GroupMembershipAce.GrantsMemberWrite(new[] { Allow(Caller) }, "", null));
    }

    [Fact]
    public void AnExplicitAllow_DefeatsAnInheritedDeny()
    {
        // aud-a4b finding 1, and the correction of my own mistake in aud-a4. I wrote that Deny
        // wins regardless of order and that a canonical DACL lists denies first. Both are
        // false: canonical order is EXPLICIT before INHERITED, and evaluation stops once the
        // requested right is satisfied. So an explicit Allow beats an inherited group Deny,
        // and the previous evaluator refused it.
        var aces = new[] { Deny(Contractors, inherited: true), Allow(Caller) };

        Assert.True(GroupMembershipAce.GrantsMemberWrite(aces, Caller, new[] { Contractors }));
    }

    [Fact]
    public void AnInheritedDeny_StillBeatsAnInheritedAllow()
    {
        // Within the same inheritance level, Deny precedes Allow - so widening for the case
        // above must not flip this one.
        var aces = new[] { Allow(Caller, inherited: true), Deny(Contractors, inherited: true) };

        Assert.False(GroupMembershipAce.GrantsMemberWrite(aces, Caller, new[] { Contractors }));
    }

    [Fact]
    public void AnInheritOnlyAllow_GrantsNothingOnThisObject()
    {
        // aud-a4b finding 3. INHERIT_ONLY applies to child objects, never to the object
        // carrying it. The projection dropped the flag, so such an ACE read as an effective
        // grant and the privileged write proceeded where Windows refuses.
        var aces = new[] { Allow(Caller, inheritOnly: true) };

        Assert.False(GroupMembershipAce.GrantsMemberWrite(aces, Caller, new[] { Contractors }));
    }

    [Fact]
    public void AnInheritOnlyDeny_DoesNotRefuseOnThisObject()
    {
        // The same flag honoured in the other direction, so the fix above is not simply
        // "ignore more things and deny".
        var aces = new[] { Deny(Contractors, inheritOnly: true), Allow(Caller) };

        Assert.True(GroupMembershipAce.GrantsMemberWrite(aces, Caller, new[] { Contractors }));
    }

    [Fact]
    public void ADenyOnlyMembership_Denies_ButCannotGrant()
    {
        // aud-a4b finding 6. Windows keeps deny-only memberships in the token so Deny ACEs
        // still apply, while refusing to let them satisfy an Allow. Both halves are pinned,
        // because merging them into the ordinary trustee set would invent access.
        var denyOnly = new[] { Contractors };

        Assert.False(GroupMembershipAce.GrantsMemberWrite(
            new[] { Deny(Contractors), Allow(Caller) }, Caller, callerGroupSids: null, denyOnlySids: denyOnly));

        Assert.False(GroupMembershipAce.GrantsMemberWrite(
            new[] { Allow(Contractors) }, Caller, callerGroupSids: null, denyOnlySids: denyOnly));
    }

    [Fact]
    public void AnObjectScopedCompositeMask_DoesNotConveyMemberWrite_ThroughTheEvaluator()
    {
        // aud-a4b finding 5 reaching the gate, not just the classifier.
        var scopedGenericWrite = new GroupMembershipAce.Entry(
            Caller, IsDeny: false, Rights: 0x00020028, ObjectType: OtherAttribute,
            IsInherited: false, InheritOnly: false);

        Assert.False(GroupMembershipAce.GrantsMemberWrite(new[] { scopedGenericWrite }, Caller, null));
    }
}
