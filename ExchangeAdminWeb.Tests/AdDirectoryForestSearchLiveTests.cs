using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Live-directory tests for bug B1 (docs/AdminUIRedesign-Plan.md): group search must span the
/// forest, not just the local domain.
/// </summary>
/// <remarks>
/// These need a real multi-domain forest, so they SKIP LOUDLY where one is absent -- never a
/// silent early return, which is indistinguishable from a pass (this repo has been bitten by that
/// twice; see ADDirectoryLiveTests and review finding ppv-4).
///
/// What only a live run can prove: that <c>-Server &lt;gc&gt;:3268</c> actually returns foreign-domain
/// groups. A unit test cannot -- and the first implementation of this fix passed every unit test
/// while resolving the catalog host to an empty string, producing ":3268", which Get-ADGroup
/// accepts and then quietly serves from the local domain.
/// </remarks>
public class AdDirectoryForestSearchLiveTests
{
    private static ADDirectorySearchService CreateService() =>
        new(Substitute.For<ILogger<ADDirectorySearchService>>());

    /// <summary>
    /// How many domains this forest has, asked independently of the code under test.
    /// </summary>
    /// <remarks>
    /// This is what separates "cannot be tested here" from "the feature is broken". Without it a
    /// reverted fix looks identical to a single-domain lab, and the suite reports skips instead of
    /// failures. Returns 0 when the forest cannot be read, which skips rather than fails - an
    /// unreadable forest is genuinely not evidence either way.
    /// </remarks>
    private static int CountForestDomains()
    {
        try
        {
            return System.DirectoryServices.ActiveDirectory.Forest.GetCurrentForest().Domains.Count;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>The DNS name of the domain this host is joined to, or null.</summary>
    private static string? LocalDomain()
    {
        try
        {
            return System.DirectoryServices.ActiveDirectory.Domain.GetCurrentDomain().Name;
        }
        catch
        {
            return null;
        }
    }

    [Fact]
    public void GroupSearch_ReturnsGroupsFromMoreThanOneDomain()
    {
        var svc = CreateService();
        Assert.SkipUnless(svc.IsAvailable, "No Active Directory available on this host.");

        // "admins" is deliberately generic: it matches in both domains of this forest. A term that
        // only matched locally would pass even with the fix reverted.
        var results = svc.Search("admins", "Group", 50);
        Assert.SkipWhen(results.Count == 0, "Directory returned no groups for the probe term.");

        var domains = results
            .Select(r => r.DnsDomain)
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        Assert.SkipWhen(domains.Count == 0, "No group carried a DNS domain; cannot judge forest scope.");

        // Skip ONLY where the forest genuinely has one domain. Seeing one domain in a
        // multi-domain forest is the bug, not an untestable environment - a non-vacuity probe
        // caught an earlier version of this test SKIPPING when the fix was reverted, which is a
        // non-result dressed as a pass (cf. review finding ppv-4).
        var forestDomains = CountForestDomains();
        Assert.SkipWhen(forestDomains <= 1,
            $"Forest has {forestDomains} domain(s); cross-domain scope is not observable here.");

        Assert.True(domains.Count > 1,
            $"Forest has {forestDomains} domains but group search returned only: "
            + $"{string.Join(", ", domains)}. Search is not spanning the forest.");
    }

    [Fact]
    public void GroupSearch_FindsAForeignDomainGroupByName()
    {
        // The concrete reported case: WINROOT\Enterprise Admins was grantable by migration but
        // could not be chosen in the picker.
        var svc = CreateService();
        Assert.SkipUnless(svc.IsAvailable, "No Active Directory available on this host.");

        var results = svc.Search("Enterprise Admins", "Group", 25);
        Assert.SkipWhen(results.Count == 0, "Directory returned nothing for 'Enterprise Admins'.");

        // Skip only for a single-domain forest; otherwise a missing foreign match IS the defect.
        var forestDomains = CountForestDomains();
        Assert.SkipWhen(forestDomains <= 1,
            $"Forest has {forestDomains} domain(s); a foreign-domain group cannot exist here.");

        var local = LocalDomain();
        var foreign = results.FirstOrDefault(r =>
            r.DnsDomain is not null
            && !string.Equals(r.DnsDomain, local, StringComparison.OrdinalIgnoreCase));

        Assert.NotNull(foreign);
        Assert.False(string.IsNullOrWhiteSpace(foreign!.ObjectSid),
            "A pickable group must carry a SID -- that is what gets stored.");
    }

    [Fact]
    public void GroupSearch_EveryResultCarriesASid()
    {
        // The picker refuses anything without a SID, so a result set lacking them renders an
        // unusable list. Cheap to assert and it catches a dropped property.
        var svc = CreateService();
        Assert.SkipUnless(svc.IsAvailable, "No Active Directory available on this host.");

        var results = svc.Search("admins", "Group", 25);
        Assert.SkipWhen(results.Count == 0, "Directory returned no groups for the probe term.");

        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.ObjectSid)));
    }
}
