using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Finding kd-58, slice A2 of docs/CancellationDepth-Plan.md: the Exchange base's cloud side takes the caller's cancel. A
/// pooled read (<c>RunPooledReadAsync</c>) puts the caller's token on its tracker, and the base's command helpers look at it
/// before each command they send, so a cancelled read ends between its commands with its pipeline cleared and its connection
/// fit for the next caller (E3). A call that may write (<c>RunAsync</c>, <c>RunPooledQueryAsync</c>) gives its token to the
/// pool's borrow only, never to its tracker, so nothing after its first write is cancellable (E4). The shared location read's
/// Exchange Online half takes the token through it.
/// </summary>
/// <remarks>
/// The real pool over <see cref="ExchangeWriteHarness"/>'s settings and trace; its connect seam opens a stand-in runspace per
/// connection whose functions log each call by connection. Every wait bounded. No Exchange, no tenant.
/// </remarks>
public sealed class ExchangeServiceBaseReadCancellationTests : IDisposable
{
    private const int PoolConnections = 5;
    private const string Address = "user@example.test";

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(60);

    /// <summary>"At once": far inside the pool's two-minute wait for a connection.</summary>
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(10);

    private readonly ExchangeWriteHarness _h = new();
    private readonly List<ExoConnectionPool> _pools = [];
    private readonly ArrayList _calls = ArrayList.Synchronized(new ArrayList());
    private readonly ArrayList _disconnects = ArrayList.Synchronized(new ArrayList());
    private int _connects;

    public void Dispose()
    {
        foreach (var pool in _pools)
            pool.Dispose();
        _h.Dispose();
    }

    private string[] Calls() => _calls.Cast<string>().ToArray();

    private int Connects => Volatile.Read(ref _connects);

    /// <summary>The base's four command helpers, each of which looks at a read's token before it sends its command.</summary>
    public static TheoryData<string> Helpers => ["Invoke", "InvokeOptional", "InvokeBestEffort", "ReadUnlessNotFound"];

    // ---- E3: a read ends between its commands --------------------------------------------------------------------------

    // The read's first command runs, the caller cancels, and the read reaches its second: the helper sends nothing, clears the
    // pipeline - the command just added, and the warning the first left in its stream - and throws the caller's cancellation.
    // The pool takes the connection back, and the next read is served on the same runspace and gets its own result only.
    [Theory]
    [MemberData(nameof(Helpers))]
    public async Task AReadCancelledBetweenItsCommands_SendsNoMore_AndItsConnectionServesTheNextRead(string helper)
    {
        var pool = StandInPool();
        var reads = new Reads(pool);
        using var cancel = new CancellationTokenSource();

        var thrown = await Record.ExceptionAsync(() => reads.TwoCommands(helper, cancel).WaitAsync(Bound, TestContext.Current.CancellationToken));

        var cancellation = Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Equal(cancel.Token, cancellation.CancellationToken);
        Assert.Equal(["conn-1|Get-StandInFirst"], Calls()); // the second command never ran
        Assert.Equal((0, 0), reads.PipelineAtTheThrow); // commands and streams cleared before the throw
        Assert.Equal(1, pool.AvailableCount); // given back, not discarded as dead
        Assert.Empty(_disconnects);

        var next = await reads.OneCommand("Get-StandInSecond", TestContext.Current.CancellationToken);

        Assert.Equal("Get-StandInSecond-conn-1", next); // the same runspace, and its own answer only
        Assert.Equal(1, Connects);
        Assert.Equal((0, 0, 0), reads.PipelineAtTheStart);
    }

    // Cancelled before the read's first command: nothing is sent at all.
    [Theory]
    [MemberData(nameof(Helpers))]
    public async Task AReadCancelledBeforeItsFirstCommand_SendsNothing(string helper)
    {
        var pool = StandInPool();
        var reads = new Reads(pool);
        using var cancel = new CancellationTokenSource();

        var thrown = await Record.ExceptionAsync(() => reads.CancelledBeforeTheFirst(helper, cancel).WaitAsync(Bound, TestContext.Current.CancellationToken));

        Assert.IsAssignableFrom<OperationCanceledException>(thrown);
        Assert.Empty(Calls());
        Assert.Equal(1, pool.AvailableCount);
    }

    // ---- E4: nothing after a first write is cancellable ------------------------------------------------------------------

    // A call that may write, given a token, is cancelled once it has started: its tracker carries no token, so the write and
    // the command after it are sent and its own result comes back - the proof that nothing after a first write is cancellable.
    [Theory]
    [InlineData("RunPooledQueryAsync")]
    [InlineData("RunAsync")]
    public async Task AWriteCancelledOnceItHasStarted_RunsToItsEnd_AndKeepsItsOwnResult(string helper)
    {
        var pool = StandInPool();
        var writes = new Reads(pool);
        using var cancel = new CancellationTokenSource();

        var result = await writes.CancelledAsTheWriteStarts(helper, cancel).WaitAsync(Bound, TestContext.Current.CancellationToken);

        Assert.True(cancel.IsCancellationRequested);
        Assert.Equal(["conn-1|Set-StandInWrite", "conn-1|Get-StandInFirst"], Calls());
        Assert.Equal("written", result);
    }

    // E1 through the base: every connection held elsewhere, a call waits for one, and its caller cancels - it ends at once and
    // runs nothing. The write helpers' token reaches the borrow (E4), and the read helper's too (E3).
    [Theory]
    [InlineData("RunPooledQueryAsync")]
    [InlineData("RunAsync")]
    [InlineData("RunPooledReadAsync")]
    public async Task ACallWaitingForAPoolConnection_EndsAtOnce_WhenItsCallerCancels(string helper)
    {
        var pool = StandInPool();
        var calls = new Reads(pool);
        var held = await HoldEveryConnection(pool);
        using var cancel = new CancellationTokenSource();
        var call = calls.WaitingCall(helper, cancel.Token);
        try
        {
            await WaitUntil(() => BorrowsWaitingForASlot() == 1, "the call waits for a pool connection");
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(Promptly, TestContext.Current.CancellationToken));
        }
        finally
        {
            ReturnAll(pool, held);
            await Record.ExceptionAsync(() => call.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.Empty(Calls());
    }

    // ---- the shared location read's Exchange Online half ---------------------------------------------------------------

    // Both forms of the shared location read - by address, and by the validated mailbox the permission pages pass - hand the
    // caller's token to the Exchange Online read: a cancel while it waits for a pool connection ends it at once, nothing
    // asked of Exchange. Before, it waited for a connection and read, and only then did the caller see the cancel.
    [Theory]
    [InlineData("by address")]
    [InlineData("by the validated mailbox")]
    public async Task TheLocationReadsExchangeOnlineHalf_EndsAtOnce_WhenItsCallerCancelsWhileItWaits(string form)
    {
        var pool = StandInPool();
        var reads = new Reads(pool);
        var held = await HoldEveryConnection(pool);
        using var cancel = new CancellationTokenSource();
        var read = form == "by address"
            ? reads.GetMailboxLocationAsync(Address, cancel.Token)
            : reads.GetMailboxLocationAsync(ExchangeWriteHarness.Validated(Guid.Parse("0e0e0e0e-1111-4222-8333-444444444444")), cancel.Token);
        try
        {
            await WaitUntil(() => BorrowsWaitingForASlot() == 1, "the location read waits for a pool connection");
            await cancel.CancelAsync();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => read.WaitAsync(Promptly, TestContext.Current.CancellationToken));
        }
        finally
        {
            ReturnAll(pool, held);
            await Record.ExceptionAsync(() => read.WaitAsync(Bound, TestContext.Current.CancellationToken));
        }

        Assert.Empty(Calls());
    }

    // The control: with no token - every caller of the location read but the permission pages - it reads as before.
    [Fact]
    public async Task TheLocationRead_WithNoToken_ReadsAsBefore()
    {
        var reads = new Reads(StandInPool());

        var location = await reads.GetMailboxLocationAsync(Address, CancellationToken.None);

        Assert.Equal("Cloud", location);
        Assert.Equal([$"conn-1|Get-Mailbox {Address}"], Calls());
    }

    // ---- E3's limit: the read helper is for reads only -------------------------------------------------------------------

    // The read helper ends a call between its commands, which is safe only for a call that writes nothing. So every call site
    // in Services hands it a lambda whose commands are all Get- commands, added by name and sent through the base's helpers -
    // no script, no raw Invoke. A call that may write uses RunAsync or RunPooledQueryAsync, whose token reaches the borrow only.
    [Fact]
    public void EveryPooledReadCallSite_SendsGetCommandsOnly_ThroughTheBasesHelpers()
    {
        var root = Path.GetDirectoryName(AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.csproj"))!;
        var sites = new List<string>();
        var offences = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "Services"), "*.cs", SearchOption.AllDirectories))
        {
            var source = WithoutComments(File.ReadAllText(file));
            foreach (Match call in ReadHelperCall.Matches(source))
            {
                var relative = Path.GetRelativePath(root, file);
                var line = source[..call.Index].Count(c => c == '\n') + 1;
                var site = $"{relative}:{line}";
                sites.Add(site);
                var arguments = ArgumentsOf(source, call.Index + call.Length - 1);
                if (!Regex.IsMatch(arguments, @"^\s*\(\s*\w+\s*,\s*\w+\s*\)\s*=>"))
                {
                    offences.Add($"{site}: the read is not a lambda the tripwire can read");
                    continue;
                }
                foreach (Match added in Regex.Matches(arguments, @"\.AddCommand\(\s*(?<name>[^,)]*)"))
                {
                    if (!Regex.IsMatch(added.Groups["name"].Value, "^\"Get-[A-Za-z]+\"$"))
                        offences.Add($"{site}: adds {added.Groups["name"].Value}, not a Get- command by name");
                }
                foreach (Match forbidden in Regex.Matches(arguments, @"\.(AddScript|AddStatement|Invoke|BeginInvoke|InvokeAsync)\s*[(<]"))
                    offences.Add($"{site}: calls {forbidden.Groups[1].Value} itself, not through the base's helpers");
            }
        }

        Assert.True(offences.Count == 0, string.Join(" | ", offences));
        // The tripwire is not vacuous: the shared location read's Exchange Online half and Recipient Lookup's read are on it.
        Assert.Contains(sites, site => site.StartsWith(Path.Combine("Services", "ExchangeServiceBase.cs"), StringComparison.Ordinal));
        Assert.Contains(sites, site => site.StartsWith(Path.Combine("Services", "RecipientLookupService.cs"), StringComparison.Ordinal));
    }

    // A call of the read helper, not its definition: "RunPooledReadAsync(" or "RunPooledReadAsync<T>(", not after "Task<T> ".
    private static readonly Regex ReadHelperCall = new(@"(?<!Task<T>\s)\bRunPooledReadAsync(<[^>]*>)?\(", RegexOptions.CultureInvariant);

    // The text between the call's parentheses, in source already stripped of comments: string and character literals
    // skipped, so a parenthesis inside one is not counted; an interpolated string's holes are skipped whole with it, which is
    // enough for the reads this scans.
    private static string ArgumentsOf(string source, int open)
    {
        var depth = 0;
        for (var i = open; i < source.Length; i++)
        {
            var c = source[i];
            if (c is '"' or '\'')
            {
                i = EndOfLiteral(source, i);
                continue;
            }
            if (c == '(')
                depth++;
            else if (c == ')' && --depth == 0)
                return source[(open + 1)..i];
        }
        throw new InvalidOperationException("The read helper's call has no closing parenthesis.");
    }

    // The source with every comment blanked - each character a space, its line breaks kept, so line numbers stand - and its
    // string and character literals left as they are.
    private static string WithoutComments(string source)
    {
        var text = source.ToCharArray();
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] is '"' or '\'')
            {
                i = EndOfLiteral(source, i);
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '/')
            {
                for (; i < text.Length && text[i] != '\n'; i++)
                    text[i] = ' ';
            }
            else if (text[i] == '/' && i + 1 < text.Length && text[i + 1] == '*')
            {
                for (; i < text.Length && !(text[i] == '*' && i + 1 < text.Length && text[i + 1] == '/'); i++)
                {
                    if (text[i] != '\n')
                        text[i] = ' ';
                }
                if (i + 1 < text.Length)
                    text[i] = text[i + 1] = ' ';
                i++;
            }
        }
        return new string(text);
    }

    // The index of the quote that closes the string or character literal opened at <paramref name="start"/>: a backslash
    // escapes in a regular literal, a doubled quote in a verbatim one.
    private static int EndOfLiteral(string source, int start)
    {
        var quote = source[start];
        var verbatim = quote == '"' && start > 0 && (source[start - 1] == '@' || (start > 1 && source[start - 2] == '@'));
        for (var i = start + 1; i < source.Length; i++)
        {
            if (!verbatim && source[i] == '\\')
            {
                i++;
                continue;
            }
            if (source[i] == quote)
            {
                if (verbatim && i + 1 < source.Length && source[i + 1] == '"')
                {
                    i++;
                    continue;
                }
                return i;
            }
        }
        return source.Length - 1;
    }

    // ---- the service and the pool ----------------------------------------------------------------------------------------

    /// <summary>The base's pooled helpers over the stand-in cmdlets, as a module's service calls them.</summary>
    private sealed class Reads(ExoConnectionPool pool) : ExchangeServiceBase(pool, null!, NullLogger.Instance, "")
    {
        /// <summary>The pipeline as the read's second command threw: its commands and its warning stream.</summary>
        public (int Commands, int Warnings)? PipelineAtTheThrow { get; private set; }

        /// <summary>The pipeline as the next read found it: its commands, its error stream and its warning stream.</summary>
        public (int Commands, int Errors, int Warnings)? PipelineAtTheStart { get; private set; }

        // The first command through the helper, then the caller's cancel - as the operator's lands while it is in flight - then
        // the second through the same helper.
        public Task<string> TwoCommands(string helper, CancellationTokenSource cancel) =>
            RunPooledReadAsync((ps, tracker) =>
            {
                var first = Send(ps, tracker, helper, "Get-StandInFirst");
                cancel.Cancel();
                try
                {
                    return first + "," + Send(ps, tracker, helper, "Get-StandInSecond");
                }
                catch (OperationCanceledException)
                {
                    PipelineAtTheThrow = (ps.Commands.Commands.Count, ps.Streams.Warning.Count);
                    throw;
                }
            }, cancel.Token);

        public Task<string> CancelledBeforeTheFirst(string helper, CancellationTokenSource cancel) =>
            RunPooledReadAsync((ps, tracker) =>
            {
                cancel.Cancel();
                return Send(ps, tracker, helper, "Get-StandInFirst");
            }, cancel.Token);

        public Task<string> OneCommand(string command, CancellationToken cancellationToken) =>
            RunPooledReadAsync((ps, tracker) =>
            {
                PipelineAtTheStart = (ps.Commands.Commands.Count, ps.Streams.Error.Count, ps.Streams.Warning.Count);
                return Send(ps, tracker, "Invoke", command);
            }, cancellationToken);

        // The caller cancels as the write's call starts, then the write is sent, and a command after it.
        public async Task<string> CancelledAsTheWriteStarts(string helper, CancellationTokenSource cancel)
        {
            if (helper == "RunAsync")
            {
                var result = await RunAsync((ps, tracker) =>
                {
                    cancel.Cancel();
                    Send(ps, tracker, "Invoke", "Set-StandInWrite");
                    Send(ps, tracker, "Invoke", "Get-StandInFirst");
                }, () => ("written", null), cancellationToken: cancel.Token);
                return result.Success ? result.Message : "failed: " + result.Message;
            }

            return await RunPooledQueryAsync((ps, tracker) =>
            {
                cancel.Cancel();
                Send(ps, tracker, "Invoke", "Set-StandInWrite");
                Send(ps, tracker, "Invoke", "Get-StandInFirst");
                return "written";
            }, cancellationToken: cancel.Token);
        }

        public Task WaitingCall(string helper, CancellationToken cancellationToken) => helper switch
        {
            "RunAsync" => RunAsync((ps, tracker) => Send(ps, tracker, "Invoke", "Get-StandInFirst"), cancellationToken: cancellationToken),
            "RunPooledQueryAsync" => RunPooledQueryAsync((ps, tracker) => Send(ps, tracker, "Invoke", "Get-StandInFirst"), cancellationToken: cancellationToken),
            _ => RunPooledReadAsync((ps, tracker) => Send(ps, tracker, "Invoke", "Get-StandInFirst"), cancellationToken),
        };

        private static string Send(PowerShell ps, ConnectionErrorTracker tracker, string helper, string command)
        {
            ps.AddCommand(command);
            var rows = helper switch
            {
                "Invoke" => Invoke(ps, tracker),
                "InvokeOptional" => InvokeOptional(ps, tracker),
                "InvokeBestEffort" => InvokeBestEffort(ps, tracker, out _),
                _ => ReadUnlessNotFound(ps, tracker),
            };
            return string.Join("+", rows.Select(row => row.ToString()));
        }
    }

    /// <summary>A pool whose connect seam opens a stand-in runspace per connection: conn-1, conn-2, ...</summary>
    private ExoConnectionPool StandInPool()
    {
        var pool = new ExoConnectionPool(_h.Config, _h.ModuleConfig, _h.Enablement, NullLogger<ExoConnectionPool>.Instance, _h.Trace,
            (_, generation, _) => StandIn(generation, $"conn-{Interlocked.Increment(ref _connects)}"));
        _pools.Add(pool);
        return pool;
    }

    /// <summary>
    /// An opened runspace of stand-ins that log each call by connection: Get-StandInFirst answers and leaves a warning in its
    /// stream; Get-StandInSecond and Set-StandInWrite answer; Get-Mailbox answers a cloud mailbox for any identity;
    /// Disconnect-ExchangeOnline logs the id it was asked to close.
    /// </summary>
    private PooledRunspace StandIn(long generation, string connectionId)
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        rs.SessionStateProxy.SetVariable("calls", _calls);
        rs.SessionStateProxy.SetVariable("disconnects", _disconnects);
        rs.SessionStateProxy.SetVariable("connectionId", connectionId);
        using (var init = PowerShell.Create())
        {
            init.Runspace = rs;
            init.AddScript("""
                function global:Get-StandInFirst {
                    [CmdletBinding()] param()
                    [void]$global:calls.Add("$global:connectionId|Get-StandInFirst")
                    Write-Warning 'the first command left this'
                    "Get-StandInFirst-$global:connectionId"
                }
                function global:Get-StandInSecond {
                    [CmdletBinding()] param()
                    [void]$global:calls.Add("$global:connectionId|Get-StandInSecond")
                    "Get-StandInSecond-$global:connectionId"
                }
                function global:Set-StandInWrite {
                    [CmdletBinding()] param()
                    [void]$global:calls.Add("$global:connectionId|Set-StandInWrite")
                }
                function global:Get-Mailbox {
                    [CmdletBinding()] param([string] $Identity)
                    [void]$global:calls.Add("$global:connectionId|Get-Mailbox $Identity")
                    [pscustomobject]@{ PrimarySmtpAddress = $Identity; RecipientTypeDetails = 'UserMailbox' }
                }
                function global:Disconnect-ExchangeOnline {
                    param($ConnectionId, $Confirm)
                    [void]$global:disconnects.Add($ConnectionId)
                }
                """);
            init.Invoke();
            Assert.False(init.HadErrors, "The stand-in cmdlets could not be defined.");
        }

        var ps = PowerShell.Create();
        ps.Runspace = rs;
        return new PooledRunspace(rs, ps, generation, connectionId);
    }

    private static async Task<List<PooledRunspace>> HoldEveryConnection(ExoConnectionPool pool)
    {
        var held = new List<PooledRunspace>();
        for (var i = 0; i < PoolConnections; i++)
            held.Add(await pool.BorrowAsync(Promptly, TestContext.Current.CancellationToken));
        return held;
    }

    private static void ReturnAll(ExoConnectionPool pool, List<PooledRunspace> held)
    {
        foreach (var runspace in held)
            pool.Return(runspace);
        held.Clear();
    }

    /// <summary>How many borrows wait for a slot, from the pool's trace: asked for, neither granted nor ended.</summary>
    private int BorrowsWaitingForASlot()
    {
        var (asked, ended, granted) = (0, 0, 0);
        foreach (var path in _h.Jsonl.GetTraceLogPaths(DateTime.Now).Where(File.Exists))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            while (reader.ReadLine() is { } line)
            {
                if (string.IsNullOrWhiteSpace(line))
                    continue;
                try
                {
                    using var step = JsonDocument.Parse(line);
                    var stage = step.RootElement.TryGetProperty("stage", out var s) ? s.GetString() : null;
                    var result = step.RootElement.TryGetProperty("result", out var r) ? r.GetString() : null;
                    if (stage == "ExoPoolSlotRequested" && result is "Failed" or "Cancelled")
                        ended++;
                    else if (stage == "ExoPoolSlotRequested")
                        asked++;
                    else if (stage == "ExoPoolSlotAcquired")
                        granted++;
                }
                catch (JsonException)
                {
                    continue;
                }
            }
        }
        return asked - granted - ended;
    }

    private static async Task WaitUntil(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + Bound;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting until {what}.");
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}
