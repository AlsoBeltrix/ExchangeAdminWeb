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
///
/// SERIALISED, and that is load-bearing. Every test here drives a real directory through a
/// service that holds a 30-second runspace lock and probes the forest on first use. Run in
/// parallel with the other live-AD classes they contend for that lock, the probe intermittently
/// fails, and the suite reported a genuine-looking "search is not spanning the forest" failure
/// roughly one run in three. Chasing that flake DID find a real product bug -- a failed forest
/// probe was being cached permanently -- but once fixed, the residual nondeterminism is the
/// environment, not the code. A test that fails one run in three trains people to re-run CI,
/// which is worse than having no test.
/// </remarks>
[Collection(LiveDirectoryCollection.Name)]
public class AdDirectoryForestSearchLiveTests
{
    private static ADDirectorySearchService CreateService() =>
        new(Substitute.For<ILogger<ADDirectorySearchService>>());

    /// <summary>
    /// Searches with retries, because <see cref="ADDirectorySearchService.Search"/> is fail-soft:
    /// a throttle timeout on the shared runspace lock returns an empty list that looks exactly
    /// like "no matches". Without this the test is flaky under a parallel suite.
    /// </summary>
    private static List<ADSearchResult> SearchWithRetry(ADDirectorySearchService svc, string term)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var hits = svc.Search(term, "Group", 50);
            if (hits.Count > 0)
                return hits;

            Thread.Sleep(1500);
        }

        return svc.Search(term, "Group", 50);
    }

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

        // "Enterprise Admins", NOT a generic term. The first version used "admins", which matches
        // 50 groups in this forest - exactly the service's result cap. Search sorts by
        // DisplayName and then truncates, so whether the 3 WINROOT matches survive the cut is a
        // race, and the test alternated between pass, skip and fail across full-suite runs. A
        // flaky test is worse than a failing one: it teaches people to re-run CI.
        //
        // This term returns 8 (7 local + 1 foreign), comfortably inside the cap, so the outcome
        // depends on the code under test rather than on sort order.
        var results = SearchWithRetry(svc, "Enterprise Admins");
        Assert.SkipWhen(results.Count == 0, "Directory returned no groups for the probe term after retries.");

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

        var results = SearchWithRetry(svc, "Enterprise Admins");
        Assert.SkipWhen(results.Count == 0, "Directory returned nothing for 'Enterprise Admins' after retries.");

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

        // Same reasoning as the first test: a term that saturates the 50-result cap makes the
        // outcome depend on sort order rather than on the code.
        var results = SearchWithRetry(svc, "Enterprise Admins");
        Assert.SkipWhen(results.Count == 0, "Directory returned no groups for the probe term after retries.");

        Assert.All(results, r => Assert.False(string.IsNullOrWhiteSpace(r.ObjectSid)));
    }
}
