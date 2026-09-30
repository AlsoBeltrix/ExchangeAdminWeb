using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Comms-10k membership listing (docs/Comms10kBulkResolveScale-Plan.md S2, test 8).
/// </summary>
/// <remarks>
/// <para>
/// Preview and Download CSV did not merely scale badly - they THREW on the real group.
/// <c>Get-ADGroupMember</c> expands every member into a full object and is bound by the ADWS
/// <c>MaxGroupOrMemberEntries</c> cap, default 5,000, which is below the size this module exists
/// to manage. <c>ExecuteReplaceAsync</c> had already been fixed to read the raw <c>member</c>
/// attribute; this half never got the same treatment.
/// </para>
/// <para>
/// <b>The rule these protect is the opposite of the address resolver's, and the two must not be
/// made consistent with each other.</b> There, an unresolved directory row means an address
/// matched nobody, and it is dropped from a WRITE. Here an unresolved DN is a member who IS in
/// the group - dropping it under-reports who is subscribed on the screen an operator reads to
/// decide exactly that.
/// </para>
/// </remarks>
public class Comms10kMemberResolverTests
{
    private static List<string> Dns(int count) =>
        Enumerable.Range(0, count).Select(i => $"CN=User{i},OU=People,DC=example,DC=test").ToList();

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(499)]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(10001)]
    public void BatchingCoversEveryMemberExactlyOnceAndInOrder(int count)
    {
        var input = Dns(count);
        var flattened = Comms10kMemberResolver.Batch(input).SelectMany(b => b).ToList();

        Assert.Equal(input, flattened);
    }

    [Fact]
    public void EveryMemberProducesARowEvenWhenTheDirectoryReturnedNoDetailForIt()
    {
        // The property worth stating on its own: rows out == members in. A detail query that
        // missed somebody costs a display name, never a member. Anything else means the page
        // under-reports the membership.
        var dns = Dns(5);
        var details = new[]
        {
            new Comms10kMemberResolver.Detail(dns[1], "user1", "user1@example.test", "User One"),
        };

        var rows = Comms10kMemberResolver.BuildRows(dns, details);

        Assert.Equal(5, rows.Count);
        Assert.Equal("User One", rows[1].DisplayName);
        Assert.Equal("user1@example.test", rows[1].Email);
    }

    [Fact]
    public void AnUnresolvableMemberFallsBackToItsLeadingCnRatherThanDisappearing()
    {
        var rows = Comms10kMemberResolver.BuildRows(["CN=Jane Doe,OU=People,DC=example,DC=test"], []);

        var row = Assert.Single(rows);
        Assert.Equal("Jane Doe", row.DisplayName);
        Assert.Equal("", row.Email);
    }

    [Fact]
    public void ACommaInsideACnDoesNotCutTheFallbackNameInHalf()
    {
        // A DN escapes a literal comma inside a CN as "\,". Splitting on a bare comma - the
        // obvious implementation - would render "Smith\" for every surname-first account in the
        // directory, which is most of them.
        Assert.Equal("Smith, John", Comms10kMemberResolver.LeadingCn(@"CN=Smith\, John,OU=People,DC=example,DC=test"));
    }

    [Fact]
    public void TheDetailFilterEscapesADnRatherThanLettingItCloseTheClause()
    {
        // A CN can legitimately contain parentheses - "CN=Smith (Contractor)" - and one
        // unescaped ")" silently corrupts the filter for the whole batch of 500, not just for
        // that member.
        var filter = Comms10kMemberResolver.BuildBatchFilter([@"CN=Smith (Contractor),OU=People,DC=x"]);

        Assert.Contains(@"CN=Smith \28Contractor\29,OU=People,DC=x", filter, StringComparison.Ordinal);
        Assert.StartsWith("(|(distinguishedName=", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDetailFilterIsClassAgnostic()
    {
        // A distribution list can legitimately hold a contact or a nested group. This is a
        // LISTING: filtering to users would hide members who really are subscribed.
        var filter = Comms10kMemberResolver.BuildBatchFilter(["CN=A,DC=x"]);

        Assert.DoesNotContain("objectClass", filter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectCategory", filter, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("S-1-5-21-1111111111-2222222222-3333333333-1234", "1234")]
    [InlineData("S-1-5-21-1-2-3-512", "512")]
    [InlineData("not-a-sid", null)]
    [InlineData("S-1-5-21-1-2-3-", null)]
    [InlineData(null, null)]
    public void TheGroupRidIsReadForThePrimaryGroupUnion(string? sid, string? expected)
    {
        // The union exists because the linked `member` attribute does NOT carry members whose
        // membership comes from their primary group, which Get-ADGroupMember included. Swapping
        // one for the other without it silently shortens the list - lst-2, already fixed once in
        // GroupManagementService. A null RID must fail the read, never yield a shorter list,
        // which is why the service throws on it.
        Assert.Equal(expected, Comms10kMemberResolver.RidFromSid(sid));
    }

    [Fact]
    public void TheListingReadsTheMemberAttributeAndNeverGetAdGroupMember()
    {
        // Source-level because the seam IS the live query. This is the defect that made Preview
        // and Download CSV throw, and it is one careless edit away from returning.
        var source = File.ReadAllText(Path.Combine(GetServicesDirectory(), "Comms10kService.cs"));
        // Comments stripped: the seam EXPLAINS in prose why it does not call Get-ADGroupMember,
        // so a scan that kept them would read the explanation as the violation. Third time this
        // trap has fired today - a guard whose subject is a named antipattern must never read
        // the comment that names it.
        var seam = StripLineComments(ExtractSeam(source, "QueryMemberDns"));

        Assert.Contains("\"Get-ADGroup\"", seam, StringComparison.Ordinal);
        Assert.Contains("\"member\"", seam, StringComparison.Ordinal);
        Assert.Contains("primaryGroupID=", seam, StringComparison.Ordinal);
        Assert.DoesNotContain("Get-ADGroupMember", seam, StringComparison.Ordinal);
    }

    [Fact]
    public void TheListingFailsClosedRatherThanReturningAShorterMembership()
    {
        // An unreadable SID or a failed primary-group query must throw. A quietly shorter
        // membership list is the finding itself, not a degraded mode.
        var seam = ExtractSeam(File.ReadAllText(Path.Combine(GetServicesDirectory(), "Comms10kService.cs")), "QueryMemberDns");

        Assert.Contains("its SID was unreadable", seam, StringComparison.Ordinal);
        Assert.Contains("primary-group membership could not be read", seam, StringComparison.Ordinal);
    }

    // ---- helpers ----

    /// <summary>
    /// The named seam's body. Anchored on the declaration, not on a bare name - the looser
    /// anchor matches the CALL SITE first and sweeps up the doc comment, which cost a round on
    /// the S1 tests earlier the same day.
    /// </summary>
    private static string ExtractSeam(string source, string name)
    {
        var start = source.IndexOf($"internal virtual IReadOnlyList<", StringComparison.Ordinal);
        while (start >= 0 && !source[start..Math.Min(source.Length, start + 200)].Contains(name, StringComparison.Ordinal))
            start = source.IndexOf("internal virtual IReadOnlyList<", start + 1, StringComparison.Ordinal);

        Assert.True(start >= 0, $"declaration of '{name}' not found in Comms10kService.cs");

        var next = source.IndexOf("\n    /// <summary>", start, StringComparison.Ordinal);
        return next > start ? source[start..next] : source[start..];
    }

    /// <summary>Source with // comments removed, so prose naming a rule is not read as breaking it.</summary>
    private static string StripLineComments(string source) =>
        System.Text.RegularExpressions.Regex.Replace(source, @"//[^\r\n]*", "");

    private static string GetServicesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var services = Path.Combine(dir.FullName, "Services");
            if (Directory.Exists(services))
                return services;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Services from the test base directory.");
    }
}
