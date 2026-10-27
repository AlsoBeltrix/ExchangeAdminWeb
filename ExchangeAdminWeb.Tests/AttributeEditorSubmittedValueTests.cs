using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using static ExchangeAdminWeb.Tests.SubmittedValueHarness;
using Page = ExchangeAdminWeb.Components.Pages.ADAttributeEditor;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-72 on AD Attribute Editor: <c>PerformSearch</c> read the identity after its paint yield, and <c>ConfirmSave</c>
/// read the ticket after its yield - into both refusal records, <c>SaveAsync</c>'s audit row and notice, and the
/// catch's record and email. Both boxes bind on input, so a keystroke landing in the yield changed what was looked up
/// and the ticket a change was recorded under. Both are now read above the yield, and a second submit during either
/// is ignored.
/// </summary>
/// <remarks>
/// The compiled handlers over <see cref="AttributeEditorHarness"/>'s real service and in-memory directory - no domain
/// is reached - held at their yield by <see cref="SubmittedValueHarness.HeldContinuations"/> while the test types.
/// </remarks>
public sealed class AttributeEditorSubmittedValueTests : IDisposable
{
    private const string Ticket = "CHG0200";

    private readonly AttributeEditorHarness _h = new();
    private readonly RecordingProgress _progress = new();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();
    private readonly HashSet<string> _granted = new(StringComparer.Ordinal) { "ADAttributeEditor", "ADAttributeEditorLevel1" };

    public AttributeEditorSubmittedValueTests()
    {
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(call => Task.FromResult(_granted.Contains(call.ArgAt<string>(2))
                ? AuthorizationResult.Success()
                : AuthorizationResult.Failed()));
    }

    public void Dispose() => _h.Dispose();

    [Fact]
    public void AKeystrokeLandingInTheLookupsYield_LooksUpAndLabelsTheIdentitySubmitted()
    {
        var alex = _h.Ad.AddAlex();
        var page = PageFor();
        SetField(page, "searchIdentity", alex.Upn);

        RunWithChangeWhileSuspended(() => Call(page, "PerformSearch"),
            () => SetField(page, "searchIdentity", "nobody@example.test"));

        Assert.Equal($"Looking up {alex.Upn}", Assert.Single(_progress.Handles).Label);
        Assert.Null(Field<string>(page, "searchError"));
        Assert.Equal(alex.Upn, Field<AttributeLookupResult>(page, "lookupResult")?.Principal?.UserPrincipalName);
    }

    [Fact]
    public void ASecondSubmitWhileALookupRuns_IsIgnored_NotRunBesideIt()
    {
        var alex = _h.Ad.AddAlex();
        var page = PageFor();
        SetField(page, "searchIdentity", alex.Upn);

        RunWithChangeWhileSuspended(() => Call(page, "PerformSearch"), () =>
        {
            var second = Call(page, "PerformSearch");
            Assert.True(second.IsCompleted, "a second submit during a lookup started a second lookup");
        });

        Assert.Single(_progress.Handles);
    }

    [Fact]
    public void AKeystrokeLandingInTheSavesYield_IsNotTheTicketTheChangeIsRecordedUnder()
    {
        var alex = _h.Ad.AddAlex();
        var page = PageShowing(alex);

        RunWithChangeWhileSuspended(() => Call(page, "ConfirmSave"), () => SetField(page, "ticketNumber", "CHG9999"));

        Assert.True(Field<(bool success, string message)?>(page, "operationResult")?.success);
        Assert.Single(_h.Ad.Sets);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("Success", Text(record, "result"));
        Assert.Equal(Ticket, Text(record, "ticket"));
        Assert.Equal(Ticket, Assert.Single(_h.Notices).Ticket);
    }

    [Fact]
    public void ASecondConfirmWhileASaveRuns_IsIgnored_NotRunBesideIt()
    {
        var alex = _h.Ad.AddAlex();
        var page = PageShowing(alex);

        RunWithChangeWhileSuspended(() => Call(page, "ConfirmSave"), () =>
        {
            var second = Call(page, "ConfirmSave");
            Assert.True(second.IsCompleted, "a second confirm during a save started a second save");
        });

        Assert.Single(_progress.Handles);
        Assert.Single(_h.Ad.Sets);
        Assert.Single(_h.AuditEvents());
    }

    private Page PageFor()
    {
        var page = new Page();
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(AttributeEditorHarness.Operator()));
        Set(page, "EditorService", _h.Service);
        Set(page, "Progress", _progress);
        Set(page, "ProtectedPrincipalService", _h.Protection);
        Set(page, "Servicers", _h.Servicers);
        Set(page, "AuthStateProvider", authState);
        Set(page, "AuthorizationService", _authorization);
        Set(page, "Email", _h.Email);
        Set(page, "Logger", NullLogger<Page>.Instance);
        SetField(page, "currentUser", @"EXAMPLE\operator");
        SetField(page, "clientIpAddress", "10.0.0.5");
        return page;
    }

    /// <summary>The page as it stands when the operator confirms a title change for <paramref name="user"/>.</summary>
    private Page PageShowing(LocalAd.User user)
    {
        var page = PageFor();
        SetField(page, "ticketNumber", Ticket);
        SetField(page, "lookupResult", new AttributeLookupResult(
            true, null, AttributeEditorHarness.PrincipalFor(user), "OnPremAD", false, null, null));
        SetField(page, "pendingChanges", new List<AttributeChange> { new("title", "Job Title", "Analyst", "Lead Analyst") });
        return page;
    }

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
}
