using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The combined answer for one person (docs/TrueLastLogon-Plan.md S3).
/// </summary>
/// <remarks>
/// These protect one distinction and it is the whole reason the module exists: "we did not
/// check" and "we checked and there was nothing" are DIFFERENT ANSWERS. The plan says so in as
/// many words - *"Not checked" and "Never" are different answers and must never share a
/// rendering* - and the cost of collapsing them is an active account read as dormant and
/// disabled.
/// </remarks>
public class TrueLastLogonCombinerTests
{
    private static readonly DateTime Older = new(2026, 6, 1, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Newer = new(2026, 9, 25, 17, 30, 0, DateTimeKind.Utc);

    private static OnPremLogonResult OnPrem(DateTime? when, string? dc, int answered = 5) =>
        new(when, dc, [], answered);

    private static CloudSignInResult Cloud(
        DateTime? interactive,
        DateTime? nonInteractive,
        CloudVerification verified) =>
        new(interactive, nonInteractive, verified);

    [Fact]
    public void TheAnswerIsTheLatestAcrossEverySourceAndNamesWhichOneHeldIt()
    {
        // The maximum, not a preference order: neither half is a superset of the other.
        var answer = TrueLastLogonCombiner.Combine(
            OnPrem(Older, "DC01"),
            Cloud(Newer, Older, CloudVerification.LogVerified));

        Assert.Equal(Newer, answer.LastLogon);
        Assert.Equal(LogonSource.CloudInteractive, answer.Source);
    }

    [Fact]
    public void AnOnPremLogonWinsWhenItIsTheLatestAndCarriesTheDomainControllerThatSawIt()
    {
        // lastLogon is the only source that sees on-prem-only activity. A cloud-preferring rule
        // would call a workstation user dormant, and the DC name travels because a result is
        // only as good as the DC that produced it.
        var answer = TrueLastLogonCombiner.Combine(
            OnPrem(Newer, "DC07"),
            Cloud(Older, null, CloudVerification.LogVerified));

        Assert.Equal(Newer, answer.LastLogon);
        Assert.Equal(LogonSource.OnPrem, answer.Source);
        Assert.Equal("DC07", answer.SourceDetail);
    }

    [Fact]
    public void ANonInteractiveSignInCanBeTheLatestAndIsNamedAsOne()
    {
        // The query that catches what signInActivity under-reports. If it could never surface as
        // the answer there would be no point running it.
        var answer = TrueLastLogonCombiner.Combine(
            OnPrem(Older, "DC01"),
            Cloud(Older, Newer, CloudVerification.LogVerified));

        Assert.Equal(Newer, answer.LastLogon);
        Assert.Equal(LogonSource.CloudNonInteractive, answer.Source);
    }

    [Fact]
    public void NeitherHalfRunningIsNotCheckedAndIsNeverAnAbsence()
    {
        // THE test. Both halves failed, so the module knows nothing. A page that renders this as
        // "Never" has invented evidence of dormancy out of an outage.
        var answer = TrueLastLogonCombiner.Combine(onPrem: null, cloud: null);

        Assert.True(answer.NothingWasChecked);
        Assert.False(answer.NoLogonFoundOnWhatWasChecked);
        Assert.Null(answer.LastLogon);
        Assert.Equal(LogonSource.None, answer.Source);
    }

    [Fact]
    public void AHalfThatAnsweredAndSawNothingIsCheckedAndIsNotTheSameAsNotChecked()
    {
        // The other side of the same coin: the sweep ran, the DCs answered, none had seen this
        // account. That IS evidence - as far as the coverage goes - and must not be flattened
        // into the unknown case above.
        var answer = TrueLastLogonCombiner.Combine(
            OnPrem(null, null),
            cloud: null);

        Assert.False(answer.NothingWasChecked);
        Assert.True(answer.NoLogonFoundOnWhatWasChecked);
        Assert.Null(answer.LastLogon);
    }

    [Fact]
    public void ASweepThatRanButWhereNoDomainControllerAnsweredStillCountsAsChecked()
    {
        // Coverage is REPORTED, never required (owner, 2026-09-30). The on-prem half ran and has
        // something to say about coverage even at zero answers; that is a different fact from
        // the sweep being unable to start, which arrives as null.
        var answer = TrueLastLogonCombiner.Combine(
            OnPrem(null, null, answered: 0),
            cloud: null);

        Assert.True(answer.OnPremChecked);
        Assert.False(answer.NothingWasChecked);
    }

    [Fact]
    public void AnUnverifiedCloudLookupWithNothingInItDidNotCheckTheCloud()
    {
        // Unverified means neither cloud source responded. Counting it as checked would let a
        // total Graph outage render as a half that looked and saw nothing.
        var answer = TrueLastLogonCombiner.Combine(
            onPrem: null,
            cloud: Cloud(null, null, CloudVerification.Unverified));

        Assert.False(answer.CloudChecked);
        Assert.True(answer.NothingWasChecked);
    }

    [Fact]
    public void AnUnverifiedCloudLookupThatStillCarriesADateCountsAsChecked()
    {
        // Reachable, not hypothetical. CloudSignInService marks the log as having answered only
        // when BOTH its queries did, so an interactive query that returned a real sign-in while
        // the non-interactive one and signInActivity both failed arrives here as Unverified
        // CARRYING A DATE. Without this the page would print a date and "Not checked" together.
        var answer = TrueLastLogonCombiner.Combine(
            onPrem: null,
            cloud: Cloud(Newer, null, CloudVerification.Unverified));

        Assert.True(answer.CloudChecked);
        Assert.False(answer.NothingWasChecked);
        Assert.Equal(Newer, answer.LastLogon);
        Assert.Equal(LogonSource.CloudInteractive, answer.Source);
    }

    [Fact]
    public void NothingWasCheckedAndADateCanNeverBothBeTrue()
    {
        // The invariant the case above protects, stated on its own so a future change to either
        // Checked flag has to break this rather than merely look reasonable.
        foreach (var cloud in new CloudSignInResult?[]
                 {
                     null,
                     Cloud(null, null, CloudVerification.Unverified),
                     Cloud(Newer, null, CloudVerification.Unverified),
                     Cloud(null, Newer, CloudVerification.ActivityOnly),
                 })
        {
            foreach (var onPrem in new OnPremLogonResult?[] { null, OnPrem(Older, "DC01"), OnPrem(null, null) })
            {
                var answer = TrueLastLogonCombiner.Combine(onPrem, cloud);

                Assert.False(answer.NothingWasChecked && answer.LastLogon != null,
                    "a date arrived from a source the answer claims was never checked");
            }
        }
    }
}
