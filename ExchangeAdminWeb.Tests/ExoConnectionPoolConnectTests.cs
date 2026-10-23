using System.Collections;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// psl-9: the pool's own connect step (<c>CreateConnected</c>), driven through its session and certificate seams over a local
/// runspace whose <c>Import-Module</c>, <c>Get-ConnectionInformation</c>, <c>Connect-ExchangeOnline</c> and
/// <c>Disconnect-ExchangeOnline</c> are stand-in advanced functions. No Exchange module, certificate or tenant is used.
/// </summary>
/// <remarks>
/// The import cleared only the commands: an error record it left - one written past -ErrorAction Stop, which does not throw -
/// stayed in the stream, and <c>ReadConnectionIds</c>, which takes any record for its own failure, read the snapshot before
/// the connect as unreadable. The new connection's id was then unidentified (<c>NewConnectionId</c> refuses an unreadable
/// snapshot), and the drain disposed the connection without <c>Disconnect-ExchangeOnline -ConnectionId</c> (aud-a6b), leaving
/// its server session to time out.
/// </remarks>
public sealed class ExoConnectionPoolConnectTests : IDisposable
{
    private readonly ExchangeWriteHarness _h = new();
    private readonly ArrayList _ids = ArrayList.Synchronized(new ArrayList { "c-existing" });
    private readonly ArrayList _calls = ArrayList.Synchronized(new ArrayList());

    public void Dispose() => _h.Dispose();

    /// <summary>
    /// The connect step's session: the stand-ins over this test's ids, not yet opened, as the seam hands one back. The
    /// import writes an error record past Stop when <paramref name="importWritesAnError"/> - the per-call -ErrorAction
    /// Continue on Write-Error outranks the Stop the pool passes - and the connect adds the id <c>c-new</c>.
    /// </summary>
    private Runspace Session(bool importWritesAnError)
    {
        var state = InitialSessionState.CreateDefault2();
        state.Variables.Add(new SessionStateVariableEntry("ConnectCalls", _calls, null));
        state.Variables.Add(new SessionStateVariableEntry("ConnectIds", _ids, null));
        state.Variables.Add(new SessionStateVariableEntry("ImportWritesAnError", importWritesAnError, null));
        state.Commands.Add(new SessionStateFunctionEntry("Import-Module", """
            [CmdletBinding()] param([string] $Name)
            [void]$ConnectCalls.Add("Import-Module $Name")
            if ($ImportWritesAnError) { Write-Error 'stand-in: a command the module ran reported an error' -ErrorAction Continue }
            """));
        state.Commands.Add(new SessionStateFunctionEntry("Get-ConnectionInformation", """
            [CmdletBinding()] param()
            [void]$ConnectCalls.Add('Get-ConnectionInformation')
            foreach ($id in $ConnectIds) { [pscustomobject]@{ ConnectionId = $id } }
            """));
        state.Commands.Add(new SessionStateFunctionEntry("Connect-ExchangeOnline", """
            [CmdletBinding()] param($AppId, $CertificateThumbprint, $Organization, $ShowBanner)
            [void]$ConnectCalls.Add("Connect-ExchangeOnline $Organization $CertificateThumbprint")
            [void]$ConnectIds.Add('c-new')
            """));
        state.Commands.Add(new SessionStateFunctionEntry("Disconnect-ExchangeOnline", """
            [CmdletBinding()] param($ConnectionId, $Confirm)
            [void]$ConnectCalls.Add("Disconnect-ExchangeOnline $ConnectionId")
            """));
        return RunspaceFactory.CreateRunspace(state);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ANewConnection_IsIdentified_AndDisconnectedByItsOwnId_WhateverTheImportLeft(bool importWritesAnError)
    {
        var logger = new CapturingLogger();
        var pool = new ExoConnectionPool(_h.Config, _h.ModuleConfig, _h.Enablement, logger, _h.Trace)
        {
            ConnectRunspace = () => Session(importWritesAnError),
            CertificateThumbprint = _ => "0123456789ABCDEF",
        };

        try
        {
            var pooled = await pool.BorrowAsync(TestContext.Current.CancellationToken);

            Assert.Equal("c-new", pooled.ConnectionId);
            Assert.Contains("Connect-ExchangeOnline contoso.onmicrosoft.com 0123456789ABCDEF", _calls.Cast<string>());
            Assert.DoesNotContain(logger.Warnings, warning => warning.Contains("could not identify", StringComparison.Ordinal));

            pool.Return(pooled);
            pool.DrainPool();

            Assert.Contains("Disconnect-ExchangeOnline c-new", _calls.Cast<string>());
            Assert.Equal(0, pool.FailedDisconnects);
        }
        finally
        {
            pool.Dispose();
        }
    }

    /// <summary>Keeps the text of every warning the pool logs.</summary>
    private sealed class CapturingLogger : ILogger<ExoConnectionPool>
    {
        private readonly List<string> _warnings = [];

        public IReadOnlyList<string> Warnings
        {
            get { lock (_warnings) return _warnings.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
                lock (_warnings) _warnings.Add(formatter(state, exception));
        }
    }
}
