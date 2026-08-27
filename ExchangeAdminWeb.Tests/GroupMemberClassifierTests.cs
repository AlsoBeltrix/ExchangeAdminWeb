using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the member-kind classifier (plan docs/SelfServiceGroupsMemberListingAndPicker-Plan.md).
/// The security-relevant behavior pinned here: only a plain <c>user</c> is removable through the
/// self-service path (first-cut user-only write scope, `docs/SelfServiceGroupManagement-Plan.md`
/// section 6.5). A <c>computer</c> is an AD subclass of user but must NOT be removable; groups and
/// unknown classes are listed read-only. Classification is case-insensitive and null/blank-safe.
/// </summary>
public class GroupMemberClassifierTests
{
    [Theory]
    [InlineData("user", "User")]
    [InlineData("User", "User")]
    [InlineData("  USER  ", "User")]
    [InlineData("computer", "Computer")]
    [InlineData("group", "Group")]
    [InlineData("msDS-GroupManagedServiceAccount", "Other")]
    [InlineData("contact", "Other")]
    [InlineData("", "Other")]
    [InlineData(null, "Other")]
    public void KindOf_maps_objectClass(string? objectClass, string expected)
    {
        Assert.Equal(expected, GroupMemberClassifier.KindOf(objectClass));
    }

    [Theory]
    [InlineData("user")]
    [InlineData("User")]
    [InlineData("  user ")]
    public void User_is_removable(string objectClass)
    {
        Assert.True(GroupMemberClassifier.IsRemovable(objectClass));
    }

    [Theory]
    [InlineData("group")]
    [InlineData("Group")]
    [InlineData(" group ")]
    public void Group_is_removable(string objectClass)
    {
        // Nesting plan S4 (D2): an owner may remove a nested group, behind the page's inline
        // warning-and-confirm step. Group ADDS stay excluded (D1).
        Assert.True(GroupMemberClassifier.IsRemovable(objectClass));
    }

    [Theory]
    [InlineData("computer")]
    [InlineData("contact")]
    [InlineData("")]
    [InlineData(null)]
    public void Other_classes_are_not_removable(string? objectClass)
    {
        Assert.False(GroupMemberClassifier.IsRemovable(objectClass));
    }
}
