using System.Text;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-88: one directory-read credential fetch per operation. On the dev log one grant fetched the protected-principal
/// directory-read secret three times before its group check timed out (4.1 s, 2.7 s, 2.7 s): the target's resolution, the
/// proof of its mailbox and the group check each fetched it, and the self-grant check fetches it again. Driven end to end
/// through the real validator and the real protected-principal service - every directory read in a session of the stand-in
/// forest, the secret from the harness's counting stand-in store. Nothing here reaches a directory, Exchange or the store.
/// </summary>
public sealed class DirectoryReadCredentialOnceTests : IDisposable
{
    private const string Target = "shared@contoso.test";
    private const string Other = "other@contoso.test";
    private static readonly Guid TargetMailbox = Guid.Parse("0f0f0f0f-4444-4555-8666-888888888888");
    private const string TargetSid = SelfGrantDirectoryStandIn.DomainSid + "-3999";
    private static readonly string ReadBySid = $"Get-ADObject {SelfGrantDirectoryStandIn.GlobalCatalogEndpoint} (objectSid={TargetSid})";

    private readonly ExchangeWriteHarness _h = new();
    private readonly string _protectedGroupDn;

    public DirectoryReadCredentialOnceTests()
    {
        _h.AddMailbox(Target, TargetMailbox);

        // The target is a synced mailbox: Graph reports its account's SID, so the resolution reads the typed string, reads the
        // account by that SID at the global catalog, and the group check reads its groups - each of the three a fetch, before.
        _h.GraphSids["ext-" + Target] = TargetSid;
        _h.Forest.Add(new SelfGrantDirectoryStandIn.Entry
        {
            Dn = $"CN=shared,OU=Staff,{SelfGrantDirectoryStandIn.DomainDn}",
            Class = "user",
            Upn = Target,
            Mail = Target,
            Sam = "shared",
            Sid = TargetSid,
            PrimaryGroupId = SelfGrantDirectoryStandIn.DomainUsersRid,
            Proxies = [$"SMTP:{Target}"],
        });
        var other = _h.Forest.AddUser("other", upn: Other, mail: Other, proxies: [$"SMTP:{Other}"]);
        _protectedGroupDn = _h.Forest.AddGroup("VIP Staff", members: [other]).Dn;
    }

    public void Dispose() => _h.Dispose();

    /// <summary>Exchange names the target as a synced mailbox; nobody else.</summary>
    private static IIdentityResolver ExchangeKnowsTheSyncedTarget()
    {
        var resolver = ExchangeWriteHarness.ExchangeKnows(Target, Target, TargetMailbox);
        resolver.ResolveRecipientAsync(Target).Returns(
            new ResolvedRecipient(Target, "ext-" + Target, "UserMailbox", ExistsOnPrem: true, ExchangeGuid: TargetMailbox));
        return resolver;
    }

    private PermissionValidator Validator() =>
        _h.ProtectedValidator(ExchangeKnowsTheSyncedTarget(), new ProtectedPrincipalConfig { Groups = [_protectedGroupDn] });

    private void AssertEveryCheckRead()
    {
        // Each of the reads that used to fetch for itself was made: the resolution, the read by SID, the group check.
        Assert.Contains(_h.Forest.Searches, search => search.StartsWith("Get-ADUser ", StringComparison.Ordinal));
        Assert.Contains(_h.Forest.Searches, search => search == ReadBySid);
        Assert.Contains(_h.Forest.Searches, search => search.EndsWith(" " + SelfGrantDirectoryStandIn.TokenGroups, StringComparison.Ordinal));
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task TheTargetCheck_FetchesTheCredentialOnce_ForItsResolutionItsReadBySidAndItsGroupCheck()
    {
        var validation = await Validator().ValidateTargetMailboxAsync(Target, _h.Operator, "MailboxPermissions");

        Assert.Null(validation.Error);
        Assert.Equal(TargetMailbox, validation.Mailbox!.ExchangeGuid);
        AssertEveryCheckRead();
        Assert.Equal(1, _h.DirectoryReadFetches);
    }

    [Fact]
    public async Task ASingleChange_FetchesTheCredentialOnce_ForTheTargetCheckAndTheSelfGrantCheck()
    {
        // The page handler's shape: one operation over both checks of the change.
        var validator = Validator();
        using (DirectoryReadOperation.Begin())
        {
            Assert.Null((await validator.ValidateTargetMailboxAsync(Target, _h.Operator, "MailboxPermissions")).Error);
            Assert.Null(await validator.ValidateSelfGrantAsync(_h.Operator, Other));
        }

        AssertEveryCheckRead();
        Assert.Equal(1, _h.DirectoryReadFetches);
    }

    [Fact]
    public async Task TwoChanges_FetchOncePerChange_NothingIsKeptBetweenThem()
    {
        var validator = Validator();
        for (var change = 0; change < 2; change++)
        {
            using var operation = DirectoryReadOperation.Begin();
            Assert.Null((await validator.ValidateTargetMailboxAsync(Target, _h.Operator, "MailboxPermissions")).Error);
            Assert.Null(await validator.ValidateSelfGrantAsync(_h.Operator, Other));
        }

        Assert.Equal(2, _h.DirectoryReadFetches);
    }

    public static TheoryData<string> Loops => ["Mailbox Permissions", "Calendar Permissions"];

    [Theory]
    [MemberData(nameof(Loops))]
    public async Task ABulkFile_FetchesTheCredentialOnce_ForEveryRowsChecks(string loop)
    {
        // aud-a70's rule for the file, now for the protected-principal check as well as the self-grant check: three rows, three
        // target checks and three self-grant checks, one fetch.
        var validator = Validator();
        var email = Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());
        var rows = string.Concat(Enumerable.Repeat(
            loop == "Mailbox Permissions" ? $"{Target},{Other},True,False,False\n" : $"{Target},{Other},Reviewer\n", 3));

        if (loop == "Mailbox Permissions")
        {
            var service = new MailboxPermissionService(_h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace);
            await service.ProcessMailboxPermissionsCsvAsync(
                new MemoryStream(Encoding.UTF8.GetBytes("Target,User,FullAccess,SendAs,AutoMapping\n" + rows)), isAdd: true, validator,
                ExchangeWriteHarness.OperatorLogonName, _h.Audit, email, "10.0.0.5", "INC100", actingUser: _h.Operator);
        }
        else
        {
            var service = new CalendarPermissionService(_h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace);
            await service.ProcessCalendarPermissionsCsvAsync(
                new MemoryStream(Encoding.UTF8.GetBytes("Target,User,AccessRight\n" + rows)), isSet: true, validator,
                ExchangeWriteHarness.OperatorLogonName, _h.Audit, email, "10.0.0.5", "INC100", actingUser: _h.Operator);
        }

        AssertEveryCheckRead();
        Assert.Equal(3, _h.Forest.Searches.Count(search => search == ReadBySid));
        Assert.Equal(1, _h.DirectoryReadFetches);
    }
}
