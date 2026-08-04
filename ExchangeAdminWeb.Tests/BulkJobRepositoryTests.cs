using ExchangeAdminWeb.Services.Jobs;
using ExchangeAdminWeb.Services.Storage;

namespace ExchangeAdminWeb.Tests;

public class BulkJobRepositoryTests
{
    private static BulkJobRepository CreateRepo(TempDir temp)
    {
        var factory = new SqliteConnectionFactory(System.IO.Path.Combine(temp.Path, "exchangeadmin-jobs.db"));
        new JobStoreMigrator(factory).Migrate();
        return new BulkJobRepository(factory);
    }

    private static BulkJob NewJob(string id, BulkJobStatus status = BulkJobStatus.Queued) => new()
    {
        Id = id,
        ModuleId = "ConferenceRooms",
        JobType = "SetMetadata_Bulk",
        Status = status,
        SubmittedBy = "jdoe",
        SubmittedByDisplay = "Jane Doe",
        SubmittedIp = "10.0.0.5",
        Ticket = "INC123",
        AuthSnapshotJson = """{"roles":["ConfRoomAdmins"]}""",
        PayloadJson = """[{"email":"room1@x"}]""",
        SubmittedAtUtc = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc)
    };

    [Fact]
    public void InsertThenGet_RoundTripsAllFields()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        var job = NewJob("j1");

        repo.Insert(job);
        var loaded = repo.Get("j1");

        Assert.NotNull(loaded);
        Assert.Equal("ConferenceRooms", loaded!.ModuleId);
        Assert.Equal("SetMetadata_Bulk", loaded.JobType);
        Assert.Equal(BulkJobStatus.Queued, loaded.Status);
        Assert.Equal("jdoe", loaded.SubmittedBy);
        Assert.Equal("Jane Doe", loaded.SubmittedByDisplay);
        Assert.Equal("10.0.0.5", loaded.SubmittedIp);
        Assert.Equal("INC123", loaded.Ticket);
        Assert.Equal("""{"roles":["ConfRoomAdmins"]}""", loaded.AuthSnapshotJson);
        Assert.Equal("""[{"email":"room1@x"}]""", loaded.PayloadJson);
        Assert.Equal(job.SubmittedAtUtc, loaded.SubmittedAtUtc);
    }

    [Fact]
    public void Get_MissingId_ReturnsNull()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        Assert.Null(repo.Get("nope"));
    }

    [Fact]
    public void TryStart_PromotesQueuedToRunning_StampsTotalAndTimestamps()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(NewJob("j1"));
        var now = new DateTime(2026, 1, 1, 12, 1, 0, DateTimeKind.Utc);

        var started = repo.TryStart("j1", totalRows: 10, now);

        Assert.True(started);
        var loaded = repo.Get("j1")!;
        Assert.Equal(BulkJobStatus.Running, loaded.Status);
        Assert.Equal(10, loaded.TotalRows);
        Assert.Equal(now, loaded.StartedAtUtc);
        Assert.Equal(now, loaded.HeartbeatAtUtc);
    }

    [Fact]
    public void TryStart_IsCompareAndSwap_DoesNotResurrectTerminalJob()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        var interrupted = NewJob("j1", BulkJobStatus.Interrupted);
        interrupted.FinishedAtUtc = new DateTime(2026, 1, 1, 11, 0, 0, DateTimeKind.Utc);
        repo.Insert(interrupted);

        var started = repo.TryStart("j1", 5, DateTime.UtcNow);

        Assert.False(started);
        Assert.Equal(BulkJobStatus.Interrupted, repo.Get("j1")!.Status);
    }

    [Fact]
    public void RequestCancel_SetsFlagOnActiveJob_TargetedNotStatusOverwrite()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(NewJob("j1", BulkJobStatus.Running));

        repo.RequestCancel("j1");

        Assert.True(repo.IsCancelRequested("j1"));
        // Cancel is a targeted flag write - it must not itself change the status.
        Assert.Equal(BulkJobStatus.Running, repo.Get("j1")!.Status);
    }

    [Fact]
    public void RequestCancel_NoOpOnTerminalJob()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(NewJob("j1", BulkJobStatus.Completed));

        repo.RequestCancel("j1");

        Assert.False(repo.IsCancelRequested("j1"));
    }

    [Fact]
    public void RecordRow_UpsertsRowAndDerivesAggregatesFromRows_InOneStep()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(NewJob("j1", BulkJobStatus.Running));
        var hb = new DateTime(2026, 1, 1, 12, 5, 0, DateTimeKind.Utc);

        repo.RecordRow(new BulkJobRow { JobId = "j1", RowIndex = 0, Target = "a", Status = BulkJobRowStatus.Success, RecordedAtUtc = hb }, hb);
        repo.RecordRow(new BulkJobRow { JobId = "j1", RowIndex = 1, Target = "b", Status = BulkJobRowStatus.Partial, RecordedAtUtc = hb }, hb);
        repo.RecordRow(new BulkJobRow { JobId = "j1", RowIndex = 2, Target = "c", Status = BulkJobRowStatus.Failed, RecordedAtUtc = hb }, hb);

        var loaded = repo.Get("j1")!;
        Assert.Equal(3, loaded.ProcessedRows);
        Assert.Equal(1, loaded.SuccessCount);
        Assert.Equal(1, loaded.PartialCount);
        Assert.Equal(1, loaded.FailedCount);
        Assert.Equal(hb, loaded.HeartbeatAtUtc);
    }

    [Fact]
    public void RecordRow_ReRecordingSameIndex_DoesNotDoubleCount()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(NewJob("j1", BulkJobStatus.Running));
        var hb = DateTime.UtcNow;

        repo.RecordRow(new BulkJobRow { JobId = "j1", RowIndex = 0, Target = "r@x", Status = BulkJobRowStatus.Failed, Message = "boom", RecordedAtUtc = hb }, hb);
        repo.RecordRow(new BulkJobRow { JobId = "j1", RowIndex = 0, Target = "r@x", Status = BulkJobRowStatus.Success, Message = "ok on retry", RecordedAtUtc = hb }, hb);

        var rows = repo.GetRows("j1");
        Assert.Single(rows);
        Assert.Equal(BulkJobRowStatus.Success, rows[0].Status);
        Assert.Equal("ok on retry", rows[0].Message);

        var loaded = repo.Get("j1")!;
        Assert.Equal(1, loaded.ProcessedRows);
        Assert.Equal(1, loaded.SuccessCount);
        Assert.Equal(0, loaded.FailedCount);
    }

    [Fact]
    public void TryFinish_TransitionsFromAllowedState_StampsFinishedAndMessage()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(NewJob("j1", BulkJobStatus.Running));
        var finished = new DateTime(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc);

        var ok = repo.TryFinish("j1", BulkJobStatus.Completed, "all done", finished, BulkJobStatus.Running);

        Assert.True(ok);
        var loaded = repo.Get("j1")!;
        Assert.Equal(BulkJobStatus.Completed, loaded.Status);
        Assert.Equal(finished, loaded.FinishedAtUtc);
        Assert.Equal("all done", loaded.Message);
    }

    [Fact]
    public void TryFinish_RefusesTransitionFromDisallowedState()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(NewJob("j1", BulkJobStatus.Queued));

        // Only Running is allowed as a source here; a Queued job must not be marked Completed.
        var ok = repo.TryFinish("j1", BulkJobStatus.Completed, null, DateTime.UtcNow, BulkJobStatus.Running);

        Assert.False(ok);
        Assert.Equal(BulkJobStatus.Queued, repo.Get("j1")!.Status);
    }

    [Fact]
    public void GetRows_ReturnsInRowIndexOrder()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(NewJob("j1", BulkJobStatus.Running));
        var hb = DateTime.UtcNow;

        repo.RecordRow(new BulkJobRow { JobId = "j1", RowIndex = 2, Target = "c", Status = BulkJobRowStatus.Success, RecordedAtUtc = hb }, hb);
        repo.RecordRow(new BulkJobRow { JobId = "j1", RowIndex = 0, Target = "a", Status = BulkJobRowStatus.Success, RecordedAtUtc = hb }, hb);
        repo.RecordRow(new BulkJobRow { JobId = "j1", RowIndex = 1, Target = "b", Status = BulkJobRowStatus.Partial, RecordedAtUtc = hb }, hb);

        var rows = repo.GetRows("j1");
        Assert.Equal(["a", "b", "c"], rows.Select(r => r.Target));
    }

    [Fact]
    public void GetActive_ReturnsQueuedAndRunning_FifoBySubmittedAt()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        var running = NewJob("running", BulkJobStatus.Running);
        var queuedNew = NewJob("queuedNew", BulkJobStatus.Queued);
        var queuedOld = NewJob("queuedOld", BulkJobStatus.Queued);
        var done = NewJob("done", BulkJobStatus.Completed);
        // Distinct submission times so FIFO ordering is deterministic.
        repo.Insert(WithSubmitted(running, new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        repo.Insert(WithSubmitted(queuedOld, new DateTime(2026, 1, 1, 10, 1, 0, DateTimeKind.Utc)));
        repo.Insert(WithSubmitted(queuedNew, new DateTime(2026, 1, 1, 10, 2, 0, DateTimeKind.Utc)));
        repo.Insert(WithSubmitted(done, new DateTime(2026, 1, 1, 9, 0, 0, DateTimeKind.Utc)));

        var active = repo.GetActive();
        Assert.Equal(["running", "queuedOld", "queuedNew"], active.Select(j => j.Id));
    }

    [Fact]
    public void InterruptAllNonTerminal_FlipsQueuedAndRunning_LeavesTerminalUntouched()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(NewJob("running", BulkJobStatus.Running));
        repo.Insert(NewJob("queued", BulkJobStatus.Queued));
        var completed = NewJob("completed", BulkJobStatus.Completed);
        completed.FinishedAtUtc = new DateTime(2026, 1, 1, 13, 0, 0, DateTimeKind.Utc);
        repo.Insert(completed);

        var flipped = repo.InterruptAllNonTerminal("Interrupted by app restart");

        Assert.Equal(2, flipped);
        Assert.Equal(BulkJobStatus.Interrupted, repo.Get("running")!.Status);
        Assert.Equal(BulkJobStatus.Interrupted, repo.Get("queued")!.Status);
        Assert.Equal(BulkJobStatus.Completed, repo.Get("completed")!.Status);
        Assert.Equal("Interrupted by app restart", repo.Get("running")!.Message);
        Assert.NotNull(repo.Get("running")!.FinishedAtUtc);
    }

    [Fact]
    public void PruneFinishedBefore_DeletesOldTerminalJobsAndCascadesRows_KeepsRecentAndActive()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        var oldDone = NewJob("old", BulkJobStatus.Completed);
        oldDone.FinishedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        repo.Insert(oldDone);
        repo.RecordRow(new BulkJobRow { JobId = "old", RowIndex = 0, Target = "r", Status = BulkJobRowStatus.Success, RecordedAtUtc = DateTime.UtcNow }, DateTime.UtcNow);

        var recentDone = NewJob("recent", BulkJobStatus.Completed);
        recentDone.FinishedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        repo.Insert(recentDone);

        repo.Insert(NewJob("running", BulkJobStatus.Running)); // no finished_at, never pruned

        var pruned = repo.PruneFinishedBefore(new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));

        Assert.Equal(1, pruned);
        Assert.Null(repo.Get("old"));
        Assert.Empty(repo.GetRows("old")); // cascaded
        Assert.NotNull(repo.Get("recent"));
        Assert.NotNull(repo.Get("running"));
    }

    [Fact]
    public void GetRecentFinished_NewestFirst_RespectsLimit()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        for (var i = 0; i < 5; i++)
        {
            var j = NewJob($"j{i}", BulkJobStatus.Completed);
            j.FinishedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i);
            repo.Insert(j);
        }

        var recent = repo.GetRecentFinished(3);
        Assert.Equal(["j4", "j3", "j2"], recent.Select(j => j.Id));
    }

    [Fact]
    public void GetFinishedByType_FiltersByModuleAndType_ExcludesEverythingElse()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        repo.Insert(Finished(NewJob("mine", BulkJobStatus.Completed)));
        repo.Insert(Finished(Retyped(NewJob("otherType", BulkJobStatus.Completed), jobType: "SetType_Bulk")));
        repo.Insert(Finished(Retyped(NewJob("otherModule", BulkJobStatus.Completed), moduleId: "MessageTrace")));
        repo.Insert(NewJob("stillRunning", BulkJobStatus.Running));

        var found = repo.GetFinishedByType("ConferenceRooms", "SetMetadata_Bulk", 50);

        Assert.Equal(["mine"], found.Select(j => j.Id));
    }

    [Fact]
    public void GetFinishedByType_IsNotBoundedByTheRecentJobLimit()
    {
        // The reason this method exists rather than reusing GetRecentFinished: that one is unfiltered
        // and capped at BulkJobs:RecentJobLimit (default 25), so a busy day in another module would
        // silently empty a page built on it.
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        for (var i = 0; i < 40; i++)
        {
            var j = NewJob($"j{i}", BulkJobStatus.Completed);
            j.FinishedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i);
            repo.Insert(j);
        }

        Assert.Equal(40, repo.GetFinishedByType("ConferenceRooms", "SetMetadata_Bulk", 200).Count);
    }

    [Fact]
    public void GetFinishedByType_IncludesCancelledAndInterrupted_NewestFirst()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        var statuses = new[] { BulkJobStatus.Completed, BulkJobStatus.Cancelled, BulkJobStatus.Interrupted };
        for (var i = 0; i < statuses.Length; i++)
        {
            var j = NewJob($"j{i}", statuses[i]);
            j.FinishedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i);
            repo.Insert(j);
        }

        var found = repo.GetFinishedByType("ConferenceRooms", "SetMetadata_Bulk", 50);

        Assert.Equal(["j2", "j1", "j0"], found.Select(j => j.Id));
    }

    // -------------------------------------------------------------------------
    // Module-scoped reads. A module panel built on the unfiltered reads showed a
    // Message Analysis export as a Conference Rooms job and offered a Cancel
    // button for it (docs/ConferenceRoomsBulkJobPanel-Plan.md F1).
    // -------------------------------------------------------------------------

    [Fact]
    public void GetActiveByModule_ReturnsOnlyThatModulesNonTerminalJobs()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        repo.Insert(NewJob("mineQueued", BulkJobStatus.Queued));
        repo.Insert(NewJob("mineRunning", BulkJobStatus.Running));
        repo.Insert(Retyped(NewJob("foreignRunning", BulkJobStatus.Running), moduleId: "MessageTrace"));
        repo.Insert(Finished(NewJob("mineDone", BulkJobStatus.Completed)));

        var active = repo.GetActiveByModule("ConferenceRooms");

        Assert.Equal(["mineQueued", "mineRunning"], active.Select(j => j.Id).OrderBy(id => id));
    }

    [Fact]
    public void GetActiveByModule_ExcludesAForeignRunningJob_WhichIsTheCancelExposure()
    {
        // The severe half of F1: the panel renders a Cancel button per active row and
        // BulkJobService.CancelJob takes only an id, so a foreign row here is a live control
        // over another module's work.
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        repo.Insert(Retyped(NewJob("export", BulkJobStatus.Running), moduleId: "MessageTrace"));

        Assert.Empty(repo.GetActiveByModule("ConferenceRooms"));
    }

    [Fact]
    public void GetFinishedByModule_ReturnsEveryJobTypeOfThatModuleOnly()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        repo.Insert(Finished(NewJob("finder", BulkJobStatus.Completed)));
        repo.Insert(Finished(Retyped(NewJob("type", BulkJobStatus.Completed), jobType: "SetType_Bulk")));
        repo.Insert(Finished(Retyped(NewJob("foreign", BulkJobStatus.Completed), moduleId: "MessageTrace")));

        var found = repo.GetFinishedByModule("ConferenceRooms", 50);

        // Both of this module's kinds, neither of anyone else's. This is why the panel needs its
        // own read rather than GetFinishedByType, which is per (module, type).
        Assert.Equal(["finder", "type"], found.Select(j => j.Id).OrderBy(id => id));
    }

    [Fact]
    public void GetFinishedByModule_ExcludesNonTerminalJobs()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        repo.Insert(NewJob("running", BulkJobStatus.Running));
        repo.Insert(Finished(NewJob("done", BulkJobStatus.Completed)));

        Assert.Equal(["done"], repo.GetFinishedByModule("ConferenceRooms", 50).Select(j => j.Id));
    }

    [Fact]
    public void GetFinishedByModule_AppliesTheLimitAfterTheModuleFilter()
    {
        // The reason the predicate must be in SQL rather than applied by the caller: LIMIT is
        // applied by the database. Filtering after an unfiltered read would let these foreign
        // jobs consume the whole window and show this module an empty panel.
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        for (var i = 0; i < 10; i++)
        {
            var foreign = Retyped(NewJob($"foreign{i}", BulkJobStatus.Completed), moduleId: "MessageTrace");
            foreign.FinishedAtUtc = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc).AddMinutes(i);
            repo.Insert(foreign);
        }

        var mine = NewJob("mine", BulkJobStatus.Completed);
        mine.FinishedAtUtc = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc); // OLDEST of the lot
        repo.Insert(mine);

        var found = repo.GetFinishedByModule("ConferenceRooms", 5);

        Assert.Equal(["mine"], found.Select(j => j.Id));
    }

    // -------------------------------------------------------------------------
    // Delete - operator "Remove" on a finished job
    // -------------------------------------------------------------------------

    [Fact]
    public void Delete_RemovesATerminalJobAndCascadesItsRows()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(Finished(NewJob("done", BulkJobStatus.Completed)));
        repo.RecordRow(new BulkJobRow { JobId = "done", RowIndex = 0, Target = "r", Status = BulkJobRowStatus.Success, RecordedAtUtc = DateTime.UtcNow }, DateTime.UtcNow);

        Assert.True(repo.Delete("done"));

        Assert.Null(repo.Get("done"));
        Assert.Empty(repo.GetRows("done"));
    }

    [Theory]
    [InlineData(BulkJobStatus.Queued)]
    [InlineData(BulkJobStatus.Running)]
    public void Delete_RefusesANonTerminalJob(BulkJobStatus status)
    {
        // Deleting an active job would leave the runner holding a cancellation token for a row
        // that no longer exists, and its next heartbeat write would target a missing row. The
        // guard is in SQL so no caller can skip it.
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(NewJob("active", status));

        Assert.False(repo.Delete("active"));
        Assert.NotNull(repo.Get("active"));
    }

    [Fact]
    public void Delete_ReturnsFalseForAnUnknownId()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        Assert.False(repo.Delete("nope"));
    }

    [Fact]
    public void Delete_RemovesOnlyTheNamedJob()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(Finished(NewJob("target", BulkJobStatus.Completed)));
        repo.Insert(Finished(NewJob("bystander", BulkJobStatus.Completed)));

        repo.Delete("target");

        Assert.Null(repo.Get("target"));
        Assert.NotNull(repo.Get("bystander"));
    }

    // -------------------------------------------------------------------------
    // AppendMessage - the completion-step note the terminal transition cannot carry
    // -------------------------------------------------------------------------

    [Fact]
    public void AppendMessage_OnTerminalJob_Writes_WithoutChangingTheResult()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        var job = NewJob("j1", BulkJobStatus.Completed);
        job.FinishedAtUtc = new DateTime(2026, 1, 1, 13, 0, 0, DateTimeKind.Utc);
        repo.Insert(job);

        Assert.True(repo.AppendMessage("j1", "log save failed"));

        var loaded = repo.Get("j1")!;
        Assert.Equal("log save failed", loaded.Message);
        // The whole point: a delivery note must not rewrite a job result.
        Assert.Equal(BulkJobStatus.Completed, loaded.Status);
        Assert.Equal(job.FinishedAtUtc, loaded.FinishedAtUtc);
    }

    /// <summary>
    /// Appends rather than replaces, so a note can never erase the reason a job was cancelled or
    /// interrupted - that text is the only record of why.
    /// </summary>
    [Fact]
    public void AppendMessage_KeepsAnExistingMessage()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        var job = NewJob("j1", BulkJobStatus.Cancelled);
        job.Message = "Cancelled by operator.";
        repo.Insert(job);

        repo.AppendMessage("j1", "log save failed");

        var loaded = repo.Get("j1")!;
        Assert.Contains("Cancelled by operator.", loaded.Message);
        Assert.Contains("log save failed", loaded.Message);
    }

    [Fact]
    public void AppendMessage_UnknownJob_ReturnsFalse()
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);

        Assert.False(repo.AppendMessage("nope", "log save failed"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AppendMessage_BlankNote_IsRejected(string? note)
    {
        using var temp = new TempDir();
        var repo = CreateRepo(temp);
        repo.Insert(NewJob("j1", BulkJobStatus.Completed));

        Assert.ThrowsAny<ArgumentException>(() => repo.AppendMessage("j1", note!));
    }

    private static BulkJob Finished(BulkJob job)
    {
        job.FinishedAtUtc = new DateTime(2026, 1, 1, 13, 0, 0, DateTimeKind.Utc);
        return job;
    }

    private static BulkJob Retyped(BulkJob job, string? moduleId = null, string? jobType = null) => new()
    {
        Id = job.Id,
        ModuleId = moduleId ?? job.ModuleId,
        JobType = jobType ?? job.JobType,
        Status = job.Status,
        SubmittedBy = job.SubmittedBy,
        SubmittedByDisplay = job.SubmittedByDisplay,
        SubmittedIp = job.SubmittedIp,
        Ticket = job.Ticket,
        AuthSnapshotJson = job.AuthSnapshotJson,
        PayloadJson = job.PayloadJson,
        SubmittedAtUtc = job.SubmittedAtUtc,
        FinishedAtUtc = job.FinishedAtUtc
    };

    private static BulkJob WithSubmitted(BulkJob job, DateTime submitted) => new()
    {
        Id = job.Id,
        ModuleId = job.ModuleId,
        JobType = job.JobType,
        Status = job.Status,
        SubmittedBy = job.SubmittedBy,
        SubmittedByDisplay = job.SubmittedByDisplay,
        SubmittedIp = job.SubmittedIp,
        Ticket = job.Ticket,
        AuthSnapshotJson = job.AuthSnapshotJson,
        PayloadJson = job.PayloadJson,
        SubmittedAtUtc = submitted
    };
}
