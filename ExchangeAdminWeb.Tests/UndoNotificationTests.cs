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
/// The undo's own dispatcher, driven for real against a substituted <see cref="EmailService"/>,
/// so the action name, the contents and the swallowed-failure behaviour are observed rather than
/// read. Since aud-a26b that dispatcher sends only the exception-path notice; the returned
/// results are announced by SaveAsync, and that is driven end to end through ExecuteUndoAsync
/// in <see cref="ADAttributeEditorNotificationTests"/>, which replaced the source scan that used
/// to count three dispatches here.
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
    public void AnExceptionDuringUndoIsAuditedAsWellAsNotified()
    {
        // Review of A26. The notification tells administrators; the audit record is what
        // answers "did anyone try to reverse this, and what happened?" months later. The
        // catch wrote neither.
        var body = AuditCategoryFilingTests.MethodBody(
            File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", "ADAttributeEditorUndoService.cs")),
            "public async Task<UndoResult> ExecuteUndoAsync");

        var catchAt = body.LastIndexOf("catch (Exception ex)", StringComparison.Ordinal);
        Assert.True(catchAt > 0, "the outer catch must still be there");

        var tail = body[catchAt..];
        Assert.Contains("LogUndoAudit(", tail, StringComparison.Ordinal);
        Assert.Contains("NotifyAdministratorsAsync(", tail, StringComparison.Ordinal);
    }
}
