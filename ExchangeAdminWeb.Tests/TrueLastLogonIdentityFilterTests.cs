using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// True Last Logon resolves the identity the operator typed, whether it is a userPrincipalName
/// or a sAMAccountName.
/// </summary>
/// <remarks>
/// <para>
/// <b>The defect these close.</b> The sweep called
/// <c>Get-ADUser -Identity &lt;what the operator typed&gt;</c>. <c>-Identity</c> resolves a
/// distinguished name, an objectGUID, an objectSid or a sAMAccountName and NOTHING ELSE - a
/// userPrincipalName is not on that list. So every UPN search failed on every domain controller
/// with "Cannot find an object with identity", and the module reported that all 37 DCs had
/// failed to answer.
/// </para>
/// <para>
/// <b>The module's fail-closed design was the only thing that made this safe.</b> Because a DC
/// that errors is counted as not having answered rather than as "never logged on", the page said
/// it knew nothing instead of reporting a live account as dormant. That is the difference between
/// a visible bug and a disabled account, and it is why the aggregator's rule is not negotiable.
/// </para>
/// </remarks>
public class TrueLastLogonIdentityFilterTests
{
    [Theory]
    [InlineData("michael.coelho@analog.com")]
    [InlineData("mcoelho")]
    public void EitherShapeOfIdentityIsMatchedOnBothAttributes(string typed)
    {
        // No branch on "@". Splitting on the character would be a guess about what was typed;
        // matching either attribute needs no guess, and the two cannot collide because a
        // sAMAccountName cannot contain "@" and a UPN must.
        var filter = TrueLastLogonService.BuildIdentityFilter(typed);

        Assert.Contains($"(userPrincipalName={typed})", filter, StringComparison.Ordinal);
        Assert.Contains($"(sAMAccountName={typed})", filter, StringComparison.Ordinal);
        Assert.StartsWith("(&(objectCategory=person)(objectClass=user)(|", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void TheIdentityIsTrimmedBeforeItIsMatched()
    {
        // A pasted address routinely carries trailing whitespace, and a filter is exact-match.
        var filter = TrueLastLogonService.BuildIdentityFilter("  mcoelho  ");

        Assert.Contains("(sAMAccountName=mcoelho)", filter, StringComparison.Ordinal);
        Assert.DoesNotContain(" mcoelho", filter, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("*", @"\2a")]
    [InlineData("(", @"\28")]
    [InlineData(")", @"\29")]
    [InlineData(@"\", @"\5c")]
    public void FilterMetacharactersAreEscapedRatherThanPassedThrough(string raw, string escaped)
    {
        // The value comes straight from a text box. An unescaped metacharacter does not fail -
        // it changes the filter's STRUCTURE. A bare asterisk would turn an exact match into a
        // wildcard sweep and report the first stranger it found under the name that was typed.
        var filter = TrueLastLogonService.BuildIdentityFilter($"user{raw}name");

        Assert.Contains($"user{escaped}name", filter, StringComparison.Ordinal);
        Assert.DoesNotContain($"user{raw}name", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void AWildcardCannotSurviveIntoTheFilter()
    {
        // Stated on its own because it is the one that silently returns a WRONG ANSWER rather
        // than an error, and a wrong last-logon date is what gets an account disabled.
        var filter = TrueLastLogonService.BuildIdentityFilter("*");

        Assert.DoesNotContain("(userPrincipalName=*)", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("(sAMAccountName=*)", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void TheFilterIsBalancedSoItCannotBeTruncatedIntoSomethingValid()
    {
        var filter = TrueLastLogonService.BuildIdentityFilter(@"a)(b*c\d");

        Assert.Equal(filter.Count(c => c == '('), filter.Count(c => c == ')'));
    }

    // ----- the sweep script uses the filter, and never -Identity again -------------------------

    [Fact]
    public void TheSweepQueriesByLdapFilterAndNotByIdentity()
    {
        var script = SweepScript();

        Assert.Contains("Get-ADUser -LDAPFilter $using:LdapFilter", script, StringComparison.Ordinal);
        Assert.DoesNotContain("-Identity", script, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdentityMatchingNoUserOnADcIsAnError_NeverASilentNullDate()
    {
        // A filter returns an empty set where -Identity threw. If that became a null date the DC
        // would be counted as having answered "never logged on", and enough of those read as a
        // dormant account.
        var script = SweepScript();

        Assert.Contains("$found.Count -eq 0", script, StringComparison.Ordinal);
        Assert.Contains("No user matched this identity", script, StringComparison.Ordinal);
    }

    [Fact]
    public void MoreThanOneMatchFailsClosed()
    {
        // -Identity could only ever return one object; a filter can return several. Picking one
        // would report an arbitrary stranger's logon under the typed name.
        var script = SweepScript();

        Assert.Contains("$found.Count -gt 1", script, StringComparison.Ordinal);
        Assert.Contains("Ambiguous", script, StringComparison.Ordinal);
    }

    [Fact]
    public void NoDomainIsNamedInTheSweep()
    {
        // Architectural invariant 7: directory scope is discovered from the host's own
        // membership, never named. The source script's -Domain default deliberately did not port,
        // and the filter added here must not smuggle one back in.
        var script = SweepScript();

        Assert.DoesNotContain("-Domain ", script, StringComparison.Ordinal);
        Assert.DoesNotContain("DC=", script, StringComparison.Ordinal);
    }

    // ----- harness -------------------------------------------------------------------------

    /// <summary>
    /// The sweep script's source text. Read from the file rather than reflected out of the
    /// private const, so the test names the thing a reviewer would read.
    /// </summary>
    private static string SweepScript()
    {
        var source = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Services", "TrueLastLogonService.cs"));

        var match = Regex.Match(source, @"SweepScript = @""(?<script>.*?)"";", RegexOptions.Singleline);
        Assert.True(match.Success, "the sweep script literal was not found in TrueLastLogonService.cs.");

        // Comments stripped: the script now explains in prose exactly why it does not use
        // -Identity, and a guard forbidding that construct must not match the explanation.
        return Regex.Replace(match.Groups["script"].Value, @"#[^\r\n]*", " ");
    }
}
