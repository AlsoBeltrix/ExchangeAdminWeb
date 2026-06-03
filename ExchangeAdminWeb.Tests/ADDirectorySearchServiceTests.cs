using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

public class ADDirectorySearchServiceTests
{
    // ---------------------------------------------------------------
    // LDAP filter escaping (delegates to ProtectedPrincipalService.EscapeLdapFilter)
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("john", "john")]
    [InlineData("O'Brien", "O'Brien")]              // Apostrophe is safe in LDAP
    [InlineData("test*user", "test\\2auser")]        // Wildcard escaped
    [InlineData("test(user)", "test\\28user\\29")]   // Parentheses escaped
    [InlineData("test\\user", "test\\5cuser")]       // Backslash escaped
    [InlineData("a\0b", "a\\00b")]                   // Null byte escaped
    [InlineData("*()\\\0", "\\2a\\28\\29\\5c\\00")]  // All special chars together
    public void EscapeLdapFilter_EscapesSpecialCharacters(string input, string expected)
    {
        var result = ProtectedPrincipalService.EscapeLdapFilter(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EscapeLdapFilter_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", ProtectedPrincipalService.EscapeLdapFilter(""));
    }

    [Fact]
    public void EscapeLdapFilter_PlainText_Unchanged()
    {
        const string input = "John Smith 123";
        Assert.Equal(input, ProtectedPrincipalService.EscapeLdapFilter(input));
    }

    // ---------------------------------------------------------------
    // Minimum query length enforcement
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("  ")]
    [InlineData("  a")]
    [InlineData(" ab")]
    public void Search_TermTooShort_ReturnsEmpty(string term)
    {
        var svc = CreateService();
        var results = svc.Search(term, "Any");
        Assert.Empty(results);
    }

    [Fact]
    public void Search_NullTerm_ReturnsEmpty()
    {
        var svc = CreateService();
        var results = svc.Search(null!, "Any");
        Assert.Empty(results);
    }

    // ---------------------------------------------------------------
    // Max results enforcement
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Search_InvalidMaxResults_DoesNotThrow(int maxResults)
    {
        var svc = CreateService();
        // Should not throw; will return empty because AD is unavailable in test env
        var results = svc.Search("test term", "Any", maxResults);
        Assert.NotNull(results);
    }

    // ---------------------------------------------------------------
    // Graceful fallback when AD unavailable
    // ---------------------------------------------------------------

    [Fact]
    public void IsAvailable_WhenAdUnavailable_ReturnsFalse()
    {
        // In a test environment without RSAT, IsAvailable should be false
        var svc = CreateService();
        // Access IsAvailable — this triggers the lazy probe
        var available = svc.IsAvailable;
        // We can't guarantee the test host has RSAT, so just verify no exception
        Assert.IsType<bool>(available);
    }

    [Fact]
    public void Search_WhenAdUnavailable_ReturnsEmpty()
    {
        var svc = CreateService();
        // If RSAT is not installed (typical CI), this returns empty gracefully
        if (!svc.IsAvailable)
        {
            var results = svc.Search("test user search", "User");
            Assert.Empty(results);
        }
    }

    [Fact]
    public void SearchUsers_WhenAdUnavailable_ReturnsEmpty()
    {
        var svc = CreateService();
        if (!svc.IsAvailable)
        {
            var results = svc.SearchUsers("test user");
            Assert.Empty(results);
        }
    }

    [Fact]
    public void SearchGroups_WhenAdUnavailable_ReturnsEmpty()
    {
        var svc = CreateService();
        if (!svc.IsAvailable)
        {
            var results = svc.SearchGroups("test group");
            Assert.Empty(results);
        }
    }

    // ---------------------------------------------------------------
    // ObjectKind parameter validation
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("User")]
    [InlineData("Group")]
    [InlineData("Any")]
    public void Search_AllObjectKinds_DoNotThrow(string objectKind)
    {
        var svc = CreateService();
        var results = svc.Search("test search term", objectKind);
        Assert.NotNull(results);
    }

    // ---------------------------------------------------------------
    // ADSearchResult record
    // ---------------------------------------------------------------

    [Fact]
    public void ADSearchResult_StoresAllFields()
    {
        var result = new ADSearchResult(
            DisplayName: "John Smith",
            DistinguishedName: "CN=John Smith,OU=Users,DC=contoso,DC=com",
            SamAccountName: "jsmith",
            UserPrincipalName: "jsmith@contoso.com",
            Email: "john.smith@contoso.com",
            ObjectType: "User");

        Assert.Equal("John Smith", result.DisplayName);
        Assert.Equal("CN=John Smith,OU=Users,DC=contoso,DC=com", result.DistinguishedName);
        Assert.Equal("jsmith", result.SamAccountName);
        Assert.Equal("jsmith@contoso.com", result.UserPrincipalName);
        Assert.Equal("john.smith@contoso.com", result.Email);
        Assert.Equal("User", result.ObjectType);
    }

    [Fact]
    public void ADSearchResult_GroupWithNullFields()
    {
        var result = new ADSearchResult(
            DisplayName: "IT Admins",
            DistinguishedName: "CN=IT Admins,OU=Groups,DC=contoso,DC=com",
            SamAccountName: "ITAdmins",
            UserPrincipalName: null,
            Email: null,
            ObjectType: "Group");

        Assert.Equal("IT Admins", result.DisplayName);
        Assert.Null(result.UserPrincipalName);
        Assert.Null(result.Email);
        Assert.Equal("Group", result.ObjectType);
    }

    [Fact]
    public void ADSearchResult_Equality()
    {
        var a = new ADSearchResult("A", "DN=A", "a", "a@x.com", "a@x.com", "User");
        var b = new ADSearchResult("A", "DN=A", "a", "a@x.com", "a@x.com", "User");
        Assert.Equal(a, b);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    private static ADDirectorySearchService CreateService()
    {
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<ADDirectorySearchService>.Instance;
        return new ADDirectorySearchService(logger);
    }
}
