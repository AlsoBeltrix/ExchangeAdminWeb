using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The first tests to reach any of <c>MailboxPermissionService</c>'s decision logic. Before the
/// seam extraction this aggregation existed four times over, each copy inside a closure that
/// needed a live Exchange connection, so the service sat at 0% coverage - the two services that
/// grant and revoke access to other people's mailboxes were the least-tested code in the app.
///
/// What is proven here is the repo's Known Failure Class #2: a loop over N items must aggregate
/// per-item failures and never report blanket success.
/// </summary>
public class MailboxPermissionOutcomeTests
{
    private const string Target = "ceo@analog.com";
    private const string User = "jdoe@analog.com";

    // ---------------------------------------------------------------- The partial case

    [Fact]
    public void Grant_PartialSuccess_IsNotReportedAsFailure()
    {
        // The case that matters most and was previously untestable. FullAccess LANDED; reporting a
        // flat failure would send an operator to retry an operation that half-applied, and would
        // understate in the audit log what access now exists.
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [
            RightOutcome.Ok("FullAccess"),
            RightOutcome.Failed("SendAs", "insufficient rights")
        ]);

        Assert.False(result.Success);
        Assert.Contains("Partial", result.Message);
        Assert.Contains("FullAccess", result.Message);
        Assert.Contains("insufficient rights", result.Message);
    }

    [Fact]
    public void Grant_PartialSuccess_DetailNamesOnlyWhatWasApplied()
    {
        // Detail feeds the audit row. It must say what actually landed - not the attempted set,
        // which would record access that was never granted.
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [
            RightOutcome.Ok("FullAccess"),
            RightOutcome.Failed("SendAs", "boom")
        ]);

        Assert.Equal("FullAccess", result.Detail);
    }

    [Fact]
    public void Revoke_PartialSuccess_DetailNamesOnlyWhatWasRemoved()
    {
        // The mirror risk: an operator told "failed" when FullAccess was in fact revoked may
        // believe access still exists, and leave it in place.
        var result = MailboxPermissionOutcome.ForRevoke(Target, User, [
            RightOutcome.Failed("FullAccess", "not found"),
            RightOutcome.Ok("SendAs")
        ]);

        Assert.False(result.Success);
        Assert.Equal("SendAs", result.Detail);
        Assert.Contains("Partial", result.Message);
    }

    // ---------------------------------------------------------------- Total failure

    [Fact]
    public void Grant_AllRightsFail_IsAFailureWithEveryReason()
    {
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [
            RightOutcome.Failed("FullAccess", "denied"),
            RightOutcome.Failed("SendAs", "not found")
        ]);

        Assert.False(result.Success);
        // Both reasons survive: reporting only the first would hide half the diagnosis.
        Assert.Contains("denied", result.Message);
        Assert.Contains("not found", result.Message);
    }

    [Fact]
    public void Grant_SingleFailure_NamesTheMailbox()
    {
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [
            RightOutcome.Failed("FullAccess", "denied")
        ]);

        Assert.False(result.Success);
        Assert.Contains(Target, result.Message);
    }

    // ---------------------------------------------------------------- The empty case

    [Fact]
    public void Grant_NoRightsAttempted_IsAFailure()
    {
        // Behavior CHANGE, deliberate and the only one in this refactor. The old code hit the
        // success branch with an empty list and produced "has been granted  rights to ...",
        // telling an operator an empty operation had worked. The UI blocks this and the CSV path
        // rejects it earlier, so it was unreachable - but it is a blanket-success-on-nothing
        // report, which is the exact failure class this file exists to prevent.
        var result = MailboxPermissionOutcome.ForGrant(Target, User, []);

        Assert.False(result.Success);
        Assert.Contains("No permissions were specified", result.Message);
    }

    [Fact]
    public void Revoke_NoRightsAttempted_IsAFailure()
    {
        var result = MailboxPermissionOutcome.ForRevoke(Target, User, []);

        Assert.False(result.Success);
        Assert.Contains("No permissions were specified", result.Message);
    }

    // ---------------------------------------------------------------- Success

    [Fact]
    public void Grant_BothRights_ReportsBothAndOffersTheMailboxLink()
    {
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [
            RightOutcome.Ok("FullAccess"), RightOutcome.Ok("SendAs")
        ]);

        Assert.True(result.Success);
        Assert.Contains("FullAccess and SendAs", result.Message);
        Assert.Contains($"https://outlook.office.com/mail/{Target}/", result.Detail);
    }

    [Fact]
    public void Grant_OnPrem_OmitsTheCloudLink()
    {
        // An OWA link for an on-premises mailbox would send the operator somewhere that cannot
        // serve it.
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [RightOutcome.Ok("FullAccess")], onPrem: true);

        Assert.True(result.Success);
        Assert.Contains("(on-premises)", result.Message);
        Assert.DoesNotContain("outlook.office.com", result.Detail ?? "");
    }

    [Fact]
    public void Revoke_Success_NamesUserRightsAndMailbox()
    {
        var result = MailboxPermissionOutcome.ForRevoke(Target, User, [RightOutcome.Ok("FullAccess")]);

        Assert.True(result.Success);
        Assert.Contains("FullAccess", result.Message);
        Assert.Contains(User, result.Message);
        Assert.Contains(Target, result.Message);
    }

    [Fact]
    public void Revoke_OnPrem_IsMarkedOnPremises()
    {
        var result = MailboxPermissionOutcome.ForRevoke(Target, User, [RightOutcome.Ok("SendAs")], onPrem: true);

        Assert.True(result.Success);
        Assert.Contains("(on-premises)", result.Message);
    }

    // ---------------------------------------------------------------- Wording parity

    [Theory]
    [InlineData(false, "jdoe@analog.com has been granted FullAccess rights to ceo@analog.com")]
    [InlineData(true, "jdoe@analog.com has been granted FullAccess on ceo@analog.com (on-premises).")]
    public void Grant_WordingIsUnchangedByTheExtraction(bool onPrem, string expected)
    {
        // Pinned verbatim against what each path produced BEFORE the seam was extracted. The two
        // phrasings differ pointlessly, but operators and the audit log read these strings, so
        // normalizing them would be a visible behavior change and belongs in its own commit.
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [RightOutcome.Ok("FullAccess")], onPrem);

        Assert.Equal(expected, result.Message);
    }

    [Theory]
    [InlineData(false, "FullAccess rights removed for jdoe@analog.com on ceo@analog.com")]
    [InlineData(true, "FullAccess removed for jdoe@analog.com on ceo@analog.com (on-premises).")]
    public void Revoke_WordingIsUnchangedByTheExtraction(bool onPrem, string expected)
    {
        var result = MailboxPermissionOutcome.ForRevoke(Target, User, [RightOutcome.Ok("FullAccess")], onPrem);

        Assert.Equal(expected, result.Message);
    }

    [Theory]
    [InlineData(false, "Failed on ceo@analog.com: FullAccess: denied")]
    [InlineData(true, "Failed on ceo@analog.com (on-premises): FullAccess: denied")]
    public void Failure_WordingIsUnchangedByTheExtraction(bool onPrem, string expected)
    {
        var result = MailboxPermissionOutcome.ForGrant(Target, User, [RightOutcome.Failed("FullAccess", "denied")], onPrem);

        Assert.Equal(expected, result.Message);
    }
}
