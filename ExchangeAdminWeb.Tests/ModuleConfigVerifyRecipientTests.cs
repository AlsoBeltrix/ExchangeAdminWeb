using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/DirectoryObjectVerify-Plan.md A8, B.1 (slice V4): the Conference Rooms group addresses read through that module's own
/// pooled Exchange session - the real <see cref="ConferenceRoomService"/> on a real <see cref="ExoConnectionPool"/> whose
/// connect seam hands out a stand-in <c>Get-Recipient</c>. Exchange's not-found, in either of the shapes it reports one, is a
/// confirmed absence (missing); any other failure is unreadable; a recipient is classified by its type. No Exchange.
/// </summary>
public sealed class ModuleConfigVerifyRecipientTests : IDisposable
{
    private readonly ExchangeWriteHarness _h = new();
    private readonly Hashtable _recipients = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));
    private readonly Hashtable _failures = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));
    private readonly ArrayList _calls = ArrayList.Synchronized(new ArrayList());

    public void Dispose() => _h.Dispose();

    // An advanced function, so -ErrorAction Stop makes a written error a throw, as it does for the cmdlet. A failure is written
    // with the category and message Exchange gives it.
    private const string StandInRecipient = """
        [CmdletBinding()] param([string] $Identity)
        $null = $Calls.Add("Get-Recipient $Identity")
        $failure = $Failures[$Identity]
        if ($null -ne $failure) {
            $PSCmdlet.WriteError([System.Management.Automation.ErrorRecord]::new([System.Exception]::new($failure[1]), 'StandIn', $failure[0], $Identity)); return
        }
        if ($Recipients.ContainsKey($Identity)) {
            return [pscustomobject]@{ RecipientType = $Recipients[$Identity]; PrimarySmtpAddress = $Identity; DistinguishedName = "CN=$Identity,OU=Exchange" }
        }
        $PSCmdlet.WriteError([System.Management.Automation.ErrorRecord]::new([System.Exception]::new("The operation couldn't be performed because object '$Identity' couldn't be found on 'srv01'."), 'StandIn', [System.Management.Automation.ErrorCategory]::ObjectNotFound, $Identity))
        """;

    private PooledRunspace StandIn(long generation)
    {
        var state = InitialSessionState.CreateDefault2();
        state.Variables.Add(new SessionStateVariableEntry("Recipients", _recipients, null));
        state.Variables.Add(new SessionStateVariableEntry("Failures", _failures, null));
        state.Variables.Add(new SessionStateVariableEntry("Calls", _calls, null));
        state.Commands.Add(new SessionStateFunctionEntry("Get-Recipient", StandInRecipient));
        var runspace = RunspaceFactory.CreateRunspace(state);
        runspace.Open();
        var ps = PowerShell.Create();
        ps.Runspace = runspace;
        return new PooledRunspace(runspace, ps, generation, "stand-in");
    }

    private ConferenceRoomService Service()
    {
        var pool = new ExoConnectionPool(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            connect: (_, generation, _) => StandIn(generation));
        return new ConferenceRoomService(pool, null!, _h.ModuleConfig, null!, _h.Trace, _h.Config, NullLogger<ConferenceRoomService>.Instance);
    }

    private void Fail(string identity, ErrorCategory category, string message) => _failures[identity] = new object[] { category, message };

    [Fact]
    public async Task ExchangesNotFound_IsMissing_InBothItsShapes_AndAnyOtherFailure_IsUnreadable()
    {
        _recipients["group@contoso.com"] = "MailUniversalSecurityGroup";
        _recipients["person@contoso.com"] = "UserMailbox";
        // Exchange Online's bare-text form: no category, the sentence naming the object asked about (kd-89).
        Fail("bare@contoso.com", ErrorCategory.NotSpecified, "||The operation couldn't be performed because object 'bare@contoso.com' couldn't be found on 'srv01'.");
        Fail("busy@contoso.com", ErrorCategory.ResourceUnavailable, "The server is busy. Try again later.");
        // A not-found about ANOTHER object says nothing about this one.
        Fail("other@contoso.com", ErrorCategory.NotSpecified, "||The operation couldn't be performed because object 'someone-else' couldn't be found on 'srv01'.");

        var addresses = new[] { "group@contoso.com", "person@contoso.com", "nobody@contoso.com", "bare@contoso.com", "busy@contoso.com", "other@contoso.com" };
        var answers = await Service().LookUpRecipientsForVerifyAsync(addresses, CancellationToken.None);

        Assert.Equal(addresses.Length, answers.Count);
        Assert.Equal("MailUniversalSecurityGroup", Assert.Single(Assert.IsType<VerifyLookupResult.Found>(answers[0]).Objects).ObjectClass);
        Assert.Equal("UserMailbox", Assert.Single(Assert.IsType<VerifyLookupResult.Found>(answers[1]).Objects).ObjectClass);
        Assert.Empty(Assert.IsType<VerifyLookupResult.Found>(answers[2]).Objects); // ObjectNotFound: confirmed absence
        Assert.Empty(Assert.IsType<VerifyLookupResult.Found>(answers[3]).Objects); // the sentence about this address
        Assert.Contains("The server is busy", Assert.IsType<VerifyLookupResult.Unreadable>(answers[4]).Reason, StringComparison.Ordinal);
        Assert.IsType<VerifyLookupResult.Unreadable>(answers[5]);
        Assert.Equal(addresses.Select(a => "Get-Recipient " + a), _calls.Cast<string>());
    }

    [Fact]
    public async Task ThroughTheEngine_AGroupVerifies_AnotherRecipientIsTheWrongType_NotFoundIsMissing_AFailureIsUnreadable()
    {
        _recipients["group@contoso.com"] = "MailUniversalDistributionGroup";
        _recipients["person@contoso.com"] = "UserMailbox";
        Fail("busy@contoso.com", ErrorCategory.ResourceUnavailable, "The server is busy.");
        var service = Service();
        var lookup = new ServiceLookup(service);
        var engine = new DirectoryObjectVerifier(new DirectoryObjectVerifierTests.StandInAccess(new DirectoryObjectVerifierTests.StandInVerifyDirectory()),
            [new ModuleConfigVerifyAdapter(lookup)], NullLogger<DirectoryObjectVerifier>.Instance);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["DefaultArbiterGroup"] = "group@contoso.com",
            ["ConfAdminsGroup"] = "person@contoso.com",
            ["ConfCEOGroup"] = "nobody@contoso.com",
            ["ADGTAdminsGroup"] = "busy@contoso.com",
        };

        var report = await engine.VerifyAsync(ModuleConfigVerifyAdapter.ItemsFrom("ConferenceRooms", values, "v"), CancellationToken.None);
        var byKey = report.Results.ToDictionary(r => r.Item.RowKey, r => r.Finding.Kind);

        Assert.Equal(VerifyFindingKind.Verified, byKey["DefaultArbiterGroup"]);
        Assert.Equal(VerifyFindingKind.WrongClass, byKey["ConfAdminsGroup"]);
        Assert.Equal(VerifyFindingKind.Missing, byKey["ConfCEOGroup"]);
        Assert.Equal(VerifyFindingKind.Unreadable, byKey["ADGTAdminsGroup"]);
    }

    private sealed class ServiceLookup(ConferenceRoomService service) : IRecipientGroupLookup
    {
        public Task<IReadOnlyList<VerifyLookupResult>> LookUpAsync(IReadOnlyList<string> addresses, CancellationToken cancellationToken) =>
            service.LookUpRecipientsForVerifyAsync(addresses, cancellationToken);
    }
}
