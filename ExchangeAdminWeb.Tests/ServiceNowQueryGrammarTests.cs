using System.Net;
using System.Text;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding A30: the ticket number was interpolated into ServiceNow's
/// <c>sysparm_query</c>, whose grammar is parsed after url-decoding, and the ticket that came
/// back was never compared to the one asked for.
/// </summary>
/// <remarks>
/// Behaviour, through the real <c>ValidateTicketAsync</c> over a stub handler, so the real URL
/// construction and response handling run.
///
/// The owner has said ticket validation is not enabled in this deployment and will not be
/// until ServiceNow admins provide an API login. That lowers the urgency, not the defect -
/// the code is what will run the day it is switched on.
/// </remarks>
public class ServiceNowQueryGrammarTests
{
    private sealed class Stub(string body) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }

    private static (ServiceNowService Service, Stub Handler) Service(string responseBody)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceNow:InstanceUrl"] = "https://snow.local",
            ["ServiceNow:Username"] = "u",
            ["ServiceNow:Password"] = "p",
            ["ServiceNow:Enabled"] = "true",
        }).Build();

        var handler = new Stub(responseBody);
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));

        return (new ServiceNowService(config, NullLogger<ServiceNowService>.Instance, factory), handler);
    }

    private const string ActiveIncident =
        """{"result":[{"number":"INC0012345","state":"2","short_description":"Printer","sys_id":"abc"}]}""";

    [Fact]
    public async Task AnOrdinaryTicket_StillValidates()
    {
        // Without this, a fix that refused everything would satisfy the rest of the file.
        var (service, _) = Service(ActiveIncident);

        var result = await service.ValidateTicketAsync("INC0012345");

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("INC0012345^ORnumberISNOTEMPTY")]
    [InlineData("INC0012345^state=1")]
    [InlineData("INC0012345,INC0099999")]
    [InlineData("number=INC0012345")]
    public async Task ATicketCarryingQueryGrammar_IsRefusedBeforeAnyRequest(string hostile)
    {
        // Uri.EscapeDataString protects the transport; ServiceNow decodes before parsing its
        // own grammar, so "^OR" smuggled in here would have become a second clause matching
        // any ticket.
        var (service, handler) = Service(ActiveIncident);

        var result = await service.ValidateTicketAsync(hostile);

        Assert.False(result.IsValid);
        Assert.Empty(handler.Urls);
    }

    [Fact]
    public async Task ADifferentTicketComingBack_IsRefused()
    {
        // The second half: the response was trusted to be about what was asked for. Any row
        // returned had its state read as though it were this ticket's.
        var (service, _) = Service(
            """{"result":[{"number":"INC0099999","state":"2","short_description":"Something else","sys_id":"xyz"}]}""");

        var result = await service.ValidateTicketAsync("INC0012345");

        Assert.False(result.IsValid);
        Assert.Contains("different ticket", result.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AResponseWithNoNumberField_IsRefused()
    {
        var (service, _) = Service("""{"result":[{"state":"2","short_description":"No number","sys_id":"xyz"}]}""");

        var result = await service.ValidateTicketAsync("INC0012345");

        Assert.False(result.IsValid);
    }
}
