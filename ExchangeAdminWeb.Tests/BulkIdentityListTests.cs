using ExchangeAdminWeb.Services;
using Xunit;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/GroupBulkActions-Plan.md S1: the pure paste-list parser, batch filter builder,
/// line-to-object matcher and batch summary the two group modules' bulk actions run on.
/// </summary>
public class BulkIdentityListTests
{
    private static BulkIdentityList.Candidate User(
        string dn, string? upn = null, string? sam = null, string? mail = null, string? name = null, string? guid = null) =>
        new(dn, "user", name, null, upn, sam, mail, guid);

    private static BulkIdentityList.Candidate Group(
        string dn, string? name = null, string? sam = null, string? mail = null, string? guid = null) =>
        new(dn, "group", name, null, null, sam, mail, guid);

    private static IReadOnlyList<BulkIdentityList.Line> Lines(params string[] texts) =>
        texts.Select((t, i) => new BulkIdentityList.Line(i + 1, t)).ToList();

    // ----- Parse -----

    [Fact]
    public void Parse_SplitsOnNewlineCommaSemicolon_TrimsAndDropsBlanks()
    {
        var parsed = BulkIdentityList.Parse("a\r\nb, c;d\n\n e ");

        Assert.Equal(new[] { "a", "b", "c", "d", "e" }, parsed.Kept.Select(l => l.Text));
        Assert.Equal(new[] { 1, 2, 3, 4, 5 }, parsed.Kept.Select(l => l.Number));
        Assert.Empty(parsed.Duplicates);
        Assert.Empty(parsed.OverCap);
    }

    [Fact]
    public void Parse_DeduplicatesCaseInsensitive_KeepingFirst()
    {
        var parsed = BulkIdentityList.Parse("JDoe\njdoe\nother");

        Assert.Equal(new[] { "JDoe", "other" }, parsed.Kept.Select(l => l.Text));
        var dup = Assert.Single(parsed.Duplicates);
        Assert.Equal(2, dup.Line.Number);
        Assert.Equal("jdoe", dup.Line.Text);
        Assert.Equal(1, dup.DuplicateOf);
    }

    [Fact]
    public void Parse_LinesPastCap_AreOverCap()
    {
        var text = string.Join("\n", Enumerable.Range(1, BulkIdentityList.MaxBatch + 1).Select(i => $"user{i}"));

        var parsed = BulkIdentityList.Parse(text);

        Assert.Equal(BulkIdentityList.MaxBatch, parsed.Kept.Count);
        var over = Assert.Single(parsed.OverCap);
        Assert.Equal(BulkIdentityList.MaxBatch + 1, over.Number);
        Assert.Equal($"user{BulkIdentityList.MaxBatch + 1}", over.Text);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(" \r\n , ; ")]
    public void Parse_BlankInput_YieldsNothing(string? text)
    {
        var parsed = BulkIdentityList.Parse(text);

        Assert.Empty(parsed.Kept);
        Assert.Empty(parsed.Duplicates);
        Assert.Empty(parsed.OverCap);
    }

    // ----- Chunk -----

    [Fact]
    public void Chunk_SplitsAtFifty()
    {
        var kept = Lines(Enumerable.Range(1, 120).Select(i => $"u{i}").ToArray());

        var chunks = BulkIdentityList.Chunk(kept).ToList();

        Assert.Equal(new[] { 50, 50, 20 }, chunks.Select(c => c.Count));
        Assert.Equal("u51", chunks[1][0].Text);
    }

    // ----- BuildBatchFilter -----

    [Fact]
    public void BuildBatchFilter_EscapesEachValue_AndOrsLines()
    {
        var chunk = Lines("jdoe", "bad*(name");

        var users = BulkIdentityList.BuildBatchFilter(chunk, allowGroups: false);
        var both = BulkIdentityList.BuildBatchFilter(chunk, allowGroups: true);

        Assert.StartsWith("(|", users);
        Assert.EndsWith(")", users);
        Assert.Contains("(userPrincipalName=jdoe)", users);
        Assert.Contains("(sAMAccountName=bad\\2a\\28name)", users);
        Assert.DoesNotContain("bad*(name", users);
        Assert.DoesNotContain("objectCategory=group", users);
        Assert.Equal(2, CountOf(users, "(objectClass=user)"));

        Assert.Contains("(&(objectCategory=group)(|(name=jdoe)(sAMAccountName=jdoe)(mail=jdoe)))", both);
        Assert.Equal(2, CountOf(both, "objectCategory=group"));
    }

    [Fact]
    public void BuildBatchFilter_EmptyChunk_Throws()
    {
        Assert.Throws<ArgumentException>(() => BulkIdentityList.BuildBatchFilter([], allowGroups: true));
    }

    private static int CountOf(string haystack, string needle)
    {
        var count = 0;
        var i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0) { count++; i += needle.Length; }
        return count;
    }

    // ----- Match -----

    [Fact]
    public void Match_ExactlyOne_Resolved()
    {
        var kept = Lines("jdoe@contoso.com");
        var c = User("CN=J,DC=x", upn: "jdoe@contoso.com", sam: "jdoe");

        var r = Assert.Single(BulkIdentityList.Match(kept, [c], allowGroups: false));

        Assert.Equal(BulkIdentityList.Status.Resolved, r.Status);
        Assert.Same(c, r.Match);
    }

    [Fact]
    public void Match_MatchesMailAndSam_CaseInsensitive()
    {
        var kept = Lines("JDOE@CONTOSO.COM", "ASmith");
        var j = User("CN=J,DC=x", upn: "jdoe@x.local", sam: "jdoe", mail: "jdoe@contoso.com");
        var a = User("CN=A,DC=x", upn: "asmith@x.local", sam: "asmith");

        var results = BulkIdentityList.Match(kept, [j, a], allowGroups: false);

        Assert.All(results, r => Assert.Equal(BulkIdentityList.Status.Resolved, r.Status));
        Assert.Same(j, results[0].Match);
        Assert.Same(a, results[1].Match);
    }

    [Fact]
    public void Match_Zero_NotFound()
    {
        var r = Assert.Single(BulkIdentityList.Match(Lines("nobody"), [], allowGroups: false));

        Assert.Equal(BulkIdentityList.Status.NotFound, r.Status);
        Assert.Null(r.Match);
        Assert.Contains("nobody", r.Reason);
    }

    [Fact]
    public void Match_Multiple_Ambiguous()
    {
        var kept = Lines("ops");
        var a = User("CN=Ops User,DC=x", sam: "ops");
        var b = User("CN=Ops Other,DC=y", sam: "ops");

        var r = Assert.Single(BulkIdentityList.Match(kept, [a, b], allowGroups: false));

        Assert.Equal(BulkIdentityList.Status.Ambiguous, r.Status);
        Assert.Null(r.Match);
        Assert.Contains("2 directory objects", r.Reason);
    }

    [Fact]
    public void Match_SameObjectTwice_LaterLineIsDuplicate()
    {
        var kept = Lines("jdoe", "jdoe@contoso.com");
        var c = User("CN=J,DC=x", upn: "jdoe@contoso.com", sam: "jdoe");

        var results = BulkIdentityList.Match(kept, [c], allowGroups: false);

        Assert.Equal(BulkIdentityList.Status.Resolved, results[0].Status);
        Assert.Equal(BulkIdentityList.Status.Duplicate, results[1].Status);
        Assert.Null(results[1].Match);
        Assert.Contains("Duplicate of line 1", results[1].Reason);
    }

    [Fact]
    public void Match_GroupCandidate_OnlyWhenAllowed()
    {
        var kept = Lines("ExchangeWebAdmins");
        var g = Group("CN=Exchange Web Admins,DC=x", name: "Exchange Web Admins", sam: "ExchangeWebAdmins");

        var allowed = Assert.Single(BulkIdentityList.Match(kept, [g], allowGroups: true));
        var denied = Assert.Single(BulkIdentityList.Match(kept, [g], allowGroups: false));

        Assert.Equal(BulkIdentityList.Status.Resolved, allowed.Status);
        Assert.Same(g, allowed.Match);
        Assert.Equal(BulkIdentityList.Status.NotFound, denied.Status);
    }

    [Fact]
    public void Match_GroupCandidate_ByNameOnly_Resolves()
    {
        // gba-3: the group clause of the filter matches on name, so a group whose ONLY
        // matching attribute is its name must match back to its line.
        var kept = Lines("Exchange Web Admins");
        var g = Group("CN=Exchange Web Admins,DC=x", name: "Exchange Web Admins", sam: "ExchangeWebAdmins", mail: "ewa@contoso.com");

        var r = Assert.Single(BulkIdentityList.Match(kept, [g], allowGroups: true));

        Assert.Equal(BulkIdentityList.Status.Resolved, r.Status);
        Assert.Same(g, r.Match);
    }

    [Fact]
    public void Match_UserCandidate_NameDoesNotMatch()
    {
        // Name is a GROUP-only key: a user's name is not an identity a user is addressed by.
        var kept = Lines("Jane Doe");
        var u = User("CN=Jane Doe,DC=x", upn: "jdoe@contoso.com", sam: "jdoe", mail: "jane.doe@contoso.com", name: "Jane Doe");

        var r = Assert.Single(BulkIdentityList.Match(kept, [u], allowGroups: true));

        Assert.Equal(BulkIdentityList.Status.NotFound, r.Status);
    }

    [Fact]
    public void Match_NullAttributes_DoNotThrow()
    {
        var nulls = new BulkIdentityList.Candidate(null, null, null, null, null, null, null, null);

        var r = Assert.Single(BulkIdentityList.Match(Lines("x"), [nulls], allowGroups: true));

        Assert.Equal(BulkIdentityList.Status.NotFound, r.Status);
    }

    [Fact]
    public void Match_OneStatusPerLine_InOrder()
    {
        var kept = Lines("a", "b", "c");
        var a = User("CN=A,DC=x", sam: "a");

        var results = BulkIdentityList.Match(kept, [a], allowGroups: false);

        Assert.Equal(3, results.Count);
        Assert.Equal(new[] { 1, 2, 3 }, results.Select(r => r.Line.Number));
    }

    // ----- BulkOutcomeSummary -----

    [Fact]
    public void Summary_SuccessOnlyWhenEveryRowDone()
    {
        var rows = new List<BulkRowOutcome>
        {
            new("a", true, "a removed"),
            new("b", true, "b removed"),
            new("c", true, "c removed"),
            new("d", false, "This member is a protected principal. Operation not permitted."),
        };

        var s = BulkOutcomeSummary.Of(rows);

        Assert.False(s.Success);
        Assert.Equal(4, s.Requested);
        Assert.Equal(3, s.Done);
        Assert.Equal(1, s.NotDone);
        Assert.Equal("1 of 4 not done", s.ErrorDetail);
        Assert.Equal("d: Not done - This member is a protected principal. Operation not permitted.", s.MemberLines[3]);
        Assert.Equal("a: Done - a removed", s.MemberLines[0]);
    }

    [Fact]
    public void Summary_AllDone_NoErrorDetail()
    {
        var s = BulkOutcomeSummary.Of([new("a", true, ""), new("b", true, "ok")]);

        Assert.True(s.Success);
        Assert.Null(s.ErrorDetail);
        Assert.Equal(2, s.Done);
        Assert.Equal(0, s.NotDone);
        Assert.Equal("a: Done", s.MemberLines[0]);
    }

    [Fact]
    public void Summary_EmptyBatch_IsSuccessWithZeroCounts()
    {
        var s = BulkOutcomeSummary.Of([]);

        Assert.True(s.Success);
        Assert.Equal(0, s.Requested);
        Assert.Empty(s.MemberLines);
    }
}
