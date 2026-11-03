using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md K.3 point 1 (slice S6): <c>db convert-identifiers</c> and
/// <c>db identifier-status</c>. The lookups' decisions over a fake directory (re-homed from the startup passes' tests and from
/// the evaluator's name-resolution tests, which moved here with the code); the verbs end to end against a real SQLite file;
/// the invoking-user directory over the stand-in forest.
/// </summary>
public sealed class ConfigDbIdentifierTests : IDisposable
{
    private const string AliceSid = "S-1-5-21-1000000000-1000000000-1000000000-1105";
    private const string BobSid = "S-1-5-21-1000000000-1000000000-1000000000-1106";
    private const string Alice = "alice@contoso.com";
    private const string GroupSid = "S-1-5-21-1000000000-1000000000-1000000000-2001";
    private const string GroupDn = "CN=Tier0 Admins,OU=Groups,DC=contoso,DC=com";
    private const string OuGuid = "0b5e3c2a-7d41-4f8e-9c6a-2e1f3d4b5a69";
    private const string OuDn = "OU=Tier0,DC=contoso,DC=com";
    private const string ModuleA = "MailboxPermissions";
    private const string ModuleB = "CalendarPermissions";
    private const string ContosoOpsSid = "S-1-5-21-1000000000-1000000000-1000000000-3001";
    private const string FabrikamOpsSid = "S-1-5-21-2000000000-2000000000-2000000000-3001";
    private const string ContosoOpsDn = "CN=Ops,OU=Groups,DC=contoso,DC=com";
    private const string FabrikamOpsDn = "CN=Ops,OU=Groups,DC=fabrikam,DC=com";
    private const string PickedSid = "S-1-5-21-1000000000-1000000000-1000000000-3999";

    private readonly TempDir _temp = new();
    private readonly SqliteConfigStore _store;

    public ConfigDbIdentifierTests() => _store = TestConfigStore.Create(_temp.Path);

    public void Dispose()
    {

        _temp.Dispose();
    }

    // ----- the lookups: one per row, exactly one match converts (B.4 step 3) -----

    [Fact]
    public void EachKind_ConvertsOnExactlyOneMatch()
    {
        var directory = StandardDirectory();
        directory.Users["(sAMAccountName=bob)|contoso.com"] = [new("CN=Bob,OU=Staff,DC=contoso,DC=com", BobSid)];

        var resolved = ProtectedPrincipalIdentifierResolver.Resolve(
        [
            new(ProtectedPrincipalRepository.KindUser, Alice),
            new(ProtectedPrincipalRepository.KindUser, @"CONTOSO\bob"),
            new(ProtectedPrincipalRepository.KindGroup, GroupDn),
            new(ProtectedPrincipalRepository.KindOu, OuDn),
        ], directory, CancellationToken.None);

        Assert.Equal([AliceSid, BobSid, GroupSid, OuGuid], resolved.Select(r => r.Identifier));
        Assert.Equal(
            [ProtectedPrincipalRepository.KindUserSid, ProtectedPrincipalRepository.KindUserSid,
             ProtectedPrincipalRepository.KindGroupSid, ProtectedPrincipalRepository.KindOuGuid],
            resolved.Select(r => r.IdKind));
        // DOMAIN\name is asked of the domain it names, never the forest.
        Assert.Contains("FindUsers contoso.com", directory.Calls);
        // A user is always a person: a computer account, also of class user, is never the match.
        Assert.All(directory.Calls.Where(c => c.StartsWith("filter ", StringComparison.Ordinal)),
            c => Assert.Contains(ProtectedPrincipalIdentifierResolver.PersonFilter, c));
    }

    [Fact]
    public void ZeroManyAndUnavailable_AreNamed_WithTheReason()
    {
        var directory = new FakeDirectory();
        directory.Users["(userPrincipalName=two@contoso.com)"] =
            [new("CN=A,DC=contoso,DC=com", AliceSid), new("CN=B,DC=contoso,DC=com", BobSid)];
        directory.Groups[(GroupLookupKey.SamAccountName, "Ops", "(catalog)")] =
            [new(ContosoOpsDn, GroupSid, DirectoryGroupScope.Global), new(FabrikamOpsDn, BobSid, DirectoryGroupScope.Global)];
        directory.Throws[OuDn] = new InvalidOperationException("Unable to contact the server.");

        var resolved = ProtectedPrincipalIdentifierResolver.Resolve(
        [
            new(ProtectedPrincipalRepository.KindUser, "nobody@contoso.com"),
            new(ProtectedPrincipalRepository.KindUser, "two@contoso.com"),
            new(ProtectedPrincipalRepository.KindGroup, "Ops"),
            new(ProtectedPrincipalRepository.KindGroup, "S-1-5-32-544"),
            new(ProtectedPrincipalRepository.KindOu, OuDn),
            new(ProtectedPrincipalRepository.KindOu, "OU=Missing,DC=contoso,DC=com"),
        ], directory, CancellationToken.None);

        Assert.All(resolved, r => Assert.Null(r.Identifier));
        Assert.Equal(
            [ProtectedPrincipalLeftReason.NotFound, ProtectedPrincipalLeftReason.Ambiguous, ProtectedPrincipalLeftReason.Ambiguous,
             ProtectedPrincipalLeftReason.WellKnownSid, ProtectedPrincipalLeftReason.DirectoryUnavailable, ProtectedPrincipalLeftReason.NotFound],
            resolved.Select(r => r.Reason!.Value));
        Assert.Equal("matches 2 objects", resolved[1].Detail);
        Assert.Contains("Unable to contact the server", resolved[4].Detail);
    }

    // ----- group entries: the name resolution moved here from the evaluator (aud-a3b hole 3) -----

    [Fact]
    public void ADomainQualifiedGroupEntry_ResolvesInTheNamedDomain_NotAnother()
    {
        var directory = TwoDomainDirectory();

        var contoso = ProtectedPrincipalIdentifierResolver.ResolveGroupEntry(ProtectedGroupEntry.Parse(@"CONTOSO\Ops"), directory, out _);
        var fabrikam = ProtectedPrincipalIdentifierResolver.ResolveGroupEntry(ProtectedGroupEntry.Parse(@"FABRIKAM\Ops"), directory, out _);

        Assert.Equal(ContosoOpsSid, contoso?.Sid);
        Assert.Equal(FabrikamOpsSid, fabrikam?.Sid);
        Assert.Contains("FindGroups SamAccountName Ops fabrikam.com", directory.Calls);
    }

    [Fact]
    public void ABareGroupNameTwoDomainsShare_IsAmbiguous_AndAUniqueOne_IsTheForests()
    {
        var directory = TwoDomainDirectory();
        directory.Groups[(GroupLookupKey.SamAccountName, "Tier0 Admins", "(catalog)")] = [new(GroupDn, GroupSid, DirectoryGroupScope.Universal)];

        var shared = ProtectedPrincipalIdentifierResolver.ResolveGroupEntry(ProtectedGroupEntry.Parse("Ops"), directory, out var sharedProblem);
        var unique = ProtectedPrincipalIdentifierResolver.ResolveGroupEntry(ProtectedGroupEntry.Parse("Tier0 Admins"), directory, out _);

        Assert.Null(shared);
        Assert.Equal("2 groups match", sharedProblem);
        Assert.Equal(GroupSid, unique?.Sid);
        Assert.Contains("FindGroups SamAccountName Tier0 Admins (catalog)", directory.Calls);
    }

    [Fact]
    public void AGroupName_FallsBackToTheCommonName_OnlyWhenNoSamAccountNameMatches()
    {
        var directory = TwoDomainDirectory();
        directory.Groups[(GroupLookupKey.CommonName, "Tier Zero", "(catalog)")] = [new(GroupDn, GroupSid, DirectoryGroupScope.Global)];

        Assert.Equal(GroupSid, ProtectedPrincipalIdentifierResolver.ResolveGroupEntry(ProtectedGroupEntry.Parse("Tier Zero"), directory, out _)?.Sid);
        ProtectedPrincipalIdentifierResolver.ResolveGroupEntry(ProtectedGroupEntry.Parse(@"CONTOSO\Ops"), directory, out _);
        Assert.DoesNotContain("FindGroups CommonName Ops contoso.com", directory.Calls);
    }

    [Fact]
    public void AGroupEntryForADomainTheForestLacks_CouldNotBeLookedUp_NeverAnotherDomain()
    {
        var resolved = ProtectedPrincipalIdentifierResolver.Resolve(
            [new(ProtectedPrincipalRepository.KindGroup, @"NOWHERE\Ops")], TwoDomainDirectory(), CancellationToken.None);

        Assert.Equal(ProtectedPrincipalLeftReason.DirectoryUnavailable, Assert.Single(resolved).Reason);
    }

    [Fact]
    public void AGroupDnThatNoLongerExists_IsNotFound()
    {
        var resolved = ProtectedPrincipalIdentifierResolver.Resolve(
            [new(ProtectedPrincipalRepository.KindGroup, "CN=Gone,OU=Groups,DC=contoso,DC=com")], TwoDomainDirectory(), CancellationToken.None);

        Assert.Equal(ProtectedPrincipalLeftReason.NotFound, Assert.Single(resolved).Reason);
    }

    // ----- module-admin grants (B.6) -----

    [Fact]
    public void ADomainQualifiedGrant_ResolvesOnlyInItsDomain_NotToASameNamedGroupOfAnotherDomain()
    {
        var directory = TwoDomainDirectory();

        var resolved = ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants(
            ["FABRIKAM\\Ops", "CONTOSO\\Ops", "Ops"], directory, CancellationToken.None);

        Assert.Equal(FabrikamOpsSid, resolved[0].Identifier);
        Assert.Equal(ContosoOpsSid, resolved[1].Identifier);
        // A bare name is the forest's, and two domains carry it: ambiguous, never whichever answered first.
        Assert.Null(resolved[2].Identifier);
        Assert.Equal(ProtectedPrincipalLeftReason.Ambiguous, resolved[2].Reason);
        Assert.Contains("FindGroups AnyGroupName Ops fabrikam.com", directory.Calls);
        Assert.Contains("FindGroups AnyGroupName Ops contoso.com", directory.Calls);
        Assert.Contains("FindGroups AnyGroupName Ops (catalog)", directory.Calls);
    }

    [Fact]
    public void ZeroManyUnavailableAndWellKnownGrants_AreNamed_WithTheirReason()
    {
        var directory = TwoDomainDirectory();
        directory.Groups[(GroupLookupKey.AnyGroupName, "Administrators", "(catalog)")] =
            [new("CN=Administrators,CN=Builtin,DC=contoso,DC=com", "S-1-5-32-544", DirectoryGroupScope.DomainLocal)];
        directory.Throws["Flaky"] = new InvalidOperationException("Unable to contact the server.");

        var resolved = ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants(
            ["Nobody", "Ops", "Flaky", "Administrators", "BUILTIN\\Administrators", "S-1-5-32-544", "NOSUCH\\Ops"],
            directory, CancellationToken.None);

        Assert.All(resolved, r => Assert.Null(r.Identifier));
        Assert.Equal(
            [ProtectedPrincipalLeftReason.NotFound, ProtectedPrincipalLeftReason.Ambiguous, ProtectedPrincipalLeftReason.DirectoryUnavailable,
             ProtectedPrincipalLeftReason.WellKnownSid, ProtectedPrincipalLeftReason.WellKnownSid, ProtectedPrincipalLeftReason.WellKnownSid,
             ProtectedPrincipalLeftReason.DirectoryUnavailable],
            resolved.Select(r => r.Reason!.Value));
        Assert.Equal($"2 groups match: {ContosoOpsDn}; {FabrikamOpsDn}", resolved[1].Detail);
        // A BUILTIN prefix and a well-known SID are refused without a lookup.
        Assert.DoesNotContain(directory.Calls, c => c.EndsWith(" S-1-5-32-544 (catalog)", StringComparison.Ordinal));
        Assert.DoesNotContain("domain BUILTIN", directory.Calls);
    }

    // ----- db convert-identifiers, end to end against a real file -----

    [Fact]
    public void Plan_ReportsEachConversion_AndWritesNothing()
    {
        SeedRules(users: [Alice], groups: [GroupDn], ous: [OuDn]);
        SeedAdmins(ModuleA, @"CONTOSO\Ops", PickedSid);
        var before = ReadRules();
        var token = _store.GetChangeToken();

        var result = Run(StandardDirectory(), "convert-identifiers", _temp.DbPath, "--plan");

        Assert.Equal(ConfigDbCli.ExitOk, result.Exit);
        Assert.Equal(
        [
            "plan: nothing written; a real run would convert:",
            $"user: {Alice} -> {AliceSid}",
            $"group: {GroupDn} -> {GroupSid}",
            $"ou: {OuDn} -> {OuGuid}",
            $"module-admin {ModuleA}: CONTOSO\\Ops -> {ContosoOpsSid}",
            "would convert 4",
        ], result.OutLines);
        Assert.Equivalent(before, ReadRules());
        Assert.Equal([@"CONTOSO\Ops", PickedSid], Admins(ModuleA).Order(StringComparer.Ordinal));
        Assert.Equal(token, _store.GetChangeToken());
    }

    [Fact]
    public void ARealRun_AddsEachIdentifierBesideItsLegacyRow_ReplacesEachNameGrant_AndASecondRunFindsNothing()
    {
        SeedRules(users: [Alice], groups: [GroupDn], ous: [OuDn]);
        SeedAdmins(ModuleA, @"CONTOSO\Ops", PickedSid);
        SeedAdmins(ModuleB, @"CONTOSO\Ops");
        var token = _store.GetChangeToken();

        var first = Run(StandardDirectory(), "convert-identifiers", _temp.DbPath);

        Assert.Equal(ConfigDbCli.ExitOk, first.Exit);
        Assert.Contains($"user: {Alice} -> {AliceSid}", first.OutLines);
        Assert.Contains($"module-admin {ModuleB}: CONTOSO\\Ops -> {ContosoOpsSid}", first.OutLines);
        Assert.Equal("converted 5", first.OutLines[^1]);
        var data = ReadRules();
        // The legacy rows are untouched: an older build sharing the database keeps enforcing them (B.1).
        Assert.Equal([Alice], data.Users);
        Assert.Equal([GroupDn], data.Groups);
        Assert.Equal([OuDn], data.OrganizationalUnits);
        Assert.Equal([ProtectedPrincipalIdEntry.Format(AliceSid, Alice)], data.UserIds);
        Assert.Equal([ProtectedPrincipalIdEntry.Format(GroupSid, GroupDn)], data.GroupIds);
        Assert.Equal([ProtectedPrincipalIdEntry.Format(OuGuid, OuDn)], data.OuIds);
        Assert.Equal([ContosoOpsSid, PickedSid], Admins(ModuleA).Order(StringComparer.Ordinal));
        Assert.Equal([ContosoOpsSid], Admins(ModuleB));
        Assert.NotEqual(token, _store.GetChangeToken()); // a running instance re-reads

        var second = Run(StandardDirectory(), "convert-identifiers", _temp.DbPath);

        Assert.Equal(ConfigDbCli.ExitOk, second.Exit);
        Assert.StartsWith("nothing to convert", Assert.Single(second.OutLines), StringComparison.Ordinal);
    }

    [Fact]
    public void OneEntryThatDoesNotResolve_WritesNothing_AndEveryOffendingEntryIsNamed()
    {
        SeedRules(users: [Alice, "nobody@contoso.com"], groups: [GroupDn, "Ops"], ous: []);
        SeedAdmins(ModuleA, "Nobody");
        var before = ReadRules();
        var directory = StandardDirectory();
        directory.Groups[(GroupLookupKey.SamAccountName, "Ops", "(catalog)")] =
            [new(ContosoOpsDn, ContosoOpsSid, DirectoryGroupScope.Global), new(FabrikamOpsDn, FabrikamOpsSid, DirectoryGroupScope.Global)];

        foreach (var args in new[] { new[] { "convert-identifiers", _temp.DbPath }, ["convert-identifiers", _temp.DbPath, "--plan"] })
        {
            var result = Run(directory, args);

            Assert.Equal(ConfigDbCli.ExitFailure, result.Exit);
            Assert.Equal(string.Empty, result.Out);
            Assert.Contains("user: nobody@contoso.com - not found", result.Err, StringComparison.Ordinal);
            Assert.Contains("group: Ops - 2 groups match", result.Err, StringComparison.Ordinal);
            Assert.Contains($"module-admin {ModuleA}: Nobody - not found", result.Err, StringComparison.Ordinal);
            Assert.Contains("error: 3 of 5 entries do not resolve to exactly one object; nothing was written.", result.Err, StringComparison.Ordinal);
            Assert.Equivalent(before, ReadRules());
            Assert.Equal(["Nobody"], Admins(ModuleA));
        }
    }

    [Fact]
    public void ADirectoryThatCannotBeOpened_WritesNothing_AndNamesEveryEntry()
    {
        SeedRules(users: [Alice], groups: [], ous: []);

        var result = Run(() => throw new InvalidOperationException("The ActiveDirectory module could not be loaded."),
            "convert-identifiers", _temp.DbPath);

        Assert.Equal(ConfigDbCli.ExitFailure, result.Exit);
        Assert.Contains($"user: {Alice} - directory unavailable: The ActiveDirectory module could not be loaded.", result.Err, StringComparison.Ordinal);
        Assert.Empty(ReadRules().UserIds);
    }

    [Fact]
    public void ARowChangedWhileTheLookupsRan_WritesNothing_AndSaysToRunAgain()
    {
        SeedRules(users: [Alice], groups: [GroupDn], ous: []);
        var directory = StandardDirectory();
        // Inside the lookups, with no lock held: an older build on the shared database saves, adding a row.
        directory.During = () => SeedRules(users: [Alice, "carol@contoso.com"], groups: [GroupDn], ous: []);

        var result = Run(directory, "convert-identifiers", _temp.DbPath);

        Assert.Equal(ConfigDbCli.ExitFailure, result.Exit);
        Assert.Contains("changed while the lookups ran; nothing was written. Run this again.", result.Err, StringComparison.Ordinal);
        Assert.Empty(ReadRules().UserIds);
        Assert.Empty(ReadRules().GroupIds);
    }

    [Fact]
    public void NothingToConvert_NeverOpensTheDirectory()
    {
        SeedRules(users: [], groups: [], ous: []);
        SeedAdmins(ModuleA, PickedSid);

        var result = Run(() => throw new InvalidOperationException("must not be opened"), "convert-identifiers", _temp.DbPath, "--plan");

        Assert.Equal(ConfigDbCli.ExitOk, result.Exit);
        Assert.StartsWith("nothing to convert", Assert.Single(result.OutLines), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("convert-identifiers")]
    [InlineData("convert-identifiers --plan")]
    [InlineData("convert-identifiers a.db --plan --plan")]
    [InlineData("convert-identifiers a.db b.db")]
    [InlineData("convert-identifiers a.db --force")]
    [InlineData("identifier-status")]
    [InlineData("identifier-status a.db --plan")]
    public void AnyOtherArgumentShape_IsUsage(string argLine)
    {
        var result = Run(StandardDirectory(), argLine.Split(' '));

        Assert.Equal(ConfigDbCli.ExitUsage, result.Exit);
        Assert.Contains("db convert-identifiers <db> [--plan]", result.Err, StringComparison.Ordinal);
        Assert.Contains("db identifier-status <db>", result.Err, StringComparison.Ordinal);
    }

    [Fact]
    public void AMissingDatabase_IsExitTwo_AndNothingIsCreated()
    {
        var missing = Path.Combine(_temp.Path, "absent.db");

        Assert.Equal(ConfigDbCli.ExitFailure, Run(StandardDirectory(), "convert-identifiers", missing).Exit);
        Assert.Equal(ConfigDbCli.ExitFailure, Run(StandardDirectory(), "identifier-status", missing).Exit);
        Assert.False(File.Exists(missing));
    }

    // ----- db identifier-status -----

    [Fact]
    public void Status_ListsEveryRowAndGrantInItsState_AndExitsTwoWhileAnyIsStoredByName()
    {
        new ProtectedPrincipalRepository(_store).Save(ProtectedPrincipalData.Empty with
        {
            Users = [Alice, "nobody@contoso.com"],
            UserIds = [ProtectedPrincipalIdEntry.Format(AliceSid, Alice)],
            GroupIds = [ProtectedPrincipalIdEntry.Format(GroupSid, GroupDn)],
            SamAccountNamePatterns = ["svc-*"],
        });
        SeedAdmins(ModuleA, @"CONTOSO\Ops", PickedSid);
        var token = _store.GetChangeToken();

        var result = Run(StandardDirectory(), "identifier-status", _temp.DbPath);

        Assert.Equal(ConfigDbCli.ExitFailure, result.Exit);
        Assert.Equal(string.Empty, result.Out);
        var lines = result.Err.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
        Assert.Equal(
        [
            $"converted user_sid: {AliceSid}|{Alice}",
            "legacy user: nobody@contoso.com",
            $"identifier group_sid: {GroupSid}|{GroupDn}",
            $"grant-name {ModuleA}: CONTOSO\\Ops",
            $"grant-sid {ModuleA}: {PickedSid}",
            "1 converted, 1 identifier, 1 legacy, 1 grant-sid, 1 grant-name",
            "error: 2 entries are still stored by name; run db convert-identifiers",
        ], lines);
        Assert.Equal(token, _store.GetChangeToken());
    }

    [Fact]
    public void Status_AfterAConversion_IsExitZero_OnStdout()
    {
        SeedRules(users: [Alice], groups: [GroupDn], ous: [OuDn]);
        SeedAdmins(ModuleA, @"CONTOSO\Ops");
        Assert.Equal(ConfigDbCli.ExitOk, Run(StandardDirectory(), "convert-identifiers", _temp.DbPath).Exit);

        var result = Run(StandardDirectory(), "identifier-status", _temp.DbPath);

        Assert.Equal(ConfigDbCli.ExitOk, result.Exit);
        Assert.Equal(string.Empty, result.Err);
        Assert.Equal("3 converted, 0 identifier, 0 legacy, 1 grant-sid, 0 grant-name", result.OutLines[^1]);
    }

    // ----- the invoking user's directory, over the stand-in forest -----

    [Fact]
    public void TheVerbsDirectory_ReadsAsTheInvokingUser_AtTheHostForestsCatalog_GroupsOnly()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var group = forest.Add(new SelfGrantDirectoryStandIn.Entry
        {
            Dn = "CN=Operations Team,OU=Groups," + SelfGrantDirectoryStandIn.DomainDn,
            Class = "group",
            Sam = "ops-sam",
            DisplayName = "Operations (display)",
            Sid = SelfGrantDirectoryStandIn.DomainSid + "-7001",
        });
        // A user carrying the same name is never the match: groups only.
        forest.AddUser("ops-user", nickname: null);

        IReadOnlyList<ProtectedPrincipalRowResolution> resolved;
        using (var directory = new InvokingUserIdentifierDirectory(forest.Open))
            resolved = ProtectedPrincipalIdentifierResolver.ResolveModuleAdminGrants(
                ["ops-sam", "Operations Team", "Operations (display)", "ops-user"], directory, CancellationToken.None);

        Assert.Equal([group.Sid, group.Sid, group.Sid, null], resolved.Select(r => r.Identifier));
        Assert.Equal(ProtectedPrincipalLeftReason.NotFound, resolved[3].Reason);
        Assert.Empty(forest.Errors);
        // No credential on any read: the db mode has no host and fetches none (K.3 point 1).
        Assert.All(forest.CallsAs, call => Assert.Null(call.As));
        Assert.Contains(forest.CallsAs, call => call.Call == "Get-ADForest");
        Assert.All(forest.CallsAs.Where(call => call.Call.StartsWith("Get-ADGroup", StringComparison.Ordinal)),
            call => Assert.StartsWith("Get-ADGroup " + SelfGrantDirectoryStandIn.GlobalCatalogEndpoint + " ", call.Call, StringComparison.Ordinal));
    }

    // ----- harness -----

    private sealed record CliResult(int Exit, string Out, string Err)
    {
        public string[] OutLines => Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.TrimEnd('\r')).ToArray();
    }

    private static CliResult Run(FakeDirectory directory, params string[] args) => Run(() => directory, args);

    private static CliResult Run(Func<IProtectedPrincipalResolveDirectory> openDirectory, params string[] args)
    {
        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var exit = ConfigDbCli.Run(args, stdout, stderr, openDirectory);
        return new CliResult(exit, stdout.ToString(), stderr.ToString());
    }

    private void SeedRules(string[] users, string[] groups, string[] ous) =>
        new ProtectedPrincipalRepository(_store).Save(ProtectedPrincipalData.Empty with { Users = users, Groups = groups, OrganizationalUnits = ous });

    private void SeedAdmins(string moduleId, params string[] grants) => new ModuleAdminRepository(_store).SetForModule(moduleId, grants);

    private string[] Admins(string moduleId) => new ModuleAdminRepository(_store).GetForModule(moduleId);

    private ProtectedPrincipalData ReadRules()
    {
        Assert.True(new ProtectedPrincipalRepository(_store).TryRead(out var data, out _));
        return data;
    }

    private static FakeDirectory TwoDomainDirectory()
    {
        var directory = new FakeDirectory();
        directory.Domains["CONTOSO"] = "contoso.com";
        directory.Domains["FABRIKAM"] = "fabrikam.com";
        var contosoOps = new DirectoryGroup(ContosoOpsDn, ContosoOpsSid, DirectoryGroupScope.Global);
        var fabrikamOps = new DirectoryGroup(FabrikamOpsDn, FabrikamOpsSid, DirectoryGroupScope.Global);
        foreach (var key in new[] { GroupLookupKey.SamAccountName, GroupLookupKey.AnyGroupName })
        {
            directory.Groups[(key, "Ops", "contoso.com")] = [contosoOps];
            directory.Groups[(key, "Ops", "fabrikam.com")] = [fabrikamOps];
            directory.Groups[(key, "Ops", "(catalog)")] = [contosoOps, fabrikamOps];
        }
        return directory;
    }

    private static FakeDirectory StandardDirectory()
    {
        var directory = TwoDomainDirectory();
        directory.Users["(userPrincipalName=alice@contoso.com)"] = [new("CN=Alice,OU=Staff,DC=contoso,DC=com", AliceSid)];
        directory.Groups[(GroupLookupKey.DistinguishedName, GroupDn, "contoso.com")] = [new(GroupDn, GroupSid, DirectoryGroupScope.Global)];
        directory.Ous[OuDn] = OuGuid;
        return directory;
    }

    /// <summary>Groups keyed by (key, value, server or "(catalog)"); users by the inner filter term, plus "|server".</summary>
    private sealed class FakeDirectory : IProtectedPrincipalResolveDirectory
    {
        public Dictionary<string, List<DirectoryUserObject>> Users { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<(GroupLookupKey, string, string), List<DirectoryGroup>> Groups { get; } = [];
        public Dictionary<string, string> Ous { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Domains { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, Exception> Throws { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Calls { get; } = [];
        public Action? During { get; set; }

        private void Once()
        {
            var during = During;
            During = null;
            during?.Invoke();
        }

        public IReadOnlyList<DirectoryUserObject> FindUsers(string ldapFilter, string? server)
        {
            Once();
            Calls.Add("filter " + ldapFilter);
            Calls.Add("FindUsers " + (server ?? "(catalog)"));
            foreach (var (key, users) in Users)
            {
                var parts = key.Split('|');
                if (ldapFilter.Contains(parts[0], StringComparison.OrdinalIgnoreCase)
                    && (parts.Length == 1 ? server is null : string.Equals(parts[1], server, StringComparison.OrdinalIgnoreCase)))
                    return users;
            }
            return [];
        }

        public string ResolveDomainServer(string domain)
        {
            Calls.Add("domain " + domain);
            return Domains.TryGetValue(domain, out var server) ? server : throw new InvalidOperationException($"No domain '{domain}'.");
        }

        public IReadOnlyList<DirectoryGroup> FindGroups(GroupLookupKey key, string value, string? server)
        {
            Once();
            Calls.Add($"FindGroups {key} {value} {server ?? "(catalog)"}");
            if (Throws.TryGetValue(value, out var ex))
                throw ex;
            return Groups.TryGetValue((key, value, server ?? "(catalog)"), out var groups) ? groups : [];
        }

        public IReadOnlyList<DirectoryObjectGuid> ReadObjectGuids(IReadOnlyList<string> dns, string server)
        {
            Once();
            if (Throws.TryGetValue(dns[0], out var ex))
                throw ex;
            return dns.Where(Ous.ContainsKey).Select(dn => new DirectoryObjectGuid(dn, Ous[dn])).ToList();
        }
    }
}

/// <summary>
/// docs/ProtectedPrincipalIdentifiers-Plan.md K.3 point 3 (slice S6): the protection check never resolves or matches a stored
/// name. A user, group or OU row with no identifier companion makes the check incomplete, naming the row and saying to run
/// the conversion; legacy Group rows stored as a SID, and identifier rows, are compared by identifier. Through the real
/// service over the stand-in forest.
/// </summary>
public sealed class ProtectedPrincipalNotConvertedTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), $"ppi-s6-test-{Guid.NewGuid():N}");

    public ProtectedPrincipalNotConvertedTests() => Directory.CreateDirectory(Path.Combine(_tempDir, "config"));

    public void Dispose()
    {

        try { Directory.Delete(_tempDir, true); } catch { }
    }

    [Theory]
    [InlineData("group", "CN=Tier0,OU=Groups," + SelfGrantDirectoryStandIn.DomainDn)]
    [InlineData("group", @"CONTOSO\Tier0")]
    [InlineData("user", "carol@contoso.test")]
    [InlineData("OU", "OU=Staff," + SelfGrantDirectoryStandIn.DomainDn)]
    public async Task ARowStoredByNameOnly_RefusesTheCheck_NamingIt_AndIsNeverMatchedByName(string noun, string value)
    {
        // The value names the target itself, or a group or OU it is in: a string match would have protected it. The new build
        // does not match it - it cannot say the target is not protected, and refuses, naming the row and the cure.
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        forest.AddGroup("Tier0", members: target);
        var fetches = 0;
        var service = Service(forest, () => fetches++);
        service.SaveConfig(noun switch
        {
            "group" => new ProtectedPrincipalConfig { Groups = [value] },
            "user" => new ProtectedPrincipalConfig { Users = [value] },
            _ => new ProtectedPrincipalConfig { OrganizationalUnits = [value] },
        });

        var result = await service.CheckAsync(Principal(target));

        Assert.True(result.CheckFailed, result.Reason);
        Assert.Contains($"protected {noun} '{value}': not converted - run db convert-identifiers", result.Reason, StringComparison.Ordinal);
        Assert.False(result.IsProtected);
        // Nothing was looked up for it: no group read, no credential fetch.
        Assert.Equal(0, fetches);
        Assert.DoesNotContain(forest.Calls.Cast<string>(), c => c.StartsWith("Get-ADGroup", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARowStoredByNameOnly_DoesNotCancelAMatchOnAnIdentifierRow()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var service = Service(forest, () => { });
        service.SaveConfig(new ProtectedPrincipalConfig
        {
            Users = ["someone@contoso.test", "carol@contoso.test"],
            UserIds = [ProtectedPrincipalIdEntry.Format(target.Sid!, "carol@contoso.test")],
        });

        var result = await service.CheckAsync(Principal(target));

        Assert.False(result.CheckFailed, result.Reason);
        Assert.True(result.IsProtected);
    }

    [Fact]
    public async Task ALegacyGroupRowStoredAsASid_IsComparedByThatSid()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var group = forest.AddGroup("Tier0", members: target);
        var service = Service(forest, () => { });
        service.SaveConfig(new ProtectedPrincipalConfig { Groups = [group.Sid!] });

        var result = await service.CheckAsync(Principal(target));

        Assert.False(result.CheckFailed, result.Reason);
        Assert.True(result.IsProtected);
    }

    [Fact]
    public async Task AGroupSidRowWhoseLookupFails_StillFailsTheCheckClosed_NamingTheRow()
    {
        // sweep-c item 17's banner, kept: a read that fails is no answer, and the refusal names the row by its label.
        var forest = new SelfGrantDirectoryStandIn();
        var target = forest.AddUser("carol", upn: "carol@contoso.test");
        var label = "CN=Unreadable,OU=Groups," + SelfGrantDirectoryStandIn.DomainDn;
        forest.Failures["Get-ADGroup"] = "The server is not operational.";
        var service = Service(forest, () => { });
        service.SaveConfig(new ProtectedPrincipalConfig
        {
            Groups = [label],
            GroupIds = [ProtectedPrincipalIdEntry.Format(SelfGrantDirectoryStandIn.DomainSid + "-9001", label)],
        });

        var result = await service.CheckAsync(Principal(target));

        Assert.True(result.CheckFailed, result.Reason);
        Assert.Contains($"protected group '{label}'", result.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("expansion errors", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void TheWriteTargetCheck_RefusesOnAUserRowStoredByNameOnly_InsteadOfMatchingItsText()
    {
        var forest = new SelfGrantDirectoryStandIn();
        var service = Service(forest, () => { });
        service.SaveConfig(new ProtectedPrincipalConfig { Users = ["ops@contoso.test"] });

        var result = service.CheckWriteTarget(new ResolvedDirectoryPrincipal(
            "Test", "Ops", "ops@contoso.test", "ops", "ops@contoso.test", "CN=Ops,OU=Groups," + SelfGrantDirectoryStandIn.DomainDn, null, null));

        Assert.True(result.CheckFailed, result.Reason);
        Assert.Contains("protected user 'ops@contoso.test': not converted - run db convert-identifiers", result.Reason, StringComparison.Ordinal);
    }

    private static ResolvedDirectoryPrincipal Principal(SelfGrantDirectoryStandIn.Entry target) =>
        new("Test", "carol", target.Upn!, target.Sam, target.Upn, target.Dn, null, null, ObjectSid: target.Sid);

    private ProtectedPrincipalService Service(SelfGrantDirectoryStandIn forest, Action onFetch)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Delinea:SecretServerUrl"] = "https://fake.local",
            ["Audit:LogRoot"] = _tempDir,
            ["Security:ProtectedPrincipalDirectoryReadSecretId"] = "4242",
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(_tempDir);
        var moduleConfig = new ModuleConfigService(
            new ModuleCatalog(), env, TestConfigStore.CreateModuleConfig(_tempDir), NullLogger<ModuleConfigService>.Instance);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(new HttpClient());
        var extLog = new ExtendedLogService(configuration, env, TestConfigStore.CreateAppSettings(_tempDir), NullLogger<ExtendedLogService>.Instance);
        var trace = new OperationTraceService(configuration, new JsonlLogService(configuration, NullLogger<JsonlLogService>.Instance));
        var delinea = new DelineaService(httpClientFactory, configuration, NullLogger<DelineaService>.Instance, extLog, trace);
        return new ProtectedPrincipalService(
            env, configuration, moduleConfig, TestConfigStore.CreateProtectedPrincipal(_tempDir), delinea,
            NullLogger<ProtectedPrincipalService>.Instance)
        {
            DirectoryRunspace = forest.Open,
            DirectoryReadSecretFetch = _ =>
            {
                onFetch();
                return Task.FromResult<(string username, string password, string domain)?>(("svc-read", "pw", "CONTOSO"));
            },
        };
    }
}
