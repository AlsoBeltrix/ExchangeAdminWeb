using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the sidebar Home link removal (owner ruling 2026-08-27): the brand link
/// (navbar-brand, targeting href="") stays as the app's single href="" control, and
/// the redundant "Home" NavLink (nav-home, also targeting href="" with
/// Match="NavLinkMatch.All") must not come back. Source-text guard because there is
/// no bUnit harness in this repo to render NavMenu.razor.
/// </summary>
public class NavMenuTests
{
    [Fact]
    public void NavMenu_HasNoRedundantHomeLink()
    {
        var text = File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Layout", "NavMenu.razor"));

        Assert.Contains("navbar-brand", text);
        Assert.DoesNotContain("nav-home", text);
        Assert.DoesNotContain("Match=\"NavLinkMatch.All\"", text);
    }
}
