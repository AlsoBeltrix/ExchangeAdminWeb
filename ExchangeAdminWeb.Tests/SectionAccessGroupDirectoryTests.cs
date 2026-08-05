using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The orchestration in <c>SectionAccessGroupDirectory</c>: which directory errors are fatal, which
/// absences are answers, and what a partial result means.
/// </summary>
/// <remarks>
/// Unreachable by any test before <c>ISectionAccessDirectoryCommands</c> existed - the class opened
/// its own PowerShell runspace and imported the ActiveDirectory module, so nothing here could run
/// without a domain-joined host with RSAT. <c>SectionAccessDirectoryReading</c> already covers the
/// pure value-shaping decisions beside this logic; what these tests add is the fail-closed
/// behaviour AROUND them, which is where an outage being misread as "no such group" would delete a
/// live access grant.
///
/// The fake records the calls it received, because several of these properties are about what was
/// asked, not only what was returned: querying the wrong domain, or a wildcard instead of an exact
/// filter, is a silent wrong-group answer.
/// </remarks>
public class SectionAccessGroupDirectoryTests
{
    private const string DomainSid = "S-1-5-21-8915387-325452579-1788637320";
    private const string GroupSid = DomainSid + "-677335";

    private sealed record RecordedCall(string Command, IReadOnlyDictionary<string, object?> Parameters);

    private sealed class FakeCommands : ISectionAccessDirectoryCommands
    {
        private readonly Dictionary<string, Queue<DirectoryCommandOutcome>> _responses = new(StringComparer.OrdinalIgnoreCase);

        public List<RecordedCall> Calls { get; } = [];

        public string? SidTranslation { get; set; }

        public Exception? ThrowOnTranslate { get; set; }

        public Exception? ThrowOnInvoke { get; set; }

        public bool Disposed { get; private set; }

        /// <summary>Queues one outcome for a command; repeated calls consume them in order.</summary>
        public FakeCommands Returns(string command, DirectoryCommandOutcome outcome)
        {
            if (!_responses.TryGetValue(command, out var queue))
                _responses[command] = queue = new Queue<DirectoryCommandOutcome>();
            queue.Enqueue(outcome);
            return this;
        }

        public DirectoryCommandOutcome Invoke(string command, IReadOnlyDictionary<string, object?> parameters)
        {
            Calls.Add(new RecordedCall(command, parameters));

            if (ThrowOnInvoke is not null)
                throw ThrowOnInvoke;

            if (_responses.TryGetValue(command, out var queue) && queue.Count > 0)
                return queue.Dequeue();

            // An unscripted command is a test bug, not an empty directory: returning "no rows"
            // here would let a test claim a fail-closed path was exercised when the call it was
            // about never happened.
            throw new InvalidOperationException($"No response scripted for '{command}'.");
        }

        public string? TranslateSidToNTAccount(string sid)
        {
            if (ThrowOnTranslate is not null)
                throw ThrowOnTranslate;
            return SidTranslation;
        }

        public void Dispose() => Disposed = true;

        public RecordedCall LastCall(string command) =>
            Calls.Last(c => string.Equals(c.Command, command, StringComparison.OrdinalIgnoreCase));
    }

    private static DirectoryObject Row(params (string Name, object? Value)[] properties) =>
        new(properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.OrdinalIgnoreCase));

    private static DirectoryObject GroupRow(string? sid = GroupSid, string? sam = "ExchangeWebAdmins") =>
        Row(("objectSid", sid), ("SamAccountName", sam), ("Name", "ExchangeWebAdmins"), ("DisplayName", "Exchange Web Admins"));

    private static SectionAccessGroupDirectory Create(FakeCommands commands) =>
        new(() => commands, Substitute.For<ILogger<SectionAccessGroupDirectory>>());

    /// <summary>The two calls a domain-qualified lookup makes before it can query groups.</summary>
    private static FakeCommands WithDomain(string netBios = "ANALOG", string dnsRoot = "ad.analog.com")
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADRootDSE", DirectoryCommandOutcome.Success(
            Row(("configurationNamingContext", "CN=Configuration,DC=ad,DC=analog,DC=com"))));
        commands.Returns("Get-ADObject", DirectoryCommandOutcome.Success(
            Row(("netBIOSName", netBios), ("dnsRoot", dnsRoot))));
        return commands;
    }

    // ------------------------------------------------------------------ Input guard

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void BlankName_ThrowsBeforeTouchingTheDirectory(string name)
    {
        var commands = new FakeCommands();

        Assert.Throws<DirectoryUnavailableException>(() => Create(commands).FindGroupsByName(name, null));

        // Not merely "it threw": a blank name reaching a directory query would build the filter
        // (|(sAMAccountName=)(cn=)(name=)), which matches every group in the domain.
        Assert.Empty(commands.Calls);
    }

    // ------------------------------------------------------------------ Happy paths

    [Fact]
    public void ResolvesAGroupAgainstTheLocalDomainWhenNoDomainIsGiven()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success(GroupRow()));
        commands.SidTranslation = @"ANALOG\ExchangeWebAdmins";

        var matches = Create(commands).FindGroupsByName("ExchangeWebAdmins", null);

        var match = Assert.Single(matches);
        Assert.Equal(GroupSid, match.Sid);
        Assert.Equal(@"ANALOG\ExchangeWebAdmins", match.DisplayName);

        // No domain given means no partition lookup at all: the local domain is the default, and
        // resolving a server for it would be a query that can fail for no reason.
        Assert.DoesNotContain(commands.Calls, c => c.Command == "Get-ADRootDSE");
        Assert.False(commands.LastCall("Get-ADGroup").Parameters.ContainsKey("Server"));
    }

    [Fact]
    public void PointsTheGroupQueryAtTheDomainTheCrossRefNames()
    {
        var commands = WithDomain(dnsRoot: "winroot.analog.com");
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success(GroupRow(sam: "Enterprise Admins")));
        commands.SidTranslation = @"WINROOT\Enterprise Admins";

        var matches = Create(commands).FindGroupsByName("Enterprise Admins", "WINROOT");

        Assert.Single(matches);

        // The load-bearing assertion of this file. Verified against live AD 2026-08-03: this group
        // lives in winroot.analog.com and returns ZERO matches when queried without a server, so a
        // lookup that dropped the domain half would turn a real cross-domain grant into an
        // unresolvable row.
        Assert.Equal("winroot.analog.com", commands.LastCall("Get-ADGroup").Parameters["Server"]);
    }

    [Fact]
    public void QueriesTheCrossRefUnderTheConfigurationNamingContextTheDirectoryReported()
    {
        var commands = WithDomain();
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success(GroupRow()));

        Create(commands).FindGroupsByName("ExchangeWebAdmins", "ANALOG");

        var partitionQuery = commands.LastCall("Get-ADObject").Parameters;

        // Read from the directory, never assembled from the domain name: this deployment's ANALOG
        // is ad.analog.com, so a guessed naming context searches a container that does not exist.
        Assert.Equal("CN=Partitions,CN=Configuration,DC=ad,DC=analog,DC=com", partitionQuery["SearchBase"]);
        Assert.Equal("(&(objectClass=crossRef)(netBIOSName=ANALOG))", partitionQuery["LDAPFilter"]);
    }

    [Fact]
    public void ReturnsEveryMatchRatherThanChoosingOne()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success(
            GroupRow(sid: DomainSid + "-1", sam: "IAM"),
            GroupRow(sid: DomainSid + "-2", sam: "IAM")));

        var matches = Create(commands).FindGroupsByName("IAM", null);

        // Two groups answering to one name is the exact collision the SID migration removes.
        // Narrowing here - first, closest, local - would preserve it silently; the caller refuses.
        Assert.Equal(2, matches.Count);
    }

    [Fact]
    public void AnEmptyResultIsAnAnswer_NotAFailure()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success());

        // "The directory answered and there is no such group" is a permanent condition an
        // administrator must fix. Throwing here would make it indistinguishable from an outage,
        // which the caller retries forever instead of reporting.
        Assert.Empty(Create(commands).FindGroupsByName("NoSuchGroup", null));
    }

    [Fact]
    public void SkipsANullPipelineRowWithoutFailingTheLookup()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success(null, GroupRow()));

        // A PowerShell pipeline can yield a null element (docs/MessageTraceNullRow-Plan.md); the
        // real group beside it must still resolve.
        Assert.Single(Create(commands).FindGroupsByName("ExchangeWebAdmins", null));
    }

    // ------------------------------------------------------------------ Fail-closed paths

    [Fact]
    public void AGroupQueryErrorIsFatal_EvenWhenRowsCameBack()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADGroup", new DirectoryCommandOutcome([GroupRow()], "The server is not operational."));

        var ex = Assert.Throws<DirectoryUnavailableException>(
            () => Create(commands).FindGroupsByName("ExchangeWebAdmins", null));

        // The row-plus-error case is the whole point. A run that complained proved nothing about
        // how many groups exist, so reporting the one row it managed would let a partial failure
        // read as a confident single match - and be migrated as one.
        Assert.Contains("The server is not operational.", ex.Message);
    }

    [Fact]
    public void APartitionQueryErrorIsFatal()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADRootDSE", DirectoryCommandOutcome.Success(
            Row(("configurationNamingContext", "CN=Configuration,DC=ad,DC=analog,DC=com"))));
        commands.Returns("Get-ADObject", DirectoryCommandOutcome.Failure("Access is denied."));

        var ex = Assert.Throws<DirectoryUnavailableException>(
            () => Create(commands).FindGroupsByName("ExchangeWebAdmins", "ANALOG"));

        Assert.Contains("Access is denied.", ex.Message);

        // It must not fall through to a group query against the local domain: that would answer
        // the wrong question confidently, which is worse than not answering.
        Assert.DoesNotContain(commands.Calls, c => c.Command == "Get-ADGroup");
    }

    [Fact]
    public void AnUnreadableConfigurationNamingContextIsFatal()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADRootDSE", DirectoryCommandOutcome.Success(Row(("configurationNamingContext", "   "))));

        var ex = Assert.Throws<DirectoryUnavailableException>(
            () => Create(commands).FindGroupsByName("ExchangeWebAdmins", "ANALOG"));

        Assert.Contains("configuration naming context", ex.Message);
    }

    [Fact]
    public void AnEmptyRootDseResultIsFatal()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADRootDSE", DirectoryCommandOutcome.Success());

        // No rows at all, rather than a row with a blank attribute: both must fail closed, and
        // only one of them would be caught by a null-check on the property.
        Assert.Throws<DirectoryUnavailableException>(
            () => Create(commands).FindGroupsByName("ExchangeWebAdmins", "ANALOG"));
    }

    [Theory]
    [InlineData(0, "matched no forest partition")]
    [InlineData(2, "matched 2 forest partitions")]
    public void APartitionCountOtherThanOneIsFatal_WithWordingThatSaysWhich(int count, string expected)
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADRootDSE", DirectoryCommandOutcome.Success(
            Row(("configurationNamingContext", "CN=Configuration,DC=ad,DC=analog,DC=com"))));
        commands.Returns("Get-ADObject", DirectoryCommandOutcome.Success(
            Enumerable.Range(0, count).Select(_ => (DirectoryObject?)Row(("dnsRoot", "ad.analog.com"))).ToArray()));

        var ex = Assert.Throws<DirectoryUnavailableException>(
            () => Create(commands).FindGroupsByName("ExchangeWebAdmins", "NOPE"));

        // The distinct wording is operational, not cosmetic: "not found" sends an administrator to
        // check the stored value, "matched N" sends them to check the forest.
        Assert.Contains(expected, ex.Message);
    }

    [Fact]
    public void APartitionWithNoUsableDnsRootIsFatal()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADRootDSE", DirectoryCommandOutcome.Success(
            Row(("configurationNamingContext", "CN=Configuration,DC=ad,DC=analog,DC=com"))));
        commands.Returns("Get-ADObject", DirectoryCommandOutcome.Success(Row(("dnsRoot", ""))));

        var ex = Assert.Throws<DirectoryUnavailableException>(
            () => Create(commands).FindGroupsByName("ExchangeWebAdmins", "ANALOG"));

        Assert.Contains("no usable dnsRoot", ex.Message);
    }

    [Fact]
    public void AMatchedGroupWithNoReadableSidIsFatal_NotSkipped()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success(
            GroupRow(sid: null, sam: "IAM"),
            GroupRow(sid: DomainSid + "-2", sam: "IAM")));

        var ex = Assert.Throws<DirectoryUnavailableException>(
            () => Create(commands).FindGroupsByName("IAM", null));

        // Rejecting rather than skipping is load-bearing: dropping the SID-less row silently would
        // leave one match, and the caller refuses only on ambiguity - so two groups where one lost
        // its SID would migrate as a confident single answer. A wrong-group grant, with no error.
        Assert.Contains("no readable objectSid", ex.Message);
    }

    [Fact]
    public void AThrowingDirectoryBecomesUnavailable_NeverAnEmptyResult()
    {
        var commands = new FakeCommands { ThrowOnInvoke = new InvalidOperationException("The ActiveDirectory module is not installed.") };

        var ex = Assert.Throws<DirectoryUnavailableException>(
            () => Create(commands).FindGroupsByName("ExchangeWebAdmins", null));

        // An empty list means "no such group", which makes the migration delete a live grant. A
        // failed lookup must stay distinguishable so the store is left untouched for a retry.
        Assert.Contains("The ActiveDirectory module is not installed.", ex.Message);
        Assert.IsType<InvalidOperationException>(ex.InnerException);
    }

    // ------------------------------------------------------------------ Display decoration

    [Fact]
    public void TheDisplayDomainComesFromTheSid_NotTheQueriedDomain()
    {
        var commands = WithDomain();
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success(GroupRow()));

        // The SID is authoritative about which domain owns the group; the queried domain is only
        // where the search was pointed. A referral can answer from elsewhere.
        commands.SidTranslation = @"WINROOT\ExchangeWebAdmins";

        var match = Assert.Single(Create(commands).FindGroupsByName("ExchangeWebAdmins", "ANALOG"));

        Assert.Equal(@"WINROOT\ExchangeWebAdmins", match.DisplayName);
    }

    [Fact]
    public void FallsBackToTheQueriedDomainWhenTheSidCannotBeTranslated()
    {
        var commands = WithDomain();
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success(GroupRow()));
        commands.SidTranslation = null;

        var match = Assert.Single(Create(commands).FindGroupsByName("ExchangeWebAdmins", "ANALOG"));

        Assert.Equal(@"ANALOG\ExchangeWebAdmins", match.DisplayName);
    }

    [Fact]
    public void ATranslationFailureLeavesABareName_NeverFailsTheLookup()
    {
        var commands = new FakeCommands { ThrowOnTranslate = new SystemException("The trust relationship failed.") };
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success(GroupRow()));

        var match = Assert.Single(Create(commands).FindGroupsByName("ExchangeWebAdmins", null));

        // Fail-soft on purpose, and the asymmetry with every other path here is deliberate: this
        // decorates a display string, while the SID is the real product of the lookup. Failing the
        // migration because a name could not be prettified would be the wrong trade.
        Assert.Equal(GroupSid, match.Sid);
        Assert.Equal("ExchangeWebAdmins", match.DisplayName);
    }

    [Fact]
    public void PrefersSamAccountNameOverTheOtherNameAttributes()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success(
            Row(("objectSid", GroupSid), ("SamAccountName", "IAM"), ("Name", "IAM-Legacy"), ("DisplayName", "Identity Access"))));
        commands.SidTranslation = @"ANALOG\IAM";

        var match = Assert.Single(Create(commands).FindGroupsByName("IAM", null));

        // Precedence is proven exhaustively in SectionAccessDirectoryReadingTests; this pins that
        // the service passes the attributes in that order rather than whichever it read first.
        Assert.Equal(@"ANALOG\IAM", match.DisplayName);
    }

    // ------------------------------------------------------------------ Query construction

    [Fact]
    public void UsesAnExactLdapFilter_NeverAWildcardAndNeverDashFilter()
    {
        var commands = new FakeCommands();
        commands.Returns("Get-ADGroup", DirectoryCommandOutcome.Success());

        Create(commands).FindGroupsByName("$KOO300-S3AMUVVBVMI1", null);

        var parameters = commands.LastCall("Get-ADGroup").Parameters;

        // -LDAPFilter, never -Filter: -Filter expands '$' as a PowerShell variable, and this store
        // holds exactly such a group. Exact, never wildcard: substring matching would let IAM also
        // find IAM-Readers, manufacturing the ambiguity the caller refuses on.
        Assert.True(parameters.ContainsKey("LDAPFilter"));
        Assert.False(parameters.ContainsKey("Filter"));
        Assert.Equal(
            SectionAccessGroupIdentity.BuildGroupLookupFilter("$KOO300-S3AMUVVBVMI1"),
            parameters["LDAPFilter"]);
    }

    // ------------------------------------------------------------------ Session lifetime

    [Fact]
    public void DisposesTheDirectorySession_EvenWhenTheLookupFails()
    {
        var commands = new FakeCommands { ThrowOnInvoke = new InvalidOperationException("boom") };

        Assert.Throws<DirectoryUnavailableException>(
            () => Create(commands).FindGroupsByName("ExchangeWebAdmins", null));

        // The session owns a runspace. Leaking one per failed lookup exhausts the host during
        // exactly the outage that produces repeated failures.
        Assert.True(commands.Disposed);
    }
}
