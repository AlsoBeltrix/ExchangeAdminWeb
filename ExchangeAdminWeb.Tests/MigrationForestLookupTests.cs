using System.Text;
using ExchangeAdminWeb.Models;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23r, part 2. The excluded-group check of a move to Exchange Online looked the user up with
/// Get-ADUser and no -Server, so a domain controller of the host's own domain answered, and a user of any other domain of
/// the forest read "no directory user has the user principal name ..." - Undetermined, a valid mailbox blocked whenever
/// excluded groups are configured. Each configured group was resolved in the host's domain the same way.
/// </summary>
/// <remarks>
/// The real service over <see cref="MigrationStubExchange"/>'s Active Directory stand-in, here a forest of two domains:
/// contoso.test, the host's, and fabrikam.test. A user search on the forest's global catalog answers both domains' users,
/// as the catalog's partial copy (no memberOf); a read by DN answers only on the DN's own domain; Get-ADGroup answers, on a
/// domain, the groups that domain holds. No directory, no Exchange. Every check is a move to Exchange Online of a mailbox
/// not yet there, Eligible before the AD step; one that passes the AD step reaches the size step, whose reason alone shows
/// it, as in <see cref="MigrationExcludedGroupTests"/>.
/// </remarks>
public sealed class MigrationForestLookupTests : IDisposable
{
    private const string Host = "contoso.test";
    private const string Second = "fabrikam.test";
    private const string Address = "bob@fabrikam.test";
    private const string Catalog = "gc1.contoso.test:3268";
    private const string SalesDn = "CN=Sales,OU=Groups,DC=contoso,DC=test";
    private const string SecondSalesDn = "CN=Sales,OU=Groups,DC=fabrikam,DC=test";
    private const string StaffDn = "CN=Staff,OU=Groups,DC=fabrikam,DC=test";
    private const string SizeStep = "Could not be determined: the on-premises mailbox and archive sizes could not be read: on-premises Exchange is not configured (OnPremExchange:ServerUri is not set)";
    private const string Incomplete = "Could not be determined: the excluded-group check could not be completed: ";

    private readonly MigrationStubExchange _exchange = new();

    public MigrationForestLookupTests()
    {
        _exchange.ForestDomains.Add(Second);
        _exchange.ExcludeGroups("Sales");
        _exchange.DirectoryGroups["Sales"] = SalesDn;
    }

    public void Dispose() => _exchange.Dispose();

    private Task<MigrationEligibilityResult> Check(string address = Address) =>
        _exchange.Service().CheckMigrationEligibilityAsync(address, MigrationDirection.ToCloud);

    private static string Search(string address) => $"Get-ADUser -Server {Catalog} UserPrincipalName -eq '{address}'";

    // ---- the user: found in the forest, read in its own domain -----------------------------------

    // The finding. A member of the excluded group whose account is in the forest's other domain: found through the global
    // catalog, read again on its own domain - where its whole memberOf is - and found in the group. Before, the host's
    // domain did not know the user: Undetermined, "no directory user has the user principal name ...".
    [Fact]
    public async Task AUserOfAnotherDomain_IsFoundInTheForest_AndReadInItsOwnDomain()
    {
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, StaffDn, SalesDn));

        var result = await Check();

        Assert.Equal(MigrationStatus.Ineligible, result.Status);
        Assert.Equal(["Member of excluded group: Sales"], result.IneligibilityReasons);
        Assert.Equal(
            [Search(Address), $"Get-ADUser -Server {Second} {UserDn(Second, Address)}", $"Get-ADGroup -Server {Host} Sales", $"Get-ADGroup -Server {Second} Sales"],
            _exchange.DirectoryCallList());
    }

    // The same user in no excluded group passes the AD step, where before it could not be checked at all.
    [Fact]
    public async Task AUserOfAnotherDomain_InNoExcludedGroup_PassesTheCheck()
    {
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, StaffDn));

        var result = await Check();

        Assert.Equal([SizeStep], result.IneligibilityReasons);
    }

    // A user the forest does not have is still no answer (fail closed).
    [Fact]
    public async Task AUserFoundNowhereInTheForest_CannotBeChecked()
    {
        var result = await Check();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal([Incomplete + $"no directory user in the forest has the user principal name {Address}"], result.IneligibilityReasons);
    }

    // A DN that names no domain cannot be routed to one.
    [Fact]
    public async Task AUserWhoseDnNamesNoDomain_CannotBeChecked()
    {
        _exchange.DirectoryUsers[Address] = Users(Row(("UserPrincipalName", Address), ("DistinguishedName", "CN=bob,OU=Users"), ("memberOf", new[] { SalesDn })));

        var result = await Check();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal([Incomplete + $"the directory user found for {Address} has no distinguished name that names its domain (CN=bob,OU=Users)"],
            result.IneligibilityReasons);
    }

    // The read in the user's own domain failing is no answer either, and names that domain.
    [Theory]
    [InlineData("thrown")]
    [InlineData("written")]
    public async Task AReadInTheUsersOwnDomainThatFails_CannotBeChecked(string how)
    {
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, SalesDn));
        _exchange.Failures[$"Get-ADUser {UserDn(Second, Address)}"] = how == "thrown"
            ? "the domain controller is not available"
            : Refused("the domain controller is not available");

        var result = await Check();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal([Incomplete + $"reading {UserDn(Second, Address)} in its own domain {Second} failed: the domain controller is not available"],
            result.IneligibilityReasons);
    }

    // ---- the forest itself ------------------------------------------------------------------------

    [Fact]
    public async Task AForestThatReportsNoGlobalCatalog_CannotBeChecked()
    {
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, SalesDn));
        _exchange.ForestCatalogs.Clear();

        var result = await Check();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal([Incomplete + "the forest reported no global catalog"], result.IneligibilityReasons);
        Assert.Empty(_exchange.DirectoryCallList());
    }

    [Theory]
    [InlineData("thrown")]
    [InlineData("written")]
    public async Task AForestThatCannotBeRead_CannotBeChecked(string how)
    {
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, SalesDn));
        _exchange.Failures["Get-ADForest"] = how == "thrown" ? "the forest could not be contacted" : Refused("the forest could not be contacted");

        var result = await Check();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal([Incomplete + "the forest's global catalog could not be found: the forest could not be contacted"], result.IneligibilityReasons);
    }

    // ---- the groups: resolved in the domain that holds them ---------------------------------------

    // A group the host's domain does not hold is found in the domain that does. Before, it was asked of the host's domain
    // only: "could not be resolved", Undetermined for every check.
    [Fact]
    public async Task AGroupOfAnotherDomain_IsResolvedThere()
    {
        _exchange.DirectoryGroups["Sales"] = SecondSalesDn;
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, SecondSalesDn));

        var result = await Check();

        Assert.Equal(["Member of excluded group: Sales"], result.IneligibilityReasons);
    }

    // A name two domains each hold names two groups: which one the setting means cannot be told, so the check cannot be
    // completed - never "the first one found".
    [Fact]
    public async Task ANameTwoDomainsHold_CannotBeChecked()
    {
        _exchange.DirectoryGroups["Sales"] = new[] { SalesDn, SecondSalesDn };
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, SecondSalesDn));

        var result = await Check();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal([Incomplete + $"the excluded group 'Sales' resolved to 2 groups across the forest ({Host}, {Second})"], result.IneligibilityReasons);
    }

    // No domain holds it: each domain's own answer is named.
    [Fact]
    public async Task AGroupNoDomainHolds_NamesEachDomainsAnswer()
    {
        _exchange.DirectoryGroups.Remove("Sales");
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, StaffDn));

        var result = await Check();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal(
            [Incomplete + "the excluded group 'Sales' could not be resolved in any domain of the forest ("
                + $"{Host}: Cannot find an object with identity: 'Sales' under: 'DC=contoso,DC=test'; "
                + $"{Second}: Cannot find an object with identity: 'Sales' under: 'DC=fabrikam,DC=test')"],
            result.IneligibilityReasons);
    }

    // A domain that could not be asked leaves the answer open: the group found in another may not be the only one.
    [Fact]
    public async Task ADomainThatCannotBeAsked_LeavesTheGroupUnresolved()
    {
        _exchange.Failures[$"Get-ADGroup Sales @{Second}"] = "the server is not operational";
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, StaffDn));

        var result = await Check();

        Assert.Equal(MigrationStatus.Undetermined, result.Status);
        Assert.Equal([Incomplete + $"the excluded group 'Sales' could not be resolved in {Second}: the server is not operational"], result.IneligibilityReasons);
    }

    // ---- once per run ---------------------------------------------------------------------------

    // A bulk check is one run: the forest is read once and each group resolved once, for every row. The next run asks
    // again, so a group fixed in the directory is seen.
    [Fact]
    public async Task ABulkRun_ResolvesEachGroupOnce_AndTheNextRunAsksAgain()
    {
        const string Other = "carol@contoso.test";
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, SalesDn));
        _exchange.DirectoryUsers[Other] = Users(DirectoryUser(Other, StaffDn));
        var service = _exchange.Service();

        var first = await Bulk(service, Address, Other);

        Assert.Equal(["Member of excluded group: Sales"], first.EligibilityResults.Single(r => r.EmailAddress == Address).IneligibilityReasons);
        Assert.Equal([SizeStep], first.EligibilityResults.Single(r => r.EmailAddress == Other).IneligibilityReasons);
        Assert.Equal(2, _exchange.CallList().Count(call => call == "Get-ADForest"));
        Assert.Equal([$"Get-ADGroup -Server {Host} Sales", $"Get-ADGroup -Server {Second} Sales"],
            _exchange.DirectoryCallList().Where(call => call.StartsWith("Get-ADGroup", StringComparison.Ordinal)));

        _exchange.DirectoryCalls.Clear();
        _exchange.Calls.Clear();
        await Bulk(service, Address, Other);

        Assert.Equal(2, _exchange.CallList().Count(call => call == "Get-ADForest"));
        Assert.Equal(2, _exchange.DirectoryCallList().Count(call => call.StartsWith("Get-ADGroup", StringComparison.Ordinal)));
    }

    // What could not be read is kept for the run too: every row is told the same, and the forest is not asked again.
    [Fact]
    public async Task AForestThatCannotBeReadInARun_IsTheSameAnswerForEveryRow()
    {
        const string Other = "carol@contoso.test";
        _exchange.DirectoryUsers[Address] = Users(DirectoryUserIn(Second, Address, SalesDn));
        _exchange.DirectoryUsers[Other] = Users(DirectoryUser(Other, StaffDn));
        _exchange.Failures["Get-ADForest"] = "the forest could not be contacted";

        var bulk = await Bulk(_exchange.Service(), Address, Other);

        Assert.All(bulk.EligibilityResults, row =>
        {
            Assert.Equal(MigrationStatus.Undetermined, row.Status);
            Assert.Equal([Incomplete + "the forest's global catalog could not be found: the forest could not be contacted"], row.IneligibilityReasons);
        });
        Assert.Equal(1, _exchange.CallList().Count(call => call == "Get-ADForest"));
    }

    private static async Task<MigrationBatchResult> Bulk(ExchangeAdminWeb.Services.MigrationService service, params string[] addresses)
    {
        using var csv = new MemoryStream(Encoding.UTF8.GetBytes("EmailAddress\n" + string.Join("\n", addresses) + "\n"));
        return await service.CheckBulkMigrationEligibilityAsync(csv, MigrationDirection.ToCloud);
    }
}
