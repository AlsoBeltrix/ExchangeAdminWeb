using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Slice 2 of docs/SectionAccessSidStorage-Plan.md, against a real SQLite store. The planner's
/// decisions are proven in <see cref="SectionAccessSidMigrationPlannerTests"/>; what is proven here
/// is that the store ends up in the state those decisions call for - in particular, that every
/// failure path leaves it EXACTLY as it was.
/// </summary>
public class SectionAccessSidMigrationTests : IDisposable
{
    private const string DomainSid = "S-1-5-21-8915387-325452579-1788637320";
    private const string IamSid = DomainSid + "-586078";
    private const string WebAdminsSid = DomainSid + "-677335";

    private readonly string _tempDir;
    private readonly SqliteConfigStore _store;
    private readonly SectionAccessRepository _repository;

    public SectionAccessSidMigrationTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"sidmigration_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = TestConfigStore.Create(_tempDir);
        _repository = new SectionAccessRepository(_store);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, true); }
        catch { }
    }

    private sealed class FakeDirectory : ISectionAccessGroupDirectory
    {
        private readonly Dictionary<string, List<DirectoryGroupMatch>> _byName = new(StringComparer.OrdinalIgnoreCase);

        public Exception? ThrowOnLookup { get; set; }

        public FakeDirectory Add(string name, string sid, string display)
        {
            if (!_byName.TryGetValue(name, out var list))
                _byName[name] = list = [];
            list.Add(new DirectoryGroupMatch(sid, display));
            return this;
        }

        public IReadOnlyList<DirectoryGroupMatch> FindGroupsByName(string name, string? netBiosDomain)
        {
            if (ThrowOnLookup is not null)
                throw ThrowOnLookup;
            return _byName.TryGetValue(name, out var list) ? list : [];
        }
    }

    private static FakeDirectory Directory2() => new FakeDirectory()
        .Add("IAM", IamSid, "IAM")
        .Add("ExchangeWebAdmins", WebAdminsSid, "ExchangeWebAdmins");

    private SectionAccessSidMigration CreateMigration(ISectionAccessGroupDirectory directory)
        => new(_repository, directory, Substitute.For<ILogger<SectionAccessSidMigration>>());

    private void Seed(Dictionary<string, string[]> access) => _repository.SaveAll(access);

    private Dictionary<string, string[]> Read()
    {
        _repository.TryGetAll(out var access);
        return access;
    }

    // ---------------------------------------------------------------- Success

    [Fact]
    public void WritesSidsAndDisplayNames()
    {
        Seed(new() { ["SelfServiceGroups"] = ["IAM"] });

        var status = CreateMigration(Directory2()).Run();

        Assert.Equal(SectionAccessMigrationStatus.Migrated, status);
        Assert.Equal([IamSid], Read()["SelfServiceGroups"]);
        Assert.Equal("IAM", _repository.GetDisplayNames()[IamSid]);
    }

    [Fact]
    public void MergesTwoNamesThatResolveToTheSameGroup()
    {
        // Both "IAM" and "ANALOG\IAM" are in the real prod store under different aliases. Under one
        // alias they become one row - the same grant, stated once. This is why the write is
        // delete-then-insert: an in-place UPDATE would collide on the primary key.
        Seed(new() { ["SelfServiceGroups"] = ["IAM", @"ANALOG\IAM"] });
        var directory = new FakeDirectory().Add("IAM", IamSid, "IAM");

        var status = CreateMigration(directory).Run();

        Assert.Equal(SectionAccessMigrationStatus.Migrated, status);
        Assert.Equal([IamSid], Read()["SelfServiceGroups"]);
    }

    [Fact]
    public void PreservesEveryAliasGrant()
    {
        // The migration must not change who can reach what. Same groups, same sections, new
        // representation.
        Seed(new()
        {
            ["SelfServiceGroups"] = ["IAM"],
            ["BlockedSenders"] = ["IAM", "ExchangeWebAdmins"]
        });

        CreateMigration(Directory2()).Run();

        var after = Read();
        Assert.Equal([IamSid], after["SelfServiceGroups"]);
        Assert.Equal(2, after["BlockedSenders"].Length);
        Assert.Contains(IamSid, after["BlockedSenders"]);
        Assert.Contains(WebAdminsSid, after["BlockedSenders"]);
    }

    [Fact]
    public void KeepsTheStoreMarkedConfigured()
    {
        Seed(new() { ["SelfServiceGroups"] = ["IAM"] });

        CreateMigration(Directory2()).Run();

        Assert.True(_repository.IsConfigured());
    }

    [Fact]
    public void MarksTheStoreConfiguredWhenRowsExistWithoutTheMarker()
    {
        // The marker lives in its own table, so the migration's DELETE cannot lose it and the test
        // above passes with or without the re-assert. This is the state where it bites: rows
        // present, marker absent. "Configured but denied" and "never configured" are different -
        // the second falls back to the permissive appsettings AllowedGroups path - so a migration
        // that rewrote the rows and left the store reading as unconfigured would quietly widen
        // access. A non-vacuity probe caught the weaker test passing with MarkPresent removed.
        InsertRowWithoutMarker("SelfServiceGroups", "IAM");
        Assert.False(_repository.IsConfigured());

        CreateMigration(Directory2()).Run();

        Assert.True(_repository.IsConfigured());
        Assert.Equal([IamSid], Read()["SelfServiceGroups"]);
    }

    private void InsertRowWithoutMarker(string alias, string group)
    {
        _store.Write((connection, transaction) =>
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText =
                "INSERT INTO section_access (policy_alias, group_value) VALUES ($alias, $group);";
            insert.Parameters.AddWithValue("$alias", alias);
            insert.Parameters.AddWithValue("$group", group);
            insert.ExecuteNonQuery();
        });
    }

    // ---------------------------------------------------------------- Failure leaves the store alone

    [Fact]
    public void HaltLeavesEveryRowUnchanged()
    {
        // The all-or-nothing guarantee, observed at the store rather than in the plan. "IAM"
        // resolves and must STILL not be written, because its neighbour did not.
        Seed(new() { ["SelfServiceGroups"] = ["IAM", "Ghost"] });

        var status = CreateMigration(Directory2()).Run();

        Assert.Equal(SectionAccessMigrationStatus.Halted, status);
        var after = Read()["SelfServiceGroups"];
        Assert.Contains("IAM", after);
        Assert.Contains("Ghost", after);
        Assert.DoesNotContain(IamSid, after);
    }

    [Fact]
    public void DirectoryOutageLeavesEveryRowUnchanged()
    {
        Seed(new() { ["SelfServiceGroups"] = ["IAM"] });
        var directory = Directory2();
        directory.ThrowOnLookup = new DirectoryUnavailableException("unreachable");

        var status = CreateMigration(directory).Run();

        Assert.Equal(SectionAccessMigrationStatus.DirectoryUnavailable, status);
        Assert.Equal(["IAM"], Read()["SelfServiceGroups"]);
    }

    [Fact]
    public void DirectoryOutageIsDistinguishedFromMissingGroups()
    {
        // Both leave the store untouched, so only the status tells an operator whether to wait or
        // to go and fix the data.
        Seed(new() { ["SelfServiceGroups"] = ["IAM"] });
        var outage = Directory2();
        outage.ThrowOnLookup = new DirectoryUnavailableException("unreachable");

        Assert.Equal(SectionAccessMigrationStatus.DirectoryUnavailable, CreateMigration(outage).Run());

        Seed(new() { ["SelfServiceGroups"] = ["Ghost"] });
        Assert.Equal(SectionAccessMigrationStatus.Halted, CreateMigration(Directory2()).Run());
    }

    [Fact]
    public void UnexpectedDirectoryExceptionDoesNotCorruptTheStore()
    {
        // A directory implementation that throws something other than DirectoryUnavailableException
        // is a bug, but it must not take the authorization table with it.
        Seed(new() { ["SelfServiceGroups"] = ["IAM"] });
        var directory = Directory2();
        directory.ThrowOnLookup = new InvalidOperationException("unexpected");

        Assert.Throws<InvalidOperationException>(() => CreateMigration(directory).Run());
        Assert.Equal(["IAM"], Read()["SelfServiceGroups"]);
    }

    // ---------------------------------------------------------------- Idempotence

    [Fact]
    public void SecondRunIsANoOp()
    {
        Seed(new() { ["SelfServiceGroups"] = ["IAM"] });
        CreateMigration(Directory2()).Run();

        var status = CreateMigration(Directory2()).Run();

        Assert.Equal(SectionAccessMigrationStatus.AlreadyMigrated, status);
        Assert.Equal([IamSid], Read()["SelfServiceGroups"]);
    }

    [Fact]
    public void SecondRunSucceedsEvenWithNoDirectory()
    {
        // Once migrated, startup must not depend on AD at all.
        Seed(new() { ["SelfServiceGroups"] = ["IAM"] });
        CreateMigration(Directory2()).Run();

        var dead = new FakeDirectory { ThrowOnLookup = new DirectoryUnavailableException("down") };

        Assert.Equal(SectionAccessMigrationStatus.AlreadyMigrated, CreateMigration(dead).Run());
        Assert.Equal([IamSid], Read()["SelfServiceGroups"]);
    }

    [Fact]
    public void RetriesAfterADeferredRun()
    {
        // The self-healing path: an AD outage defers the migration, the next start completes it.
        Seed(new() { ["SelfServiceGroups"] = ["IAM"] });
        var down = Directory2();
        down.ThrowOnLookup = new DirectoryUnavailableException("down");
        CreateMigration(down).Run();

        Assert.Equal(SectionAccessMigrationStatus.Migrated, CreateMigration(Directory2()).Run());
        Assert.Equal([IamSid], Read()["SelfServiceGroups"]);
    }

    [Fact]
    public void EmptyStoreIsANoOp()
    {
        Assert.Equal(SectionAccessMigrationStatus.AlreadyMigrated, CreateMigration(Directory2()).Run());
    }

    // ---------------------------------------------------------------- Legacy import ordering

    [Fact]
    public void LegacyImportedRowsAreMigratedInTheSameStartup()
    {
        // Review finding sid-2. The legacy sectionaccess.json import is a SIDE EFFECT of the
        // SectionAccessService constructor, so on a lazily-constructed singleton its timing is
        // decided by whoever resolves it first. If that happens after the migration, the table
        // holds names for the whole process lifetime - and since non-SID rows are now inert
        // (sid-1), everyone configured only through that file is denied until a restart.
        //
        // This drives the real constructor, exactly as Program.cs now does before Run().
        WriteLegacyFile(@"{""Security"":{""SectionAccess"":{""SelfServiceGroups"":[""IAM""]}}}");
        _ = CreateRealSectionAccessService();

        var status = CreateMigration(Directory2()).Run();

        Assert.Equal(SectionAccessMigrationStatus.Migrated, status);
        Assert.Equal([IamSid], Read()["SelfServiceGroups"]);
    }

    private void WriteLegacyFile(string json)
    {
        var configDir = Path.Combine(_tempDir, "config");
        System.IO.Directory.CreateDirectory(configDir);
        File.WriteAllText(Path.Combine(configDir, "sectionaccess.json"), json);
    }

    private SectionAccessService CreateRealSectionAccessService()
    {
        var config = new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build();
        var env = Substitute.For<Microsoft.AspNetCore.Hosting.IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);

        return new SectionAccessService(
            config,
            Substitute.For<ILogger<SectionAccessService>>(),
            env,
            new ExchangeAdminWeb.Modules.ModuleCatalog(),
            _repository);
    }

    // ---------------------------------------------------------------- Display names (slice 4)

    [Fact]
    public void AnOrdinarySaveDoesNotWipeDisplayNames()
    {
        // SaveAll is delete-then-insert, so without carrying names across it, every admin save
        // would blank the names of groups it did not touch - and the migration, being idempotent,
        // would never run again to restore them. The page would silently revert to raw SIDs.
        Seed(new() { ["SelfServiceGroups"] = ["IAM"] });
        CreateMigration(Directory2()).Run();
        Assert.Equal("IAM", _repository.GetDisplayNames()[IamSid]);

        _repository.SaveAll(new Dictionary<string, string[]> { ["SelfServiceGroups"] = [IamSid] });

        Assert.Equal("IAM", _repository.GetDisplayNames()[IamSid]);
    }

    [Fact]
    public void SavingADisplayNameDoesNotCreateAGrant()
    {
        // The update is UPDATE-only. A name arriving for a group that is not granted anything
        // must not insert a row - that would be an authorization change made by a cosmetic write.
        _repository.SaveDisplayNames(new Dictionary<string, string> { [IamSid] = "IAM" });

        Assert.Empty(Read());
    }

    [Fact]
    public void DisplayNamesSurviveAcrossAliases()
    {
        Seed(new() { ["SelfServiceGroups"] = ["IAM"], ["BlockedSenders"] = ["IAM"] });
        CreateMigration(Directory2()).Run();

        var names = _repository.GetDisplayNames();

        Assert.Equal("IAM", names[IamSid]);
    }

    [Fact]
    public void DisplayNameIsAbsentForRowsThatNeverHadOne()
    {
        // A store already holding SIDs (from a prior run of an older build) is left alone rather
        // than re-resolved, so its display names stay missing - the page falls back to the SID.
        // Cosmetic by design; re-resolving would put AD back on the startup path.
        Seed(new() { ["SelfServiceGroups"] = [IamSid] });

        CreateMigration(Directory2()).Run();

        Assert.False(_repository.GetDisplayNames().ContainsKey(IamSid));
    }
}
