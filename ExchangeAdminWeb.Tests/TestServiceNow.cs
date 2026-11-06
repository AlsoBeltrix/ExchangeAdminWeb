using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Sweep item 41. ServiceNow clients for the page harnesses that set a page's injected services by hand: the ticket checks
/// the six pages gained ask ServiceNow after the presence rule, as every other ticket-gated page does.
/// </summary>
internal static class TestServiceNow
{
    /// <summary>ServiceNow:Enabled unset: the integration is dormant and every present ticket validates.</summary>
    public static ServiceNowService Dormant()
    {
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        return new ServiceNowService(new ConfigurationBuilder().Build(), NullLogger<ServiceNowService>.Instance, http);
    }

    /// <summary>
    /// ServiceNow enabled, answering every ticket lookup with <paramref name="responseBody"/>; <c>{"result":[]}</c> is a ticket
    /// it does not hold. The requests it was sent are counted.
    /// </summary>
    public static (ServiceNowService Service, List<string> Requests) Enabled(string responseBody)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ServiceNow:InstanceUrl"] = "https://snow.local",
            ["ServiceNow:Username"] = "u",
            ["ServiceNow:Password"] = "p",
            ["ServiceNow:Enabled"] = "true",
        }).Build();

        var handler = new Stub(responseBody);
        var http = Substitute.For<IHttpClientFactory>();
        http.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient(handler, disposeHandler: false));
        return (new ServiceNowService(config, NullLogger<ServiceNowService>.Instance, http), handler.Requests);
    }

    private sealed class Stub(string body) : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!.ToString());
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json"),
            });
        }
    }
}
