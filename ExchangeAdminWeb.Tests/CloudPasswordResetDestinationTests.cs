using ExchangeAdminWeb.Services;

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
