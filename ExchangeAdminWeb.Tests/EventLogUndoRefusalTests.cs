using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.AdminEventLog;

namespace ExchangeAdminWeb.Tests;

#pragma warning disable BL0006
/// <summary>
/// aud-a28l: the Admin Event Log refused an undo whose operator had lost the authorization for it,
/// and wrote no audit record.
/// </summary>
/// <remarks>
/// ExecuteUndo is driven itself - the compiled page's handler, its injected services set by
/// reflection, run on a renderer's dispatcher because it calls StateHasChanged; the page is
/// subclassed only to render nothing. Behind it is <see cref="AttributeEditorHarness"/>'s real undo
/// service in a real <see cref="UndoRegistry"/>, so the record read back is the one the shipped code
/// writes. Conditions are asserted, not wording.
/// </remarks>
public sealed class EventLogUndoRefusalTests : IDisposable
{
    private const string Ip = "10.0.0.5";
    private const string Ticket = "CHG0400";
    private const BindingFlags PrivateInstance = BindingFlags.Instance | BindingFlags.NonPublic;

    private readonly AttributeEditorHarness _h = new();
    private readonly ActivityProgressService _progress = new();
    private readonly HandlerRenderer _renderer = new();
    private readonly IAuthorizationService _authorization = Substitute.For<IAuthorizationService>();
    private readonly HashSet<string> _denied = new(StringComparer.Ordinal);

    public EventLogUndoRefusalTests()
    {
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(call => Task.FromResult(_denied.Contains(call.ArgAt<string>(2))
                ? AuthorizationResult.Failed()
                : AuthorizationResult.Success()));
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _progress.Dispose();
        _h.Dispose();
    }

    /// <summary>The page with the undo panel open on <paramref name="edit"/> and a ticket typed.</summary>
    private Page PageConfirming(Dictionary<string, object?> edit)
    {
        var page = new QuietEventLog();
        SetProperty(page, "UndoRegistry", new UndoRegistry([_h.CreateUndoService()]));
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(AttributeEditorHarness.Operator()));
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", _authorization);
        SetProperty(page, "ClientInfo", new ClientInfoService { IpAddress = Ip });
        SetProperty(page, "Progress", _progress);
        SetProperty(page, "Logger", NullLogger<Page>.Instance);

        var entryType = typeof(Page).GetNestedType("MergedLogEntry", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType)!;
        entryType.GetProperty("RawFields")!.SetValue(entry, edit);
        entryType.GetProperty("OperationId")!.SetValue(entry, (string)edit["operationId"]!);
        SetField(page, "undoTargetEvent", entry);
        SetField(page, "undoPreview", new UndoPreview(
            true, null, "ADAttributeEditor", "ADAttributeEditor_Update", (string)edit["target"]!,
            (string)edit["operationId"]!, [], false, null));
        SetField(page, "undoTicket", Ticket);

        _renderer.Dispatcher.InvokeAsync(() => _renderer.Attach(page)).GetAwaiter().GetResult();
        return page;
    }

    private Task ExecuteUndo(Page page) =>
        _renderer.Dispatcher.InvokeAsync(() =>
            (Task)typeof(Page).GetMethod("ExecuteUndo", PrivateInstance)!.Invoke(page, null)!);

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, PrivateInstance)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, PrivateInstance)!.SetValue(page, value);

    private static T Field<T>(Page page, string name) =>
        (T)typeof(Page).GetField(name, PrivateInstance)!.GetValue(page)!;

    private static string Text(JsonElement record, string property) =>
        record.GetProperty(property).GetString() ?? "";

    [Theory]
    [InlineData("EventLog")]
    [InlineData("UndoAuditedActions")]
    public async Task AnUndoRefusedForAuthorization_LeavesOneRefusalRecord_AndTheEditKeepsItsUndo(string revoked)
    {
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var page = PageConfirming(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));
        _denied.Add(revoked);

        await ExecuteUndo(page);

        var shown = Field<string?>(page, "undoError");
        Assert.False(string.IsNullOrEmpty(shown));
        Assert.Null(Field<string?>(page, "undoSuccess"));

        var record = Assert.Single(_h.AuditEvents());
        Assert.Equal("ADAttributeEditor_Undo", Text(record, "action"));
        Assert.Equal("ADAttributeEditor", Text(record, "category"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.Equal("Authorization", Text(record, "refusedAt"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(shown, Text(record, "error"));
        Assert.Equal("op-forward-1", Text(record, "originalOperationId"));
        Assert.Equal(alex.Upn, Text(record, "target"));
        Assert.Equal("operator", Text(record, "user"));
        Assert.Equal(Ip, Text(record, "ip"));
        Assert.Equal(Ticket, Text(record, "ticket"));
        Assert.Equal(["title"], record.GetProperty("changedAttributes").EnumerateArray().Select(a => a.GetString()));
        Assert.Equal("Lead Analyst", Text(record, "old_title"));
        Assert.Equal("Analyst", Text(record, "new_title"));

        Assert.Equal(0, _h.Service.SessionsOpened);
        Assert.Empty(_h.Ad.Sets);
        Assert.Empty(_h.Notices);

        Assert.DoesNotContain("op-forward-1", Field<HashSet<string>>(page, "reversedOperationIds"));
        Assert.DoesNotContain("op-forward-1", ReversedOperations.From(
            _h.AuditEvents(),
            e => e.TryGetProperty("originalOperationId", out var id) ? id.GetString() : null,
            e => e.TryGetProperty("result", out var outcome) ? outcome.GetString() : null));
    }

    [Fact]
    public void ARefusalWhoseRecordCannotBeWritten_IsLogged_NotThrown()
    {
        var logger = Substitute.For<ILogger<ADAttributeEditorUndoService>>();
        var service = new ADAttributeEditorUndoService(null!, null!, null!, null!, null!, null!, null!, logger);
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");

        var thrown = Record.Exception(() => service.LogUndoRefused(
            AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"),
            "EXAMPLE\\operator", Ip, Ticket, "Authorization", "Authorization denied."));

        Assert.Null(thrown);
        Assert.Contains(logger.ReceivedCalls(), call =>
            call.GetMethodInfo().Name == "Log" && Equals(call.GetArguments()[0], LogLevel.Error));
    }

    private sealed class QuietEventLog : Page
    {
        protected override void BuildRenderTree(RenderTreeBuilder builder)
        {
        }
    }

    private sealed class HandlerRenderer() : Renderer(new ServiceCollection().BuildServiceProvider(), NullLoggerFactory.Instance)
    {
        public override Dispatcher Dispatcher { get; } = Dispatcher.CreateDefault();

        public void Attach(IComponent component) => AssignRootComponentId(component);

        protected override Task UpdateDisplayAsync(in RenderBatch batch) => Task.CompletedTask;

        protected override void HandleException(Exception exception) => throw exception;
    }
}
#pragma warning restore BL0006
