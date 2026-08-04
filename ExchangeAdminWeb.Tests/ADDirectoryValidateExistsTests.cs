using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards <see cref="ADDirectorySearchService.ValidateExists"/> - the existence check the
/// Protected Principals admin page gates new entries on
/// (docs/ProtectedPrincipalInputValidation-Plan.md).
///
/// Two properties are load-bearing:
///
/// 1. A lookup that could not run reports Unavailable, never NotFound. The two drive opposite
///    operator messages - "check the name" versus "AD unreachable, try again later" - so
///    collapsing them tells an admin their correct entry was a typo during an outage. This is
///    why the method exists at all instead of reusing Search, which returns an empty list for
///    unavailable, throttle timeout, exception and short term alike.
///
/// 2. The filter is exact-match, not the autocomplete's wildcard. Search would let `jdoe`
///    validate against `jdoe2` and report a nonexistent account as real.
///
/// The directory call itself needs a live AD, so these cover the decision helpers it defers to
/// plus the outcome the service returns when AD is absent (the CI condition).
/// </summary>
[Collection(LiveDirectoryCollection.Name)]
public class ADDirectoryValidateExistsTests
{
    private static ADDirectorySearchService CreateService()
        => new(NullLogger<ADDirectorySearchService>.Instance);

    // ---- absence vs failure --------------------------------------------------
    //
    // Asserted against ClassifyOutcome rather than ValidateExists. A test that drives the public
    // method can only observe the unavailable path on a machine with no working AD, so on a
    // developer box with RSAT it would skip and pass vacuously - which is exactly what happened
    // when this suite was first written, and the non-vacuity probe caught it.

    [Theory]
    [InlineData(ADDirectorySearchService.ValidationStep.DirectoryUnavailable)]
    [InlineData(ADDirectorySearchService.ValidationStep.ThrottleTimeout)]
    [InlineData(ADDirectorySearchService.ValidationStep.LookupThrew)]
    [InlineData(ADDirectorySearchService.ValidationStep.CmdletReportedErrors)]
    public void ClassifyOutcome_LookupDidNotComplete_IsUnavailable_NeverNotFound(
        ADDirectorySearchService.ValidationStep step)
    {
        // THE fail-closed case. Each of these is a question the directory never answered.
        // Reporting NotFound would tell an admin their correct entry was a typo during an outage,
        // and the refusal message would send them chasing a mistake they did not make.
        Assert.Equal(DirectoryLookupOutcome.Unavailable, ADDirectorySearchService.ClassifyOutcome(step));
    }

    [Fact]
    public void ClassifyOutcome_QueryRanAndFoundNothing_IsNotFound()
    {
        // The one and only path from the directory to NotFound: a query that actually completed.
        Assert.Equal(
            DirectoryLookupOutcome.NotFound,
            ADDirectorySearchService.ClassifyOutcome(ADDirectorySearchService.ValidationStep.CompletedWithNoResults));
    }

    [Fact]
    public void ClassifyOutcome_QueryRanAndFoundSomething_IsFound()
    {
        Assert.Equal(
            DirectoryLookupOutcome.Found,
            ADDirectorySearchService.ClassifyOutcome(ADDirectorySearchService.ValidationStep.CompletedWithResults));
    }

    [Fact]
    public void ClassifyOutcome_BlankInput_IsNotFound_NotUnavailable()
    {
        // Blank never reaches the directory, so "AD is down" would be wrong in the other
        // direction - the entry really is invalid and the operator should be told so.
        Assert.Equal(
            DirectoryLookupOutcome.NotFound,
            ADDirectorySearchService.ClassifyOutcome(ADDirectorySearchService.ValidationStep.BlankInput));
    }

    [Fact]
    public void ClassifyOutcome_EveryStepIsClassified()
    {
        // A step added later without a mapping falls into the `_` arm and becomes Unavailable.
        // That default is safe, but it should be a decision rather than an oversight, so this
        // fails loudly if the enum grows.
        var steps = Enum.GetValues<ADDirectorySearchService.ValidationStep>();
        Assert.Equal(7, steps.Length);

        foreach (var step in steps)
            Assert.True(Enum.IsDefined(ADDirectorySearchService.ClassifyOutcome(step)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ValidateExists_BlankInput_IsNotFound_WithoutConsultingTheDirectory(string? identity)
    {
        // Drives the real public method: blank must short-circuit before any directory call, so
        // this assertion holds whether or not the host has AD.
        var result = CreateService().ValidateExists(identity!, "User");

        Assert.Equal(DirectoryLookupOutcome.NotFound, result.Outcome);
        Assert.Null(result.Match);
    }

    // ---- exact-match filters -------------------------------------------------

    [Fact]
    public void BuildExactMatchFilter_User_MirrorsTheProtectionEnginesFilter()
    {
        // Must accept exactly the identity forms ResolveViaActiveDirectory resolves, or an entry
        // could validate and then never match at enforcement time.
        var filter = ADDirectorySearchService.BuildExactMatchFilter("jdoe@contoso.com", "User");

        Assert.Equal(
            "(|(userPrincipalName=jdoe@contoso.com)(mail=jdoe@contoso.com)(sAMAccountName=jdoe@contoso.com))",
            filter);
    }

    [Fact]
    public void BuildExactMatchFilter_Group_CoversDnCnAndSamAccountName()
    {
        var filter = ADDirectorySearchService.BuildExactMatchFilter("VR Staff", "Group");

        Assert.Equal(
            "(|(distinguishedName=VR Staff)(cn=VR Staff)(sAMAccountName=VR Staff)(name=VR Staff))",
            filter);
    }

    [Fact]
    public void BuildExactMatchFilter_Ou_MatchesOnDnOnly()
    {
        // CheckOuMatches is a DN suffix comparison, so only a DN is meaningful for an OU.
        var filter = ADDirectorySearchService.BuildExactMatchFilter("OU=Tier0,DC=contoso,DC=com", "OU");

        Assert.Equal("(distinguishedName=OU=Tier0,DC=contoso,DC=com)", filter);
    }

    [Theory]
    [InlineData("User")]
    [InlineData("Group")]
    [InlineData("OU")]
    public void BuildExactMatchFilter_NeverEmitsAWildcard(string objectKind)
    {
        // The autocomplete's substring filter would let `jdoe` validate against `jdoe2`. A
        // literal asterisk in the input must be escaped, not passed through as a matcher.
        var filter = ADDirectorySearchService.BuildExactMatchFilter("jdoe", objectKind);
        Assert.DoesNotContain("*", filter);

        var withStar = ADDirectorySearchService.BuildExactMatchFilter("jd*e", objectKind);
        Assert.DoesNotContain("*", withStar);
        Assert.Contains("\\2a", withStar);
    }

    [Fact]
    public void BuildExactMatchFilter_EscapesLdapMetacharacters()
    {
        var filter = ADDirectorySearchService.BuildExactMatchFilter("a(b)c", "User");

        Assert.Contains("\\28", filter);
        Assert.Contains("\\29", filter);
    }

    [Fact]
    public void BuildExactMatchFilter_UnknownObjectKind_FallsBackToTheUserFilter()
    {
        // Defensive: a caller passing an unrecognized kind must not produce an unbounded filter.
        var filter = ADDirectorySearchService.BuildExactMatchFilter("jdoe", "Nonsense");

        Assert.StartsWith("(|(userPrincipalName=", filter);
        Assert.DoesNotContain("*", filter);
    }

    // ---- DOMAIN\ prefix handling ---------------------------------------------

    [Theory]
    [InlineData("CONTOSO\\jdoe", "jdoe")]
    [InlineData("contoso\\VR Staff", "VR Staff")]
    [InlineData("jdoe", "jdoe")]
    [InlineData("jdoe@contoso.com", "jdoe@contoso.com")]
    [InlineData("  CONTOSO\\jdoe  ", "jdoe")]
    public void NormalizeIdentity_StripsTheDomainPrefix(string input, string expected)
    {
        // The admin page invites DOMAIN\username, but no AD attribute stores that form. Without
        // stripping, a legitimate CONTOSO\Admins entry would be refused as nonexistent.
        Assert.Equal(expected, ADDirectorySearchService.NormalizeIdentity(input));
    }

    [Theory]
    [InlineData("OU=Sales\\, East,DC=contoso,DC=com")]
    [InlineData("CN=VIP\\, Tier0,OU=Groups,DC=contoso,DC=com")]
    [InlineData("CN=Doe\\, Jane,OU=Users,DC=contoso,DC=com")]
    public void NormalizeIdentity_DnWithEscapedComma_IsNotTreatedAsADomainPrefix(string dn)
    {
        // Review finding ppv-2. A backslash inside a DN escapes a comma - legal AD, and exactly
        // what the group and OU pickers store. Stripping through it turned
        // "OU=Sales\, East,DC=contoso,DC=com" into ", East,DC=contoso,DC=com", so a valid entry
        // was refused as nonexistent and a saved one was badged stale.
        Assert.Equal(dn, ADDirectorySearchService.NormalizeIdentity(dn));
    }

    [Theory]
    [InlineData("OU=Sales\\, East,DC=contoso,DC=com", "OU")]
    [InlineData("CN=VIP\\, Tier0,OU=Groups,DC=contoso,DC=com", "Group")]
    public void BuildExactMatchFilter_DnWithEscapedComma_KeepsTheWholeDn(string dn, string kind)
    {
        var filter = ADDirectorySearchService.BuildExactMatchFilter(dn, kind);

        // The whole DN survives to the filter (LDAP-escaped), rather than only the tail after
        // the escape character.
        Assert.Contains(ProtectedPrincipalService.EscapeLdapFilter(dn), filter);
    }

    [Fact]
    public void NormalizeIdentity_TrailingBackslash_KeepsTheOriginal()
    {
        // Stripping would leave an empty term, and an empty exact-match filter matches every
        // object - turning a malformed entry into a confident "exists".
        Assert.Equal("CONTOSO\\", ADDirectorySearchService.NormalizeIdentity("CONTOSO\\"));
    }

    [Fact]
    public void BuildExactMatchFilter_DomainQualifiedGroup_SearchesTheBareName()
    {
        var filter = ADDirectorySearchService.BuildExactMatchFilter("CONTOSO\\VR Staff", "Group");

        Assert.Contains("(cn=VR Staff)", filter);
        Assert.DoesNotContain("CONTOSO", filter);
    }

    // ---- OU search (slice 3) -------------------------------------------------

    [Theory]
    [InlineData("User")]
    [InlineData("Group")]
    [InlineData("OU")]
    [InlineData("Any")]
    public void Search_AllObjectKindsIncludingOu_DoNotThrow(string objectKind)
    {
        // OU routes to Get-ADOrganizationalUnit rather than Get-ADUser/Get-ADGroup, so it needs
        // its own smoke coverage; the existing suite only knew about User/Group/Any.
        var results = CreateService().Search("test search term", objectKind);
        Assert.NotNull(results);
    }

    [Fact]
    public void Search_OuTermTooShort_ReturnsEmpty()
    {
        // The 3-character minimum is a shared guard, but OU was added after it was written.
        Assert.Empty(CreateService().Search("ou", "OU"));
    }

    // ---- result shape --------------------------------------------------------

    [Fact]
    public void DirectoryValidationResult_FoundCarriesTheMatch()
    {
        var match = new ADSearchResult("John", "CN=John,DC=x", "jdoe", "jdoe@x.com", "jdoe@x.com", "User");
        var result = new DirectoryValidationResult(DirectoryLookupOutcome.Found, match);

        Assert.Equal(DirectoryLookupOutcome.Found, result.Outcome);
        Assert.Equal("CN=John,DC=x", result.Match!.DistinguishedName);
    }

    [Fact]
    public void DirectoryLookupOutcome_DistinguishesAbsenceFromFailure()
    {
        // Stated as a test because the whole design rests on these being separate values.
        Assert.NotEqual(DirectoryLookupOutcome.NotFound, DirectoryLookupOutcome.Unavailable);
    }
}
