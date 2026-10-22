using ExchangeAdminWeb.Models;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23j. The excluded-group check of a move to Exchange Online ran in Active Directory and, when it could
/// not be completed - the module, the user lookup, a group - only added a warning, so the mailbox stayed Eligible and a batch
/// could be created for a member of an excluded group. When no directory user had the address as its user principal name
/// it returned without checking at all: Eligible again. And the match was a substring of each memberOf DN, so a group whose
/// DN merely contained the configured text matched.
/// </summary>
/// <remarks>
/// The real service over <see cref="MigrationStubExchange"/> and its Active Directory stand-in (stub Import-Module,
/// Get-ADUser and Get-ADGroup over in-memory users and groups). No directory, no Exchange. Every check is a move to Exchange
/// Online of a mailbox not yet there, which is Eligible before the AD step. One that passes the AD step goes on to the size
/// step, which runs only for an Eligible verdict and which this harness has no on-premises server for - so its reason,
/// alone, is the sign that the AD step let the mailbox through.
/// </remarks>
public sealed class MigrationExcludedGroupTests : IDisposable
{
    private const string Address = "alice@contoso.test";
    private const string SalesDn = "CN=Sales,OU=Groups,DC=contoso,DC=test";
    private const string StaffDn = "CN=Staff,OU=Groups,DC=contoso,DC=test";
    private const string SizeStep = "Could not verify on-prem mailbox size (on-prem connection unavailable)";
    private const string Incomplete = "Could not be determined: the excluded-group check could not be completed: ";

    private readonly MigrationStubExchange _exchange = new();

    public MigrationExcludedGroupTests()
    {
        _exchange.ExcludeGroups("Sales");
        _exchange.DirectoryGroups["Sales"] = SalesDn;
    }

    public void Dispose() => _exchange.Dispose();

    private Task<MigrationEligibilityResult> Check() =>
        _exchange.Service().CheckMigrationEligibilityAsync(Address, MigrationDirection.ToCloud);

    // ---- the finding: a check that cannot be completed is no answer -------------------------------

    // Each way the check can fail to complete. Before, every one of these left the mailbox Eligible - the first four with
    // a "Warning: Could not verify AD group membership" reason, the last three with no reason at all - and the size step
    // and batch creation went ahead.
    [Theory]
    [InlineData("the module cannot be loaded")]
    [InlineData("Get-ADUser throws")]
    [InlineData("Get-ADUser writes an error")]
    [InlineData("the group does not resolve")]
    [InlineData("no directory user")]
    [InlineData("two directory users")]
    [InlineData("no memberOf")]
    public async Task AnExcludedGroupCheckThatCannotBeCompleted_IsUndetermined_NeverEligible(string how)
    {
        var expected = Arrange(how);

        var result = await Check();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal([Incomplete + expected], result.IneligibilityReasons);
    }

    private string Arrange(string how)
    {
        switch (how)
        {
            case "the module cannot be loaded":
                _exchange.DirectoryUsers[Address] = Users(DirectoryUser(Address, StaffDn));
                _exchange.Failures["Import-Module"] = "the ActiveDirectory module is not installed on this host";
                return "the ActiveDirectory module could not be loaded: the ActiveDirectory module is not installed on this host";
            case "Get-ADUser throws":
                _exchange.Failures["Get-ADUser"] = "the server is not operational";
                return "Get-ADUser failed: the server is not operational";
            case "Get-ADUser writes an error":
                _exchange.Failures["Get-ADUser"] = Refused("the directory refused the query");
                return "Get-ADUser failed: the directory refused the query";
            case "the group does not resolve":
                _exchange.DirectoryUsers[Address] = Users(DirectoryUser(Address, SalesDn));
                _exchange.DirectoryGroups.Remove("Sales");
                return "the excluded group 'Sales' could not be resolved: Cannot find an object with identity: 'Sales' under: 'DC=contoso,DC=test'";
            case "no directory user":
                return $"no directory user has the user principal name {Address}";
            case "two directory users":
                _exchange.DirectoryUsers[Address] = Users(DirectoryUser(Address, StaffDn), DirectoryUser(Address, StaffDn));
                return $"2 directory users have the user principal name {Address}";
            case "no memberOf":
                _exchange.DirectoryUsers[Address] = Users(Row(("UserPrincipalName", Address)));
                return $"the directory reported no memberOf for {Address}";
            default:
                throw new ArgumentOutOfRangeException(nameof(how), how, null);
        }
    }

    // An answer already reached stands: a mailbox already in Exchange Online is ineligible whatever the AD step would
    // have said, and the check that could not be completed is listed beside it.
    [Fact]
    public async Task AnIneligibleAnswer_WhoseGroupCheckCannotBeCompleted_StaysIneligible_WithBothReasons()
    {
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
        _exchange.Failures["Get-ADUser"] = Refused("the directory refused the query");

        var result = await Check();

        Assert.Equal(MigrationStatus.Ineligible, result.Status);
        Assert.Equal(["Already a cloud mailbox", Incomplete + "Get-ADUser failed: the directory refused the query"], result.IneligibilityReasons);
    }

    // ---- the match: the resolved group's DN, exactly ---------------------------------------------

    [Fact]
    public async Task AMemberOfTheResolvedGroup_IsIneligible()
    {
        _exchange.DirectoryUsers[Address] = Users(DirectoryUser(Address, StaffDn, SalesDn));

        var result = await Check();

        Assert.Equal(MigrationStatus.Ineligible, result.Status);
        Assert.Equal(["Member of excluded group: Sales"], result.IneligibilityReasons);
    }

    // AD compares distinguished names without regard to case, so a memberOf value that differs only in case is the group.
    [Fact]
    public async Task AMemberOfValueThatDiffersOnlyInCase_IsTheGroup()
    {
        _exchange.DirectoryUsers[Address] = Users(DirectoryUser(Address, "cn=sales,ou=groups,dc=contoso,dc=test"));

        var result = await Check();

        Assert.Equal(["Member of excluded group: Sales"], result.IneligibilityReasons);
    }

    // The finding's second half. Each of these contains the configured name, or the group's whole DN, as a SUBSTRING -
    // a group of a similar name, the name in an OU path, an object inside the group's own container - and none of them is
    // the group. Before, each was "Member of excluded group: Sales". Now the mailbox passes the AD step, so the size step
    // runs and its reason is the only one.
    [Theory]
    [InlineData("CN=Sales Team,OU=Groups,DC=contoso,DC=test")]
    [InlineData("CN=Other,OU=Sales,DC=contoso,DC=test")]
    [InlineData("CN=Archive,CN=Sales,OU=Groups,DC=contoso,DC=test")]
    public async Task AGroupThatOnlyContainsTheNameOrTheDn_IsNotTheGroup(string memberOf)
    {
        _exchange.DirectoryUsers[Address] = Users(DirectoryUser(Address, memberOf));

        var result = await Check();

        Assert.Equal([SizeStep], result.IneligibilityReasons);
    }

    // The control for the theory above: a user in no excluded group passes the AD step.
    [Fact]
    public async Task AUserInNoExcludedGroup_PassesTheCheck()
    {
        _exchange.DirectoryUsers[Address] = Users(DirectoryUser(Address, StaffDn));

        var result = await Check();

        Assert.Equal([SizeStep], result.IneligibilityReasons);
    }

    // The setting is comma-separated, so a DN typed into it is cut at its commas into fragments no group resolves to. It
    // now reads "could not be resolved" and blocks; the substring matched a fragment such as "OU=Groups" against every
    // group in that OU, and "DC=test" against every group in the domain.
    [Fact]
    public async Task ADnTypedIntoTheCommaSeparatedSetting_CannotBeResolved_RatherThanMatchingEveryGroupNearIt()
    {
        _exchange.ExcludeGroups(SalesDn);
        _exchange.DirectoryUsers[Address] = Users(DirectoryUser(Address, StaffDn));

        var result = await Check();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal(
            [Incomplete + "the excluded group 'CN=Sales' could not be resolved: Cannot find an object with identity: 'CN=Sales' under: 'DC=contoso,DC=test'"],
            result.IneligibilityReasons);
    }
}
