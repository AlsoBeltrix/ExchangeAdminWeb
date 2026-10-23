using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
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
/// and wrote no audit record. aud-a28m: its two other early exits wrote none either, and a close while
/// the undo ran made its catch throw out of the handler.
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
    private readonly Dictionary<string, TaskCompletionSource<AuthorizationResult>> _held = new(StringComparer.Ordinal);

    public EventLogUndoRefusalTests()
    {
        _authorization.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(call =>
            {
                var policy = call.ArgAt<string>(2);
                if (_held.TryGetValue(policy, out var held))
                    return held.Task;

                return Task.FromResult(_denied.Contains(policy)
                    ? AuthorizationResult.Failed()
                    : AuthorizationResult.Success());
            });
    }

    /// <summary>The answer for <paramref name="policy"/> is whatever the returned source is given.</summary>
    private TaskCompletionSource<AuthorizationResult> Hold(string policy)
    {
        var answer = new TaskCompletionSource<AuthorizationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        _held[policy] = answer;
        return answer;
    }

    public void Dispose()
    {
        _renderer.Dispose();
        _progress.Dispose();
        _h.Dispose();
    }

    /// <summary>
    /// The page with the undo panel open on <paramref name="edit"/> and a ticket typed; its logger and its progress channel
    /// <paramref name="logger"/>'s and <paramref name="progress"/>'s when given (aud-a63).
    /// </summary>
    private Page PageConfirming(
        Dictionary<string, object?> edit, UndoRegistry? registry = null, ILogger<Page>? logger = null, IActivityProgress? progress = null)
    {
        var page = Unattached(registry ?? new UndoRegistry([_h.CreateUndoService()]), logger, progress);
        SetField(page, "undoTargetEvent", Entry(edit));
        SetField(page, "undoPreview", PreviewOf(edit));
        SetField(page, "undoTicket", Ticket);

        _renderer.Dispatcher.InvokeAsync(() => _renderer.Attach(page)).GetAwaiter().GetResult();
        return page;
    }

    /// <summary>The page with no undo panel open, its undo handlers <paramref name="registry"/>'s (aud-a26o).</summary>
    private Page PageWithoutAPanel(UndoRegistry registry)
    {
        var page = Unattached(registry);
        _renderer.Dispatcher.InvokeAsync(() => _renderer.Attach(page)).GetAwaiter().GetResult();
        return page;
    }

    private Page Unattached(UndoRegistry registry, ILogger<Page>? logger = null, IActivityProgress? progress = null)
    {
        var page = new QuietEventLog();
        SetProperty(page, "UndoRegistry", registry);
        SetProperty(page, "Audit", _h.Audit);
        var authState = Substitute.For<AuthenticationStateProvider>();
        authState.GetAuthenticationStateAsync().Returns(new AuthenticationState(AttributeEditorHarness.Operator()));
        SetProperty(page, "AuthStateProvider", authState);
        SetProperty(page, "AuthorizationService", _authorization);
        SetProperty(page, "ClientInfo", new ClientInfoService { IpAddress = Ip });
        SetProperty(page, "Progress", progress ?? _progress);
        SetProperty(page, "Logger", logger ?? NullLogger<Page>.Instance);
        return page;
    }

    /// <summary>The undo preview a module would answer for <paramref name="edit"/>.</summary>
    private static UndoPreview PreviewOf(Dictionary<string, object?> edit) =>
        new(true, null, "ADAttributeEditor", "ADAttributeEditor_Update", (string)edit["target"]!,
            (string)edit["operationId"]!, [], false, null);

    /// <summary>The page's log row for <paramref name="edit"/>, as LoadEvents builds it.</summary>
    private static object Entry(Dictionary<string, object?> edit)
    {
        var entryType = typeof(Page).GetNestedType("MergedLogEntry", BindingFlags.NonPublic)!;
        var entry = Activator.CreateInstance(entryType)!;
        entryType.GetProperty("RawFields")!.SetValue(entry, edit);
        entryType.GetProperty("OperationId")!.SetValue(entry, (string)edit["operationId"]!);
        entryType.GetProperty("FullTarget")!.SetValue(entry, (string)edit["target"]!);
        return entry;
    }

    private Task ExecuteUndo(Page page) =>
        _renderer.Dispatcher.InvokeAsync(() =>
            (Task)typeof(Page).GetMethod("ExecuteUndo", PrivateInstance)!.Invoke(page, null)!);

    private Task ShowUndoPreview(Page page, object entry) =>
        _renderer.Dispatcher.InvokeAsync(() =>
            (Task)typeof(Page).GetMethod("ShowUndoPreview", PrivateInstance)!.Invoke(page, [entry])!);

    private Task CloseUndoPanel(Page page) =>
        _renderer.Dispatcher.InvokeAsync(() =>
            typeof(Page).GetMethod("CloseUndoPanel", PrivateInstance)!.Invoke(page, null));

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

        // Refused before the undo service ran: no directory session, nothing written, and no notice -
        // the page never announced this refusal, and recording it does not start to.
        Assert.Equal(0, _h.Service.SessionsOpened);
        Assert.Empty(_h.Ad.Sets);
        Assert.Empty(_h.Notices);

        // The edit keeps its Undo button: the refusal names the edit, and neither the page's in-session
        // set nor the rule the next load applies to the records (aud-a26c) takes that for a reversal.
        Assert.DoesNotContain("op-forward-1", Field<HashSet<string>>(page, "reversedOperationIds"));
        Assert.DoesNotContain("op-forward-1", ReversedOperations.From(
            _h.AuditEvents(),
            e => e.TryGetProperty("originalOperationId", out var id) ? id.GetString() : null,
            e => e.TryGetProperty("result", out var outcome) ? outcome.GetString() : null));
    }

    [Fact]
    public void ARefusalWhoseRecordCannotBeWritten_IsLogged_NotThrown()
    {
        // No audit writer at all stands in for one that throws. The page calls this on its refusal path,
        // so a throw would replace "Authorization denied." with the catch's message and still leave no
        // record.
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

    // ---- aud-a28m: every exit of ExecuteUndo leaves its record, and a close cannot break it ----------

    private const string StoreUnreadable = "The authorization store could not be read.";

    /// <summary>The edit is not marked reversed: not by the page's in-session set, nor by the rule the next load applies.</summary>
    private void AssertNotReversed(Page page)
    {
        Assert.DoesNotContain("op-forward-1", Field<HashSet<string>>(page, "reversedOperationIds"));
        Assert.DoesNotContain("op-forward-1", ReversedOperations.From(
            _h.AuditEvents(),
            e => e.TryGetProperty("originalOperationId", out var id) ? id.GetString() : null,
            e => e.TryGetProperty("result", out var outcome) ? outcome.GetString() : null));
    }

    /// <summary>The event log's own record of an undo it did not hand to a module.</summary>
    private static void AssertEventLogRecord(JsonElement record, string shown, LocalAd.User alex)
    {
        Assert.Equal("AdminEventLog_Undo", Text(record, "action"));
        Assert.Equal("AdminEventLog", Text(record, "category"));
        Assert.Equal("Failed", Text(record, "result"));
        Assert.False(record.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(shown, Text(record, "error"));
        Assert.Equal("op-forward-1", Text(record, "originalOperationId"));
        Assert.Equal(alex.Upn, Text(record, "target"));
        Assert.Equal("operator", Text(record, "user"));
        Assert.Equal(Ip, Text(record, "ip"));
        Assert.Equal(Ticket, Text(record, "ticket"));
    }

    [Fact]
    public async Task AnUndoNoModuleClaims_LeavesOneRefusalRecord()
    {
        // No module claims the event, so no module can record the attempt: the event log records it.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var page = PageConfirming(
            AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"), new UndoRegistry([]));

        await ExecuteUndo(page);

        var shown = Field<string?>(page, "undoError");
        Assert.False(string.IsNullOrEmpty(shown));
        Assert.Null(Field<string?>(page, "undoSuccess"));

        var record = Assert.Single(_h.AuditEvents());
        AssertEventLogRecord(record, shown!, alex);
        Assert.Equal("NoHandler", Text(record, "refusedAt"));

        Assert.Equal(0, _h.Service.SessionsOpened);
        Assert.Empty(_h.Ad.Sets);
        Assert.Empty(_h.Notices);
        AssertNotReversed(page);
    }

    [Theory]
    [InlineData("EventLog")]
    [InlineData("ADAttributeEditorLevel3")]
    public async Task AnUndoThatThrowsBeforeItsModuleIsCalled_LeavesOneFailureRecord(string faulted)
    {
        // The authorization store failing under the page's re-check, or under the level check after it:
        // either way the module is never called, so the page's catch is the only place left to record it.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var page = PageConfirming(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));
        Hold(faulted).SetException(new InvalidOperationException(StoreUnreadable));

        await ExecuteUndo(page);

        var shown = Field<string?>(page, "undoError");
        Assert.Contains(StoreUnreadable, shown, StringComparison.Ordinal);
        Assert.Null(Field<string?>(page, "undoSuccess"));

        var record = Assert.Single(_h.AuditEvents());
        AssertEventLogRecord(record, shown!, alex);
        Assert.False(record.TryGetProperty("refusedAt", out _));

        Assert.Equal(0, _h.Service.SessionsOpened);
        Assert.Empty(_h.Ad.Sets);
        Assert.Empty(_h.Notices);
        AssertNotReversed(page);
    }

    // ---- aud-a63: a logging provider that throws decides nothing about the undo's outcome ---------------------------

    [Theory]
    [InlineData("EventLog")]
    [InlineData("ADAttributeEditorLevel3")]
    public async Task AnUndoThatThrowsBeforeItsModuleIsCalled_WithALoggerThatThrows_IsRecordedAndFailed_AndNothingEscapes(string faulted)
    {
        // The defect: the catch reported the exception first, with no guard. A provider that threw left undoError unset,
        // so no record was written, the finally completed the activity as a success, and the exception reached the
        // circuit.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var logger = new ThrowingLogger<Page>();
        var progress = new RecordingProgress();
        var page = PageConfirming(
            AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"), logger: logger, progress: progress);
        Hold(faulted).SetException(new InvalidOperationException(StoreUnreadable));

        var escaped = await Record.ExceptionAsync(() => ExecuteUndo(page));

        // The defect's three halves, each reported on its own when it is back.
        var shown = Field<string?>(page, "undoError");
        var records = _h.AuditEvents();
        var completed = progress.Completed;
        Assert.Multiple(
            () => Assert.Null(escaped),
            () => Assert.Single(records),
            () => Assert.Equal<(string Label, bool Success, string? Message)>([("Undoing the selected action", false, shown)], completed));

        Assert.Contains(StoreUnreadable, shown, StringComparison.Ordinal);
        Assert.Null(Field<string?>(page, "undoSuccess"));
        Assert.False(Field<bool>(page, "undoExecuting"));
        AssertEventLogRecord(records[0], shown!, alex);
        Assert.False(records[0].TryGetProperty("refusedAt", out _));
        AssertNotReversed(page);
        Assert.Equal(1, logger.Attempts);
    }

    [Fact]
    public async Task ACancelWhileTheUndoRuns_IsRefused_AndTheUndosOutcomeIsShownAndRecorded()
    {
        // The finding's race: Cancel pressed while the re-check is awaited. The close is refused, the
        // outcome is recorded and shown, and once the undo has ended Cancel works again - the guard is
        // lowered in the handler's finally, not left stuck.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var page = PageConfirming(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));
        var check = Hold("EventLog");
        var confirmed = Field<object?>(page, "undoTargetEvent");

        var undo = ExecuteUndo(page);
        Assert.False(undo.IsCompleted);
        Assert.True(Field<bool>(page, "undoExecuting"));

        await CloseUndoPanel(page);

        Assert.Same(confirmed, Field<object?>(page, "undoTargetEvent"));
        Assert.NotNull(Field<UndoPreview?>(page, "undoPreview"));
        Assert.Equal(Ticket, Field<string>(page, "undoTicket"));

        check.SetException(new InvalidOperationException(StoreUnreadable));
        var escaped = await Record.ExceptionAsync(() => undo);

        Assert.Null(escaped);
        var shown = Field<string?>(page, "undoError");
        Assert.Contains(StoreUnreadable, shown, StringComparison.Ordinal);
        AssertEventLogRecord(Assert.Single(_h.AuditEvents()), shown!, alex);
        Assert.False(Field<bool>(page, "undoExecuting"));

        await CloseUndoPanel(page);

        Assert.Null(Field<object?>(page, "undoTargetEvent"));
        Assert.Null(Field<UndoPreview?>(page, "undoPreview"));
    }

    [Fact]
    public async Task ThePanelClearedWhileTheUndoRuns_CannotTurnItsOutcomeIntoAnException()
    {
        // Whatever clears the panel while the undo is suspended - below, what the Cancel button did before
        // aud-a28m - the handler records the event and ticket it was confirmed for, and nothing escapes it.
        // The close's refusal is pinned above; this pins the locals without it.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var page = PageConfirming(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));
        var check = Hold("EventLog");

        var undo = ExecuteUndo(page);
        await _renderer.Dispatcher.InvokeAsync(() =>
        {
            SetField(page, "undoPreview", null);
            SetField(page, "undoTargetEvent", null);
            SetField(page, "undoError", null);
            SetField(page, "undoSuccess", null);
            SetField(page, "undoTicket", "");
        });

        check.SetException(new InvalidOperationException(StoreUnreadable));
        var escaped = await Record.ExceptionAsync(() => undo);

        Assert.Null(escaped);
        var shown = Field<string?>(page, "undoError");
        Assert.Contains(StoreUnreadable, shown, StringComparison.Ordinal);
        AssertEventLogRecord(Assert.Single(_h.AuditEvents()), shown!, alex);
    }

    [Fact]
    public void TheUndoPanelsCloseButtons_AreDisabledWhileAnUndoRuns()
    {
        // Tripwire: no bUnit host renders the page. The backdrop cannot be disabled; CloseUndoPanel's own
        // refusal, driven above, covers it.
        var markup = RazorSyntax.CommentsBlanked(File.ReadAllText(
            AuditCategoryFilingTests.FindRepoFile("Components", "Pages", "AdminEventLog.razor")));

        var buttons = Regex.Matches(markup, @"<button[^>]*@onclick=""CloseUndoPanel""[^>]*>")
            .Select(m => m.Value)
            .ToList();

        Assert.Equal(2, buttons.Count);
        Assert.All(buttons, button => Assert.Contains("disabled=\"@undoExecuting\"", button, StringComparison.Ordinal));
    }

    // ---- aud-a28o: neither a preview nor a second undo starts while an undo runs --------------------

    [Fact]
    public async Task APreviewRequestedWhileAnUndoRuns_IsRefused_AndTheUndosOutcomeStaysOnItsPanel()
    {
        // The table's Undo buttons sit behind the backdrop but can still be reached by keyboard. A preview
        // opened mid-run used to replace the panel, and the running undo's outcome then showed on it.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var page = PageConfirming(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));
        var check = Hold("EventLog");
        var confirmed = Field<object?>(page, "undoTargetEvent");
        var confirmedPreview = Field<UndoPreview?>(page, "undoPreview");

        var undo = ExecuteUndo(page);
        Assert.False(undo.IsCompleted);

        var sam = _h.Ad.Add("sam.rivera@example.test", "CN=Sam Rivera,OU=Staff,DC=example,DC=test", "Sam Rivera", ("title", "Lead Analyst"));
        var other = AttributeEditorHarness.ForwardEdit(sam, "title", from: "Analyst", to: "Lead Analyst");
        other["operationId"] = "op-forward-2";
        await ShowUndoPreview(page, Entry(other));

        Assert.Same(confirmed, Field<object?>(page, "undoTargetEvent"));
        Assert.Same(confirmedPreview, Field<UndoPreview?>(page, "undoPreview"));
        Assert.Equal(Ticket, Field<string>(page, "undoTicket"));
        Assert.Equal(0, _h.Service.SessionsOpened);

        check.SetException(new InvalidOperationException(StoreUnreadable));
        Assert.Null(await Record.ExceptionAsync(() => undo));

        var shown = Field<string?>(page, "undoError");
        Assert.Contains(StoreUnreadable, shown, StringComparison.Ordinal);
        AssertEventLogRecord(Assert.Single(_h.AuditEvents()), shown!, alex);
        Assert.Same(confirmed, Field<object?>(page, "undoTargetEvent"));
    }

    [Fact]
    public async Task ASecondConfirmWhileAnUndoRuns_ReturnsAtOnce_AndLeavesNoRecordOfItsOwn()
    {
        // A double-click before the re-render disables Confirm: the second call used to pass every check the
        // first had and run a second undo beside it.
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var page = PageConfirming(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));
        var check = Hold("EventLog");

        var first = ExecuteUndo(page);
        Assert.False(first.IsCompleted);
        var second = ExecuteUndo(page);
        var settled = await Task.WhenAny(second, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var secondReturnedAtOnce = ReferenceEquals(settled, second);
        var recordsWhileTheFirstRuns = _h.AuditEvents().Count;

        // Released either way, so a second call that did wait cannot hang the test.
        check.SetException(new InvalidOperationException(StoreUnreadable));
        var escaped = await Record.ExceptionAsync(() => Task.WhenAll(first, second));

        Assert.True(secondReturnedAtOnce, "the second ExecuteUndo waited on the first one's check instead of returning");
        Assert.Equal(0, recordsWhileTheFirstRuns);
        Assert.Null(escaped);
        var shown = Field<string?>(page, "undoError");
        Assert.Contains(StoreUnreadable, shown, StringComparison.Ordinal);
        AssertEventLogRecord(Assert.Single(_h.AuditEvents()), shown!, alex);
        Assert.False(Field<bool>(page, "undoExecuting"));
    }

    // ---- aud-a26o: the panel's preview is its own event's -----------------------------------------------------

    [Fact]
    public async Task APreviewRequestedWhileAnotherLoads_IsRefused_AndTheFirstIsShownForItsOwnEvent()
    {
        // Two previews back to back used to both run: the later click set the target, and whichever answer came
        // last filled the panel - one event's preview beside another event as the target that Confirm undoes.
        var handler = new HeldPreviews();
        var page = PageWithoutAPanel(new UndoRegistry([handler]));
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var first = AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst");
        var second = AttributeEditorHarness.ForwardEdit(alex, "department", from: "Finance", to: "Audit");
        second["operationId"] = "op-forward-2";
        var firstEntry = Entry(first);

        var loading = ShowUndoPreview(page, firstEntry);
        Assert.False(loading.IsCompleted);
        Assert.Single(handler.Requests);
        var refused = ShowUndoPreview(page, Entry(second));
        var settled = await Task.WhenAny(refused, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var refusedAtOnce = ReferenceEquals(settled, refused);

        // Answered either way, so a second preview that did start cannot hang the test.
        foreach (var (requested, answer) in handler.Requests)
            answer.TrySetResult(PreviewOf(requested));
        await Task.WhenAll(loading, refused);

        Assert.True(refusedAtOnce, "the second preview started while the first was loading");
        Assert.Single(handler.Requests);
        Assert.Same(firstEntry, Field<object?>(page, "undoTargetEvent"));
        Assert.Equal("op-forward-1", Field<UndoPreview?>(page, "undoPreview")!.OriginalOperationId);
        Assert.False(Field<bool>(page, "undoLoading"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task APreviewThatFinishesAfterItsPanelWasClosed_IsDropped(bool previewFails)
    {
        // Closed while it loaded, the panel used to reopen with the answer - a preview, or the error preview a
        // failure builds - beside no event at all.
        var handler = new HeldPreviews();
        var page = PageWithoutAPanel(new UndoRegistry([handler]));
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");

        var loading = ShowUndoPreview(page, Entry(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst")));
        var (requested, answer) = Assert.Single(handler.Requests);
        await CloseUndoPanel(page);
        if (previewFails)
            answer.SetException(new InvalidOperationException(StoreUnreadable));
        else
            answer.SetResult(PreviewOf(requested));

        Assert.Null(await Record.ExceptionAsync(() => loading));
        Assert.Null(Field<object?>(page, "undoTargetEvent"));
        Assert.Null(Field<UndoPreview?>(page, "undoPreview"));
        Assert.False(Field<bool>(page, "undoLoading"));
    }

    // ---- aud-a26r: a closed panel's preview neither blocks the next one nor lands on it ---------------------------

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task APreviewAfterALoadingPanelWasClosed_Starts_AndTheClosedOnesAnswerIsDropped(bool sameEntry)
    {
        // Closed while it loaded, the panel kept its loading flag until the closed preview's handler returned, so every
        // preview asked for meantime was refused. And once a close lowers the flag, the entry check alone would let the
        // closed preview's answer land on a new panel opened for the same entry.
        var handler = new HeldPreviews();
        var page = PageWithoutAPanel(new UndoRegistry([handler]));
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var firstEntry = Entry(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));
        var other = AttributeEditorHarness.ForwardEdit(alex, "department", from: "Finance", to: "Audit");
        other["operationId"] = "op-forward-2";
        var nextEntry = sameEntry ? firstEntry : Entry(other);

        var closed = ShowUndoPreview(page, firstEntry);
        await CloseUndoPanel(page);

        Assert.False(Field<bool>(page, "undoLoading"));

        var next = ShowUndoPreview(page, nextEntry);

        Assert.False(next.IsCompleted);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Same(nextEntry, Field<object?>(page, "undoTargetEvent"));

        // The closed preview's answer arrives first: dropped, and the new preview is still the one loading.
        var (closedEvent, closedAnswer) = handler.Requests[0];
        closedAnswer.SetResult(PreviewOf(closedEvent) with { OriginalOperationId = "the closed panel's answer" });
        Assert.Null(await Record.ExceptionAsync(() => closed));

        Assert.True(Field<bool>(page, "undoLoading"));
        Assert.NotEqual("the closed panel's answer", Field<UndoPreview?>(page, "undoPreview")?.OriginalOperationId);
        Assert.Same(nextEntry, Field<object?>(page, "undoTargetEvent"));

        // So a third request while it loads is refused, as any is.
        var refused = ShowUndoPreview(page, Entry(other));
        var settled = await Task.WhenAny(refused, Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken));
        var refusedAtOnce = ReferenceEquals(settled, refused);

        // Answered either way, so a request that did start cannot hang the test.
        foreach (var (requested, answer) in handler.Requests.Skip(1))
            answer.TrySetResult(PreviewOf(requested));
        await Task.WhenAll(next, refused);

        Assert.True(refusedAtOnce, "a third preview started while the second was loading");
        Assert.Equal(2, handler.Requests.Count);
        Assert.Same(nextEntry, Field<object?>(page, "undoTargetEvent"));
        Assert.Equal(sameEntry ? "op-forward-1" : "op-forward-2", Field<UndoPreview?>(page, "undoPreview")!.OriginalOperationId);
        Assert.False(Field<bool>(page, "undoLoading"));
    }

    // ---- aud-a26u: a panel that closes takes its preview's activity with it ----------------------------------------

    [Fact]
    public async Task AClosedPanelsPreviewActivity_LeavesTheFrameWithIt_AndTheNextPreviewKeepsItsOwn()
    {
        // aud-a26r cleared the loading guard on close, not the activity: "Building undo preview" stayed in the status
        // frame until the closed preview's handler returned.
        var handler = new HeldPreviews();
        var page = PageWithoutAPanel(new UndoRegistry([handler]));
        var alex = _h.Ad.AddAlex(title: "Lead Analyst");
        var firstEntry = Entry(AttributeEditorHarness.ForwardEdit(alex, "title", from: "Analyst", to: "Lead Analyst"));
        var other = AttributeEditorHarness.ForwardEdit(alex, "department", from: "Finance", to: "Audit");
        other["operationId"] = "op-forward-2";

        var closed = ShowUndoPreview(page, firstEntry);
        Assert.Equal("Building undo preview", Assert.Single(_progress.Active).Label);

        await CloseUndoPanel(page);

        Assert.False(closed.IsCompleted, "the closed preview's handler has not answered yet");
        Assert.Empty(_progress.Active);

        var next = ShowUndoPreview(page, Entry(other));
        var nextActivity = Assert.Single(_progress.Active);

        // The closed preview answers: its finally completes its own activity, which is already gone, and leaves the
        // next one's alone.
        var (closedEvent, closedAnswer) = handler.Requests[0];
        closedAnswer.SetResult(PreviewOf(closedEvent));
        Assert.Null(await Record.ExceptionAsync(() => closed));
        Assert.Equal(nextActivity.Id, Assert.Single(_progress.Active).Id);

        // And closing that panel takes the next one's activity too.
        await CloseUndoPanel(page);
        Assert.Empty(_progress.Active);

        var (nextEvent, nextAnswer) = handler.Requests[1];
        nextAnswer.SetResult(PreviewOf(nextEvent));
        Assert.Null(await Record.ExceptionAsync(() => next));
        Assert.Empty(_progress.Active);
        Assert.Null(Field<UndoPreview?>(page, "undoPreview"));
    }

    /// <summary>An undo handler whose every preview waits for the test to answer it.</summary>
    private sealed class HeldPreviews : IUndoableModule
    {
        private readonly List<(Dictionary<string, object?> Event, TaskCompletionSource<UndoPreview> Answer)> _requests = [];

        public IReadOnlyList<(Dictionary<string, object?> Event, TaskCompletionSource<UndoPreview> Answer)> Requests
        {
            get { lock (_requests) return _requests.ToList(); }
        }

        public string ModuleId => "ADAttributeEditor";

        public bool CanUndo(Dictionary<string, object?> auditEvent) => true;

        public Task<UndoPreview> PreviewUndoAsync(Dictionary<string, object?> auditEvent, ClaimsPrincipal? actingUser)
        {
            var answer = new TaskCompletionSource<UndoPreview>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_requests)
                _requests.Add((auditEvent, answer));
            return answer.Task;
        }

        public Task<UndoResult> ExecuteUndoAsync(
            Dictionary<string, object?> auditEvent, string performedBy, string ip, string ticket, ClaimsPrincipal? actingUser) =>
            throw new InvalidOperationException("no undo runs in these tests");

        public void LogUndoRefused(
            Dictionary<string, object?> auditEvent, string performedBy, string ip, string ticket, string refusedAt, string reason)
        {
        }
    }

    /// <summary>Counts every report and throws once it has, as a failing provider can (aud-a28u).</summary>
    private sealed class ThrowingLogger<T> : ILogger<T>
    {
        private int _attempts;

        public int Attempts => Volatile.Read(ref _attempts);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Interlocked.Increment(ref _attempts);
            throw new InvalidOperationException("the logging provider failed");
        }
    }

    /// <summary>
    /// A progress channel that keeps how each activity ended (aud-a63). The real one drops the outcome, because its frame is
    /// live only, so an activity completed as a success where it failed could not be seen through it.
    /// </summary>
    private sealed class RecordingProgress : IActivityProgress
    {
        private readonly List<(string Label, bool Success, string? Message)> _completed = [];

        public IReadOnlyList<(string Label, bool Success, string? Message)> Completed
        {
            get { lock (_completed) return _completed.ToList(); }
        }

        public IReadOnlyList<ProgressActivity> Active => [];

        public bool HasActive => false;

        public event Action? Changed
        {
            add { }
            remove { }
        }

        public IActivityHandle Begin(string moduleId, string label, ActivitySize size) => new Handle(this, label);

        public void Cancel(string id)
        {
        }

        public void CancelAll()
        {
        }

        private void Add(string label, bool success, string? message)
        {
            lock (_completed)
                _completed.Add((label, success, message));
        }

        private sealed class Handle(RecordingProgress owner, string label) : IActivityHandle
        {
            private int _ended;

            public string Id { get; } = Guid.NewGuid().ToString("N");

            public CancellationToken CancellationToken => CancellationToken.None;

            public void Report(int done, string? detail = null)
            {
            }

            public void Step(string detail)
            {
            }

            // The first call wins, as the real handle's does.
            public void Complete(bool success, string? message = null)
            {
                if (Interlocked.Exchange(ref _ended, 1) == 0)
                    owner.Add(label, success, message);
            }

            public IProgress<ActivityUpdate> AsProgress() => new Progress<ActivityUpdate>();

            public void Dispose() => Complete(false, "This did not finish.");
        }
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
