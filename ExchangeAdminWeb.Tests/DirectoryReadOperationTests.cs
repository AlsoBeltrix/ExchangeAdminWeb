using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// kd-88: one operation's directory-read credential, fetched at most once and handed to every check inside it. The dev log of
/// the grant that waited two minutes shows the secret fetched three times for the one grant.
/// </summary>
public sealed class DirectoryReadOperationTests
{
    private int _fetches;

    private Task<(string username, string password, string domain)?> Vault(int secretId)
    {
        Interlocked.Increment(ref _fetches);
        return Task.FromResult<(string username, string password, string domain)?>(("svc-dirread", "secret-" + secretId, "CONTOSO"));
    }

    [Fact]
    public async Task OutsideAnOperation_EveryCheckFetchesForItself()
    {
        Assert.False(DirectoryReadOperation.InProgress);

        await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);
        await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);

        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task InsideAnOperation_TheSecretIsFetchedOnce_AndHandedToEveryCheck()
    {
        using (DirectoryReadOperation.Begin())
        {
            var first = await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);
            var second = await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);
            var third = await Task.Run(() => DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault), TestContext.Current.CancellationToken);

            Assert.Equal(first, second);
            Assert.Equal(first, third);
        }

        Assert.Equal(1, _fetches);
    }

    [Fact]
    public async Task AnotherSecretId_IsItsOwnFetch()
    {
        using (DirectoryReadOperation.Begin())
        {
            await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);
            await DirectoryReadOperation.DirectoryReadSecretAsync(4343, Vault);
            await DirectoryReadOperation.DirectoryReadSecretAsync(4343, Vault);
        }

        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task AFetchThatFailed_IsThatFailureForEveryLaterCheck_AndIsNotRepeated()
    {
        var calls = 0;
        Task<(string username, string password, string domain)?> Nothing(int _)
        {
            calls++;
            return Task.FromResult<(string username, string password, string domain)?>(null);
        }

        Task<(string username, string password, string domain)?> Throws(int _)
        {
            calls++;
            return Task.FromException<(string username, string password, string domain)?>(new InvalidOperationException("the secret store is unreachable"));
        }

        using (DirectoryReadOperation.Begin())
        {
            Assert.Null(await DirectoryReadOperation.DirectoryReadSecretAsync(1, Nothing));
            Assert.Null(await DirectoryReadOperation.DirectoryReadSecretAsync(1, Vault));

            await Assert.ThrowsAsync<InvalidOperationException>(() => DirectoryReadOperation.DirectoryReadSecretAsync(2, Throws));
            await Assert.ThrowsAsync<InvalidOperationException>(() => DirectoryReadOperation.DirectoryReadSecretAsync(2, Vault));
        }

        Assert.Equal(2, calls);
        Assert.Equal(0, _fetches);
    }

    [Fact]
    public async Task AnOperationBegunInsideAnother_JoinsIt_AndOnlyTheOutermostEndsIt()
    {
        using (DirectoryReadOperation.Begin())
        {
            using (DirectoryReadOperation.Begin())
                await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);

            Assert.True(DirectoryReadOperation.InProgress);
            await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);
        }

        Assert.False(DirectoryReadOperation.InProgress);
        Assert.Equal(1, _fetches);
    }

    [Fact]
    public async Task NothingOutlivesTheOperation_TheNextOneFetchesAfresh()
    {
        using (DirectoryReadOperation.Begin())
            await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);
        using (DirectoryReadOperation.Begin())
            await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);

        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task TwoOperationsRunAtOnce_DoNotShare()
    {
        // Two operators' changes run in their own flows: each operation is its own.
        async Task OneChange()
        {
            using var operation = DirectoryReadOperation.Begin();
            await Task.Yield();
            await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);
            await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);
        }

        await Task.WhenAll(Task.Run(OneChange, TestContext.Current.CancellationToken), Task.Run(OneChange, TestContext.Current.CancellationToken));

        Assert.Equal(2, _fetches);
    }

    [Fact]
    public async Task ACheck_LogsOneLine_WithTheReadsAndFetchesMadeWhileItRan_ItsNestedChecksIncluded()
    {
        var log = new Lines();
        using (DirectoryReadOperation.BeginCheck(log, "Protected-principal check", "tess@contoso.test"))
        {
            DirectoryReadOperation.CountRead();
            await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);
            using (DirectoryReadOperation.BeginCheck(log, "Protected-principal check", "the service's own check"))
                DirectoryReadOperation.CountRead();
            using (DirectoryReadOperation.BeginCheck(log, "Self-grant check", "other@contoso.test"))
            {
                DirectoryReadOperation.CountRead();
                await DirectoryReadOperation.DirectoryReadSecretAsync(4242, Vault);
            }
        }

        // The inner check of the same name is part of the outer one; the self-grant check is its own, and shared the fetch.
        Assert.Equal(2, log.Count);
        Assert.Matches(@"^Self-grant check for other@contoso\.test took \d+ ms \(1 directory reads, 0 credential fetches\)$", log[0]);
        Assert.Matches(@"^Protected-principal check for tess@contoso\.test took \d+ ms \(3 directory reads, 1 credential fetches\)$", log[1]);
        Assert.Equal(1, _fetches);
    }

    [Fact]
    public void ReadsOutsideAnyCheck_AreCountedNowhere()
    {
        var log = new Lines();
        DirectoryReadOperation.CountRead();
        using (DirectoryReadOperation.BeginCheck(log, "Protected-principal check", null))
        {
        }

        Assert.Matches(@"^Protected-principal check for \(no target\) took \d+ ms \(0 directory reads, 0 credential fetches\)$", Assert.Single(log));
    }

    private sealed class Lines : List<string>, Microsoft.Extensions.Logging.ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Information)
                Add(formatter(state, exception));
        }
    }

    [Fact]
    public async Task AnOperationEndsWithTheFlowThatBeganIt()
    {
        // Begun inside an async method, it does not leak out to the caller when that method returns.
        async Task Inner()
        {
            DirectoryReadOperation.Begin();
            await Task.Yield();
            Assert.True(DirectoryReadOperation.InProgress);
        }

        await Inner();

        Assert.False(DirectoryReadOperation.InProgress);
    }
}
