using System.Globalization;
using System.Management.Automation;
using System.Reflection;
using ExchangeAdminWeb.Models;
using Microsoft.AspNetCore.Components.Rendering;
using Microsoft.AspNetCore.Components.RenderTree;
using static ExchangeAdminWeb.Tests.MigrationStubExchange;
using Page = ExchangeAdminWeb.Components.Pages.RecipientLookup;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Audit finding aud-a68, part 1. Recipient Lookup took the on-premises archive's size from the on-premises read but never
/// <c>ArchiveEnabled</c> - only the Exchange Online read set it - so every on-premises archive showed "Archive: Disabled" with
/// its rows hidden, and an on-premises archive that could not be read showed "Disabled" beside its own warning.
/// </summary>
/// <remarks>
/// The real <see cref="ExchangeAdminWeb.Services.RecipientLookupService"/> over <see cref="MigrationStubExchange"/>: Exchange
/// Online holds the recipient as a mail user with no mailbox there, so the location read asks on-premises, whose Get-Mailbox
/// holds it; on-premises Get-MailboxStatistics reports 2.5 GB and an archive of 512 MB unless a test says otherwise. And the
/// page's compiled render tree showing the result. No Exchange.
/// </remarks>
public sealed class RecipientLookupOnPremArchiveTests : IDisposable
{
    private const string Address = "alice@contoso.test";

    private readonly MigrationStubExchange _exchange = new() { OnPremConfigured = true };

    public RecipientLookupOnPremArchiveTests()
    {
        _exchange.Recipients[Address] = Row(("DisplayName", "Alice"), ("RecipientTypeDetails", "MailUser"));
        _exchange.OnPremMailboxes[Address] = Row(("Identity", Address));
    }

    public void Dispose() => _exchange.Dispose();

    private async Task<RecipientInfoResult> LookedUp()
    {
        var result = await _exchange.RecipientLookup().GetRecipientInfoAsync(Address);
        Assert.Null(result.Error);
        Assert.Equal("On-Premises", result.MailboxLocation);
        return result;
    }

    private static Func<string, ErrorRecord> NotFoundIn(string shape) => shape switch
    {
        "category" => NotFoundByCategory,
        "type" => NotFoundByType,
        _ => NotFoundByIdentifier,
    };

#pragma warning disable BL0006 // the page's compiled render tree is read directly; no renderer is involved
    private static string Rendered(RecipientInfoResult result)
    {
        var page = new Page();
        typeof(Page).GetField("result", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(page, result);
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
        try
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
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }
#pragma warning restore BL0006

    // The finding. Before: ArchiveEnabled false, so "Archive: Disabled" and no archive rows, over an archive whose size had
    // just been read.
    [Fact]
    public async Task AnOnPremArchive_IsEnabled_AndItsRowsAreShown()
    {
        var result = await LookedUp();

        Assert.True(result.ArchiveEnabled);
        Assert.Null(result.ArchiveUnreadable);
        Assert.Equal(0.5, result.ArchiveSizeGB);
        Assert.Empty(result.Warnings);

        var page = Rendered(result);
        Assert.Contains("Archive</td>Enabled", page, StringComparison.Ordinal);
        Assert.Contains("Archive Size</td>0.50 GB", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Archive</td>Disabled", page, StringComparison.Ordinal);
    }

    // An archive with nothing in it is still an archive. Found is what says so, not a size above zero: Exchange's not-found
    // and an empty archive both read as 0 GB.
    [Fact]
    public async Task AnEmptyOnPremArchive_IsEnabled_AtZero()
    {
        _exchange.Behaviour["OnPremArchiveSize"] = "0 B (0 bytes)";

        var result = await LookedUp();

        Assert.True(result.ArchiveEnabled);
        Assert.Equal(0.0, result.ArchiveSizeGB);
        Assert.Contains("Archive Size</td>0.00 GB", Rendered(result), StringComparison.Ordinal);
    }

    // Exchange's own "no such object" for the archive, in each shape it is read from: no archive, as before.
    [Theory]
    [InlineData("category")]
    [InlineData("type")]
    [InlineData("identifier")]
    public async Task NoOnPremArchive_IsDisabled(string shape)
    {
        _exchange.NotFound = NotFoundIn(shape);
        _exchange.Behaviour["OnPremNoArchive"] = true;

        var result = await LookedUp();

        Assert.False(result.ArchiveEnabled);
        Assert.Null(result.ArchiveUnreadable);
        Assert.Null(result.ArchiveSizeGB);
        Assert.Empty(result.Warnings);

        var page = Rendered(result);
        Assert.Contains("Archive</td>Disabled", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Archive Size</td>", page, StringComparison.Ordinal);
    }

    // The finding's second half. Before: "Archive: Disabled" beside the warning that said the archive could not be read.
    [Theory]
    [InlineData("the read fails", "The on-premises archive size could not be read: Get-MailboxStatistics -Archive failed: the archive database is offline.")]
    [InlineData("the read answers no row", "The on-premises archive size could not be read: on-premises Exchange returned no archive statistics.")]
    [InlineData("the size does not parse", "The on-premises archive size could not be read: the reported TotalItemSize 'Unlimited' is not a size this app can read.")]
    [InlineData("the size read throws as a whole", "Could not retrieve on-premises mailbox size: the credential store is unreachable")]
    public async Task AnOnPremArchiveThatCannotBeRead_IsCouldNotBeRead_NeverDisabled(string how, string warning)
    {
        switch (how)
        {
            case "the read fails":
                _exchange.Failures["OnPrem Get-MailboxStatistics -Archive"] = Refused("the archive database is offline");
                break;
            case "the read answers no row":
                _exchange.Behaviour["OnPremArchiveSize"] = null;
                break;
            case "the size does not parse":
                _exchange.Behaviour["OnPremArchiveSize"] = "Unlimited";
                break;
            default:
                // kd-14: a throw that is not a cancellation - the size read's credentials failing - since a cancellation
                // now leaves the lookup rather than reaching this catch.
                _exchange.Failures["OnPrem Credentials on-prem mailbox size check"] = "the credential store is unreachable";
                break;
        }

        var result = await LookedUp();

        Assert.NotNull(result.ArchiveUnreadable);
        Assert.Null(result.ArchiveSizeGB);
        Assert.Contains(result.Warnings, shown => shown.StartsWith(warning, StringComparison.Ordinal));

        var page = Rendered(result);
        Assert.Contains("Archive</td>Could not be read", page, StringComparison.Ordinal);
        Assert.DoesNotContain("Archive</td>Disabled", page, StringComparison.Ordinal);
    }
}
