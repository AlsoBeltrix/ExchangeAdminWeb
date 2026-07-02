using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Tests;

public class JobStoreMigratorTests
{
    private static string JobsDbPath(TempDir temp) =>
        System.IO.Path.Combine(temp.Path, "exchangeadmin-jobs.db");

    [Fact]
    public void Migrate_FreshDatabase_ReachesTargetVersionAndCreatesTables()
    {
        using var temp = new TempDir();
        var factory = new SqliteConnectionFactory(JobsDbPath(temp));

        var version = new JobStoreMigrator(factory).Migrate();

        Assert.Equal(JobStoreMigrator.TargetVersion, version);
        Assert.True(TableExists(factory, "bulk_job"), "bulk_job should exist");
        Assert.True(TableExists(factory, "bulk_job_row"), "bulk_job_row should exist");
    }

    [Fact]
    public void Migrate_RunTwice_IsIdempotentNoOp()
    {
        using var temp = new TempDir();
        var factory = new SqliteConnectionFactory(JobsDbPath(temp));
        var migrator = new JobStoreMigrator(factory);

        var first = migrator.Migrate();
        var second = migrator.Migrate();

        Assert.Equal(first, second);
        Assert.Equal(JobStoreMigrator.TargetVersion, second);
    }

    [Fact]
    public void Migrate_DatabaseNewerThanBuild_FailsFast()
    {
        using var temp = new TempDir();
        var factory = new SqliteConnectionFactory(JobsDbPath(temp));
        new JobStoreMigrator(factory).Migrate();

        using (var connection = factory.Open())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = $"PRAGMA user_version = {JobStoreMigrator.TargetVersion + 3};";
            command.ExecuteNonQuery();
        }

        var ex = Assert.Throws<InvalidOperationException>(() => new JobStoreMigrator(factory).Migrate());
        Assert.Contains("newer than this build", ex.Message);
    }

    [Fact]
    public void RowDelete_CascadesFromParentJob()
    {
        using var temp = new TempDir();
        var factory = new SqliteConnectionFactory(JobsDbPath(temp));
        new JobStoreMigrator(factory).Migrate();

        using var connection = factory.Open();
        using (var insertJob = connection.CreateCommand())
        {
            insertJob.CommandText =
                "INSERT INTO bulk_job (id, module_id, job_type, status, submitted_by, submitted_ip, " +
                "payload_json, submitted_at) VALUES ('j1','M','T','Completed','u','ip','[]','2026-01-01T00:00:00.0000000Z');";
            insertJob.ExecuteNonQuery();
        }
        using (var insertRow = connection.CreateCommand())
        {
            insertRow.CommandText =
                "INSERT INTO bulk_job_row (job_id, row_index, target, status, recorded_at) " +
                "VALUES ('j1', 0, 'r@x', 'Success', '2026-01-01T00:00:00.0000000Z');";
            insertRow.ExecuteNonQuery();
        }

        // Foreign key ON DELETE CASCADE (PRAGMA foreign_keys=ON is applied by the factory).
        using (var del = connection.CreateCommand())
        {
            del.CommandText = "DELETE FROM bulk_job WHERE id = 'j1';";
            del.ExecuteNonQuery();
        }

        using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM bulk_job_row WHERE job_id = 'j1';";
        Assert.Equal(0L, count.ExecuteScalar());
    }

    private static bool TableExists(SqliteConnectionFactory factory, string table)
    {
        using var connection = factory.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type='table' AND name=$name;";
        command.Parameters.AddWithValue("$name", table);
        return command.ExecuteScalar() is not null;
    }
}
