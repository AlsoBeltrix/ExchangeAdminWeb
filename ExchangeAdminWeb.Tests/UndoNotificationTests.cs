using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A26: undoing an AD attribute edit notified nobody, because dispatch lived in
/// the forward-edit page and undo does not go through it.
/// </summary>
/// <remarks>
/// Split coverage, and the split is the honest part. The dispatch itself is driven for real
/// against a substituted <see cref="EmailService"/>, so the action name, the contents and the
/// swallowed-failure behaviour are observed rather than read. That it is CALLED on both the
/// success and failure paths is a source scan, because <c>UndoAsync</c> reaches a live
/// directory through the lookup long before it gets there.
/// </remarks>
public class UndoNotificationTests
{
    private readonly EmailService _email = Substitute.For<EmailService>(
        new ConfigurationBuilder().Build(), Substitute.For<ILogger<EmailService>>());

    /// <summary>
    /// The service under test with only the two dependencies this method touches.
    /// </summary>
    /// <remarks>
    /// The nulls are deliberate and load-bearing: if <see cref="ADAttributeEditorUndoService.NotifyAdministratorsAsync"/>
    /// ever starts reaching for another dependency, these tests throw rather than quietly
    /// covering less than they claim.
    /// </remarks>
    private ADAttributeEditorUndoService Service() => new(
        null!, null!, null!, null!, null!, null!, _email,
        Substitute.For<ILogger<ADAttributeEditorUndoService>>());

    private static Task Notify(ADAttributeEditorUndoService service, bool success, string? error = null) =>
        service.NotifyAdministratorsAsync(
            performedBy: "admin@contoso.com",
            ip: "10.0.0.1",
            ticket: "INC0009",
            target: "user@contoso.com",
            changedAttributes: ["title", "department"],
            originalOperationId: "op-1234",
            success: success,
            error: error);

    [Fact]
    public async Task AReversalIsAnnouncedAsAnUndo_NotAsAnotherUpdate()
    {
        // Reusing ADAttributeEditor_Update would make the change and its retraction
        // indistinguishable in the mailbox, which defeats the point of sending it.
        await Notify(Service(), success: true);

        await _email.Received(1).SendAdminNotificationAsync(
            "admin@contoso.com", "10.0.0.1", "ADAttributeEditor_Undo", true, "INC0009",
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>());
    }

    [Fact]
    public async Task TheNoticeNamesWhatWasRevertedAndWhichOperation()
    {
        IReadOnlyDictionary<string, string>? sent = null;
        _email.SendAdminNotificationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<string>(), Arg.Do<IReadOnlyDictionary<string, string>>(d => sent = d),
                Arg.Any<string?>())
            .Returns(Task.CompletedTask);

        await Notify(Service(), success: true);

        Assert.NotNull(sent);
        Assert.Equal("user@contoso.com", sent!["Target"]);
        Assert.Equal("title, department", sent["Reverted"]);
        Assert.Equal("op-1234", sent["Original operation"]);

        // A successful reversal carries no error row.
        Assert.False(sent.ContainsKey("Error"));
    }

    [Fact]
    public async Task AFailedReversalSaysSo_AndCarriesTheError()
    {
        IReadOnlyDictionary<string, string>? sent = null;
        _email.SendAdminNotificationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<string>(), Arg.Do<IReadOnlyDictionary<string, string>>(d => sent = d),
                Arg.Any<string?>())
            .Returns(Task.CompletedTask);

        await Notify(Service(), success: false, error: "AD credentials unavailable.");

        await _email.Received(1).SendAdminNotificationAsync(
            Arg.Any<string>(), Arg.Any<string>(), "ADAttributeEditor_Undo", false,
            Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(),
            "AD credentials unavailable.");

        Assert.Equal("AD credentials unavailable.", sent!["Error"]);
    }

    [Fact]
    public async Task AnUnsendableNoticeDoesNotFailTheReversal()
    {
        // The directory is already written and the audit record already exists. A mail server
        // being down must not turn a completed reversal into a reported failure.
        _email.SendAdminNotificationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(),
                Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>())
            .Returns<Task>(_ => throw new InvalidOperationException("smtp down"));

        await Notify(Service(), success: true);
    }

    [Fact]
    public void BothOutcomesOfExecuteUndo_DispatchTheNotice()
    {
        // The wiring, which the tests above cannot reach. ExecuteUndoAsync hits a live directory
        // through its lookup well before the notification, so this is read, not driven - and
        // it is why the four tests above exist to cover what the dispatch actually does.
        var body = AuditCategoryFilingTests.MethodBody(
            File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "ADAttributeEditorUndoService.cs")),
            "public async Task<UndoResult> ExecuteUndoAsync");

        var calls = System.Text.RegularExpressions.Regex.Matches(body, "await NotifyAdministratorsAsync");
        Assert.True(calls.Count == 2, $"Expected the success and failure dispatches; found {calls.Count}.");

        Assert.Contains("success: true", body, StringComparison.Ordinal);
        Assert.Contains("success: false", body, StringComparison.Ordinal);
    }
}
