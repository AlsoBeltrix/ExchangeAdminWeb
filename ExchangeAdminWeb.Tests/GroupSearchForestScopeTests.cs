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

    // ----- fsr-1: everything downstream of the forest search routes by the picked DN -----

    [Fact]
    public void ResolveGroupForWrite_TakesTheDnFastPath_ExactOrNothing()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "GroupManagementService.cs"));
        var start = text.IndexOf("internal virtual ResolvedMember ResolveGroupForWrite(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ResolveGroupForWrite signature not found - tripwire is stale.");
        var end = text.IndexOf("internal static string? CombineNotes(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound ResolveGroupForWrite - update the tripwire.");
        var body = text[start..end];

        var iDnServer = body.IndexOf("var dnServer = ServerFromDn(groupIdentity);", StringComparison.Ordinal);
        var iRoute = body.IndexOf("AddParameter(\"Server\", dnServer)", StringComparison.Ordinal);
        var iMiss = body.IndexOf("AD group not found by its distinguished name", StringComparison.Ordinal);
        var iLoop = body.IndexOf("foreach (var candidate in candidates", StringComparison.Ordinal);

        Assert.True(iDnServer >= 0, "The DN fast-path is gone from ResolveGroupForWrite.");
        Assert.True(iRoute > iDnServer, "The DN resolve must route to the DN's owning domain.");
        // Exact-or-nothing: a DN miss returns Failed INSIDE the branch - falling through to
        // the local-domain name loop would bring back the namesake swap (fsr-1's worst case).
        Assert.True(iMiss > iRoute && iMiss < iLoop, "A DN miss must fail before the name loop.");
        Assert.True(iLoop > iMiss, "The name loop must sit after the whole DN branch.");
    }

    [Fact]
    public void ResolveGroupForWrite_NameFallback_SearchesTheForest_AndRereadsInTheOwningDomain()
    {
        // 2026-09-02: the fallback used when the page holds no DN (sam/name/mail only) queried
        // the credential's HOME DOMAIN, so a foreign-domain target could not be resolved at all -
        // its gate and its write both failed with "The group could not be resolved right now"
        // (recorded in .agents/state.md as a pre-existing gap).
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "GroupManagementService.cs"));
        var start = text.IndexOf("internal virtual ResolvedMember ResolveGroupForWrite(", StringComparison.Ordinal);
        Assert.True(start >= 0, "ResolveGroupForWrite signature not found - tripwire is stale.");
        var end = text.IndexOf("internal static string? CombineNotes(", start, StringComparison.Ordinal);
        Assert.True(end > start, "Could not bound ResolveGroupForWrite - update the tripwire.");
        var body = text[start..end];

        var iCatalog = body.IndexOf("var catalog = ResolveSearchGlobalCatalog(ps, credential);", StringComparison.Ordinal);
        var iLoop = body.IndexOf("foreach (var candidate in candidates", StringComparison.Ordinal);
        var iGuard = body.IndexOf("catalog is not null && !catalog.StartsWith(':')", StringComparison.Ordinal);
        var iServer = body.IndexOf("AddParameter(\"Server\", catalog)", StringComparison.Ordinal);
        var iExactlyOne = body.IndexOf("if (groups.Count != 1)", StringComparison.Ordinal);
        var iReread = body.IndexOf("var matchServer = ServerFromDn(dn);", StringComparison.Ordinal);
        var iRerouted = body.IndexOf("AddParameter(\"Server\", matchServer)", StringComparison.Ordinal);

        Assert.True(iCatalog >= 0, "The name fallback no longer searches the forest.");
        Assert.True(iLoop > iCatalog, "The catalog must be resolved before the candidate loop.");
        // Same ':3268' guard as the search: a failed host lookup yields a server string
        // Get-ADGroup accepts and then quietly serves from the local domain.
        Assert.True(iGuard > iLoop, "The ':3268' guard is missing from the candidate query.");
        Assert.True(iServer > iGuard, "-Server must be applied only behind the guard.");
        // A name that exists in two domains now matches twice and is REFUSED - the namesake swap
        // fails closed instead of resolving to whichever domain answered.
        Assert.True(iExactlyOne > iServer, "The exactly-one-match rule must gate the catalog result.");
        // A catalog row is a partial attribute set and this snapshot feeds the protected-target
        // gate, so the match is re-read where it lives - a missing identifier is a protection
        // entry that cannot match.
        Assert.True(iReread > iExactlyOne, "A catalog match must be re-read in its own domain.");
        Assert.True(iRerouted > iReread, "The re-read must be routed to the match's own domain.");
        Assert.Contains("could not be read in its own domain", body, StringComparison.Ordinal);
    }

    [Fact]
    public void MemberRead_And_Writes_RouteByTheGroupsOwningDomain()
    {
        var text = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(
            "Services", "GroupManagementService.cs"));

        // GetMembersAsync: a DN identity is used as-is (never re-resolved by local name)...
        Assert.Contains("var resolvedDn = ServerFromDn(groupIdentity) is not null", text, StringComparison.Ordinal);
        // ...and the group read itself is routed.
        Assert.Contains("var memberReadServer = ServerFromDn(resolvedDn);", text, StringComparison.Ordinal);

        // Both write cmdlets and the cycle probe act on the group object in ITS domain.
        Assert.Contains("var addServer = ServerFromDn(resolvedGroupDn);", text, StringComparison.Ordinal);
        Assert.Contains("var removeServer = ServerFromDn(resolvedGroupDn);", text, StringComparison.Ordinal);
        Assert.Contains("var cycleServer = ServerFromDn(resolvedGroupDn);", text, StringComparison.Ordinal);
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
