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

    [Fact]
    public void BuildGroupByNameFilter_matches_name_and_sam_exactly()
    {
        var filter = AdOwnershipFilter.BuildGroupByNameFilter("Finance Team");
        Assert.Equal(
            "(&(objectCategory=group)(|(name=Finance Team)(sAMAccountName=Finance Team)))",
            filter);
    }

    [Fact]
    public void BuildGroupByNameFilter_escapes_wildcard_so_it_cannot_widen_the_match()
    {
        // A '*' the user types must become a LITERAL, never an LDAP wildcard - otherwise "a*"
        // would match every group starting with "a" (over-broad disclosure).
        var filter = AdOwnershipFilter.BuildGroupByNameFilter("a*");
        Assert.DoesNotContain("name=a*", filter);
        Assert.Contains("(name=a\\2a)", filter);
        Assert.Contains("(sAMAccountName=a\\2a)", filter);
    }

    [Fact]
    public void BuildGroupByNameFilter_escapes_a_hostile_name_so_structure_is_intact()
    {
        var hostile = "x)(objectClass=*)";
        var filter = AdOwnershipFilter.BuildGroupByNameFilter(hostile);

        Assert.Contains("x\\29\\28objectClass=\\2a\\29", filter);
        // Only the builder's own structural parens are unescaped, exactly 5 pairs:
        // (&  (objectCategory=group)  (|  (name=..)  (sAMAccountName=..) ) )
        Assert.Equal(5, filter.Count(c => c == '('));
        Assert.Equal(5, filter.Count(c => c == ')'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildGroupByNameFilter_rejects_blank_name(string name)
    {
        Assert.Throws<ArgumentException>(() => AdOwnershipFilter.BuildGroupByNameFilter(name));
    }

    [Fact]
    public void BuildUserByIdentityFilter_matches_the_three_identifiers_and_is_user_only()
    {
        var filter = AdOwnershipFilter.BuildUserByIdentityFilter("jane@contoso.com");
        Assert.Equal(
            "(&(objectCategory=person)(objectClass=user)(|(userPrincipalName=jane@contoso.com)(mail=jane@contoso.com)(sAMAccountName=jane@contoso.com)))",
            filter);
    }

    [Fact]
    public void BuildUserByIdentityFilter_is_bounded_to_users_not_groups()
    {
        // USER-ONLY membership (codex F7): the person/user bound must be present so a group or other
        // object with a colliding identity can never be resolved as a member here.
        var filter = AdOwnershipFilter.BuildUserByIdentityFilter("payroll");
        Assert.Contains("(objectCategory=person)", filter);
        Assert.Contains("(objectClass=user)", filter);
    }

    [Fact]
    public void BuildUserByIdentityFilter_escapes_wildcard_so_it_cannot_widen_the_match()
    {
        // A '*' in the identity must become a LITERAL, never an LDAP wildcard - otherwise "a*" would
        // match many users and the write could target the wrong principal.
        var filter = AdOwnershipFilter.BuildUserByIdentityFilter("a*");
        Assert.DoesNotContain("userPrincipalName=a*", filter);
        Assert.Contains("(userPrincipalName=a\\2a)", filter);
        Assert.Contains("(sAMAccountName=a\\2a)", filter);
    }

    [Fact]
    public void BuildUserByIdentityFilter_escapes_a_hostile_identity_so_structure_is_intact()
    {
        var hostile = "x)(objectClass=*)";
        var filter = AdOwnershipFilter.BuildUserByIdentityFilter(hostile);

        Assert.Contains("x\\29\\28objectClass=\\2a\\29", filter);
        // Only the builder's own structural parens are unescaped, exactly 7 pairs:
        // (&  (objectCategory=person)  (objectClass=user)  (|  (upn=..)  (mail=..)  (sam=..) ) )
        Assert.Equal(7, filter.Count(c => c == '('));
        Assert.Equal(7, filter.Count(c => c == ')'));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BuildUserByIdentityFilter_rejects_blank_identity(string identity)
    {
        Assert.Throws<ArgumentException>(() => AdOwnershipFilter.BuildUserByIdentityFilter(identity));
    }
}
