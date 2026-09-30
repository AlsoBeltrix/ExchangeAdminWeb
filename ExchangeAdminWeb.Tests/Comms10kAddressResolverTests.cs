using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Comms-10k batched address resolution (docs/Comms10kBulkResolveScale-Plan.md S1, tests 1-7).
/// </summary>
/// <remarks>
/// <b>What these actually protect is the unsubscribe path.</b> This module REPLACES a group's
/// membership with the resolved list, so every address that fails to resolve is a person removed
/// from a ten-thousand-member communications group. A batching bug that drops one element, a
/// filter that matches nothing because a metacharacter broke it, or a failed query reported as
/// "not found" are all the same outcome: real people silently unsubscribed, with a green success
/// message on screen.
/// </remarks>
public class Comms10kAddressResolverTests
{
    private static List<string> Addresses(int count) =>
        Enumerable.Range(0, count).Select(i => $"user{i}@example.com").ToList();

    private static Comms10kAddressResolver.Candidate User(string dn, string? upn = null, string? mail = null) =>
        new(dn, upn, mail);

    // ---- 1. Batching covers every input exactly once, in order. ----

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(499)]
    [InlineData(500)]
    [InlineData(501)]
    [InlineData(10001)]
    public void BatchingCoversEveryAddressExactlyOnceAndInOrder(int count)
    {
        // The sizes are the plan's, and they are the boundaries: one under, exactly on, and one
        // over the batch size, plus the real-world magnitude. An off-by-one at 500 would drop or
        // duplicate one person per batch, which at 10,001 addresses is twenty people.
        var input = Addresses(count);

        var batches = Comms10kAddressResolver.Batch(input).ToList();
        var flattened = batches.SelectMany(b => b).ToList();

        Assert.Equal(input, flattened);
        Assert.All(batches, b => Assert.InRange(b.Count, 1, Comms10kAddressResolver.BatchSize));
        Assert.Equal(
            (int)Math.Ceiling(count / (double)Comms10kAddressResolver.BatchSize),
            batches.Count);
    }

    // ---- 2. RFC 4515 escaping, and the dollar sign survives. ----

    [Fact]
    public void TheFilterEscapesEveryRfc4515MetacharacterSoAPastedValueCannotAlterIt()
    {
        // These five are the ones that change a filter's STRUCTURE. An unescaped ")" closes a
        // clause early; an unescaped "*" turns an equality match into a wildcard that can match
        // half the directory and make every address in the batch read as ambiguous.
        var filter = Comms10kAddressResolver.BuildBatchFilter(["a(b)c*d\\e@example.com"]);

        Assert.Contains("a\\28b\\29c\\2ad\\5ce@example.com", filter, StringComparison.Ordinal);
        Assert.DoesNotContain("a(b)c", filter, StringComparison.Ordinal);
    }

    [Fact]
    public void ADollarSignSurvivesAsALiteralBecauseLdapFilterDoesNotExpandIt()
    {
        // The defect this replaces: the old code interpolated the address into -Filter, where
        // PowerShell expands $ as a variable, so an address containing one queried something
        // else entirely (SectionAccessGroupDirectory.cs:132 records the same lesson).
        // -LDAPFilter has no such expansion, and $ is not an RFC 4515 metacharacter, so it must
        // pass through untouched - escaping it would break the match instead.
        var filter = Comms10kAddressResolver.BuildBatchFilter(["od$car@example.com"]);

        Assert.Contains("od$car@example.com", filter, StringComparison.Ordinal);
    }

    // ---- 3. Users only, and only the two keys this module matches on. ----

    [Fact]
    public void TheFilterMatchesUsersOnlyOnUpnAndMailAndNothingElse()
    {
        // Widening these keys changes WHO RECEIVES THE MAIL. A sAMAccountName clause would let a
        // bare login name resolve from a column meant to hold addresses, and a group clause would
        // put a group's DN into a membership the page presents as individual subscribers.
        // BulkIdentityList.BuildBatchFilter does both, which is exactly why it is not reused.
        var filter = Comms10kAddressResolver.BuildBatchFilter(["a@example.com", "b@example.com"]);

        Assert.Contains("(objectCategory=person)", filter, StringComparison.Ordinal);
        Assert.Contains("(objectClass=user)", filter, StringComparison.Ordinal);
        Assert.Contains("(userPrincipalName=a@example.com)", filter, StringComparison.Ordinal);
        Assert.Contains("(mail=a@example.com)", filter, StringComparison.Ordinal);

        Assert.DoesNotContain("sAMAccountName", filter, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("objectCategory=group", filter, StringComparison.OrdinalIgnoreCase);

        // One clause per address, both present, so a batch cannot quietly ask about fewer
        // addresses than it was given.
        Assert.Equal(2, Regex.Matches(filter, @"\(objectCategory=person\)").Count);
    }

    [Fact]
    public void ABlankAddressIsRefusedRatherThanEmittedAsAnEmptyAssertion()
    {
        // An empty equality assertion is not legal filter syntax, and this query is fail-closed
        // for the whole batch - so one blank CSV line reaching the filter would abort the
        // resolution of the 499 real addresses beside it. QueryableAddresses screens them out;
        // this is the guard proving the builder does not rely on that silently.
        Assert.Throws<ArgumentException>(() =>
            Comms10kAddressResolver.BuildBatchFilter(["a@example.com", "   "]));
    }

    // ---- 4. No -Server, no ResultSetSize. Asserted against the source. ----

    [Fact]
    public void TheLiveQueryPassesNoServerAndSetsNoResultSetSize()
    {
        // ResultSetSize would truncate the batch SILENTLY, and a truncated answer here is
        // indistinguishable from "not found" - which is the unsubscribe path. It would also
        // destroy ambiguity detection: a second match dropped by the cap turns an AMBIGUOUS
        // address into a confidently resolved wrong person. -Server would name a directory, which
        // architectural invariant 7 forbids.
        //
        // Source-level because the seam IS the live query; nothing here can run PowerShell
        // against a domain. This proves a SHAPE, not a behaviour.
        var source = ReadService();
        var seam = ExtractMethod(source, "QueryBatchCandidates");

        Assert.DoesNotContain("ResultSetSize", seam, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"Server\"", seam, StringComparison.Ordinal);
        Assert.Contains("\"LDAPFilter\"", seam, StringComparison.Ordinal);
    }

    [Fact]
    public void TheLiveQueryFailsClosedForTheWholeResolutionRatherThanReturningPartialCandidates()
    {
        // Known Failure Class 2, and the highest-consequence instance of it in this module: a
        // partially answered query must never read as "these were not found", because the write
        // removes everyone absent from the resolved list.
        var seam = ExtractMethod(ReadService(), "QueryBatchCandidates");

        Assert.Contains("ps.HadErrors", seam, StringComparison.Ordinal);
        Assert.Contains("throw new InvalidOperationException", seam, StringComparison.Ordinal);
        Assert.Contains("\"Stop\"", seam, StringComparison.Ordinal);
    }

    // ---- 5. Match outcomes. ----

    [Fact]
    public void OneMatchResolvesToItsDistinguishedName()
    {
        var outcome = Comms10kAddressResolver.Resolve(
            ["a@example.com"],
            [User("CN=A,DC=x", upn: "a@example.com")]);

        Assert.Equal(["CN=A,DC=x"], outcome.ResolvedDns);
        Assert.Empty(outcome.SkippedAddresses);
    }

    [Fact]
    public void AMailOnlyMatchResolvesToo()
    {
        // The old -Filter asked UserPrincipalName -or EmailAddress; EmailAddress IS mail. An
        // address that is only a proxy address and not the UPN must keep resolving.
        var outcome = Comms10kAddressResolver.Resolve(
            ["a@example.com"],
            [User("CN=A,DC=x", upn: "different@example.com", mail: "a@example.com")]);

        Assert.Equal(["CN=A,DC=x"], outcome.ResolvedDns);
    }

    [Fact]
    public void TwoMatchesAreAmbiguousAndAreSkippedWithTheCountRatherThanPickingOne()
    {
        // Picking one would subscribe the wrong person and say nothing. The count is in the
        // message because an operator resolving the conflict needs to know there were two.
        var outcome = Comms10kAddressResolver.Resolve(
            ["a@example.com"],
            [User("CN=A1,DC=x", upn: "a@example.com"), User("CN=A2,DC=x", mail: "a@example.com")]);

        Assert.Empty(outcome.ResolvedDns);
        var skip = Assert.Single(outcome.SkippedAddresses);
        Assert.Contains("ambiguous: 2 matches", skip, StringComparison.Ordinal);
    }

    [Fact]
    public void NoMatchIsNotFound()
    {
        var outcome = Comms10kAddressResolver.Resolve(["ghost@example.com"], []);

        Assert.Empty(outcome.ResolvedDns);
        Assert.Equal(["ghost@example.com"], outcome.SkippedAddresses);
    }

    [Fact]
    public void AMatchCarryingNoDistinguishedNameIsSkippedRatherThanDropped()
    {
        // It cannot be written, so it must be REPORTED. Dropping it from both lists would
        // unsubscribe a person who does exist, with nothing on screen saying so.
        var outcome = Comms10kAddressResolver.Resolve(
            ["a@example.com"],
            [User(dn: "", upn: "a@example.com")]);

        Assert.Empty(outcome.ResolvedDns);
        Assert.Equal(["a@example.com"], outcome.SkippedAddresses);
    }

    [Fact]
    public void ARepeatedPersonIsWrittenOnceAndIsNotReportedAsSkipped()
    {
        // Two addresses for one mailbox - UPN in one CSV row, proxy address in another - is one
        // member, not a duplicate to report. Reporting the second as skipped would read as a
        // failure on a run that did exactly the right thing.
        var outcome = Comms10kAddressResolver.Resolve(
            ["a@example.com", "a.alias@example.com"],
            [User("CN=A,DC=x", upn: "a@example.com", mail: "a.alias@example.com")]);

        Assert.Equal(["CN=A,DC=x"], outcome.ResolvedDns);
        Assert.Empty(outcome.SkippedAddresses);
    }

    // ---- 6. Order preserved across a batch boundary, including a repeat spanning one. ----

    [Fact]
    public void InputOrderIsPreservedAcrossBatchBoundariesIncludingARepeatThatSpansOne()
    {
        // Resolve walks the ORIGINAL address list against a pooled candidate set, so a match
        // found by batch 3 still lands in position 1 if that is where its address was. A
        // per-batch assignment would reorder the membership by which query answered first.
        var addresses = Addresses(1200);
        addresses.Add("user7@example.com");          // a repeat, three batches after its first
        addresses.Insert(700, "user1100@example.com"); // resolved by a LATER batch, earlier slot

        var candidates = addresses
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(a => User($"CN={a},DC=x", upn: a))
            .ToList();

        var outcome = Comms10kAddressResolver.Resolve(addresses, candidates);

        var expected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var a in addresses)
        {
            var dn = $"CN={a},DC=x";
            if (seen.Add(dn))
                expected.Add(dn);
        }

        Assert.Equal(expected, outcome.ResolvedDns);
        Assert.Empty(outcome.SkippedAddresses);
    }

    // ---- 7. Screening, which is what keeps a blank line from aborting a real batch. ----

    [Fact]
    public void QueryableAddressesDropsBlanksAndDuplicatesWithoutChangingTheReportedOutcome()
    {
        // Two separate jobs, deliberately not merged: what gets ASKED is deduplicated and
        // non-blank, while what gets REPORTED still walks every original line - so a blank row
        // is still reported skipped and a duplicate still resolves.
        var input = new List<string> { "a@example.com", "", "A@EXAMPLE.COM", "   ", "b@example.com" };

        Assert.Equal(["a@example.com", "b@example.com"], Comms10kAddressResolver.QueryableAddresses(input));

        var outcome = Comms10kAddressResolver.Resolve(
            input,
            [User("CN=A,DC=x", upn: "a@example.com"), User("CN=B,DC=x", upn: "b@example.com")]);

        Assert.Equal(["CN=A,DC=x", "CN=B,DC=x"], outcome.ResolvedDns);
        Assert.Equal(2, outcome.SkippedAddresses.Count);   // the two blanks, and only those
    }

    // ---- helpers ----

    private static string ReadService() =>
        File.ReadAllText(Path.Combine(GetServicesDirectory(), "Comms10kService.cs"));

    /// <summary>
    /// The named method's BODY, from its declaration to the next member.
    /// </summary>
    /// <remarks>
    /// Anchored on <c>internal virtual ... Name(</c>, not on <c>" Name("</c>. The looser anchor
    /// matched the CALL SITE inside ResolveEmailsAsync first, so the slice began mid-caller and
    /// swept up the seam's own doc comment - which names "-Server" and "ResultSetSize" in prose
    /// explaining why it passes neither. The assertion then failed against the explanation rather
    /// than the code. A marker that occurs more than once is not a boundary.
    /// </remarks>
    private static string ExtractMethod(string source, string name)
    {
        var declaration = $"internal virtual IReadOnlyList<Comms10kAddressResolver.Candidate> {name}(";
        var start = source.IndexOf(declaration, StringComparison.Ordinal);
        Assert.True(start >= 0, $"declaration of '{name}' not found in Comms10kService.cs");

        var next = source.IndexOf("\n    public ", start, StringComparison.Ordinal);
        return next > start ? source[start..next] : source[start..];
    }

    private static string GetServicesDirectory()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var services = Path.Combine(dir.FullName, "Services");
            if (Directory.Exists(services))
                return services;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate Services from the test base directory.");
    }
}
