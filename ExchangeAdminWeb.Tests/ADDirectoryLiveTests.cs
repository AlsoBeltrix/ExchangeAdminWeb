using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Exercises <see cref="ADDirectorySearchService"/> against the REAL directory when one is
/// reachable, and reports an explicit SKIP when it is not.
///
/// Every other AD test in this repo asserts on pure functions, because CI (windows-latest) has no
/// RSAT. That leaves one thing unproven: whether the PowerShell property names the mapping code
/// reads actually match what the cmdlets return. A typo there compiles, passes every unit test,
/// and produces empty strings at runtime.
///
/// Two rules keep this suite honest, both learned the hard way (see .agents/state.md):
///
/// 1. Skip LOUDLY, never by an early <c>return</c>. A returned test reports PASSED, so on a host
///    without AD these would be indistinguishable from real coverage - green tests that asserted
///    nothing. Review finding ppv-4; the sibling fixture-missing case had already been caught the
///    same way one commit earlier.
/// 2. Never let a conditional live test be the only coverage of a rule. On CI it does not run at
///    all. These complement the pure tests; they never replace them.
/// </summary>
/// <remarks>
/// OPT-IN (psl-10 (5)). Every test here reads the live directory wherever the ActiveDirectory module loads, so the
/// class carries <c>[Trait("Category", "LiveDirectory")]</c>, and the test project's run settings
/// (<c>ExchangeAdminWeb.Tests.runsettings</c>) filter that trait out of every run: the default
/// <c>dotnet test ExchangeAdminWeb.slnx</c>, the mandated gate and CI's run never reach it. Run it by hand, on a
/// domain-joined host with RSAT, from the repository root, with
/// <code>dotnet test ExchangeAdminWeb.slnx -s ExchangeAdminWeb.Tests/LiveDirectory.runsettings</code>
/// which runs this class and <see cref="AdDirectoryForestSearchLiveTests"/>, and nothing else: a settings file named on
/// the command line replaces the project's. A <c>--filter</c> cannot do it, because the run joins it to the project's
/// filter with AND, which then matches no test.
/// </remarks>
[Trait("Category", "LiveDirectory")]
[Collection(LiveDirectoryCollection.Name)]
public class ADDirectoryLiveTests
{
    private static ADDirectorySearchService CreateService()
        => new(NullLogger<ADDirectorySearchService>.Instance);

    private static bool Reachable(ADDirectorySearchService svc) => svc.IsAvailable;

    /// <summary>
    /// Finds one real OU without hardcoding an environment-specific name. Tries a few common
    /// name fragments; returns null when the directory has none matching, so callers can
    /// distinguish "nothing to test against" from "the code is broken".
    /// </summary>
    /// <remarks>
    /// Do NOT search for "OU=" hoping to match every DN: AD does not substring-match
    /// distinguishedName that way and it silently returns zero rows, which made an earlier
    /// version of these tests pass by early-return even with the mapping code deliberately broken.
    /// </remarks>
    private static ADSearchResult? FindAnyOu(ADDirectorySearchService svc)
    {
        foreach (var fragment in new[] { "amer", "user", "admin", "group", "comp", "serv" })
        {
            var hits = svc.Search(fragment, "OU", maxResults: 5);
            if (hits.Count > 0)
                return hits[0];
        }
        return null;
    }

    [Fact]
    public void Search_Ou_MapsNameAndDistinguishedName()
    {
        // Guards the property names in the OU branch: Get-ADOrganizationalUnit returns "Name",
        // not "DisplayName", and reading the wrong one yields a blank suggestion label.
        var svc = CreateService();
        Assert.SkipWhen(!Reachable(svc), "Active Directory is not reachable from this host.");

        var ou = FindAnyOu(svc);
        Assert.SkipWhen(ou is null, "No OU in this directory matched any probe fragment.");

        Assert.Equal("OU", ou!.ObjectType);
        Assert.False(string.IsNullOrWhiteSpace(ou.DistinguishedName),
            "OU suggestions must carry a DN - it is the value that gets stored and matched.");
        Assert.False(string.IsNullOrWhiteSpace(ou.DisplayName),
            "OU suggestions must carry a name, or the dropdown row renders blank. "
            + "Get-ADOrganizationalUnit returns 'Name', not 'DisplayName'.");
        Assert.StartsWith("OU=", ou.DistinguishedName, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Search_Any_ExcludesOrganizationalUnits()
    {
        // "Any" is the people-and-groups picker used elsewhere in the app. Leaking containers into
        // it would be noise on every other page that uses this component.
        var svc = CreateService();
        Assert.SkipWhen(!Reachable(svc), "Active Directory is not reachable from this host.");

        var results = svc.Search("adm", "Any", maxResults: 25);

        Assert.DoesNotContain(results, r => r.ObjectType == "OU");
    }

    [Fact]
    public void ValidateExists_RealOu_IsFound()
    {
        // The end-to-end shape of the OU validation path: filter -> cmdlet -> mapped result.
        var svc = CreateService();
        Assert.SkipWhen(!Reachable(svc), "Active Directory is not reachable from this host.");

        // Discover an OU rather than hardcoding one, so this is not tied to one environment.
        var ou = FindAnyOu(svc);
        Assert.SkipWhen(ou is null, "No OU in this directory matched any probe fragment.");

        var result = svc.ValidateExists(ou!.DistinguishedName, "OU");

        Assert.Equal(DirectoryLookupOutcome.Found, result.Outcome);
        Assert.Equal(ou.DistinguishedName, result.Match!.DistinguishedName);
    }

    [Fact]
    public void ValidateExists_NonexistentOu_IsNotFound_NotUnavailable()
    {
        // Proves the live path can produce an affirmative absence at all. If a working directory
        // returned Unavailable here, every refusal would read as an outage and the operator would
        // never be told they mistyped.
        var svc = CreateService();
        Assert.SkipWhen(!Reachable(svc), "Active Directory is not reachable from this host.");

        var result = svc.ValidateExists("OU=NoSuchOuAnywhere-a7f3,DC=invalid,DC=example", "OU");

        Assert.Equal(DirectoryLookupOutcome.NotFound, result.Outcome);
    }

    [Fact]
    public void ValidateExists_NonexistentUser_IsNotFound_NotUnavailable()
    {
        var svc = CreateService();
        Assert.SkipWhen(!Reachable(svc), "Active Directory is not reachable from this host.");

        var result = svc.ValidateExists("no.such.person.a7f3@invalid.example", "User");

        Assert.Equal(DirectoryLookupOutcome.NotFound, result.Outcome);
    }
}
