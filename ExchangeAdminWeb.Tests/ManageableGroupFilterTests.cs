using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the in-list filter (plan docs/SelfServiceGroupManagement-Plan.md AC9). The behavior these
/// tests pin: AC9 requires a NON-PREFIX term to find a group - a word from the middle of the name, or a
/// word from the description - so the match must be a case-insensitive SUBSTRING, not a prefix match. A
/// blank term returns the whole list; matching preserves input order.
/// </summary>
public class ManageableGroupFilterTests
{
    private static ManageableGroup Group(string name, string sam = "", string? description = null) => new()
    {
        ObjectGuid = Guid.NewGuid().ToString(),
        DistinguishedName = $"CN={name},OU=Groups,DC=example,DC=com",
        Name = name,
        SamAccountName = sam,
        Description = description,
    };

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Blank_term_matches_every_group(string? term)
    {
        var g = Group("Finance Team");
        Assert.True(ManageableGroupFilter.Matches(g, term));
    }

    [Fact]
    public void Matches_a_mid_name_word_not_just_a_prefix()
    {
        // AC9 core: "Team" is in the MIDDLE of the name, not a prefix. A prefix-only match would miss it.
        var g = Group("Corporate Team Alpha");
        Assert.True(ManageableGroupFilter.Matches(g, "Team"));
        Assert.True(ManageableGroupFilter.Matches(g, "Alpha"));
    }

    [Fact]
    public void Matches_a_description_word()
    {
        var g = Group("GRP-001", description: "Payroll approvers for the western region");
        Assert.True(ManageableGroupFilter.Matches(g, "approvers"));
        Assert.True(ManageableGroupFilter.Matches(g, "western"));
    }

    [Fact]
    public void Matches_sam_account_name()
    {
        var g = Group("Human Resources", sam: "grp-hr-all");
        Assert.True(ManageableGroupFilter.Matches(g, "hr-all"));
    }

    [Fact]
    public void Match_is_case_insensitive()
    {
        var g = Group("Finance Team", description: "Budget owners");
        Assert.True(ManageableGroupFilter.Matches(g, "finance"));
        Assert.True(ManageableGroupFilter.Matches(g, "BUDGET"));
    }

    [Fact]
    public void Non_matching_term_does_not_match()
    {
        var g = Group("Finance Team", sam: "grp-fin", description: "Budget owners");
        Assert.False(ManageableGroupFilter.Matches(g, "engineering"));
    }

    [Fact]
    public void Null_description_does_not_throw_and_does_not_match_on_description()
    {
        var g = Group("Finance Team", description: null);
        Assert.False(ManageableGroupFilter.Matches(g, "owners"));
        Assert.True(ManageableGroupFilter.Matches(g, "Finance"));
    }

    [Fact]
    public void Filter_blank_term_returns_all_in_input_order()
    {
        var groups = new[] { Group("Alpha"), Group("Bravo"), Group("Charlie") };
        var result = ManageableGroupFilter.Filter(groups, "  ");
        Assert.Equal(new[] { "Alpha", "Bravo", "Charlie" }, result.Select(g => g.Name));
    }

    [Fact]
    public void Filter_returns_only_matches_preserving_order()
    {
        var groups = new[]
        {
            Group("Finance Team"),
            Group("Engineering Team"),
            Group("Finance Approvers", description: "money"),
        };
        var result = ManageableGroupFilter.Filter(groups, "finance");
        Assert.Equal(new[] { "Finance Team", "Finance Approvers" }, result.Select(g => g.Name));
    }

    [Fact]
    public void Filter_no_match_returns_empty_not_null()
    {
        var groups = new[] { Group("Finance Team") };
        var result = ManageableGroupFilter.Filter(groups, "zzz");
        Assert.Empty(result);
    }
}
