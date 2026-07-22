using ExchangeAdminWeb.Services.SelfServiceGroups;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Guards the injection-safe construction of the AD ownership reverse-lookup filter (plan
/// docs/SelfServiceGroupManagement-Plan.md task 1, codex F11). The caller DN is attacker-influenced
/// only via directory data, but the escaping contract must hold regardless: LDAP filter
/// metacharacters in the DN must be neutralized so they cannot alter the filter's structure.
/// </summary>
public class AdOwnershipFilterTests
{
    [Theory]
    [InlineData("plain", "plain")]
    [InlineData("a*b", "a\\2ab")]
    [InlineData("a(b)c", "a\\28b\\29c")]
    [InlineData("a\\b", "a\\5cb")]
    [InlineData("CN=Foo (Bar)*,OU=x", "CN=Foo \\28Bar\\29\\2a,OU=x")]
    public void EscapeLdapFilterValue_escapes_all_metacharacters(string input, string expected)
    {
        Assert.Equal(expected, AdOwnershipFilter.EscapeLdapFilterValue(input));
    }

    [Fact]
    public void EscapeLdapFilterValue_escapes_null_char()
    {
        Assert.Equal("a\\00b", AdOwnershipFilter.EscapeLdapFilterValue("a\0b"));
    }

    [Fact]
    public void BuildOwnedGroupsFilter_covers_both_owner_attributes()
    {
        var filter = AdOwnershipFilter.BuildOwnedGroupsFilter("CN=Jane,OU=Users,DC=contoso,DC=com");
        Assert.Equal(
            "(&(objectCategory=group)(|(managedBy=CN=Jane,OU=Users,DC=contoso,DC=com)(msExchCoManagedByLink=CN=Jane,OU=Users,DC=contoso,DC=com)))",
            filter);
    }

    [Fact]
    public void BuildOwnedGroupsFilter_escapes_a_hostile_dn_so_structure_is_intact()
    {
        // A DN carrying a filter-injection attempt must be neutralized: the ')' and '*' are escaped,
        // so no extra clause can be introduced.
        var hostile = "CN=x)(objectClass=*),OU=Users,DC=contoso,DC=com";
        var filter = AdOwnershipFilter.BuildOwnedGroupsFilter(hostile);

        Assert.DoesNotContain(")(objectClass=\\2a)", filter); // sanity on our own expectation string
        Assert.Contains("\\29\\28objectClass=\\2a\\29", filter);
        // The only unescaped parens are the ones this builder itself emits (structure), exactly 8:
        // (&  (objectCategory=group)  (|  (managedBy=..)  (msExchCoManagedByLink=..) ) )
        var structuralOpen = filter.Count(c => c == '(');
        var structuralClose = filter.Count(c => c == ')');
        Assert.Equal(5, structuralOpen);
        Assert.Equal(5, structuralClose);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildOwnedGroupsFilter_rejects_blank_dn(string dn)
    {
        Assert.Throws<ArgumentException>(() => AdOwnershipFilter.BuildOwnedGroupsFilter(dn));
    }
}
