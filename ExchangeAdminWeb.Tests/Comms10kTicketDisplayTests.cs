using System.Reflection;
using System.Text;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.Comms10k;

namespace ExchangeAdminWeb.Tests;

// Compiled input attributes and binder callbacks; no browser or rendering host is involved.
#pragma warning disable BL0006
public sealed partial class Comms10kRefusalTests
{
    [Theory]
    [InlineData(" INC123 ", "INC123")]
    [InlineData("   ", "")]
    public async Task C6ApprovalLocksTheDisplayedTicketThroughPersistedJobAuditAndNotification(string entered, string expected)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C6Page(fixture, progress);
        var beforeValidation = C6Input(page);
        Assert.False(beforeValidation.Disabled);
        await C6Edit(beforeValidation, entered);
        Assert.Equal(entered, C6Input(page).Value);

        await InvokePage(page, "ValidateEmails");

        Assert.True(CanReplace(page));
        var approvedInput = C6Input(page);
        Assert.True(approvedInput.Disabled);
        Assert.Equal(expected, approvedInput.Value);
        await C6Edit(beforeValidation, "INC999");
        await C6Edit(approvedInput, "INC998");
        Assert.Equal(expected, C6Input(page).Value);
        Assert.Equal(expected, C5Field<string>(page, "ticketNumber"));
        Assert.Equal(expected, C5Field<Comms10kConfirmation>(page, "confirmation").Ticket);

        await InvokeReplace(page);

        var job = Assert.Single(repository.GetActive());
        Assert.Equal(expected, job.Ticket);
        Assert.Equal(expected, C6Input(page).Value);
        Assert.False(C6Input(page).Disabled);
        await C6Edit(C6Input(page), "INC456");
        Assert.Equal("INC456", C6Input(page).Value);
        Assert.Equal(expected, job.Ticket);

        var processor = fixture.Processor(fixture.ResolvingSubstitute());
        Assert.Equal(BulkJobRowStatus.Success, (await processor.ProcessRowAsync(job, Comms10kReplaceProcessor.ResolveRow, TestContext.Current.CancellationToken)).Status);
        Assert.Equal(BulkJobRowStatus.Success, (await processor.ProcessRowAsync(job, Comms10kReplaceProcessor.ReplaceRow, TestContext.Current.CancellationToken)).Status);
        fixture.AssertAudit(null, Comms10kOutcome.Succeeded, success: true, ticket: expected);
        await processor.OnJobCompletedAsync(job);
        var email = (EmailService)typeof(Page).GetProperty("Email", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page)!;
        var notice = Assert.Single(email.ReceivedCalls(), call => call.GetMethodInfo().Name == "SendAdminNotificationAsync");
        Assert.Equal(expected, notice.GetArguments()[4]);
    }

    [Theory]
    [InlineData("yield")]
    [InlineData("ticket")]
    [InlineData("directory")]
    public async Task C6ValidationWaitsKeepTheCapturedTicketVisibleAndRejectDelayedInput(string waitAt)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, _) = C6Page(fixture, progress);
        var input = C6Input(page);
        await C6Edit(input, " INC123 ");
        var gate = new C7Gate();
        var requests = new List<Uri>();
        using var http = new HttpClient(new C7TicketHandler(gate, requests));
        if (waitAt == "ticket") SetPageProperty(page, "ServiceNow", C6ServiceNow(http));
        if (waitAt == "directory") fixture.BeforeCredentialResponse = gate.WaitAsync;
        var context = new C7YieldContext();
        var pending = waitAt == "yield"
            ? context.Start(() => InvokePage(page, "ValidateEmails"))
            : InvokePage(page, "ValidateEmails");
        try
        {
            if (waitAt != "yield") await gate.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
            Assert.False(pending.IsCompleted);
            Assert.True(C6Input(page).Disabled);
            Assert.Equal("INC123", C6Input(page).Value);
            await C6Edit(input, "INC999");
            Assert.Equal("INC123", C6Input(page).Value);
            Assert.Equal("INC123", C5Field<string>(page, "ticketNumber"));
        }
        finally
        {
            gate.Release();
            if (waitAt == "yield") context.Resume();
            await pending;
        }

        Assert.True(CanReplace(page));
        Assert.True(C6Input(page).Disabled);
        Assert.Equal(C5Field<Comms10kConfirmation>(page, "confirmation").Ticket, C6Input(page).Value);
        if (waitAt == "ticket")
            Assert.Contains("number=INC123", Uri.UnescapeDataString(Assert.Single(requests).Query), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("ticket")]
    [InlineData("directory")]
    public async Task C6FailedValidationLeavesTheTicketEditableWithoutApproval(string failure)
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C6Page(fixture, progress);
        await C6Edit(C6Input(page), " INC123 ");
        var gate = new C7Gate();
        gate.Release();
        using var http = new HttpClient(new C7TicketHandler(gate, [], valid: false));
        if (failure == "ticket") SetPageProperty(page, "ServiceNow", C6ServiceNow(http));
        else fixture.Service.Candidates = [];

        await InvokePage(page, "ValidateEmails");

        Assert.False(CanReplace(page));
        Assert.NotNull(C5Field<Comms10kUpdateResult?>(page, "result"));
        Assert.False(C6Input(page).Disabled);
        Assert.Equal("INC123", C6Input(page).Value);
        await C6Edit(C6Input(page), "INC456");
        Assert.Equal("INC456", C6Input(page).Value);
        Assert.Empty(repository.GetActive());
    }

    [Fact]
    public async Task C6NewUploadUnlocksTheTicketAndRequiresAFreshMatchingApproval()
    {
        using var fixture = new Fixture();
        using var progress = new ActivityProgressService();
        var (page, repository) = C6Page(fixture, progress);
        await C6Edit(C6Input(page), "INC123");
        await InvokePage(page, "ValidateEmails");
        Assert.True(C6Input(page).Disabled);

        var file = new C7File("new.csv", new MemoryStream(Encoding.UTF8.GetBytes("Email\r\n" + Address + "\r\n")));
        await C7InvokeOwner(page, "HandleFileUpload", file);

        Assert.False(CanReplace(page));
        Assert.False(C6Input(page).Disabled);
        Assert.Equal("INC123", C6Input(page).Value);
        await C6Edit(C6Input(page), " INC456 ");
        Assert.Equal(" INC456 ", C6Input(page).Value);
        await InvokePage(page, "ValidateEmails");
        Assert.True(C6Input(page).Disabled);
        Assert.Equal("INC456", C6Input(page).Value);
        await InvokeReplace(page);
        Assert.Equal("INC456", Assert.Single(repository.GetActive()).Ticket);
    }

    private static (Page Page, BulkJobRepository Repository) C6Page(Fixture fixture, ActivityProgressService progress)
    {
        var (page, repository, _) = C7Page(fixture, progress);
        SetField(page, "authChecked", true);
        SetField(page, "confirmation", null);
        SetField(page, "resolveResult", null);
        SetField(page, "ticketNumber", "");
        return (page, repository);
    }

    private static ServiceNowService C6ServiceNow(HttpClient http)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient("ServiceNow").Returns(http);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceNow:Enabled"] = "true",
            ["ServiceNow:InstanceUrl"] = "https://tickets.invalid",
        }).Build();
        return new ServiceNowService(config, NullLogger<ServiceNowService>.Instance, factory);
    }

    private sealed record C6Ticket(string Value, bool Disabled, object OnInput);

    private static C6Ticket C6Input(Page page)
    {
        using var builder = new RenderTreeBuilder();
        typeof(Page).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [builder]);
        var frames = builder.GetFrames();
        var input = Assert.Single(Enumerable.Range(0, frames.Count),
            index => frames.Array[index].FrameType == RenderTreeFrameType.Element && frames.Array[index].ElementName == "input");
        var attributes = frames.Array.Skip(input + 1).TakeWhile(frame => frame.FrameType == RenderTreeFrameType.Attribute)
            .ToDictionary(frame => frame.AttributeName, frame => frame.AttributeValue);
        return new C6Ticket(attributes.GetValueOrDefault("value")?.ToString() ?? "",
            Equals(true, attributes.GetValueOrDefault("disabled")), attributes["oninput"]);
    }

    private static async Task C6Edit(C6Ticket input, string value)
    {
        // Invoke the actual generated binder delegate, as the existing handler harness does,
        // without ComponentBase's render dispatch (there is no attached rendering host here).
        var callback = input.OnInput;
        var handler = Assert.IsAssignableFrom<MulticastDelegate>(callback as MulticastDelegate
            ?? callback.GetType().GetField("Delegate", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)!.GetValue(callback));
        if (handler.DynamicInvoke(new ChangeEventArgs { Value = value }) is Task pending) await pending;
    }
}
#pragma warning restore BL0006
