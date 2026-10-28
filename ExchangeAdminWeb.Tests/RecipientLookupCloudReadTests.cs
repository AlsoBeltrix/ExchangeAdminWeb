using System.Management.Automation;
using System.Reflection;
using ExchangeAdminWeb.Models;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Page = ExchangeAdminWeb.Components.Pages.RecipientLookup;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a54. Recipient Lookup read the recipient's Exchange Online mailbox with its own Get-Mailbox under
/// -ErrorAction Ignore, so a read that failed - a throw included - showed as "not a mailbox": Forwarding "None", Archive
/// "Disabled", and nothing said. And it wrapped the archive statistics read in a bare catch, so any failure of that read
/// showed as no archive. Exchange's own "no such object", read from the error's structure, is still an absence; any other
/// failure is a warning naming the read and why, and the page says "Could not be read" where it stated an absence.
/// </summary>
/// <remarks>
/// The real <see cref="ExchangeAdminWeb.Services.RecipientLookupService"/> over <see cref="MigrationStubExchange"/>'s
/// Exchange Online stand-ins (Get-Recipient, Get-Mailbox, Get-MailboxStatistics with -Archive), and the page's compiled
/// render tree showing the result. No Exchange.
/// </remarks>
public sealed class RecipientLookupCloudReadTests : IDisposable
{
    private const string Address = "alice@contoso.test";
    private const string Refusal = "the directory is busy";

    private readonly MigrationStubExchange _exchange = new();

    public void Dispose() => _exchange.Dispose();

    private void ACloudMailbox()
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "UserMailbox"));
        _exchange.Mailboxes[Address] = CloudMailbox(Address);
    }

    private static Func<string, ErrorRecord> NotFoundIn(string shape) => shape switch
    {
        "category" => NotFoundByCategory,
        "type" => NotFoundByType,
        _ => NotFoundByIdentifier,
    };

    private object Failing(string how) => how == "throw" ? Refusal : Refused(Refusal);

    private bool StatisticsWereAsked() =>
        _exchange.CallList().Any(call => call.StartsWith("Get-MailboxStatistics", StringComparison.Ordinal));

#pragma warning disable BL0006 // the page's compiled render tree is read directly; no renderer is involved
    private static string Rendered(RecipientInfoResult result)
    {
        var page = new Page();
        typeof(Page).GetField("result", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, result);
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

    // ---- the mailbox read ----------------------------------------------------------------------

    // The finding. Before, Ignore turned both into no rows: not a mailbox, no warning, "None" and "Disabled" on the page.
    [Theory]
    [InlineData("throw")]
    [InlineData("error")]
    public async Task AMailboxReadThatFails_IsAWarning_NeverNoMailbox(string how)
    {
        ACloudMailbox();
        _exchange.Failures[$"Get-Mailbox {Address}"] = Failing(how);

        var result = await _exchange.RecipientLookup().GetRecipientInfoAsync(Address, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Equal($"Get-Mailbox failed: {Refusal}", result.MailboxUnreadable);
        Assert.Equal(result.MailboxUnreadable, result.ArchiveUnreadable);
        Assert.False(result.ArchiveEnabled);
        Assert.Contains($"The Exchange Online mailbox could not be read: Get-Mailbox failed: {Refusal}.", result.Warnings);
        Assert.False(StatisticsWereAsked());

        var page = Rendered(result);
        Assert.Contains("Forwarding</td>Could not be read", page, StringComparison.Ordinal);
        Assert.Contains("Archive</td>Could not be read", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Forwarding</td>None", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Archive</td>Disabled", page, StringComparison.Ordinal);
    }

    // Exchange's own "no such object", in each shape it is read from: a recipient with no Exchange Online mailbox is not a
    // mailbox, as before - nothing more is read and the page says so. RecipientLookupAccuracy S1: Get-Mailbox is no longer
    // asked about a mail user, whose type already answers it, so the recipient here is one whose type Exchange did not
    // report - Get-Mailbox decides - and which it reports as cloud-only, so on-premises is not asked either.
    [Theory]
    [InlineData("category")]
    [InlineData("type")]
    [InlineData("identifier")]
    public async Task NoMailbox_IsNoMailbox(string shape)
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("IsDirSynced", false));
        _exchange.NotFound = NotFoundIn(shape);
        _exchange.OnPremConfigured = true;

        var result = await _exchange.RecipientLookup().GetRecipientInfoAsync(Address, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.Contains($"Get-Mailbox {Address}", _exchange.CallList());
        Assert.Null(result.MailboxUnreadable);
        Assert.Null(result.ArchiveUnreadable);
        Assert.Empty(result.Warnings);
        Assert.False(StatisticsWereAsked());
        Assert.Equal(RecipientLocationClass.NotApplicable, result.LocationClass);
        Assert.DoesNotContain(_exchange.CallList(), call => call.StartsWith("OnPrem", StringComparison.Ordinal));

        var page = Rendered(result);
        Assert.Contains("Forwarding</td>None", page, StringComparison.Ordinal);
        Assert.Contains("Archive</td>Disabled", page, StringComparison.Ordinal);
    }

    // ---- the archive statistics read -----------------------------------------------------------

    // The finding. Before, the bare catch made both no archive: "Disabled", and nothing said.
    [Theory]
    [InlineData("throw")]
    [InlineData("error")]
    public async Task AnArchiveReadThatFails_IsAWarning_NotNoArchive(string how)
    {
        ACloudMailbox();
        _exchange.Failures["Get-MailboxStatistics -Archive"] = Failing(how);

        var result = await _exchange.RecipientLookup().GetRecipientInfoAsync(Address, TestContext.Current.CancellationToken);

        Assert.Null(result.Error);
        Assert.False(result.ArchiveEnabled);
        Assert.Equal($"Get-MailboxStatistics -Archive failed: {Refusal}", result.ArchiveUnreadable);
        Assert.Null(result.MailboxUnreadable);
        Assert.Equal(1.5, result.MailboxSizeGB);
        Assert.Equal([$"The Exchange Online archive could not be read: Get-MailboxStatistics -Archive failed: {Refusal}."], result.Warnings);

        var page = Rendered(result);
        Assert.Contains("Archive</td>Could not be read", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Archive</td>Disabled", page, StringComparison.Ordinal);
    }

    // An answer with no row and no error is not Exchange saying there is no archive, so it is not an absence: the marker
    // both size readers give the same answer.
    [Fact]
    public async Task AnArchiveReadWithNoRow_IsAWarning_NotNoArchive()
    {
        ACloudMailbox();
        _exchange.Behaviour["ArchiveSize"] = null;

        var result = await _exchange.RecipientLookup().GetRecipientInfoAsync(Address, TestContext.Current.CancellationToken);

        Assert.False(result.ArchiveEnabled);
        Assert.Equal("Exchange returned no archive statistics", result.ArchiveUnreadable);
        Assert.Equal(["The Exchange Online archive could not be read: Exchange returned no archive statistics."], result.Warnings);
        Assert.Contains("Archive</td>Could not be read", Rendered(result), StringComparison.Ordinal);
    }

    // Exchange's own "no such object" for the archive, in each shape it is read from: no archive, as before.
    [Theory]
    [InlineData("category")]
    [InlineData("type")]
    [InlineData("identifier")]
    public async Task NoArchive_IsNoArchive(string shape)
    {
        ACloudMailbox();
        _exchange.NotFound = NotFoundIn(shape);
        _exchange.Behaviour["NoArchive"] = true;

        var result = await _exchange.RecipientLookup().GetRecipientInfoAsync(Address, TestContext.Current.CancellationToken);

        Assert.False(result.ArchiveEnabled);
        Assert.Null(result.ArchiveUnreadable);
        Assert.Empty(result.Warnings);
        Assert.Contains("Archive</td>Disabled", Rendered(result), StringComparison.Ordinal);
    }

    // The control: a mailbox with an archive, read.
    [Fact]
    public async Task AnArchive_IsShown()
    {
        ACloudMailbox();

        var result = await _exchange.RecipientLookup().GetRecipientInfoAsync(Address, TestContext.Current.CancellationToken);

        Assert.Equal("Cloud", result.MailboxLocation);
        Assert.True(result.ArchiveEnabled);
        Assert.Null(result.ArchiveUnreadable);
        Assert.Null(result.MailboxUnreadable);
        Assert.Equal(1.5, result.ArchiveSizeGB);
        Assert.Empty(result.Warnings);

        var page = Rendered(result);
        Assert.Contains("Archive</td>Enabled", page, StringComparison.Ordinal);
        Assert.Contains("Forwarding</td>None", page, StringComparison.Ordinal);
    }
}
