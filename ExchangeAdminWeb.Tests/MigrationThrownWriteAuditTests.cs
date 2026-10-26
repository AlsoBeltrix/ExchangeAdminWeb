using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-33. The Migration page's four write executors wrote a write's audit row only once its action returned, so a
/// throw out of the action - a sign-in that fails during the write, the Stop exception the pool's borrow rethrows - left no
/// trace of an attempted mutation. Each now files the row in the action's catch: Failed when the service says the write's
/// command was never sent (aud-a76's <see cref="PermissionWriteNotSubmittedException"/>), Undetermined when it may have
/// been (aud-a73's rule), in the cause's own words.
/// </summary>
/// <remarks>
/// The real <see cref="MigrationService"/> over <see cref="MigrationStubExchange"/>: a pool that cannot connect, or one whose
/// first connection reaches the write's command and fails it with Exchange's "must call Connect-ExchangeOnline" pre-check,
/// which the pool retries, and whose retry cannot sign in. The page's own executors over it, and the audit file read back.
/// No Exchange.
/// </remarks>
public sealed class MigrationThrownWriteAuditTests : IDisposable
{
    private const string Batch = "Wave 12";
    private const string Address = "alice@contoso.test";
    private const string Ticket = "INC0042";
    private const string SignIn = "the certificate is not in the store";
    private const string RetrySignIn = "the sign-in for the retry was refused";
    private const string PreCheck = "You must call Connect-ExchangeOnline before calling any other cmdlet.";

    private readonly MigrationStubExchange _exchange = new();

    public void Dispose() => _exchange.Dispose();

    // ---- the service: a write says when nothing was sent ----------------------------------------------------------------

    public static TheoryData<string> Writes =>
    [
        "CompleteMigrationBatchAsync", "ScheduleMigrationBatchCompletionAsync", "CompleteMigrationUserAsync",
        "ApproveMigrationUserAsync", "StopMigrationUserAsync", "ResumeMigrationUserAsync", "RemoveMigrationUserAsync",
        "RemoveMigrationBatchAsync", "StopMigrationBatchAsync", "StartMigrationBatchAsync",
    ];

    private static Task<PermissionResult> Write(MigrationService service, string write) => write switch
    {
        "CompleteMigrationBatchAsync" => service.CompleteMigrationBatchAsync(Batch),
        "ScheduleMigrationBatchCompletionAsync" => service.ScheduleMigrationBatchCompletionAsync(Batch, DateTime.UtcNow.AddDays(1)),
        "CompleteMigrationUserAsync" => service.CompleteMigrationUserAsync(Address),
        "ApproveMigrationUserAsync" => service.ApproveMigrationUserAsync(Address),
        "StopMigrationUserAsync" => service.StopMigrationUserAsync(Address),
        "ResumeMigrationUserAsync" => service.ResumeMigrationUserAsync(Address),
        "RemoveMigrationUserAsync" => service.RemoveMigrationUserAsync(Address),
        "RemoveMigrationBatchAsync" => service.RemoveMigrationBatchAsync(Batch),
        "StopMigrationBatchAsync" => service.StopMigrationBatchAsync(Batch),
        _ => service.StartMigrationBatchAsync(Batch),
    };

    // The borrow - its connect - is what a write lets out before its command: said so, with the cause inside, in its words.
    [Theory]
    [MemberData(nameof(Writes))]
    public async Task AWriteWhoseSignInFails_SaysNothingWasSent(string write)
    {
        var failure = StopFailure(SignIn);
        _exchange.ConnectFailure = failure;

        var notSent = await Assert.ThrowsAsync<PermissionWriteNotSubmittedException>(() => Write(_exchange.Service(connects: false), write));

        Assert.Same(failure, notSent.InnerException);
        Assert.Equal(failure.Message, notSent.Message);
        Assert.Empty(_exchange.CallList());
    }

    // A write whose first attempt reached its command is never marked as not sent, whatever its retry's sign-in does: the
    // pool read the failure as Exchange's pre-check, which says the command did not run, and "nothing was written" does not
    // rest on that reading (aud-a76's rule).
    [Fact]
    public async Task AWriteWhoseCommandWasSent_AndWhoseRetryCannotSignIn_GoesOutAsItIs()
    {
        var failure = StopFailure(RetrySignIn);
        _exchange.ConnectFailure = failure;
        _exchange.ConnectsAllowed = 1;
        _exchange.Failures["Stop-MigrationBatch"] = PreCheck;

        var thrown = await Assert.ThrowsAnyAsync<Exception>(() => _exchange.Service().StopMigrationBatchAsync(Batch));

        Assert.Same(failure, thrown);
        Assert.Equal([$"Stop-MigrationBatch {Batch}"], _exchange.CallList());
    }

    // ---- the page: each executor audits a write that threw ---------------------------------------------------------------

    public static TheoryData<string> Executors =>
        ["ExecuteBatchAction", "ExecuteUserAction", "ExecuteBulkBatchAction", "ExecuteBulkMailboxAction"];

    private static bool OnABatch(string handler) => handler is "ExecuteBatchAction" or "ExecuteBulkBatchAction";

    private async Task Run(string handler, MigrationService service, params string[] batches)
    {
        var page = _exchange.Page(service);
        Set(page, "Progress", new RecordingProgress());
        switch (handler)
        {
            case "ExecuteBatchAction":
                await Call(page, handler, (Func<Task<PermissionResult>>)(() => service.StopMigrationBatchAsync(Batch)), "StopMigrationBatch", Batch, Ticket);
                break;
            case "ExecuteUserAction":
                await Call(page, handler, (Func<Task<PermissionResult>>)(() => service.StopMigrationUserAsync(Address)), "StopMigrationUser", Address, Ticket);
                break;
            case "ExecuteBulkBatchAction":
                Field("migrationBatches").SetValue(page, new List<MigrationBatchInfo>());
                await Call(page, handler, (IReadOnlyList<string>)(batches.Length > 0 ? batches : [Batch]), (IReadOnlyList<MigrationBatchSkip>)[],
                    (Func<string, Task<PermissionResult>>)(name => service.StopMigrationBatchAsync(name)),
                    "StopMigrationBatch", "Migration_StopSelected", "Queued stop of", Ticket);
                break;
            default:
                Field("batchUsers").SetValue(page, new List<MigrationUserInfo> { new() { EmailAddress = Address, Status = "Syncing" } });
                await Call(page, handler, new MigrationUserActionPlan([Address], []),
                    (Func<string, Task<PermissionResult>>)(email => service.StopMigrationUserAsync(email)),
                    "StopMigrationUser", "Migration_PauseMailboxes", "Queued pause of", Ticket);
                break;
        }
    }

    private static void AssertTheRow(JsonElement row, string action, string target, string result, string error)
    {
        Assert.Equal(action, row.GetProperty("action").GetString());
        Assert.Equal("MigrationAction", row.GetProperty("category").GetString());
        Assert.Equal(target, row.GetProperty("target").GetString());
        Assert.Equal(result, row.GetProperty("result").GetString());
        Assert.Equal(error, row.GetProperty("error").GetString());
        Assert.Equal(Ticket, row.GetProperty("ticket").GetString());
    }

    // The finding, before the command: a sign-in that fails during a write. Before: no row at all.
    [Theory]
    [MemberData(nameof(Executors))]
    public async Task AWriteWhoseSignInFails_IsAuditedFailed_NothingWritten_InTheCausesOwnWords(string handler)
    {
        _exchange.ConnectFailure = StopFailure(SignIn);

        await Run(handler, _exchange.Service(connects: false));

        var (action, target) = OnABatch(handler) ? ("StopMigrationBatch", Batch) : ("StopMigrationUser", Address);
        AssertTheRow(Assert.Single(_exchange.AuditEvents()), action, target, "Failed",
            $"Nothing was written: the connection to Exchange failed: {SignIn}.");
        Assert.Empty(_exchange.CallList());
    }

    // The finding, after the command may have been sent: the first attempt reached it, the retry could not sign in.
    [Theory]
    [MemberData(nameof(Executors))]
    public async Task AWriteThatThrowsAfterItsCommandMayHaveBeenSent_IsAuditedUndetermined(string handler)
    {
        _exchange.ConnectFailure = StopFailure(RetrySignIn);
        _exchange.ConnectsAllowed = 1;
        var (step, action, target) = OnABatch(handler)
            ? ("Stop-MigrationBatch", "StopMigrationBatch", Batch)
            : ("Stop-MigrationUser", "StopMigrationUser", Address);
        _exchange.Failures[step] = PreCheck;

        await Run(handler, _exchange.Service());

        AssertTheRow(Assert.Single(_exchange.AuditEvents()), action, target, "Undetermined",
            $"Whether the change was made could not be confirmed: {RetrySignIn}.");
        Assert.Equal([$"{step} {target}"], _exchange.CallList());
    }

    // A bulk run that throws part-way: the rows written as the earlier actions returned stay, the action in flight gets its
    // own, and the ones never reached get none.
    [Fact]
    public async Task ABulkRunThatThrowsPartWay_KeepsTheRowsBefore_AuditsTheOneInFlight_AndNoneAfter()
    {
        _exchange.ConnectFailure = StopFailure(RetrySignIn);
        _exchange.ConnectsAllowed = 1;
        _exchange.Failures["Stop-MigrationBatch Wave 13"] = PreCheck;

        await Run("ExecuteBulkBatchAction", _exchange.Service(), Batch, "Wave 13", "Wave 14");

        var rows = _exchange.AuditEvents();
        Assert.Equal(2, rows.Count);
        Assert.Equal((Batch, "Success"), (rows[0].GetProperty("target").GetString(), rows[0].GetProperty("result").GetString()));
        AssertTheRow(rows[1], "StopMigrationBatch", "Wave 13", "Undetermined",
            $"Whether the change was made could not be confirmed: {RetrySignIn}.");
        Assert.Equal([$"Stop-MigrationBatch {Batch}", "Stop-MigrationBatch Wave 13"], _exchange.CallList());
    }
}
