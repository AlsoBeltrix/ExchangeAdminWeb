using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The on-prem half of True Last Logon (docs/TrueLastLogon-Plan.md S1).
/// </summary>
/// <remarks>
/// These assert one rule and one refusal: the answer is the MAXIMUM across every domain
/// controller, and a DC that did not answer is named rather than dropped. Both exist because
/// <c>lastLogon</c> does not replicate - each DC knows only the logons it personally handled -
/// so an incomplete sweep can report a stale date, or worse "never", for an active account.
/// </remarks>
public class OnPremLogonAggregatorTests
{
    private static DomainControllerLogon Answered(string dc, DateTime? when) => new(dc, when, null);

    private static DomainControllerLogon Failed(string dc, string why) => new(dc, null, why);

    [Fact]
    public void TheAnswerIsTheNewestAcrossEveryDomainController()
    {
        // The whole reason the sweep exists. dc-old answered first and answered honestly; its
        // date is simply not the user's last logon.
        var result = OnPremLogonAggregator.Aggregate(
        [
            Answered("dc-old", new DateTime(2026, 1, 5, 9, 0, 0, DateTimeKind.Utc)),
            Answered("dc-new", new DateTime(2026, 9, 28, 17, 30, 0, DateTimeKind.Utc)),
            Answered("dc-mid", new DateTime(2026, 6, 1, 8, 0, 0, DateTimeKind.Utc)),
        ]);

        Assert.Equal(new DateTime(2026, 9, 28, 17, 30, 0, DateTimeKind.Utc), result.LastLogon);
        Assert.Equal("dc-new", result.SourceDomainController);
        Assert.Equal(3, result.AnsweredCount);
        Assert.True(result.EveryDomainControllerAnswered);
    }

    [Fact]
    public void ADomainControllerThatDidNotAnswerIsNamedAndBreaksCompleteness()
    {
        // The failure this class exists to prevent: 2 of 3 DCs answered, so the date below is a
        // FLOOR. The unreachable one could hold something newer, and nothing in the date says so.
        var result = OnPremLogonAggregator.Aggregate(
        [
            Answered("dc-a", new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)),
            Failed("dc-b", "TCP 389 timed out"),
            Answered("dc-c", new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc)),
        ]);

        Assert.Equal(new DateTime(2026, 4, 1, 0, 0, 0, DateTimeKind.Utc), result.LastLogon);
        Assert.False(result.EveryDomainControllerAnswered);

        var skipped = Assert.Single(result.Skipped);
        Assert.Equal("dc-b", skipped.DomainController);
        Assert.Equal("TCP 389 timed out", skipped.Error);
    }

    [Fact]
    public void NoLogonFoundIsReportedWithItsCoverageRatherThanGatedOnACompleteSweep()
    {
        // Owner, 2026-09-30: "you will NEVER get a response from ALL domain controllers. that
        // cannot be a gate." An earlier version required a complete sweep before it would say
        // no logon was found, which in a global estate meant never - the module could not
        // answer the question it exists for.
        //
        // So the finding stands on its own and the COVERAGE travels with it. A partial sweep
        // still reports what it found; it just also reports what it could not reach, and the
        // caller has to show both.
        var partial = OnPremLogonAggregator.Aggregate(
        [
            Answered("dc-a", null),
            Answered("dc-b", null),
            Failed("dc-c", "unreachable"),
        ]);

        Assert.Null(partial.LastLogon);
        Assert.True(partial.NoLogonOnAnsweringDomainControllers);
        Assert.Equal(2, partial.AnsweredCount);
        Assert.Single(partial.Skipped);
        Assert.False(partial.EveryDomainControllerAnswered);
    }

    [Fact]
    public void ADomainControllerThatErroredIsSkippedEvenIfItCarriedADate()
    {
        // A DC that failed partway cannot be treated as having given its final word. Counting
        // its date would let a partial sweep report itself complete.
        var result = OnPremLogonAggregator.Aggregate(
        [
            new DomainControllerLogon("dc-flaky", new DateTime(2026, 9, 1, 0, 0, 0, DateTimeKind.Utc), "bind failed"),
            Answered("dc-ok", new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
        ]);

        Assert.Equal(new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc), result.LastLogon);
        Assert.Equal("dc-ok", result.SourceDomainController);
        Assert.Equal(1, result.AnsweredCount);
        Assert.False(result.EveryDomainControllerAnswered);
    }

    [Fact]
    public void NoDomainControllersAtAllIsNotEvidenceOfAnything()
    {
        // An empty sweep must not read as "never". AnsweredCount 0 is what stops it.
        var result = OnPremLogonAggregator.Aggregate([]);

        Assert.Null(result.LastLogon);
        Assert.Equal(0, result.AnsweredCount);
        Assert.False(result.NoLogonOnAnsweringDomainControllers);

        var nothing = OnPremLogonAggregator.Aggregate(null);
        Assert.Null(nothing.LastLogon);
        Assert.False(nothing.NoLogonOnAnsweringDomainControllers);
    }
}
