using ExchangeAdminWeb.Services;
using System.Text.Json;
using ExchangeAdminWeb.Modules;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Tests for the Cloud Password Reset destination decision
/// (docs/CloudPasswordReset-Plan.md, "Where the password goes").
/// </summary>
/// <remarks>
/// This is the branch that decides where an admin account's new password is emailed. It is
/// tested exhaustively rather than sampled, because a wrong answer here does not throw or fail a
/// build - it mails a credential to the wrong person and looks like a successful reset while
/// doing it.
/// </remarks>
public class CloudPasswordResetDestinationTests
{
    private static ADSearchResult Owner(string? email, string display = "Jo Owner") =>
        new(
            DisplayName: display,
            DistinguishedName: "CN=Jo,DC=corp,DC=test",
            SamAccountName: "jo",
            UserPrincipalName: "jo@corp.test",
            Email: email,
            ObjectType: "User");

    private static DirectoryValidationResult Found(ADSearchResult user, bool ambiguous = false) =>
        new(DirectoryLookupOutcome.Found, user, ambiguous);

    private static DirectoryValidationResult NotFound() =>
        new(DirectoryLookupOutcome.NotFound, null);

    private static DirectoryValidationResult Unavailable() =>
        new(DirectoryLookupOutcome.Unavailable, null);

    [Fact]
    public void Resolves_when_exactly_one_owner_has_a_mailbox()
    {
        var result = CloudPasswordResetService.ClassifyDestination("0001234", Found(Owner("jo@corp.test")));

        Assert.True(result.Resolved);
        Assert.Equal(CloudPasswordResetRefusal.None, result.Refusal);
        Assert.Equal("jo@corp.test", result.Address);
        Assert.Equal("0001234", result.EmployeeId);
    }

    [Fact]
    public void Lowercases_the_address_so_the_audit_and_the_send_cannot_differ_by_case()
    {
        var result = CloudPasswordResetService.ClassifyDestination("1", Found(Owner("Jo.Owner@Corp.Test")));

        Assert.Equal("jo.owner@corp.test", result.Address);
    }

    [Fact]
    public void Refuses_when_the_account_carries_no_employee_id()
    {
        var result = CloudPasswordResetService.ClassifyDestination(null, null);

        Assert.Equal(CloudPasswordResetRefusal.DestinationNoEmployeeId, result.Refusal);
        Assert.Null(result.Address);
    }

    [Fact]
    public void Treats_a_whitespace_employee_id_as_absent_rather_than_searching_for_it()
    {
        var result = CloudPasswordResetService.ClassifyDestination("   ", null);

        Assert.Equal(CloudPasswordResetRefusal.DestinationNoEmployeeId, result.Refusal);
    }

    [Fact]
    public void Refuses_when_the_employee_id_matches_nobody()
    {
        var result = CloudPasswordResetService.ClassifyDestination("0001234", NotFound());

        Assert.Equal(CloudPasswordResetRefusal.DestinationNoMatch, result.Refusal);
        Assert.Null(result.Address);
    }

    [Fact]
    public void Refuses_when_the_lookup_did_not_complete()
    {
        var result = CloudPasswordResetService.ClassifyDestination("0001234", Unavailable());

        Assert.Equal(CloudPasswordResetRefusal.DestinationLookupFailed, result.Refusal);
    }

    [Fact]
    public void A_failed_lookup_is_never_reported_as_no_match()
    {
        // The distinction this whole design rests on, and the one this repo got wrong on
        // 2026-09-22: "we looked and nobody is there" versus "we could not look". Collapsing them
        // turns an outage into what reads as a clean negative answer.
        var unavailable = CloudPasswordResetService.ClassifyDestination("1", Unavailable());
        var notFound = CloudPasswordResetService.ClassifyDestination("1", NotFound());

        Assert.NotEqual(notFound.Refusal, unavailable.Refusal);
        Assert.Equal(CloudPasswordResetRefusal.DestinationLookupFailed, unavailable.Refusal);
        Assert.Equal(CloudPasswordResetRefusal.DestinationNoMatch, notFound.Refusal);
    }

    [Fact]
    public void A_lookup_that_was_never_run_is_a_failure_not_an_absence()
    {
        // A null result means the caller skipped the query. Reading that as "nobody carries this
        // id" would let a code path that forgot to look report a clean refusal.
        var result = CloudPasswordResetService.ClassifyDestination("0001234", null);

        Assert.Equal(CloudPasswordResetRefusal.DestinationLookupFailed, result.Refusal);
    }

    [Fact]
    public void Refuses_when_more_than_one_user_carries_the_employee_id()
    {
        var result = CloudPasswordResetService.ClassifyDestination("0001234", Found(Owner("jo@corp.test"), ambiguous: true));

        Assert.Equal(CloudPasswordResetRefusal.DestinationAmbiguous, result.Refusal);
        Assert.Null(result.Address);
    }

    [Fact]
    public void Ambiguity_outranks_the_mailbox_check()
    {
        // With two matches the module does not know whose mailbox it is looking at, so any
        // statement about that mailbox would be about an arbitrary one of them.
        var result = CloudPasswordResetService.ClassifyDestination("1", Found(Owner(email: null), ambiguous: true));

        Assert.Equal(CloudPasswordResetRefusal.DestinationAmbiguous, result.Refusal);
    }

    [Fact]
    public void Refuses_when_the_single_match_has_no_mailbox()
    {
        var result = CloudPasswordResetService.ClassifyDestination("0001234", Found(Owner(email: null)));

        Assert.Equal(CloudPasswordResetRefusal.DestinationNoMailbox, result.Refusal);
        Assert.Null(result.Address);
    }

    [Fact]
    public void Refuses_when_the_single_match_has_a_blank_mailbox()
    {
        var result = CloudPasswordResetService.ClassifyDestination("0001234", Found(Owner("   ")));

        Assert.Equal(CloudPasswordResetRefusal.DestinationNoMailbox, result.Refusal);
    }

    [Fact]
    public void Carries_the_employee_id_on_every_refusal_that_had_one()
    {
        // The audit needs to say WHY a password went where it went, or why it went nowhere. An
        // address with no id behind it cannot be traced back to the directory row that caused it.
        foreach (var lookup in new[] { NotFound(), Unavailable(), Found(Owner(null)), Found(Owner("a@b.test"), true) })
        {
            var result = CloudPasswordResetService.ClassifyDestination("0009999", lookup);

            Assert.NotEqual(CloudPasswordResetRefusal.None, result.Refusal);
            Assert.Equal("0009999", result.EmployeeId);
        }
    }

    [Fact]
    public void Resolved_is_true_only_when_an_address_was_produced()
    {
        foreach (var lookup in new DirectoryValidationResult?[] { null, NotFound(), Unavailable(), Found(Owner(null)), Found(Owner("a@b.test"), true) })
        {
            Assert.False(CloudPasswordResetService.ClassifyDestination("1", lookup).Resolved);
        }

        Assert.True(CloudPasswordResetService.ClassifyDestination("1", Found(Owner("a@b.test"))).Resolved);
    }

    [Fact]
    public void Never_returns_an_address_alongside_a_refusal()
    {
        // The two fields are mutually exclusive by contract. A caller that checks only one of
        // them must not be able to send to an address a refusal was issued for.
        foreach (var lookup in new DirectoryValidationResult?[] { null, NotFound(), Unavailable(), Found(Owner(null)), Found(Owner("a@b.test"), true) })
        {
            var result = CloudPasswordResetService.ClassifyDestination("1", lookup);

            Assert.True(result.Refusal == CloudPasswordResetRefusal.None || result.Address is null);
        }
    }

    [Fact]
    public void Trims_the_employee_id_before_using_it()
    {
        var result = CloudPasswordResetService.ClassifyDestination("  0001234  ", Found(Owner("jo@corp.test")));

        Assert.Equal("0001234", result.EmployeeId);
    }
}

/// <summary>
/// Tests for the sync-state read (review finding cpr-4).
/// </summary>
/// <remarks>
/// The property that decides whether an account is in scope at all. Reading it as two states
/// instead of three admitted a synced account whenever Graph answered 200 without projecting it.
/// </remarks>
public class CloudPasswordResetSyncStateTests
{
    private static JsonElement Json(string raw) => JsonDocument.Parse(raw).RootElement;

    [Fact]
    public void True_is_synced_and_therefore_out_of_scope()
    {
        Assert.Equal(
            CloudPasswordResetService.SyncState.Synced,
            CloudPasswordResetService.ClassifySyncState(Json("""{"onPremisesSyncEnabled": true}""")));
    }

    [Fact]
    public void Null_is_cloud_only_because_that_is_how_Graph_says_it()
    {
        // Graph returns null, never false, for an account that is not synced. Treating null as
        // unknown would refuse this module's entire population.
        Assert.Equal(
            CloudPasswordResetService.SyncState.CloudOnly,
            CloudPasswordResetService.ClassifySyncState(Json("""{"onPremisesSyncEnabled": null}""")));
    }

    [Fact]
    public void False_is_cloud_only_too_if_Graph_ever_sends_it()
    {
        Assert.Equal(
            CloudPasswordResetService.SyncState.CloudOnly,
            CloudPasswordResetService.ClassifySyncState(Json("""{"onPremisesSyncEnabled": false}""")));
    }

    [Fact]
    public void An_absent_property_is_unknown_and_must_not_read_as_cloud_only()
    {
        // The defect codex found. The property is in the $select, so its absence means the
        // projection did not happen - not that the account is cloud-only.
        Assert.Equal(
            CloudPasswordResetService.SyncState.Unknown,
            CloudPasswordResetService.ClassifySyncState(Json("""{"id": "abc"}""")));
    }

    [Fact]
    public void An_unexpected_kind_is_unknown()
    {
        foreach (var raw in new[]
                 {
                     """{"onPremisesSyncEnabled": "true"}""",
                     """{"onPremisesSyncEnabled": 1}""",
                     """{"onPremisesSyncEnabled": {}}""",
                     """{"onPremisesSyncEnabled": []}""",
                 })
        {
            Assert.Equal(
                CloudPasswordResetService.SyncState.Unknown,
                CloudPasswordResetService.ClassifySyncState(Json(raw)));
        }
    }

    [Fact]
    public void Only_a_definite_negative_admits_the_target()
    {
        // The property this guard exists to hold: nothing ambiguous ever reaches CloudOnly.
        foreach (var raw in new[]
                 {
                     """{}""",
                     """{"onPremisesSyncEnabled": "yes"}""",
                     """{"onPremisesSyncEnabled": 0}""",
                 })
        {
            Assert.NotEqual(
                CloudPasswordResetService.SyncState.CloudOnly,
                CloudPasswordResetService.ClassifySyncState(Json(raw)));
        }
    }
}

/// <summary>
/// Catalog and page tests for the Cloud Password Reset module
/// (docs/CloudPasswordReset-Plan.md, "Catalog descriptor" and the page slice).
/// </summary>
public class CloudPasswordResetCatalogTests
{
    private readonly ModuleCatalog _catalog = new();

    private static string PageText() =>
        File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "CloudPasswordReset.razor"));

    [Fact]
    public void Module_is_registered_and_optional()
    {
        var module = _catalog.GetById("CloudPasswordReset");

        Assert.NotNull(module);
        Assert.Equal("cloud-password-reset", module!.Route);
        Assert.Equal("Identity & Access", module.Category);
        Assert.False(module.EnabledByDefault);
        Assert.False(module.IsSystemModule);
        Assert.Equal("1.0.0", module.Version);
    }

    [Fact]
    public void Both_permissions_are_fail_closed()
    {
        // This module resets the password of accounts that are almost all administrative. A
        // permission here that fell back to AllowedGroups would hand the capability to everyone
        // the app has ever granted anything.
        var module = _catalog.GetById("CloudPasswordReset")!;

        Assert.Equal("CloudPasswordReset", module.MainPermission.PolicyAlias);
        Assert.True(module.MainPermission.FailClosed);

        var reveal = Assert.Single(module.GranularPermissions);
        Assert.Equal("Reveal", reveal.Name);
        Assert.Equal("CloudPasswordResetReveal", reveal.PolicyAlias);
        Assert.True(reveal.FailClosed);
    }

    [Fact]
    public void Declares_its_own_graph_secret_and_a_boolean_ticket_switch()
    {
        var module = _catalog.GetById("CloudPasswordReset")!;

        Assert.Contains(module.ConfigFields, f => f.Key == "GraphDelineaSecretId");

        // A true/false setting rendered as free text can be mistyped, and a mistyped security
        // switch silently does the wrong thing (owner ruling 2026-09-01).
        var ticketSwitch = Assert.Single(module.ConfigFields, f => f.Key == "ValidateTickets");
        Assert.Equal(ConfigFieldType.Boolean, ticketSwitch.FieldType);
        Assert.Equal("false", ticketSwitch.DefaultValue);
    }

    [Fact]
    public void Page_route_and_policy_match_the_descriptor()
    {
        var page = PageText();

        Assert.Contains("@page \"/cloud-password-reset\"", page);
        Assert.Contains("[Authorize(Policy = \"CloudPasswordReset\")]", page);
    }

    [Fact]
    public void Page_shows_its_module_version()
    {
        // Canonical rule, enforced by tools/validate-module-package.ps1.
        Assert.Contains("<ModuleVersion />", PageText());
    }

    [Fact]
    public void Page_rechecks_authorization_on_initialization()
    {
        var page = PageText();

        Assert.Contains("AuthorizeAsync(user, \"CloudPasswordReset\")", page);
        Assert.Contains("access-denied", page);
    }

    [Fact]
    public void Page_offers_NO_destination_control_of_any_kind()
    {
        // AC3. The whole point of deriving the destination is that the operator cannot choose it,
        // and a text box - even a disabled or read-only one - is the defect this design removes.
        var page = PageText();

        Assert.DoesNotContain("@bind=\"destinationInput\"", page);
        Assert.DoesNotContain("destinationAddress\"", page);
        Assert.DoesNotContain("Destination address</label>", page);
    }

    [Fact]
    public void Force_change_checkbox_defaults_to_checked()
    {
        // Owner ruling 2026-09-23. The password travels by email, so forcing a change makes it a
        // one-time handover rather than a standing credential sitting in a mailbox.
        var page = PageText();

        Assert.Contains("private bool forceChangeAtNextSignIn = true;", page);
    }

    [Fact]
    public void Clearing_the_checkbox_shows_the_owners_instruction_verbatim()
    {
        // The owner's own words, recorded as not-to-be-reworded in .agents/decisions.md.
        Assert.Contains(
            "This is a security risk. You MUST walk the user through a manual reset and confirm it's been reset before closing the ticket.",
            PageText());
    }

    [Fact]
    public void Page_has_no_write_path_yet()
    {
        // This slice is preflight only. A reset button here would mean the write landed without
        // its authorization re-check, protection flow, audit and notification.
        var page = PageText();

        Assert.DoesNotContain("ResetPasswordAsync", page);
    }

    [Fact]
    public void Page_never_renders_a_password()
    {
        var page = PageText();

        Assert.DoesNotContain("outcome.Password", page);
        Assert.DoesNotContain("@password", page);
    }
}
