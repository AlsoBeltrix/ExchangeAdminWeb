using ExchangeAdminWeb.Middleware;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Http;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Behaviour of the page-load reporting that the status frame reads.
///
/// Worth saying why these tests can exist at all: the four previous attempts at this lived in
/// browser JavaScript, where nothing in this repo can execute them, so every guard was a source
/// scan that could only check that text was present. Each one passed while the feature was
/// visibly broken on the deployment. Moving the question to the server did not just fix the
/// defect - it made the behaviour testable for the first time.
/// </summary>
public class PageLoadTrackerTests
{
    [Fact]
    public void ARequestIsVisibleWhileItIsBeingServedAndGoneAfterwards()
    {
        var tracker = new PageLoadTracker();

        using (tracker.Begin("ANALOG\\mcoelho", "Mailbox Permissions"))
        {
            var load = Assert.Single(tracker.InFlightFor("ANALOG\\mcoelho"));
            Assert.Equal("Mailbox Permissions", load.Route);
        }

        Assert.Empty(tracker.InFlightFor("ANALOG\\mcoelho"));
    }

    [Fact]
    public void TheRequestIsForgottenEvenWhenTheRequestThrows()
    {
        var tracker = new PageLoadTracker();

        // The whole anti-stranding guarantee. Four browser-side versions of this feature left
        // a readout running forever because the retraction never came; here the retraction is
        // a `using`, so the only way to strand one is to not write the using at all.
        var threw = false;
        try
        {
            using (tracker.Begin("user", "Migration"))
            {
                throw new InvalidOperationException("the page blew up");
            }
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.True(threw);

        Assert.Empty(tracker.InFlightFor("user"));
    }

    [Fact]
    public void OneOperatorNeverSeesAnothersPageLoads()
    {
        var tracker = new PageLoadTracker();

        using var mine = tracker.Begin("alice", "Risky Users");
        using var theirs = tracker.Begin("bob", "Comms-10k");

        Assert.Equal("Risky Users", Assert.Single(tracker.InFlightFor("alice")).Route);
        Assert.Equal("Comms-10k", Assert.Single(tracker.InFlightFor("bob")).Route);
    }

    [Fact]
    public void TheSameOperatorCanHaveSeveralInFlightAtOnce()
    {
        var tracker = new PageLoadTracker();

        // Two tabs, or a navigation started before the previous one finished. Each scope ends
        // its own request and no other.
        var first = tracker.Begin("alice", "Migration");
        using var second = tracker.Begin("alice", "Service Health");

        Assert.Equal(2, tracker.InFlightFor("alice").Count);

        first.Dispose();

        Assert.Equal("Service Health", Assert.Single(tracker.InFlightFor("alice")).Route);
    }

    [Fact]
    public void DisposingTwiceEndsOneRequestNotTwo()
    {
        var tracker = new PageLoadTracker();
        var first = tracker.Begin("alice", "Migration");
        using var second = tracker.Begin("alice", "Service Health");

        first.Dispose();
        first.Dispose();

        Assert.Single(tracker.InFlightFor("alice"));
    }

    [Fact]
    public void ChangedFiresOnBeginAndOnEndWithTheOperatorsName()
    {
        var tracker = new PageLoadTracker();
        var heard = new List<string>();
        tracker.Changed += heard.Add;

        using (tracker.Begin("alice", "Migration"))
        {
        }

        // The frame renders on these, so both edges matter: without the end event the readout
        // would stay on screen until something else happened to redraw it.
        Assert.Equal(new[] { "alice", "alice" }, heard);
    }

    [Fact]
    public void ASubscriberThatThrowsDoesNotFailThePageRequest()
    {
        var tracker = new PageLoadTracker();
        tracker.Changed += _ => throw new InvalidOperationException("broken frame");

        // The request is the operator's actual work. The status frame is commentary on it and
        // must never be able to take it down.
        using (tracker.Begin("alice", "Migration"))
        {
        }

        Assert.Empty(tracker.InFlightFor("alice"));
    }

    [Fact]
    public void AnUnknownOperatorHasNothingInFlight()
    {
        var tracker = new PageLoadTracker();

        Assert.Empty(tracker.InFlightFor("nobody"));
        Assert.Empty(tracker.InFlightFor(""));
    }

    [Fact]
    public void ABlankUserOrRouteIsNotTracked()
    {
        var tracker = new PageLoadTracker();

        // Fail closed rather than filing a load under an empty key, which would show up in
        // every anonymous context at once.
        using var noUser = tracker.Begin("", "Migration");
        using var noRoute = tracker.Begin("alice", "   ");

        Assert.Empty(tracker.InFlightFor(""));
        Assert.Empty(tracker.InFlightFor("alice"));
    }

    [Theory]
    [InlineData("/mailbox-permissions", "Mailbox Permissions")]
    [InlineData("/ExchangeAdminWebDev/true-last-logon", "True Last Logon")]
    [InlineData("/module-config/Comms10k", "Comms10k")]
    [InlineData("/", "Home")]
    [InlineData("", "Home")]
    public void TheRouteIsRenderedAsSomethingTheOperatorRecognises(string path, string expected)
    {
        // "mailbox-permissions" is what the URL says; "Mailbox Permissions" is what the
        // operator clicked. The frame has to say the second one or it is not a status readout.
        Assert.Equal(expected, PageLoadMiddleware.DisplayRoute(new PathString(path)));
    }

    [Fact]
    public void ElapsedTimeIsDerivedFromTheStartStampRatherThanCounted()
    {
        var tracker = new PageLoadTracker();
        var before = DateTime.UtcNow;

        using var scope = tracker.Begin("alice", "Migration");

        var load = Assert.Single(tracker.InFlightFor("alice"));
        Assert.InRange(load.StartedUtc, before.AddSeconds(-1), DateTime.UtcNow.AddSeconds(1));

        // A start stamp cannot drift and cannot outlive the request that carries it. The
        // previous design accumulated elapsed time in a browser timer, which both could.
    }
}
