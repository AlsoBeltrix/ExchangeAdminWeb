using ExchangeAdminWeb.Authorization;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice 2 of docs/SectionAccessSidStorage-Plan.md. Directory-free: the fake stands in for AD, so
/// every rule below is provable on CI. The fixtures are this deployment's real stored values,
/// because the rules exist for shapes that data actually contains.
/// </summary>
public class SectionAccessSidMigrationPlannerTests
{
    private const string DomainSid = "S-1-5-21-8915387-325452579-1788637320";
    private const string IamSid = DomainSid + "-586078";
    private const string EmployeesAllSid = DomainSid + "-123668";
    private const string WebAdminsSid = DomainSid + "-677335";
    private const string WinrootEaSid = "S-1-5-21-725345543-2052111302-839522115-519";

    /// <summary>
    /// Stands in for Active Directory. Keyed by "domain\name" (or just "name" for the local
    /// domain), so a test can prove the domain half was carried through rather than dropped.
    /// </summary>
    private sealed class FakeDirectory : ISectionAccessGroupDirectory
    {
        private readonly Dictionary<string, List<DirectoryGroupMatch>> _byKey = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Queries { get; } = [];
        public Exception? ThrowOnLookup { get; set; }

        public FakeDirectory Add(string? domain, string name, string sid, string display)
        {
            var key = Key(name, domain);
            if (!_byKey.TryGetValue(key, out var list))
                _byKey[key] = list = [];
            list.Add(new DirectoryGroupMatch(sid, display));
            return this;
        }

        public IReadOnlyList<DirectoryGroupMatch> FindGroupsByName(string name, string? netBiosDomain)
        {
            Queries.Add(Key(name, netBiosDomain));

            if (ThrowOnLookup is not null)
                throw ThrowOnLookup;

            return _byKey.TryGetValue(Key(name, netBiosDomain), out var list) ? list : [];
        }

        private static string Key(string name, string? domain)
            => domain is null ? name : $"{domain}\\{name}";
    }

    private static FakeDirectory ProdLikeDirectory() => new FakeDirectory()
        .Add(null, "IAM", IamSid, "IAM")
        .Add("ANALOG", "IAM", IamSid, "IAM")
        .Add(null, "$KOO300-S3AMUVVBVMI1", EmployeesAllSid, "Employees-All")
        .Add(null, "ExchangeWebAdmins", WebAdminsSid, "ExchangeWebAdmins")
        .Add("ANALOG", "ExchangeWebAdmins", WebAdminsSid, "ExchangeWebAdmins")
        .Add("winroot", "Enterprise Admins", WinrootEaSid, "Enterprise Admins");

    // ---------------------------------------------------------------- The happy path

    [Fact]
    public void ConvertsBareAndQualifiedNamesToSids()
    {
        var plan = SectionAccessSidMigrationPlanner.Plan(
            [("SelfServiceGroups", "IAM"), ("MailboxPermissions", @"ANALOG\ExchangeWebAdmins")],
            ProdLikeDirectory());

        Assert.True(plan.ShouldWrite);
        Assert.Empty(plan.Failures);
        Assert.Equal(IamSid, plan.Rows[0].Sid);
        Assert.Equal(WebAdminsSid, plan.Rows[1].Sid);
    }

    [Fact]
    public void KeepsPolicyAliasWithEachRow()
    {
        var plan = SectionAccessSidMigrationPlanner.Plan(
            [("SelfServiceGroups", "IAM"), ("BlockedSenders", "IAM")],
            ProdLikeDirectory());

        Assert.Equal("SelfServiceGroups", plan.Rows[0].PolicyAlias);
        Assert.Equal("BlockedSenders", plan.Rows[1].PolicyAlias);
        Assert.All(plan.Rows, r => Assert.Equal(IamSid, r.Sid));
    }

    [Fact]
    public void StoresDisplayNameFromTheDirectory_NotTheStoredString()
    {
        // The whole point of the display column: $KOO300-S3AMUVVBVMI1 is a sAMAccountName, and an
        // admin page showing it teaches nobody which group it is. AD calls it Employees-All.
        var plan = SectionAccessSidMigrationPlanner.Plan(
            [("SelfServiceGroups", "$KOO300-S3AMUVVBVMI1")],
            ProdLikeDirectory());

        Assert.Equal(EmployeesAllSid, plan.Rows[0].Sid);
        Assert.Equal("Employees-All", plan.Rows[0].DisplayName);
    }

    [Fact]
    public void ResolvesForeignDomainAgainstThatDomain()
    {
        // The fake only holds "Enterprise Admins" under winroot, mirroring live AD where the
        // local-domain query returns 0 matches. Dropping the domain half fails this test.
        var directory = ProdLikeDirectory();

        var plan = SectionAccessSidMigrationPlanner.Plan(
            [("DhcpAuthorization", @"winroot\Enterprise Admins")], directory);

        Assert.True(plan.ShouldWrite);
        Assert.Equal(WinrootEaSid, plan.Rows[0].Sid);
        Assert.Contains(@"winroot\Enterprise Admins", directory.Queries);
    }

    [Fact]
    public void QueriesEachDistinctValueOnce()
    {
        // 58 prod rows hold 18 distinct values. A round-trip per row would triple the directory
        // work done while the app is starting.
        var directory = ProdLikeDirectory();

        SectionAccessSidMigrationPlanner.Plan(
            [("A", "IAM"), ("B", "IAM"), ("C", "IAM"), ("D", "ExchangeWebAdmins")], directory);

        Assert.Equal(2, directory.Queries.Count);
    }

    [Fact]
    public void TreatsBareAndQualifiedFormsOfTheSameGroupAsSeparateQuestions()
    {
        // "IAM" and "ANALOG\IAM" resolve to the same SID here, but only because this domain is
        // the local one. Caching them together would silently answer a cross-domain question with
        // a local answer.
        var directory = ProdLikeDirectory();

        SectionAccessSidMigrationPlanner.Plan([("A", "IAM"), ("B", @"ANALOG\IAM")], directory);

        Assert.Equal(2, directory.Queries.Count);
        Assert.Contains("IAM", directory.Queries);
        Assert.Contains(@"ANALOG\IAM", directory.Queries);
    }

    // ---------------------------------------------------------------- All-or-nothing

    [Fact]
    public void OneUnresolvableRowStopsTheEntireWrite()
    {
        // The load-bearing rule. A partial migration leaves some rows SIDs and some names, and the
        // dropped ones are access grants that vanished with no audit trail.
        var plan = SectionAccessSidMigrationPlanner.Plan(
            [("A", "IAM"), ("B", "NoSuchGroup"), ("C", "ExchangeWebAdmins")],
            ProdLikeDirectory());

        Assert.False(plan.ShouldWrite);
        Assert.Single(plan.Failures);
        Assert.Equal("NoSuchGroup", plan.Failures[0].OriginalValue);
    }

    [Fact]
    public void ResolvableRowsAreStillReported_SoTheHaltIsDiagnosable()
    {
        var plan = SectionAccessSidMigrationPlanner.Plan(
            [("A", "IAM"), ("B", "NoSuchGroup")], ProdLikeDirectory());

        Assert.Equal(2, plan.Rows.Count);
        Assert.True(plan.Rows[0].Converted);
        Assert.False(plan.Rows[1].Converted);
    }

    [Fact]
    public void AmbiguousNameHalts_RatherThanPickingOne()
    {
        // Two groups answering to one name IS the collision this migration removes. Choosing
        // between them would preserve it, and would do so invisibly.
        var directory = new FakeDirectory()
            .Add(null, "Admins", DomainSid + "-1001", "Admins")
            .Add(null, "Admins", DomainSid + "-1002", "Admins");

        var plan = SectionAccessSidMigrationPlanner.Plan([("A", "Admins")], directory);

        Assert.False(plan.ShouldWrite);
        Assert.Contains("2 groups", plan.Failures[0].Failure);
    }

    [Fact]
    public void MissingGroupHalts()
    {
        var plan = SectionAccessSidMigrationPlanner.Plan([("A", "Ghost")], ProdLikeDirectory());

        Assert.False(plan.ShouldWrite);
        Assert.Contains("no group with that name", plan.Failures[0].Failure);
    }

    [Fact]
    public void MissingGroupNamesTheDomainItLookedIn()
    {
        var plan = SectionAccessSidMigrationPlanner.Plan([("A", @"winroot\Ghost")], ProdLikeDirectory());

        Assert.Contains("winroot", plan.Failures[0].Failure);
    }

    [Fact]
    public void UnusableStoredValueHalts_WithoutConsultingTheDirectory()
    {
        var directory = ProdLikeDirectory();

        var plan = SectionAccessSidMigrationPlanner.Plan([("A", @"ANALOG\SUB\Weird")], directory);

        Assert.False(plan.ShouldWrite);
        Assert.Empty(directory.Queries);
    }

    [Fact]
    public void DirectoryReturningAWellKnownSidIsRefused()
    {
        // Last gate before a value becomes an authorization subject. Do not trust the directory to
        // have returned something usable.
        var directory = new FakeDirectory().Add(null, "Everyone", "S-1-1-0", "Everyone");

        var plan = SectionAccessSidMigrationPlanner.Plan([("A", "Everyone")], directory);

        Assert.False(plan.ShouldWrite);
        Assert.Contains("well-known", plan.Failures[0].Failure);
    }

    // ---------------------------------------------------------------- Outage vs absence

    [Fact]
    public void DirectoryFailurePropagates_RatherThanBecomingUnresolvableRows()
    {
        // Both outcomes leave the store untouched, so the difference is not what gets written - it
        // is that an outage must not be reported as "these groups do not exist", sending an
        // administrator to fix data that is correct.
        var directory = ProdLikeDirectory();
        directory.ThrowOnLookup = new DirectoryUnavailableException("domain controller unreachable");

        Assert.Throws<DirectoryUnavailableException>(
            () => SectionAccessSidMigrationPlanner.Plan([("A", "IAM")], directory));
    }

    // ---------------------------------------------------------------- Idempotence

    [Fact]
    public void AlreadyMigratedStoreIsANoOp()
    {
        var directory = ProdLikeDirectory();

        var plan = SectionAccessSidMigrationPlanner.Plan([("A", IamSid), ("B", WebAdminsSid)], directory);

        Assert.True(plan.AlreadyMigrated);
        Assert.False(plan.ShouldWrite);
        Assert.Empty(directory.Queries);
    }

    [Fact]
    public void AlreadyMigratedRowsAreNotReResolved()
    {
        // A completed migration must not depend on AD again - that would reintroduce the outage
        // sensitivity the design removes.
        var directory = ProdLikeDirectory();

        SectionAccessSidMigrationPlanner.Plan([("A", IamSid)], directory);

        Assert.Empty(directory.Queries);
    }

    [Fact]
    public void PartlyMigratedStoreConvertsOnlyTheRemainder()
    {
        // The state a run deferred by an AD outage leaves behind, if a later admin edit added a
        // name. It must complete, not restart or refuse.
        var directory = ProdLikeDirectory();

        var plan = SectionAccessSidMigrationPlanner.Plan([("A", IamSid), ("B", "ExchangeWebAdmins")], directory);

        Assert.True(plan.ShouldWrite);
        Assert.False(plan.AlreadyMigrated);
        Assert.Equal(IamSid, plan.Rows[0].Sid);
        Assert.Equal(WebAdminsSid, plan.Rows[1].Sid);
        Assert.Single(directory.Queries);
    }

    [Fact]
    public void EmptyStoreWritesNothing()
    {
        var plan = SectionAccessSidMigrationPlanner.Plan([], ProdLikeDirectory());

        Assert.False(plan.ShouldWrite);
        Assert.Empty(plan.Rows);
    }

    // ---------------------------------------------------------------- The halt report

    [Fact]
    public void FailureReportNamesEveryOffendingRowAndSaysAccessIsUnaffected()
    {
        var plan = SectionAccessSidMigrationPlanner.Plan(
            [("SelfServiceGroups", "Ghost"), ("BlockedSenders", "AlsoMissing")],
            ProdLikeDirectory());

        var report = SectionAccessSidMigrationPlanner.DescribeFailures(plan.Failures);

        Assert.Contains("SelfServiceGroups", report);
        Assert.Contains("Ghost", report);
        Assert.Contains("BlockedSenders", report);
        Assert.Contains("AlsoMissing", report);
        // A human reading this after a silent halt needs to know the app is still serving.
        Assert.Contains("access is unaffected", report, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FailureReportIsEmptyWhenNothingFailed()
        => Assert.Empty(SectionAccessSidMigrationPlanner.DescribeFailures([]));
}
