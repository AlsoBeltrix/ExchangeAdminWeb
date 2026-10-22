using System.Collections;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Reflection;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.JSInterop;
using NSubstitute;
using Page = ExchangeAdminWeb.Components.Pages.RecipientLookup;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a23d, the Recipient Lookup half: <c>WhenCreated</c> and <c>LastLogonTime</c> were
/// read <c>as DateTime?</c>, so any other type showed as "N/A", as if none were set.
/// </summary>
/// <remarks>
/// Behaviour, not text. The real <see cref="RecipientLookupService"/> on a real pool, whose connect seam
/// hands out an in-process runspace carrying stub <c>Get-Recipient</c>, <c>Get-Mailbox</c> and
/// <c>Get-MailboxStatistics</c> functions that report what each test sets, and the page's compiled render
/// tree with the result. The mailbox is a cloud one, so nothing on-premises is asked. No Exchange.
/// </remarks>
public sealed class RecipientLookupReportedTimeTests : IDisposable
{
    private const string Mailbox = "alice@contoso.test";

    // Created late in the UTC day, so a reading that kept the +05:30 wall clock would show the next day.
    private static readonly DateTime CreatedUtc = new(2026, 10, 1, 20, 15, 0, DateTimeKind.Utc);
    private static readonly DateTime LogonUtc = new(2026, 10, 30, 13, 0, 0, DateTimeKind.Utc);

    // aud-a23e. After the US clocks go back (2026-11-01), so New York's offset differs from CreatedUtc's.
    private static readonly DateTime LogonAfterDstEndUtc = new(2026, 11, 6, 14, 0, 0, DateTimeKind.Utc);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "recipient-times-" + Guid.NewGuid().ToString("N"));
    private readonly Hashtable _read = Hashtable.Synchronized(new Hashtable { ["WhenCreated"] = null, ["LastLogonTime"] = null });
    private ExoConnectionPool? _pool;

    public void Dispose()
    {
        _pool?.Dispose();
        try { Directory.Delete(_directory, recursive: true); } catch (IOException) { }
    }

    // ---- the harness ------------------------------------------------------------------------

    private static object As(string form, DateTime utc) => form switch
    {
        "DateTimeOffset" => new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(5.5)),
        "PSObject wrapping a DateTimeOffset" => new PSObject(new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(5.5))),
        "PSObject wrapping a DateTime" => new PSObject(utc),
        "DateTime of Kind Utc" => utc,
        _ => throw new ArgumentOutOfRangeException(nameof(form), form, null),
    };

    private RecipientLookupService CreateService()
    {
        Directory.CreateDirectory(_directory);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = _directory,
            ["ExchangeOnline:AppId"] = "11111111-1111-1111-1111-111111111111",
            ["ExchangeOnline:Organization"] = "contoso.onmicrosoft.com",
        }).Build();
        var environment = Substitute.For<IWebHostEnvironment>();
        environment.ContentRootPath.Returns(_directory);
        var store = TestConfigStore.Create(_directory);
        var catalog = new ModuleCatalog();
        var moduleConfig = new ModuleConfigService(catalog, environment,
            new ModuleConfigRepository(store), Substitute.For<ILogger<ModuleConfigService>>());
        var enablement = new ModuleEnablementService(catalog, environment, moduleConfig,
            new ModuleEnablementRepository(store), config, Substitute.For<ILogger<ModuleEnablementService>>());
        enablement.SaveEnablement(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = true });
        var trace = new OperationTraceService(config,
            new JsonlLogService(config, Substitute.For<ILogger<JsonlLogService>>()));

        _pool = new ExoConnectionPool(config, moduleConfig, enablement,
            Substitute.For<ILogger<ExoConnectionPool>>(), trace, (_, generation, _) =>
            {
                var state = InitialSessionState.CreateDefault2();
                state.Variables.Add(new SessionStateVariableEntry("Read", _read, null));
                state.Commands.Add(new SessionStateFunctionEntry("Disconnect-ExchangeOnline", "[CmdletBinding()] param([switch] $Confirm)"));
                state.Commands.Add(new SessionStateFunctionEntry("Get-Recipient", """
                    [CmdletBinding()]
                    param([string] $Identity)
                    [pscustomobject]@{ DisplayName = 'Alice'; RecipientTypeDetails = 'UserMailbox'; WhenCreated = $Read['WhenCreated']; EmailAddresses = @("SMTP:$Identity") }
                    """));
                state.Commands.Add(new SessionStateFunctionEntry("Get-Mailbox", """
                    [CmdletBinding()]
                    param([string] $Identity)
                    [pscustomobject]@{ ForwardingSmtpAddress = $null; ForwardingAddress = $null }
                    """));
                state.Commands.Add(new SessionStateFunctionEntry("Get-MailboxStatistics", """
                    [CmdletBinding()]
                    param([string] $Identity, [switch] $Archive)
                    if ($Archive) { return }
                    [pscustomobject]@{ ItemCount = 10; DeletedItemCount = 1; LastLogonTime = $Read['LastLogonTime']; TotalItemSize = '1 GB (1,073,741,824 bytes)'; TotalDeletedItemSize = '0 B (0 bytes)' }
                    """));
                var runspace = RunspaceFactory.CreateRunspace(state);
                runspace.Open();
                var powershell = PowerShell.Create();
                powershell.Runspace = runspace;
                return new PooledRunspace(runspace, powershell, generation);
            });

        return new RecipientLookupService(_pool, null!, Substitute.For<ILogger<RecipientLookupService>>(), config, null!, trace);
    }

    /// <summary>The browser, as far as the page asks it anything: its time zone, or a failure to give one.</summary>
    private sealed class Browser(string? zone) : IJSRuntime
    {
        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, object?[]? args) =>
            InvokeAsync<TValue>(identifier, CancellationToken.None, args);

        public ValueTask<TValue> InvokeAsync<TValue>(string identifier, CancellationToken cancellationToken, object?[]? args) =>
            identifier == "getBrowserTimeZone" && zone is not null
                ? ValueTask.FromResult((TValue)(object)zone)
                : ValueTask.FromException<TValue>(new JSException($"{identifier} could not answer"));
    }

    /// <summary>
    /// aud-a23e. The page showing the lookup's result after asking a browser that answers <paramref name="zone"/>
    /// (null: the call fails) for its time zone, through the page's own first-render read.
    /// </summary>
    private async Task<string> LookedUp(string? zone)
    {
        var page = new Page();
        const BindingFlags members = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        typeof(Page).GetProperty("JS", members)!.SetValue(page, new Browser(zone));
        typeof(Page).GetProperty("Logger", members)!.SetValue(page, NullLogger<Page>.Instance);
        await (Task)typeof(Page).GetMethod("ReadBrowserZoneAsync", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [])!;
        return await LookedUp(page);
    }

    /// <summary>The page showing the lookup's result: its rendered text, in the invariant culture.</summary>
    private Task<string> LookedUp() => LookedUp(new Page());

    private async Task<string> LookedUp(Page page)
    {
        var result = await CreateService().GetRecipientInfoAsync(Mailbox);
        Assert.Null(result.Error);

        typeof(Page).GetField("result", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, result);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
        {
            return Rendered(page);
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

#pragma warning disable BL0006 // the page's compiled render tree is read directly; no renderer is involved
    private static string Rendered(Page page)
    {
        using var builder = new RenderTreeBuilder();
        typeof(Page).GetMethod("BuildRenderTree", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(page, [builder]);
        var frames = builder.GetFrames();
        return string.Concat(frames.Array.Take(frames.Count).Select(frame => frame.FrameType switch
        {
            RenderTreeFrameType.Text => frame.TextContent,
            RenderTreeFrameType.Markup => frame.MarkupContent,
            _ => "",
        }));
    }
#pragma warning restore BL0006

    // ---- the guards -------------------------------------------------------------------------

    // aud-a23d, the finding: a DateTimeOffset or a wrapped value showed as "N/A". Each is read as its
    // instant and shown as the Kind-Utc DateTime of that instant is - with no browser zone known, in UTC,
    // labelled UTC (aud-a23e) - so the creation time, late in the UTC day, would read as the next day had the
    // +05:30 wall clock been kept. The Kind-Utc DateTime is the control.
    [Theory]
    [InlineData("DateTimeOffset")]
    [InlineData("PSObject wrapping a DateTimeOffset")]
    [InlineData("PSObject wrapping a DateTime")]
    [InlineData("DateTime of Kind Utc")]
    public async Task BothTimesReportedInAnotherForm_AreShownAsTheSameInstant(string form)
    {
        _read["WhenCreated"] = As(form, CreatedUtc);
        _read["LastLogonTime"] = As(form, LogonUtc);

        var text = await LookedUp();

        Assert.Contains("Created</td>2026-10-01 20:15 (UTC, UTC+00:00)", text, StringComparison.Ordinal);
        Assert.Contains("Last Logon</td>2026-10-30 13:00 (UTC, UTC+00:00)", text, StringComparison.Ordinal);
        Assert.DoesNotContain("N/A", text.Split("Mailbox Size")[0], StringComparison.Ordinal);
    }

    // aud-a23e, the finding: both times were printed as read, so a Kind-Local and a Kind-Utc value of one instant
    // showed two clocks, and neither said whose. With the browser's zone known, each is shown in it, the zone
    // named, with that zone's offset at that instant - New York's differs across the clocks going back.
    // aud-a23g: of Kind Utc only; a Kind-Local value names no zone this app can trust (the test below).
    [Theory]
    [InlineData("America/New_York", "2026-10-01 16:15 (America/New_York, UTC-04:00)", "2026-11-06 09:00 (America/New_York, UTC-05:00)")]
    [InlineData("Asia/Kolkata", "2026-10-02 01:45 (Asia/Kolkata, UTC+05:30)", "2026-11-06 19:30 (Asia/Kolkata, UTC+05:30)")]
    public async Task WithTheBrowsersZone_BothTimesAreShownInIt_WithTheZoneAndItsOffset(string zone, string created, string logon)
    {
        _read["WhenCreated"] = CreatedUtc;
        _read["LastLogonTime"] = LogonAfterDstEndUtc;

        var text = await LookedUp(zone);

        Assert.Contains($"Created</td>{created}", text, StringComparison.Ordinal);
        Assert.Contains($"Last Logon</td>{logon}", text, StringComparison.Ordinal);
    }

    // aud-a23g. A Kind-Local value says its clock is this server's, which nothing here proves: it is shown as
    // reported and given no zone, even with the browser's zone known - the rule Migration and Check Status share.
    [Theory]
    [InlineData("America/New_York")]
    [InlineData("Asia/Kolkata")]
    public async Task WithTheBrowsersZone_TimesOfKindLocal_AreShownAsReported_AndGivenNoZone(string zone)
    {
        var created = CreatedUtc.ToLocalTime();
        var logon = LogonAfterDstEndUtc.ToLocalTime();
        _read["WhenCreated"] = created;
        _read["LastLogonTime"] = logon;

        var text = await LookedUp(zone);

        Assert.Contains($"Created</td>{created.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} (time zone not reported)",
            text, StringComparison.Ordinal);
        Assert.Contains($"Last Logon</td>{logon.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)} (time zone not reported)",
            text, StringComparison.Ordinal);
        Assert.DoesNotContain($"({zone},", text, StringComparison.Ordinal);
    }

    // aud-a23e. Without the browser's zone - the call fails, the answer is blank, or this server cannot resolve
    // it - the instant is still known, so each time is shown in UTC and labelled UTC: never a guessed zone, and
    // never an unlabelled clock.
    [Theory]
    [InlineData("the call fails")]
    [InlineData("a blank answer")]
    [InlineData("an id this server cannot resolve")]
    public async Task WithoutTheBrowsersZone_BothTimesAreShownInUtc_AndSaySo(string answer)
    {
        _read["WhenCreated"] = CreatedUtc;
        _read["LastLogonTime"] = LogonUtc;

        var text = await LookedUp(answer switch
        {
            "the call fails" => null,
            "a blank answer" => " ",
            _ => "Mars/Olympus_Mons",
        });

        Assert.Contains("Created</td>2026-10-01 20:15 (UTC, UTC+00:00)", text, StringComparison.Ordinal);
        Assert.Contains("Last Logon</td>2026-10-30 13:00 (UTC, UTC+00:00)", text, StringComparison.Ordinal);
    }

    // aud-a23d. What cannot be read as a time is the aud-a7i marker, with its type and its text - not "N/A",
    // which says Exchange reported nothing.
    [Theory]
    [InlineData("text in a culture's own format", "System.String", "10/01/2026 8:15:00 PM")]
    [InlineData("a number", "System.Int32", "42")]
    public async Task BothTimesThisAppCannotRead_AreShownAsTheMarker_WithTheirText_NeverAsNA(string form, string typeName, string shownText)
    {
        object value = form == "a number" ? 42 : shownText;
        _read["WhenCreated"] = value;
        _read["LastLogonTime"] = value;

        var text = await LookedUp();

        var marker = $"reported in a form this app cannot read ({typeName}): {shownText}";
        Assert.Contains($"Created</td>{marker}", text, StringComparison.Ordinal);
        Assert.Contains($"Last Logon</td>{marker}", text, StringComparison.Ordinal);
    }

    // aud-a23d. Nothing reported is still "N/A", as before.
    [Fact]
    public async Task BothTimesNotReported_AreShownAsNA_AsBefore()
    {
        var text = await LookedUp();

        Assert.Contains("Created</td>N/A", text, StringComparison.Ordinal);
        Assert.Contains("Last Logon</td>N/A", text, StringComparison.Ordinal);
    }
}
