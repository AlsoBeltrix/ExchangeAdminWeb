using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using static ExchangeAdminWeb.Services.EmployeeIdFillOutcome;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// T6: the name rule (docs/CloudPasswordResetEmployeeId-Plan.md 4.2 step 1), pure.
/// </summary>
/// <remarks>
/// The endings here are neutral test values. The deployment's own are a setting nobody but the person who switches the
/// fill-in on types (plan 4.3).
/// </remarks>
public class CloudPasswordResetOwnerNameTests
{
    private static string Name(string? upn, params string[] endings) =>
        CloudPasswordResetService.DeriveOwnerLogonName(upn, endings).Name;

    [Fact]
    public void A_configured_ending_is_removed_from_the_local_part()
    {
        Assert.Equal("jdoe", Name("jdoe-adm@tenant.example.test", "-adm"));
    }

    [Theory]
    [InlineData("JDOE-ADM@tenant.example.test", "-adm", "JDOE")]
    [InlineData("jdoe-Adm@tenant.example.test", "-ADM", "jdoe")]
    [InlineData("jdoe_ADM@tenant.example.test", "_adm", "jdoe")]
    public void The_ending_is_compared_case_insensitively(string upn, string ending, string expected)
    {
        Assert.Equal(expected, Name(upn, ending));
    }

    [Theory]
    [InlineData("jdoe-adm2@tenant.example.test", "jdoe-adm2")]
    [InlineData("jo-adm-doe@tenant.example.test", "jo-adm-doe")]
    [InlineData("-admjdoe@tenant.example.test", "-admjdoe")]
    public void An_ending_is_removed_only_at_the_end(string upn, string expected)
    {
        Assert.Equal(expected, Name(upn, "-adm"));
    }

    [Fact]
    public void An_ending_is_removed_once()
    {
        Assert.Equal("jdoe-adm", Name("jdoe-adm-adm@tenant.example.test", "-adm"));
    }

    [Theory]
    [InlineData("adm", "-adm")]
    [InlineData("-adm", "adm")]
    public void The_longest_matching_ending_wins_in_whatever_order_they_are_configured(string first, string second)
    {
        // Shortest-first would remove "adm" and leave "jdoe-", a logon name nobody holds - or worse, one somebody does.
        Assert.Equal("jdoe", Name("jdoe-adm@tenant.example.test", first, second));
    }

    [Fact]
    public void A_name_with_no_configured_ending_is_used_whole()
    {
        Assert.Equal("jdoe", Name("jdoe@tenant.example.test", "-adm", "_adm"));
    }

    [Fact]
    public void With_no_endings_configured_the_whole_local_part_is_the_name()
    {
        Assert.Equal("jdoe-adm", Name("jdoe-adm@tenant.example.test"));
        Assert.Equal("jdoe-adm", Name("jdoe-adm@tenant.example.test", "", "  "));
    }

    [Theory]
    [InlineData("jdoe@corp-adm", "jdoe")]
    [InlineData("jdoe-adm@x-adm.example.test", "jdoe")]
    public void The_domain_part_plays_no_part(string upn, string expected)
    {
        Assert.Equal(expected, Name(upn, "-adm"));
    }

    [Fact]
    public void Nothing_else_about_the_name_is_changed()
    {
        // No punctuation folding, name-order handling or numeric-suffix rules (J9).
        Assert.Equal("Jo.Doe_2", Name("Jo.Doe_2-adm@tenant.example.test", "-adm"));
    }

    [Theory]
    [InlineData("-adm@tenant.example.test")]
    [InlineData("@tenant.example.test")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void A_name_that_is_only_an_ending_or_nothing_at_all_is_blank(string? upn)
    {
        Assert.True(string.IsNullOrWhiteSpace(Name(upn, "-adm")));
    }

    [Fact]
    public void The_ending_removed_is_reported_for_the_log()
    {
        var (name, ending) = CloudPasswordResetService.DeriveOwnerLogonName("jdoe_ADM@tenant.example.test", ["-adm", "_adm"]);

        Assert.Equal("jdoe", name);
        Assert.Equal("_adm", ending);
    }
}

/// <summary>
/// T7: the fill-in classifier (docs/CloudPasswordResetEmployeeId-Plan.md 4.2, the outcome table), pure, every row.
/// </summary>
/// <remarks>
/// Tested exhaustively rather than sampled, for the reason the destination tests are: a wrong answer here does not throw,
/// it mails an admin password to the wrong person and saves that person's number on the account.
/// </remarks>
public class CloudPasswordResetFillClassifierTests
{
    private const string OwnerGuid = "6f1c2c7e-3b9a-4d55-9a51-1c2f0e4b7a10";
    private const string OtherGuid = "0b8f5f1e-2c3d-4e5f-8a9b-0c1d2e3f4a5b";

    private static ADSearchResult Person(
        string? employeeId = "0001234", string? mail = "jo.doe@example.test", string? guid = OwnerGuid,
        string sam = "jdoe", string display = "Jo Doe") =>
        new(display, $"CN={display},OU=Staff,DC=example,DC=test", sam, $"{sam}@example.test", mail, "User",
            ObjectGuid: guid, EmployeeId: employeeId);

    private static DirectoryValidationResult Found(ADSearchResult user, bool ambiguous = false) =>
        new(DirectoryLookupOutcome.Found, user, ambiguous);

    private static DirectoryValidationResult NotFound() => new(DirectoryLookupOutcome.NotFound, null);

    private static DirectoryValidationResult Unavailable() => new(DirectoryLookupOutcome.Unavailable, null);

    private static CloudPasswordDestination Classify(
        DirectoryValidationResult? owner, DirectoryValidationResult? number, string? name = "jdoe") =>
        CloudPasswordResetService.ClassifyEmployeeIdFill(name, owner, number);

    private static EmployeeIdFillOutcome Outcome(
        DirectoryValidationResult? owner, DirectoryValidationResult? number, string? name = "jdoe") =>
        Classify(owner, number, name).Fill!.Outcome;

    // ----- 1 -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Row1_a_blank_name_is_owner_not_found(string? name)
    {
        Assert.Equal(OwnerNotFound, Outcome(Found(Person()), Found(Person()), name));
    }

    // ----- 2 -----

    [Fact]
    public void Row2_an_owner_search_that_was_never_run_is_a_failed_lookup()
    {
        Assert.Equal(OwnerLookupFailed, Outcome(null, null));
    }

    [Fact]
    public void Row2_an_unavailable_owner_search_is_a_failed_lookup_and_never_owner_not_found()
    {
        var outcome = Outcome(Unavailable(), Found(Person()));

        Assert.Equal(OwnerLookupFailed, outcome);
        Assert.NotEqual(OwnerNotFound, outcome);
    }

    // ----- 3 -----

    [Fact]
    public void Row3_an_owner_search_that_found_nobody_is_owner_not_found()
    {
        Assert.Equal(OwnerNotFound, Outcome(NotFound(), null));
    }

    // ----- 4 -----

    [Fact]
    public void Row4_two_owners_are_ambiguous_whatever_else_is_true()
    {
        Assert.Equal(OwnerAmbiguous, Outcome(Found(Person(), ambiguous: true), Found(Person())));
        Assert.Equal(OwnerAmbiguous, Outcome(Found(Person(employeeId: null, mail: null, guid: null), ambiguous: true), null));
    }

    [Fact]
    public void A_search_that_says_found_but_names_nobody_is_no_answer()
    {
        Assert.Equal(OwnerLookupFailed, Outcome(new DirectoryValidationResult(DirectoryLookupOutcome.Found, null), null));
    }

    // ----- 5 -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Row5_an_owner_without_an_employee_id_is_reported_as_such(string? employeeId)
    {
        var fill = Classify(Found(Person(employeeId: employeeId)), null).Fill!;

        Assert.Equal(OwnerHasNoEmployeeId, fill.Outcome);
        Assert.Null(fill.EmployeeId);
        Assert.Equal(OwnerGuid, fill.SourceObjectGuid);
    }

    // ----- 6 -----

    [Fact]
    public void Row6_a_number_search_that_was_never_run_is_a_failed_lookup()
    {
        Assert.Equal(OwnerLookupFailed, Outcome(Found(Person()), null));
    }

    [Fact]
    public void Row6_an_unavailable_number_search_is_a_failed_lookup_and_never_a_mismatch()
    {
        var outcome = Outcome(Found(Person()), Unavailable());

        Assert.Equal(OwnerLookupFailed, outcome);
        Assert.NotEqual(EmployeeIdMismatch, outcome);
    }

    // ----- 7 -----

    [Fact]
    public void Row7_a_number_that_leads_to_nobody_is_a_mismatch()
    {
        Assert.Equal(EmployeeIdMismatch, Outcome(Found(Person()), NotFound()));
    }

    // ----- 8 -----

    [Fact]
    public void Row8_a_number_several_accounts_carry_is_shared_and_outranks_the_guid_and_mailbox_checks()
    {
        Assert.Equal(EmployeeIdShared, Outcome(Found(Person()), Found(Person(), ambiguous: true)));
        Assert.Equal(EmployeeIdShared, Outcome(Found(Person()), Found(Person(guid: OtherGuid, mail: null), ambiguous: true)));
    }

    // ----- 9 -----

    [Fact]
    public void Row9_a_number_that_leads_to_someone_else_is_a_mismatch()
    {
        Assert.Equal(EmployeeIdMismatch, Outcome(Found(Person()), Found(Person(guid: OtherGuid, sam: "someone.else"))));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void Row9_a_missing_guid_on_either_side_is_a_mismatch(string? missing)
    {
        Assert.Equal(EmployeeIdMismatch, Outcome(Found(Person(guid: missing)), Found(Person())));
        Assert.Equal(EmployeeIdMismatch, Outcome(Found(Person()), Found(Person(guid: missing))));
        Assert.Equal(EmployeeIdMismatch, Outcome(Found(Person(guid: missing)), Found(Person(guid: missing))));
    }

    [Fact]
    public void Row9_the_guid_check_outranks_the_mailbox_check()
    {
        Assert.Equal(EmployeeIdMismatch, Outcome(Found(Person()), Found(Person(guid: OtherGuid, mail: null))));
    }

    [Fact]
    public void Row9_one_guid_written_two_ways_is_one_account()
    {
        var upper = Found(Person(guid: OwnerGuid.ToUpperInvariant()));
        var braced = Found(Person(guid: "{" + OwnerGuid + "}"));

        Assert.Equal(Planned, Outcome(upper, braced));
    }

    // ----- 10 -----

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Row10_an_owner_without_a_mailbox_is_reported_as_such(string? mail)
    {
        Assert.Equal(OwnerHasNoMailbox, Outcome(Found(Person()), Found(Person(mail: mail))));
    }

    // ----- 11 -----

    [Fact]
    public void Row11_a_verified_owner_is_planned_with_the_mail_trimmed_and_lowercased()
    {
        var result = Classify(Found(Person(employeeId: " 0001234 ")), Found(Person(mail: "  Jo.Doe@Example.Test ")));

        Assert.True(result.Resolved);
        Assert.Equal(CloudPasswordResetRefusal.None, result.Refusal);
        Assert.Equal("jo.doe@example.test", result.Address);
        Assert.Equal("0001234", result.EmployeeId);
        Assert.Equal("Jo Doe", result.OwnerDisplayName);

        var fill = result.Fill!;
        Assert.Equal(Planned, fill.Outcome);
        Assert.Equal("0001234", fill.EmployeeId);
        Assert.Equal(OwnerGuid, fill.SourceObjectGuid);
        Assert.Equal("jdoe", fill.SourceLogonName);
        Assert.Equal("Jo Doe", fill.SourceDisplayName);
    }

    [Fact]
    public void The_address_comes_from_the_number_lookups_match_never_the_name_lookups()
    {
        var result = Classify(
            Found(Person(mail: "from.the.name@example.test")),
            Found(Person(mail: "from.the.number@example.test")));

        Assert.Equal("from.the.number@example.test", result.Address);
    }

    // ----- across the table -----

    [Fact]
    public void An_unavailable_search_at_either_step_never_reads_as_an_absence()
    {
        EmployeeIdFillOutcome[] absences = [OwnerNotFound, OwnerHasNoEmployeeId, EmployeeIdMismatch];

        Assert.DoesNotContain(Outcome(Unavailable(), Found(Person())), absences);
        Assert.DoesNotContain(Outcome(Found(Person()), Unavailable()), absences);
        Assert.Equal(OwnerLookupFailed, Outcome(Unavailable(), Found(Person())));
        Assert.Equal(OwnerLookupFailed, Outcome(Found(Person()), Unavailable()));
    }

    public static TheoryData<string> FailingRows => new()
    {
        "blank name", "owner search unavailable", "owner not found", "owner ambiguous", "owner without a number",
        "number search unavailable", "number leads nowhere", "number shared", "number leads elsewhere", "no mailbox",
    };

    private static CloudPasswordDestination FailingRow(string row) => row switch
    {
        "blank name" => Classify(Found(Person()), Found(Person()), name: " "),
        "owner search unavailable" => Classify(Unavailable(), null),
        "owner not found" => Classify(NotFound(), null),
        "owner ambiguous" => Classify(Found(Person(), ambiguous: true), null),
        "owner without a number" => Classify(Found(Person(employeeId: null)), null),
        "number search unavailable" => Classify(Found(Person()), Unavailable()),
        "number leads nowhere" => Classify(Found(Person()), NotFound()),
        "number shared" => Classify(Found(Person()), Found(Person(), ambiguous: true)),
        "number leads elsewhere" => Classify(Found(Person()), Found(Person(guid: OtherGuid))),
        "no mailbox" => Classify(Found(Person()), Found(Person(mail: null))),
        _ => throw new ArgumentOutOfRangeException(nameof(row)),
    };

    [Theory]
    [MemberData(nameof(FailingRows))]
    public void Every_outcome_but_planned_leaves_the_destination_exactly_as_a_missing_number_always_has(string row)
    {
        // So the page's unchanged gates answer it as before: today's refusal, word for word, for an operator who can open
        // the module but cannot see the password, and today's reveal for one who can (the owner's rule, 2026-10-27).
        var result = FailingRow(row);

        Assert.NotEqual(Planned, result.Fill!.Outcome);
        Assert.False(result.Resolved);
        Assert.Equal(CloudPasswordResetService.ClassifyDestination(null, null), result with { Fill = null });
    }

    [Fact]
    public void A_failure_before_a_single_owner_was_found_names_no_number_and_no_source()
    {
        foreach (var row in new[] { "blank name", "owner search unavailable", "owner not found", "owner ambiguous" })
        {
            var fill = FailingRow(row).Fill!;
            Assert.Null(fill.EmployeeId);
            Assert.Null(fill.SourceObjectGuid);
        }
    }

    [Fact]
    public void A_failure_after_the_owner_gave_a_number_still_names_the_number_and_its_source()
    {
        // employeeIdFillValue and employeeIdFillSource say which number was refused, and from whom.
        foreach (var row in new[] { "number search unavailable", "number leads nowhere", "number shared", "number leads elsewhere", "no mailbox" })
        {
            var fill = FailingRow(row).Fill!;
            Assert.Equal("0001234", fill.EmployeeId);
            Assert.Equal(OwnerGuid, fill.SourceObjectGuid);
        }
    }
}

/// <summary>
/// T8: the setting (docs/CloudPasswordResetEmployeeId-Plan.md 4.3): off by default, fail closed, read at operation time.
/// </summary>
public class CloudPasswordResetFillSettingTests
{
    private static CloudPasswordResetService.EmployeeIdFillSetting Parse(string? value, string? endings = null, bool readable = true) =>
        CloudPasswordResetService.ParseFillSetting(readable, value, endings);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void An_absent_or_blank_switch_is_off(string? value)
    {
        Assert.Equal(CloudPasswordResetService.FillSwitch.Off, Parse(value, "-adm").Switch);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("True")]
    [InlineData("TRUE")]
    [InlineData(" true ")]
    public void True_in_any_casing_is_on(string value)
    {
        Assert.Equal(CloudPasswordResetService.FillSwitch.On, Parse(value).Switch);
    }

    [Theory]
    [InlineData("false")]
    [InlineData("False")]
    [InlineData("FALSE")]
    public void False_is_off(string value)
    {
        Assert.Equal(CloudPasswordResetService.FillSwitch.Off, Parse(value).Switch);
    }

    [Theory]
    [InlineData("yes")]
    [InlineData("1")]
    [InlineData("on")]
    [InlineData("0")]
    [InlineData("no")]
    [InlineData("enabled")]
    [InlineData("true,")]
    public void Any_other_value_is_unreadable_and_never_on(string value)
    {
        // A mistyped security switch must not silently do the wrong thing (owner ruling 2026-09-01), in either direction.
        var setting = Parse(value, "-adm");

        Assert.Equal(CloudPasswordResetService.FillSwitch.Unreadable, setting.Switch);
        Assert.Empty(setting.Endings);
        Assert.Equal(value.Trim(), setting.RawSwitch);
    }

    [Fact]
    public void An_unreadable_config_is_unreadable_whatever_it_seemed_to_hold()
    {
        Assert.Equal(CloudPasswordResetService.FillSwitch.Unreadable, Parse("true", "-adm", readable: false).Switch);
    }

    [Fact]
    public void Endings_parse_on_commas_trimmed_with_blank_entries_dropped()
    {
        Assert.Equal(["-adm", "_adm", ".x"], Parse("true", " -adm, _adm ,, ,.x ").Endings);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" , ,")]
    public void No_configured_endings_are_none_because_there_is_no_default(string? raw)
    {
        Assert.Empty(Parse("true", raw).Endings);
    }

    // ----- through the service, over a real store -----

    [Fact]
    public void The_service_reads_an_absent_switch_as_off()
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest(),
            new Dictionary<string, string> { [CloudPasswordResetService.OwnerNameEndingsKey] = "-adm" });

        Assert.Equal(CloudPasswordResetService.FillSwitch.Off, harness.Service.ReadFillSetting().Switch);
    }

    [Fact]
    public void The_service_reads_a_stored_true_as_on_with_its_endings()
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest(), new Dictionary<string, string>
        {
            [CloudPasswordResetService.FillMissingEmployeeIdKey] = "TRUE",
            [CloudPasswordResetService.OwnerNameEndingsKey] = "-adm, _adm",
        });

        var setting = harness.Service.ReadFillSetting();

        Assert.Equal(CloudPasswordResetService.FillSwitch.On, setting.Switch);
        Assert.Equal(["-adm", "_adm"], setting.Endings);
    }

    [Fact]
    public void The_service_reads_a_mistyped_switch_as_unreadable()
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest(),
            new Dictionary<string, string> { [CloudPasswordResetService.FillMissingEmployeeIdKey] = "yes" });

        Assert.Equal(CloudPasswordResetService.FillSwitch.Unreadable, harness.Service.ReadFillSetting().Switch);
    }

    [Fact]
    public void The_service_reads_a_corrupt_module_config_as_unreadable()
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest(), corruptConfig: true);

        Assert.Equal(CloudPasswordResetService.FillSwitch.Unreadable, harness.Service.ReadFillSetting().Switch);
    }

    [Fact]
    public void The_setting_is_read_at_each_operation_and_never_cached()
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest());
        Assert.Equal(CloudPasswordResetService.FillSwitch.Off, harness.Service.ReadFillSetting().Switch);

        harness.ModuleConfig.SaveModuleConfig(CloudPasswordResetService.ModuleId,
            new Dictionary<string, string> { [CloudPasswordResetService.FillMissingEmployeeIdKey] = "true" });

        Assert.Equal(CloudPasswordResetService.FillSwitch.On, harness.Service.ReadFillSetting().Switch);
    }
}

/// <summary>
/// T9 and T10, and the fill-in end to end through <see cref="CloudPasswordResetService.DeriveDestination"/> over a
/// stand-in forest: the real service, its real lookups, a real config store.
/// </summary>
public class CloudPasswordResetFillDerivationTests
{
    private const string DomainA = "one.example.test";
    private const string DomainB = "two.example.test";
    private const string Number = "0001234";

    private static readonly Dictionary<string, string> SwitchedOn = new()
    {
        [CloudPasswordResetService.FillMissingEmployeeIdKey] = "true",
        [CloudPasswordResetService.OwnerNameEndingsKey] = "-adm, _adm",
    };

    private static CloudPasswordTarget Target(string? employeeId = null, string upn = "jdoe-adm@tenant.example.test", bool fromEntra = true) =>
        new("0f0f0f0f-1111-4222-8333-444444444444", upn, "Jo Doe (admin)", AccountEnabled: true, CloudOnly: true,
            employeeId, [], DirectoryRolesReadFailed: false, UserPrincipalNameFromEntra: fromEntra);

    /// <summary>Two domains; the owner, jdoe, in the second.</summary>
    private static StandInForest Forest(out StandInForest.User owner)
    {
        var forest = new StandInForest(DomainA, DomainB);
        owner = forest.Add(DomainB, "jdoe", Number, "Jo.Doe@Example.Test", "Jo Doe");
        return forest;
    }

    private static CloudPasswordDestination Derive(
        StandInForest forest, CloudPasswordTarget target, IReadOnlyDictionary<string, string>? config = null, bool corrupt = false)
    {
        using var harness = new CloudPasswordResetHarness(forest, config ?? SwitchedOn, corrupt);
        return harness.Service.DeriveDestination(target);
    }

    private static readonly CloudPasswordDestination Today = CloudPasswordResetService.ClassifyDestination(null, null);

    private static bool IsNameQuery(StandInForest.UserQuery query) =>
        query.LdapFilter.StartsWith("(sAMAccountName=", StringComparison.Ordinal);

    // ----- T9: an account that carries a number never reaches the owner lookup -----

    [Fact]
    public void T9_an_account_that_carries_a_number_never_reaches_the_owner_lookup()
    {
        var forest = Forest(out _);

        var destination = Derive(forest, Target(employeeId: Number));

        Assert.Equal(NotNeeded, destination.Fill!.Outcome);
        Assert.True(destination.Resolved);
        Assert.Equal("jo.doe@example.test", destination.Address);
        Assert.NotEmpty(forest.UserQueries);
        Assert.DoesNotContain(forest.UserQueries, IsNameQuery);
        Assert.All(forest.UserQueries, q => Assert.Equal("(employeeID=0001234)", q.LdapFilter));
    }

    [Fact]
    public void T9_a_number_that_matches_nobody_keeps_its_refusal_even_where_the_name_would_find_an_owner()
    {
        // The fill-in never replaces a number (plan section 5). The account's name points at jdoe, and that changes nothing.
        var forest = Forest(out _);

        var destination = Derive(forest, Target(employeeId: "0009999"));

        Assert.Equal(CloudPasswordResetRefusal.DestinationNoMatch, destination.Refusal);
        Assert.Equal(NotNeeded, destination.Fill!.Outcome);
        Assert.DoesNotContain(forest.UserQueries, IsNameQuery);
    }

    [Fact]
    public void T9_a_number_whose_search_fails_keeps_its_refusal_and_reads_no_name()
    {
        var forest = Forest(out _);
        forest.Fail(DomainA, StandInForest.Failure.Reports);

        var destination = Derive(forest, Target(employeeId: Number));

        Assert.Equal(CloudPasswordResetRefusal.DestinationLookupFailed, destination.Refusal);
        Assert.Equal(NotNeeded, destination.Fill!.Outcome);
        Assert.DoesNotContain(forest.UserQueries, IsNameQuery);
    }

    // ----- T10: switched off or unreadable, no query and today's destination -----

    public static TheoryData<string, string?, bool, EmployeeIdFillOutcome> OffOrUnreadable => new()
    {
        { "never set", null, false, Off },
        { "set false", "false", false, Off },
        { "mistyped", "yes", false, SettingUnreadable },
        { "config unreadable", "true", true, SettingUnreadable },
    };

    [Theory]
    [MemberData(nameof(OffOrUnreadable))]
    public void T10_with_the_fill_in_off_or_unreadable_no_query_runs_and_the_destination_is_todays(
        string state, string? value, bool corrupt, EmployeeIdFillOutcome expected)
    {
        var forest = Forest(out _);
        var config = new Dictionary<string, string> { [CloudPasswordResetService.OwnerNameEndingsKey] = "-adm" };
        if (value is not null)
            config[CloudPasswordResetService.FillMissingEmployeeIdKey] = value;

        var destination = Derive(forest, Target(), config, corrupt);

        Assert.True(forest.Commands.Count == 0, $"{state}: the directory was queried");
        Assert.Equal(expected, destination.Fill!.Outcome);
        Assert.Equal(Today, destination with { Fill = null });
    }

    // ----- switched on: the planned fill-in -----

    [Fact]
    public void A_verified_owner_gives_a_destination_derived_from_the_owners_number()
    {
        var forest = Forest(out var owner);

        var destination = Derive(forest, Target(upn: "JDOE_ADM@tenant.example.test"));

        Assert.True(destination.Resolved);
        Assert.Equal("jo.doe@example.test", destination.Address);
        Assert.Equal(Number, destination.EmployeeId);
        Assert.Equal("Jo Doe", destination.OwnerDisplayName);

        var fill = destination.Fill!;
        Assert.Equal(Planned, fill.Outcome);
        Assert.Equal(Number, fill.EmployeeId);
        Assert.Equal(Guid.Parse(owner.ObjectGuid!).ToString("D"), fill.SourceObjectGuid);
        Assert.Equal("jdoe", fill.SourceLogonName);
        Assert.Equal("Jo Doe", fill.SourceDisplayName);
    }

    [Fact]
    public void The_fill_in_asks_the_directory_about_one_name_and_one_number_and_nothing_else()
    {
        // One account per operation, no enumeration (module plan AC4a; this plan's AC-F9): each search is an exact
        // equality, run once per domain, at most two users per domain.
        var forest = Forest(out _);

        Derive(forest, Target(upn: "JDOE_ADM@tenant.example.test"));

        Assert.Equal(
            ["(sAMAccountName=JDOE)", "(sAMAccountName=JDOE)", "(employeeID=0001234)", "(employeeID=0001234)"],
            forest.UserQueries.Select(q => q.LdapFilter));
        Assert.Equal([DomainA, DomainB, DomainA, DomainB], forest.UserQueries.Select(q => q.Server));
        Assert.All(forest.UserQueries, q => Assert.Equal(2, q.ResultSetSize));
    }

    // ----- switched on: every failed lookup is today's destination, and says why -----

    [Fact]
    public void A_name_that_names_nobody_is_owner_not_found_and_no_number_is_searched()
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainA, "someone.else", Number, "someone@example.test");

        var destination = Derive(forest, Target());

        Assert.Equal(OwnerNotFound, destination.Fill!.Outcome);
        Assert.Equal(Today, destination with { Fill = null });
        Assert.All(forest.UserQueries, q => Assert.True(IsNameQuery(q)));
    }

    [Fact]
    public void A_name_held_in_two_domains_is_ambiguous_and_no_number_is_searched()
    {
        var forest = Forest(out _);
        forest.Add(DomainA, "jdoe", "0005678", "j.doe@example.test", "J Doe");

        var destination = Derive(forest, Target());

        Assert.Equal(OwnerAmbiguous, destination.Fill!.Outcome);
        Assert.Equal(Today, destination with { Fill = null });
        Assert.All(forest.UserQueries, q => Assert.True(IsNameQuery(q)));
    }

    [Fact]
    public void An_owner_without_a_number_is_reported_and_no_number_is_searched()
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainB, "jdoe", employeeId: null, "jo.doe@example.test");

        var destination = Derive(forest, Target());

        Assert.Equal(OwnerHasNoEmployeeId, destination.Fill!.Outcome);
        Assert.Equal(Today, destination with { Fill = null });
        Assert.All(forest.UserQueries, q => Assert.True(IsNameQuery(q)));
    }

    [Fact]
    public void A_number_another_account_also_carries_is_shared()
    {
        var forest = Forest(out _);
        forest.Add(DomainA, "jdoe.admin", Number, "jdoe.admin@example.test");

        var destination = Derive(forest, Target());

        Assert.Equal(EmployeeIdShared, destination.Fill!.Outcome);
        Assert.Equal(Number, destination.Fill.EmployeeId);
        Assert.Equal(Today, destination with { Fill = null });
    }

    [Fact]
    public void An_owner_without_a_mailbox_is_reported()
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainB, "jdoe", Number, mail: null);

        var destination = Derive(forest, Target());

        Assert.Equal(OwnerHasNoMailbox, destination.Fill!.Outcome);
        Assert.Equal(Today, destination with { Fill = null });
    }

    [Fact]
    public void An_owner_who_answers_without_a_guid_cannot_be_shown_to_be_the_numbers_carrier()
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(new StandInForest.User(DomainB, "jdoe", Number, "jo.doe@example.test", ObjectGuid: null, "Jo Doe"));

        var destination = Derive(forest, Target());

        Assert.Equal(EmployeeIdMismatch, destination.Fill!.Outcome);
        Assert.Equal(Today, destination with { Fill = null });
    }

    [Fact]
    public void A_domain_that_fails_during_the_owner_search_is_a_failed_lookup_and_no_number_is_searched()
    {
        var forest = Forest(out _);
        forest.Fail(DomainA, StandInForest.Failure.Reports);

        var destination = Derive(forest, Target());

        Assert.Equal(OwnerLookupFailed, destination.Fill!.Outcome);
        Assert.Equal(Today, destination with { Fill = null });
        Assert.All(forest.UserQueries, q => Assert.True(IsNameQuery(q)));
    }

    [Fact]
    public void A_domain_that_fails_during_the_number_search_is_a_failed_lookup()
    {
        var forest = Forest(out _);
        forest.QueryFailure = q => q.LdapFilter.StartsWith("(employeeID=", StringComparison.Ordinal) && q.Server == DomainB
            ? StandInForest.Failure.Throws
            : StandInForest.Failure.None;

        var destination = Derive(forest, Target());

        Assert.Equal(OwnerLookupFailed, destination.Fill!.Outcome);
        Assert.Equal(Number, destination.Fill.EmployeeId);
        Assert.Equal(Today, destination with { Fill = null });
    }

    [Fact]
    public void A_name_that_is_only_an_ending_names_nobody_and_runs_no_query_at_all()
    {
        var forest = Forest(out _);

        var destination = Derive(forest, Target(upn: "-ADM@tenant.example.test"));

        Assert.Equal(OwnerNotFound, destination.Fill!.Outcome);
        Assert.Empty(forest.Commands);
        Assert.Equal(Today, destination with { Fill = null });
    }

    [Fact]
    public void A_name_entra_did_not_return_is_never_read_for_an_owner()
    {
        // The name the fill-in uses is Entra's, never the operator's typed text (plan 4.2 step 1).
        var forest = Forest(out _);

        var destination = Derive(forest, Target(fromEntra: false));

        Assert.Equal(OwnerNotFound, destination.Fill!.Outcome);
        Assert.Empty(forest.Commands);
        Assert.Equal(Today, destination with { Fill = null });
    }

    // ----- deriving is read-only, whatever it finds -----

    public static TheoryData<string> Derivations => new() { "stamped", "planned", "owner not found" };

    [Theory]
    [MemberData(nameof(Derivations))]
    public void Deriving_a_destination_never_writes_to_entra(string derivation)
    {
        // The preflight and the confirm's re-derive write nothing (plan 4.1); the save is SetEmployeeIdAsync, and only
        // the confirmed reset calls it. An account that carries a number gets no save at all (AC-F1).
        var forest = derivation == "owner not found" ? new StandInForest(DomainA, DomainB) : Forest(out _);
        using var harness = new CloudPasswordResetHarness(forest, SwitchedOn);

        harness.Service.DeriveDestination(Target(employeeId: derivation == "stamped" ? Number : null));

        Assert.Empty(harness.Graph.Requests);
    }

    [Fact]
    public void The_derive_path_calls_no_write_and_builds_no_graph_client()
    {
        // The behavioural test above cannot see a save attempted without the module's Graph secret, which stops before any
        // request. So the derive path's own source must reach no write and no Graph client at all.
        var source = string.Join('\n',
            File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "CloudPasswordResetService.cs"))
                .Split('\n')
                .Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

        foreach (var signature in new[]
                 {
                     "public CloudPasswordDestination DeriveDestination(",
                     "private CloudPasswordDestination FillMissingEmployeeId(",
                     "internal static CloudPasswordDestination ClassifyEmployeeIdFill(",
                 })
        {
            var body = AuditCategoryFilingTests.MethodBody(source, signature);
            foreach (var write in new[] { "SetEmployeeIdAsync(", "SendEmployeeIdAsync(", "ResetPasswordAsync(", "PatchWithStatusAsync(", "GetGraphClientAsync(" })
                Assert.DoesNotContain(write, body, StringComparison.Ordinal);
        }
    }

    // ----- the log: a failed search and an empty one stay distinguishable by level -----

    [Fact]
    public void A_search_that_failed_logs_at_error_and_one_that_found_nobody_at_warning()
    {
        var failed = Forest(out _);
        failed.Fail(DomainB, StandInForest.Failure.Reports);
        using var failedHarness = new CloudPasswordResetHarness(failed, SwitchedOn);
        failedHarness.Service.DeriveDestination(Target());

        var empty = new StandInForest(DomainA, DomainB);
        using var emptyHarness = new CloudPasswordResetHarness(empty, SwitchedOn);
        emptyHarness.Service.DeriveDestination(Target());

        Assert.Contains(failedHarness.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("did not complete", StringComparison.Ordinal));
        Assert.Contains(emptyHarness.Log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(nameof(OwnerNotFound), StringComparison.Ordinal));
        Assert.DoesNotContain(emptyHarness.Log.Entries, e => e.Level >= LogLevel.Error);
    }

    [Fact]
    public void A_mistyped_switch_logs_at_error()
    {
        using var harness = new CloudPasswordResetHarness(Forest(out _),
            new Dictionary<string, string> { [CloudPasswordResetService.FillMissingEmployeeIdKey] = "yes" });

        harness.Service.DeriveDestination(Target());

        // The value is named, as the ValidateTickets switch names it, so the fix is obvious from the log line.
        Assert.Contains(harness.Log.Entries, e => e.Level == LogLevel.Error && e.Message.Contains("(yes)", StringComparison.Ordinal));
    }
}

/// <summary>
/// T11: the save (docs/CloudPasswordResetEmployeeId-Plan.md 4.4), over a recording handler behind GraphTokenClient, the
/// GraphTokenClientTests shape.
/// </summary>
public class CloudPasswordResetEmployeeIdSaveTests
{
    private const string ObjectId = "0f0f0f0f-1111-4222-8333-444444444444";

    private const string GraphErrorBody =
        """{"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation."}}""";

    [Fact]
    public async Task The_save_is_one_PATCH_to_the_object_id_and_never_the_upn()
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest());

        await harness.Service.SendEmployeeIdAsync(harness.GraphClient(), ObjectId, "0001234");

        var request = Assert.Single(harness.Graph.Requests);
        Assert.Equal(HttpMethod.Patch, request.Method);
        Assert.Equal($"https://graph.microsoft.com/v1.0/users/{ObjectId}", request.Uri.AbsoluteUri);
    }

    [Theory]
    [InlineData("0001234")]
    [InlineData("00042")]
    [InlineData("A-1/2 x")]
    public async Task The_body_is_exactly_one_property_the_employee_id_byte_for_byte(string employeeId)
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest());

        await harness.Service.SendEmployeeIdAsync(harness.GraphClient(), ObjectId, employeeId);

        using var body = JsonDocument.Parse(Assert.Single(harness.Graph.Requests).Body!);
        var property = Assert.Single(body.RootElement.EnumerateObject());
        Assert.Equal("employeeId", property.Name);
        Assert.Equal(JsonValueKind.String, property.Value.ValueKind);
        Assert.Equal(employeeId, property.Value.GetString());
    }

    [Theory]
    [InlineData(HttpStatusCode.NoContent)]
    [InlineData(HttpStatusCode.OK)]
    public async Task A_success_status_is_written(HttpStatusCode status)
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest());
        harness.Graph.Respond = _ => new HttpResponseMessage(status);

        var result = await harness.Service.SendEmployeeIdAsync(harness.GraphClient(), ObjectId, "0001234");

        Assert.Equal(Written, result.Outcome);
        Assert.Equal(status, result.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.BadRequest)]
    [InlineData(HttpStatusCode.Forbidden)]
    public async Task A_refusal_is_rejected_carrying_its_status_and_no_response_body(HttpStatusCode status)
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest());
        harness.Graph.Respond = _ => new HttpResponseMessage(status) { Content = new StringContent(GraphErrorBody) };

        var result = await harness.Service.SendEmployeeIdAsync(harness.GraphClient(), ObjectId, "0001234");

        Assert.Equal(WriteRejected, result.Outcome);
        Assert.Equal(status, result.Status);
        Assert.Contains(((int)status).ToString(CultureInfo.InvariantCulture), result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Insufficient privileges", result.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("Authorization_RequestDenied", result.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string> LostInFlight => new() { "connection dropped", "timed out" };

    [Theory]
    [MemberData(nameof(LostInFlight))]
    public async Task A_throw_after_the_request_left_is_indeterminate_and_never_rejected(string failure)
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest());
        harness.Graph.Respond = _ =>
        {
            if (failure == "timed out")
                throw new TaskCanceledException("The request was canceled due to the configured HttpClient.Timeout.");
            throw new HttpRequestException("The connection was reset.");
        };

        var result = await harness.Service.SendEmployeeIdAsync(harness.GraphClient(), ObjectId, "0001234");

        // The request left (the cpr-8 lesson): "not saved" would be a claim nothing supports.
        Assert.Single(harness.Graph.Requests);
        Assert.Equal(WriteIndeterminate, result.Outcome);
        Assert.NotEqual(WriteRejected, result.Outcome);
        Assert.Null(result.Status);
        Assert.DoesNotContain("not saved", result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Without_the_modules_graph_credential_nothing_is_sent_and_the_number_is_not_saved()
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest());

        var result = await harness.Service.SetEmployeeIdAsync(ObjectId, "0001234");

        Assert.Equal(WriteRejected, result.Outcome);
        Assert.Null(result.Status);
        Assert.Empty(harness.Graph.Requests);
    }

    [Theory]
    [InlineData("", "0001234")]
    [InlineData(ObjectId, "")]
    [InlineData("  ", "  ")]
    public async Task A_blank_account_or_number_sends_nothing(string objectId, string employeeId)
    {
        using var harness = new CloudPasswordResetHarness(new StandInForest(),
            new Dictionary<string, string> { ["GraphDelineaSecretId"] = "42" });

        var result = await harness.Service.SetEmployeeIdAsync(objectId, employeeId);

        Assert.Equal(WriteRejected, result.Outcome);
        Assert.Empty(harness.Graph.Requests);
    }

    [Fact]
    public async Task A_saved_number_is_logged_at_information_and_a_refused_one_at_error()
    {
        using var saved = new CloudPasswordResetHarness(new StandInForest());
        await saved.Service.SendEmployeeIdAsync(saved.GraphClient(), ObjectId, "0001234");

        using var refused = new CloudPasswordResetHarness(new StandInForest());
        refused.Graph.Respond = _ => new HttpResponseMessage(HttpStatusCode.Forbidden) { Content = new StringContent(GraphErrorBody) };
        await refused.Service.SendEmployeeIdAsync(refused.GraphClient(), ObjectId, "0001234");

        Assert.Contains(saved.Log.Entries, e => e.Level == LogLevel.Information);
        Assert.Contains(refused.Log.Entries, e => e.Level == LogLevel.Error);
    }
}

/// <summary>
/// T12 (docs/CloudPasswordResetEmployeeId-Plan.md AC-F12, Architectural Invariant 7): no production file this work
/// touches names this deployment's account-name endings or domains.
/// </summary>
public class CloudPasswordResetNeutralityTests
{
    // This deployment's names as the ported true-up script hard-codes them (plan section 1): its directory domain, its
    // cloud domain, its other forest domain and its two account-name endings. A test fixture is the one place they may
    // be written.
    private static readonly Regex DeploymentNames =
        new(@"analog|winroot|[-_]cld(?![a-z0-9])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static string Source(string file) =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile(file.Split('/')));

    private static string CodeOnly(string text) =>
        string.Join('\n', text.Split('\n').Where(line => !line.TrimStart().StartsWith("//", StringComparison.Ordinal)));

    [Theory]
    [InlineData("Services/CloudPasswordResetService.cs")]
    [InlineData("Services/ADEmployeeIdLookup.cs")]
    public void The_fill_in_code_names_none_of_this_deployments_endings_or_domains_even_in_comments(string file)
    {
        var hits = DeploymentNames.Matches(Source(file)).Select(m => m.Value).ToList();

        Assert.True(hits.Count == 0, $"{file} names this deployment: {string.Join(", ", hits)}");
    }

    [Fact]
    public void The_shared_search_service_names_none_of_them_in_code()
    {
        // Its comments explain this forest by name, which Invariant 7 exempts. Its code must not.
        var hits = DeploymentNames.Matches(CodeOnly(Source("Services/ADDirectorySearchService.cs"))).Select(m => m.Value).ToList();

        Assert.True(hits.Count == 0, $"ADDirectorySearchService.cs names this deployment in code: {string.Join(", ", hits)}");
    }

    [Fact]
    public void The_guard_sees_the_names_it_exists_to_refuse()
    {
        // A pattern that matched nothing would pass every file above.
        foreach (var sample in new[] { "ad.analog.com", "tenant.onmicrosoft.com is analog", "WINROOT", "jdoe-CLD", "jdoe_cld@x" })
            Assert.Matches(DeploymentNames, sample);

        Assert.DoesNotMatch(DeploymentNames, "jdoe-adm@tenant.example.test");
    }
}

/// <summary>
/// The published vocabulary the audit fields will carry (docs/CloudPasswordResetEmployeeId-Plan.md 4.3 and 4.8).
/// </summary>
public class CloudPasswordResetFillVocabularyTests
{
    [Fact]
    public void The_fill_outcomes_are_exactly_the_plans_closed_list()
    {
        // These names reach the audit log as employeeIdFill and are frozen once shipped; a member added or renamed here
        // without the plan's table is a Splunk search broken in silence.
        string[] closedList =
        [
            "NotNeeded", "NotAttempted", "Off", "SettingUnreadable", "OwnerNotFound", "OwnerAmbiguous", "OwnerLookupFailed",
            "OwnerHasNoEmployeeId", "EmployeeIdShared", "EmployeeIdMismatch", "OwnerHasNoMailbox", "Planned", "Written",
            "WriteRejected", "WriteIndeterminate",
        ];

        Assert.Equal(closedList.Order(StringComparer.Ordinal), Enum.GetNames<EmployeeIdFillOutcome>().Order(StringComparer.Ordinal));
    }

    [Fact]
    public void A_value_nobody_set_reads_as_not_attempted()
    {
        Assert.Equal(NotAttempted, default(EmployeeIdFillOutcome));
    }

    [Fact]
    public void The_setting_keys_are_the_plans()
    {
        Assert.Equal("FillMissingEmployeeId", CloudPasswordResetService.FillMissingEmployeeIdKey);
        Assert.Equal("OwnerNameEndings", CloudPasswordResetService.OwnerNameEndingsKey);
    }

    [Fact]
    public void No_public_method_of_the_service_takes_an_owner_or_an_address()
    {
        // AC-F8 (the module plan's AC3): no operator input reaches the number, the owner or the address. The destination
        // is derived from the account Entra returned, and the save takes only the account's id and the number the
        // fill-in found - there is nothing for a caller to pass wrongly.
        string[] forbidden = ["address", "destination", "owner", "recipient", "mail", "samaccountname", "logonname"];

        var parameters = typeof(CloudPasswordResetService)
            .GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.DeclaredOnly)
            .SelectMany(method => method.GetParameters().Select(p => $"{method.Name}({p.Name})"))
            .ToList();

        Assert.NotEmpty(parameters);
        Assert.DoesNotContain(parameters, p => forbidden.Any(word => p.Contains($"({word}", StringComparison.OrdinalIgnoreCase)));

        var derive = Assert.Single(typeof(CloudPasswordResetService).GetMethods(), m => m.Name == nameof(CloudPasswordResetService.DeriveDestination));
        Assert.Equal([typeof(CloudPasswordTarget)], derive.GetParameters().Select(p => p.ParameterType));
    }
}
