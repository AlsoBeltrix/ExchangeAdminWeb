using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a28w: nothing in the app opened a PowerShell runspace before the first operator's session, so after a start the first
/// sessions of two operators could open at once - the shape the suite's start-up guard closes (aud-a26p). The app now
/// starts PowerShell once, from its startup pass, in the background: <see cref="PowerShellWarmUp"/>.
/// </summary>
/// <remarks>
/// The stand-in cases replace the open and never start PowerShell; one case runs the app's own warm-up, which opens one
/// <c>CreateDefault</c> runspace in the test host - no module, no command, no connection. The last two are the folded-in
/// finding: the suite's fixture swallowed the failure it exists to report.
/// </remarks>
public sealed class PowerShellWarmUpTests
{
    private static readonly IReadOnlyList<(string Name, Func<InitialSessionState> Create)> StandIn =
        [("Stand-in", InitialSessionState.Create)];

    private sealed class RecordingLogger : ILogger
    {
        private readonly List<(LogLevel Level, string Message, Exception? Exception)> _lines = [];

        public IReadOnlyList<(LogLevel Level, string Message, Exception? Exception)> Lines
        {
            get { lock (_lines) return _lines.ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_lines)
                _lines.Add((logLevel, formatter(state, exception), exception));
        }
    }

    [Fact]
    public void Program_StartsTheWarmUp_AfterTheHostIsBuilt_AndBeforeItRuns()
    {
        // Before app.Run() is before the server takes a request; after Build() is where the container exists.
        var program = RazorSyntax.CodeView(File.ReadAllText(AuditCategoryFilingTests.FindRepoFile("Program.cs")));
        var built = program.IndexOf("var app = builder.Build();", StringComparison.Ordinal);
        var run = program.IndexOf("app.Run();", StringComparison.Ordinal);
        Assert.True(built > 0 && run > built, "Program.cs no longer builds the host and then runs it - this test is stale");

        var warmUp = program.IndexOf("PowerShellWarmUp.StartInBackground(", StringComparison.Ordinal);
        Assert.True(warmUp > built && warmUp < run,
            "Program.cs does not start the PowerShell warm-up between building the host and running it (aud-a28w)");
    }

    [Fact]
    public async Task TheAppsWarmUp_OpensADefaultSession_AndLogsOneInformationLine()
    {
        var log = new RecordingLogger();

        var outcome = await PowerShellWarmUp.StartInBackground(log)
            .WaitAsync(TimeSpan.FromMinutes(2), TestContext.Current.CancellationToken);

        Assert.True(outcome.Started, $"the warm-up did not start PowerShell: {outcome.FailedShape}: {outcome.LastFailure}");
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
        Assert.Contains("(CreateDefault, attempt ", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task StartInBackground_ReturnsWhileTheWarmUpsOpenIsStillHeld()
    {
        var cancel = TestContext.Current.CancellationToken;
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        Task<PowerShellStartOutcome>? warmUp = null;
        try
        {
            var starting = Task.Run(() =>
            {
                warmUp = PowerShellWarmUp.StartInBackground(new RecordingLogger(), StandIn, _ =>
                {
                    entered.Set();
                    release.Wait();
                });
            }, cancel);

            // The call itself comes back at once - start-up is not held - while its open is still held.
            Assert.Same(starting, await Task.WhenAny(starting, Task.Delay(TimeSpan.FromSeconds(10), cancel)));
            Assert.True(entered.Wait(TimeSpan.FromSeconds(10), cancel), "the warm-up's open never began");
            Assert.False(warmUp!.IsCompleted, "the warm-up finished while its open was still held");
        }
        finally
        {
            release.Set();
        }

        var outcome = await warmUp!.WaitAsync(TimeSpan.FromSeconds(10), cancel);
        Assert.True(outcome.Started);
    }

    [Fact]
    public async Task AWarmUpThatAlwaysFails_IsTriedThreeTimes_NeverThrown_AndLogsOneWarningWithTheFailure()
    {
        var log = new RecordingLogger();
        var tries = 0;
        var failure = new InvalidOperationException("An item with the same key has already been added. Key: Alias");

        var outcome = await PowerShellWarmUp.StartInBackground(log, StandIn, _ =>
            {
                Interlocked.Increment(ref tries);
                throw failure;
            })
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.False(outcome.Started);
        Assert.Equal(3, tries);
        Assert.Equal(3, outcome.Attempts);
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Warning, line.Level);
        Assert.Same(failure, line.Exception);
        Assert.Contains("Stand-in", line.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AWarmUpThatFailsOnce_IsRetried_AndLogsOneInformationLine()
    {
        var log = new RecordingLogger();
        var tries = 0;

        var outcome = await PowerShellWarmUp.StartInBackground(log, StandIn, _ =>
            {
                if (Interlocked.Increment(ref tries) == 1)
                    throw new InvalidOperationException("the first start");
            })
            .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        Assert.True(outcome.Started);
        Assert.Equal(2, outcome.Attempts);
        var line = Assert.Single(log.Lines);
        Assert.Equal(LogLevel.Information, line.Level);
    }

    [Fact]
    public void Start_KeepsTheLastFailure_AndTheShapeThatFailed()
    {
        var failure = new InvalidOperationException("the second shape failed");
        var opens = 0;

        var outcome = PowerShellWarmUp.Start(
            [("First", InitialSessionState.Create), ("Second", InitialSessionState.Create)],
            attempts: 2,
            _ =>
            {
                if (++opens % 2 == 0)
                    throw failure;
            });

        Assert.False(outcome.Started);
        Assert.Equal(2, outcome.Attempts);
        Assert.Equal("Second", outcome.FailedShape);
        Assert.Same(failure, outcome.LastFailure);
    }

    [Fact]
    public void ThePinsMessage_NamesTheShapeThatFailed_AndTheFailure()
    {
        var failure = new InvalidOperationException("An item with the same key has already been added. Key: Alias");

        var message = PowerShellStartupTests.NotStartedMessage(new PowerShellStartOutcome(false, 3, "CreateDefault2", failure));

        Assert.Contains("CreateDefault2", message, StringComparison.Ordinal);
        Assert.Contains(nameof(InvalidOperationException), message, StringComparison.Ordinal);
        Assert.Contains(failure.Message, message, StringComparison.Ordinal);
        Assert.Contains("not registered", PowerShellStartupTests.NotStartedMessage(null), StringComparison.Ordinal);
    }
}
