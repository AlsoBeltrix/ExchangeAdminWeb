using System.Management.Automation;
using System.Security.Principal;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The production directory read behind circuit revalidation (aud-a29b), driven through the
/// <see cref="ISectionAccessDirectoryCommands"/> seam. No runspace is opened and no directory is
/// reached: every command answer is scripted.
/// </summary>
public class OperatorMembershipDirectoryTests
{
    private const string DomainSid = "S-1-5-21-1000-2000-3000";
    private const string OperatorSid = DomainSid + "-1105";
    private const string GroupA = DomainSid + "-5001";
    private const string GroupB = DomainSid + "-5002";
    private const string DomainUsers = DomainSid + "-513";

    [Fact]
    public async Task TheAccountIsReadBySidFromItsOwnDomainWithItsTransitiveGroups()
    {
        var commands = WithDomain().Returns("Get-ADUser", Account(enabled: true, Sids(GroupA, DomainUsers)));
        var directory = Create(commands);

        var membership = await directory.GetMembershipAsync(OperatorSid, "CONTOSO", Ct);

        Assert.True(membership.AccountEnabled);
        Assert.Equal(new[] { DomainUsers, GroupA }.Order(), membership.GroupSids.Order());

        var read = commands.LastCall("Get-ADUser").Parameters;
        Assert.Equal(OperatorSid, read["Identity"]);
        Assert.Equal("contoso.test", read["Server"]);
        Assert.Contains("tokenGroups", Assert.IsType<string[]>(read["Properties"]));
        Assert.Equal("Stop", read["ErrorAction"]);

        // The domain was found in the forest's partitions, not named anywhere.
        var partitions = commands.LastCall("Get-ADObject").Parameters;
        Assert.Equal("CN=Partitions,CN=Configuration,DC=contoso,DC=test", partitions["SearchBase"]);
        Assert.Contains("(netBIOSName=CONTOSO)", (string)partitions["LDAPFilter"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDomainServerIsResolvedOnceAndReused()
    {
        var commands = WithDomain()
            .Returns("Get-ADUser", Account(enabled: true, Sids(GroupA)))
            .Returns("Get-ADUser", Account(enabled: true, Sids(GroupA)));
        var directory = Create(commands);

        await directory.GetMembershipAsync(OperatorSid, "CONTOSO", Ct);
        await directory.GetMembershipAsync(OperatorSid, "contoso", Ct);

        Assert.Single(commands.Calls, c => c.Command == "Get-ADRootDSE");
        Assert.Equal(2, commands.Calls.Count(c => c.Command == "Get-ADUser"));
        Assert.All(commands.Calls.Where(c => c.Command == "Get-ADUser"), c => Assert.Equal("contoso.test", c.Parameters["Server"]));
    }

    [Fact]
    public async Task AFailedReadForgetsTheCachedServer()
    {
        var commands = WithDomain()
            .Returns("Get-ADUser", Account(enabled: true, Sids(GroupA)))
            .Returns("Get-ADUser", DirectoryCommandOutcome.Failure("The server is not operational"));
        commands.Returns("Get-ADRootDSE", RootDse()).Returns("Get-ADObject", Partition())
            .Returns("Get-ADUser", Account(enabled: true, Sids(GroupA)));
        var directory = Create(commands);

        await directory.GetMembershipAsync(OperatorSid, "CONTOSO", Ct);
        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => directory.GetMembershipAsync(OperatorSid, "CONTOSO", Ct));
        await directory.GetMembershipAsync(OperatorSid, "CONTOSO", Ct);

        Assert.Equal(2, commands.Calls.Count(c => c.Command == "Get-ADRootDSE"));
    }

    [Fact]
    public async Task WithoutADomainTheHostsOwnDomainIsQueried()
    {
        var commands = new FakeCommands().Returns("Get-ADUser", Account(enabled: true, Sids(GroupA)));

        await Create(commands).GetMembershipAsync(OperatorSid, null, Ct);

        Assert.DoesNotContain(commands.Calls, c => c.Command == "Get-ADRootDSE");
        Assert.False(commands.LastCall("Get-ADUser").Parameters.ContainsKey("Server"));
    }

    [Fact]
    public void TokenGroupsAreReadInEveryShapeTheCmdletCanProduce()
    {
        var binary = new byte[new SecurityIdentifier(GroupB).BinaryLength];
        new SecurityIdentifier(GroupB).GetBinaryForm(binary, 0);

        var sids = OperatorMembershipDirectory.ParseSids(new object?[]
        {
            new SecurityIdentifier(GroupA),
            binary,
            new PSObject(new SecurityIdentifier(DomainUsers)),
            DomainSid + "-5003",
            "not a sid",
            "BA",
            null,
        });

        Assert.Equal(new[] { DomainUsers, GroupA, GroupB, DomainSid + "-5003" }.Order(), sids.Order());
        Assert.Equal(new[] { GroupA }, OperatorMembershipDirectory.ParseSids(GroupA).ToArray());
        Assert.Empty(OperatorMembershipDirectory.ParseSids(null));
    }

    [Fact]
    public async Task ADisabledAccountIsReportedDisabled()
    {
        var commands = WithDomain().Returns("Get-ADUser", Account(enabled: false, Sids(GroupA)));

        var membership = await Create(commands).GetMembershipAsync(OperatorSid, "CONTOSO", Ct);

        Assert.False(membership.AccountEnabled);
    }

    [Theory]
    [InlineData("a cmdlet error")]
    [InlineData("an account and an error")]
    [InlineData("no account")]
    [InlineData("only a null row")]
    [InlineData("two accounts")]
    [InlineData("an unreadable enabled state")]
    [InlineData("no group memberships")]
    [InlineData("no tokenGroups at all")]
    public async Task AnAnswerThatProvesNothingIsAFailureNotAnEmptyMembership(string shape)
    {
        var answer = shape switch
        {
            "a cmdlet error" => DirectoryCommandOutcome.Failure("Cannot find an object with identity"),

            // Rows and an error together prove nothing about the account: a partial failure must
            // not read as a confident answer.
            "an account and an error" => new DirectoryCommandOutcome(
                new DirectoryObject?[] { AccountRow(true, Sids(GroupA)) }, "A referral was returned from the server"),
            "no account" => DirectoryCommandOutcome.Success(),
            "only a null row" => DirectoryCommandOutcome.Success(new DirectoryObject?[] { null }),
            "two accounts" => DirectoryCommandOutcome.Success(AccountRow(true, Sids(GroupA)), AccountRow(true, Sids(GroupA))),
            "an unreadable enabled state" => DirectoryCommandOutcome.Success(Row(("tokenGroups", Sids(GroupA)))),
            "no group memberships" => DirectoryCommandOutcome.Success(AccountRow(true, Array.Empty<SecurityIdentifier>())),
            _ => DirectoryCommandOutcome.Success(Row(("Enabled", true))),
        };
        var commands = WithDomain().Returns("Get-ADUser", answer);

        var ex = await Assert.ThrowsAsync<DirectoryUnavailableException>(
            () => Create(commands).GetMembershipAsync(OperatorSid, "CONTOSO", Ct));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message), shape);
    }

    [Fact]
    public async Task ACommandThatThrowsIsReportedAsDirectoryUnavailable()
    {
        var commands = WithDomain();
        commands.ThrowOn("Get-ADUser", new InvalidOperationException("ADWS is not running"));

        var ex = await Assert.ThrowsAsync<DirectoryUnavailableException>(
            () => Create(commands).GetMembershipAsync(OperatorSid, "CONTOSO", Ct));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    [Fact]
    public async Task ADomainOutsideTheForestIsAFailure()
    {
        var commands = new FakeCommands()
            .Returns("Get-ADRootDSE", RootDse())
            .Returns("Get-ADObject", DirectoryCommandOutcome.Success());

        await Assert.ThrowsAsync<DirectoryUnavailableException>(
            () => Create(commands).GetMembershipAsync(OperatorSid, "ELSEWHERE", Ct));

        Assert.DoesNotContain(commands.Calls, c => c.Command == "Get-ADUser");
    }

    [Fact]
    public async Task TheCommandSessionIsDisposedAfterEveryRead()
    {
        var commands = WithDomain().Returns("Get-ADUser", DirectoryCommandOutcome.Failure("boom"));

        await Assert.ThrowsAsync<DirectoryUnavailableException>(
            () => Create(commands).GetMembershipAsync(OperatorSid, "CONTOSO", Ct));

        Assert.Equal(1, commands.Disposals);
    }

    [Fact(Timeout = 20000)]
    public async Task CancellationStopsTheRunningCommand()
    {
        var commands = new BlockingCommands();
        var directory = new OperatorMembershipDirectory(() => commands, _ => null);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var lookup = directory.GetMembershipAsync(OperatorSid, null, cancel.Token);
        Assert.True(commands.Started.Wait(TimeSpan.FromSeconds(10), Ct), "The command never started.");
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.True(commands.StopRequested.Wait(TimeSpan.FromSeconds(10), Ct), "The running command was not stopped.");
    }

    [Fact]
    public async Task ABlankAccountSidNeverReachesTheDirectory()
    {
        var commands = new FakeCommands();

        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => Create(commands).GetMembershipAsync(" ", null, Ct));

        Assert.Empty(commands.Calls);
    }

    [Fact]
    public void GroupNameTranslation_PassesTheAnswerThroughAndWrapsAFailure()
    {
        var directory = new OperatorMembershipDirectory(() => new FakeCommands(), name => name == @"CONTOSO\Admins" ? GroupA : null);

        Assert.Equal(GroupA, directory.TranslateGroupName(@"CONTOSO\Admins"));
        Assert.Null(directory.TranslateGroupName(@"CONTOSO\Nobody"));
        Assert.Null(directory.TranslateGroupName("  "));

        var failing = new OperatorMembershipDirectory(() => new FakeCommands(),
            _ => throw new System.ComponentModel.Win32Exception(1789));
        Assert.Throws<DirectoryUnavailableException>(() => failing.TranslateGroupName(@"CONTOSO\Admins"));
    }

    // ------------------------------------------------------------------ helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OperatorMembershipDirectory Create(FakeCommands commands) => new(() => commands, _ => null);

    private static SecurityIdentifier[] Sids(params string[] sids) => sids.Select(s => new SecurityIdentifier(s)).ToArray();

    private static DirectoryObject Row(params (string Name, object? Value)[] properties) =>
        new(properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase));

    private static DirectoryObject AccountRow(bool enabled, SecurityIdentifier[] groups) =>
        Row(("Enabled", enabled), ("tokenGroups", new System.Collections.ArrayList(groups)), ("SID", new SecurityIdentifier(OperatorSid)));

    private static DirectoryCommandOutcome Account(bool enabled, SecurityIdentifier[] groups) =>
        DirectoryCommandOutcome.Success(AccountRow(enabled, groups));

    private static DirectoryCommandOutcome RootDse() =>
        DirectoryCommandOutcome.Success(Row(("configurationNamingContext", "CN=Configuration,DC=contoso,DC=test")));

    private static DirectoryCommandOutcome Partition() =>
        DirectoryCommandOutcome.Success(Row(("netBIOSName", "CONTOSO"), ("dnsRoot", new[] { "contoso.test" })));

    private static FakeCommands WithDomain() =>
        new FakeCommands().Returns("Get-ADRootDSE", RootDse()).Returns("Get-ADObject", Partition());

    private sealed record RecordedCall(string Command, IReadOnlyDictionary<string, object?> Parameters);

    private sealed class FakeCommands : ISectionAccessDirectoryCommands
    {
        private readonly Dictionary<string, Queue<DirectoryCommandOutcome>> _responses = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, Exception> _throws = new(StringComparer.OrdinalIgnoreCase);

        public List<RecordedCall> Calls { get; } = [];

        public int Disposals { get; private set; }

        public FakeCommands Returns(string command, DirectoryCommandOutcome outcome)
        {
            if (!_responses.TryGetValue(command, out var queue))
                _responses[command] = queue = new Queue<DirectoryCommandOutcome>();
            queue.Enqueue(outcome);
            return this;
        }

        public void ThrowOn(string command, Exception exception) => _throws[command] = exception;

        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters)
        {
            Calls.Add(new RecordedCall(command, parameters));

            if (_throws.TryGetValue(command, out var exception))
                throw exception;

            if (_responses.TryGetValue(command, out var queue) && queue.Count > 0)
                return queue.Dequeue();

            // An unscripted command is a test bug, not an empty directory.
            throw new InvalidOperationException($"No response scripted for '{command}'.");
        }

        public string? TranslateSidToNTAccount(string sid) => null;

        public void Dispose() => Disposals++;

        public RecordedCall LastCall(string command) =>
            Calls.Last(c => string.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase));
    }

    private sealed class BlockingCommands : ICancellableDirectoryCommands
    {
        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim StopRequested { get; } = new();

        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters)
            => throw new InvalidOperationException("The cancellable overload must be used.");

        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            Started.Set();
            StopRequested.Wait(TimeSpan.FromSeconds(15), CancellationToken.None);
            throw new OperationCanceledException(cancellationToken);
        }

        public void RequestStop() => StopRequested.Set();

        public string? TranslateSidToNTAccount(string sid) => null;

        // The lookup disposes its session when the worker ends; the test still reads the events
        // afterwards, so disposal leaves them alone.
        public void Dispose()
        {
        }
    }
}
