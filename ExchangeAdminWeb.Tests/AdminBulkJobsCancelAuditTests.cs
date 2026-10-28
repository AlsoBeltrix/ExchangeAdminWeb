using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Progress;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.AdminBulkJobs;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-26: Admin Bulk Jobs audits a cancel with what the cancel did - cancelled, not cancelled because the job had already
/// finished or gone, refused, or failed - where it recorded Success after any cancel. kd-53: under the category of the
/// module whose job it is. kd-54: and a removal that did not happen says why. kd-77: decided with the removal, so a job
/// another administrator removed first is told as gone. kd-86: a cancel or a removal whose job list cannot be reloaded
/// afterwards says what it did and goes no further.
/// </summary>
/// <remarks>
/// The real page, its injected services set through reflection (<see cref="ConferenceRoomPageTests"/>' idiom), its
/// <c>CancelJob</c> invoked, over a real <see cref="BulkJobService"/> on a jobs database of its own and the real audit read
/// back from its file. Nothing here reaches a directory, Exchange or the network.
/// </remarks>
public sealed class AdminBulkJobsCancelAuditTests : IDisposable
{
    private const string Operator = @"CONTOSO\admin";
    private const string OperatorIp = "10.0.0.7";
    private const string Action = "AdminBulkJobs_CancelJob";
    private const string Module = "Comms10k";
    private const string NotCancelled = "The job was not cancelled: it had already finished, or it no longer exists.";

    private static readonly DateTime Submitted = new(2026, 10, 23, 8, 15, 0, DateTimeKind.Utc);

    private readonly ExchangeWriteHarness _h = new();
    private readonly ActivityProgressService _progress = new();

    public void Dispose()
    {
        _progress.Dispose();
        _h.Dispose();
    }

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    public async Task ACancelThatTakesEffect_IsAuditedAsDone(string state)
    {
        var (jobs, store) = JobService();
        var job = Job(state == "queued" ? BulkJobStatus.Queued : BulkJobStatus.Running);
        store.Insert(job);
        var page = BuildPage(jobs);

        await Cancel(page, job);

        var row = TheRow(job, success: true, error: null);
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
        Assert.Equal($"Cancellation requested for {Module} job T.", GetField(page, "actionMessage"));
        if (state == "queued")
            Assert.Equal(BulkJobStatus.Cancelled, store.Get(job.Id)!.Status);
        else
            Assert.True(store.Get(job.Id)!.CancelRequested);
    }

    [Theory]
    [InlineData("finished")]
    [InlineData("gone")]
    public async Task ACancelThatTakesNoEffect_IsAuditedAsNotCancelled_NotAsDone(string state)
    {
        // The row was rendered active; by the click the job had finished, or been removed. Nothing was cancelled, and the row
        // and the banner say so - the row said Success and the banner "Cancellation requested".
        var (jobs, store) = JobService();
        var job = Job(BulkJobStatus.Running);
        if (state == "finished")
            store.Insert(Job(BulkJobStatus.Completed));
        var page = BuildPage(jobs);

        await Cancel(page, job);

        var row = TheRow(job, success: false, error: NotCancelled);
        // kd-32. A refusal by the job's state, not a failed write: nothing was changed.
        Assert.Equal("JobState", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal($"{Module} job T was not cancelled - it had already finished, or it no longer exists.", GetField(page, "actionMessage"));
        if (state == "finished")
            Assert.Equal(BulkJobStatus.Completed, store.Get(job.Id)!.Status);
        else
            Assert.Null(store.Get(job.Id));
    }

    [Fact]
    public async Task ARevokedOperatorsCancel_IsAuditedAsRefused_AndTheJobIsUntouched()
    {
        var (jobs, store) = JobService();
        var job = Job(BulkJobStatus.Queued);
        store.Insert(job);
        var page = BuildPage(jobs, authorized: false);

        await Cancel(page, job);

        var row = TheRow(job, success: false, error: "Authorization denied.");
        Assert.Equal("Authorization", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal(BulkJobStatus.Queued, store.Get(job.Id)!.Status);
        Assert.Equal("Your access to bulk job administration has been removed. Reload the page.", GetField(page, "actionMessage"));
    }

    [Fact]
    public async Task AJobCarryingNoModule_IsFiledUnderThisPagesOwnCategory_NotABlankOne()
    {
        // kd-53. The category comes from the job; a job with none would leave the row uncategorised, which no filter and no
        // compliance query can reach.
        var (jobs, store) = JobService();
        var job = Job(BulkJobStatus.Queued, moduleId: "");
        store.Insert(job);
        var page = BuildPage(jobs);

        await Cancel(page, job);

        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == Action);
        Assert.Equal("AdminBulkJobs", row.GetProperty("category").GetString());
        Assert.Equal("Success", row.GetProperty("result").GetString());
    }

    [Fact]
    public async Task ACancelTheStoreFails_IsAuditedAsFailed_AndStillThrows()
    {
        var (jobs, _) = JobService(migrated: false);
        var job = Job(BulkJobStatus.Queued);
        var page = BuildPage(jobs);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => Cancel(page, job));

        var row = TheRow(job, success: false, error: thrown.Message);
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
    }

    // ---- kd-54: a removal that did not happen says why --------------------------------------------
    // RemoveJob filed Failed with no error and no refusal fields when DeleteJob answered false, and the banner guessed "it
    // may still be running". DeleteJob refuses a job that is gone and one that has not finished; the row now names which.

    private const string RemoveAction = "AdminBulkJobs_RemoveJob";

    private static Task Remove(Page page, BulkJob job) =>
        (Task)typeof(Page).GetMethod("RemoveJob", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [job])!;

    [Fact]
    public async Task ARemovalThatHappens_IsAuditedAsDone()
    {
        var (jobs, store) = JobService();
        var job = Job(BulkJobStatus.Completed);
        store.Insert(job);
        var page = BuildPage(jobs);

        await Remove(page, job);

        Assert.Null(store.Get(job.Id));
        var row = TheRow(job, success: true, error: null, action: RemoveAction);
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
        Assert.Equal($"Removed {Module} job T.", GetField(page, "actionMessage"));
    }

    [Theory]
    [InlineData("running", "it has not finished.")]
    [InlineData("queued", "it has not finished.")]
    [InlineData("gone", "it no longer exists.")]
    public async Task ARemovalThatDoesNotHappen_IsAuditedWithItsReason(string state, string because)
    {
        var (jobs, store) = JobService();
        var job = Job(state == "running" ? BulkJobStatus.Running : BulkJobStatus.Queued);
        if (state != "gone")
            store.Insert(job);
        var page = BuildPage(jobs);

        await Remove(page, job);

        var row = TheRow(job, success: false, error: $"The job was not removed: {because}", action: RemoveAction);
        Assert.Equal("JobState", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal($"Could not remove {Module} job T - {because}", GetField(page, "actionMessage"));
        if (state == "gone")
            Assert.Null(store.Get(job.Id));
        else
            Assert.Equal(job.Status, store.Get(job.Id)!.Status);
    }

    [Fact]
    public async Task TwoAdministratorsRemovingTheSameFinishedJob_TheSecondIsToldItNoLongerExists()
    {
        // kd-77. The removal read the job and then deleted it in another step, and read a delete that removed nothing as a job
        // that had not finished. Two administrators removing the same finished job both read it, the first deleted it, and the
        // second was told - in its banner and its audit row - "it has not finished". Here the first removal is held inside its
        // own write transaction, the job deleted and not yet committed, while the second reaches the store; then it commits.
        var factory = NewJobsDatabase();
        var (jobs, store) = JobService(factory);
        var job = Job(BulkJobStatus.Completed);
        store.Insert(job);
        var page = BuildPage(jobs);

        Task second;
        using (var first = factory.Open())
        {
            using var removing = first.BeginTransaction(deferred: false);
            using (var delete = first.CreateCommand())
            {
                delete.Transaction = removing;
                delete.CommandText = "DELETE FROM bulk_job WHERE id = $id;";
                delete.Parameters.AddWithValue("$id", job.Id);
                Assert.Equal(1, delete.ExecuteNonQuery());
            }

            second = Task.Run(() => Remove(page, job), TestContext.Current.CancellationToken);
            // Long enough for the second removal to reach the store; well inside the five seconds a store call waits for a
            // write lock before it fails.
            await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            Assert.False(second.IsCompleted, "the second removal did not wait for the first's write lock");
            removing.Commit();
        }

        await second.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        var row = TheRow(job, success: false, error: "The job was not removed: it no longer exists.", action: RemoveAction);
        Assert.Equal("JobState", row.GetProperty("refusedAt").GetString());
        Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
        Assert.Equal($"Could not remove {Module} job T - it no longer exists.", GetField(page, "actionMessage"));
        Assert.Null(store.Get(job.Id));
    }

    [Fact]
    public async Task ARemovalTheStoreFails_IsAuditedAsFailed_AndStillThrows()
    {
        // kd-62. The removal and its reason are one call now, and it is wrapped: a store that throws leaves the row it
        // always owed - a mutation was attempted - instead of ending the handler with no record and no banner.
        var (jobs, _) = JobService(migrated: false);
        var job = Job(BulkJobStatus.Completed);
        var page = BuildPage(jobs);

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => Remove(page, job));

        var row = TheRow(job, success: false, error: thrown.Message, action: RemoveAction);
        Assert.False(row.TryGetProperty("refusedAt", out _));
        Assert.False(row.TryGetProperty("mutationAttempted", out _));
    }

    // ---- kd-86: a cancel or a removal whose list reload fails says what it did, keeps its row, and goes no further ----
    // CancelJob and RemoveJob reloaded the job list once the row was filed, outside any try, so a jobs store that failed
    // that read - after a cancel or a removal that happened - threw out of the handler to the circuit. The throw is now
    // logged, the banner keeps what the action did and says the list could not be refreshed, nothing escapes, and the
    // action's own row stands, with nothing more filed.

    private const string NotRefreshed = " The job list could not be refreshed.";

    [Theory]
    [InlineData("queued")]
    [InlineData("running")]
    [InlineData("finished")]
    public async Task ACancelWhoseListReloadFails_SaysWhatItDid_KeepsItsRow_AndThrowsNothing(string state)
    {
        // "queued" and "running" are the finding: the store takes the cancel - the queued job finished Cancelled, the
        // running job's cancellation requested - and fails every read after it, the page's reload among them (and, for the
        // queued job, the cancel's own read of the job, which kd-85 answers as the write landed). "finished" is the cancel
        // that took no effect, the job having finished by the click: nothing is written, and the store fails the reload.
        var (jobs, store) = JobService();
        var job = Job(state == "queued" ? BulkJobStatus.Queued : BulkJobStatus.Running);
        store.Insert(state == "finished" ? Job(BulkJobStatus.Completed) : job);
        var log = new PageLog();
        var page = BuildPage(jobs, logger: log);
        var unreadable = state == "finished" ? FailTheReloadOnly(store) : FailEveryReadAfterTheWrite(jobs, store);

        var escaped = await Record.ExceptionAsync(() => Cancel(page, job));

        Assert.Null(escaped);
        AssertTheReloadFailedAndWasLogged(jobs, unreadable, log,
            "The job list could not be refreshed after the cancel of bulk job job-1");
        // The cancel's own row, as without the failure, and no other row at all.
        Assert.Single(_h.AuditRows());
        if (state == "finished")
        {
            var row = TheRow(job, success: false, error: NotCancelled);
            Assert.Equal("JobState", row.GetProperty("refusedAt").GetString());
            Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
            Assert.Equal($"{Module} job T was not cancelled - it had already finished, or it no longer exists.{NotRefreshed}",
                GetField(page, "actionMessage"));
        }
        else
        {
            var row = TheRow(job, success: true, error: null);
            Assert.False(row.TryGetProperty("refusedAt", out _));
            Assert.False(row.TryGetProperty("mutationAttempted", out _));
            Assert.Equal($"Cancellation requested for {Module} job T.{NotRefreshed}", GetField(page, "actionMessage"));
        }

        // What the store holds, read once it can be read again: the cancel as its write left it, or nothing changed.
        Readable(store);
        var held = store.Get(job.Id)!;
        if (state == "queued")
            Assert.Equal(BulkJobStatus.Cancelled, held.Status);
        else if (state == "running")
            Assert.True(held.CancelRequested);
        else
            Assert.Equal(BulkJobStatus.Completed, held.Status);
    }

    [Theory]
    [InlineData("removed")]
    [InlineData("gone")]
    public async Task ARemovalWhoseListReloadFails_SaysWhatItDid_KeepsItsRow_AndThrowsNothing(string state)
    {
        // "removed" is the finding: the store deletes the finished job and fails every read after it, the page's reload
        // among them. "gone" is the removal that did not happen - another administrator removed the job first (kd-77) -
        // with the store failing the reload: nothing is written, so no change event marks the moment, and the stand-in
        // engages as the reload begins.
        var (jobs, store) = JobService();
        var job = Job(BulkJobStatus.Completed);
        if (state == "removed")
            store.Insert(job);
        var log = new PageLog();
        var page = BuildPage(jobs, logger: log);
        var unreadable = state == "removed" ? FailEveryReadAfterTheWrite(jobs, store) : FailTheReloadOnly(store);

        var escaped = await Record.ExceptionAsync(() => Remove(page, job));

        Assert.Null(escaped);
        AssertTheReloadFailedAndWasLogged(jobs, unreadable, log,
            "The job list could not be refreshed after the removal of bulk job job-1");
        // The removal's own row, as without the failure, and no other row at all.
        Assert.Single(_h.AuditRows());
        if (state == "removed")
        {
            var row = TheRow(job, success: true, error: null, action: RemoveAction);
            Assert.False(row.TryGetProperty("refusedAt", out _));
            Assert.False(row.TryGetProperty("mutationAttempted", out _));
            Assert.Equal($"Removed {Module} job T.{NotRefreshed}", GetField(page, "actionMessage"));
        }
        else
        {
            var row = TheRow(job, success: false, error: "The job was not removed: it no longer exists.", action: RemoveAction);
            Assert.Equal("JobState", row.GetProperty("refusedAt").GetString());
            Assert.False(row.GetProperty("mutationAttempted").GetBoolean());
            Assert.Equal($"Could not remove {Module} job T - it no longer exists.{NotRefreshed}", GetField(page, "actionMessage"));
        }

        Readable(store);
        Assert.Null(store.Get(job.Id));
    }

    // The reload failed and the page said so: the stand-in engaged and the store fails a read of the list, the reload's
    // activity is over, and the page logged one error naming the job, with the store's own exception.
    private void AssertTheReloadFailedAndWasLogged(BulkJobService jobs, Func<bool> unreadable, PageLog log, string line)
    {
        Assert.True(unreadable(), "the jobs store was not made unreadable");
        Assert.ThrowsAny<SqliteException>(() => jobs.GetActiveJobs());
        Assert.Empty(_progress.Active);
        var said = Assert.Single(log.Entries);
        Assert.Equal(LogLevel.Error, said.Level);
        Assert.Equal(line, said.Message);
        Assert.Contains("no such table: bulk_job", Assert.IsType<SqliteException>(said.Exception).Message);
    }

    // kd-84's and kd-85's stand-in, at the page: a jobs store that takes the action's write and fails every read after it.
    // The jobs table is renamed away (kd-82's device) when JobChanged announces the write - the service raises it once the
    // cancel or the removal has landed, before the page reloads - so the reload's first read meets "no such table".
    // Returns whether it ran.
    private static Func<bool> FailEveryReadAfterTheWrite(BulkJobService jobs, BulkJobRepository store)
    {
        var renamed = false;
        jobs.JobChanged += _ =>
        {
            if (renamed)
                return;
            RenameJobsTable(store, "bulk_job", "bulk_job_held");
            renamed = true;
        };
        return () => renamed;
    }

    // kd-82's stand-in, for an action that wrote nothing and so raised no JobChanged: a jobs store that fails the reload
    // and nothing before it. RefreshJobs begins its "Loading the bulk job list" activity before it reads, so the jobs
    // table is renamed away the moment that activity begins. Returns whether it ran.
    private Func<bool> FailTheReloadOnly(BulkJobRepository store)
    {
        var renamed = false;
        _progress.Changed += () =>
        {
            if (renamed || !_progress.Active.Any(activity => activity.Label == "Loading the bulk job list"))
                return;
            RenameJobsTable(store, "bulk_job", "bulk_job_held");
            renamed = true;
        };
        return () => renamed;
    }

    // The jobs table renamed back, so the test can read what the store holds.
    private static void Readable(BulkJobRepository store) => RenameJobsTable(store, "bulk_job_held", "bulk_job");

    private static void RenameJobsTable(BulkJobRepository store, string from, string to)
    {
        using var connection = new SqliteConnectionFactory(store.DatabasePath).Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"ALTER TABLE {from} RENAME TO {to};";
        command.ExecuteNonQuery();
    }

    // ---- the row -------------------------------------------------------------------------------

    // The one cancel row, as the page always wrote it - operator, IP, module/job, ticket, the job as rendered - with the
    // outcome and its reason.
    private JsonElement TheRow(BulkJob job, bool success, string? error, string action = Action)
    {
        var row = Assert.Single(_h.AuditRows(), r => r.GetProperty("action").GetString() == action);
        // kd-53. Filed under the job's own module: this fixture's job is a Comms-10k one, and every row went under
        // ConferenceRooms whatever module the job belonged to.
        Assert.Equal(Module, row.GetProperty("category").GetString());
        Assert.Equal("admin", row.GetProperty("user").GetString());
        Assert.Equal(OperatorIp, row.GetProperty("ip").GetString());
        Assert.Equal($"{Module}/{job.Id}", row.GetProperty("target").GetString());
        Assert.Equal("INC777", row.GetProperty("ticket").GetString());
        Assert.Equal(success ? "Success" : "Failed", row.GetProperty("result").GetString());
        Assert.Equal(error, row.TryGetProperty("error", out var written) && written.ValueKind != JsonValueKind.Null ? written.GetString() : null);
        Assert.Equal(Module, row.GetProperty("old_ModuleId").GetString());
        Assert.Equal(job.Status.ToString(), row.GetProperty("old_Status").GetString());
        Assert.Equal(3, row.GetProperty("old_TotalRows").GetInt32());
        return row;
    }

    // ---- the page and its job store --------------------------------------------------------------

    private static BulkJob Job(BulkJobStatus status, string moduleId = Module) => new()
    {
        Id = "job-1",
        ModuleId = moduleId,
        JobType = "T",
        Status = status,
        SubmittedBy = "jdoe",
        SubmittedIp = "10.0.0.9",
        Ticket = "INC777",
        PayloadJson = "{}",
        SubmittedAtUtc = Submitted,
        TotalRows = 3,
    };

    // With migrated false the database has no tables, so every store call throws.
    private (BulkJobService Jobs, BulkJobRepository Store) JobService(bool migrated = true)
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_h.Dir, $"jobs-{Guid.NewGuid():N}.db"));
        if (migrated)
            new JobStoreMigrator(factory).Migrate();
        return JobService(factory);
    }

    // kd-77. A jobs database of its own, migrated, for a test that also opens it.
    private SqliteConnectionFactory NewJobsDatabase()
    {
        var factory = new SqliteConnectionFactory(Path.Combine(_h.Dir, $"jobs-{Guid.NewGuid():N}.db"));
        new JobStoreMigrator(factory).Migrate();
        return factory;
    }

    private (BulkJobService Jobs, BulkJobRepository Store) JobService(SqliteConnectionFactory factory)
    {
        var store = new BulkJobRepository(factory);
        var scopes = new ServiceCollection().BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        return (new BulkJobService(scopes, store, new BulkJobProcessorRegistry([]), _h.Config, NullLogger<BulkJobService>.Instance), store);
    }

    private Page BuildPage(BulkJobService jobs, bool authorized = true, ILogger<Page>? logger = null)
    {
        var page = new Page();
        SetProperty(page, "BulkJobs", jobs);
        SetProperty(page, "Audit", _h.Audit);
        SetProperty(page, "AuthStateProvider", Authentication());
        SetProperty(page, "AuthorizationService", Authorization(authorized));
        SetProperty(page, "Logger", logger ?? NullLogger<Page>.Instance);
        SetProperty(page, "Progress", _progress);
        SetField(page, "currentUser", Operator);
        SetField(page, "clientIpAddress", OperatorIp);
        return page;
    }

    private static Task Cancel(Page page, BulkJob job) =>
        (Task)typeof(Page).GetMethod("CancelJob", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [job])!;

    private static AuthenticationStateProvider Authentication()
    {
        var state = new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Name, Operator)], "test")));
        var provider = Substitute.For<AuthenticationStateProvider>();
        provider.GetAuthenticationStateAsync().Returns(Task.FromResult(state));
        return provider;
    }

    private static IAuthorizationService Authorization(bool authorized)
    {
        var service = Substitute.For<IAuthorizationService>();
        service.AuthorizeAsync(Arg.Any<ClaimsPrincipal>(), Arg.Any<object?>(), Arg.Any<string>())
            .Returns(authorized ? AuthorizationResult.Success() : AuthorizationResult.Failed());
        return service;
    }

    private static void SetProperty(Page page, string name, object value) =>
        typeof(Page).GetProperty(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static void SetField(Page page, string name, object? value) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, value);

    private static object? GetField(Page page, string name) =>
        typeof(Page).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(page);

    // kd-86. The page's log, kept: each line's level, its rendered message and its exception.
    private sealed class PageLog : ILogger<Page>
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _entries = [];

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Entries
        {
            get
            {
                lock (_entries)
                    return _entries.ToList();
            }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add((logLevel, formatter(state, exception), exception));
        }
    }
}
