using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the pure affected-user-notification decision on <see cref="MembershipChangeResult"/> (plan
/// docs/SelfServiceGroupManagement-Plan.md AC10; Constitution "Notifications"). The affected member is
/// emailed ONLY when the change succeeded, actually altered membership, the group is a SECURITY group,
/// and a real address is known - so a failed/denied attempt, an idempotent no-op, a distribution group,
/// or a missing address never triggers an end-user email. This is the AD-free gate the page trusts, so
/// it is unit-tested without AD/email.
/// </summary>
public class MembershipChangeResultTests
{
    private static MembershipChangeResult Make(
        bool success, bool changed, bool security, string? email) =>
        new(success ? PermissionResult.Ok() : PermissionResult.Fail("x"), email, "Jane Doe", security, changed);

    [Fact]
    public void NotifyAffectedUser_true_when_success_changed_security_and_addressed()
    {
        Assert.True(Make(success: true, changed: true, security: true, email: "jane@contoso.com").NotifyAffectedUser);
    }

    [Fact]
    public void NotifyAffectedUser_false_when_change_failed()
    {
        Assert.False(Make(success: false, changed: true, security: true, email: "jane@contoso.com").NotifyAffectedUser);
    }

    [Fact]
    public void NotifyAffectedUser_false_when_no_membership_change()
    {
        // Idempotent already-satisfied no-op: nothing changed, so the member is not emailed.
        Assert.False(Make(success: true, changed: false, security: true, email: "jane@contoso.com").NotifyAffectedUser);
    }

    [Fact]
    public void NotifyAffectedUser_false_for_distribution_group()
    {
        // Affected-user notify is scoped to access-bearing (security) groups.
        Assert.False(Make(success: true, changed: true, security: false, email: "jane@contoso.com").NotifyAffectedUser);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NotifyAffectedUser_false_without_an_address(string? email)
    {
        Assert.False(Make(success: true, changed: true, security: true, email: email).NotifyAffectedUser);
    }

    [Fact]
    public void From_wraps_a_bare_result_with_no_notify_metadata()
    {
        var r = MembershipChangeResult.From(PermissionResult.Fail("nope"));
        Assert.False(r.Result.Success);
        Assert.Null(r.AffectedMemberEmail);
        Assert.False(r.IsSecurityGroup);
        Assert.False(r.MembershipChanged);
        Assert.False(r.NotifyAffectedUser);
    }
}
