using System.Net;
using ExchangeAdminWeb.Services;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// sweep-d item 29. A reset used to build a new Graph client - one Secret Server read and one token - for each of the
/// preflight read, the confirm's re-read, the employee-ID save and the password write: four vault reads and four
/// tokens, about 11-16 s. And the employee-ID save's administrator alert went out between the save and the password
/// write, before anyone knew what became of the reset. Driven through the page's compiled handlers over the real service,
/// with the service building and keeping its own client from a counted Secret Server read.
/// </summary>
public class CloudPasswordResetOneCredentialTests
{
    private const string DomainA = "one.example.test";
    private const string DomainB = "two.example.test";
    private const string Number = "0001234";
    private const string SaveAlert = "Cloud password reset: employee ID";
    private const string ResetAlert = "Cloud password reset";

    private static StandInForest Forest()
    {
        var forest = new StandInForest(DomainA, DomainB);
        forest.Add(DomainB, "jdoe", Number, "Jo.Doe@Example.Test", "Jo Doe");
        return forest;
    }

    [Fact]
    public void A_preflight_and_its_confirm_read_the_vault_once_and_take_one_token()
    {
        using var run = new CloudPasswordResetPageRun(Forest(), throughVault: true);

        run.Preflight();
        run.Confirm();

        Assert.True(run.ResultSuccess, run.ResultMessage);
        // The preflight read, the confirm's re-read, the employee-ID save and the password write.
        Assert.Equal(2, run.Writes.Count);
        Assert.Equal(1, run.Backend.VaultReads);
        Assert.Equal(1, run.Backend.Graph.TokenRequests);
    }

    [Fact]
    public void An_operation_more_than_five_minutes_after_the_read_reads_the_vault_again()
    {
        var clock = new ManualClock();
        using var run = new CloudPasswordResetPageRun(Forest(), throughVault: true, clock: clock);

        run.Preflight();
        clock.Advance(CloudPasswordResetServiceWindow + TimeSpan.FromSeconds(1));
        run.Confirm();

        Assert.True(run.ResultSuccess, run.ResultMessage);
        Assert.Equal(2, run.Backend.VaultReads);
        Assert.Equal(2, run.Backend.Graph.TokenRequests);
    }

    [Fact]
    public void The_window_is_measured_from_the_read_not_from_the_last_use()
    {
        // A rotated secret must be picked up within five minutes however busy the module is.
        var clock = new ManualClock();
        using var run = new CloudPasswordResetPageRun(Forest(), throughVault: true, clock: clock);

        run.Preflight();
        clock.Advance(TimeSpan.FromMinutes(3));
        run.Preflight();
        clock.Advance(TimeSpan.FromMinutes(3));
        run.Preflight();

        Assert.Equal(2, run.Backend.VaultReads);
    }

    [Fact]
    public void The_save_alert_is_sent_after_the_password_write_and_after_the_resets_own_alert()
    {
        using var run = new CloudPasswordResetPageRun(Forest(), throughVault: true);
        var order = Record(run);

        run.Preflight();
        run.Confirm();

        Assert.True(run.ResultSuccess, run.ResultMessage);
        Assert.Equal(["write:employeeId", "write:password", "alert:" + ResetAlert, "alert:" + SaveAlert], order.ToArray());

        // What the alert says is unchanged: the save's outcome, to the administrators, once.
        var (success, details) = Assert.Single(run.AdminAlerts(SaveAlert));
        Assert.True(success);
        Assert.Equal(nameof(EmployeeIdFillOutcome.Written), details["Outcome"]);
        Assert.Equal(Number, details["Employee ID"]);
    }

    [Fact]
    public void The_save_alert_is_still_sent_when_the_password_write_is_refused_and_still_after_it()
    {
        using var run = new CloudPasswordResetPageRun(Forest(), throughVault: true);
        var order = Record(run, passwordAnswer: () => new HttpResponseMessage(HttpStatusCode.Forbidden));

        run.Preflight();
        run.Confirm();

        Assert.False(run.ResultSuccess);
        Assert.Equal("write:password", order[1]);
        Assert.Equal("alert:" + SaveAlert, order[^1]);
        Assert.Single(run.AdminAlerts(SaveAlert));
    }

    private static readonly TimeSpan CloudPasswordResetServiceWindow = DelineaService.DefaultCredentialWindow;

    private static List<string> Record(CloudPasswordResetPageRun run, Func<HttpResponseMessage>? passwordAnswer = null)
    {
        var order = new List<string>();
        var answer = run.Backend.Graph.Respond;
        run.Backend.Graph.Respond = request =>
        {
            if (request.Method == HttpMethod.Patch)
            {
                var isPassword = request.Body?.Contains("passwordProfile", StringComparison.Ordinal) == true;
                lock (order) order.Add(isPassword ? "write:password" : "write:employeeId");
                if (isPassword && passwordAnswer is not null)
                    return passwordAnswer();
            }

            return answer(request);
        };

        run.Email.When(email => email.SendAdminNotificationAsync(
                Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<string?>()))
            .Do(call => { lock (order) order.Add("alert:" + call.ArgAt<string>(2)); });
        return order;
    }

    /// <summary>A clock that moves only when the test says.</summary>
    private sealed class ManualClock : TimeProvider
    {
        private long _ticks = 1_000_000;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Interlocked.Read(ref _ticks);

        public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);
    }
}
