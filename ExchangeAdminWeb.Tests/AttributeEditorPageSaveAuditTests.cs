using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.ADAttributeEditor;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28g: the editor page's own save refusals and its catch wrote no audit record.
/// </summary>
/// <remarks>
/// ConfirmSave is driven itself - the compiled page's handler, invoked by reflection with its
/// injected services set, the Comms10k and Defender handler pattern; no renderer is needed because
/// the handler never renders. Behind it is <see cref="AttributeEditorHarness"/>'s real service, so
/// what the page leaves to SaveAsync is recorded by SaveAsync. Each attempt must leave exactly one
/// record, whichever of the two wrote it. Conditions are asserted, not wording.
/// </remarks>
public sealed class AttributeEditorPageSaveAuditTests : IDisposable
{
    private const string Operator = "EXAMPLE\\operator";
    private const string Ip = "10.0.0.5";
    private const string Ticket = "CHG0200";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly AttributeEditorHarness _h = new();
    private readonly ActivityProgressService _progress = new();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();
    private readonly HashSet<string> _granted = new(StringComparer.Ordinal) { "ADAttributeEditor", "ADAttributeEditorLevel1" };
    private Exception? _authorizationFailure;

    public AttributeEditorPageSaveAuditTests()
    {
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(call => _authorizationFailure is { } failure
                ? Task.FromException<AuthorizationResult>(failure)
                : Task.FromResult(_granted.Contains(call.ArgAt<string>(2))
                    ? AuthorizationResult.Success()
                    : AuthorizationResult.Failed()));
    }

    public void Dispose()
    {
        _progress.Dispose();
        _h.Dispose();
    }

    private static AttributeChange Title() => new("title", "Job Title", "Analyst", "Lead Analyst");

    /// <summary>The page as it stands when the operator confirms the diff preview.</summary>
    private Page PageShowing(LocalAd.User? user, params AttributeChange[] changes)
    {
        var page = new Page();
        SetProperty(page, "EditorService", _h.Service);
        SetProperty(page, "Progress", _progress);
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(AttributeEditorHarness.Operator()));
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", _authorization);
        SetProperty(page, "Email", _h.Email);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);
        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", Ip);
        SetField(page, "ticketNumber", Ticket);
        if (user is not null)
        {
            SetField(page, "lookupResult", new AttributeLookupResult(
                true, null, AttributeEditorHarness.PrincipalFor(user), "OnPremAD", false, null, null));
        }

        SetField(page, "pendingChanges", changes.ToList());
        return page;
    }

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, PrivateInstance)!.SetValue(page, value);

    private static Task ConfirmSave(Page page) =>
        (Task)typeof(Page).GetMethod("ConfirmSave", PrivateInstance)!.Invoke(page, null)!;

    /// <summary>What the page told the operator.</summary>
    private static (bool Success, string Message) Shown(Page page) =>
        ((bool, string))typeof(Page).GetField("operationResult", PrivateInstance)!.GetValue(page)!;

    private static string Text(JsonElement record, string property) =>
        record.GetProperty(property).GetString() ?? "";

    /// <summary>One refusal record, saying where it refused, that nothing was tried, and what the operator saw.</summary>
    private static void AssertRefusal(JsonElement record, string refusedAt, string target, string shown)
    {
        Assert.Equal("ADAttributeEdit_Refused", Text(record, "action"));
        Assert.Equal("ADAttributeEditor", Text(record, "category"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(refusedAt, Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(shown, Text(record, "error"));
        Assert.Equal(target, Text(record, "target"));
        Assert.Equal("operator", Text(record, "user"));
        Assert.Equal(Ip, Text(record, "ip"));
        Assert.Equal(Ticket, Text(record, "ticket"));
    }

    // ---- refusals the page makes itself --------------------------------------------------------

    [Fact]
    public async Task AnAuthorizationRefusal_LeavesOneRefusalRecord_AndOpensNoDirectorySession()
    {
        var alex = _h.Ad.AddAlex();
        var page = PageShowing(alex, Title());
        _granted.Remove("ADAttributeEditor");

        await ConfirmSave(page);

        var shown = Shown(page);
        Assert.False(shown.Success);
        AssertRefusal(Assert.Single(_h.AuditEvents()), "Authorization", alex.Upn, shown.Message);
        Assert.Equal(0, _h.Service.SessionsOpened);
        Assert.Empty(_h.Ad.Sets);
        Assert.Empty(_h.Notices);
    }

    [Fact]
    public async Task ASaveWithNoLookedUpUser_LeavesOneRefusalRecord_AndOpensNoDirectorySession()
    {
        // A search that cleared the panel between the preview and the confirm: there is no target
        // to save to, which is a refused attempt like any other.
        var page = PageShowing(null, Title());

        await ConfirmSave(page);

        var shown = Shown(page);
        Assert.False(shown.Success);
        AssertRefusal(Assert.Single(_h.AuditEvents()), "Lookup", "(none)", shown.Message);
        Assert.Equal(0, _h.Service.SessionsOpened);
        Assert.Empty(_h.Notices);
    }

    // ---- refusals the page now leaves to the service ------------------------------------------

    [Fact]
    public async Task ACorruptAllowlist_IsRefusedOnce_ByTheService()
    {
        var alex = _h.Ad.AddAlex();
        var page = PageShowing(alex, Title());
        _h.CorruptAllowlist();

        await ConfirmSave(page);

        var shown = Shown(page);
        Assert.False(shown.Success);
        AssertRefusal(Assert.Single(_h.AuditEvents()), "AllowlistConfig", alex.Upn, shown.Message);
        Assert.Equal(0, _h.Service.SessionsOpened);
        // SaveAsync announces every result it returns (aud-a26b), so this refusal now has one notice.
        var notice = Assert.Single(_h.Notices);
        Assert.False(notice.Success);
    }

    [Fact]
    public async Task AnAttributeAboveTheOperatorsLevel_IsRefusedOnce_ByTheService()
    {
        // extensionAttribute5 is level 2; this operator holds level 1 only.
        var alex = _h.Ad.AddAlex();
        var page = PageShowing(alex, new AttributeChange("extensionAttribute5", "Cost Centre", null, "CC-42"));

        await ConfirmSave(page);

        var shown = Shown(page);
        Assert.False(shown.Success);
        Assert.Contains("extensionAttribute5", shown.Message, StringComparison.Ordinal);
        AssertRefusal(Assert.Single(_h.AuditEvents()), "LevelCheck", alex.Upn, shown.Message);
        Assert.Equal(0, _h.Service.SessionsOpened);
        Assert.Empty(_h.Ad.Sets);
        var notice = Assert.Single(_h.Notices);
        Assert.False(notice.Success);
    }

    // ---- the catch -----------------------------------------------------------------------------

    [Fact]
    public async Task AnExceptionTheSaveDoesNotReturn_LeavesOneFailureRecord_AndTheCatchsOneNotice()
    {
        var alex = _h.Ad.AddAlex();
        var page = PageShowing(alex, Title());
        _authorizationFailure = new InvalidOperationException("the authorization store could not be read");

        await ConfirmSave(page);

        var shown = Shown(page);
        Assert.False(shown.Success);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal(shown.Message, Text(record, "error"));
        Assert.Equal(alex.Upn, Text(record, "target"));
        Assert.Equal(Ticket, Text(record, "ticket"));
        // Not a refusal: the record must not claim nothing was tried.
        Assert.False(record.TryGetProperty("refusedAt", out _));
        Assert.False(record.TryGetProperty("mutationAttempted", out _));
        Assert.Equal(0, _h.Service.SessionsOpened);
        var notice = Assert.Single(_h.Notices);
        Assert.False(notice.Success);
        Assert.Equal(shown.Message, notice.Error);
    }

    // ---- the control ---------------------------------------------------------------------------

    [Fact]
    public async Task ASaveThatGoesThrough_LeavesExactlyOneRecord_TheService()
    {
        // The page adds no record of its own to an attempt SaveAsync records.
        var alex = _h.Ad.AddAlex();
        var page = PageShowing(alex, Title());

        await ConfirmSave(page);

        Assert.True(Shown(page).Success, Shown(page).Message);
        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Update", Text(record, "action"));
        Assert.Equal("Success", Text(record, "result"));
        Assert.Single(_h.Ad.Sets);
        Assert.Single(_h.Notices);
    }
}
