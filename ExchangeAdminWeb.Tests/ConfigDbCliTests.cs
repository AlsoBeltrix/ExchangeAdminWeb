using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.Data.Sqlite;

namespace ExchangeAdminWeb.Tests;

public class ConfigDbCliTests
{
    private sealed record CliResult(int Exit, string Out, string Err)
    {
        public string[] OutLines => Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.TrimEnd('\r'))
            .ToArray();
    }

    private static CliResult Run(params string[] verbArgs)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = ConfigDbCli.Run(verbArgs, stdout, stderr);
        return new CliResult(exit, stdout.ToString(), stderr.ToString());
    }

    private static string ReadProgram()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var path = Path.Combine(dir.FullName, "Program.cs");
            if (File.Exists(path))
                return File.ReadAllText(path);

            dir = dir.Parent;
        }

        throw new FileNotFoundException("Could not locate Program.cs from the test base directory.");
    }

    [Fact]
    public void TryRun_ReturnsFalseForNoArgumentsAndForAnyOtherFirstArgument()
    {
        Assert.False(ConfigDbCli.TryRun([], out _));
        Assert.False(ConfigDbCli.TryRun(["--urls", "http://localhost:5000"], out _));
        Assert.False(ConfigDbCli.TryRun(["DB", "check", "x.db"], out _));
        Assert.False(ConfigDbCli.TryRun(["serve"], out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("bogus")]
    [InlineData("backup")]
    [InlineData("backup --source a.db")]
    [InlineData("backup --source a.db --source b.db")]
    [InlineData("backup --source a.db --dest b.db extra")]
    [InlineData("backup --src a.db --dest b.db")]
    [InlineData("check")]
    [InlineData("check a.db b.db")]
    [InlineData("active-jobs")]
    [InlineData("init")]
    public void AnyOtherArgumentShape_PrintsUsageToStderrAndExitsOne(string argLine)
    {
        var result = Run(argLine.Split(' ', StringSplitOptions.RemoveEmptyEntries));

        Assert.Equal(ConfigDbCli.ExitUsage, result.Exit);
        Assert.Equal(string.Empty, result.Out);
        Assert.Contains("Usage:", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void Backup_Succeeds_PrintsOkThenTheDestinationPath()
    {
        using var temp = new TempDir();
        using (var c = ConfigDbTestFiles.Open(temp.DbPath))
        {
            ConfigDbTestFiles.Exec(c, "CREATE TABLE t (id INTEGER PRIMARY KEY);");
            ConfigDbTestFiles.Exec(c, "INSERT INTO t DEFAULT VALUES;");
        }

        var dest = Path.Combine(temp.Path, "backup's copy.db");

        var result = Run("backup", "--dest", dest, "--source", temp.DbPath);

        Assert.Equal(ConfigDbCli.ExitOk, result.Exit);
        Assert.Equal(new[] { "ok", Path.GetFullPath(dest) }, result.OutLines);
        Assert.Equal(1, ConfigDbTestFiles.CountRows(dest, "t"));
    }

    [Fact]
    public void Backup_WhenDestinationExists_ExitsTwoAndLeavesItUntouched()
    {
        using var temp = new TempDir();
        using (var c = ConfigDbTestFiles.Open(temp.DbPath))
            ConfigDbTestFiles.Exec(c, "CREATE TABLE t (id INTEGER PRIMARY KEY);");

        var dest = Path.Combine(temp.Path, "backup.db");
        File.WriteAllText(dest, "previous backup");
        var past = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(dest, past);

        var result = Run("backup", "--source", temp.DbPath, "--dest", dest);

        Assert.Equal(ConfigDbCli.ExitFailure, result.Exit);
        Assert.Equal(string.Empty, result.Out);
        Assert.Equal("previous backup", File.ReadAllText(dest));
        Assert.Equal(past, File.GetLastWriteTimeUtc(dest));
    }

    [Fact]
    public void Backup_OfCorruptSource_ExitsTwoAndLeavesNoFileBehind()
    {
        using var temp = new TempDir();
        ConfigDbTestFiles.CreateCorrupt(temp.DbPath);
        var dest = Path.Combine(temp.Path, "backup.db");

        var result = Run("backup", "--source", temp.DbPath, "--dest", dest);

        Assert.Equal(ConfigDbCli.ExitFailure, result.Exit);
        Assert.Equal(string.Empty, result.Out);
        Assert.False(File.Exists(dest), "a failed backup must not leave a file that looks like a snapshot");
        Assert.Empty(Directory.GetFiles(temp.Path, "backup.db*"));
    }

    [Fact]
    public void Backup_WhenSourceMissing_ExitsTwo()
    {
        using var temp = new TempDir();

        var result = Run("backup", "--source", temp.DbPath, "--dest", Path.Combine(temp.Path, "b.db"));

        Assert.Equal(ConfigDbCli.ExitFailure, result.Exit);
        Assert.False(File.Exists(temp.DbPath));
    }

    [Fact]
    public void Check_OnGoodDatabase_PrintsOkAndExitsZero()
    {
        using var temp = new TempDir();
        using (var c = ConfigDbTestFiles.Open(temp.DbPath))
            ConfigDbTestFiles.Exec(c, "CREATE TABLE t (id INTEGER PRIMARY KEY);");

        var result = Run("check", temp.DbPath);

        Assert.Equal(ConfigDbCli.ExitOk, result.Exit);
        Assert.Equal(new[] { "ok" }, result.OutLines);
    }

    [Fact]
    public void Check_OnCorruptDatabase_ExitsTwo()
    {
        using var temp = new TempDir();
        ConfigDbTestFiles.CreateCorrupt(temp.DbPath);

        var result = Run("check", temp.DbPath);

        Assert.Equal(ConfigDbCli.ExitFailure, result.Exit);
        Assert.Equal(string.Empty, result.Out);
    }

    [Fact]
    public void Check_OnMissingOrUnreadableFile_ExitsTwoAndCreatesNothing()
    {
        using var temp = new TempDir();
        var garbage = Path.Combine(temp.Path, "garbage.db");
        File.WriteAllText(garbage, new string('x', 8192));

        Assert.Equal(ConfigDbCli.ExitFailure, Run("check", temp.DbPath).Exit);
        Assert.False(File.Exists(temp.DbPath));
        Assert.Equal(ConfigDbCli.ExitFailure, Run("check", garbage).Exit);
    }

    [Fact]
    public void ActiveJobs_ReturnsRunningAndQueuedOnlyInSubmissionOrderAsFivePipeColumns()
    {
        using var temp = new TempDir();
        var jobsDb = Path.Combine(temp.Path, "exchangeadmin-jobs.db");
        new JobStoreMigrator(new SqliteConnectionFactory(jobsDb)).Migrate();
        SqliteConnection.ClearAllPools();

        using (var c = ConfigDbTestFiles.Open(jobsDb))
        {
            void Insert(string id, string status, string? ticket, string submittedAt, int processed, int total)
            {
                using var cmd = c.CreateCommand();
                cmd.CommandText = """
                    INSERT INTO bulk_job (id, module_id, job_type, status, submitted_by, submitted_ip,
                        ticket, payload_json, submitted_at, processed_rows, total_rows)
                    VALUES ($id, 'ConferenceRooms', $type, $status, $by, '10.0.0.5',
                        $ticket, '[]', $at, $processed, $total);
                    """;
                cmd.Parameters.AddWithValue("$id", id);
                cmd.Parameters.AddWithValue("$type", "Type-" + id);
                cmd.Parameters.AddWithValue("$status", status);
                cmd.Parameters.AddWithValue("$by", "user-" + id);
                cmd.Parameters.AddWithValue("$ticket", (object?)ticket ?? DBNull.Value);
                cmd.Parameters.AddWithValue("$at", submittedAt);
                cmd.Parameters.AddWithValue("$processed", processed);
                cmd.Parameters.AddWithValue("$total", total);
                cmd.ExecuteNonQuery();
            }

            Insert("j1", "Queued", null, "2026-01-01T12:03:00.0000000Z", 0, 7);
            Insert("j2", "Completed", "INC2", "2026-01-01T12:00:00.0000000Z", 5, 5);
            Insert("j3", "Running", "INC3", "2026-01-01T12:01:00.0000000Z", 2, 10);
            Insert("j4", "Interrupted", "INC4", "2026-01-01T12:02:00.0000000Z", 1, 4);
            Insert("j5", "Failed", null, "2026-01-01T11:59:00.0000000Z", 0, 1);
        }

        var result = Run("active-jobs", jobsDb);

        Assert.Equal(ConfigDbCli.ExitOk, result.Exit);
        Assert.Equal(
            new[]
            {
                "Running|Type-j3|user-j3|INC3|2/10",
                "Queued|Type-j1|user-j1||0/7",
            },
            result.OutLines);
        Assert.All(result.OutLines, line => Assert.Equal(5, line.Split('|').Length));
    }

    [Fact]
    public void ActiveJobs_WithNoActiveJobs_PrintsNothingAndExitsZero()
    {
        using var temp = new TempDir();
        var jobsDb = Path.Combine(temp.Path, "exchangeadmin-jobs.db");
        new JobStoreMigrator(new SqliteConnectionFactory(jobsDb)).Migrate();
        SqliteConnection.ClearAllPools();

        var result = Run("active-jobs", jobsDb);

        Assert.Equal(ConfigDbCli.ExitOk, result.Exit);
        Assert.Equal(string.Empty, result.Out);
    }

    [Fact]
    public void ActiveJobs_OnMissingOrSchemalessDatabase_ExitsTwo()
    {
        using var temp = new TempDir();
        Assert.Equal(ConfigDbCli.ExitFailure, Run("active-jobs", Path.Combine(temp.Path, "none.db")).Exit);

        using (var c = ConfigDbTestFiles.Open(temp.DbPath))
            ConfigDbTestFiles.Exec(c, "CREATE TABLE other (id INTEGER);");
        Assert.Equal(ConfigDbCli.ExitFailure, Run("active-jobs", temp.DbPath).Exit);
    }

    [Fact]
    public void Init_CreatesTheFileAndPrintsItsPath_ThenRefusesItTheSecondTime()
    {
        using var temp = new TempDir();

        var first = Run("init", temp.DbPath);

        Assert.Equal(ConfigDbCli.ExitOk, first.Exit);
        Assert.Equal(new[] { Path.GetFullPath(temp.DbPath) }, first.OutLines);
        Assert.True(File.Exists(temp.DbPath));
        Assert.Equal(ConfigDbCli.ExitOk, Run("check", temp.DbPath).Exit);

        var second = Run("init", temp.DbPath);

        Assert.Equal(ConfigDbCli.ExitFailure, second.Exit);
        Assert.Equal(string.Empty, second.Out);
    }

    [Fact]
    public void Program_DispatchesTheDbModeBeforeTheLoggerAndBeforeTheWebHostBuilder()
    {
        var program = ReadProgram();

        var dispatch = Regex.Match(program,
            @"^if \((?:ExchangeAdminWeb\.Services\.Storage\.)?ConfigDbCli\.TryRun\(args, out var (\w+)\)\)\s*return \1;",
            RegexOptions.Multiline);
        Assert.True(dispatch.Success, "Program.cs must dispatch the db mode at top level and return its exit code");

        var registerProvider = program.IndexOf("Encoding.RegisterProvider(", StringComparison.Ordinal);
        var logger = program.IndexOf("Log.Logger =", StringComparison.Ordinal);
        var builder = program.IndexOf("WebApplication.CreateBuilder(", StringComparison.Ordinal);
        Assert.True(registerProvider >= 0 && logger >= 0 && builder >= 0, "anchors not found in Program.cs");

        Assert.True(dispatch.Index > registerProvider, "the dispatch must follow RegisterProvider");
        Assert.True(dispatch.Index < logger, "the dispatch must precede the bootstrap logger");
        Assert.True(dispatch.Index < builder, "the dispatch must precede WebApplication.CreateBuilder");
    }
}
