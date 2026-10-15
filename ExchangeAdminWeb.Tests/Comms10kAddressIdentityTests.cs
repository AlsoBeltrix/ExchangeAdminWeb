using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>Exercises repeated directory identities through actual batches without contacting AD.</summary>
public class Comms10kAddressIdentityTests
{
    [Fact]
    public void OnePersonReturnedByTwoRealBatchQueriesResolvesOnceWithoutAmbiguity()
    {
        var personGuid = Guid.NewGuid();
        const string upn = "person@example.com";
        const string mail = "person.mail@example.com";
        const string dn = "CN=Person,DC=example,DC=test";
        var addresses = new List<string> { upn };
        var rows = new List<PSObject> { DirectoryRow(personGuid, dn, upn, mail) };
        for (var i = 0; i < 499; i++)
        {
            var address = $"filler{i}@example.com";
            addresses.Add(address);
            rows.Add(DirectoryRow(Guid.NewGuid(), $"CN=Filler{i},DC=example,DC=test", address));
        }
        addresses.Add(mail);
        using var harness = new QueryHarness(rows);

        var candidates = harness.Query(addresses);
        var outcome = Comms10kAddressResolver.Resolve(addresses, candidates);

        Assert.Equal(2, harness.Filters.Count);
        Assert.Contains($"(userPrincipalName={upn})", harness.Filters[0], StringComparison.Ordinal);
        Assert.DoesNotContain($"(mail={mail})", harness.Filters[0], StringComparison.Ordinal);
        Assert.Contains($"(mail={mail})", harness.Filters[1], StringComparison.Ordinal);
        Assert.Equal(501, candidates.Count);
        Assert.Equal(2, candidates.Count(c => c.ObjectGuid == personGuid));
        Assert.Equal(500, outcome.ResolvedMembers.Count);
        Assert.Equal(new Comms10kAddressResolver.ResolvedMember(personGuid, dn), outcome.ResolvedMembers[0]);
        Assert.Equal(500, outcome.ResolvedDns.Count);
        Assert.Empty(outcome.Rejections);
        Assert.Empty(harness.PowerShell.Commands.Commands);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistinctPeopleRemainAmbiguousAndEveryRejectedInputIsReported(bool sameDn)
    {
        var firstGuid = Guid.NewGuid();
        var secondGuid = Guid.NewGuid();
        const string firstDn = "CN=First,DC=example,DC=test";
        var first = new Comms10kAddressResolver.Candidate(firstGuid, firstDn, "shared@example.com", "unique@example.com");
        var second = new Comms10kAddressResolver.Candidate(
            secondGuid, sameDn ? firstDn : "CN=Second,DC=example,DC=test", "second@example.com", "shared@example.com");
        string[] addresses =
        [
            "shared@example.com", "SHARED@EXAMPLE.COM", "ghost@example.com", "ghost@example.com",
            "", "   ", "unique@example.com", "UNIQUE@EXAMPLE.COM"
        ];

        var outcome = Comms10kAddressResolver.Resolve(addresses, [first, first, second, second]);

        Assert.Equal(new Comms10kAddressResolver.ResolvedMember(firstGuid, firstDn), Assert.Single(outcome.ResolvedMembers));
        Assert.Equal(addresses.Take(6), outcome.Rejections.Select(r => r.Address));
        Assert.Equal(
            [
                "Ambiguous: 2 matches", "Ambiguous: 2 matches",
                Comms10kAddressResolver.Reasons.NoMatch, Comms10kAddressResolver.Reasons.NoMatch,
                Comms10kAddressResolver.Reasons.Blank, Comms10kAddressResolver.Reasons.Blank
            ],
            outcome.Rejections.Select(r => r.Reason));
    }

    [Fact]
    public void ARepeatedGuidKeepsAllReturnedAddressKeysAndResolvesOnceEvenWhenItsDnChanges()
    {
        var guid = Guid.NewGuid();
        const string firstDn = "CN=Before move,DC=example,DC=test";
        const string laterDn = "CN=After move,DC=example,DC=test";
        var candidates = new Comms10kAddressResolver.Candidate[]
        {
            new(guid, firstDn, "before@example.com", "before.mail@example.com"),
            new(guid, laterDn, "after@example.com", "after.mail@example.com")
        };

        var outcome = Comms10kAddressResolver.Resolve(
            ["after.mail@example.com", "before@example.com", "AFTER@EXAMPLE.COM"], candidates);

        Assert.Equal(new Comms10kAddressResolver.ResolvedMember(guid, laterDn), Assert.Single(outcome.ResolvedMembers));
        Assert.Equal([laterDn], outcome.ResolvedDns);
        Assert.Empty(outcome.Rejections);
    }

    [Fact]
    public void CandidateRowsAreReadOnceInsteadOfRescannedForEveryInputAddress()
    {
        var addresses = Enumerable.Range(0, 32).Select(i => $"person{i}@example.com").ToArray();
        var candidates = new CountingCandidates(addresses.Select((a, i) =>
            new Comms10kAddressResolver.Candidate(Guid.NewGuid(), $"CN=Person{i},DC=x", a, a)).ToArray());

        var outcome = Comms10kAddressResolver.Resolve(addresses, candidates);

        Assert.Equal(addresses.Length, outcome.ResolvedMembers.Count);
        Assert.Empty(outcome.Rejections);
        Assert.InRange(candidates.Reads, 1, candidates.Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-guid")]
    [InlineData("00000000-0000-0000-0000-000000000000")]
    public void TheBatchQueryRefusesAnyUnusableObjectGuidInsteadOfReturningPartialCandidates(string? identity)
    {
        // The good first row must not escape when a later directory row cannot be identified.
        using var harness = new QueryHarness(
        [
            DirectoryRow(Guid.NewGuid(), "CN=Good,DC=x", "good@example.com"),
            DirectoryRow(identity, "CN=Unidentified,DC=x", "unknown@example.com")
        ]);

        var error = Assert.Throws<InvalidOperationException>(() =>
            harness.Query(["good@example.com", "unknown@example.com"]));

        Assert.Contains("ObjectGUID", error.Message, StringComparison.Ordinal);
        Assert.Empty(harness.PowerShell.Commands.Commands);
    }

    [Fact]
    public void PureResolutionRefusesEmptyGuidsInsteadOfCollapsingUnidentifiedPeople()
    {
        var candidates = new Comms10kAddressResolver.Candidate[]
        {
            new(Guid.Empty, "CN=First,DC=x", "first@example.com", null),
            new(Guid.Empty, "CN=Second,DC=x", "second@example.com", null)
        };

        var error = Assert.Throws<ArgumentException>(() =>
            Comms10kAddressResolver.Resolve(["first@example.com", "second@example.com"], candidates));

        Assert.Contains("ObjectGUID", error.Message, StringComparison.Ordinal);
    }

    private static PSObject DirectoryRow(object? identity, string dn, string upn, string? mail = null)
    {
        var row = new PSObject();
        if (identity is not null)
            row.Properties.Add(new PSNoteProperty("objectGUID", identity));
        row.Properties.Add(new PSNoteProperty("DistinguishedName", dn));
        row.Properties.Add(new PSNoteProperty("userPrincipalName", upn));
        row.Properties.Add(new PSNoteProperty("mail", mail));
        return row;
    }

    private sealed class QueryHarness : IDisposable
    {
        private readonly Runspace _runspace;
        private readonly PSCredential _credential = new("EXAMPLE\\tester", new SecureString());
        public PowerShell PowerShell { get; }
        public List<string> Filters { get; } = new();

        public QueryHarness(List<PSObject> rows)
        {
            var state = InitialSessionState.CreateDefault2();
            state.Commands.Add(new SessionStateFunctionEntry("Get-ADObject", """
                [CmdletBinding()]
                param([string] $LDAPFilter, [string[]] $Properties, [pscredential] $Credential)
                if ($Properties -notcontains 'objectGUID') { throw 'ObjectGUID was not requested' }
                $global:C10Filters.Add($LDAPFilter)
                foreach ($row in $global:C10Rows) {
                    if ($LDAPFilter.Contains('(userPrincipalName=' + $row.userPrincipalName + ')') -or
                        $LDAPFilter.Contains('(mail=' + $row.mail + ')')) {
                        $row
                    }
                }
                """));
            _runspace = RunspaceFactory.CreateRunspace(state);
            _runspace.Open();
            _runspace.SessionStateProxy.SetVariable("C10Rows", rows);
            _runspace.SessionStateProxy.SetVariable("C10Filters", Filters);
            PowerShell = PowerShell.Create();
            PowerShell.Runspace = _runspace;
        }

        public IReadOnlyList<Comms10kAddressResolver.Candidate> Query(IReadOnlyList<string> addresses) =>
            Comms10kService.ReadAddressCandidates(PowerShell, _credential, addresses);

        public void Dispose()
        {
            PowerShell.Dispose();
            _runspace.Dispose();
            _credential.Password.Dispose();
        }
    }

    private sealed class CountingCandidates(IReadOnlyList<Comms10kAddressResolver.Candidate> rows)
        : IReadOnlyList<Comms10kAddressResolver.Candidate>
    {
        public int Reads { get; private set; }
        public int Count => rows.Count;
        public Comms10kAddressResolver.Candidate this[int index]
        {
            get
            {
                Reads++;
                return rows[index];
            }
        }

        public IEnumerator<Comms10kAddressResolver.Candidate> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
