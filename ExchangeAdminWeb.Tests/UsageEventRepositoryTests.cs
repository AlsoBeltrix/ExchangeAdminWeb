using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Real temp-file SQLite through a usage-specific <see cref="SqliteConnectionFactory"/>, the
/// way the app builds it (docs/UsageTelemetry-Plan.md AC1/AC2). Nothing here touches the config
/// store: the usage database is a separate file with no migrator and no schema version.
/// </summary>
public class UsageEventRepositoryTests
{
    private static readonly DateTime Base = new(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc);

    private static UsageEventRepository CreateRepo(TempDir temp) =>
        new(new SqliteConnectionFactory(Path.Combine(temp.Path, "exchangeadmin-usage.db")));

    private static UsageEvent Open(string? session, string? module, double minutes = 0) =>
        new(Base.AddMinutes(minutes), session, "open", module, null, module is null ? "" : module.ToLowerInvariant(), null);

    private static UsageEvent Action(string? session, string? module, bool success, double minutes = 0) =>
        new(Base.AddMinutes(minutes), session, "action", module, null, "DoThing", success);

    private static UsageEvent Theme(string session, string themeId, double minutes = 0) =>
        new(Base.AddMinutes(minutes), session, "theme", null, null, themeId, null);

    [Fact]
    public void Schema_HasNoIdentityColumns()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(Open("s1", "GroupManagement"));

        var columns = new List<string>();
        using (var connection = new SqliteConnection($"Data Source={repo.DatabasePath}"))
        {
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA table_info(usage_event);";
            using var reader = command.ExecuteReader();
            while (reader.Read())
                columns.Add(reader.GetString(1));
        }

        Assert.Equal(
            new[] { "id", "ts", "session", "kind", "module", "category", "detail", "success" },
            columns);
    }

    [Fact]
    public void Schema_IsCreatedLazily_AndIdempotent()
    {
        using var temp = new TempDir();
        var path = Path.Combine(temp.Path, "exchangeadmin-usage.db");
        var first = new UsageEventRepository(new SqliteConnectionFactory(path));
        var second = new UsageEventRepository(new SqliteConnectionFactory(path));

        first.Insert(Open("s1", "GroupManagement"));
        second.Insert(Open("s2", "GroupManagement", 1));

        var rows = second.ModuleSummary(Base.AddMinutes(-1), Base.AddMinutes(10));
        Assert.Equal(2, Assert.Single(rows).Opens);
    }

    [Fact]
    public void ConfigStoreMigrator_IsUntouched()
    {
        // The usage table is deliberately NOT a config-store migration (review finding ute-1):
        // the config DB is promoted and backed up, telemetry must not be.
        Assert.Equal(6, ConfigStoreMigrator.TargetVersion);
    }

    [Fact]
    public void Insert_ThenModuleSummary_CountsOpensActionsFailedSessions()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        repo.Insert(Open("s1", "Migration"));
        repo.Insert(Open("s1", "Migration", 1));
        repo.Insert(Open("s2", "Migration", 2));
        repo.Insert(Action("s1", "Migration", true, 3));
        repo.Insert(Action("s2", "Migration", false, 4));

        var row = Assert.Single(repo.ModuleSummary(Base.AddMinutes(-1), Base.AddMinutes(10)));
        Assert.Equal("Migration", row.Module);
        Assert.Equal(3, row.Opens);
        Assert.Equal(2, row.Actions);
        Assert.Equal(1, row.FailedActions);
        Assert.Equal(2, row.DistinctSessions);
    }

    [Fact]
    public void ModuleSummary_GroupsUnmappedActionsByRawCategory()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        repo.Insert(new UsageEvent(Base, "s1", "action", null, "Lookup", "Search", true));
        repo.Insert(Open("s1", null, 1));   // home: no module, no category - not a module row

        var row = Assert.Single(repo.ModuleSummary(Base.AddMinutes(-1), Base.AddMinutes(10)));
        Assert.Equal("Lookup", row.Module);
        Assert.Equal(1, row.Actions);
        Assert.Equal(0, row.Opens);
    }

    [Fact]
    public void ModuleSummary_RespectsRange()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        repo.Insert(Open("s1", "Migration", -60));  // before the window
        repo.Insert(Open("s1", "Migration", 5));    // inside
        repo.Insert(Open("s1", "Migration", 600));  // after

        var row = Assert.Single(repo.ModuleSummary(Base, Base.AddMinutes(10)));
        Assert.Equal(1, row.Opens);
    }

    [Fact]
    public void ThemeSummary_UsesLatestThemePerSession()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        repo.Insert(Theme("sA", "light"));
        repo.Insert(Theme("sA", "oled", 1));
        repo.Insert(Theme("sB", "light", 2));

        var rows = repo.ThemeSummary(Base.AddMinutes(-1), Base.AddMinutes(10));

        Assert.Equal(2, rows.Count);
        Assert.Equal(1, rows.Single(r => r.ThemeId == "oled").Sessions);
        Assert.Equal(1, rows.Single(r => r.ThemeId == "light").Sessions);
    }

    [Fact]
    public void SessionSummary_FlagsSessionsWithNoAction_ByExactSessionJoin()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        repo.Insert(Open("sA", "Migration"));
        repo.Insert(Action("sA", "Migration", true, 1));
        repo.Insert(Open("sB", "Migration", 1));
        // A session-less action in the same module and minute must NOT make sB look active.
        repo.Insert(Action(null, "Migration", true, 1));

        var summary = repo.SessionSummary(Base.AddMinutes(-1), Base.AddMinutes(10));

        Assert.Equal(2, summary.Sessions);
        Assert.Equal(1.0, summary.AvgModulesPerSession, 3);
        Assert.Equal(1, summary.SessionsWithNoAction);
    }

    [Fact]
    public void PruneOlderThan_DeletesOnlyOlderRows()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        repo.Insert(Open("s1", "Migration", -200));
        repo.Insert(Open("s1", "Migration", -150));
        repo.Insert(Open("s1", "Migration", 5));

        Assert.Equal(2, repo.PruneOlderThan(Base));

        var row = Assert.Single(repo.ModuleSummary(Base.AddMinutes(-1000), Base.AddMinutes(1000)));
        Assert.Equal(1, row.Opens);
    }
}
