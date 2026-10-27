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

    [Fact]
    public void TheReadme_SaysTheCheckNeedsTheDirectoryReadCredential_AndWhatItRefusesWithoutIt()
    {
        // aud-a70 (2): since aud-a60 and aud-a69 a grant to a group, or to a grantee only the directory holds, needed it.
        // aud-a70 (6): every grant does, and the README said a grant to a person Exchange names still went ahead.
        var section = ReadmeSelfGrantSection();

        Assert.Contains("**Needs the Directory Read Credential** (Admin Settings, Protected principals)", section, StringComparison.Ordinal);
        Assert.Contains("While the check is on (Prevent Self-Grant, the default), every grant needs it: without it, every grant is refused as incomplete, naming the setting - to a person or to a group, whichever directory holds the grantee - and nothing is written.", section, StringComparison.Ordinal);
        Assert.Contains("A removal is not checked and does not need it.", section, StringComparison.Ordinal);
        Assert.Contains("With it configured, each check fetches it once", section, StringComparison.Ordinal);
        Assert.DoesNotContain("still goes ahead", section, StringComparison.Ordinal);
    }

    [Fact]
    public void AdminSettings_SaysEveryPermissionGrantNeedsTheDirectoryReadCredential()
    {
        // aud-a70 (3): the credential's placeholder and hint named the protected-principal rules only. aud-a70 (6): they named
        // grants to groups and to grantees only the directory holds; every grant needs it.
        var page = OneSpaced(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "AdminSettings.razor")));

        Assert.Contains("placeholder=\"Required for group and OU rules, and for every permission grant\"", page, StringComparison.Ordinal);
        Assert.Contains("While Prevent Self-Grant is on, Mailbox Permissions and Calendar Permissions also need it for every grant: without it every grant is refused. A removal does not need it.", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Required when groups or OUs are configured", page, StringComparison.Ordinal);
        Assert.DoesNotContain("also need it to grant to a group", page, StringComparison.Ordinal);
    }
}
