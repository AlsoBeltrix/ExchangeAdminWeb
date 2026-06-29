using System.Management.Automation;
using ExchangeAdminWeb.Models.BlockedSenders;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// Catalog integration + deterministic-helper coverage for the BlockedSenders module. The live
/// EXO read/unblock run through the shared <see cref="ExoConnectionPool"/>, which is sealed and
/// cannot be unit-hosted (see ExoConnectionPool remarks), so the PowerShell paths are
/// manual-validation-only — see docs/BlockedSenders.md. What is covered here: the descriptor wiring
/// the authorization model depends on, and the pure mapping/normalization helpers.
/// </summary>
public sealed class BlockedSendersTests
{
    private readonly ModuleCatalog _catalog = new();

    // ---- Catalog wiring ---------------------------------------------------------------------

    [Fact]
    public void Catalog_HasBlockedSendersModule()
    {
        var module = _catalog.GetById("BlockedSenders");
        Assert.NotNull(module);
        Assert.Equal("blocked-senders", module!.Route);
        Assert.Equal("Exchange", module.Category);
        Assert.Equal("ExchangeOnline", module.DependsOn);
        Assert.False(module.EnabledByDefault);
        Assert.False(module.IsSystemModule);
    }

    [Fact]
    public void Catalog_BlockedSenders_RouteIsUnique()
    {
        var matches = _catalog.GetAll().Count(m =>
            string.Equals(m.Route, "blocked-senders", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, matches);
    }

    [Fact]
    public void Catalog_BlockedSenders_MainPermissionIsFailClosed()
    {
        var module = _catalog.GetById("BlockedSenders")!;
        Assert.Equal("BlockedSenders", module.MainPermission.PolicyAlias);
        Assert.True(module.MainPermission.FailClosed);
    }

    [Fact]
    public void Catalog_BlockedSenders_HasFailClosedUnblockGranular()
    {
        var module = _catalog.GetById("BlockedSenders")!;
        var granular = Assert.Single(module.GranularPermissions);
        Assert.Equal("BlockedSendersUnblock", granular.PolicyAlias);
        Assert.True(granular.FailClosed);
    }

    [Fact]
    public void Catalog_BlockedSenders_PolicyAliasesAreConfigurable()
    {
        var aliases = _catalog.GetConfigurablePolicyAliases();
        Assert.Contains("BlockedSenders", aliases);
        Assert.Contains("BlockedSendersUnblock", aliases);
    }

    // ---- Page wiring (descriptor route/policy must match the .razor) -------------------------

    [Fact]
    public void Page_BlockedSenders_RouteAndPolicyMatchDescriptor()
    {
        var razor = File.ReadAllText(FindPage("BlockedSenders.razor"));
        Assert.Contains("@page \"/blocked-senders\"", razor);
        Assert.Contains("[Authorize(Policy = \"BlockedSenders\")]", razor);
        // The mutating write must re-check the granular policy, not just the page policy.
        Assert.Contains("BlockedSendersUnblock", razor);
        // Required module-version display.
        Assert.Contains("<ModuleVersion />", razor);
    }

    // ---- Address normalization (pure) -------------------------------------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NormalizeAddress_BlankReturnsNull(string? input)
    {
        Assert.Null(BlockedSenderService.NormalizeAddress(input));
    }

    [Fact]
    public void NormalizeAddress_TrimsWhitespace()
    {
        Assert.Equal("user@contoso.com", BlockedSenderService.NormalizeAddress("  user@contoso.com  "));
    }

    // ---- PSObject mapping (pure, null-tolerant) ---------------------------------------------

    [Fact]
    public void FromPSObject_MapsAllProperties()
    {
        var ps = new PSObject();
        ps.Properties.Add(new PSNoteProperty("SenderAddress", "spammer@contoso.com"));
        ps.Properties.Add(new PSNoteProperty("Reason", "OutboundSpam"));
        ps.Properties.Add(new PSNoteProperty("CreatedDatetime", "2026-06-29T12:00:00Z"));

        var info = BlockedSenderInfo.FromPSObject(ps);

        Assert.NotNull(info);
        Assert.Equal("spammer@contoso.com", info!.SenderAddress);
        Assert.Equal("OutboundSpam", info.Reason);
        Assert.Equal("2026-06-29T12:00:00Z", info.BlockedDateRaw);
    }

    [Fact]
    public void FromPSObject_MissingOptionalProperties_DoesNotThrow()
    {
        var ps = new PSObject();
        ps.Properties.Add(new PSNoteProperty("SenderAddress", "spammer@contoso.com"));

        var info = BlockedSenderInfo.FromPSObject(ps);

        Assert.NotNull(info);
        Assert.Equal("spammer@contoso.com", info!.SenderAddress);
        Assert.Null(info.Reason);
        Assert.Null(info.BlockedDateRaw);
    }

    [Fact]
    public void FromPSObject_NoSenderAddress_ReturnsNull()
    {
        var ps = new PSObject();
        ps.Properties.Add(new PSNoteProperty("Reason", "OutboundSpam"));

        Assert.Null(BlockedSenderInfo.FromPSObject(ps));
    }

    [Fact]
    public void FromPSObject_NullInput_ReturnsNull()
    {
        Assert.Null(BlockedSenderInfo.FromPSObject(null));
    }

    private static string FindPage(string fileName)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "Components", "Pages", fileName);
            if (File.Exists(candidate))
                return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException($"Could not locate {fileName} under Components/Pages.");
    }
}
