using System.Management.Automation;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// sweep-c item 13. Every Exchange write and pre-write read names the mailbox by its ExchangeGuid (aud-a2d,
/// <see cref="ValidatedMailbox.Identity"/>) - an assumption only the dev deployment can confirm. If Exchange does not take
/// that form every change is refused with "couldn't be found", naming a GUID the operator never typed. These pin that the
/// refusal says what it is and is logged with the cmdlet and the GUID, and - a tripwire - that every cmdlet the sweep named
/// still takes the GUID, so a later change cannot half-revert aud-a2d.
/// </summary>
public sealed class MailboxGuidIdentityTests : IDisposable
{
    private const string Trustee = "jdoe@contoso.test";
    private static readonly Guid Checked = Guid.Parse("0c0c0c0c-1111-4222-8333-555555555555");
    private static string Guid_ => Checked.ToString("D");

    private readonly ExchangeWriteHarness _h = new();
    private readonly CapturingLogger _log = new();
    private readonly ILogger _previous = MailboxGuidIdentityRefusal.Logger;

    public MailboxGuidIdentityTests()
    {
        _h.AddMailbox("shared@contoso.test", Checked);
        MailboxGuidIdentityRefusal.Logger = _log;
    }

    public void Dispose()
    {
        MailboxGuidIdentityRefusal.Logger = _previous;
        _h.Dispose();
    }

    private static string NotFound(string identity) =>
        $"The operation couldn't be performed because object '{identity}' couldn't be found on 'stand-in'.";

    private MailboxPermissionService Service() =>
        new(_h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace);

    // ---- the refusal says what it is ---------------------------------------------------------------

    [Fact]
    public async Task AReadBackRefusedForTheGuid_SaysTheMailboxGuidIdentityWasRefused_AndLogsTheCmdletAndTheGuid()
    {
        // Exchange does not know the mailbox by its GUID: the read-back before the write is told "couldn't be found".
        _h.Mailboxes.Remove(Guid_);

        var result = await Service().AddMailboxPermissionsAsync("shared@contoso.test", ExchangeWriteHarness.Validated(Checked), Trustee,
            fullAccess: true, sendAs: false, autoMapping: false, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.StartsWith($"{MailboxGuidIdentityRefusal.Marker} {Guid_} for Get-Mailbox", result.Message, StringComparison.Ordinal);
        Assert.Contains("couldn't be found", result.Message, StringComparison.Ordinal);
        Assert.Empty(_h.Writes);
        var warning = Assert.Single(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(Guid_, StringComparison.Ordinal));
        Assert.Contains("Get-Mailbox", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWriteRefusedForTheGuid_SaysSo_ForThatRight()
    {
        // The read-back takes the GUID; the write's cmdlet does not.
        _h.Failures["Add-RecipientPermission"] = NotFound(Guid_);

        var result = await Service().AddMailboxPermissionsAsync("shared@contoso.test", ExchangeWriteHarness.Validated(Checked), Trustee,
            fullAccess: false, sendAs: true, autoMapping: false, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains($"{MailboxGuidIdentityRefusal.Marker} {Guid_} for Add-RecipientPermission", result.Message, StringComparison.Ordinal);
        Assert.Contains(_log.Entries, e => e.Level == LogLevel.Warning && e.Message.Contains(Guid_, StringComparison.Ordinal)
            && e.Message.Contains("Add-RecipientPermission", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ATrusteeNotFound_IsExchangesOwnWords_NotTheGuidRefusal()
    {
        _h.Failures["Add-MailboxPermission"] = NotFound(Trustee);

        var result = await Service().AddMailboxPermissionsAsync("shared@contoso.test", ExchangeWriteHarness.Validated(Checked), Trustee,
            fullAccess: true, sendAs: false, autoMapping: false, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.DoesNotContain(MailboxGuidIdentityRefusal.Marker, result.Message, StringComparison.Ordinal);
        Assert.Contains(NotFound(Trustee).TrimEnd('.'), result.Message, StringComparison.Ordinal);
    }

    public static TheoryData<string, string> Shapes => new()
    {
        { "Identity", "Set-Place" },
        { "Member", "Add-DistributionGroupMember" },
        { "folder", "Add-MailboxFolderPermission" },
        { "on-premises", "Invoke-Command (Add-MailboxPermission)" },
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void EachWayACommandNamesTheMailbox_IsRecognised(string shape, string cmdlet)
    {
        using var ps = PowerShell.Create();
        switch (shape)
        {
            case "Identity":
                ps.AddCommand("Set-Place").AddParameter("Identity", Guid_).AddParameter("City", "x");
                break;
            case "Member":
                ps.AddCommand("Add-DistributionGroupMember").AddParameter("Identity", "rooms@contoso.test").AddParameter("Member", Guid_);
                break;
            case "folder":
                ps.AddCommand("Add-MailboxFolderPermission").AddParameter("Identity", Guid_ + @":\Calendar").AddParameter("User", Trustee);
                break;
            default:
                ps.AddCommand("Invoke-Command")
                  .AddParameter("ScriptBlock", ScriptBlock.Create("param($Identity, $User) Add-MailboxPermission -Identity $Identity -User $User"))
                  .AddParameter("ArgumentList", new object[] { Guid_, Trustee });
                break;
        }

        var message = MailboxGuidIdentityRefusal.Describe(ps, NotFound(Guid_), []);

        Assert.NotNull(message);
        Assert.StartsWith($"{MailboxGuidIdentityRefusal.Marker} {Guid_} for {cmdlet}:", message, StringComparison.Ordinal);
    }

    [Fact]
    public void AGuidPassedAsTheTrustee_OrAnErrorThatIsNotNotFound_IsLeftAlone()
    {
        using var ps = PowerShell.Create();
        ps.AddCommand("Add-MailboxPermission").AddParameter("Identity", "shared@contoso.test").AddParameter("User", Guid_);
        Assert.Null(MailboxGuidIdentityRefusal.Describe(ps, NotFound(Guid_), []));

        using var other = PowerShell.Create();
        other.AddCommand("Add-MailboxPermission").AddParameter("Identity", Guid_).AddParameter("User", Trustee);
        Assert.Null(MailboxGuidIdentityRefusal.Describe(other, $"Access to {Guid_} is denied.", []));
    }

    // ---- the tripwire: every cmdlet item 13 named still takes .Identity ----------------------------

    public static TheoryData<string, string, string, string> GuidBoundCommands => new()
    {
        // file, cmdlet, parameter, the ValidatedMailbox it is bound to
        { "ExchangeServiceBase.cs", "Get-Mailbox", "Identity", "mailbox" },
        { "MailboxPermissionService.cs", "Add-MailboxPermission", "Identity", "mailbox" },
        { "MailboxPermissionService.cs", "Remove-MailboxPermission", "Identity", "mailbox" },
        { "MailboxPermissionService.cs", "Add-RecipientPermission", "Identity", "mailbox" },
        { "MailboxPermissionService.cs", "Remove-RecipientPermission", "Identity", "mailbox" },
        { "OutOfOfficeService.cs", "Set-MailboxAutoReplyConfiguration", "Identity", "mailbox" },
        { "ConferenceRoomService.cs", "Set-Place", "Identity", "room" },
        { "ConferenceRoomService.cs", "Add-DistributionGroupMember", "Member", "room" },
    };

    [Theory]
    [MemberData(nameof(GuidBoundCommands))]
    public void EveryCmdletTheSweepNamed_IsStillBoundToTheValidatedMailboxIdentity(string file, string cmdlet, string parameter, string holder)
    {
        var code = Source(file);
        var chains = Regex.Matches(code, $@"AddCommand\(""{Regex.Escape(cmdlet)}""\)(?<chain>[^;]*);").Select(m => m.Groups["chain"].Value).ToList();

        Assert.NotEmpty(chains);
        // Get-Mailbox is also the location read of a string (ExchangeServiceBase.GetMailboxLocationAsync); the read-back is
        // the one in ReadValidatedMailbox, pinned below. Every other cmdlet here is the GUID write and nothing else.
        if (cmdlet == "Get-Mailbox")
            Assert.Contains(chains, chain => chain.Contains($@".AddParameter(""{parameter}"", {holder}.Identity)", StringComparison.Ordinal));
        else
            Assert.All(chains, chain => Assert.Contains($@".AddParameter(""{parameter}"", {holder}.Identity)", chain, StringComparison.Ordinal));
    }

    public static TheoryData<string, string, int> GuidBoundCalls => new()
    {
        // The on-premises writes pass the mailbox as the script block's first argument; the calendar folder and the
        // auto-reply read and the room read take it through a helper.
        { "MailboxPermissionService.cs", @"""ArgumentList"", new object[] { mailbox.Identity, ", 4 },
        { "CalendarPermissionService.cs", @"""ArgumentList"", new object[] { mailbox.Identity, ", 2 },
        { "CalendarPermissionService.cs", "GetCalendarFolderName(ps, mailbox.Identity)", 2 },
        { "OutOfOfficeService.cs", "GetOutOfOfficeAsync(mailbox.Identity)", 1 },
        { "ConferenceRoomService.cs", "ReadRoomDetails(ps, tracker, ReadValidatedMailbox(ps, tracker, room), room.Identity)", 1 },
        { "ExchangeServiceBase.cs", "GetMailboxLocationAsync(mailbox.Identity", 1 },
    };

    [Theory]
    [MemberData(nameof(GuidBoundCalls))]
    public void EveryOtherPathTheSweepNamed_StillPassesTheValidatedMailboxIdentity(string file, string call, int count)
    {
        var code = Source(file);

        Assert.Equal(count, Regex.Matches(code, Regex.Escape(call)).Count);
        if (call.StartsWith(@"""ArgumentList""", StringComparison.Ordinal))
            Assert.Equal(count, Regex.Matches(code, Regex.Escape(@"""ArgumentList"", new object[] {")).Count);
    }

    [Fact]
    public void TheIdentity_IsStillTheExchangeGuid()
    {
        var code = Source("ValidatedMailbox.cs");

        Assert.Contains("public string Identity => ExchangeGuid.ToString(\"D\");", code, StringComparison.Ordinal);
        Assert.Contains("MailboxGuidIdentityRefusal.Describe(", Source("ExchangeServiceBase.cs"), StringComparison.Ordinal);
    }

    private static string Source(string file) => File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Services", file));

    private sealed class CapturingLogger : ILogger
    {
        private readonly object _gate = new();
        private readonly List<(LogLevel Level, string Message)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message)> Entries
        {
            get
            {
                lock (_gate)
                    return [.. _entries];
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_gate)
                _entries.Add((logLevel, formatter(state, exception)));
        }
    }
}
