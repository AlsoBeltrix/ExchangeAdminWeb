using System.Text.RegularExpressions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a70. What the README's Self-grant Prevention section and the Admin Settings page tell an operator about the self-grant
/// check: what it needs, and what it refuses without it. Source checks over the files as shipped, white space read as one
/// space so a re-wrapped line does not fail them.
/// </summary>
public class SelfGrantDocumentationTests
{
    private static string OneSpaced(string text) => Regex.Replace(text, @"\s+", " ");

    /// <summary>The README's Self-grant Prevention bullet and its sub-bullets, up to the next feature.</summary>
    private static string ReadmeSelfGrantSection()
    {
        var readme = File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("README.md"));
        var start = readme.IndexOf("- **Self-grant Prevention**", StringComparison.Ordinal);
        Assert.True(start >= 0, "the README has no Self-grant Prevention entry");
        var end = readme.IndexOf("- **Protected User Lists**", start, StringComparison.Ordinal);
        Assert.True(end > start, "the README's Self-grant Prevention entry is not followed by Protected User Lists");
        return OneSpaced(readme[start..end]);
    }

    [Fact]
    public void TheReadme_SaysADynamicGroupIsCheckedAsExchangeLastCalculatedIt_AndAnEmptyOneIsRefused()
    {
        // aud-a70 (8), (11): the check reads the members Exchange calculated, not the filter, and refuses an empty list.
        var section = ReadmeSelfGrantSection();

        Assert.Contains("A dynamic distribution group is checked against the members Exchange Online last calculated for it", section, StringComparison.Ordinal);
        Assert.Contains("a recipient its filter matches only since then is not compared", section, StringComparison.Ordinal);
        Assert.Contains("One Exchange reports no member of is refused as incomplete", section, StringComparison.Ordinal);
    }
}
