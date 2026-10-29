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
/// <remarks>
/// A lookup is two reads (kd-87): the account by its SID, for its distinguished name, then its
/// <c>tokenGroups</c> and enabled state by a base-scope search at that name. <see cref="FakeCommands"/>
/// answers each command from a queue, so a lookup that works takes two Get-ADUser answers
/// (<see cref="FakeCommands.ReturnsAccount"/>). <see cref="BaseSearchOnlyDirectory"/> answers as Active
/// Directory does on the point kd-87 is about.
/// </remarks>
public class OperatorMembershipDirectoryTests
{
    private const string DomainSid = "S-1-5-21-1000-2000-3000";
    private const string OperatorSid = DomainSid + "-1105";
    private const string OtherAccountSid = DomainSid + "-1999";
    private const string GroupA = DomainSid + "-5001";
    private const string GroupB = DomainSid + "-5002";
    private const string DomainUsers = DomainSid + "-513";

    // An escaped comma in the name, as a "Surname, Given" account has: the name is passed on as read.
    private const string OperatorDn = @"CN=Operator\, Test,OU=Staff,DC=contoso,DC=test";

    private const string BaseSearchRefusal = "The requested search operation is only supported for base searches.";

    [Fact]
    public async Task TheAccountIsReadBySidFromItsOwnDomainWithItsTransitiveGroups()
    {
        var commands = WithDomain().ReturnsAccount(enabled: true, Sids(GroupA, DomainUsers));
        var directory = Create(commands);

        var membership = await directory.GetMembershipAsync(OperatorSid, "CONTOSO", Ct);

        Assert.True(membership.AccountEnabled);
        Assert.Equal(new[] { DomainUsers, GroupA }.Order(), membership.GroupSids.Order());

        var reads = commands.Calls.Where(c => c.Command == "Get-ADUser").Select(c => c.Parameters).ToList();
        Assert.Equal(2, reads.Count);

        // First the account by its SID, asking for nothing beyond the cmdlet's defaults - so no
        // tokenGroups, which AD refuses in a search that is not base-scope (kd-87).
        var lookup = reads[0];
        Assert.Equal(OperatorSid, lookup["Identity"]);
        Assert.False(lookup.ContainsKey("Properties"));
        Assert.Equal("contoso.test", lookup["Server"]);
        Assert.Equal("Stop", lookup["ErrorAction"]);

        // Then tokenGroups and Enabled by a base-scope search at the name the first read returned,
        // bound by the SID, from the same server.
        var groupRead = reads[1];
        Assert.False(groupRead.ContainsKey("Identity"));
        Assert.Equal(OperatorDn, groupRead["SearchBase"]);
        Assert.Equal("Base", groupRead["SearchScope"]);
        Assert.Equal("(objectSid=" + OperatorSid + ")", groupRead["LDAPFilter"]);
        var properties = Assert.IsType<string[]>(groupRead["Properties"]);
        Assert.Contains("tokenGroups", properties);
        Assert.Contains("Enabled", properties);
        Assert.Equal("contoso.test", groupRead["Server"]);
        Assert.Equal("Stop", groupRead["ErrorAction"]);

        // The domain was found in the forest's partitions, not named anywhere.
        var partitions = commands.LastCall("Get-ADObject").Parameters;
        Assert.Equal("CN=Partitions,CN=Configuration,DC=contoso,DC=test", partitions["SearchBase"]);
        Assert.Contains("(netBIOSName=CONTOSO)", (string)partitions["LDAPFilter"]!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheDomainServerIsResolvedOnceAndReused()
    {
        var commands = WithDomain()
            .ReturnsAccount(enabled: true, Sids(GroupA))
            .ReturnsAccount(enabled: true, Sids(GroupA));
        var directory = Create(commands);

        await directory.GetMembershipAsync(OperatorSid, "CONTOSO", Ct);
        await directory.GetMembershipAsync(OperatorSid, "contoso", Ct);

        Assert.Single(commands.Calls, c => c.Command == "Get-ADRootDSE");
        Assert.Equal(4, commands.Calls.Count(c => c.Command == "Get-ADUser"));
        Assert.All(commands.Calls.Where(c => c.Command == "Get-ADUser"), c => Assert.Equal("contoso.test", c.Parameters["Server"]));
    }

    [Theory]
    [InlineData("the account lookup")]
    [InlineData("the group read")]
    public async Task AFailedReadForgetsTheCachedServer(string failingRead)
    {
        var commands = WithDomain().ReturnsAccount(enabled: true, Sids(GroupA));
        if (failingRead == "the group read")
            commands.Returns("Get-ADUser", Located());
        commands.Returns("Get-ADUser", DirectoryCommandOutcome.Failure("The server is not operational"));
        commands.Returns("Get-ADRootDSE", RootDse()).Returns("Get-ADObject", Partition())
            .ReturnsAccount(enabled: true, Sids(GroupA));
        var directory = Create(commands);

        await directory.GetMembershipAsync(OperatorSid, "CONTOSO", Ct);
        await Assert.ThrowsAsync<DirectoryUnavailableException>(() => directory.GetMembershipAsync(OperatorSid, "CONTOSO", Ct));
        await directory.GetMembershipAsync(OperatorSid, "CONTOSO", Ct);

        Assert.Equal(2, commands.Calls.Count(c => c.Command == "Get-ADRootDSE"));
        Assert.Equal(failingRead == "the group read" ? 6 : 5, commands.Calls.Count(c => c.Command == "Get-ADUser"));
    }

    [Fact]
    public async Task WithoutADomainTheHostsOwnDomainIsQueried()
    {
        var commands = new FakeCommands().ReturnsAccount(enabled: true, Sids(GroupA));

        await Create(commands).GetMembershipAsync(OperatorSid, null, Ct);

        Assert.DoesNotContain(commands.Calls, c => c.Command == "Get-ADRootDSE");
        var reads = commands.Calls.Where(c => c.Command == "Get-ADUser").ToList();
        Assert.Equal(2, reads.Count);
        Assert.All(reads, c => Assert.False(c.Parameters.ContainsKey("Server")));
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
        // The account lookup's row says enabled; the enabled state is the group read's.
        var commands = WithDomain().ReturnsAccount(enabled: false, Sids(GroupA));

        var membership = await Create(commands).GetMembershipAsync(OperatorSid, "CONTOSO", Ct);

        Assert.False(membership.AccountEnabled);
    }

    [Theory]
    [InlineData("a cmdlet error")]
    [InlineData("an account and an error")]
    [InlineData("no account")]
    [InlineData("only a null row")]
    [InlineData("two accounts")]
    [InlineData("no distinguished name")]
    [InlineData("a blank distinguished name")]
    public async Task AnAccountLookupThatProvesNothingIsAFailureAndNoGroupReadIsMade(string shape)
    {
        var answer = shape switch
        {
            "a cmdlet error" => DirectoryCommandOutcome.Failure("Cannot find an object with identity"),
            "an account and an error" => new DirectoryCommandOutcome(
                new DirectoryObject?[] { LocatedRow() }, "A referral was returned from the server"),
            "no account" => DirectoryCommandOutcome.Success(),
            "only a null row" => DirectoryCommandOutcome.Success(new DirectoryObject?[] { null }),
            "two accounts" => DirectoryCommandOutcome.Success(LocatedRow(), LocatedRow()),
            "no distinguished name" => DirectoryCommandOutcome.Success(
                Row(("SID", new SecurityIdentifier(OperatorSid)), ("Enabled", true))),
            _ => DirectoryCommandOutcome.Success(
                Row(("DistinguishedName", " "), ("SID", new SecurityIdentifier(OperatorSid)), ("Enabled", true))),
        };

        // A good group read is scripted behind it, so a lookup that went past the failed first read
        // would be answered - and succeed - rather than fail for want of a script.
        var commands = WithDomain()
            .Returns("Get-ADUser", answer)
            .Returns("Get-ADUser", Account(enabled: true, Sids(GroupA)));

        var ex = await Assert.ThrowsAsync<DirectoryUnavailableException>(
            () => Create(commands).GetMembershipAsync(OperatorSid, "CONTOSO", Ct));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message), shape);
        Assert.Single(commands.Calls, c => c.Command == "Get-ADUser");
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

        // The account is found; the answer under test is the group read's.
        var commands = WithDomain().Returns("Get-ADUser", Located()).Returns("Get-ADUser", answer);

        var ex = await Assert.ThrowsAsync<DirectoryUnavailableException>(
            () => Create(commands).GetMembershipAsync(OperatorSid, "CONTOSO", Ct));

        Assert.False(string.IsNullOrWhiteSpace(ex.Message), shape);
        Assert.Equal(2, commands.Calls.Count(c => c.Command == "Get-ADUser"));
    }

    [Theory]
    [InlineData("the account lookup")]
    [InlineData("the group read")]
    public async Task ACommandThatThrowsIsReportedAsDirectoryUnavailable(string failingRead)
    {
        var commands = WithDomain();
        if (failingRead == "the group read")
            commands.Returns("Get-ADUser", Located());
        commands.Throws("Get-ADUser", new InvalidOperationException("ADWS is not running"));

        var ex = await Assert.ThrowsAsync<DirectoryUnavailableException>(
            () => Create(commands).GetMembershipAsync(OperatorSid, "CONTOSO", Ct));

        Assert.IsType<InvalidOperationException>(ex.InnerException);
        Assert.Equal("ADWS is not running", ex.InnerException!.Message);
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

    [Theory(Timeout = 20000)]
    [InlineData("the account lookup")]
    [InlineData("the group read")]
    public async Task CancellationStopsTheRunningCommand(string blockedRead)
    {
        var commands = blockedRead == "the group read" ? new BlockingCommands(Located()) : new BlockingCommands();
        var directory = new OperatorMembershipDirectory(() => commands, _ => null);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        var lookup = directory.GetMembershipAsync(OperatorSid, null, cancel.Token);
        Assert.True(commands.Started.Wait(TimeSpan.FromSeconds(10), Ct), "The command never started.");
        Assert.Equal(blockedRead == "the group read" ? 2 : 1, commands.Calls.Count);
        await cancel.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.True(commands.StopRequested.Wait(TimeSpan.FromSeconds(10), Ct), "The running command was not stopped.");
    }

    [Fact]
    public async Task BothReadsTakeTheCallersTokenThroughTheCancellableOverload()
    {
        var directory = new BaseSearchOnlyDirectory(refuseByThrowing: true)
            .WithAccount(OperatorSid, OperatorDn, enabled: true, GroupA);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        await new OperatorMembershipDirectory(() => directory, _ => null).GetMembershipAsync(OperatorSid, "CONTOSO", cancel.Token);

        var reads = directory.Calls.Where(c => c.Command == "Get-ADUser").ToList();
        Assert.Equal(2, reads.Count);
        Assert.All(reads, read =>
        {
            Assert.True(read.Cancellable, "A read went through the overload that cannot be stopped.");
            Assert.Equal(cancel.Token, read.Token);
        });
    }

    [Fact(Timeout = 20000)]
    public async Task ACancelDuringTheAccountLookupStartsNoGroupRead()
    {
        var directory = new BaseSearchOnlyDirectory(refuseByThrowing: true)
            .WithAccount(OperatorSid, OperatorDn, enabled: true, GroupA);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Ct);

        // The cancel lands while the first read runs, and that read still answers.
        directory.AfterAccountLookup = () => cancel.Cancel();

        var lookup = new OperatorMembershipDirectory(() => directory, _ => null).GetMembershipAsync(OperatorSid, null, cancel.Token);

        // The lookup has ended once its session is disposed; only then is the count final. Waited for
        // before the lookup is awaited: the cancel runs on the lookup's own thread, and a test already
        // awaiting could be resumed there, inside the cancel, to wait for that thread itself.
        Assert.True(directory.Disposed.Wait(TimeSpan.FromSeconds(10), Ct), "The lookup never ended.");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => lookup);
        Assert.Single(directory.Calls, c => c.Command == "Get-ADUser");
        Assert.True(directory.StopRequested, "The cancel did not stop the session.");
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

    // ------------------------------------------------------------------ kd-87: AD's base-search rule

    /// <summary>
    /// The finding replayed: against a directory that computes <c>tokenGroups</c> only in a
    /// base-scope search, the one read asking for it with the account given by SID is refused in
    /// AD's own words, and the lookup is answered. "thrown" is the shape the dev log showed (the
    /// refusal reached the lookup's catch as an exception); "reported" is the same refusal as an
    /// error record.
    /// </summary>
    [Theory]
    [InlineData("thrown")]
    [InlineData("reported")]
    public async Task AgainstADirectoryThatComputesTokenGroupsOnlyInABaseSearch_TheLookupIsAnswered(string refusal)
    {
        BaseSearchOnlyDirectory NewDirectory() =>
            new BaseSearchOnlyDirectory(refuseByThrowing: refusal == "thrown")
                .WithAccount(OperatorSid, OperatorDn, enabled: true, GroupA, DomainUsers);

        // The stand-in refuses the read as it was: tokenGroups asked for with the account given by SID.
        var asItWas = new Dictionary<string, object?>
        {
            ["Identity"] = OperatorSid,
            ["Properties"] = new[] { "tokenGroups" },
            ["ErrorAction"] = "Stop",
            ["Server"] = "contoso.test",
        };
        var standIn = NewDirectory();
        var refused = refusal == "thrown"
            ? Assert.Throws<RuntimeException>(() => standIn.Invoke("Get-ADUser", asItWas, Ct)).Message
            : standIn.Invoke("Get-ADUser", asItWas, Ct).Error;
        Assert.Equal(BaseSearchRefusal, refused);

        var directory = NewDirectory();
        var membership = await new OperatorMembershipDirectory(() => directory, _ => null).GetMembershipAsync(OperatorSid, "CONTOSO", Ct);

        Assert.True(membership.AccountEnabled);
        Assert.Equal(new[] { DomainUsers, GroupA }.Order(), membership.GroupSids.Order());
        Assert.Equal(0, directory.Refusals);
        var reads = directory.Calls.Where(c => c.Command == "Get-ADUser").ToList();
        Assert.Equal(2, reads.Count);
        Assert.All(reads, read => Assert.Equal("contoso.test", read.Parameters["Server"]));
    }

    [Fact]
    public async Task AnotherAccountFoundAtTheAccountsNameIsNeverReadAsTheAccount()
    {
        var directory = new BaseSearchOnlyDirectory(refuseByThrowing: true)
            .WithAccount(OperatorSid, OperatorDn, enabled: true, GroupA);

        // Between the two reads the account is moved away and another is created under its name,
        // holding every group the operator's session could be checked for and more.
        directory.AfterAccountLookup = () =>
            directory.ReplaceAccountAt(OperatorDn, OtherAccountSid, enabled: true, GroupA, GroupB, DomainUsers);

        var ex = await Assert.ThrowsAsync<DirectoryUnavailableException>(
            () => new OperatorMembershipDirectory(() => directory, _ => null).GetMembershipAsync(OperatorSid, null, Ct));

        Assert.Contains("returned 0 objects", ex.Message, StringComparison.Ordinal);
        Assert.Equal(2, directory.Calls.Count(c => c.Command == "Get-ADUser"));
    }

    // ------------------------------------------------------------------ helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static OperatorMembershipDirectory Create(FakeCommands commands) => new(() => commands, _ => null);

    private static SecurityIdentifier[] Sids(params string[] sids) => sids.Select(s => new SecurityIdentifier(s)).ToArray();

    private static DirectoryObject Row(params (string Name, object? Value)[] properties) =>
        new(properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase));

    // The account lookup's answer: the cmdlet's default properties, no tokenGroups.
    private static DirectoryObject LocatedRow() =>
        Row(("DistinguishedName", OperatorDn), ("SID", new SecurityIdentifier(OperatorSid)), ("Enabled", true), ("SamAccountName", "operator"));

    private static DirectoryCommandOutcome Located() => DirectoryCommandOutcome.Success(LocatedRow());

    // The group read's answer.
    private static DirectoryObject AccountRow(bool enabled, SecurityIdentifier[] groups) =>
        Row(("Enabled", enabled), ("tokenGroups", new System.Collections.ArrayList(groups)), ("SID", new SecurityIdentifier(OperatorSid)),
            ("DistinguishedName", OperatorDn));

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
        private readonly Dictionary<string, Queue<Func<DirectoryCommandOutcome>>> _responses = new(StringComparer.OrdinalIgnoreCase);

        public List<RecordedCall> Calls { get; } = [];

        public int Disposals { get; private set; }

        public FakeCommands Returns(string command, DirectoryCommandOutcome outcome) => Enqueue(command, () => outcome);

        public FakeCommands Throws(string command, Exception exception) => Enqueue(command, () => throw exception);

        /// <summary>A lookup that works: the account found by its SID, then its group read.</summary>
        public FakeCommands ReturnsAccount(bool enabled, SecurityIdentifier[] groups) =>
            Returns("Get-ADUser", Located()).Returns("Get-ADUser", Account(enabled, groups));

        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters)
        {
            Calls.Add(new RecordedCall(command, parameters));

            if (_responses.TryGetValue(command, out var queue) && queue.Count > 0)
                return queue.Dequeue()();

            // An unscripted command is a test bug, not an empty directory.
            throw new InvalidOperationException($"No response scripted for '{command}'.");
        }

        public string? TranslateSidToNTAccount(string sid) => null;

        public void Dispose() => Disposals++;

        public RecordedCall LastCall(string command) =>
            Calls.Last(c => string.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase));

        private FakeCommands Enqueue(string command, Func<DirectoryCommandOutcome> answer)
        {
            if (!_responses.TryGetValue(command, out var queue))
                _responses[command] = queue = new Queue<Func<DirectoryCommandOutcome>>();
            queue.Enqueue(answer);
            return this;
        }
    }

    /// <summary>
    /// Answers the given outcomes in order, then blocks the next command until it is stopped.
    /// </summary>
    private sealed class BlockingCommands(params DirectoryCommandOutcome[] answersBeforeBlocking) : ICancellableDirectoryCommands
    {
        private readonly Queue<DirectoryCommandOutcome> _answers = new(answersBeforeBlocking);

        public List<RecordedCall> Calls { get; } = [];

        public ManualResetEventSlim Started { get; } = new();

        public ManualResetEventSlim StopRequested { get; } = new();

        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters)
            => throw new InvalidOperationException("The cancellable overload must be used.");

        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken)
        {
            Calls.Add(new RecordedCall(command, parameters));
            if (_answers.Count > 0)
                return _answers.Dequeue();

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

    private sealed record StandInCall(
        string Command, IReadOnlyDictionary<string, object?> Parameters, bool Cancellable, CancellationToken Token);

    /// <summary>
    /// A directory that answers as Active Directory does on the point kd-87 is about: <c>tokenGroups</c>
    /// is computed only in a base-scope search (<c>-SearchBase</c> at the object, <c>-SearchScope
    /// Base</c>) and refused in every other search in AD's own words - thrown, or as an error record.
    /// </summary>
    /// <remarks>
    /// Stricter than AD may be: every <c>-Identity</c> read counts as a search that is not base-scope,
    /// a distinguished-name identity included, because nothing here shows the cmdlet finds a DN
    /// identity by a base search. One domain, contoso.test, found through its partition; a user is
    /// found by SID or distinguished name.
    /// </remarks>
    private sealed class BaseSearchOnlyDirectory(bool refuseByThrowing) : ICancellableDirectoryCommands
    {
        private readonly List<StandInAccount> _accounts = [];

        public List<StandInCall> Calls { get; } = [];

        public int Refusals { get; private set; }

        public bool StopRequested { get; private set; }

        public ManualResetEventSlim Disposed { get; } = new();

        /// <summary>Run once, after the first <c>-Identity</c> read has been answered.</summary>
        public Action? AfterAccountLookup { get; set; }

        public BaseSearchOnlyDirectory WithAccount(string sid, string dn, bool enabled, params string[] groups)
        {
            _accounts.Add(new StandInAccount(sid, dn, enabled, groups));
            return this;
        }

        /// <summary>The object at <paramref name="dn"/> is now another account.</summary>
        public void ReplaceAccountAt(string dn, string sid, bool enabled, params string[] groups)
        {
            _accounts.RemoveAll(account => string.Equals(account.Dn, dn, StringComparison.OrdinalIgnoreCase));
            _accounts.Add(new StandInAccount(sid, dn, enabled, groups));
        }

        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters)
            => Answer(command, parameters, cancellable: false, CancellationToken.None);

        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters,
            CancellationToken cancellationToken) => Answer(command, parameters, cancellable: true, cancellationToken);

        public void RequestStop() => StopRequested = true;

        public string? TranslateSidToNTAccount(string sid) => null;

        public void Dispose() => Disposed.Set();

        private DirectoryCommandOutcome Answer(
            string command, IReadOnlyDictionary<string, object?> parameters, bool cancellable, CancellationToken token)
        {
            Calls.Add(new StandInCall(command, parameters, cancellable, token));
            return command switch
            {
                "Get-ADRootDSE" => RootDse(),
                "Get-ADObject" => Partition(),
                "Get-ADUser" => GetUser(parameters),
                _ => throw new InvalidOperationException($"The stand-in has no '{command}'."),
            };
        }

        private DirectoryCommandOutcome GetUser(IReadOnlyDictionary<string, object?> parameters)
        {
            var properties = parameters.GetValueOrDefault("Properties") as string[] ?? [];
            var baseScope = !parameters.ContainsKey("Identity")
                && parameters.GetValueOrDefault("SearchBase") is string
                && string.Equals(parameters.GetValueOrDefault("SearchScope") as string, "Base", StringComparison.OrdinalIgnoreCase);

            if (properties.Contains("tokenGroups", StringComparer.OrdinalIgnoreCase) && !baseScope)
            {
                Refusals++;
                return refuseByThrowing
                    ? throw new RuntimeException(BaseSearchRefusal)
                    : DirectoryCommandOutcome.Failure(BaseSearchRefusal);
            }

            if (parameters.TryGetValue("Identity", out var identity))
            {
                var found = _accounts.Where(account => account.Is(identity as string)).ToList();
                if (found.Count == 0)
                    throw new RuntimeException($"Cannot find an object with identity: '{identity}' under: 'DC=contoso,DC=test'.");

                var answer = DirectoryCommandOutcome.Success(found.Select(account => account.ToRow(properties)).ToArray());
                var after = AfterAccountLookup;
                AfterAccountLookup = null;
                after?.Invoke();
                return answer;
            }

            var searchBase = parameters.GetValueOrDefault("SearchBase") as string;
            var atBase = _accounts.Where(account => string.Equals(account.Dn, searchBase, StringComparison.OrdinalIgnoreCase)).ToList();
            if (atBase.Count == 0)
                throw new RuntimeException("Directory object not found");

            var filter = parameters.GetValueOrDefault("LDAPFilter") as string;
            return DirectoryCommandOutcome.Success(atBase.Where(account => account.Matches(filter)).Select(account => account.ToRow(properties)).ToArray());
        }
    }

    private sealed record StandInAccount(string Sid, string Dn, bool Enabled, string[] Groups)
    {
        public bool Is(string? identity) =>
            string.Equals(identity, Sid, StringComparison.OrdinalIgnoreCase) || string.Equals(identity, Dn, StringComparison.OrdinalIgnoreCase);

        // The filters a user read is given here. Anything else is a test bug, not "no match".
        public bool Matches(string? filter)
        {
            if (filter is not null && filter.StartsWith("(objectSid=", StringComparison.OrdinalIgnoreCase))
                return string.Equals(filter, "(objectSid=" + Sid + ")", StringComparison.OrdinalIgnoreCase);

            return filter is "(objectClass=user)" or "(objectClass=*)"
                ? true
                : throw new InvalidOperationException($"The stand-in does not read the filter '{filter}'.");
        }

        // The cmdlet's default properties, and tokenGroups when asked for - which only a base-scope
        // search reaches.
        public DirectoryObject ToRow(string[] properties)
        {
            var values = new List<(string Name, object? Value)>
            {
                ("DistinguishedName", Dn),
                ("Enabled", Enabled),
                ("SID", new SecurityIdentifier(Sid)),
                ("ObjectClass", "user"),
            };
            if (properties.Contains("tokenGroups", StringComparer.OrdinalIgnoreCase))
                values.Add(("tokenGroups", new System.Collections.ArrayList(Sids(Groups))));

            return Row(values.ToArray());
        }
    }
}
