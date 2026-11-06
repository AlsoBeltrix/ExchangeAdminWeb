using System.Management.Automation;
using System.Text;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2d, Calendar Permissions: the folder identity every calendar write takes is built from the
/// mailbox the protected-principal check validated, never from the string the operator typed.
/// </summary>
/// <remarks>
/// The typed alias names a DIFFERENT mailbox in Exchange, with its own calendar folder name, so a
/// folder identity built from the typed string - which is what these writes did before - would
/// point at another mailbox's calendar and say so in the recorded identity.
/// </remarks>
public sealed class CalendarPermissionWriteIdentityTests : IDisposable
{
    private const string Typed = "sales";
    private const string Trustee = "jdoe@contoso.test";
    private static readonly Guid Checked = Guid.Parse("0d0d0d0d-1111-4222-8333-444444444444");
    private static readonly Guid Vip = Guid.Parse("0e0e0e0e-5555-4666-8777-888888888888");

    private readonly ExchangeWriteHarness _h = new();

    public CalendarPermissionWriteIdentityTests()
    {
        _h.AddMailbox("shared@contoso.test", Checked, calendarFolder: "Calendar");
        _h.AddMailbox("vip@contoso.test", Vip, calendarFolder: "Kalender", aliases: Typed);
    }

    public void Dispose() => _h.Dispose();

    private static string Folder(Guid guid) => guid.ToString("D") + @":\Calendar";

    private CalendarPermissionService Service() =>
        new(_h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace);

    [Fact]
    public async Task ASet_WritesTheValidatedMailboxsCalendar_NotTheStringTyped()
    {
        var result = await Service().SetCalendarPermissionAsync(Typed, ExchangeWriteHarness.Validated(Checked), Trustee, CalendarAccessRight.Reviewer, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Checked.ToString("D"), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-Mailbox")), "Identity"));
        Assert.Equal(Checked.ToString("D"), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-MailboxFolderStatistics")), "Identity"));
        Assert.Equal(Folder(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-MailboxFolderPermission")), "Identity"));
        Assert.Empty(_h.CallsTo("Add-MailboxFolderPermission"));
    }

    [Fact]
    public async Task ASetThatFallsBackToAdd_AddsOnTheValidatedMailboxsCalendar()
    {
        // No existing entry for the trustee: Set fails and the write falls back to Add, which
        // must take the same folder identity.
        _h.Failures["Set-MailboxFolderPermission"] = "There is no existing permission entry found for user: jdoe.";

        var result = await Service().SetCalendarPermissionAsync(Typed, ExchangeWriteHarness.Validated(Checked), Trustee, CalendarAccessRight.Reviewer, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Folder(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Add-MailboxFolderPermission")), "Identity"));
    }

    [Fact]
    public async Task ARemove_WritesTheValidatedMailboxsCalendar_NotTheStringTyped()
    {
        var result = await Service().RemoveCalendarPermissionAsync(Typed, ExchangeWriteHarness.Validated(Checked), Trustee, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        Assert.Equal(Checked.ToString("D"), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-Mailbox")), "Identity"));
        Assert.Equal(Folder(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Remove-MailboxFolderPermission")), "Identity"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithoutAValidatedMailbox_NothingIsWritten_AndNoConnectionIsBorrowed(bool set)
    {
        var result = set
            ? await Service().SetCalendarPermissionAsync(Typed, null, Trustee, CalendarAccessRight.Reviewer, TestContext.Current.CancellationToken)
            : await Service().RemoveCalendarPermissionAsync(Typed, null, Trustee, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ValidatedMailbox.Refusal(Typed), result.Message);
        Assert.Empty(_h.Calls);
        Assert.Equal(0, _h.Connects);
    }

    [Fact]
    public async Task AReadBackOfADifferentMailbox_RefusesBeforeAnyWrite()
    {
        _h.Mailboxes[Checked.ToString("D")] = _h.Mailboxes["vip@contoso.test"];

        var result = await Service().SetCalendarPermissionAsync(Typed, ExchangeWriteHarness.Validated(Checked), Trustee, CalendarAccessRight.Reviewer, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("different mailbox", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
    }

    // ---- on-premises ---------------------------------------------------------------------------

    private sealed class OnPremService : CalendarPermissionService
    {
        private readonly ExchangeWriteHarness _h;
        public int CredentialRequests;

        public OnPremService(ExchangeWriteHarness h)
            : base(h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, h.Config, null!, h.Trace) => _h = h;

        protected override Task<(string username, string password, string domain)?> GetModuleCredentialsAsync(string purpose, CancellationToken cancellationToken)
        {
            CredentialRequests++;
            return Task.FromResult<(string username, string password, string domain)?>(("svc", "secret", "CONTOSO"));
        }

        protected override void ConnectOnPrem(PowerShell ps, string username, string password, string domain, int maxRetries = 3, CancellationToken cancellationToken = default)
            => _h.DefineOnPremSession(ps);
    }

    [Fact]
    public async Task AnOnPremSet_SendsTheValidatedMailbox_AndWritesItsCalendar()
    {
        var result = await new OnPremService(_h).SetCalendarPermissionOnPremAsync(Typed, ExchangeWriteHarness.Validated(Checked), Trustee, "Reviewer", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        // Sweep item 23: the folder read from the validated mailbox, then the Set on that folder - two commands, each one.
        var sent = _h.CallsTo("Invoke-Command");
        Assert.Equal(2, sent.Count);
        Assert.Equal(Checked.ToString("D"), ExchangeWriteHarness.Argument(sent[0], 0));
        Assert.Equal(Checked.ToString("D"), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Get-MailboxFolderStatistics")), "Identity"));
        Assert.Equal(Folder(Checked), ExchangeWriteHarness.Argument(sent[1], 0));
        Assert.Equal(Folder(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-MailboxFolderPermission")), "Identity"));
        Assert.Empty(_h.CallsTo("Add-MailboxFolderPermission"));
    }

    [Fact]
    public async Task AnOnPremRemove_SendsTheValidatedMailbox_AndWritesItsCalendar()
    {
        var result = await new OnPremService(_h).RemoveCalendarPermissionOnPremAsync(Typed, ExchangeWriteHarness.Validated(Checked), Trustee, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        var sent = _h.CallsTo("Invoke-Command");
        Assert.Equal(2, sent.Count);
        Assert.Equal(Checked.ToString("D"), ExchangeWriteHarness.Argument(sent[0], 0));
        Assert.Equal(Folder(Checked), ExchangeWriteHarness.Argument(sent[1], 0));
        Assert.Equal(Folder(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Remove-MailboxFolderPermission")), "Identity"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task WithoutAValidatedMailbox_TheOnPremPathAsksForNoCredential_AndRunsNothing(bool set)
    {
        var service = new OnPremService(_h);

        var result = set
            ? await service.SetCalendarPermissionOnPremAsync(Typed, null, Trustee, "Reviewer", TestContext.Current.CancellationToken)
            : await service.RemoveCalendarPermissionOnPremAsync(Typed, null, Trustee, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ValidatedMailbox.Refusal(Typed), result.Message);
        Assert.Equal(0, service.CredentialRequests);
        Assert.Empty(_h.Calls);
    }

    // ---- sweep item 23: one command per Invoke-Command, the folder read on-premises -----------------

    [Fact]
    public async Task AnOnPremSetWithNoEntryToSet_AddsInACommandOfItsOwn_OnTheSameFolder()
    {
        _h.Failures["Set-MailboxFolderPermission"] = "There is no existing permission entry found for user: jdoe.";

        var result = await new OnPremService(_h).SetCalendarPermissionOnPremAsync(Typed, ExchangeWriteHarness.Validated(Checked), Trustee, "Reviewer", TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        var sent = _h.CallsTo("Invoke-Command");
        Assert.Equal(3, sent.Count);
        Assert.Contains("Set-MailboxFolderPermission", (string)sent[1]["Script"]!, StringComparison.Ordinal);
        Assert.DoesNotContain("Add-MailboxFolderPermission", (string)sent[1]["Script"]!, StringComparison.Ordinal);
        Assert.Contains("Add-MailboxFolderPermission", (string)sent[2]["Script"]!, StringComparison.Ordinal);
        var added = Assert.Single(_h.CallsTo("Add-MailboxFolderPermission"));
        Assert.Equal(Folder(Checked), ExchangeWriteHarness.Bound(added, "Identity"));
        Assert.Equal("Reviewer", ExchangeWriteHarness.Bound(added, "AccessRights")?.ToString());
    }

    [Fact]
    public async Task AnOnPremSetRefusedForAnotherReason_IsThatFailure_NoAdd_NotUnconfirmed()
    {
        _h.Failures["Set-MailboxFolderPermission"] = "The user 'jdoe' isn't valid for this operation.";

        var result = await new OnPremService(_h).SetCalendarPermissionOnPremAsync(Typed, ExchangeWriteHarness.Validated(Checked), Trustee, "Reviewer", TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("isn't valid for this operation", result.Message, StringComparison.Ordinal);
        Assert.False(result.Unconfirmed);
        Assert.Empty(_h.CallsTo("Add-MailboxFolderPermission"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOnPremWriteOnALocalizedCalendar_WritesThatFolder_NotBackslashCalendar(bool set)
    {
        var service = new OnPremService(_h);
        var result = set
            ? await service.SetCalendarPermissionOnPremAsync(Typed, ExchangeWriteHarness.Validated(Vip), Trustee, "Reviewer", TestContext.Current.CancellationToken)
            : await service.RemoveCalendarPermissionOnPremAsync(Typed, ExchangeWriteHarness.Validated(Vip), Trustee, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Message);
        var written = Assert.Single(_h.CallsTo(set ? "Set-MailboxFolderPermission" : "Remove-MailboxFolderPermission"));
        Assert.Equal(Vip.ToString("D") + @":\Kalender", ExchangeWriteHarness.Bound(written, "Identity"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AnOnPremFolderReadThatFails_WritesNothing_AndSaysSo(bool set)
    {
        _h.Failures["Get-MailboxFolderStatistics"] = "The operation couldn't be performed because the mailbox is unavailable.";
        var service = new OnPremService(_h);

        var result = set
            ? await service.SetCalendarPermissionOnPremAsync(Typed, ExchangeWriteHarness.Validated(Checked), Trustee, "Reviewer", TestContext.Current.CancellationToken)
            : await service.RemoveCalendarPermissionOnPremAsync(Typed, ExchangeWriteHarness.Validated(Checked), Trustee, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("calendar folder could not be read", result.Message, StringComparison.Ordinal);
        Assert.Contains("Nothing was written", result.Message, StringComparison.Ordinal);
        Assert.False(result.Unconfirmed);
        Assert.Empty(_h.CallsTo("Set-MailboxFolderPermission"));
        Assert.Empty(_h.CallsTo("Add-MailboxFolderPermission"));
        Assert.Empty(_h.CallsTo("Remove-MailboxFolderPermission"));
    }

    [Theory]
    [InlineData(CalendarPermissionService.OnPremSetScript)]
    [InlineData(CalendarPermissionService.OnPremAddScript)]
    [InlineData(CalendarPermissionService.OnPremRemoveScript)]
    public void EachOnPremWriteScript_ConvertsToOneCommand_AsTheNoLanguageEndpointRequires(string script)
    {
        // What Invoke-Command does before it sends: a block that converts goes as a command; one that does not goes as
        // script text, which a no-language endpoint refuses.
        // The conversion needs a runspace's context, so it is asked inside one.
        using var ps = PowerShell.Create();
        ps.AddScript("param($s) [scriptblock]::Create($s).GetPowerShell('folder', 'user', 'Reviewer').Commands.Commands.Count").AddArgument(script);
        var converted = ps.Invoke();
        Assert.False(ps.HadErrors, string.Join(" | ", ps.Streams.Error.Select(e => e.ToString())));
        Assert.Equal(1, (int)Assert.Single(converted).BaseObject);
    }

    [Fact]
    public void ANoLanguageRefusal_IsNotADeadSession_SoTheWriteIsNotFiledUnconfirmed()
    {
        var refusal = new RemoteException("The syntax is not supported by this runspace. This can occur if the runspace is in no-language mode.");

        Assert.False(ExoConnectionPool.IsConnectionError(refusal));
        Assert.False(ExchangeServiceBase.OutcomeUnknown(refusal, new ExchangeServiceBase.ConnectionErrorTracker()));
        // Still a dead session: a runspace that has closed, in words with no language-mode phrase.
        Assert.True(ExoConnectionPool.IsConnectionError(new InvalidOperationException("The runspace is not open.")));
    }

    // ---- the CSV bulk loop ---------------------------------------------------------------------

    [Fact]
    public async Task TheBulkLoop_WritesEachRowsOwnValidatedCalendar_AndRefusesARowWithNone()
    {
        var resolver = ExchangeWriteHarness.ExchangeKnows(Typed, "shared@contoso.test", Checked);
        var email = Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());
        // aud-a69: the trustee is someone - the directory holds their account. One neither Exchange nor the directory holds is
        // refused by the self-grant check as a check that could not be completed.
        _h.Forest.AddUser("jdoe", upn: Trustee, mail: Trustee, proxies: [$"SMTP:{Trustee}"]);

        var csv = "Target,User,AccessRight\n"
            + $"{Typed},{Trustee},Reviewer\n"
            + $"ghost@contoso.test,{Trustee},Reviewer\n";
        // aud-a43: run as the harness's signed-in operator - the self-grant check refuses a row it cannot say who granted.
        var bulk = await Service().ProcessCalendarPermissionsCsvAsync(
            new MemoryStream(Encoding.UTF8.GetBytes(csv)), isSet: true, _h.NothingProtectedValidator(resolver), ExchangeWriteHarness.OperatorLogonName,
            _h.Audit, email, "10.0.0.5", "INC100", actingUser: _h.Operator);

        Assert.Equal(1, bulk.SuccessCount);
        Assert.Equal(1, bulk.FailedCount);
        Assert.Equal(Folder(Checked), ExchangeWriteHarness.Bound(Assert.Single(_h.CallsTo("Set-MailboxFolderPermission")), "Identity"));

        var rows = _h.AuditRows();
        var written = Assert.Single(rows, r => r.GetProperty("action").GetString() == "BulkSetCalendar");
        Assert.Equal(Typed, written.GetProperty("target").GetString());
        Assert.Equal(Checked.ToString("D"), written.GetProperty(ValidatedMailbox.AuditKey).GetString());

        var refused = Assert.Single(rows, r => r.GetProperty("action").GetString() == "BulkSetCalendar_ExcludedTarget");
        Assert.Equal("ghost@contoso.test", refused.GetProperty("target").GetString());
        Assert.Equal(PermissionValidator.NoMailboxMessage("ghost@contoso.test"), refused.GetProperty("error").GetString());
    }
}
