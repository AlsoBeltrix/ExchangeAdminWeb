using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The cloud half of True Last Logon (docs/TrueLastLogon-Plan.md S2).
/// </summary>
/// <remarks>
/// What these protect is not the date, it is the trust level attached to an ABSENCE.
/// signInActivity under-reports - 9 of 557 on the source script's own re-check - so "no
/// sign-in" from it alone is not evidence of dormancy, and a module that renders it as though
/// it were is more dangerous than the script, because a UI looks authoritative.
/// </remarks>
public class CloudSignInAggregatorTests
{
    private static readonly DateTime Older = new(2026, 7, 1, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 9, 20, 14, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheAnswerIsTheLaterOfTheTwoSourcesInEachDirection()
    {
        // Neither source is a superset. signInActivity keeps dates older than the log's ~30
        // days; the log is live and sees sign-ins signInActivity has not aggregated yet. So the
        // rule is "later wins" per field, not "prefer one source".
        var result = CloudSignInAggregator.Combine(
            activity: new CloudSignInAnswer(true, Interactive: Older, NonInteractive: Newer),
            log: new CloudSignInAnswer(true, Interactive: Newer, NonInteractive: Older));

        Assert.Equal(Newer, result.Interactive);
        Assert.Equal(Newer, result.NonInteractive);
        Assert.Equal(CloudVerification.LogVerified, result.Verified);
    }

    [Fact]
    public void ANullFromOneSourceDoesNotSuppressARealDateFromTheOther()
    {
        // The direction that matters: treating null as a zero date would let the source with
        // nothing win, and the account would read as older than it is - or dormant.
        var result = CloudSignInAggregator.Combine(
            activity: new CloudSignInAnswer(true, Interactive: null, NonInteractive: null),
            log: new CloudSignInAnswer(true, Interactive: Newer, NonInteractive: null));

        Assert.Equal(Newer, result.Interactive);
        Assert.Equal(Newer, result.Latest);
        Assert.False(result.NoSignInReported);
    }

    [Theory]
    [InlineData(true, true, CloudVerification.LogVerified)]
    [InlineData(true, false, CloudVerification.ActivityOnly)]
    [InlineData(false, true, CloudVerification.LogOnly30d)]
    [InlineData(false, false, CloudVerification.Unverified)]
    public void TheTrustLevelStatesWhichSourcesActuallyAnswered(
        bool activityAnswered, bool logAnswered, CloudVerification expected)
    {
        var result = CloudSignInAggregator.Combine(
            new CloudSignInAnswer(activityAnswered, null, null),
            new CloudSignInAnswer(logAnswered, null, null));

        Assert.Equal(expected, result.Verified);
    }

    [Fact]
    public void AnsweringWithNothingAndFailingToAnswerAreDifferentFacts()
    {
        // A source that responded "no sign-ins" is evidence. A source that fell over is not,
        // and must not be counted as though it had reported an absence - that is how a Graph
        // outage turns into "this account is dormant".
        var reportedNothing = CloudSignInAggregator.Combine(
            new CloudSignInAnswer(true, null, null),
            new CloudSignInAnswer(true, null, null));

        Assert.True(reportedNothing.NoSignInReported);
        Assert.Equal(CloudVerification.LogVerified, reportedNothing.Verified);

        var nobodyAnswered = CloudSignInAggregator.Combine(
            CloudSignInAnswer.DidNotAnswer,
            CloudSignInAnswer.DidNotAnswer);

        Assert.Equal(CloudVerification.Unverified, nobodyAnswered.Verified);
        Assert.False(nobodyAnswered.NoSignInReported);
    }

    [Fact]
    public void AnAbsenceFromSignInActivityAloneIsNotVerifiedDormancy()
    {
        // The measured failure, encoded: signInActivity said nothing and the log was not
        // consulted. The absence is real as far as it goes and is NOT LogVerified, so a caller
        // that acts only on LogVerified cannot disable this account on this evidence.
        var result = CloudSignInAggregator.Combine(
            new CloudSignInAnswer(true, null, null),
            CloudSignInAnswer.DidNotAnswer);

        Assert.True(result.NoSignInReported);
        Assert.Equal(CloudVerification.ActivityOnly, result.Verified);
        Assert.NotEqual(CloudVerification.LogVerified, result.Verified);
    }

    [Fact]
    public void LatestIsTheLaterOfInteractiveAndNonInteractive()
    {
        // "Last seen in the cloud" is either kind. A non-interactive sign-in still means the
        // account is in use, which is precisely the case the script found signInActivity
        // missing.
        var result = CloudSignInAggregator.Combine(
            new CloudSignInAnswer(true, Interactive: Older, NonInteractive: Newer),
            CloudSignInAnswer.DidNotAnswer);

        Assert.Equal(Newer, result.Latest);
    }
}
