using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards GroupManagementService.RankGroups - the pure ranking that GM-1 introduced so the
/// on-prem group search surfaces the exact match first, then prefix, then substring matches,
/// alphabetical within each tier. The live AD query in SearchGroupsAsync is not unit-testable;
/// the logic-bearing ranking is. Plan: docs/GroupManagementSearch-Plan.md (slice 1).
/// </summary>
public class GroupManagementSearchRankingTests
{
    private static GroupInfo G(string name, string? sam = null) =>
        new() { Name = name, SamAccountName = sam ?? name };

    private static List<string> Names(IEnumerable<GroupInfo> g) => g.Select(x => x.Name).ToList();

    [Fact]
    public void ExactMatch_RanksFirst_EvenWhenInputPutsItLast()
    {
        var input = new[] { G("Team-IAM-West"), G("DiagramTeam"), G("IAM-Admins"), G("IAM") };

        var ranked = Names(GroupManagementService.RankGroups(input, "IAM"));

        Assert.Equal("IAM", ranked[0]);
    }

    [Fact]
    public void StartsWith_RanksAboveContains()
    {
        var input = new[] { G("Team-IAM-West"), G("IAM-Admins"), G("Corp-IAM") };

        var ranked = Names(GroupManagementService.RankGroups(input, "IAM"));

        // IAM-Admins starts with the term; the other two only contain it.
        Assert.Equal("IAM-Admins", ranked[0]);
        Assert.Equal(new[] { "Corp-IAM", "Team-IAM-West" }, ranked.Skip(1).ToArray());
    }

    [Fact]
    public void WithinTier_OrderedAlphabeticallyByName()
    {
        var input = new[] { G("IAM-Zulu"), G("IAM-Alpha"), G("IAM-Mike") };

        var ranked = Names(GroupManagementService.RankGroups(input, "IAM"));

        Assert.Equal(new[] { "IAM-Alpha", "IAM-Mike", "IAM-Zulu" }, ranked.ToArray());
    }

    [Fact]
    public void ExactMatchOnSamAccountName_PromotesToTierOne()
    {
        // Display name only contains the term; the SamAccountName equals it exactly.
        var input = new[] { G("Identity Access Mgmt", sam: "Other"), G("Apply-IAM-Policy", sam: "IAM") };

        var ranked = GroupManagementService.RankGroups(input, "IAM");

        Assert.Equal("Apply-IAM-Policy", ranked[0].Name);
    }

    [Fact]
    public void Ranking_IsCaseInsensitive()
    {
        var input = new[] { G("contains-iam-here"), G("iam") };

        var ranked = Names(GroupManagementService.RankGroups(input, "IAM"));

        Assert.Equal("iam", ranked[0]);
    }

    [Fact]
    public void RealisticSet_ExactThenPrefixThenContains()
    {
        // Deliberately shuffled so the test fails if ranking is a no-op (identity order).
        var input = new[]
        {
            G("IAM-Operators"),     // prefix
            G("IAM"),               // exact
            G("Team-IAM-West"),     // contains
            G("IAM-Admins"),        // prefix
            G("DiagramTeam"),       // contains "iam"
        };

        var ranked = Names(GroupManagementService.RankGroups(input, "IAM"));

        Assert.Equal(
            new[] { "IAM", "IAM-Admins", "IAM-Operators", "DiagramTeam", "Team-IAM-West" },
            ranked.ToArray());
    }

    [Fact]
    public void BlankTerm_ReturnsInputAlphabetical()
    {
        var input = new[] { G("Zebra"), G("Apple"), G("Mango") };

        var ranked = Names(GroupManagementService.RankGroups(input, "  "));

        Assert.Equal(new[] { "Apple", "Mango", "Zebra" }, ranked.ToArray());
    }
}
