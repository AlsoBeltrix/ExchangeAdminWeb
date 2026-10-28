using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The forest-wide single-user lookups: the logon-name lookup the employee-ID fill-in needs, and the employee-ID lookup
/// it shares its per-domain loop with (docs/CloudPasswordResetEmployeeId-Plan.md S1, tests T1-T5).
/// </summary>
/// <remarks>
/// Behavioural, over <see cref="StandInForest"/>: the real service runs its real commands against stand-in sessions.
/// Every failure-semantics case runs against BOTH lookups. The plan's reason for one shared loop is that the two cannot
/// drift, and a guard that exercised only one of them could not see the other drift.
/// </remarks>
public class ForestUserLookupTests
{
    private const string DomainA = "one.example.test";
    private const string DomainB = "two.example.test";
    private const string Name = "jdoe";
    private const string Number = "0001234";

    public static TheoryData<string> BothLookups => new() { "logon-name", "employeeID" };

    public static TheoryData<string, StandInForest.Failure> BothLookupsAndBothFailures => new()
    {
        { "logon-name", StandInForest.Failure.Reports },
        { "logon-name", StandInForest.Failure.Throws },
        { "employeeID", StandInForest.Failure.Reports },
        { "employeeID", StandInForest.Failure.Throws },
    };

    private static DirectoryValidationResult Find(ADDirectorySearchService directory, string lookup, string? value = null) =>
        lookup == "logon-name"
            ? directory.FindUserByLogonName(value ?? Name)
            : directory.FindUserByEmployeeId(value ?? Number);

    private static string ExpectedFilter(string lookup) =>
        lookup == "logon-name" ? "(sAMAccountName=jdoe)" : "(employeeID=0001234)";

    /// <summary>The source with its comment lines dropped, so a guard reads what the code does, not what it says.</summary>
    private static string LookupCode() =>
        string.Join('\n', File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "ADEmployeeIdLookup.cs"))
            .Split('\n')
            .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    // ----- T1: the logon-name filter is an exact, escaped equality -----

    [Fact]
    public void The_logon_name_filter_is_an_exact_sAMAccountName_equality()
    {
        Assert.Equal("(sAMAccountName=jdoe)", ADDirectorySearchService.BuildLogonNameFilter("jdoe"));
    }

    [Theory]
    [InlineData("jd*", @"(sAMAccountName=jd\2a)")]
    [InlineData("jd(", @"(sAMAccountName=jd\28)")]
    [InlineData("jd)", @"(sAMAccountName=jd\29)")]
    [InlineData(@"jd\", @"(sAMAccountName=jd\5c)")]
    [InlineData("jd\u0000", @"(sAMAccountName=jd\00)")]
    [InlineData("*)(sAMAccountName=*", @"(sAMAccountName=\2a\29\28sAMAccountName=\2a)")]
    public void The_logon_name_filter_escapes_every_LDAP_special_character(string name, string expected)
    {
        // Unescaped, a * is a wildcard and a parenthesis closes the equality and opens another filter: a lookup that
        // answers for people the name does not name.
        Assert.Equal(expected, ADDirectorySearchService.BuildLogonNameFilter(name));
    }

    [Fact]
    public void A_name_carrying_a_wildcard_finds_only_that_literal_name_and_not_everyone_it_would_match()
    {
        var forest = new StandInForest(DomainA);
        forest.Add(DomainA, "jdoe", Number, "jo.doe@example.test");

        var result = forest.Service().FindUserByLogonName("jd*");

        // A directory reads an unescaped * as a wildcard. Unescaped, this would have found jdoe and offered his mailbox.
        Assert.Equal(DirectoryLookupOutcome.NotFound, result.Outcome);
        Assert.Equal(@"(sAMAccountName=jd\2a)", Assert.Single(forest.UserQueries).LdapFilter);
    }

    // ----- T2: every domain, each by name as its own server, never the global catalog -----

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void Every_domain_of_the_forest_is_searched_once_as_its_own_server_with_the_exact_filter(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainB, Name, Number, "jo.doe@example.test");

        var result = Find(forest.Service(), lookup);

        Assert.Equal(DirectoryLookupOutcome.Found, result.Outcome);
        Assert.Equal(["Get-ADForest", "Get-ADUser", "Get-ADUser"], forest.Commands);
        Assert.Equal([DomainA, DomainB], forest.UserQueries.Select(q => q.Server));
        Assert.All(forest.UserQueries, q => Assert.Equal(ExpectedFilter(lookup), q.LdapFilter));
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void The_global_catalog_is_never_queried(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainA, Name, Number, "jo.doe@example.test");

        Find(forest.Service(), lookup);

        // employeeID is not in the catalog's default partial attribute set: a catalog query matches nobody, silently,
        // and the owner's number would come back blank.
        Assert.NotEmpty(forest.UserQueries);
        Assert.DoesNotContain(forest.UserQueries, q =>
            q.Server.Contains(StandInForest.GlobalCatalog, StringComparison.OrdinalIgnoreCase)
            || q.Server.Contains(":3268", StringComparison.Ordinal));
    }

    [Fact]
    public void Both_lookups_run_through_the_one_per_domain_loop_and_it_names_each_domain_as_the_server()
    {
        var code = LookupCode();

        Assert.Contains("FindOneUserAcrossForest(employeeId, BuildEmployeeIdFilter, ", code, StringComparison.Ordinal);
        Assert.Contains("FindOneUserAcrossForest(logonName, BuildLogonNameFilter, ", code, StringComparison.Ordinal);

        // One Get-ADUser in the file. A second copy of the loop is how the two lookups would drift.
        Assert.Single(Regex.Matches(code, Regex.Escape("AddCommand(\"Get-ADUser\")")));

        var loop = AuditCategoryFilingTests.MethodBody(code, "private DirectoryValidationResult SearchEveryDomainForOneUser(");
        Assert.Contains("foreach (var domain in domains)", loop, StringComparison.Ordinal);
        Assert.Contains(".AddParameter(\"Server\", domain)", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalCatalog", loop, StringComparison.Ordinal);
        Assert.DoesNotContain("3268", loop, StringComparison.Ordinal);
    }

    // ----- T3: any one failure is Unavailable, never a partial answer and never NotFound -----

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void A_domain_that_reports_an_error_makes_the_lookup_unavailable_though_another_domain_matched(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainA, Name, Number, "jo.doe@example.test");
        forest.Fail(DomainB, StandInForest.Failure.Reports);

        var result = Find(forest.Service(), lookup);

        // A match in the domain that failed is exactly what would make the other domain's single hit ambiguous.
        Assert.Equal(DirectoryLookupOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Match);
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void A_domain_that_reports_an_error_before_the_match_is_reached_makes_the_lookup_unavailable(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Fail(DomainA, StandInForest.Failure.Reports);
        forest.Add(DomainB, Name, Number, "jo.doe@example.test");

        var result = Find(forest.Service(), lookup);

        Assert.Equal(DirectoryLookupOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Match);
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void An_error_where_nobody_matched_is_unavailable_and_never_not_found(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Fail(DomainB, StandInForest.Failure.Reports);

        var result = Find(forest.Service(), lookup);

        // "Nobody has it" is a fact about the directory; a domain that did not answer is not evidence of it.
        Assert.Equal(DirectoryLookupOutcome.Unavailable, result.Outcome);
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void A_domain_whose_query_throws_makes_the_lookup_unavailable(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainA, Name, Number, "jo.doe@example.test");
        forest.Fail(DomainB, StandInForest.Failure.Throws);

        var result = Find(forest.Service(), lookup);

        Assert.Equal(DirectoryLookupOutcome.Unavailable, result.Outcome);
        Assert.Null(result.Match);
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void A_forest_with_no_domains_is_unavailable_and_searches_nothing(string lookup)
    {
        var forest = new StandInForest();

        var result = Find(forest.Service(), lookup);

        Assert.Equal(DirectoryLookupOutcome.Unavailable, result.Outcome);
        Assert.Empty(forest.UserQueries);
    }

    [Theory]
    [MemberData(nameof(BothLookupsAndBothFailures))]
    public void A_forest_that_cannot_be_read_is_unavailable_and_searches_nothing(string lookup, StandInForest.Failure failure)
    {
        var forest = new StandInForest(DomainA) { ForestFailure = failure };
        forest.Add(DomainA, Name, Number, "jo.doe@example.test");

        var result = Find(forest.Service(), lookup);

        // Never a fallback to the local domain: it cannot see a second domain's match.
        Assert.Equal(DirectoryLookupOutcome.Unavailable, result.Outcome);
        Assert.Empty(forest.UserQueries);
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void An_unavailable_directory_answers_unavailable_without_a_search(string lookup)
    {
        var directory = ADDirectorySearchServiceTests.CreateService();

        Assert.Equal(DirectoryLookupOutcome.Unavailable, Find(directory, lookup).Outcome);
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void A_blank_value_is_never_searched_for(string lookup)
    {
        var forest = new StandInForest(DomainA);
        forest.Add(DomainA, Name, Number, "jo.doe@example.test");

        var result = Find(forest.Service(), lookup, "   ");

        Assert.Equal(DirectoryLookupOutcome.NotFound, result.Outcome);
        Assert.Empty(forest.Commands);
    }

    // ----- T4: two users per domain at most, and two anywhere is ambiguous -----

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void Every_domain_is_asked_for_two_users_at_most(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainA, Name, Number, "jo.doe@example.test");

        Find(forest.Service(), lookup);

        Assert.Equal(2, forest.UserQueries.Count);
        Assert.All(forest.UserQueries, q => Assert.Equal(2, q.ResultSetSize));
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void Matches_in_two_domains_are_ambiguous(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainA, Name, Number, "jo.doe@one.example.test");
        forest.Add(DomainB, Name, Number, "jo.doe@two.example.test");

        var result = Find(forest.Service(), lookup);

        Assert.Equal(DirectoryLookupOutcome.Found, result.Outcome);
        Assert.True(result.Ambiguous);
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void Two_matches_in_one_domain_are_ambiguous(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainA, Name, Number, "jo.doe@example.test");
        forest.Add(DomainA, Name, Number, "jo.doe2@example.test");

        var result = Find(forest.Service(), lookup);

        Assert.Equal(DirectoryLookupOutcome.Found, result.Outcome);
        Assert.True(result.Ambiguous);
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void A_single_match_anywhere_in_the_forest_is_found_and_unambiguous(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainB, Name, Number, "jo.doe@example.test");

        var result = Find(forest.Service(), lookup);

        Assert.Equal(DirectoryLookupOutcome.Found, result.Outcome);
        Assert.False(result.Ambiguous);
        Assert.NotNull(result.Match);
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void Nobody_matching_in_any_domain_is_not_found(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainA, "someone.else", "0009999", "someone@example.test");

        var result = Find(forest.Service(), lookup);

        Assert.Equal(DirectoryLookupOutcome.NotFound, result.Outcome);
        Assert.Equal(2, forest.UserQueries.Count);
    }

    // ----- T5: the match carries the employee ID; the existing lookup's tests and guards stay as they were -----

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void The_match_carries_the_users_employee_id_and_identity(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);
        var user = forest.Add(DomainB, Name, Number, "Jo.Doe@Example.Test", "Jo Doe");

        var match = Find(forest.Service(), lookup).Match;

        Assert.NotNull(match);
        Assert.Equal(Number, match.EmployeeId);
        Assert.Equal(Name, match.SamAccountName);
        Assert.Equal("Jo.Doe@Example.Test", match.Email);
        Assert.Equal("Jo Doe", match.DisplayName);
        Assert.Equal($"{Name}@{DomainB}", match.UserPrincipalName);
        Assert.Equal(Guid.Parse(user.ObjectGuid!), Guid.Parse(match.ObjectGuid!));
    }

    [Theory]
    [MemberData(nameof(BothLookups))]
    public void Every_query_asks_for_the_attributes_the_match_carries(string lookup)
    {
        var forest = new StandInForest(DomainA, DomainB);

        Find(forest.Service(), lookup);

        Assert.All(forest.UserQueries, q =>
        {
            foreach (var attribute in new[] { "employeeID", "mail", "ObjectGUID", "DisplayName" })
                Assert.Contains(attribute, q.Properties, StringComparer.OrdinalIgnoreCase);
        });
    }

    [Fact]
    public void A_user_who_carries_no_employee_id_comes_back_with_none()
    {
        var forest = new StandInForest(DomainA);
        forest.Add(DomainA, Name, employeeId: null, "jo.doe@example.test");

        var result = forest.Service().FindUserByLogonName(Name);

        Assert.Equal(DirectoryLookupOutcome.Found, result.Outcome);
        Assert.Null(result.Match!.EmployeeId);
    }

    [Fact]
    public void A_search_result_built_without_an_employee_id_carries_none()
    {
        // Optional with a null default, so every existing construction site is unaffected.
        var result = new ADSearchResult("A", "DN=A", "a", "a@x.test", "a@x.test", "User");

        Assert.Null(result.EmployeeId);
    }

    [Fact]
    public void The_logon_name_is_trimmed_before_it_is_searched_for()
    {
        var forest = new StandInForest(DomainA);
        forest.Add(DomainA, Name, Number, "jo.doe@example.test");

        var result = forest.Service().FindUserByLogonName("  jdoe  ");

        Assert.Equal(DirectoryLookupOutcome.Found, result.Outcome);
        Assert.Equal("(sAMAccountName=jdoe)", Assert.Single(forest.UserQueries).LdapFilter);
    }
}
