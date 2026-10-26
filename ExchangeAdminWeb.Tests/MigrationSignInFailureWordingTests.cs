using System.Collections;
using System.Text;
using System.Text.Json;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.AspNetCore.Components.Forms;
using NSubstitute;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-34. Nine more of the Migration page's catches formatted a throw's raw <c>ex.Message</c> - kd-15's class. What
/// reaches them is a throw out of a service call, and a sign-in that fails during one - the live connect runs
/// Connect-ExchangeOnline under -ErrorAction Stop and the pool's borrow rethrows what it throws - is PowerShell's
/// ActionPreferenceStopException, whose message puts "The running command stopped because the preference variable ... is
/// set to Stop:" in front of the error. Each site now gives the error in its own words (aud-a23i's helper).
/// </summary>
/// <remarks>
/// The page's own handlers over <see cref="MigrationStubExchange"/>'s service built not to connect, its connection failing
/// with a real Stop failure; a progress channel that records how each activity ended, and the audit file read back. One
/// case per site, by class: the eligibility checks, the creates, the batch-list load, the single actions and the search.
/// No Exchange.
/// </remarks>
public sealed class MigrationSignInFailureWordingTests : IDisposable
{
    private const string Address = "alice@contoso.test";
    private const string Batch = "Wave 12";
    private const string Ticket = "INC0042";
    private const string SignIn = "the certificate is not in the store";

    private readonly MigrationStubExchange _exchange = new();
    private readonly RecordingProgress _progress = new();

    public MigrationSignInFailureWordingTests()
    {
        _exchange.ConnectFailure = StopFailure(SignIn);
        _exchange.Protection = new Hashtable { ["*"] = "clear" }; // read when the service is built: the creates' check passes
    }

    public void Dispose() => _exchange.Dispose();

    public static TheoryData<string> Sites =>
    [
        "the single eligibility check", "the bulk eligibility check",
        "the single create", "the bulk create",
        "the batch-list load",
        "a batch's action", "a mailbox's action",
        "the search",
    ];

    private JsonElement OneRow(string action) =>
        Assert.Single(_exchange.AuditEvents(), row => row.GetProperty("action").GetString() == action);

    // The finding: before, each text read "The running command stopped because the preference variable
    // "ErrorActionPreference" or common parameter is set to Stop: the certificate is not in the store".
    [Theory]
    [MemberData(nameof(Sites))]
    public async Task ASignInThatFailsUnderStop_IsGivenInTheErrorsOwnWords(string site)
    {
        var service = _exchange.Service(connects: false);
        var page = _exchange.Page(service);
        Set(page, "Progress", _progress);
        string shown;
        string? activity;
        var tab = 0;

        switch (site)
        {
            case "the single eligibility check":
                Field("singleEmail").SetValue(page, Address);
                Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToOnPrem);
                await Call(page, "CheckSingleEligibility");
                shown = Assert.Single(Assert.IsType<MigrationEligibilityResult>(Field("singleResult").GetValue(page)).IneligibilityReasons);
                activity = $"Could not be determined: {SignIn}";
                Assert.Equal(activity, shown);
                Assert.Equal(SignIn, OneRow("CheckMigrationEligibility").GetProperty("reasons").GetString());
                break;

            case "the bulk eligibility check":
                var file = Substitute.For<IBrowserFile>();
                file.Name.Returns("wave.csv");
                file.OpenReadStream(Arg.Any<long>(), Arg.Any<CancellationToken>())
                    .Returns(_ => new MemoryStream(Encoding.UTF8.GetBytes($"EmailAddress\n{Address}\n")));
                Field("csvFile").SetValue(page, file);
                Field("migrationDirection").SetValue(page, MigrationDirection.ToOnPrem);
                await Call(page, "CheckBulkEligibility");
                shown = Assert.IsType<MigrationBatchResult>(Field("bulkResult").GetValue(page)).Message!;
                Assert.Equal(SignIn, shown);
                activity = $"The bulk eligibility check failed: {SignIn}";
                Assert.Equal(SignIn, OneRow("BulkEligibilityCheck_ToOnPrem").GetProperty("error").GetString());
                tab = 1;
                break;

            case "the single create":
                Field("singleResult").SetValue(page, new MigrationEligibilityResult { EmailAddress = Address, Status = MigrationStatus.Eligible });
                Field("singleTicketNumber").SetValue(page, Ticket);
                Field("singleMigrationDirection").SetValue(page, MigrationDirection.ToCloud);
                await Call(page, "CreateSingleMigrationBatch");
                shown = Assert.IsType<PermissionResult>(Field("singleCreateResult").GetValue(page)).Message!;
                Assert.Equal(SignIn, shown);
                activity = SignIn;
                break;

            case "the bulk create":
                Field("bulkResult").SetValue(page, new MigrationBatchResult
                {
                    BatchName = Batch,
                    Success = true,
                    Direction = MigrationDirection.ToCloud,
                    TotalUsers = 1,
                    EligibleUsers = 1,
                    EligibilityResults = [new MigrationEligibilityResult { EmailAddress = Address, Status = MigrationStatus.Eligible }],
                });
                Field("bulkTicketNumber").SetValue(page, Ticket);
                Field("migrationDirection").SetValue(page, MigrationDirection.ToCloud);
                await Call(page, "CreateMigrationBatch");
                shown = Assert.IsType<PermissionResult>(Field("createResult").GetValue(page)).Message!;
                Assert.Equal(SignIn, shown);
                activity = SignIn;
                tab = 1;
                break;

            case "the batch-list load":
                await Call(page, "LoadBatchList");
                shown = (string)Field("batchListFailure").GetValue(page)!;
                Assert.Equal(SignIn, shown);
                activity = null;
                tab = 2;
                Assert.Contains($"The migration batches could not be read: {SignIn}", page.RenderText(), StringComparison.Ordinal);
                break;

            case "a batch's action":
                await Call(page, "ExecuteBatchAction", (Func<Task<PermissionResult>>)(() => service.StopMigrationBatchAsync(Batch)), "StopMigrationBatch", Batch, Ticket);
                shown = Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page)).Message!;
                Assert.Equal(SignIn, shown);
                activity = SignIn;
                tab = 2;
                break;

            case "a mailbox's action":
                await Call(page, "ExecuteUserAction", (Func<Task<PermissionResult>>)(() => service.StopMigrationUserAsync(Address)), "StopMigrationUser", Address, Ticket);
                shown = Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page)).Message!;
                Assert.Equal(SignIn, shown);
                activity = SignIn;
                tab = 2;
                break;

            default:
                Field("migrationBatches").SetValue(page, new List<MigrationBatchInfo>());
                Field("searchQuery").SetValue(page, "zed@contoso.test");
                await Call(page, "SearchUser");
                shown = Assert.IsType<PermissionResult>(Field("batchActionResult").GetValue(page)).Message!;
                Assert.Equal($"Search failed: {SignIn}", shown);
                activity = shown;
                tab = 2;
                break;
        }

        if (activity is not null)
            Assert.Equal((false, activity), Assert.Single(_progress.Handles).Outcome);
        Assert.DoesNotContain("The running command stopped", shown, StringComparison.Ordinal);
        Assert.DoesNotContain(_exchange.AuditEvents(), row => row.ToString().Contains("The running command stopped", StringComparison.Ordinal));
        Field("tabIndex").SetValue(page, tab);
        Assert.DoesNotContain("The running command stopped", page.RenderText(), StringComparison.Ordinal);
    }
}
