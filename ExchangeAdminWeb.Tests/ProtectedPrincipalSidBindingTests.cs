using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md C.2 (slice S3), end to end through the real validator and protected-principal
/// service over the stand-in forest: a synced mailbox is bound to the one account holding the on-premises SID Graph reports,
/// read at the global catalog, so a mailbox whose account is in another domain of the forest resolves and is checked in its
/// own domain. Nothing here reaches a directory, Exchange, Graph or the secret store.
/// </summary>
public sealed class ProtectedPrincipalSidBindingTests : IDisposable
{
    private const string Target = "far@contoso.test";
    private static readonly Guid TargetMailbox = Guid.Parse("0f0f0f0f-7777-4888-8999-bbbbbbbbbbbb");
    private const string FabrikamSid = "S-1-5-21-444444444-555555555-666666666";

    private readonly ExchangeWriteHarness _h = new();
    private readonly SelfGrantDirectoryStandIn.Entry _far;

    public ProtectedPrincipalSidBindingTests()
    {
        _h.AddMailbox(Target, TargetMailbox);
        // The mailbox's account lives in the other domain, under another UPN: the session's own domain holds nothing under
        // the typed address, so only the catalog read by SID can find it.
        // A domain SID whose sub-authorities fit a SID as Active Directory stores it, so the SID Graph reports and the one the
        // catalog returns read the same; the other domain's Domain Users under it, the account's primary group.
        _h.Forest.AddGroup("Fabrikam Domain Users", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: FabrikamSid,
            rid: SelfGrantDirectoryStandIn.DomainUsersRid);
        _far = _h.Forest.AddUser("far", upn: "far@fabrikam.test", mail: Target,
            domainDn: SelfGrantDirectoryStandIn.OtherDomainDn, domainSid: FabrikamSid, proxies: [$"SMTP:{Target}"]);
        _h.GraphSids["ext-" + Target] = _far.Sid;
    }

    public void Dispose() => _h.Dispose();

    private IIdentityResolver ExchangeKnowsTheSyncedTarget()
    {
        var resolver = ExchangeWriteHarness.ExchangeKnows(Target, Target, TargetMailbox);
        resolver.ResolveRecipientAsync(Target).Returns(
            new ResolvedRecipient(Target, "ext-" + Target, "UserMailbox", ExistsOnPrem: true, ExchangeGuid: TargetMailbox));
        return resolver;
    }

    [Fact]
    public async Task ATargetInAnotherDomain_IsBoundAtTheCatalog_AndCarriesItsAccountToTheWrite()
    {
        var unrelated = _h.Forest.AddGroup("Unrelated", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn,
            domainSid: FabrikamSid);
        var validator = _h.ProtectedValidator(ExchangeKnowsTheSyncedTarget(), new ProtectedPrincipalConfig { Groups = [unrelated.Dn] });

        var validation = await validator.ValidateTargetMailboxAsync(Target, _h.Operator, "MailboxPermissions");

        Assert.Null(validation.Error);
        Assert.Equal(TargetMailbox, validation.Mailbox!.ExchangeGuid);
        // aud-a2d, kept: the write is bound to the objectGUID of the account found by SID.
        Assert.Equal(_far.Guid.ToString(), validation.Mailbox.DirectoryObjectGuid);
        Assert.Contains(_h.Forest.Searches,
            read => read == $"Get-ADObject {SelfGrantDirectoryStandIn.GlobalCatalogEndpoint} (objectSid={_far.Sid})");
        Assert.Equal(1, _h.DirectoryReadFetches);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task ATargetInAnotherDomain_IsCheckedInItsOwnDomain_SoItsProtectionHolds()
    {
        // The account's domain - from the DN the catalog returned - is where its groups are read: a protected group of that
        // domain holding it protects it. Bound by the address in the session's own domain, it was not found at all.
        var vip = _h.Forest.AddGroup("Fabrikam VIPs", domainDn: SelfGrantDirectoryStandIn.OtherDomainDn,
            domainSid: FabrikamSid, members: [_far]);
        var validator = _h.ProtectedValidator(ExchangeKnowsTheSyncedTarget(), new ProtectedPrincipalConfig { Groups = [vip.Dn] });

        var validation = await validator.ValidateTargetMailboxAsync(Target, _h.Operator, "MailboxPermissions");

        Assert.NotNull(validation.Error);
        Assert.Contains("is protected", validation.Error, StringComparison.Ordinal);
        var otherDomain = SelfGrantDirectoryStandIn.DomainName(SelfGrantDirectoryStandIn.OtherDomainDn);
        Assert.Contains(_h.Forest.Searches, read => read.StartsWith($"Get-ADObject {otherDomain} ", StringComparison.Ordinal));
        Assert.Equal(1, _h.DirectoryReadFetches);
    }

    [Fact]
    public async Task GraphUnanswered_RefusesTheTarget_AsUnavailable_WithNoReadBySid()
    {
        _h.GraphSids.Remove("ext-" + Target);
        var validator = _h.ProtectedValidator(ExchangeKnowsTheSyncedTarget(), new ProtectedPrincipalConfig { Users = ["someone-else@contoso.test"] });

        var validation = await validator.ValidateTargetMailboxAsync(Target, _h.Operator, "MailboxPermissions");

        Assert.Contains("identity resolution is unavailable", validation.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(_h.Forest.Searches, read => read.Contains("(objectSid=", StringComparison.Ordinal));
        Assert.Equal(1, _h.GraphSidReads);
    }
}

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md C.2, "Retired": the aud-a2c mailbox-GUID proof - a search of the forest for
/// whichever objects carry a mailbox GUID in <c>msExchMailboxGuid</c> - is gone, replaced by the binding by on-premises SID.
/// A source scan, so the walk cannot quietly come back under its old names.
/// </summary>
public sealed class RetiredMailboxGuidProofTripwireTests
{
    private static readonly string[] Retired =
    [
        "ProveMailboxBackingAsync",
        "SelectMailboxBacking",
        "FindMailboxGuidHoldersAsync",
        "FindMailboxGuidHoldersViaActiveDirectory",
        "ParseMailboxGuid",
        "BuildMailboxGuidFilter",
        "MailboxGuidHolder",
        "no Active Directory object holds this mailbox GUID",
    ];

    [Fact]
    public void TheMailboxGuidProof_IsGone_FromTheAppAndItsTests()
    {
        var root = Path.GetDirectoryName(AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.csproj"))!;
        var self = Path.GetFullPath(AuditCategoryFilingTests.FindRepoFile("ExchangeAdminWeb.Tests", "ProtectedPrincipalSidBindingTests.cs"));
        var files = new[] { "Services", "Components", "Modules", "ExchangeAdminWeb.Tests" }
            .Select(dir => Path.Combine(root, dir))
            .Where(Directory.Exists)
            .SelectMany(dir => Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories))
            .Where(file => file.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".razor", StringComparison.OrdinalIgnoreCase))
            .Where(file => !file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                           && !file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            .Where(file => !string.Equals(Path.GetFullPath(file), self, StringComparison.OrdinalIgnoreCase))
            .Append(Path.Combine(root, "Program.cs"))
            .ToList();
        Assert.True(files.Count > 100, "the scan found too few files - tripwire is stale");

        var found = files
            .SelectMany(file => Retired
                .Where(name => File.ReadAllText(file).Contains(name, StringComparison.Ordinal))
                .Select(name => $"{Path.GetRelativePath(root, file)}: {name}"))
            .ToList();
        Assert.Empty(found);

        // And no directory search for a mailbox GUID: the service never builds an msExchMailboxGuid filter.
        var service = File.ReadAllText(Path.Combine(root, "Services", "ProtectedPrincipalService.cs"));
        Assert.DoesNotMatch(new Regex(@"\(msExchMailboxGuid=", RegexOptions.None, TimeSpan.FromSeconds(2)), service);
    }
}
