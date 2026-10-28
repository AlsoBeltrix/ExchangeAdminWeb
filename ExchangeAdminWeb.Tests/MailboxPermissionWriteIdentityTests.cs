using System.Management.Automation;
using System.Text;
using ExchangeAdminWeb.Services;
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
            Typed, ExchangeWriteHarness.Validated(Checked), Trustee, fullAccess: true, sendAs: true, autoMapping: false, TestContext.Current.CancellationToken);

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
            Typed, ExchangeWriteHarness.Validated(Checked), Trustee, fullAccess: true, sendAs: true, TestContext.Current.CancellationToken);

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
            ? await Service().AddMailboxPermissionsAsync(Typed, null, Trustee, fullAccess: true, sendAs: true, autoMapping: false, TestContext.Current.CancellationToken)
            : await Service().RemoveMailboxPermissionsAsync(Typed, null, Trustee, fullAccess: true, sendAs: true, TestContext.Current.CancellationToken);

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
            Typed, ExchangeWriteHarness.Validated(Checked), Trustee, fullAccess: true, sendAs: true, autoMapping: false, TestContext.Current.CancellationToken);

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

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken)
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
            Typed, ExchangeWriteHarness.Validated(Checked), Trustee, fullAccess: true, sendAs: true, TestContext.Current.CancellationToken);

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
            Typed, ExchangeWriteHarness.Validated(Checked), Trustee, fullAccess: true, sendAs: true, TestContext.Current.CancellationToken);

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
            ? await service.AddMailboxPermissionsOnPremAsync(Typed, null, Trustee, fullAccess: true, sendAs: true, TestContext.Current.CancellationToken)
            : await service.RemoveMailboxPermissionsOnPremAsync(Typed, null, Trustee, fullAccess: true, sendAs: true, TestContext.Current.CancellationToken);

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
        var resolver = ExchangeWriteHarness.ExchangeKnows(Typed, "shared@contoso.test", Checked);
        var email = Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());
        // aud-a69: the trustee is someone - the directory holds their account. One neither Exchange nor the directory holds is
        // refused by the self-grant check as a check that could not be completed.
        _h.Forest.AddUser("jdoe", upn: Trustee, mail: Trustee, proxies: [$"SMTP:{Trustee}"]);

        var csv = "Target,User,FullAccess,SendAs,AutoMapping\n"
            + $"{Typed},{Trustee},True,False,False\n"
            + $"ghost@contoso.test,{Trustee},True,False,False\n";
        // aud-a43: run as the harness's signed-in operator - the self-grant check refuses a row it cannot say who granted.
        var bulk = await Service().ProcessMailboxPermissionsCsvAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(csv)), isAdd: true, _h.NothingProtectedValidator(resolver), ExchangeWriteHarness.OperatorLogonName,
            _h.Audit, email, "10.0.0.5", "INC100", actingUser: _h.Operator);

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
}
