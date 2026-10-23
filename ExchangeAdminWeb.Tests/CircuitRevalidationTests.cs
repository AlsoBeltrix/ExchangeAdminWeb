using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Authorization;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a29b: a circuit's group membership is re-derived from the directory while it is open, and
/// a circuit whose access was removed - or can no longer be confirmed - stops working.
/// </summary>
/// <remarks>
/// Every directory answer here comes from a substitute <see cref="IOperatorMembershipDirectory"/>;
/// nothing in this file reaches Active Directory. The section-access and module-admin stores are
/// the real services over a throwaway SQLite file, so the compared set is the one production
/// computes rather than a list the test made up.
/// </remarks>
public class CircuitRevalidationTests
{
    private const string DomainSid = "S-1-5-21-1000-2000-3000";
    private const string OperatorSid = DomainSid + "-1105";
    private const string OperatorName = @"CONTOSO\operator";
    private const string DomainUsersSid = DomainSid + "-513";
    private const string SectionGroupSid = DomainSid + "-5001";
    private const string ModuleAdminGroupSid = DomainSid + "-5002";
    private const string UnconsultedGroupSid = DomainSid + "-5003";
    private const string NamedAdminGroupSid = DomainSid + "-5004";
    private const string LaterGrantedGroupSid = DomainSid + "-5005";
    private const string OtherTeamSid = DomainSid + "-5006";
    private const string NamedAdminGroup = @"CONTOSO\App-Admins";

    // ------------------------------------------------------------------ the verdict

    [Fact]
    public async Task UnchangedMembership_StaysValid()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);

        var valid = await h.Provider().ValidateAsync(Operator(SectionGroupSid, DomainUsersSid), "Interval", Ct);

        Assert.True(valid);
        Assert.False(h.Revocation.IsRevoked);
        Assert.Empty(h.AuditEvents());
    }

    [Fact]
    public async Task RemovedConsultedSid_InvalidatesTheCircuit()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);

        var valid = await h.Provider().ValidateAsync(Operator(SectionGroupSid, DomainUsersSid), "Interval", Ct);

        Assert.False(valid);
        Assert.Equal(CircuitRevocationReason.MembershipChanged, h.Revocation.Reason);
    }

    [Fact]
    public async Task AddedSid_StaysValid()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.Grant("CalendarPermissions", LaterGrantedGroupSid);

        // The directory now reports a consulted group the token does not carry, and an
        // unconsulted one besides. Gaining access is not a reason to end a session.
        h.DirectoryReturns(enabled: true, SectionGroupSid, LaterGrantedGroupSid, UnconsultedGroupSid, DomainUsersSid);

        var valid = await h.Provider().ValidateAsync(Operator(SectionGroupSid, DomainUsersSid), "Interval", Ct);

        Assert.True(valid);
        Assert.False(h.Revocation.IsRevoked);
    }

    [Fact]
    public async Task RemovedSidThatNoGateConsults_StaysValid()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);

        var valid = await h.Provider().ValidateAsync(
            Operator(SectionGroupSid, UnconsultedGroupSid, DomainUsersSid), "Interval", Ct);

        Assert.True(valid);
    }

    [Fact]
    public async Task DisabledAccount_InvalidatesTheCircuit()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: false, SectionGroupSid, DomainUsersSid);

        var valid = await h.Provider().ValidateAsync(Operator(SectionGroupSid, DomainUsersSid), "Interval", Ct);

        Assert.False(valid);
        Assert.Equal(CircuitRevocationReason.AccountDisabled, h.Revocation.Reason);
    }

    [Fact]
    public async Task ThrowingResolver_InvalidatesTheCircuit()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken)
            .ThrowsAsync(new DirectoryUnavailableException("no domain controller answered"));

        var valid = await h.Provider().ValidateAsync(Operator(SectionGroupSid), "Interval", Ct);

        Assert.False(valid);
        Assert.Equal(CircuitRevocationReason.LookupFailed, h.Revocation.Reason);
    }

    [Fact]
    public async Task ResolverThatThrowsBeforeReturningATask_InvalidatesTheCircuit()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken)
            .Throws(new InvalidOperationException("the runspace could not be opened"));

        var valid = await h.Provider().ValidateAsync(Operator(SectionGroupSid), "Interval", Ct);

        Assert.False(valid);
        Assert.Equal(CircuitRevocationReason.LookupFailed, h.Revocation.Reason);
    }

    [Fact(Timeout = 20000)]
    public async Task ResolverThatNeverAnswers_InvalidatesWhenTheLookupTimesOut()
    {
        using var h = new Harness(lookupTimeout: TimeSpan.FromMilliseconds(250));
        h.Grant("MailboxPermissions", SectionGroupSid);
        var never = new TaskCompletionSource<OperatorDirectoryMembership>();
        h.Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken).Returns(never.Task);

        var valid = await h.Provider().ValidateAsync(Operator(SectionGroupSid), "Interval", Ct);

        Assert.False(valid);
        Assert.Equal(CircuitRevocationReason.LookupFailed, h.Revocation.Reason);
        var audit = Assert.Single(h.AuditEvents()).RootElement;
        Assert.Contains("did not answer", audit.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrincipalWithoutAnAccountSid_InvalidatesWithoutTouchingTheDirectory()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);

        var valid = await h.Provider().ValidateAsync(
            Principal(primarySid: null, OperatorName, [SectionGroupSid]), "Interval", Ct);

        Assert.False(valid);
        Assert.Equal(CircuitRevocationReason.LookupFailed, h.Revocation.Reason);
        _ = h.Directory.DidNotReceive().GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task OperatorHoldingNoConsultedGroup_IsNotLookedUp()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);

        var valid = await h.Provider().ValidateAsync(Operator(UnconsultedGroupSid, DomainUsersSid), "Interval", Ct);

        Assert.True(valid);
        _ = h.Directory.DidNotReceive().GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task TheLookupIsKeyedByTheAccountSidAndRoutedByTheAccountsOwnDomain()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);

        await h.Provider().ValidateAsync(Operator(SectionGroupSid), "Interval", Ct);

        _ = h.Directory.Received(1).GetMembershipAsync(OperatorSid, "CONTOSO", Arg.Any<CancellationToken>());
    }

    // ------------------------------------------------------------------ what is consulted

    [Fact]
    public async Task NameConfiguredAdminGroup_IsComparedByItsTranslatedSid()
    {
        using var h = new Harness(adminGroups: [NamedAdminGroup]);
        h.Directory.TranslateGroupName(NamedAdminGroup).Returns(NamedAdminGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);

        var valid = await h.Provider().ValidateAsync(Operator(NamedAdminGroupSid, DomainUsersSid), "Interval", Ct);

        Assert.False(valid);
        var audit = Assert.Single(h.AuditEvents()).RootElement;
        Assert.Equal(new[] { NamedAdminGroupSid }, RemovedSids(audit));
    }

    [Fact]
    public async Task SidConfiguredAllowedGroup_IsConsultedWithoutTranslation()
    {
        using var h = new Harness(allowedGroups: [SectionGroupSid]);
        h.DirectoryReturns(enabled: true, DomainUsersSid);

        var valid = await h.Provider().ValidateAsync(Operator(SectionGroupSid), "Interval", Ct);

        Assert.False(valid);
        h.Directory.DidNotReceiveWithAnyArgs().TranslateGroupName(default!);
    }

    [Fact]
    public async Task UnmappedNameConfiguredGroup_IsSkipped()
    {
        using var h = new Harness(adminGroups: [NamedAdminGroup]);
        h.Directory.TranslateGroupName(NamedAdminGroup).Returns((string?)null);

        var valid = await h.Provider().ValidateAsync(Operator(NamedAdminGroupSid), "Interval", Ct);

        Assert.True(valid);
        _ = h.Directory.DidNotReceive().GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task NameTranslationFailure_InvalidatesTheCircuit()
    {
        using var h = new Harness(adminGroups: [NamedAdminGroup]);
        h.Directory.TranslateGroupName(NamedAdminGroup)
            .Throws(new DirectoryUnavailableException("the trust could not be reached"));

        var valid = await h.Provider().ValidateAsync(Operator(NamedAdminGroupSid), "Interval", Ct);

        Assert.False(valid);
        Assert.Equal(CircuitRevocationReason.LookupFailed, h.Revocation.Reason);
    }

    [Fact]
    public async Task ModuleAdminGrant_IsConsulted()
    {
        using var h = new Harness();
        h.ModuleAdmins.SetModuleAdmins("Migration", [ModuleAdminGroupSid]);
        h.DirectoryReturns(enabled: true, DomainUsersSid);

        var valid = await h.Provider().ValidateAsync(Operator(ModuleAdminGroupSid), "Interval", Ct);

        Assert.False(valid);
        Assert.Equal(CircuitRevocationReason.MembershipChanged, h.Revocation.Reason);
    }

    [Fact]
    public async Task AServicerGrant_IsConsultedLikeAnyOther()
    {
        // The protected-principal servicer gate reads these as SIDs only (aud-a29f), so what it
        // honours is exactly what is compared here.
        using var h = new Harness();
        h.Grant(ProtectedPrincipalServicerService.SectionKeyFor("BlockedSenders"), SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);

        Assert.False(await h.Provider().ValidateAsync(Operator(SectionGroupSid, DomainUsersSid), "Interval", Ct));
        Assert.Equal(CircuitRevocationReason.MembershipChanged, h.Revocation.Reason);
    }

    [Fact]
    public void TheInventoryHoldsOnlyUsableDomainSids()
    {
        using var h = new Harness(adminGroups: ["S-1-5-32-544", NamedAdminGroup]);
        h.Grant("MailboxPermissions", SectionGroupSid, @"CONTOSO\NotMigrated", "S-1-1-0");
        h.Directory.TranslateGroupName(NamedAdminGroup).Returns("S-1-5-32-545");

        var consulted = h.Inventory().GetConsultedSids();

        // A name nothing migrated, a well-known SID, and a name that translates to a BUILTIN
        // group are none of them a directory group this check can re-derive.
        Assert.Equal(new[] { SectionGroupSid }, consulted.ToArray());
        h.Directory.DidNotReceive().TranslateGroupName("S-1-5-32-544");

        // The two static ones can still grant through IsInRole, so they are reported, not dropped
        // (aud-a29e). The stored ones cannot grant at all: the gates read stored values as SIDs only.
        Assert.Equal(
            new[] { "S-1-5-32-544", "S-1-5-32-545" },
            h.Inventory().Read().Unrevalidatable
                .Where(grant => grant.Source == "Security:AdminGroups")
                .Select(grant => grant.Sid)
                .Order(StringComparer.Ordinal)
                .ToArray());
        Assert.Equal(2, h.Inventory().Read().Unrevalidatable.Count);
    }

    // ------------------------------------------------------------------ static groups that cannot be revalidated (aud-a29e)

    [Theory]
    [InlineData("Security:AdminGroups", "S-1-5-32-544", null)]
    [InlineData("Security:AdminGroups", @"BUILTIN\Administrators", "S-1-5-32-544")]
    [InlineData("Security:AllowedGroups", "Everyone", "S-1-1-0")]
    [InlineData("Security:AllowedGroups", "S-1-5-11", null)]
    [InlineData("Security:AdminGroups", DomainSid, null)]
    public void AStaticGroupThatCannotBeRevalidated_RefusesStartupNamingTheEntry(string key, string entry, string? translatesTo)
    {
        string[] list = [SectionGroupSid, entry];
        var ex = Assert.Throws<InvalidOperationException>(() => CircuitRevalidationSettings.EnsureStaticGroupsCanBeRevalidated(
            enabled: true,
            allowedGroups: key == "Security:AllowedGroups" ? list : [],
            adminGroups: key == "Security:AdminGroups" ? list : [],
            translateName: name => name == entry ? translatesTo : null));

        Assert.Contains($"{key} contains '{entry}'", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Use a domain group instead", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Authentication:RevalidationEnabled", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DomainStaticGroups_PassStartupValidation()
    {
        var translations = new Dictionary<string, string?>
        {
            [NamedAdminGroup] = NamedAdminGroupSid,
            [@"CONTOSO\Helpdesk"] = DomainSid + "-5007",
            [@"CONTOSO\NoSuchGroup"] = null,
        };

        // Domain SIDs, names that translate to domain SIDs, a name that maps to nothing and a value
        // that is not a SID at all: the last two grant nothing, so they are not refused either.
        var warnings = CircuitRevalidationSettings.EnsureStaticGroupsCanBeRevalidated(
            enabled: true,
            allowedGroups: [SectionGroupSid, @"CONTOSO\Helpdesk", "  "],
            adminGroups: [NamedAdminGroup, @"CONTOSO\NoSuchGroup", "S-1-5-not-a-sid"],
            translateName: name => translations[name]);

        Assert.Empty(warnings);
    }

    [Fact]
    public void TheDisabledSwitch_SkipsStaticGroupValidation()
    {
        var translated = 0;

        var warnings = CircuitRevalidationSettings.EnsureStaticGroupsCanBeRevalidated(
            enabled: false,
            allowedGroups: ["Everyone"],
            adminGroups: [@"BUILTIN\Administrators", "S-1-5-32-544"],
            translateName: _ =>
            {
                translated++;
                return "S-1-5-32-544";
            });

        Assert.Empty(warnings);
        Assert.Equal(0, translated);
    }

    [Fact]
    public void AStaticNameThatCannotBeTranslatedAtStartup_IsReportedNotFatal()
    {
        var warnings = CircuitRevalidationSettings.EnsureStaticGroupsCanBeRevalidated(
            enabled: true,
            allowedGroups: [],
            adminGroups: [NamedAdminGroup],
            translateName: _ => throw new DirectoryUnavailableException("the trust could not be reached"));

        var warning = Assert.Single(warnings);
        Assert.Contains($"Security:AdminGroups contains '{NamedAdminGroup}'", warning, StringComparison.Ordinal);
        Assert.Contains("could not be translated", warning, StringComparison.Ordinal);
        Assert.Contains("fails closed", warning, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("S-1-5-32-544", null)]
    [InlineData(NamedAdminGroup, "S-1-5-32-544")]
    public async Task AStaticGroupThatCannotBeRevalidated_FailsClosedForATokenThatCarriesIt(string entry, string? translatesTo)
    {
        // What startup refuses, should it ever get past (a name that could not be translated then).
        using var h = new Harness(adminGroups: [entry]);
        if (translatesTo is not null)
            h.Directory.TranslateGroupName(entry).Returns(translatesTo);
        h.DirectoryReturns(enabled: true, DomainUsersSid);

        Assert.False(await h.Provider().ValidateAsync(Operator("S-1-5-32-544", DomainUsersSid), "Interval", Ct));

        Assert.Equal(CircuitRevocationReason.LookupFailed, h.Revocation.Reason);
        var error = Assert.Single(h.AuditEvents()).RootElement.GetProperty("error").GetString();
        Assert.Contains("Security:AdminGroups", error, StringComparison.Ordinal);
        Assert.DoesNotContain("App-Admins", error, StringComparison.Ordinal);
        _ = h.Directory.DidNotReceive().GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task AStaticGroupThatCannotBeRevalidated_LeavesATokenWithoutItAlone()
    {
        using var h = new Harness(adminGroups: ["S-1-5-32-544"]);
        h.Grant("RiskyUsers", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);

        Assert.True(await h.Provider().ValidateAsync(Operator(SectionGroupSid, DomainUsersSid), "Interval", Ct));
        Assert.False(h.Revocation.IsRevoked);
    }

    // ------------------------------------------------------------------ the grants a circuit came through (aud-a29d)

    [Fact]
    public async Task AGrantRemovedFromTheStore_RevokesTheOpenCircuitWithoutADirectoryChange()
    {
        using var h = new Harness();
        h.Grant("RiskyUsers", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);
        using var provider = h.Provider();
        var user = Operator(SectionGroupSid, DomainUsersSid);
        Assert.True(await provider.ValidateAsync(user, "CircuitOpened", Ct));

        // The administrator takes the group out of the section; the directory has not changed.
        h.Grant("RiskyUsers");
        h.Directory.ClearReceivedCalls();

        Assert.False(await provider.ValidateAsync(user, "Interval", Ct));
        Assert.Equal(CircuitRevocationReason.GrantRemoved, h.Revocation.Reason);
        var audit = Assert.Single(h.AuditEvents()).RootElement;
        Assert.Equal("GrantRemoved", audit.GetProperty("reason").GetString());
        Assert.Equal("Interval", audit.GetProperty("trigger").GetString());
        Assert.Equal(new[] { SectionGroupSid }, RemovedSids(audit));
        _ = h.Directory.DidNotReceive().GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task RemovingAGrantTheOperatorNeverHeld_DoesNotRevoke()
    {
        using var h = new Harness();
        h.Grant("RiskyUsers", SectionGroupSid);
        h.Grant("MessageTrace", OtherTeamSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);
        using var provider = h.Provider();
        var user = Operator(SectionGroupSid, DomainUsersSid);
        Assert.True(await provider.ValidateAsync(user, "CircuitOpened", Ct));

        h.Grant("MessageTrace");

        Assert.True(await provider.ValidateAsync(user, "Interval", Ct));
        Assert.False(h.Revocation.IsRevoked);
    }

    [Fact]
    public async Task AGrantRemovedFromOneSection_RevokesWhileTheGroupStaysGrantedInAnother()
    {
        using var h = new Harness();
        h.Grant("RiskyUsers", SectionGroupSid);
        h.Grant("MessageTrace", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);
        using var provider = h.Provider();
        var user = Operator(SectionGroupSid, DomainUsersSid);
        Assert.True(await provider.ValidateAsync(user, "CircuitOpened", Ct));

        // The group still reaches Message Trace; the Risky Users grant the open page may be using is gone.
        h.Grant("RiskyUsers");

        Assert.False(await provider.ValidateAsync(user, "Interval", Ct));
        Assert.Equal(CircuitRevocationReason.GrantRemoved, h.Revocation.Reason);
    }

    [Fact]
    public async Task AGrantAddedWhileTheCircuitIsOpen_IsCapturedSoItsRemovalIsSeen()
    {
        using var h = new Harness();
        h.Grant("RiskyUsers", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, LaterGrantedGroupSid, DomainUsersSid);
        using var provider = h.Provider();
        var user = Operator(SectionGroupSid, LaterGrantedGroupSid, DomainUsersSid);
        Assert.True(await provider.ValidateAsync(user, "CircuitOpened", Ct));

        h.Grant("MessageTrace", LaterGrantedGroupSid);
        Assert.True(await provider.ValidateAsync(user, "Interval", Ct));
        Assert.Contains(new AuthorizationGrant("SectionAccess:MessageTrace", LaterGrantedGroupSid), provider.CapturedGrants);

        h.Grant("MessageTrace");

        Assert.False(await provider.ValidateAsync(user, "Interval", Ct));
        Assert.Equal(CircuitRevocationReason.GrantRemoved, h.Revocation.Reason);
        Assert.Equal(new[] { LaterGrantedGroupSid }, RemovedSids(Assert.Single(h.AuditEvents()).RootElement));
    }

    [Fact]
    public async Task AModuleAdminGrantRemoved_RevokesTheOpenCircuit()
    {
        using var h = new Harness();
        h.ModuleAdmins.SetModuleAdmins("Migration", [ModuleAdminGroupSid]);
        h.DirectoryReturns(enabled: true, ModuleAdminGroupSid, DomainUsersSid);
        using var provider = h.Provider();
        var user = Operator(ModuleAdminGroupSid, DomainUsersSid);
        Assert.True(await provider.ValidateAsync(user, "CircuitOpened", Ct));

        h.ModuleAdmins.SetModuleAdmins("Migration", []);

        Assert.False(await provider.ValidateAsync(user, "Interval", Ct));
        Assert.Equal(CircuitRevocationReason.GrantRemoved, h.Revocation.Reason);
    }

    [Fact]
    public async Task TheCapturedGrantsNeverShrink_AndStayComparedWhenALaterTokenStopsCarryingTheGroup()
    {
        using var h = new Harness();
        h.Grant("RiskyUsers", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);
        using var provider = h.Provider();
        Assert.True(await provider.ValidateAsync(Operator(SectionGroupSid, DomainUsersSid), "CircuitOpened", Ct));
        var captured = new AuthorizationGrant("SectionAccess:RiskyUsers", SectionGroupSid);

        // A reconnect brings a token without the group. The grant the circuit came through is still
        // compared with the directory, which here still has the account in the group.
        var reconnected = Operator(DomainUsersSid);
        Assert.True(await provider.ValidateAsync(reconnected, "Interval", Ct));
        Assert.Contains(captured, provider.CapturedGrants);
        _ = h.Directory.Received(2).GetMembershipAsync(AnySid, AnyDomain, AnyToken);

        // Now the directory says the account left the group.
        h.DirectoryReturns(enabled: true, DomainUsersSid);

        Assert.False(await provider.ValidateAsync(reconnected, "Interval", Ct));
        Assert.Equal(CircuitRevocationReason.MembershipChanged, h.Revocation.Reason);
        Assert.Equal(new[] { SectionGroupSid }, RemovedSids(Assert.Single(h.AuditEvents()).RootElement));
    }

    [Fact]
    public async Task ConfiguringSectionAccessForTheFirstTime_RevokesTheCircuitsThatRodeTheAllowedGroupsFallback()
    {
        // Unconfigured, every section that is not fail-closed answers to Security:AllowedGroups.
        using var h = new Harness(allowedGroups: [SectionGroupSid]);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);
        using var provider = h.Provider();
        var user = Operator(SectionGroupSid, DomainUsersSid);
        Assert.True(await provider.ValidateAsync(user, "CircuitOpened", Ct));
        Assert.Contains(new AuthorizationGrant("SectionAccess:*", SectionGroupSid), provider.CapturedGrants);

        h.Grant("RiskyUsers", OtherTeamSid);

        Assert.False(await provider.ValidateAsync(user, "Interval", Ct));
        Assert.Equal(CircuitRevocationReason.GrantRemoved, h.Revocation.Reason);
    }

    [Theory]
    [InlineData("section_access")]
    [InlineData("module_admins")]
    public async Task AGrantStoreThatCannotBeRead_IsALookupFailure(string table)
    {
        using var h = new Harness();
        h.Grant("RiskyUsers", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);
        var user = Operator(SectionGroupSid, DomainUsersSid);

        h.DropTable(table);

        // Even a circuit with nothing captured yet: an unreadable store is not "nothing granted".
        Assert.False(await h.Provider().ValidateAsync(user, "CircuitOpened", Ct));
        Assert.Equal(CircuitRevocationReason.LookupFailed, h.Revocation.Reason);
        Assert.Contains("could not be read", Assert.Single(h.AuditEvents()).RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the audit line

    [Theory]
    [InlineData(CircuitRevocationReason.MembershipChanged)]
    [InlineData(CircuitRevocationReason.AccountDisabled)]
    [InlineData(CircuitRevocationReason.LookupFailed)]
    public async Task Invalidation_WritesOneAuditLineWithItsReason(CircuitRevocationReason reason)
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        switch (reason)
        {
            case CircuitRevocationReason.MembershipChanged:
                h.DirectoryReturns(enabled: true, DomainUsersSid);
                break;
            case CircuitRevocationReason.AccountDisabled:
                h.DirectoryReturns(enabled: false, SectionGroupSid, DomainUsersSid);
                break;
            default:
                h.Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken)
                    .ThrowsAsync(new DirectoryUnavailableException("no domain controller answered"));
                break;
        }

        var provider = h.Provider();
        Assert.False(await provider.ValidateAsync(Operator(SectionGroupSid), "Interval", Ct));
        Assert.False(await provider.ValidateAsync(Operator(SectionGroupSid), "Interval", Ct));

        // One line per circuit, however many checks find it invalid afterwards.
        var audit = Assert.Single(h.AuditEvents()).RootElement;
        Assert.Equal("SessionRevalidation", audit.GetProperty("action").GetString());
        Assert.Equal("Authentication", audit.GetProperty("category").GetString());
        Assert.Equal("Failed", audit.GetProperty("result").GetString());
        Assert.Equal("operator", audit.GetProperty("user").GetString());
        Assert.Equal(OperatorName, audit.GetProperty("target").GetString());
        Assert.Equal("192.0.2.10", audit.GetProperty("ip").GetString());
        Assert.Equal(reason.ToString(), audit.GetProperty("reason").GetString());
        Assert.Equal("Interval", audit.GetProperty("trigger").GetString());
        Assert.False(string.IsNullOrWhiteSpace(audit.GetProperty("error").GetString()));

        if (reason == CircuitRevocationReason.MembershipChanged)
        {
            Assert.Equal(new[] { SectionGroupSid }, RemovedSids(audit));
        }
        else
        {
            Assert.False(audit.TryGetProperty("removedGroupSids", out _));
        }
    }

    [Fact]
    public async Task AnAuditFailure_DoesNotStopTheRevocation()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);

        // An audit service with no sink throws from inside LogModuleAction.
        var provider = new MembershipRevalidatingAuthenticationStateProvider(
            NullLoggerFactory.Instance, h.Revalidator(), h.Revocation, h.Options,
            new AuditService(null!, null!), h.ClientInfo);

        Assert.False(await provider.ValidateAsync(Operator(SectionGroupSid), "Interval", Ct));
        Assert.True(h.Revocation.IsRevoked);
    }

    [Fact]
    public void ARevocationIsRecordedOnceAndKeepsItsFirstReason()
    {
        // The open check and the interval loop can both find a circuit invalid; only the first
        // may audit, and the reason the operator is shown must not change under them.
        var revocation = new CircuitRevocation();

        Assert.True(revocation.TryRevoke(CircuitRevocationReason.AccountDisabled));
        Assert.False(revocation.TryRevoke(CircuitRevocationReason.LookupFailed));
        Assert.Equal(CircuitRevocationReason.AccountDisabled, revocation.Reason);
    }

    [Fact]
    public async Task ARevokedCircuit_IsNotLookedUpAgain()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);
        var provider = h.Provider();

        Assert.False(await provider.ValidateAsync(Operator(SectionGroupSid), "Interval", Ct));
        h.Directory.ClearReceivedCalls();
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);

        Assert.False(await provider.ValidateAsync(Operator(SectionGroupSid), "Interval", Ct));
        _ = h.Directory.DidNotReceive().GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    // ------------------------------------------------------------------ the circuit

    [Fact(Timeout = 20000)]
    public async Task TheFrameworkLoop_SignsTheCircuitOutWhenRevalidationFails()
    {
        using var h = new Harness(interval: TimeSpan.FromMilliseconds(50));
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);
        using var provider = h.Provider();

        var signedOut = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        provider.AuthenticationStateChanged += async task =>
        {
            if ((await task).User.Identity?.IsAuthenticated != true)
                signedOut.TrySetResult();
        };

        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(
            Task.FromResult(new AuthenticationState(Operator(SectionGroupSid))));

        await signedOut.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        Assert.True(h.Revocation.IsRevoked);
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        var audit = Assert.Single(h.AuditEvents()).RootElement;
        Assert.Equal("Interval", audit.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task ARevokedCircuit_IsNeverGivenAnAuthenticatedPrincipalAgain()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);
        using var provider = h.Provider();
        var host = (IHostEnvironmentAuthenticationStateProvider)provider;

        Assert.False(await provider.ValidateAsync(Operator(SectionGroupSid), "Interval", Ct));

        // What a SignalR reconnect does: ComponentHub.ConnectCircuit -> SetCircuitUser hands the
        // circuit the new connection's principal, which under Kerberos is the stale one.
        host.SetAuthenticationState(Task.FromResult(new AuthenticationState(Operator(SectionGroupSid))));

        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
    }

    [Fact]
    public async Task AnUnrevokedCircuit_TakesTheHostPrincipal()
    {
        using var h = new Harness();
        using var provider = h.Provider();
        var principal = Operator(SectionGroupSid);

        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(
            Task.FromResult(new AuthenticationState(principal)));

        Assert.Same(principal, (await provider.GetAuthenticationStateAsync()).User);
    }

    [Fact]
    public async Task TheCircuitOpenCheck_RevokesAndSignsOut()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);
        using var provider = h.Provider();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(
            Task.FromResult(new AuthenticationState(Operator(SectionGroupSid))));

        await provider.CheckAtCircuitOpenAsync();

        Assert.Equal(CircuitRevocationReason.MembershipChanged, h.Revocation.Reason);
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        var audit = Assert.Single(h.AuditEvents()).RootElement;
        Assert.Equal("CircuitOpened", audit.GetProperty("trigger").GetString());
    }

    [Fact]
    public async Task TheCircuitOpenCheck_LeavesAValidSessionAlone()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, SectionGroupSid, DomainUsersSid);
        using var provider = h.Provider();
        var principal = Operator(SectionGroupSid);
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(
            Task.FromResult(new AuthenticationState(principal)));

        await provider.CheckAtCircuitOpenAsync();

        Assert.False(h.Revocation.IsRevoked);
        Assert.Same(principal, (await provider.GetAuthenticationStateAsync()).User);
    }

    [Fact]
    public async Task TheCircuitOpenCheck_SkipsAnUnauthenticatedCircuit()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        using var provider = h.Provider();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(
            Task.FromResult(new AuthenticationState(new ClaimsPrincipal(new ClaimsIdentity()))));

        await provider.CheckAtCircuitOpenAsync();

        Assert.False(h.Revocation.IsRevoked);
        _ = h.Directory.DidNotReceive().GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact]
    public async Task TheCircuitOpenCheck_FailsClosedWhenThereIsNoStateToCheck()
    {
        using var h = new Harness();
        using var provider = h.Provider();

        // ServerAuthenticationStateProvider throws when asked before any state was set.
        await provider.CheckAtCircuitOpenAsync();

        Assert.Equal(CircuitRevocationReason.LookupFailed, h.Revocation.Reason);
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        Assert.Equal("Unknown", Assert.Single(h.AuditEvents()).RootElement.GetProperty("target").GetString());
    }

    [Fact]
    public async Task TheCircuitOpenCheck_StopsQuietlyOnceTheCircuitIsGone()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);
        var provider = h.Provider();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(
            Task.FromResult(new AuthenticationState(Operator(SectionGroupSid))));

        ((IDisposable)provider).Dispose();
        await provider.CheckAtCircuitOpenAsync();

        // A closed circuit has no session left to end, and no operator to audit against.
        Assert.False(h.Revocation.IsRevoked);
        Assert.Empty(h.AuditEvents());
    }

    [Fact]
    public async Task AnUnexpectedFailureInsideTheCheck_IsALookupFailure()
    {
        using var h = new Harness();

        var valid = await h.Provider().ValidateAsync(null!, "Interval", Ct);

        Assert.False(valid);
        Assert.Equal(CircuitRevocationReason.LookupFailed, h.Revocation.Reason);
    }

    [Fact]
    public async Task ADirectoryWithNoAnswer_IsALookupFailure()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken).Returns(Task.FromResult<OperatorDirectoryMembership>(null!));

        var valid = await h.Provider().ValidateAsync(Operator(SectionGroupSid), "Interval", Ct);

        Assert.False(valid);
        Assert.Equal(CircuitRevocationReason.LookupFailed, h.Revocation.Reason);
    }

    [Fact]
    public async Task TheGate_LeavesOpenAloneWithoutTheRevalidatingProvider()
    {
        using var h = new Harness();
        var gate = h.Gate(Substitute.For<AuthenticationStateProvider>());

        await gate.OnCircuitOpenedAsync(null!, Ct);

        Assert.True(gate.OpenCheck.IsCompletedSuccessfully);
        Assert.False(h.Revocation.IsRevoked);
    }

    [Fact]
    public async Task TheGate_PassesActivityThroughWhileTheCircuitIsValid()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        using var provider = h.Provider();
        SignIn(provider, Operator(UnconsultedGroupSid, DomainUsersSid));
        var gate = h.Gate(provider);
        await gate.OnCircuitOpenedAsync(null!, Ct);

        var ran = 0;
        var handler = gate.CreateInboundActivityHandler(_ =>
        {
            ran++;
            return Task.CompletedTask;
        });
        await handler(null!);
        await handler(null!);

        Assert.Equal(2, ran);
        Assert.False(h.Revocation.IsRevoked);
    }

    [Fact]
    public async Task TheGate_DropsEveryInboundActivityOnceRevoked()
    {
        using var h = new Harness();
        using var provider = h.Provider();
        var gate = h.Gate(provider);
        h.Revocation.TryRevoke(CircuitRevocationReason.MembershipChanged);

        var ran = 0;
        var handler = gate.CreateInboundActivityHandler(_ =>
        {
            ran++;
            return Task.CompletedTask;
        });

        await handler(null!);
        await handler(null!);

        Assert.Equal(0, ran);
    }

    [Fact]
    public async Task TheGate_StartsTheCircuitOpenCheck()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.DirectoryReturns(enabled: true, DomainUsersSid);
        using var provider = h.Provider();
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(
            Task.FromResult(new AuthenticationState(Operator(SectionGroupSid))));
        var gate = h.Gate(provider);

        // A circuit whose access the open check did not confirm is not initialized (aud-a29c).
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.OnCircuitOpenedAsync(null!, Ct));
        Assert.False(await gate.OpenCheck.WaitAsync(TimeSpan.FromSeconds(15), Ct));

        Assert.Equal(CircuitRevocationReason.MembershipChanged, h.Revocation.Reason);
    }

    [Fact]
    public void TheGate_RunsAfterTheHandlersThatCaptureCircuitContext()
    {
        using var h = new Harness();
        using var provider = h.Provider();
        var gate = h.Gate(provider);
        var clientInfo = new ClientInfoCircuitHandler(
            h.ClientInfo, Substitute.For<Microsoft.AspNetCore.Http.IHttpContextAccessor>(),
            NullLogger<ClientInfoCircuitHandler>.Instance);

        // The open check audits with the circuit's IP, which ClientInfoCircuitHandler captures in
        // its own OnCircuitOpenedAsync. Handlers run in ascending Order.
        Assert.True(gate.Order > clientInfo.Order);
        Assert.True(gate.Order > new UsageSessionCircuitHandler(new UsageSession()).Order);
    }

    // ------------------------------------------------------------------ the pending circuit (aud-a29c)

    [Fact(Timeout = 20000)]
    public async Task ActivityBeforeTheOpenCheckCompletes_IsHeldThenPassedWhenAccessIsConfirmed()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        var (asked, answer) = h.DirectoryAnswersLater();
        using var provider = h.Provider();
        SignIn(provider, Operator(SectionGroupSid, DomainUsersSid));
        var gate = h.Gate(provider);

        var opened = gate.OnCircuitOpenedAsync(null!, Ct);
        var ran = 0;
        var activity = gate.CreateInboundActivityHandler(_ =>
        {
            ran++;
            return Task.CompletedTask;
        })(null!);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        // The directory has not answered: the circuit's initialization and the activity both wait.
        Assert.False(opened.IsCompleted);
        Assert.False(activity.IsCompleted);
        Assert.Equal(0, ran);

        answer.SetResult(Membership(enabled: true, SectionGroupSid, DomainUsersSid));
        await opened.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        await activity.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        Assert.Equal(1, ran);
        Assert.False(h.Revocation.IsRevoked);
        Assert.Empty(h.AuditEvents());
    }

    [Fact(Timeout = 20000)]
    public async Task ActivityBeforeTheOpenCheckCompletes_IsDroppedWhenTheCheckRevokes()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        var (asked, answer) = h.DirectoryAnswersLater();
        using var provider = h.Provider();
        SignIn(provider, Operator(SectionGroupSid, DomainUsersSid));
        var gate = h.Gate(provider);

        var opened = gate.OnCircuitOpenedAsync(null!, Ct);
        var ran = 0;
        var activity = gate.CreateInboundActivityHandler(_ =>
        {
            ran++;
            return Task.CompletedTask;
        })(null!);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.False(activity.IsCompleted);

        answer.SetResult(Membership(enabled: true, DomainUsersSid));

        await Assert.ThrowsAsync<InvalidOperationException>(() => opened.WaitAsync(TimeSpan.FromSeconds(15), Ct));
        await activity.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        Assert.Equal(0, ran);
        Assert.Equal(CircuitRevocationReason.MembershipChanged, h.Revocation.Reason);
        Assert.Equal("CircuitOpened", Assert.Single(h.AuditEvents()).RootElement.GetProperty("trigger").GetString());
    }

    [Fact(Timeout = 20000)]
    public async Task ActivityThatReachesTheGateBeforeTheCircuitOpens_StartsTheCheckAndIsHeld()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        var (asked, answer) = h.DirectoryAnswersLater();
        using var provider = h.Provider();
        SignIn(provider, Operator(SectionGroupSid, DomainUsersSid));
        var gate = h.Gate(provider);

        // The framework rebuilds the inbound pipeline before it calls OnCircuitOpenedAsync, so an
        // activity can get here first.
        var ran = 0;
        var activity = gate.CreateInboundActivityHandler(_ =>
        {
            ran++;
            return Task.CompletedTask;
        })(null!);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        var opened = gate.OnCircuitOpenedAsync(null!, Ct);

        Assert.False(activity.IsCompleted);
        Assert.False(opened.IsCompleted);
        Assert.Equal(0, ran);

        answer.SetResult(Membership(enabled: true, SectionGroupSid, DomainUsersSid));
        await activity.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        await opened.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        Assert.Equal(1, ran);
        _ = h.Directory.Received(1).GetMembershipAsync(AnySid, AnyDomain, AnyToken);
    }

    [Fact(Timeout = 20000)]
    public async Task AnOpenCheckThatTimesOut_RevokesAndDropsTheHeldActivity()
    {
        using var h = new Harness(lookupTimeout: TimeSpan.FromSeconds(1));
        h.Grant("MailboxPermissions", SectionGroupSid);
        h.Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken)
            .Returns(new TaskCompletionSource<OperatorDirectoryMembership>().Task);
        using var provider = h.Provider();
        SignIn(provider, Operator(SectionGroupSid));
        var gate = h.Gate(provider);

        var ran = 0;
        var activity = gate.CreateInboundActivityHandler(_ =>
        {
            ran++;
            return Task.CompletedTask;
        })(null!);
        await activity.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        Assert.Equal(0, ran);
        Assert.Equal(CircuitRevocationReason.LookupFailed, h.Revocation.Reason);
        Assert.Contains("did not answer", Assert.Single(h.AuditEvents()).RootElement.GetProperty("error").GetString(), StringComparison.Ordinal);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.OnCircuitOpenedAsync(null!, Ct));
    }

    [Fact(Timeout = 20000)]
    public async Task AnOpenCheckThatNeverFinishes_IsRevokedWhenTheHoldRunsOut()
    {
        using var h = new Harness(lookupTimeout: TimeSpan.FromMilliseconds(200));
        using var provider = h.Provider();

        // A state that never resolves keeps the open check from ever reaching a verdict.
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(
            new TaskCompletionSource<AuthenticationState>().Task);
        var gate = h.Gate(provider);

        var ran = 0;
        var activity = gate.CreateInboundActivityHandler(_ =>
        {
            ran++;
            return Task.CompletedTask;
        })(null!);
        await activity.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        Assert.Equal(0, ran);
        Assert.Equal(CircuitRevocationReason.PendingCheckTimedOut, h.Revocation.Reason);
        var audit = Assert.Single(h.AuditEvents()).RootElement;
        Assert.Equal("PendingCheckTimedOut", audit.GetProperty("reason").GetString());
        Assert.Equal("CircuitOpened", audit.GetProperty("trigger").GetString());
        Assert.Equal("Unknown", audit.GetProperty("target").GetString());
        Assert.False((await provider.GetAuthenticationStateAsync()).User.Identity?.IsAuthenticated);
        await Assert.ThrowsAsync<InvalidOperationException>(() => gate.OnCircuitOpenedAsync(null!, Ct));
    }

    [Fact(Timeout = 20000)]
    public async Task ACircuitThatClosesBeforeItsOpenCheckCompletes_RunsNothingItHeld()
    {
        using var h = new Harness();
        h.Grant("MailboxPermissions", SectionGroupSid);
        var (asked, _) = h.DirectoryAnswersLater();
        var provider = h.Provider();
        SignIn(provider, Operator(SectionGroupSid));
        var gate = h.Gate(provider);

        var ran = 0;
        var activity = gate.CreateInboundActivityHandler(_ =>
        {
            ran++;
            return Task.CompletedTask;
        })(null!);
        await asked.Task.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        ((IDisposable)provider).Dispose();
        await activity.WaitAsync(TimeSpan.FromSeconds(15), Ct);

        // Nothing to end and no one to audit, but nothing held runs either.
        Assert.Equal(0, ran);
        Assert.False(h.Revocation.IsRevoked);
        Assert.Empty(h.AuditEvents());
    }

    [Fact(Timeout = 20000)]
    public async Task TheGate_HoldsNothingWhenRevalidationIsSwitchedOff()
    {
        using var h = new Harness(lookupTimeout: TimeSpan.FromMilliseconds(200), enabled: false);
        using var provider = h.Provider();

        // Were the open check run at all, this state would hold it until the backstop revoked.
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(
            new TaskCompletionSource<AuthenticationState>().Task);
        var gate = h.Gate(provider);

        var ran = 0;
        var activity = gate.CreateInboundActivityHandler(_ =>
        {
            ran++;
            return Task.CompletedTask;
        })(null!);
        await activity.WaitAsync(TimeSpan.FromSeconds(15), Ct);
        await gate.OnCircuitOpenedAsync(null!, Ct);

        Assert.Equal(1, ran);
        Assert.False(h.Revocation.IsRevoked);
        Assert.Empty(h.AuditEvents());
    }

    [Fact]
    public void TheTracedCircuitPipelineIsTheOneRunning()
    {
        // aud-a29c's safety argument quotes the framework's circuit start-up order (handlers'
        // OnCircuitOpenedAsync awaited before the root components are added, and those added inside
        // the inbound-activity pipeline). It was traced against ASP.NET Core 10; a new major version
        // must be traced again before this holds.
        Assert.True(
            typeof(CircuitHandler).Assembly.GetName().Version?.Major == 10,
            "Microsoft.AspNetCore.Components.Server is no longer version 10: re-trace the circuit "
            + "pipeline order recorded in .agents/review/findings/aud-a29c.md before trusting the gate.");
    }

    // ------------------------------------------------------------------ the off-switch

    [Fact]
    public async Task DisabledRevalidation_NeverLooksUpAndNeverRevokes()
    {
        using var h = new Harness(enabled: false);
        h.Grant("MailboxPermissions", SectionGroupSid);

        // Enabled, this answer would revoke the circuit at once.
        h.DirectoryReturns(enabled: true, DomainUsersSid);
        using var provider = h.Provider();
        var principal = Operator(SectionGroupSid);
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(
            Task.FromResult(new AuthenticationState(principal)));

        Assert.True(await provider.ValidateAsync(principal, "Interval", Ct));
        await provider.CheckAtCircuitOpenAsync();

        Assert.False(h.Revocation.IsRevoked);
        Assert.Same(principal, (await provider.GetAuthenticationStateAsync()).User);
        _ = h.Directory.DidNotReceive().GetMembershipAsync(AnySid, AnyDomain, AnyToken);
        h.Directory.DidNotReceiveWithAnyArgs().TranslateGroupName(default!);
        Assert.Empty(h.AuditEvents());
    }

    [Fact]
    public void DisabledRevalidation_IsReportedAtStartupWithOneWarningAndOneAuditLine()
    {
        using var h = new Harness(enabled: false);
        var log = new WarningLog();

        Assert.True(MembershipRevalidatingAuthenticationStateProvider.ReportIfDisabled(h.Options, h.Audit, log));

        var warning = Assert.Single(log.Warnings);
        Assert.Contains("revalidation is disabled by configuration", warning, StringComparison.Ordinal);
        Assert.Contains("Authentication:RevalidationEnabled", warning, StringComparison.Ordinal);

        // The audit trail shows the gap, in the same action as the revocations it stands in for.
        var audit = Assert.Single(h.AuditEvents()).RootElement;
        Assert.Equal("SessionRevalidation", audit.GetProperty("action").GetString());
        Assert.Equal("Authentication", audit.GetProperty("category").GetString());
        Assert.Equal("Disabled", audit.GetProperty("reason").GetString());
        Assert.Equal("Startup", audit.GetProperty("trigger").GetString());
        Assert.Equal("Failed", audit.GetProperty("result").GetString());
        Assert.Equal("Authentication:RevalidationEnabled", audit.GetProperty("target").GetString());
        Assert.Equal("System", audit.GetProperty("user").GetString());
        Assert.Contains("disabled by configuration", audit.GetProperty("error").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void DisabledRevalidation_SaysThatNothingReChecksMembership_NotEvenAReload()
    {
        // aud-a29h. Both lines said a removed operator kept their access "until it is reloaded". With
        // the switch off nothing consults the directory - no tab, no reload, no page request (aud-a29g) -
        // and under Kerberos a reload brings back the ticket's groups, so that told an operator turning
        // revalidation off during an incident that a reload would cut someone off. Written out here, not
        // read from the provider's constant, so changing the constant cannot pass this.
        const string consequence =
            "Nothing re-checks an operator's group membership against the directory - not an open tab, not a "
            + "reload, not a page request. An operator removed from an AD group, or disabled, keeps the access "
            + "their Windows sign-in carries: under Kerberos every reload and page request brings back the same "
            + "groups until their ticket is renewed or expires, and a tab already open keeps its access until it "
            + "is closed or the app restarts.";

        using var h = new Harness(enabled: false);
        var log = new WarningLog();
        MembershipRevalidatingAuthenticationStateProvider.ReportIfDisabled(h.Options, h.Audit, log);

        Assert.Equal(
            "Revocation revalidation is disabled by configuration (Authentication:RevalidationEnabled = false). " + consequence,
            Assert.Single(log.Warnings));
        Assert.Equal(
            "Revocation revalidation is disabled by configuration. " + consequence,
            Assert.Single(h.AuditEvents()).RootElement.GetProperty("error").GetString());
    }

    [Fact]
    public void Readme_SaysTheOffSwitchLeavesEveryReloadAndPageRequestUnchecked()
    {
        // aud-a29h, the README half: the old bullet said removed operators "keep their open tabs until
        // they reload".
        var readme = Squash(File.ReadAllText(Path.Combine(RepoRoot(), "README.md")));

        Assert.DoesNotContain("until they reload", readme, StringComparison.Ordinal);
        Assert.Contains(
            "Nothing then re-checks an operator's group membership against the directory - not an open tab, not a "
            + "reload, not a page request - so an operator removed from an AD group, or disabled, keeps the access "
            + "their Windows sign-in carries: under Kerberos every reload and page request brings back the same "
            + "groups until their ticket is renewed or expires, and a tab already open keeps its access until it "
            + "is closed or the app restarts.",
            readme,
            StringComparison.Ordinal);
    }

    [Fact]
    public void EnabledRevalidation_ReportsNothingAtStartup()
    {
        using var h = new Harness();
        var log = new WarningLog();

        Assert.False(MembershipRevalidatingAuthenticationStateProvider.ReportIfDisabled(h.Options, h.Audit, log));

        Assert.Empty(log.Warnings);
        Assert.Empty(h.AuditEvents());
    }

    [Fact]
    public void RevalidationIsOnUnlessSaidOtherwise() =>
        Assert.True(new CircuitRevalidationOptions(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60)).Enabled);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheOffSwitchDefaultsToOn(string? configured) =>
        Assert.True(CircuitRevalidationSettings.ResolveEnabled(configured));

    [Theory]
    [InlineData("false", false)]
    [InlineData("False", false)]
    [InlineData("FALSE", false)]
    [InlineData("true", true)]
    [InlineData(" True ", true)]
    public void TheOffSwitchReadsTrueAndFalse(string configured, bool expected) =>
        Assert.Equal(expected, CircuitRevalidationSettings.ResolveEnabled(configured));

    [Theory]
    [InlineData("no")]
    [InlineData("0")]
    [InlineData("off")]
    public void AnUnreadableOffSwitchIsRefused(string configured)
    {
        // Refused rather than guessed: either guess is wrong for someone, and the safe one (on)
        // would silently ignore an operator who meant to switch it off during an incident.
        var ex = Assert.Throws<InvalidOperationException>(() => CircuitRevalidationSettings.ResolveEnabled(configured));
        Assert.Contains("Authentication:RevalidationEnabled", ex.Message, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ the interval

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TheIntervalDefaultsToFiveMinutes(string? configured) =>
        Assert.Equal(TimeSpan.FromMinutes(5), CircuitRevalidationSettings.ResolveInterval(configured));

    [Theory]
    [InlineData("00:00:30", 30)]
    [InlineData("00:05:00", 300)]
    [InlineData("01:00:00", 3600)]
    public void AnIntervalWithinBoundsIsTaken(string configured, int seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), CircuitRevalidationSettings.ResolveInterval(configured));

    [Theory]
    [InlineData("00:00:29")]
    [InlineData("00:00:05")]
    [InlineData("00:00:00")]
    public void TheIntervalFloorIsEnforced(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CircuitRevalidationSettings.ResolveInterval(configured));
        Assert.Contains("minimum", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("01:00:01")]
    [InlineData("5")]
    public void TheIntervalCeilingIsEnforced(string configured)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => CircuitRevalidationSettings.ResolveInterval(configured));
        Assert.Contains("maximum", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnparseableIntervalIsRefused() =>
        Assert.Throws<InvalidOperationException>(() => CircuitRevalidationSettings.ResolveInterval("five minutes"));

    // ------------------------------------------------------------------ wiring

    [Fact]
    public void ProgramCs_RegistersTheRevalidatingProviderAndTheGate()
    {
        var program = Squash(ProgramCode());

        const string provider =
            "AddScoped<Microsoft.AspNetCore.Components.Authorization.AuthenticationStateProvider, MembershipRevalidatingAuthenticationStateProvider>()";
        const string gate =
            "AddScoped<Microsoft.AspNetCore.Components.Server.Circuits.CircuitHandler, CircuitRevocationGate>()";

        var interactive = program.IndexOf(".AddInteractiveServerComponents()", StringComparison.Ordinal);
        var registration = program.IndexOf(provider, StringComparison.Ordinal);

        Assert.True(interactive >= 0, "AddInteractiveServerComponents() is gone from Program.cs.");
        Assert.True(registration > interactive,
            "The revalidating provider must be registered after AddInteractiveServerComponents(), which "
            + "registers the framework's non-revalidating default.");
        Assert.Contains(gate, program, StringComparison.Ordinal);
        Assert.Matches(
            new Regex(@"CircuitRevalidationSettings\.ResolveInterval\(\s*builder\.Configuration\[CircuitRevalidationSettings\.IntervalKey\]\s*\)"),
            program);

        // Nothing registered afterwards can take its place.
        Assert.Single(Regex.Matches(program, @"\bAuthenticationStateProvider\s*[,>]"));

        // Negotiate and the group handler are untouched.
        Assert.Contains("AddAuthentication(NegotiateDefaults.AuthenticationScheme) .AddNegotiate()", program, StringComparison.Ordinal);
        Assert.Contains("AddSingleton<IAuthorizationHandler, GroupAuthorizationHandler>()", program, StringComparison.Ordinal);
    }

    [Fact]
    public void ProgramCs_ResolvesTheOffSwitchAndReportsItAtStartup()
    {
        var program = ProgramCode();

        Assert.Matches(
            new Regex(@"revalidationEnabled\s*=\s*CircuitRevalidationSettings\.ResolveEnabled\(\s*builder\.Configuration\[CircuitRevalidationSettings\.EnabledKey\]\s*\)"),
            program);
        Assert.Matches(
            new Regex(@"new CircuitRevalidationOptions\(\s*revalidationInterval,\s*CircuitRevalidationSettings\.LookupTimeout,\s*revalidationEnabled\s*\)"),
            program);

        // Reported once the app is built - the audit service exists only then - and before it
        // serves anything, so the gap is on record before the first request it affects.
        var built = program.IndexOf("builder.Build()", StringComparison.Ordinal);
        var report = program.IndexOf("MembershipRevalidatingAuthenticationStateProvider.ReportIfDisabled(", StringComparison.Ordinal);
        var run = program.IndexOf("app.Run()", StringComparison.Ordinal);

        Assert.True(built >= 0 && run > built, "Program.cs no longer builds and runs the app where expected.");
        Assert.True(report > built && report < run,
            "The disabled switch must be reported at startup: after builder.Build(), before app.Run().");
    }

    [Fact]
    public void ProgramCs_RefusesStaticGroupsThatCannotBeRevalidatedBeforeTheAppIsBuilt()
    {
        var program = ProgramCode();

        var call = Regex.Match(
            program,
            @"CircuitRevalidationSettings\.EnsureStaticGroupsCanBeRevalidated\(\s*revalidationEnabled,\s*allowedGroups,\s*adminGroups,\s*new OperatorMembershipDirectory\(\)\.TranslateGroupName\s*\)");
        Assert.True(call.Success, "Program.cs no longer validates the static groups for revalidation at startup.");

        // Inside the block whose InvalidOperationException is logged as fatal and rethrown, after the
        // switch is resolved, and before the app is built - so a refused configuration never serves.
        var resolved = program.IndexOf("CircuitRevalidationSettings.ResolveEnabled(", StringComparison.Ordinal);
        var fatal = program.IndexOf("catch (InvalidOperationException ex)", resolved, StringComparison.Ordinal);
        var built = program.IndexOf("builder.Build()", StringComparison.Ordinal);
        Assert.True(resolved >= 0 && call.Index > resolved && call.Index < fatal && fatal < built,
            "The static-group check must run after the switch is resolved, inside the fatal block, before builder.Build().");
        Assert.Contains("Log.Fatal(ex.Message);", program.Substring(fatal, 120), StringComparison.Ordinal);
    }

    [Fact]
    public void TheContainer_ResolvesTheRevalidatingProviderOverTheFrameworkDefault()
    {
        var services = new ServiceCollection();
        services.AddLogging();

        // What AddInteractiveServerComponents() registers, then the Program.cs registrations.
        services.TryAddScoped<AuthenticationStateProvider, ServerAuthenticationStateProvider>();
        services.AddSingleton(new CircuitRevalidationOptions(TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(60)));
        services.AddSingleton(Substitute.For<IOperatorMembershipDirectory>());
        services.AddSingleton<SectionAccessService>(_ => null!);
        services.AddSingleton<ModuleAdminService>(_ => null!);
        services.AddSingleton<AuditService>(_ => null!);
        services.AddScoped<ClientInfoService>();
        services.AddSingleton<AuthorizationGroupInventory>();
        services.AddSingleton<CircuitMembershipRevalidator>();
        services.AddScoped<CircuitRevocation>();
        services.AddScoped<AuthenticationStateProvider, MembershipRevalidatingAuthenticationStateProvider>();
        services.AddScoped<CircuitHandler, CircuitRevocationGate>();

        using var root = services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
        using var circuit = root.CreateScope();

        Assert.IsType<MembershipRevalidatingAuthenticationStateProvider>(
            circuit.ServiceProvider.GetRequiredService<AuthenticationStateProvider>());
        Assert.Single(circuit.ServiceProvider.GetServices<CircuitHandler>().OfType<CircuitRevocationGate>());

        // One revocation per circuit, shared by the provider that sets it and the gate that reads it.
        using var other = root.CreateScope();
        Assert.NotSame(
            circuit.ServiceProvider.GetRequiredService<CircuitRevocation>(),
            other.ServiceProvider.GetRequiredService<CircuitRevocation>());
    }

    [Fact]
    public void TheLayout_RendersTheSessionEndedNoticeOnEveryPage()
    {
        var layout = File.ReadAllText(Path.Combine(RepoRoot(), "Components", "Layout", "MainLayout.razor"));
        var notice = layout.IndexOf("<SessionEndedNotice", StringComparison.Ordinal);

        Assert.True(notice >= 0, "MainLayout no longer renders <SessionEndedNotice />.");

        // Outside every AuthorizeView: the layout is rendered statically, so a view's verdict is
        // the request's, and the notice must not depend on it.
        Assert.True(notice > layout.LastIndexOf("</AuthorizeView>", StringComparison.Ordinal),
            "The notice must sit outside the layout's AuthorizeView blocks.");
    }

    [Fact]
    public void TheNotice_IsAnInteractiveIslandThatReloadsInFull()
    {
        var notice = File.ReadAllText(Path.Combine(RepoRoot(), "Components", "Shared", "SessionEndedNotice.razor"));

        // Without a render mode it renders once, statically, and can never show.
        Assert.Contains("@rendermode InteractiveServer", notice, StringComparison.Ordinal);
        Assert.Contains("@inject CircuitRevocation Revocation", notice, StringComparison.Ordinal);
        Assert.Contains("[CascadingParameter]", notice, StringComparison.Ordinal);
        Assert.Contains("Task<AuthenticationState>", notice, StringComparison.Ordinal);
        Assert.Contains("SessionEndedMessages.For(reason)", notice, StringComparison.Ordinal);

        // A full load, so the browser re-authenticates; an enhanced navigation would try to reuse
        // the revoked circuit.
        Assert.Matches(new Regex(@"<a [^>]*data-enhance-nav=""false""[^>]*>Reload</a>"), notice);
        Assert.DoesNotContain("@onclick", notice, StringComparison.Ordinal);
    }

    [Fact]
    public void EveryReasonHasItsOwnPlainMessage()
    {
        var messages = Enum.GetValues<CircuitRevocationReason>().Select(SessionEndedMessages.For).ToList();

        Assert.Equal(messages.Count, messages.Distinct(StringComparer.Ordinal).Count());
        Assert.All(messages, m => Assert.Contains("Active Directory", m, StringComparison.Ordinal));
        Assert.Contains("Reload", SessionEndedMessages.ReloadAdvice, StringComparison.Ordinal);
    }

    // ------------------------------------------------------------------ helpers

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string AnySid => Arg.Any<string>();

    private static string? AnyDomain => Arg.Any<string?>();

    private static CancellationToken AnyToken => Arg.Any<CancellationToken>();

    private static ClaimsPrincipal Operator(params string[] groupSids) => Principal(OperatorSid, OperatorName, groupSids);

    private static void SignIn(MembershipRevalidatingAuthenticationStateProvider provider, ClaimsPrincipal user) =>
        ((IHostEnvironmentAuthenticationStateProvider)provider).SetAuthenticationState(
            Task.FromResult(new AuthenticationState(user)));

    private static OperatorDirectoryMembership Membership(bool enabled, params string[] groupSids) =>
        new(new HashSet<string>(groupSids, StringComparer.OrdinalIgnoreCase), enabled);

    private static ClaimsPrincipal Principal(string? primarySid, string name, IEnumerable<string> groupSids)
    {
        var identity = new ClaimsIdentity("Negotiate", ClaimTypes.Name, ClaimTypes.Role);
        identity.AddClaim(new Claim(ClaimTypes.Name, name));
        if (primarySid is not null)
            identity.AddClaim(new Claim(ClaimTypes.PrimarySid, primarySid));
        foreach (var sid in groupSids)
            identity.AddClaim(new Claim(ClaimTypes.GroupSid, sid));
        return new ClaimsPrincipal(identity);
    }

    private static string Squash(string text) => Regex.Replace(text, @"\s+", " ");

    /// <summary>
    /// Program.cs as code: comments and literals blanked to same-length spaces, newlines kept (aud-a42). Scanned as raw text, a
    /// comment naming a call satisfied these guards while the call itself was gone, and one naming app.Run() early tripped them.
    /// </summary>
    private static string ProgramCode() => RazorSyntax.CodeView(File.ReadAllText(Path.Combine(RepoRoot(), "Program.cs")));

    private static string[] RemovedSids(JsonElement audit) =>
        audit.GetProperty("removedGroupSids").EnumerateArray().Select(e => e.GetString() ?? "").ToArray();

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "ExchangeAdminWeb.csproj")))
                return dir.FullName;

            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the repository root from the test base directory.");
    }

    /// <summary>Records warning texts, so an assertion can say which warning fired and how often.</summary>
    private sealed class WarningLog : Microsoft.Extensions.Logging.ILogger
    {
        public List<string> Warnings { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == Microsoft.Extensions.Logging.LogLevel.Warning)
                Warnings.Add(formatter(state, exception));
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly TempDir _temp = new();

        public Harness(
            string[]? allowedGroups = null,
            string[]? adminGroups = null,
            TimeSpan? interval = null,
            TimeSpan? lookupTimeout = null,
            bool enabled = true)
        {
            var settings = new Dictionary<string, string?>
            {
                ["Audit:LogRoot"] = _temp.Path,
                ["Audit:RotationPeriod"] = "daily",
            };
            for (var i = 0; i < (allowedGroups?.Length ?? 0); i++)
                settings[$"Security:AllowedGroups:{i}"] = allowedGroups![i];
            for (var i = 0; i < (adminGroups?.Length ?? 0); i++)
                settings[$"Security:AdminGroups:{i}"] = adminGroups![i];

            var config = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            var env = Substitute.For<IWebHostEnvironment>();
            env.ContentRootPath.Returns(_temp.Path);

            Store = TestConfigStore.Create(_temp.Path);
            SectionAccess = new SectionAccessService(
                config, NullLogger<SectionAccessService>.Instance, env, new ModuleCatalog(), new SectionAccessRepository(Store));
            ModuleAdmins = new ModuleAdminService(env, new ModuleAdminRepository(Store), NullLogger<ModuleAdminService>.Instance);

            var log = new JsonlLogService(config, NullLogger<JsonlLogService>.Instance);
            Audit = new AuditService(log, new OperationTraceService(config, log));
            Options = new CircuitRevalidationOptions(
                interval ?? TimeSpan.FromMinutes(5), lookupTimeout ?? TimeSpan.FromSeconds(10), enabled);
        }

        public SqliteConfigStore Store { get; }

        public SectionAccessService SectionAccess { get; }

        public ModuleAdminService ModuleAdmins { get; }

        /// <summary>Damages the store the way a clobbered or half-migrated file does: the table is gone.</summary>
        public void DropTable(string table) =>
            Store.Write((connection, transaction) =>
            {
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = $"DROP TABLE {table};";
                command.ExecuteNonQuery();
            });

        public IOperatorMembershipDirectory Directory { get; } = Substitute.For<IOperatorMembershipDirectory>();

        public AuditService Audit { get; }

        public CircuitRevocation Revocation { get; } = new();

        public ClientInfoService ClientInfo { get; } = new() { IpAddress = "192.0.2.10" };

        public CircuitRevalidationOptions Options { get; }

        public void Grant(string section, params string[] groups)
        {
            var all = SectionAccess.GetSectionAccess();
            all[section] = groups;
            SectionAccess.SaveSectionAccess(all);
        }

        public void DirectoryReturns(bool enabled, params string[] groupSids) =>
            Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken).Returns(
                Task.FromResult(new OperatorDirectoryMembership(
                    new HashSet<string>(groupSids, StringComparer.OrdinalIgnoreCase), enabled)));

        /// <summary>
        /// A directory that answers only when told: <c>asked</c> completes when the lookup is made,
        /// <c>answer</c> is what it then returns.
        /// </summary>
        public (TaskCompletionSource Asked, TaskCompletionSource<OperatorDirectoryMembership> Answer) DirectoryAnswersLater()
        {
            var asked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var answer = new TaskCompletionSource<OperatorDirectoryMembership>(TaskCreationOptions.RunContinuationsAsynchronously);
            Directory.GetMembershipAsync(AnySid, AnyDomain, AnyToken).Returns(_ =>
            {
                asked.TrySetResult();
                return answer.Task;
            });
            return (asked, answer);
        }

        public CircuitRevocationGate Gate(AuthenticationStateProvider provider) =>
            new(Revocation, provider, Options, NullLogger<CircuitRevocationGate>.Instance);

        public AuthorizationGroupInventory Inventory() => new(SectionAccess, ModuleAdmins, Directory);

        public CircuitMembershipRevalidator Revalidator() => new(Directory, Inventory(), Options);

        public MembershipRevalidatingAuthenticationStateProvider Provider() =>
            new(NullLoggerFactory.Instance, Revalidator(), Revocation, Options, Audit, ClientInfo);

        public JsonDocument[] AuditEvents()
        {
            var dir = Path.Combine(_temp.Path, "ExchangeAdminWeb");
            if (!System.IO.Directory.Exists(dir))
                return [];

            return System.IO.Directory.GetFiles(dir, "exchangeadmin_*.jsonl")
                .Where(f => !Path.GetFileNameWithoutExtension(f).EndsWith("_trace", StringComparison.OrdinalIgnoreCase))
                .SelectMany(File.ReadAllLines)
                .Where(line => !string.IsNullOrWhiteSpace(line))
                .Select(line => JsonDocument.Parse(line))
                .Where(e => e.RootElement.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
                .ToArray();
        }

        public void Dispose() => _temp.Dispose();
    }
}
