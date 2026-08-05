using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice 1 of docs/SectionAccessSidStorage-Plan.md. Every test here is directory-free on purpose:
/// these are the decisions the migration's AD call depends on, so they must hold on CI (no AD) as
/// well as on a domain-joined box.
/// </summary>
public class SectionAccessGroupIdentityTests
{
    // The real ad.analog.com domain SID, captured 2026-08-03. Used as a realistic prefix rather
    // than for its own sake - the rules under test are shape rules, not deployment rules.
    private const string DomainSid = "S-1-5-21-8915387-325452579-1788637320";
    private const string IamGroupSid = DomainSid + "-586078";

    // ---------------------------------------------------------------- SID acceptance

    [Theory]
    [InlineData(IamGroupSid)]                                          // IAM
    [InlineData(DomainSid + "-123668")]                                // Employees-All
    [InlineData("S-1-5-21-725345543-2052111302-839522115-519")]        // winroot\Enterprise Admins
    public void AcceptsDomainGroupSids(string sid)
    {
        Assert.Null(SectionAccessGroupIdentity.SidRejectionReason(sid));
        Assert.True(SectionAccessGroupIdentity.IsUsableGroupSid(sid));
    }

    [Fact]
    public void AcceptsForeignDomainGroupSid()
    {
        // winroot\Enterprise Admins is a deliberate cross-domain grant in this deployment and must
        // survive the migration as its own domain's SID (plan Non-Goals). "Foreign" is not a
        // rejection reason.
        Assert.True(SectionAccessGroupIdentity.IsUsableGroupSid("S-1-5-21-725345543-2052111302-839522115-519"));
    }

    [Theory]
    [InlineData("S-1-1-0")]        // Everyone
    [InlineData("S-1-5-11")]       // Authenticated Users
    [InlineData("S-1-5-32-544")]   // BUILTIN\Administrators
    [InlineData("S-1-5-32-545")]   // BUILTIN\Users
    [InlineData("S-1-5-18")]       // LOCAL SYSTEM
    public void RefusesWellKnownSids(string sid)
    {
        // Unambiguous, but they grant far more than an admin picking "a group" intends.
        Assert.False(SectionAccessGroupIdentity.IsUsableGroupSid(sid));
        Assert.Contains("well-known", SectionAccessGroupIdentity.SidRejectionReason(sid));
    }

    [Theory]
    [InlineData("BA")]   // -> S-1-5-32-544, BUILTIN\Administrators
    [InlineData("WD")]   // -> S-1-1-0, Everyone
    public void RefusesSddlAliases(string alias)
    {
        // new SecurityIdentifier("BA") SUCCEEDS. Parse-success alone would let a two-letter string
        // authorize BUILTIN\Administrators.
        //
        // "DA" belongs to this set logically but NOT here: it resolves only against a joined
        // domain, so it is covered separately below.
        Assert.False(SectionAccessGroupIdentity.IsUsableGroupSid(alias));
        Assert.Contains("canonical", SectionAccessGroupIdentity.SidRejectionReason(alias));
    }

    [Fact]
    public void RefusesTheDomainAdminsSddlAlias()
    {
        // "DA" is the worst of the aliases - it resolves to a real account SID and so passes every
        // check except the round-trip - but it is also the only one that needs a DOMAIN to resolve
        // at all. On a standalone machine `new SecurityIdentifier("DA")` throws, which the code
        // reports as "not a valid SID" rather than "not canonical": still a refusal, still correct,
        // different words.
        //
        // It sat in the Theory above and turned CI red from 2026-08-04 (ba9fe4f) onward, passing on
        // this domain-joined box and failing on GitHub's standalone runner. The test was asserting
        // the environment, not the behaviour. Skipping loudly beats both a silent early return and
        // a wrong-environment assertion (the repo rule that produced Assert.SkipWhen elsewhere).
        Assert.SkipUnless(IsDomainJoined(), "\"DA\" only resolves on a domain-joined machine.");

        Assert.False(SectionAccessGroupIdentity.IsUsableGroupSid("DA"));
        Assert.Contains("canonical", SectionAccessGroupIdentity.SidRejectionReason("DA"));
    }

    [Fact]
    public void RefusesTheDomainAdminsAliasOnAnyHost()
    {
        // The part that holds EVERYWHERE, and the part that actually matters for authorization:
        // "DA" is never usable as a stored group value. Whether it is refused for being
        // non-canonical (domain-joined) or unparseable (standalone) is an implementation detail of
        // the platform, not a security property.
        //
        // This is the test that would have caught the original defect on either host, which is why
        // it exists alongside the skip rather than the skip standing alone.
        Assert.False(SectionAccessGroupIdentity.IsUsableGroupSid("DA"));
        Assert.NotNull(SectionAccessGroupIdentity.SidRejectionReason("DA"));
    }

    private static bool IsDomainJoined()
    {
        // Resolving "DA" IS the capability under test, so probe it directly rather than asking the
        // OS about domain membership - a machine can be joined while the alias still fails.
        try
        {
            _ = new System.Security.Principal.SecurityIdentifier("DA");
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    [Fact]
    public void RefusesDomainSidWithNoRid()
    {
        // Parses, round-trips, and IsAccountSid() is true - it names a domain, not a group. Only
        // the comparison against its own AccountDomainSid catches it.
        Assert.False(SectionAccessGroupIdentity.IsUsableGroupSid(DomainSid));
        Assert.Contains("domain SID", SectionAccessGroupIdentity.SidRejectionReason(DomainSid));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("IAM")]
    [InlineData("S-1-5-21-notdigits")]
    [InlineData("ANALOG\\IAM")]
    public void RefusesNonSids(string? value)
    {
        Assert.False(SectionAccessGroupIdentity.IsUsableGroupSid(value));
    }

    [Fact]
    public void RefusesPaddedSid()
    {
        // Whitespace is not trimmed away into acceptance: the stored string is what a comparison
        // will use, so it must be exactly canonical.
        Assert.False(SectionAccessGroupIdentity.IsUsableGroupSid(" " + IamGroupSid + " "));
    }

    // ---------------------------------------------------------------- Parsing stored values

    [Fact]
    public void ParsesBareName()
    {
        var parsed = SectionAccessGroupIdentity.Parse("IAM");

        Assert.Equal(StoredGroupValueKind.BareName, parsed.Kind);
        Assert.Equal("IAM", parsed.Name);
        Assert.Null(parsed.NetBiosDomain);
    }

    [Fact]
    public void ParsesDomainQualifiedName()
    {
        var parsed = SectionAccessGroupIdentity.Parse(@"ANALOG\IAM");

        Assert.Equal(StoredGroupValueKind.DomainQualified, parsed.Kind);
        Assert.Equal("ANALOG", parsed.NetBiosDomain);
        Assert.Equal("IAM", parsed.Name);
    }

    [Fact]
    public void ParsesForeignDomainQualifiedName_KeepingItsDomain()
    {
        // The domain half is load-bearing, not decoration: resolving "Enterprise Admins" against
        // the local domain returns 0 matches (verified against live AD 2026-08-03), so dropping
        // "winroot" turns a real grant into an unresolvable row.
        var parsed = SectionAccessGroupIdentity.Parse(@"winroot\Enterprise Admins");

        Assert.Equal(StoredGroupValueKind.DomainQualified, parsed.Kind);
        Assert.Equal("winroot", parsed.NetBiosDomain);
        Assert.Equal("Enterprise Admins", parsed.Name);
    }

    [Fact]
    public void ParsesNameContainingSpaces()
    {
        var parsed = SectionAccessGroupIdentity.Parse(@"ANALOG\ADI Comms Team");

        Assert.Equal(StoredGroupValueKind.DomainQualified, parsed.Kind);
        Assert.Equal("ADI Comms Team", parsed.Name);
    }

    [Fact]
    public void ParsesNameBeginningWithDollar()
    {
        // $KOO300-S3AMUVVBVMI1 is a real stored value. It is only remarkable because a PowerShell
        // -Filter expands '$' as a variable reference; an LDAP filter does not, which is why the
        // resolver uses -LDAPFilter. Nothing here should treat it as special.
        var parsed = SectionAccessGroupIdentity.Parse("$KOO300-S3AMUVVBVMI1");

        Assert.Equal(StoredGroupValueKind.BareName, parsed.Kind);
        Assert.Equal("$KOO300-S3AMUVVBVMI1", parsed.Name);
    }

    [Fact]
    public void ParsesAlreadyMigratedSid()
    {
        // Re-running the migration must be a no-op on a row it already converted.
        var parsed = SectionAccessGroupIdentity.Parse(IamGroupSid);

        Assert.Equal(StoredGroupValueKind.Sid, parsed.Kind);
        Assert.Equal(IamGroupSid, parsed.Sid);
    }

    [Fact]
    public void SidShapedButUnusableValue_IsUnusableNotAName()
    {
        // "S-1-5-32-544" must not fall through to a name lookup for a group literally called
        // "S-1-5-32-544". Halting beats resolving to something the operator never chose.
        var parsed = SectionAccessGroupIdentity.Parse("S-1-5-32-544");

        Assert.Equal(StoredGroupValueKind.Unusable, parsed.Kind);
        Assert.Contains("well-known", parsed.RejectionReason);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void ParsesBlankAsUnusable(string? value)
    {
        Assert.Equal(StoredGroupValueKind.Unusable, SectionAccessGroupIdentity.Parse(value).Kind);
    }

    [Theory]
    [InlineData(@"\IAM")]
    [InlineData(@"ANALOG\")]
    [InlineData(@"ANALOG\SUB\IAM")]
    public void ParsesMalformedBackslashFormsAsUnusable(string value)
    {
        Assert.Equal(StoredGroupValueKind.Unusable, SectionAccessGroupIdentity.Parse(value).Kind);
    }

    [Fact]
    public void ParsesDnShapedValueAsUnusable_RatherThanSplittingOnItsEscape()
    {
        // In a DN the backslash ESCAPES a comma - it is not a domain separator. Splitting there
        // yields a different, possibly real, group. Section access has never held a DN (0 of 58
        // prod rows contain '=', verified 2026-08-03), so the correct response to one is to halt.
        // Review finding ppv-2 is the same defect in the protected-principal path.
        var parsed = SectionAccessGroupIdentity.Parse(@"CN=Sales\, East,OU=Groups,DC=contoso,DC=com");

        Assert.Equal(StoredGroupValueKind.Unusable, parsed.Kind);
        Assert.Contains("distinguished name", parsed.RejectionReason);
    }

    // ---------------------------------------------------------------- Lookup filter

    [Fact]
    public void FilterQueriesAllThreeNameAttributes()
    {
        // $KOO300-S3AMUVVBVMI1 is stored as a sAMAccountName but its cn is Employees-All. A query
        // on any single attribute misses one of this deployment's real rows.
        var filter = SectionAccessGroupIdentity.BuildGroupLookupFilter("IAM");

        Assert.Equal("(|(sAMAccountName=IAM)(cn=IAM)(name=IAM))", filter);
    }

    [Fact]
    public void FilterDoesNotQueryDisplayName()
    {
        // displayName is not unique in AD. Including it would manufacture the ambiguity this work
        // exists to remove.
        Assert.DoesNotContain("displayName", SectionAccessGroupIdentity.BuildGroupLookupFilter("IAM"));
    }

    [Fact]
    public void FilterIsExactNotWildcard()
    {
        // A substring match would let "IAM" also find "IAM-Readers" - then resolve as Ambiguous, or
        // worse, silently as the wrong group.
        Assert.DoesNotContain("*", SectionAccessGroupIdentity.BuildGroupLookupFilter("IAM"));
    }

    [Fact]
    public void FilterEscapesInjectionCharacters()
    {
        var filter = SectionAccessGroupIdentity.BuildGroupLookupFilter("Ops)(cn=*");

        Assert.DoesNotContain("Ops)(cn=*", filter);
        Assert.Contains(@"Ops\29\28cn=\2a", filter);
    }

    [Fact]
    public void FilterLeavesDollarUnescaped()
    {
        // '$' has no special meaning to LDAP. It is only dangerous in a PowerShell -Filter string,
        // which is why the resolver must use -LDAPFilter.
        Assert.Contains("$KOO300-S3AMUVVBVMI1", SectionAccessGroupIdentity.BuildGroupLookupFilter("$KOO300-S3AMUVVBVMI1"));
    }

    // ---------------------------------------------------------------- Display name

    [Fact]
    public void QualifiesADisplayNameWithItsDomain()
    {
        // Owner request 2026-08-04: a bare "ExchangeWebAdmins" on the admin page does not say
        // WHICH domain's group holds the grant, and three domains can authenticate here. Storing
        // SIDs removed that ambiguity from the data; the display must not put it back.
        Assert.Equal(@"ANALOG\ExchangeWebAdmins",
            SectionAccessGroupIdentity.QualifiedDisplayName("ANALOG", "ExchangeWebAdmins"));
    }

    [Fact]
    public void DoesNotDoubleQualifyAnAlreadyQualifiedName()
    {
        // Values reach this from the picker, the migration and the store, and not all of them know
        // whether the domain was already prepended. "ANALOG\ANALOG\IAM" would be worse than bare.
        Assert.Equal(@"ANALOG\IAM",
            SectionAccessGroupIdentity.QualifiedDisplayName("ANALOG", @"ANALOG\IAM"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void FallsBackToTheBareNameWhenTheDomainIsUnknown(string? domain)
    {
        // Domain resolution is fail-soft by design: it decorates a badge, and a directory hiccup
        // must leave a readable name rather than blank the row or fail the page.
        Assert.Equal("ExchangeWebAdmins",
            SectionAccessGroupIdentity.QualifiedDisplayName(domain, "ExchangeWebAdmins"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void EmptyNameYieldsEmpty_NeverABareSeparator(string? name)
    {
        // A lone "ANALOG\" would read as a group whose name had been lost.
        Assert.Equal(string.Empty, SectionAccessGroupIdentity.QualifiedDisplayName("ANALOG", name));
    }

    [Fact]
    public void TrimsTheSeparatorFromTheDomain()
    {
        Assert.Equal(@"ANALOG\IAM", SectionAccessGroupIdentity.QualifiedDisplayName(@"ANALOG\", "IAM"));
    }

    [Fact]
    public void KeepsAForeignDomainDistinct()
    {
        // The case the whole work stream exists for, now visible to the operator: two groups of
        // the same name in different domains must not render identically.
        var local = SectionAccessGroupIdentity.QualifiedDisplayName("ANALOG", "Enterprise Admins");
        var foreign = SectionAccessGroupIdentity.QualifiedDisplayName("WINROOT", "Enterprise Admins");

        Assert.NotEqual(local, foreign);
        Assert.Equal(@"WINROOT\Enterprise Admins", foreign);
    }

    [Fact]
    public void PreservesANameBeginningWithDollar()
    {
        Assert.Equal(@"ANALOG\$KOO300-S3AMUVVBVMI1",
            SectionAccessGroupIdentity.QualifiedDisplayName("ANALOG", "$KOO300-S3AMUVVBVMI1"));
    }

    // ---------------------------------------------------------------- Match classification

    [Fact]
    public void OneMatchResolves()
        => Assert.Equal(GroupResolutionOutcome.Resolved, SectionAccessGroupIdentity.ClassifyMatchCount(1));

    [Fact]
    public void ZeroMatchesIsNotFound()
        => Assert.Equal(GroupResolutionOutcome.NotFound, SectionAccessGroupIdentity.ClassifyMatchCount(0));

    [Theory]
    [InlineData(2)]
    [InlineData(17)]
    public void SeveralMatchesIsAmbiguous(int count)
    {
        // Never "pick the first". Two groups answering to one name is precisely the collision this
        // work removes, so choosing between them at migration time would preserve it.
        Assert.Equal(GroupResolutionOutcome.Ambiguous, SectionAccessGroupIdentity.ClassifyMatchCount(count));
    }
}
