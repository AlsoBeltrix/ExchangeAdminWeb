using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The on-premises half of a message trace searches every transport server's tracking log. When the
/// list of transport servers cannot be read, or names none, the search still runs - against the
/// connected server alone - and the result carries a warning saying so, with the reason logged.
/// </summary>
/// <remarks>
/// The real <see cref="MessageTraceService.GetMessageTraceAsync"/> over the service's seams: its
/// connect defines a stand-in session (an Invoke-Command run locally; Get-TransportService answering
/// as the test says; Get-MessageTrackingLog noting the server it was sent to and answering one
/// message), and the cloud half is a pool of stand-in connections that answer nothing. The on-premises
/// throttle is the base's own, process-wide, so this class shares the collection of the others that
/// take its slots.
/// </remarks>
[Collection(OnPremThrottleCollection.Name)]
public sealed class MessageTraceOnPremServerLookupTests : IDisposable
{
    private const string OneServerOnly =
        "On-prem message tracking searched one server only: the list of transport servers could not be read. Messages handled by other servers may be missing.";

    private readonly ExchangeWriteHarness _h = new();
    private readonly CapturingLogger _log = new();

    public void Dispose() => _h.Dispose();

    private async Task<MessageTraceResponse> Search(string lookup)
    {
        using var pool = Pool();
        var service = new Trace(_h, pool, WithAnOnPremServer(), _log, lookup);
        return await service.GetMessageTraceAsync(
            "sender@contoso.test", null, DateTime.Today.AddDays(-5), DateTime.Today, null, null, TestContext.Current.CancellationToken);
    }

    // The server each on-premises tracking query was sent to, in order; "(none)" where none was named.
    private string[] ServersSearched() =>
        _h.CallsTo("Get-MessageTrackingLog").Select(call => call["Server"]?.ToString() ?? "(none)").ToArray();

    private static string[] OnPremRows(MessageTraceResponse response) =>
        response.Results.Where(r => r.Backend == "OnPrem").Select(r => r.Server).Order().ToArray();

    [Fact]
    public async Task WhenTheServerLookupNamesTheServers_EachIsSearched_AndNothingIsWarned()
    {
        var response = await Search("two servers");

        Assert.Equal(["MBX1", "MBX2"], ServersSearched());
        Assert.Equal(["MBX1", "MBX2"], OnPremRows(response));
        Assert.Empty(response.Warnings);
        Assert.Null(response.Error);
        Assert.DoesNotContain(_log.Entries, entry => entry.Level >= LogLevel.Warning);
    }

    [Theory]
    [InlineData("throws", "The transport configuration could not be read: access is denied.")]
    [InlineData("no servers", "no servers returned")]
    public async Task WhenTheServerLookupFailsOrNamesNone_TheConnectedServerIsStillSearched_AndTheResultSaysOneServerOnly(string lookup, string reason)
    {
        var response = await Search(lookup);

        // The search ran: one query, sent to no named server, and its message is in the result.
        Assert.Single(_h.CallsTo("Get-TransportService"));
        Assert.Equal(["(none)"], ServersSearched());
        Assert.Equal(["CONNECTED"], OnPremRows(response));
        Assert.Null(response.Error);
        // The result says what it does not cover, once, and the log carries why.
        Assert.Equal([OneServerOnly], response.Warnings);
        var logged = Assert.Single(_log.Entries, entry => entry.Level == LogLevel.Warning);
        Assert.Equal(
            $"The on-prem transport servers could not be listed ({reason}); message tracking searched the connected server only",
            logged.Message);
    }

    // ---- the stand-ins ----------------------------------------------------------------------------

    private IConfiguration WithAnOnPremServer() => new ConfigurationBuilder()
        .AddConfiguration(_h.Config)
        .AddInMemoryCollection(new Dictionary<string, string?> { ["OnPremExchange:ServerUri"] = "https://exchange.contoso.test/PowerShell/" })
        .Build();

    /// <summary>The service with its on-premises session and its credential replaced.</summary>
    private sealed class Trace(ExchangeWriteHarness h, ExoConnectionPool pool, IConfiguration config, ILogger<MessageTraceService> log, string lookup)
        : MessageTraceService(pool, null!, log, config, null!, h.Trace)
    {
        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3, CancellationToken cancellationToken = default)
        {
            var proxy = ps.Runspace.SessionStateProxy;
            proxy.SetVariable("Calls", h.Calls);
            proxy.SetVariable("ServerLookup", lookup);
            using (var define = PowerShell.Create())
            {
                define.Runspace = ps.Runspace;
                define.AddScript(OnPremStandIns).Invoke();
                Assert.False(define.HadErrors, "The on-premises stand-ins could not be defined.");
            }
            proxy.SetVariable("onpremSession", "stand-in-session");
        }

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken) =>
            Task.FromResult<(string username, string password, string domain)?>(("svc-trace", "not-a-secret", "CONTOSO"));
    }

    // The remote session, run locally. The server lookup fails as a broken remote call does - the call itself
    // throws - or answers no server, or two. Each tracking query notes the server it was sent to and answers
    // one message from it ("CONNECTED" when it was sent to none).
    private const string OnPremStandIns = """
        function global:Invoke-Command {
            [CmdletBinding()] param($Session, [scriptblock] $ScriptBlock, [object[]] $ArgumentList)
            if ($global:ServerLookup -eq 'throws' -and "$ScriptBlock" -like '*Get-TransportService*') {
                [void]$global:Calls.Add(@{ '__Command' = 'Get-TransportService' })
                throw 'The transport configuration could not be read: access is denied.'
            }
            & $ScriptBlock @ArgumentList
        }
        function global:Remove-PSSession { param($Session) }
        function global:Get-TransportService {
            [CmdletBinding()] param()
            [void]$global:Calls.Add(@{ '__Command' = 'Get-TransportService' })
            if ($global:ServerLookup -eq 'two servers') {
                [pscustomobject]@{ Name = 'MBX1' }
                [pscustomobject]@{ Name = 'MBX2' }
            }
        }
        function global:Get-MessageTrackingLog {
            [CmdletBinding()] param($Start, $End, $ResultSize, $Server, $Sender, $Recipients, $MessageSubject, $MessageId)
            $call = @{ '__Command' = 'Get-MessageTrackingLog' }
            if ($PSBoundParameters.ContainsKey('Server')) { $call['Server'] = "$Server" }
            [void]$global:Calls.Add($call)
            $from = if ($PSBoundParameters.ContainsKey('Server')) { "$Server" } else { 'CONNECTED' }
            [pscustomobject]@{
                Timestamp = (Get-Date); Sender = 'sender@contoso.test'; Recipients = @('recipient@contoso.test')
                MessageSubject = 'Quarterly figures'; EventId = 'DELIVER'; MessageId = "<$from@contoso.test>"
                TotalBytes = 2048; ClientIp = '10.0.0.1'; ServerIp = '10.0.0.2'; ServerHostname = $from
                InternalMessageId = "id-$from"
            }
        }
        """;

    // Exchange Online: the cloud search answers nothing.
    private const string CloudStandIns = """
        function global:Get-MessageTraceV2 {
            [CmdletBinding()] param($StartDate, $EndDate, $ResultSize, $SenderAddress, $RecipientAddress, $MessageId, $Subject, $SubjectFilterType)
        }
        function global:Disconnect-ExchangeOnline { param($ConnectionId, $Confirm) }
        """;

    private ExoConnectionPool Pool() =>
        new(_h.Config, _h.ModuleConfig, _h.Enablement, Microsoft.Extensions.Logging.Abstractions.NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            (_, generation, _) =>
            {
                var runspace = RunspaceFactory.CreateRunspace();
                runspace.Open();
                using (var define = PowerShell.Create())
                {
                    define.Runspace = runspace;
                    define.AddScript(CloudStandIns).Invoke();
                }
                var ps = PowerShell.Create();
                ps.Runspace = runspace;
                return new PooledRunspace(runspace, ps, generation, "stand-in");
            });

    private sealed class CapturingLogger : ILogger<MessageTraceService>
    {
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public (LogLevel Level, string Message)[] Entries
        {
            get { lock (_entries) return _entries.ToArray(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
