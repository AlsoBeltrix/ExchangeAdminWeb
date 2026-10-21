using System.Management.Automation;
using System.Text;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2d, Mailbox Permissions: every write is bound to the mailbox the protected-principal check
/// validated, never to the string the operator typed.
/// </summary>
/// <remarks>
/// The typed string here is an alias Exchange would resolve to a DIFFERENT mailbox - a protected
/// one - so a write that passed the string again would land somewhere the check never looked. The
/// stand-ins record what each cmdlet was bound with (<see cref="ExchangeWriteHarness"/>).
/// </remarks>
public sealed class MailboxPermissionWriteIdentityTests : IDisposable
{
    private const string Typed = "sales";
    private const string Trustee = "jdoe@contoso.test";
    private static readonly Guid Checked = Guid.Parse("0a0a0a0a-1111-4222-8333-444444444444");
    private static readonly Guid Vip = Guid.Parse("0b0b0b0b-5555-4666-8777-888888888888");

    private readonly ExchangeWriteHarness _h = new();

    public MailboxPermissionWriteIdentityTests()
    {
        // The mailbox the check validated, and the protected one the typed alias names in Exchange.
        _h.AddMailbox("shared@contoso.test", Checked);
        _h.AddMailbox("vip@contoso.test", Vip, aliases: Typed);
    }

    public void Dispose() => _h.Dispose();

    private MailboxPermissionService Service() =>
        new(_h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace);

    private static string Identity(Guid guid) => guid.ToString("D");

    [Fact]
    public async Task AGrant_WritesTheValidatedMailbox_NotTheStringTyped()
    {
        var result = await Service().AddMailboxPermissionsAsync(
            Typed, ExchangeWriteHarness.Validated(Checked), Trustee, fullAccess: true, sendAs: true, autoMapping: false);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-Mailbox")), "Identity"));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Add-MailboxPermission")), "Identity"));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Add-RecipientPermission")), "Identity"));
        Assert.DoesNotContain(_h.Calls.Cast<System.Collections.Hashtable>(),
            call => Equals(ExchangeWriteHarness.Bound(call, "Identity"), Typed));
    }

    [Fact]
    public async Task ARevoke_WritesTheValidatedMailbox_NotTheStringTyped()
    {
        var result = await Service().RemoveMailboxPermissionsAsync(
            Typed, ExchangeWriteHarness.Validated(Checked), Trustee, fullAccess: true, sendAs: true);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-Mailbox")), "Identity"));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Remove-MailboxPermission")), "Identity"));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Remove-RecipientPermission")), "Identity"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithoutAValidatedMailbox_NothingIsWritten_AndNoConnectionIsBorrowed(bool grant)
    {
        var result = grant
            ? await Service().AddMailboxPermissionsAsync(Typed, null, Trustee, fullAccess: true, sendAs: true, autoMapping: false)
            : await Service().RemoveMailboxPermissionsAsync(Typed, null, Trustee, fullAccess: true, sendAs: true);

        Assert.False(result.Success);
        Assert.Equal(ValidatedMailbox.Refusal(Typed), result.Message);
        Assert.Empty(_h.Calls);
        Assert.Equal(0, _h.Connects);
    }

    [Fact]
    public async Task AReadBackOfADifferentMailbox_RefusesBeforeAnyWrite()
    {
        // Exchange answers the GUID with some other mailbox - however that could happen, the write
        // must not follow it.
        _h.Mailboxes[Identity(Checked)] = _h.Mailboxes["vip@contoso.test"];

        var result = await Service().AddMailboxPermissionsAsync(
            Typed, ExchangeWriteHarness.Validated(Checked), Trustee, fullAccess: true, sendAs: true, autoMapping: false);

        Assert.False(result.Success);
        Assert.Contains("different mailbox", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
    }

    // ---- on-premises ---------------------------------------------------------------------------

    /// <summary>The on-prem service with its connection and credential replaced by the harness.</summary>
    private sealed class OnPremService : MailboxPermissionService
    {
        private readonly ExchangeWriteHarness _h;
        public int CredentialRequests;

        public OnPremService(ExchangeWriteHarness h)
            : base(h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, h.Config, null!, h.Trace) => _h = h;

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose)
        {
            CredentialRequests++;
            return Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));
        }

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3)
            => _h.DefineOnPremSession(ps);
    }

    [Fact]
    public async Task AnOnPremGrant_SendsTheValidatedMailbox_AndBindsSendAsToTheMailboxItNames()
    {
        var result = await new OnPremService(_h).AddMailboxPermissionsOnPremAsync(
            Typed, ExchangeWriteHarness.Validated(Checked), Trustee, fullAccess: true, sendAs: true);

        Assert.True(result.Success, result.Message);
        var remote = _h.CallsTo("Invoke-Command");
        Assert.Equal(2, remote.Count);
        Assert.All(remote, call => Assert.Equal(Identity(Checked), ExchangeWriteHarness.Argument(call, 0)));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Add-MailboxPermission")), "Identity"));
        // Send As is bound to the object Get-Mailbox returned for the GUID - the validated mailbox.
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-Mailbox")), "Identity"));
        Assert.Equal("OBJ:shared@contoso.test", ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Add-ADPermission")), "Identity"));
    }

    [Fact]
    public async Task AnOnPremRevoke_SendsTheValidatedMailbox_AndBindsSendAsToTheMailboxItNames()
    {
        var result = await new OnPremService(_h).RemoveMailboxPermissionsOnPremAsync(
            Typed, ExchangeWriteHarness.Validated(Checked), Trustee, fullAccess: true, sendAs: true);

        Assert.True(result.Success, result.Message);
        var remote = _h.CallsTo("Invoke-Command");
        Assert.Equal(2, remote.Count);
        Assert.All(remote, call => Assert.Equal(Identity(Checked), ExchangeWriteHarness.Argument(call, 0)));
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Remove-MailboxPermission")), "Identity"));
        Assert.Equal("OBJ:shared@contoso.test", ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Remove-ADPermission")), "Identity"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithoutAValidatedMailbox_TheOnPremPathAsksForNoCredential_AndRunsNothing(bool grant)
    {
        var service = new OnPremService(_h);

        var result = grant
            ? await service.AddMailboxPermissionsOnPremAsync(Typed, null, Trustee, fullAccess: true, sendAs: true)
            : await service.RemoveMailboxPermissionsOnPremAsync(Typed, null, Trustee, fullAccess: true, sendAs: true);

        Assert.False(result.Success);
        Assert.Equal(ValidatedMailbox.Refusal(Typed), result.Message);
        Assert.Equal(0, service.CredentialRequests);
        Assert.Empty(_h.Calls);
    }

    // ---- the CSV bulk loop ---------------------------------------------------------------------

    [Fact]
    public async Task TheBulkLoop_WritesEachRowsOwnValidatedMailbox_AndRefusesARowWithNone()
    {
        // Nothing protected, so the validator binds each row through Exchange: "sales" is the
        // checked mailbox here (the resolver says so), "ghost" is no mailbox at all.
        var resolver = Substitute.For<IIdentityResolver>();
        // The self-grant check compares object ids; unstubbed, both would be "" and match.
        resolver.ResolveToObjectIdAsync(Arg.Any<string>()).Returns((string?)null);
        resolver.ResolveRecipientAsync(Arg.Any<string>()).Returns((ResolvedRecipient?)null);
        resolver.ResolveRecipientAsync(Typed).Returns(
            new ResolvedRecipient("shared@contoso.test", "ext-shared", "UserMailbox", ExistsOnPrem: false, ExchangeGuid: Checked));
        var email = Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());

        var csv = "Target,User,FullAccess,SendAs,AutoMapping\n"
            + $"{Typed},{Trustee},True,False,False\n"
            + $"ghost@contoso.test,{Trustee},True,False,False\n";
        var bulk = await Service().ProcessMailboxPermissionsCsvAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(csv)), isAdd: true, NothingProtected(resolver), @"CONTOSO\operator",
            _h.Audit, email, "10.0.0.5", "INC100", actingUser: null);

        Assert.Equal(1, bulk.SuccessCount);
        Assert.Equal(1, bulk.FailedCount);
        Assert.Equal(Identity(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Add-MailboxPermission")), "Identity"));

        var rows = _h.AuditRows();
        var written = Assert.Single(rows, r => r.GetProperty("action").GetString() == "BulkAdd_FullAccess");
        Assert.Equal(Typed, written.GetProperty("target").GetString());
        Assert.Equal(Identity(Checked), written.GetProperty(ValidatedMailbox.AuditKey).GetString());

        var refused = Assert.Single(rows, r => r.GetProperty("action").GetString() == "BulkAdd_ExcludedTarget");
        Assert.Equal("ghost@contoso.test", refused.GetProperty("target").GetString());
        Assert.Equal(PermissionValidator.NoMailboxMessage("ghost@contoso.test"), refused.GetProperty("error").GetString());
    }

    /// <summary>A real validator with no protection configured, asking Exchange through <paramref name="resolver"/>.</summary>
    private PermissionValidator NothingProtected(IIdentityResolver resolver)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_h.Dir);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var extLog = new ExtendedLogService(_h.Config, env, TestConfigStore.CreateAppSettings(_h.Dir),
            Substitute.For<ILogger<ExtendedLogService>>());
        var delinea = new DelineaService(httpClientFactory, _h.Config, Substitute.For<ILogger<DelineaService>>(), extLog, _h.Trace);
        var protectedPrincipals = new ProtectedPrincipalService(env, _h.Config, _h.ModuleConfig,
            TestConfigStore.CreateProtectedPrincipal(_h.Dir), delinea, Substitute.For<ILogger<ProtectedPrincipalService>>());
        var servicers = new ProtectedPrincipalServicerService(
            new SectionAccessService(_h.Config, Substitute.For<ILogger<SectionAccessService>>(), env, new ModuleCatalog(),
                new SectionAccessRepository(TestConfigStore.Create(_h.Dir))),
            Substitute.For<ILogger<ProtectedPrincipalServicerService>>());
        var scopeFactory = Substitute.For<IServiceScopeFactory>();
        var scope = Substitute.For<IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(IIdentityResolver)).Returns(resolver);
        return new PermissionValidator(_h.Config, _h.ModuleConfig, _h.Pool, protectedPrincipals, servicers,
            Substitute.For<ILogger<PermissionValidator>>(), scopeFactory);
    }
}
