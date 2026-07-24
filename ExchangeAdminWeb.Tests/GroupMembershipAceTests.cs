using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the security-critical ACE classifier (plan docs/SelfServiceGroupManagement-Plan.md §6.3,
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
    public void GenericAll_conveys_member_write_regardless_of_object_type()
    {
        Assert.True(GroupMembershipAce.ConveysMemberWrite(GenericAll, Guid.Empty));
        Assert.True(GroupMembershipAce.ConveysMemberWrite(GenericAll, OtherAttribute));
    }

    [Fact]
    public void GenericWrite_conveys_member_write_regardless_of_object_type()
    {
        Assert.True(GroupMembershipAce.ConveysMemberWrite(GenericWrite, Guid.Empty));
        Assert.True(GroupMembershipAce.ConveysMemberWrite(GenericWrite, OtherAttribute));
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
}
