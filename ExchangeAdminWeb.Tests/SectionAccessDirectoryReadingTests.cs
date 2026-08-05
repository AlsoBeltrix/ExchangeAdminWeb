using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The directory-reading decisions extracted from <c>SectionAccessGroupDirectory</c>
/// (docs/CoverageRatchetRepair-Plan.md slice 1).
///
/// These were unreachable by any test before the extraction: every path into them opened a
/// PowerShell runspace and imported the ActiveDirectory module. Each one is here because a wrong
/// answer is SILENT - the migration keeps going and resolves the wrong thing - not because the
/// code is complicated.
/// </summary>
public class SectionAccessDirectoryReadingTests
{
    // ------------------------------------------------------------------ UnwrapDnsRoot
    //
    // dnsRoot is multi-valued in the schema, so the runtime type depends on how the cmdlet
    // materialised it. A wrong answer here points every later group query at the wrong domain.

    [Fact]
    public void UnwrapDnsRoot_TakesAPlainString()
    {
        Assert.Equal("ad.analog.com", SectionAccessDirectoryReading.UnwrapDnsRoot("ad.analog.com"));
    }

    [Fact]
    public void UnwrapDnsRoot_TakesTheFirstEntryOfACollection()
    {
        // The whole reason this is not a cast: a collection's own ToString() is a type name, which
        // would be accepted as a server address and silently query nothing.
        var value = new[] { "winroot.analog.com", "other.analog.com" };

        Assert.Equal("winroot.analog.com", SectionAccessDirectoryReading.UnwrapDnsRoot(value));
    }

    [Fact]
    public void UnwrapDnsRoot_TakesTheFirstEntryOfANonGenericCollection()
    {
        // Get-ADObject can hand back a non-generic collection type; the IEnumerable branch is what
        // covers it.
        var value = new System.Collections.ArrayList { "ad.analog.com", "ignored" };

        Assert.Equal("ad.analog.com", SectionAccessDirectoryReading.UnwrapDnsRoot(value));
    }

    [Fact]
    public void UnwrapDnsRoot_FallsBackToToStringForOtherTypes()
    {
        Assert.Equal("42", SectionAccessDirectoryReading.UnwrapDnsRoot(42));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void UnwrapDnsRoot_ReturnsNullForNothingUsable(object? value)
    {
        // Null rather than a blank string, because the caller's fail-closed throw keys off null.
        Assert.Null(SectionAccessDirectoryReading.UnwrapDnsRoot(value));
    }

    [Fact]
    public void UnwrapDnsRoot_ReturnsNullForAnEmptyCollection()
    {
        Assert.Null(SectionAccessDirectoryReading.UnwrapDnsRoot(Array.Empty<string>()));
    }

    [Fact]
    public void UnwrapDnsRoot_ReturnsNullForACollectionOfBlanks()
    {
        // First entry wins, and the first entry is unusable - do NOT scan on for a better one.
        // A partially-populated multi-value is a directory the operator should look at, not a
        // puzzle to solve silently.
        Assert.Null(SectionAccessDirectoryReading.UnwrapDnsRoot(new[] { "  ", "ad.analog.com" }));
    }

    // ------------------------------------------------------------------ ChooseBareName
    //
    // Precedence is deliberate: sAMAccountName is the half of DOMAIN\Name Windows itself uses, and
    // DisplayName is last because it is neither unique nor required to match the logon name.

    [Fact]
    public void ChooseBareName_PrefersSamAccountName()
    {
        Assert.Equal("IAM", SectionAccessDirectoryReading.ChooseBareName("IAM", "Name", "Display", "queried"));
    }

    [Fact]
    public void ChooseBareName_FallsBackToNameWhenSamIsMissing()
    {
        Assert.Equal("Name", SectionAccessDirectoryReading.ChooseBareName(null, "Name", "Display", "queried"));
    }

    [Fact]
    public void ChooseBareName_FallsBackToDisplayNameThird()
    {
        Assert.Equal("Display", SectionAccessDirectoryReading.ChooseBareName(null, null, "Display", "queried"));
    }

    [Fact]
    public void ChooseBareName_FallsBackToTheQueriedName()
    {
        Assert.Equal("queried", SectionAccessDirectoryReading.ChooseBareName(null, null, null, "queried"));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ChooseBareName_TreatsBlankAttributesAsAbsent(string blank)
    {
        // AD returns empty strings, not nulls, for attributes it holds but has not populated. A
        // null check alone would render the group as an empty label.
        Assert.Equal("Name", SectionAccessDirectoryReading.ChooseBareName(blank, "Name", "Display", "queried"));
    }

    [Fact]
    public void ChooseBareName_TreatsEveryBlankAttributeAsAbsent()
    {
        Assert.Equal("queried", SectionAccessDirectoryReading.ChooseBareName("", "  ", "", "queried"));
    }

    [Fact]
    public void ChooseBareName_KeepsTheLeadingDollarNameThisStoreActuallyHolds()
    {
        // $KOO300-S3AMUVVBVMI1 is a real sAMAccountName in this deployment whose cn is
        // Employees-All (docs/SectionAccessSidStorage-Plan.md). Pinned so no future "sanitising"
        // of the leading '$' silently renames it.
        Assert.Equal(
            "$KOO300-S3AMUVVBVMI1",
            SectionAccessDirectoryReading.ChooseBareName("$KOO300-S3AMUVVBVMI1", "Employees-All", null, "queried"));
    }

    // ------------------------------------------------------------------ GroupSidProblem

    [Fact]
    public void GroupSidProblem_AcceptsAReadableSid()
    {
        Assert.Null(SectionAccessDirectoryReading.GroupSidProblem(
            "S-1-5-21-8915387-325452579-1788637320-586078", "IAM"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void GroupSidProblem_RejectsAnUnreadableSid(string? sid)
    {
        // Rejecting, not skipping. Dropping the row would understate the match count, and the
        // caller refuses only on ambiguity - so two matches where one lost its SID would look like
        // a confident single answer and migrate as one. A wrong-group grant with no error.
        var problem = SectionAccessDirectoryReading.GroupSidProblem(sid, "IAM");

        Assert.NotNull(problem);
        Assert.Contains("IAM", problem);
        Assert.Contains("objectSid", problem);
    }

    // ------------------------------------------------------------------ PartitionMatchProblem
    //
    // Exactly one forest partition must match a NetBIOS name. Anything else has to stop the
    // migration: querying groups against a domain the operator did not name would store whatever
    // SIDs came back, which is a silent wrong-domain grant.

    [Fact]
    public void PartitionMatchProblem_AcceptsExactlyOneMatch()
    {
        Assert.Null(SectionAccessDirectoryReading.PartitionMatchProblem("ANALOG", 1));
    }

    [Fact]
    public void PartitionMatchProblem_RejectsNoMatch()
    {
        var problem = SectionAccessDirectoryReading.PartitionMatchProblem("NOSUCH", 0);

        Assert.NotNull(problem);
        Assert.Contains("NOSUCH", problem);
        // Distinct from the ambiguous case: "not found" sends an admin to the stored value,
        // "matched N" sends them to the forest. One merged message misdirects half the time.
        Assert.Contains("no forest partition", problem);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void PartitionMatchProblem_RejectsAmbiguousMatches(int count)
    {
        var problem = SectionAccessDirectoryReading.PartitionMatchProblem("ANALOG", count);

        Assert.NotNull(problem);
        Assert.Contains($"matched {count}", problem);
        Assert.Contains("expected exactly one", problem);
    }

    [Fact]
    public void PartitionMatchProblem_NamesTheDomainInEveryRejection()
    {
        // The message is the only thing an operator sees; a rejection that does not say WHICH
        // domain failed is unactionable when several are configured.
        foreach (var count in new[] { 0, 2 })
        {
            Assert.Contains("WINROOT", SectionAccessDirectoryReading.PartitionMatchProblem("WINROOT", count)!);
        }
    }

    // ------------------------------------------------------------------ NetBiosFromNTAccount

    [Fact]
    public void NetBiosFromNTAccount_TakesTheDomainHalf()
    {
        Assert.Equal("ANALOG", SectionAccessDirectoryReading.NetBiosFromNTAccount(@"ANALOG\IAM"));
    }

    [Fact]
    public void NetBiosFromNTAccount_TakesTheFirstSeparatorOnly()
    {
        // A group name may itself contain a backslash; only the first split is the domain.
        Assert.Equal("WINROOT", SectionAccessDirectoryReading.NetBiosFromNTAccount(@"WINROOT\Odd\Name"));
    }

    [Fact]
    public void NetBiosFromNTAccount_ReturnsNullForABareName()
    {
        Assert.Null(SectionAccessDirectoryReading.NetBiosFromNTAccount("IAM"));
    }

    [Fact]
    public void NetBiosFromNTAccount_ReturnsNullForALeadingSeparator()
    {
        // The reason the check is `slash > 0` and not `>= 0`. At index 0 there is no domain half,
        // and treating it as one renders the group as "\Name" - a display defect that looks like
        // data corruption to an operator.
        Assert.Null(SectionAccessDirectoryReading.NetBiosFromNTAccount(@"\IAM"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NetBiosFromNTAccount_ReturnsNullForNothingUsable(string? account)
    {
        Assert.Null(SectionAccessDirectoryReading.NetBiosFromNTAccount(account));
    }
}
