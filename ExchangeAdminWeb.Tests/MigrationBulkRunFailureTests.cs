using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-15. Both bulk executors' outer catch showed raw <c>ex.Message</c>. What reaches it is a throw out of an action:
/// <c>RunAsync</c> catches what its delegate throws but not the pool's borrow, and the live connect runs
/// Connect-ExchangeOnline under -ErrorAction Stop and rethrows what it throws, so a sign-in that fails during a bulk run
/// arrived as PowerShell's ActionPreferenceStopException - the banner and the activity read "The running command stopped
/// because the preference variable ... is set to Stop: " in front of the error.
/// </summary>
/// <remarks>
/// The page's own executors over <see cref="MigrationStubExchange"/>'s service, its connection failing with a real Stop
/// failure (<see cref="MigrationStubExchange.ConnectFailure"/>, <see cref="MigrationStubExchange.StopFailure"/>), and a
/// progress channel that records how each activity ended. No Exchange.
/// </remarks>
public sealed class MigrationBulkRunFailureTests : IDisposable
{
    private const string Batch = "Wave 12";
    private const string Address = "alice@contoso.test";
    private const string Ticket = "INC0042";
    private const string Failure = "the certificate is not in the store";

    private readonly MigrationStubExchange _exchange = new();
    private readonly RecordingProgress _progress = new();

    public void Dispose() => _exchange.Dispose();

    [Theory]
    [InlineData("ExecuteBulkBatchAction")]
    [InlineData("ExecuteBulkMailboxAction")]
    public async Task ABulkRunWhoseSignInFailsUnderStop_IsShownInTheErrorsOwnWords(string handler)
    {
        _exchange.ConnectFailure = StopFailure(Failure);
        var service = _exchange.Service(connects: false);
        var page = _exchange.Page(service);
        Set(page, "Progress", _progress);

        if (handler == "ExecuteBulkBatchAction")
        {
            Field("migrationBatches").SetValue(page, new List<MigrationBatchInfo>());
            Func<string, Task<PermissionResult>> stop = name => service.StopMigrationBatchAsync(name);
            await Call(page, handler, (IReadOnlyList<string>)[Batch], (IReadOnlyList<MigrationBatchSkip>)[], stop,
                "StopMigrationBatch", "Migration_StopSelected", "Queued stop of", Ticket);
        }
        else
        {
            Field("batchUsers").SetValue(page, new List<MigrationUserInfo> { new() { EmailAddress = Address, Status = "Syncing" } });
            Func<string, Task<PermissionResult>> pause = email => service.StopMigrationUserAsync(email);
            await Call(page, handler, new MigrationUserActionPlan([Address], []), pause,
                "StopMigrationUser", "Migration_PauseMailboxes", "Queued pause of", Ticket);
        }

        var shown = Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page));
        Assert.Equal((false, Failure), (shown.Success, shown.Message));
        Assert.Equal((false, Failure), Assert.Single(_progress.Handles).Outcome);

        Field("tabIndex").SetValue(page, 2);
        Assert.DoesNotContain("The running command stopped", page.RenderText(), StringComparison.Ordinal);
    }
}
