using System.Collections.Concurrent;
using System.Net;
using System.Security.Claims;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Middleware;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a29g: a page request is confirmed against the directory before the page is prerendered, so a
/// principal the revalidation refuses never gets the page - not its HTML, and not its initialization.
/// </summary>
/// <remarks>
/// <para>
/// The end-to-end tests start a real ASP.NET Core host on a loopback port with the app's own
/// middleware and a probe page that is what every page of the app is: a routed component declaring
/// InteractiveServer, prerendered by <c>MapRazorComponents</c> during the request, whose
/// <c>OnInitialized</c> leaves a mark. So "no page body and no OnInitialized side effect" is observed,
/// not inferred. The pipeline order is the production one; that Program.cs keeps it is the tripwire's
/// job.
/// </para>
/// <para>
/// Every directory answer comes from a substitute <see cref="IOperatorMembershipDirectory"/>; nothing
/// here reaches Active Directory. The access settings are the real services over a throwaway SQLite
/// file, and time is a manual clock.
/// </para>
/// </remarks>
public class PageRequestRevalidationTests
{
    internal const string ProbeRoute = "/aud-a29g-probe";
    private const string NotAPageRoute = "/aud-a29g-not-a-page";
    private const string NotAPageBody = "not a page";

    private const string DomainSid = "S-1-5-21-1000-2000-3000";
    private const string OperatorSid = DomainSid + "-1105";
    private const string OperatorName = @"CONTOSO\operator";
    private const string ColleagueSid = DomainSid + "-1106";
    private const string ColleagueName = @"CONTOSO\colleague";
    private const string DomainUsersSid = DomainSid + "-513";
    private const string SectionGroupSid = DomainSid + "-5001";
    private const string LaterGrantedGroupSid = DomainSid + "-5005";

    // For the tests that hold a directory read open while other steps run (aud-a29k): far above any
    // stall a loaded host has shown (about 10 s), so only a genuine hang can time them out.
    private static readonly TimeSpan SlowReadLookupTimeout = TimeSpan.FromMinutes(2);

    // ------------------------------------------------------------------ the page request, end to end

    [Theory]
    [InlineData(CircuitRevocationReason.MembershipChanged)]
    [InlineData(CircuitRevocationReason.AccountDisabled)]
    public async Task ARefusedPrincipal_GetsNoPageBodyAndThePageNeverInitializes(CircuitRevocationReason reason)
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        if (reason == CircuitRevocationReason.MembershipChanged)
            h.DirectoryReturns(enabled: true, DomainUsersSid);
        else
            h.DirectoryReturns(enabled: false, SectionGroupSid, DomainUsersSid);

        // The token still carries the section's group - the stale ticket this finding is about.
        await using var host = await h.StartHostAsync(Operator(SectionGroupSid, DomainUsersSid));
        var (status, body, cacheControl) = await host.GetAsync(ProbeRoute);

        Assert.True(status == HttpStatusCode.Forbidden, h.Log.Explain($"Expected 403, got {(int)status}."));
        Assert.DoesNotContain(PageRequestProbePage.Body, body, StringComparison.Ordinal);
        Assert.DoesNotContain("<!--Blazor:", body, StringComparison.Ordinal);
        Assert.Equal(0, h.Effects.Initialized);

        Assert.Contains(PageRequestRefusalMessages.Title, body, StringComparison.Ordinal);
        Assert.Contains(WebUtility.HtmlEncode(PageRequestRefusalMessages.For(reason)), body, StringComparison.Ordinal);
        Assert.Equal("no-store", cacheControl);

        var audit = Assert.Single(h.AuditEvents()).RootElement;
        Assert.Equal("SessionRevalidation", audit.GetProperty("action").GetString());
        Assert.Equal("Authentication", audit.GetProperty("category").GetString());
        Assert.Equal("Failed", audit.GetProperty("result").GetString());
        Assert.Equal("operator", audit.GetProperty("user").GetString());
        Assert.Equal(OperatorName, audit.GetProperty("target").GetString());
        Assert.Equal("127.0.0.1", audit.GetProperty("ip").GetString());
        Assert.Equal(reason.ToString(), audit.GetProperty("reason").GetString());
        Assert.Equal("PageRequest", audit.GetProperty("trigger").GetString());
        if (reason == CircuitRevocationReason.MembershipChanged)
            Assert.Equal(new[] { SectionGroupSid }, RemovedSids(audit));
        else
            Assert.False(audit.TryGetProperty("removedGroupSids", out _));

        Assert.Single(h.Log.Warnings, w => w.Contains("Page request for " + OperatorName + " refused", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheRefusalAuditLineNamesThePage_WithoutItsQueryString()
    {
        // aud-a29i. Several refused requests from one operator must be told apart by the page they
        // asked for - but the query string, where a page's search terms would be, stays out of the row.
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);
        await using var host = await h.StartHostAsync(Operator(SectionGroupSid, DomainUsersSid), pathBase: "/probe-base");

        var (status, _, _) = await host.GetAsync("/probe-base" + ProbeRoute + "?lookup=typed-by-the-operator");

        Assert.True(status == HttpStatusCode.Forbidden, h.Log.Explain($"Expected 403, got {(int)status}."));
        var audit = Assert.Single(h.AuditEvents()).RootElement;
        Assert.Equal("/probe-base" + ProbeRoute, audit.GetProperty("path").GetString());
        Assert.DoesNotContain("typed-by-the-operator", audit.GetRawText(), StringComparison.Ordinal);

        // Every field the row had before is still there.
        Assert.Equal("SessionRevalidation", audit.GetProperty("action").GetString());
        Assert.Equal("PageRequest", audit.GetProperty("trigger").GetString());
        Assert.Equal("MembershipChanged", audit.GetProperty("reason").GetString());
        Assert.Equal(new[] { SectionGroupSid }, RemovedSids(audit));
        Assert.Equal(OperatorName, audit.GetProperty("target").GetString());
    }

    [Fact]
    public void TheAuditPath_IsThePathBaseAndPath_EncodedAndBounded()
    {
        var request = new DefaultHttpContext().Request;
        request.PathBase = "/ExchangeAdminWeb";
        request.Path = "/risky-users";
        request.QueryString = new QueryString("?upn=someone");

        Assert.Equal("/ExchangeAdminWeb/risky-users", PageRequestRevalidationMiddleware.AuditPath(request));

        // A character that could break a log line arrives encoded; a long path is cut and marked.
        request.Path = "/line\nbreak/" + new string('x', 400);
        var bounded = PageRequestRevalidationMiddleware.AuditPath(request);

        Assert.StartsWith("/ExchangeAdminWeb/line%0Abreak/xxx", bounded, StringComparison.Ordinal);
        Assert.Equal(PageRequestRevalidationMiddleware.MaxAuditPathLength, bounded.Length);
        Assert.EndsWith("...", bounded, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', bounded);
        Assert.DoesNotContain('?', bounded);
    }

    [Fact]
    public async Task AnAcceptedPrincipal_GetsThePagePrerendered()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);

        await using var host = await h.StartHostAsync(Operator(SectionGroupSid, DomainUsersSid));
        var (status, body, _) = await host.GetAsync(ProbeRoute);

        Assert.True(status == HttpStatusCode.OK, h.Log.Explain($"Expected 200, got {(int)status}."));
        Assert.Contains(PageRequestProbePage.Body, body, StringComparison.Ordinal);

        // Prerendered as an interactive island - the path every page of the app takes - and initialized
        // exactly once, by that prerender.
        Assert.Contains("<!--Blazor:", body, StringComparison.Ordinal);
        Assert.Equal(1, h.Effects.Initialized);

        _ = h.Directory.Received(1).GetMembershipAsync(AnySid, AnyDomain, AnyToken);
        Assert.Empty(h.AuditEvents());
    }

    [Theory]
    [InlineData("the directory")]
    [InlineData("the access settings")]
    public async Task AnAccessCheckThatCannotRun_RefusesThePage(string unreadable)
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        if (unreadable == "the directory")
        {
            h.Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken)
                .ThrowsAsync(new DirectoryUnavailableException("no domain controller answered"));
        }
        else
        {
            h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);
            h.DropTable("section_access");
        }

        await using var host = await h.StartHostAsync(Operator(SectionGroupSid, DomainUsersSid));
        var (status, body, _) = await host.GetAsync(ProbeRoute);

        // Fail closed: no read possible is no page, and the operator is told so in neutral words.
        Assert.True(status == HttpStatusCode.Forbidden, h.Log.Explain($"Expected 403, got {(int)status}."));
        Assert.DoesNotContain(PageRequestProbePage.Body, body, StringComparison.Ordinal);
        Assert.Equal(0, h.Effects.Initialized);
        Assert.Contains(
            WebUtility.HtmlEncode(PageRequestRefusalMessages.For(CircuitRevocationReason.LookupFailed)), body, StringComparison.Ordinal);

        var audit = Assert.Single(h.AuditEvents()).RootElement;
        Assert.Equal("LookupFailed", audit.GetProperty("reason").GetString());
        Assert.Equal("PageRequest", audit.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task TheRecordedAnswerIsReusedInsideTheInterval()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);
        await using var host = await h.StartHostAsync(Operator(SectionGroupSid, DomainUsersSid));

        var first = await host.GetAsync(ProbeRoute);
        h.Clock.Advance(h.Options.Interval - TimeSpan.FromSeconds(1));
        var second = await host.GetAsync(ProbeRoute);

        // Both rendered; the second was confirmed by the first's answer, without a read of its own.
        Assert.True(first.Status == HttpStatusCode.OK && second.Status == HttpStatusCode.OK, h.Log.Explain("Expected two 200s."));
        Assert.Equal(2, h.Effects.Initialized);
        _ = h.Directory.Received(1).GetMembershipAsync(AnySid, AnyDomain, AnyToken);

        // One interval after that read started, its answer confirms nothing any more.
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var third = await host.GetAsync(ProbeRoute);

        Assert.Equal(HttpStatusCode.OK, third.Status);
        Assert.Equal(3, h.Effects.Initialized);
        _ = h.Directory.Received(2).GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task TheOffSwitch_LetsEveryPageThroughWithoutReadingAnything()
    {
        using var h = new Harness(enabled: false);
        h.Grant("MailboxPermissions", SectionGroupSid);

        // Enabled, this answer would refuse the page.
        h.DirectoryReturns(enabled: true, DomainUsersSid);
        await using var host = await h.StartHostAsync(Operator(SectionGroupSid, DomainUsersSid));
        var (status, body, _) = await host.GetAsync(ProbeRoute);

        Assert.True(status == HttpStatusCode.OK, h.Log.Explain($"Expected 200, got {(int)status}."));
        Assert.Contains(PageRequestProbePage.Body, body, StringComparison.Ordinal);
        Assert.Equal(1, h.Effects.Initialized);
        _ = h.Directory.DidNotReceive().GetMembershipAsync(AnySid, AnyDomain, AnyToken);
        h.Directory.DidNotReceiveWithAnyArgs().TranslateGroupName(default!);
        Assert.Empty(h.AuditEvents());
    }

    [Fact]
    public async Task ARequestThatRendersNoPage_IsNotChecked()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);
        await using var host = await h.StartHostAsync(Operator(SectionGroupSid, DomainUsersSid));

        // The same principal a page would be refused for; this endpoint is not a page.
        var (status, body, _) = await host.GetAsync(NotAPageRoute);

        Assert.True(status == HttpStatusCode.OK, h.Log.Explain($"Expected 200, got {(int)status}."));
        Assert.Equal(NotAPageBody, body);
        _ = h.Directory.DidNotReceive().GetMembershipAsync(AnySid, AnyDomain, AnyToken);
        Assert.Empty(h.AuditEvents());
    }

    [Fact]
    public async Task AnUnauthenticatedRequest_IsNotChecked()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);

        // Nothing to confirm: an anonymous principal carries no group. (In the app every page also
        // requires authorization, so such a request never reaches a page there.)
        await using var host = await h.StartHostAsync(user: null);
        var (status, body, _) = await host.GetAsync(ProbeRoute);

        Assert.True(status == HttpStatusCode.OK, h.Log.Explain($"Expected 200, got {(int)status}."));
        Assert.Contains(PageRequestProbePage.Body, body, StringComparison.Ordinal);
        _ = h.Directory.DidNotReceive().GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    // ------------------------------------------------------------------ the recorded answers

    [Fact]
    public async Task ARecordedAnswerNeverRefuses_RestoredAccessIsSeenAtTheNextRequest()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken).Returns(
            Task.FromResult(Membership(enabled: true, DomainUsersSid)),
            Task.FromResult(Membership(enabled: true, SectionGroupSid, DomainUsersSid)));
        var user = Operator(SectionGroupSid, DomainUsersSid);

        var refused = await h.Revalidator.RevalidatePageRequestAsync(user, Ct);

        // Access restored inside the interval. The recorded answer would refuse, so it is read again.
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        var restored = await h.Revalidator.RevalidatePageRequestAsync(user, Ct);
        var again = await h.Revalidator.RevalidatePageRequestAsync(user, Ct);

        Assert.Equal(CircuitRevocationReason.MembershipChanged, refused.Reason);
        Assert.True(restored.IsValid);
        Assert.True(again.IsValid);
        _ = h.Directory.Received(2).GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task ARecordedAnswerIsComparedWithTheSettingsAsTheyAreNow()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);

        // The token still carries a group the directory no longer has, which no setting grants yet.
        var user = Operator(SectionGroupSid, LaterGrantedGroupSid, DomainUsersSid);
        Assert.True((await h.Revalidator.RevalidatePageRequestAsync(user, Ct)).IsValid);

        // Granting it makes it consulted. An answer - not a verdict - was recorded, so the next request
        // compares the group too, and the refusal it leads to is a fresh read's.
        h.Grant("CalendarPermissions", LaterGrantedGroupSid);
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        var verdict = await h.Revalidator.RevalidatePageRequestAsync(user, Ct);

        Assert.Equal(CircuitRevocationReason.MembershipChanged, verdict.Reason);
        Assert.Equal(new[] { LaterGrantedGroupSid }, verdict.RemovedGroupSids);
        _ = h.Directory.Received(2).GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task TheCircuitChecksNeverUseARecordedAnswer()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);
        var user = Operator(SectionGroupSid, DomainUsersSid);

        Assert.True((await h.Revalidator.RevalidatePageRequestAsync(user, Ct)).IsValid);

        // aud-a29b's interval loop and aud-a29c's open check read the directory every time, however
        // fresh the recorded answer is.
        var revocation = new CircuitRevocation();
        using var provider = h.Provider(revocation);
        ((Microsoft.AspNetCore.Components.Authorization.IHostEnvironmentAuthenticationStateProvider)provider)
            .SetAuthenticationState(Task.FromResult(new Microsoft.AspNetCore.Components.Authorization.AuthenticationState(user)));

        Assert.True(await provider.ValidateAsync(user, "Interval", Ct));
        Assert.True(await provider.CheckAtCircuitOpenAsync());

        _ = h.Directory.Received(3).GetMembershipAsync(AnySid, AnyDomain, AnyToken);
        Assert.False(revocation.IsRevoked);
    }

    [Fact]
    public async Task AReadByACircuitCheck_ConfirmsTheNextPageRequest()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);
        var user = Operator(SectionGroupSid, DomainUsersSid);

        using var provider = h.Provider(new CircuitRevocation());
        Assert.True(await provider.ValidateAsync(user, "Interval", Ct));

        // An open tab's own check keeps its operator's page requests free.
        h.Clock.Advance(TimeSpan.FromSeconds(30));
        Assert.True((await h.Revalidator.RevalidatePageRequestAsync(user, Ct)).IsValid);
        _ = h.Directory.Received(1).GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task AFailedRead_ForgetsTheRecordedAnswer()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken).Returns(
            _ => Task.FromResult(Membership(enabled: true, SectionGroupSid, DomainUsersSid)),
            _ => throw new DirectoryUnavailableException("no domain controller answered"));
        var user = Operator(SectionGroupSid, DomainUsersSid);

        Assert.True((await h.Revalidator.RevalidatePageRequestAsync(user, Ct)).IsValid);

        // The directory stops answering; the tab's interval check finds out first.
        using var provider = h.Provider(new CircuitRevocation());
        Assert.False(await provider.ValidateAsync(user, "Interval", Ct));

        // Inside the interval of the good answer - but it is forgotten, so this request needs a read of
        // its own, and that read fails: refused.
        h.Clock.Advance(TimeSpan.FromSeconds(10));
        var verdict = await h.Revalidator.RevalidatePageRequestAsync(user, Ct);

        Assert.Equal(CircuitRevocationReason.LookupFailed, verdict.Reason);
        _ = h.Directory.Received(3).GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task AnAnswerFromAReadThatStartedEarlier_DoesNotReplaceALaterOne()
    {
        // Holds a read open across a whole circuit check: under the default 10 s a stall on a loaded
        // host timed the early check out (aud-a29k).
        using var h = new Harness(lookupTimeout: SlowReadLookupTimeout);
        h.Grant("MailboxPermissions", SectionGroupSid);
        var slow = new TaskCompletionSource<OperatorDirectoryMembership>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken).Returns(
            _ => slow.Task,
            _ => Task.FromResult(Membership(enabled: true, DomainUsersSid)));
        var user = Operator(SectionGroupSid, DomainUsersSid);

        // A page request's read starts; before it answers, the tab's interval check reads and finds the
        // operator removed.
        var early = h.Revalidator.RevalidatePageRequestAsync(user, Ct);
        await WaitForReadsAsync(h, 1);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        using var provider = h.Provider(new CircuitRevocation());
        Assert.False(await provider.ValidateAsync(user, "Interval", Ct));

        // The early read then answers with what the directory said before the removal. It confirms its
        // own request, but it must not displace the later answer.
        slow.SetResult(Membership(enabled: true, SectionGroupSid, DomainUsersSid));
        Assert.True((await early).IsValid);

        // So the next page request is not confirmed by the stale answer: the later one would refuse, so
        // the request reads again, and is refused.
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        var next = await h.Revalidator.RevalidatePageRequestAsync(user, Ct);

        Assert.Equal(CircuitRevocationReason.MembershipChanged, next.Reason);
        _ = h.Directory.Received(3).GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task AFailureOfAReadThatStartedEarlier_DoesNotForgetALaterAnswer()
    {
        // Holds a read open across a circuit check, which a stall over the default 10 s would time out
        // (aud-a29k).
        using var h = new Harness(lookupTimeout: SlowReadLookupTimeout);
        h.Grant("MailboxPermissions", SectionGroupSid);
        var slow = new TaskCompletionSource<OperatorDirectoryMembership>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken).Returns(
            _ => slow.Task,
            _ => Task.FromResult(Membership(enabled: true, SectionGroupSid, DomainUsersSid)));
        var user = Operator(SectionGroupSid, DomainUsersSid);

        var early = h.Revalidator.RevalidatePageRequestAsync(user, Ct);
        await WaitForReadsAsync(h, 1);
        h.Clock.Advance(TimeSpan.FromSeconds(1));
        using var provider = h.Provider(new CircuitRevocation());
        Assert.True(await provider.ValidateAsync(user, "Interval", Ct));

        // The early read fails after the later one answered: the later answer stays.
        slow.SetException(new DirectoryUnavailableException("no domain controller answered"));
        Assert.Equal(CircuitRevocationReason.LookupFailed, (await early).Reason);

        h.Clock.Advance(TimeSpan.FromSeconds(1));
        Assert.True((await h.Revalidator.RevalidatePageRequestAsync(user, Ct)).IsValid);
        _ = h.Directory.Received(2).GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task AnExpiredAnswer_IsSweptOutWhenAnotherAccountsAnswerIsRecorded()
    {
        // aud-a29j. An operator who never comes back must not be held for the app pool's life.
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);

        Assert.True((await h.Revalidator.RevalidatePageRequestAsync(Operator(SectionGroupSid, DomainUsersSid), Ct)).IsValid);
        Assert.Equal(new[] { OperatorSid }, h.Revalidator.RecordedAccountSids);

        // One interval after the operator's read started, a colleague's read is recorded.
        h.Clock.Advance(h.Options.Interval);
        Assert.True((await h.Revalidator.RevalidatePageRequestAsync(Colleague(SectionGroupSid, DomainUsersSid), Ct)).IsValid);

        Assert.Equal(new[] { ColleagueSid }, h.Revalidator.RecordedAccountSids);
    }

    [Fact]
    public async Task AnAnswerStillInsideTheInterval_SurvivesTheSweep()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);

        Assert.True((await h.Revalidator.RevalidatePageRequestAsync(Operator(SectionGroupSid, DomainUsersSid), Ct)).IsValid);
        h.Clock.Advance(h.Options.Interval - TimeSpan.FromSeconds(1));
        Assert.True((await h.Revalidator.RevalidatePageRequestAsync(Colleague(SectionGroupSid, DomainUsersSid), Ct)).IsValid);

        Assert.Equal(new[] { OperatorSid, ColleagueSid }, h.Revalidator.RecordedAccountSids.Order(StringComparer.Ordinal));

        // And it still confirms the operator's next page without a read.
        Assert.True((await h.Revalidator.RevalidatePageRequestAsync(Operator(SectionGroupSid, DomainUsersSid), Ct)).IsValid);
        _ = h.Directory.Received(2).GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    // ------------------------------------------------------------------ wording and wiring

    [Fact]
    public void EveryRefusalReasonHasItsOwnPlainMessage_AndThePageIsStandalone()
    {
        var reasons = new[]
        {
            CircuitRevocationReason.MembershipChanged,
            CircuitRevocationReason.AccountDisabled,
            CircuitRevocationReason.LookupFailed,
        };
        var messages = reasons.Select(PageRequestRefusalMessages.For).ToList();

        Assert.Equal(messages.Count, messages.Distinct(StringComparer.Ordinal).Count());
        Assert.All(messages, m => Assert.Contains("Active Directory", m, StringComparison.Ordinal));
        Assert.All(messages, m => Assert.DoesNotContain("S-1-", m, StringComparison.Ordinal));

        // No app markup, no script, so nothing in it can start a circuit; a full reload re-authenticates.
        var page = PageRequestRevalidationMiddleware.RefusalPage(CircuitRevocationReason.LookupFailed);
        Assert.DoesNotContain("<script", page, StringComparison.OrdinalIgnoreCase);
        Assert.Matches(new Regex(@"<a [^>]*data-enhance-nav=""false""[^>]*>Reload</a>"), page);
        Assert.Contains(WebUtility.HtmlEncode(SessionEndedMessages.ReloadAdvice), page, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramCs_ChecksPageRequestsAfterAuthorizationAndBeforeTheComponentsRender()
    {
        var program = RazorSyntax.CodeView(File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs")));

        var authorization = program.IndexOf("app.UseAuthorization()", StringComparison.Ordinal);
        var pageLoad = program.IndexOf("app.UseMiddleware<PageLoadMiddleware>()", StringComparison.Ordinal);
        var check = program.IndexOf("app.UseMiddleware<PageRequestRevalidationMiddleware>()", StringComparison.Ordinal);
        var components = program.IndexOf("app.MapRazorComponents<App>()", StringComparison.Ordinal);

        Assert.True(check >= 0, "Program.cs no longer checks page requests before they render (aud-a29g).");
        Assert.True(authorization >= 0 && check > authorization,
            "The page-request check must run after UseAuthorization, so a request the page's policy denies costs no directory read.");
        Assert.True(pageLoad >= 0 && check > pageLoad,
            "The page-request check must run inside PageLoadMiddleware's scope, so a page waiting on it is reported as loading.");
        Assert.True(components > check,
            "The page-request check must run before the components endpoint, which prerenders the page.");
        Assert.Single(Regex.Matches(program, @"UseMiddleware<PageRequestRevalidationMiddleware>\(\)"));
    }

    // ------------------------------------------------------------------ helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AnySid => Arg.Any<string>();

    private static string? AnyDomain => Arg.Any<string?>();

    private static CancellationToken AnyToken => Arg.Any<CancellationToken>();

    private static ClaimsPrincipal Operator(params string[] groupSids) => Principal(OperatorSid, OperatorName, groupSids);

    private static ClaimsPrincipal Colleague(params string[] groupSids) => Principal(ColleagueSid, ColleagueName, groupSids);

    private static ClaimsPrincipal Principal(string accountSid, string name, string[] groupSids)
    {
        var identity = new ClaimsIdentity("Negotiate", ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        identity.AddClaim(new Claim(ClaimTypes.PrimarySid, accountSid));
        foreach (var sid in groupSids)
            identity.AddClaim(new Claim(ClaimTypes.GroupSid, sid));
        return new ClaimsPrincipal(identity);
    }

    private static OperatorDirectoryMembership Membership(bool enabled, params string[] groupSids) =>
        new(new HashSet<string>(groupSids, StringComparer.OrdinalIgnoreCase), enabled);

    private static string[] RemovedSids(JsonElement audit) =>
        audit.GetProperty("removedGroupSids").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

    // The revalidator moves each check off the caller's thread, so a read is made a moment after the
    // call returns its task. Waits until the directory has been asked this many times - for as long as
    // the harness lets a check take (aud-a29k), so a stall the check itself would absorb cannot fail
    // the wait instead.
    private static async Task WaitForReadsAsync(Harness h, int reads)
    {
        var limit = h.Options.LookupTimeout;
        var deadline = DateTime.UtcNow + limit;
        while (h.Directory.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IOperatorMembershipDirectory.GetMembershipAsync)) < reads)
        {
            Assert.True(DateTime.UtcNow < deadline, $"The directory was not asked {reads} time(s) within {limit.TotalSeconds:0} seconds.");
            await Task.Delay(10, Ct);
        }
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ExchangeAdminWeb.csproj")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test base directory.");
    }

    /// <summary>A clock that moves only when told to, for the interval.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }

    /// <summary>The host's warnings and errors, so a failing assertion can say what the server said.</summary>
    private sealed class HostLog : ILoggerProvider, ILogger
    {
        private readonly ConcurrentQueue<(LogLevel Level, string Text)> _entries = new();

        public IReadOnlyList<string> Warnings =>
            _entries.Where(e => e.Level == LogLevel.Warning).Select(e => e.Text).ToList();

        public string Explain(string message) =>
            message + " Server log: " + string.Join(" | ", _entries.Select(e => $"{e.Level}: {e.Text}"));

        public ILogger CreateLogger(string categoryName) => this;

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel >= LogLevel.Warning)
                _entries.Enqueue((logLevel, formatter(state, exception) + (exception is null ? "" : " " + exception)));
        }

        public void Dispose()
        {
        }
    }

    /// <summary>Signs every request in as the principal the test chose, or as nobody.</summary>
    private sealed class ProbeSignIn(ClaimsPrincipal? user)
    {
        public const string Scheme = "Probe";

        public ClaimsPrincipal? User { get; } = user;
    }

    private sealed class ProbeAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options, ILoggerFactory logger, UrlEncoder encoder, ProbeSignIn signIn)
        : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
    {
        protected override Task<AuthenticateResult> HandleAuthenticateAsync() =>
            Task.FromResult(signIn.User is { } user
                ? AuthenticateResult.Success(new AuthenticationTicket(user, Scheme.Name))
                : AuthenticateResult.NoResult());
    }

    private sealed class ProbeHost(WebApplication app, HttpClient client) : IAsyncDisposable
    {
        public async Task<(HttpStatusCode Status, string Body, string? CacheControl)> GetAsync(string path)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, path);
            request.Headers.Accept.ParseAdd("text/html");
            using var response = await client.SendAsync(request, Ct);
            var body = await response.Content.ReadAsStringAsync(Ct);
            return (response.StatusCode, body, response.Headers.CacheControl?.ToString());
        }

        public async ValueTask DisposeAsync()
        {
            client.Dispose();
            await app.StopAsync(CancellationToken.None);
            await app.DisposeAsync();
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly TempDir _temp = new();
        private CircuitMembershipRevalidator? _revalidator;

        public Harness(bool enabled = true, TimeSpan? lookupTimeout = null)
        {
            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = _temp.Path,
                ["Audit:RotationPeriod"] = "daily",
            }).Build();
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(_temp.Path);

            Store = TestConfigStore.Create(_temp.Path);
            SectionAccess = new SectionAccessService(
                config, NullLogger<SectionAccessService>.Instance, env, new ModuleCatalog(), new SectionAccessRepository(Store));
            ModuleAdmins = new ModuleAdminService(env, new ModuleAdminRepository(Store), NullLogger<ModuleAdminService>.Instance);

            var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
            Audit = new AuditService(log, new OperationTraceService(config, log));
            Options = new CircuitRevalidationOptions(TimeSpan.FromMinutes(5), lookupTimeout ?? TimeSpan.FromSeconds(10), enabled);
        }

        public SqliteConfigStore Store { get; }

        public SectionAccessService SectionAccess { get; }

        public ModuleAdminService ModuleAdmins { get; }

        public IOperatorMembershipDirectory Directory { get; } = Substitute.For<IOperatorMembershipDirectory>();

        public AuditService Audit { get; }

        public CircuitRevalidationOptions Options { get; }

        public ManualClock Clock { get; } = new();

        public PageRequestProbeEffects Effects { get; } = new();

        public HostLog Log { get; } = new();

        /// <summary>One per test, as the singleton is one per app: the recorded answers live in it.</summary>
        public CircuitMembershipRevalidator Revalidator =>
            _revalidator ??= new CircuitMembershipRevalidator(
                Directory, new AuthorizationGroupInventory(SectionAccess, ModuleAdmins, Directory), Options)
            { Clock = Clock };

        public MembershipRevalidatingAuthenticationStateProvider Provider(CircuitRevocation revocation) =>
            new(NullLoggerFactory.Instance, Revalidator, revocation, Options, Audit, new ClientInfoService { IpAddress = "192.0.2.10" });

        public void Grant(string section, params string[] groups)
        {
            var all = SectionAccess.GetSectionAccess();
            all[section] = groups;
            SectionAccess.SaveSectionAccess(all);
        }

        public void DirectoryReturns(bool enabled, params string[] groupSids) =>
            Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken).Returns(Task.FromResult(Membership(enabled, groupSids)));

        /// <summary>Damages the store the way a clobbered or half-migrated file does: the table is gone.</summary>
        public void DropTable(string table) =>
            Store.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DROP TABLE {table};";
                command.ExecuteNonQuery();
            });

        /// <summary>
        /// A host with the production order - authentication, authorization, the page-request check,
        /// antiforgery, the components endpoint - over the probe app, and one endpoint that is not a page.
        /// </summary>
        public async Task<ProbeHost> StartHostAsync(ClaimsPrincipal? user, string? pathBase = null)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(PageRequestProbeApp).Assembly.GetName().Name,
                ContentRootPath = _temp.Path,
                EnvironmentName = Environments.Production,
            });
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Logging.AddProvider(Log);

            builder.Services.AddAuthentication(ProbeSignIn.Scheme)
                .AddScheme<AuthenticationSchemeOptions, ProbeAuthenticationHandler>(ProbeSignIn.Scheme, null);
            builder.Services.AddAuthorization();
            builder.Services.AddRazorComponents().AddInteractiveServerComponents();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddSingleton(new ProbeSignIn(user));
            builder.Services.AddSingleton(Effects);
            builder.Services.AddSingleton(Options);
            builder.Services.AddSingleton(Revalidator);
            builder.Services.AddSingleton(Audit);

            var app = builder.Build();

            // As Program.cs does with Application:PathBase, before everything else.
            if (pathBase is not null)
                app.UsePathBase(pathBase);

            app.UseAuthentication();
            app.UseAuthorization();
            app.UseMiddleware<PageRequestRevalidationMiddleware>();
            app.UseAntiforgery();
            app.MapRazorComponents<PageRequestProbeApp>().AddInteractiveServerRenderMode();
            app.MapGet(NotAPageRoute, () => NotAPageBody);

            await app.StartAsync(Ct);
            return new ProbeHost(app, new HttpClient { BaseAddress = new Uri(app.Urls.First()) });
        }

        public JsonDocument[] AuditEvents()
        {
            var dir = Path.Combine(_temp.Path, "ExchangeAdminWeb");
            if (!System.IO.Directory.Exists(dir))
                return [];

            return System.IO.Directory.GetFiles(dir, "exchangeadmin_*.jsonl")
                .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
                .SelectMany(File.ReadAllLines)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonDocument.Parse(line))
                .Where(e => e.RootElement.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
                .ToArray();
        }

        public void Dispose() => _temp.Dispose();
    }
}

/// <summary>
/// The probe app's root component (aud-a29g tests): a router and nothing else, as the app's own
/// <c>App</c> / <c>Routes</c> pair is. Public, because <c>MapRazorComponents</c> finds pages among an
/// assembly's exported types.
/// </summary>
public sealed class PageRequestProbeApp : ComponentBase
{
    protected override void BuildRenderTree(RenderTreeBuilder builder)
    {
        builder.OpenElement(0, "html");
        builder.OpenElement(1, "body");
        builder.OpenComponent<Router>(2);
        builder.AddComponentParameter(3, nameof(Router.AppAssembly), typeof(PageRequestProbeApp).Assembly);
        builder.AddComponentParameter(4, nameof(Router.Found), (RenderFragment<RouteData>)(routeData => inner =>
        {
            inner.OpenComponent<RouteView>(0);
            inner.AddComponentParameter(1, nameof(RouteView.RouteData), routeData);
            inner.CloseComponent();
        }));
        builder.CloseComponent();
        builder.CloseElement();
        builder.CloseElement();
    }
}

/// <summary>
/// The probe page: routed, InteractiveServer with prerendering - what every page of the app declares -
/// and its initialization leaves a mark a test can count.
/// </summary>
[Route(PageRequestRevalidationTests.ProbeRoute)]
[PrerenderedInteractiveServer]
public sealed class PageRequestProbePage : ComponentBase
{
    public const string Body = "aud-a29g probe page, rendered";

    [Inject]
    public PageRequestProbeEffects Effects { get; set; } = default!;

    protected override void OnInitialized() => Effects.RecordInitialized();

    protected override void BuildRenderTree(RenderTreeBuilder builder) => builder.AddContent(0, Body);
}

/// <summary>What <c>@rendermode InteractiveServer</c> compiles to: the render mode, prerendering on.</summary>
public sealed class PrerenderedInteractiveServerAttribute : RenderModeAttribute
{
    public override IComponentRenderMode Mode => RenderMode.InteractiveServer;
}

/// <summary>Counts the probe page's initializations - the side effect a refused request must not have.</summary>
public sealed class PageRequestProbeEffects
{
    private int _initialized;

    public int Initialized => Volatile.Read(ref _initialized);

    public void RecordInitialized() => Interlocked.Increment(ref _initialized);
}
