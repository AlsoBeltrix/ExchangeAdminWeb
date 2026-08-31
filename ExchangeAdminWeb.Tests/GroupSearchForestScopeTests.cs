using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Group Management's group search must span the forest, not the app credential's home
/// domain (owner, 2026-08-31: a bare "Domain Admins" row is ambiguous and the search found
/// only one domain's groups). The live Get-ADGroup call cannot run in a unit test, so the
/// pure projection is tested directly and the global-catalog wiring is pinned with source
/// tripwires, the established split (SelfServiceGroupTargetGateTests precedent, since
/// removed; GroupMemberListingTests is the living example).
/// </summary>
public sealed class GroupSearchForestScopeTests
{
    // ----- DomainLabel: pure display derivation, same convention as the pickers -----

    [Theory]
    [InlineData("ad.analog.com", "AD")]
    [InlineData("winroot.analog.com", "WINROOT")]
    [InlineData("single", "SINGLE")]
    public void DomainLabel_IsTheFirstDnsLabel_Uppercased(string dns, string expected)
    {
        Assert.Equal(expected, new GroupInfo { Domain = dns }.DomainLabel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void DomainLabel_IsEmpty_WhenTheDomainIsUnknown(string? dns)
    {
        // Empty, never a guess - the page shows nothing rather than a wrong domain.
        Assert.Equal("", new GroupInfo { Domain = dns }.DomainLabel);
    }

    // ----- Tripwires: the search queries the catalog and carries the domain -----

    [Fact]
    public void SearchGroups_QueriesTheGlobalCatalog_WithTheColonGuard()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "GroupManagementService.cs"));
        var start = text.IndexOf("public async Task<List<GroupInfo>> SearchGroupsAsync(", StringComparison.Ordinal);
        Assert.True(start >= 0, "SearchGroupsAsync signature not found - tripwire is stale.");
        var end = text.IndexOf("private string? ResolveSearchGlobalCatalog(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound SearchGroupsAsync - update the tripwire.");
        var body = text[start..end];

        var iResolve = body.IndexOf("ResolveSearchGlobalCatalog(ps, credential)", StringComparison.Ordinal);
        var iCommand = body.IndexOf("AddCommand(\"Get-ADGroup\")", StringComparison.Ordinal);
        var iGuard = body.IndexOf("catalog is not null && !catalog.StartsWith(':')", StringComparison.Ordinal);
        var iServer = body.IndexOf("AddParameter(\"Server\", catalog)", StringComparison.Ordinal);

        Assert.True(iResolve >= 0, "The search no longer resolves the global catalog.");
        Assert.True(iCommand > iResolve, "The catalog must be resolved before the group query is built.");
        // ":3268" (a failed host lookup) is a server string Get-ADGroup accepts and then quietly
        // serves from the local domain - the exact bug this feature fixes, reintroduced silently.
        Assert.True(iGuard >= 0, "The ':3268' guard is gone.");
        Assert.True(iServer > iGuard, "-Server must be applied only behind the guard.");

        // Failure handling: a success is cached, a failure is not - losing the asymmetry
        // silently pins search to local-domain-only after one transient Get-ADForest error.
        var resolver = text[end..];
        Assert.Contains("_searchGlobalCatalog = $\"{gcHost}:3268\";", resolver, StringComparison.Ordinal);
        Assert.DoesNotContain("_searchGlobalCatalog = null", resolver, StringComparison.Ordinal);
    }

    [Fact]
    public void SearchResults_CarryTheDomain_AndThePageShowsIt()
    {
        var svc = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "GroupManagementService.cs"));
        Assert.Contains("Domain = ADDirectorySearchService.DnsDomainFromDn(dn)", svc, StringComparison.Ordinal);

        var page = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Components", "Pages", "GroupManagement.razor"));
        Assert.Contains("<th>Name</th><th>Domain</th>", page, StringComparison.Ordinal);
        Assert.Contains("@g.DomainLabel", page, StringComparison.Ordinal);
    }
}
