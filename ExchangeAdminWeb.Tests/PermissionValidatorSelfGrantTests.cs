using System.Reflection;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Models;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// The self-grant check. aud-a28y: after its string match it compared the acting user and the grantee through a lookup
/// that answered a failure as "no recipient", so the grant was allowed exactly while Exchange could not answer. aud-a43:
/// the operator was put to Exchange as the Windows logon name, which Exchange need not resolve; a grantee Exchange Online
/// has no recipient for was compared by string only, though on the on-premises path it is not Exchange Online that
/// resolves it; and a grant to a group the operator belongs to was not seen at all.
/// </summary>
/// <remarks>
/// The validator over <see cref="ExchangeWriteHarness"/> with nothing protected, Exchange answering through a stand-in
/// resolver and the directory through <see cref="SelfGrantDirectoryStandIn"/>; the bulk loops write through the harness's
/// pool and stand-in cmdlets. Nothing here reaches Exchange or a directory. <see cref="PermissionValidatorConfigReadTests"/>
/// holds the case where the real resolver cannot read the Exchange Online config, and
/// <see cref="PermissionPagesSelfGrantTests"/> the two pages.
/// </remarks>
public sealed class PermissionValidatorSelfGrantTests : IDisposable
{
    private const string Logon = @"CONTOSO\jdoe";
    private const string Upn = "jdoe@contoso.test";

    // The operator's own mailbox by other names. "jdoe" and these do not match as strings.
    private const string CloudAlias = "jdoe.cloud@contoso.test";
    private const string OnPremAlias = "jd.legacy@contoso.test";
    private const string Nickname = "johnd";

    private const string Other = "other@contoso.test";
    private const string Target = "shared@contoso.test";
    private const string Delegates = "delegates@contoso.test";
    private static readonly Guid TargetMailbox = Guid.Parse("0f0f0f0f-2222-4333-8444-666666666666");

    private const string SelfGrantRefusal = "Access denied: You cannot grant permissions to yourself.";

    private readonly ExchangeWriteHarness _h = new();
    private readonly SelfGrantDirectoryStandIn.Entry _jdoe;
    private readonly SelfGrantDirectoryStandIn.Entry _other;

    public PermissionValidatorSelfGrantTests()
    {
        _h.AddMailbox(Target, TargetMailbox);
        _jdoe = _h.Forest.AddUser("jdoe", upn: Upn, mail: Upn, nickname: Nickname,
            proxies: [$"SMTP:{Upn}", $"smtp:{OnPremAlias}"]);
        _other = _h.Forest.AddUser("other", upn: Other, mail: Other, proxies: [$"SMTP:{Other}"]);
    }

    public void Dispose() => _h.Dispose();

    private ClaimsPrincipal Jdoe => SelfGrantDirectoryStandIn.Principal(Logon, _jdoe.Sid);

    private static string Incomplete(string identity) =>
        $"Access denied: The self-grant check could not be completed: Exchange Online could not resolve '{identity}'; nothing was written. Contact your administrator.";

    private static string IncompleteBecause(string reason) =>
        $"Access denied: The self-grant check could not be completed: {reason}; nothing was written. Contact your administrator.";

    private static string Text(JsonElement record, string property) =>
        record.TryGetProperty(property, out var value) ? value.GetString() ?? "" : "";

    private static ResolvedRecipient Recipient(string primary, string? objectId) =>
        new(primary, objectId, "UserMailbox", ExistsOnPrem: true, ExchangeGuid: Guid.NewGuid());

    private static ResolvedRecipient GroupRecipient(string primary) =>
        new(primary, "ext-" + primary, "MailUniversalSecurityGroup", ExistsOnPrem: true);

    /// <summary>Exchange knows the bulk loops' target mailbox; every other identity is "no such recipient" until stubbed.</summary>
    private static IIdentityResolver ExchangeKnowsTheTarget() =>
        ExchangeWriteHarness.ExchangeKnows(Target, Target, TargetMailbox);

    /// <summary>A lookup of <paramref name="identity"/> that cannot run: Exchange gives no answer at all.</summary>
    private static void Unreachable(IIdentityResolver resolver, string identity) =>
        resolver.ResolveRecipientAsync(identity).Returns(
            Task.FromException<ResolvedRecipient?>(new InvalidOperationException("Exchange Online is unreachable")));

    private EmailService Email() => Substitute.For<EmailService>(_h.Config, Substitute.For<ILogger<EmailService>>());

    // ---- who the operator is ---------------------------------------------------------------------

    [Fact]
    public async Task TheOperatorIsPutToExchange_AsTheDirectorysUserPrincipalNameForTheirSid_NotTheLogonName()
    {
        // Exchange knows the operator by their UPN and by an alias only it carries, and not by the logon name; it names
        // their mailbox by an address the directory does not carry either. The string match misses the alias and the
        // directory holds neither address, so only Exchange - asked about the right name - can say the two are one mailbox.
        var resolver = ExchangeKnowsTheTarget();
        var own = Recipient("jdoe.mailbox@contoso.test", "ext-jdoe");
        resolver.ResolveRecipientAsync(Upn).Returns(own);
        resolver.ResolveRecipientAsync(CloudAlias).Returns(own);

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, CloudAlias);

        Assert.Equal(SelfGrantRefusal, result);
        await resolver.Received().ResolveRecipientAsync(Upn);
        await resolver.DidNotReceive().ResolveRecipientAsync(Logon);
        Assert.Contains(_h.Forest.Searches, search => search.Contains($"(objectSid={_jdoe.Sid})", StringComparison.Ordinal)
            && search.StartsWith($"Get-ADObject {SelfGrantDirectoryStandIn.GlobalCatalogEndpoint} ", StringComparison.Ordinal));
        Assert.Empty(_h.Forest.Errors);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AUserPrincipalNameClaim_IsPutToExchangeAsGiven(bool withSid)
    {
        // A sign-in that carries a UPN claim names the operator itself; the directory is then asked by the SID when there
        // is one, else by that UPN - which this forest holds no account for, so nothing on-premises can be the operator.
        const string claimed = "jdoe.claim@contoso.test";
        var resolver = ExchangeKnowsTheTarget();
        var own = Recipient(claimed, "ext-jdoe");
        resolver.ResolveRecipientAsync(claimed).Returns(own);
        resolver.ResolveRecipientAsync(CloudAlias).Returns(own);
        var principal = SelfGrantDirectoryStandIn.Principal(Logon, withSid ? _jdoe.Sid : null, upn: claimed);

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(principal, CloudAlias);

        Assert.Equal(SelfGrantRefusal, result);
        await resolver.DidNotReceive().ResolveRecipientAsync(Upn);
        Assert.Empty(_h.Forest.Errors);
    }

    public static TheoryData<string> UnestablishedOperators =>
    [
        "no principal", "not signed in", "neither claim", "malformed SID", "SID unknown", "SID twice", "no UPN",
        "runspace", "module", "forest", "no catalog", "read fails",
    ];

    [Theory]
    [MemberData(nameof(UnestablishedOperators))]
    public async Task AnOperatorTheDirectoryCannotEstablish_IsRefused_AndExchangeIsNotAsked(string shape)
    {
        // Who is granting has to be established before anything can be compared with them. Every way it cannot be is a
        // check that could not be completed - never a grant cleared by the string match alone.
        var resolver = ExchangeKnowsTheTarget();
        ClaimsPrincipal? principal = Jdoe;
        var unreadable = "the on-premises directory could not be read";
        string reason;
        switch (shape)
        {
            case "no principal":
                principal = null;
                reason = "the signed-in operator could not be identified";
                break;
            case "not signed in":
                principal = SelfGrantDirectoryStandIn.Principal(Logon, _jdoe.Sid, authenticated: false);
                reason = "the signed-in operator could not be identified";
                break;
            case "neither claim":
                principal = SelfGrantDirectoryStandIn.Principal(Logon);
                reason = $"the signed-in operator '{Logon}' carries neither a user principal name nor a security identifier";
                break;
            case "malformed SID":
                principal = SelfGrantDirectoryStandIn.Principal(Logon, "BA");
                reason = $"the signed-in operator '{Logon}' carries a malformed security identifier";
                break;
            case "SID unknown":
                principal = SelfGrantDirectoryStandIn.Principal(Logon, SelfGrantDirectoryStandIn.DomainSid + "-9999");
                reason = $"the on-premises directory holds no account for the signed-in operator '{Logon}'";
                break;
            case "SID twice":
                _h.Forest.Add(new SelfGrantDirectoryStandIn.Entry
                {
                    Dn = $"CN=jdoe-copy,OU=Staff,{SelfGrantDirectoryStandIn.OtherDomainDn}",
                    Class = "user",
                    Sid = _jdoe.Sid,
                    Upn = "jdoe-copy@contoso.test",
                });
                reason = $"the on-premises directory holds 2 accounts for the signed-in operator '{Logon}'";
                break;
            case "no UPN":
                var noUpn = _h.Forest.AddUser("noupn");
                principal = SelfGrantDirectoryStandIn.Principal(@"CONTOSO\noupn", noUpn.Sid);
                reason = @"the signed-in operator 'CONTOSO\noupn' has no user principal name in the on-premises directory";
                break;
            case "runspace":
                _h.Forest.RunspaceFailure = "the session could not be created";
                reason = unreadable;
                break;
            case "module":
                _h.Forest.Failures["Import-Module"] = "The specified module 'ActiveDirectory' was not loaded because no valid module file was found.";
                reason = unreadable;
                break;
            case "forest":
                _h.Forest.Failures["Get-ADForest"] = "Unable to find a default server with Active Directory Web Services running.";
                reason = unreadable;
                break;
            case "no catalog":
                _h.Forest.GlobalCatalogs = [];
                reason = unreadable;
                break;
            default:
                _h.Forest.Failures["Get-ADObject (objectSid="] = "The server has returned the following error: invalid enumeration context.";
                reason = unreadable;
                break;
        }

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(principal, Other);

        Assert.Equal(IncompleteBecause(reason), result);
        await resolver.DidNotReceive().ResolveRecipientAsync(Arg.Any<string>());
        Assert.Equal(0, _h.Connects);
        Assert.Empty(_h.Forest.Errors);
    }

    // ---- the directory, every time ---------------------------------------------------------------

    [Theory]
    [InlineData(OnPremAlias)]
    [InlineData(Nickname)]
    [InlineData("the account's DN")]
    public async Task AGranteeOnlyTheDirectoryNames_IsComparedAsADirectoryObject_AndTheOperatorIsRefused(string grantee)
    {
        // Exchange has no recipient for any of these, so its comparison cannot see them - and on the on-premises path it is
        // not Exchange Online that resolves the grantee. The directory names each as the operator's own account.
        if (grantee == "the account's DN")
            grantee = _jdoe.Dn;
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Upn).Returns(Recipient(Upn, "ext-jdoe"));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, grantee);

        Assert.Equal(SelfGrantRefusal, result);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task AnotherDirectoryObjectCarryingTheOperatorsPrimaryAddress_IsTheOperator()
    {
        // Not their account, but the object their primary address belongs to - the mailbox Exchange would grant on.
        _h.Forest.Add(new SelfGrantDirectoryStandIn.Entry
        {
            Dn = $"CN=jdoe-linked,OU=Resources,{SelfGrantDirectoryStandIn.DomainDn}",
            Class = "user",
            Sam = "jdoe-linked",
            Proxies = [$"SMTP:{Upn}"],
        });
        var resolver = ExchangeKnowsTheTarget();

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, "jdoe-linked");

        Assert.Equal(SelfGrantRefusal, result);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task ADirectoryObjectThatIsNotTheOperator_IsAllowed()
    {
        // The control: both directories name someone else.
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Upn).Returns(Recipient(Upn, "ext-jdoe"));
        resolver.ResolveRecipientAsync(Other).Returns(Recipient(Other, "ext-other"));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, Other);

        Assert.Null(result);
        Assert.Contains(_h.Forest.Searches, search => search ==
            $"Get-ADObject {SelfGrantDirectoryStandIn.GlobalCatalogEndpoint} {SelfGrantDirectory.GranteeFilter(Other, Other)}");
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task AGranteeTheDirectoryCannotBeAskedAbout_IsRefused_AsIncomplete()
    {
        _h.Forest.Failures["Get-ADObject (mailNickname="] = "The server is not operational.";
        var resolver = ExchangeKnowsTheTarget();

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, Other);

        Assert.Equal(IncompleteBecause($"the on-premises directory could not resolve '{Other}'"), result);
    }

    // ---- groups ----------------------------------------------------------------------------------

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task AGroupTheOperatorIsADirectMemberOf_IsRefused(bool otherDomain, bool universal)
    {
        // Their own domain's group, among the groups that domain's member chains reach from them; a domain-local group of
        // another domain - the scope a group of another domain needs to hold them at all - asked in its own domain, the only
        // place its links are held; and a universal one, whose links the catalog holds (aud-a52: the evaluator's reads).
        var domain = otherDomain ? SelfGrantDirectoryStandIn.OtherDomainDn : SelfGrantDirectoryStandIn.DomainDn;
        var group = _h.Forest.AddGroup("Mailbox Delegates", mail: Delegates, domainDn: domain,
            domainSid: otherDomain ? SelfGrantDirectoryStandIn.OtherDomainSid : SelfGrantDirectoryStandIn.DomainSid,
            universal: universal, domainLocal: otherDomain && !universal, members: [_other, _jdoe]);
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Delegates).Returns(GroupRecipient(Delegates));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, Delegates);

        Assert.Equal(SelfGrantRefusal, result);
        string[] seeds = [_jdoe.Dn, _h.Forest.DomainUsers.Dn];
        var decisive = (otherDomain, universal) switch
        {
            (false, _) => $"Get-ADGroup contoso.test {ProtectedGroupMembership.ContainsAnyFilter(seeds)}",
            (true, false) => $"Get-ADGroup fabrikam.test {ProtectedGroupMembership.GroupContainsAnyFilter(group.Dn, seeds)}",
            _ => $"Get-ADGroup {SelfGrantDirectoryStandIn.GlobalCatalogEndpoint} {ProtectedGroupMembership.UniversalContainsAnyFilter(seeds)}",
        };
        Assert.Contains(decisive, _h.Forest.Searches);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task TheOperatorsPrimaryGroup_IsAGroupTheyAreAMemberOf()
    {
        // No member link holds an account in its primary group; primaryGroupID does. The grantee names both domains'
        // Domain Users groups; the operator's own domain's is their primary group.
        var resolver = ExchangeKnowsTheTarget();

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, "Domain Users");

        Assert.Equal(SelfGrantRefusal, result);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task AGroupWithoutTheOperator_IsAllowed_AndItsMembershipIsAskedInItsOwnDomain()
    {
        var group = _h.Forest.AddGroup("Shared Readers", mail: Delegates, domainDn: SelfGrantDirectoryStandIn.OtherDomainDn,
            domainSid: SelfGrantDirectoryStandIn.OtherDomainSid, domainLocal: true, members: [_other]);
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Delegates).Returns(GroupRecipient(Delegates));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, Delegates);

        Assert.Null(result);
        var server = SelfGrantDirectoryStandIn.DomainName(SelfGrantDirectoryStandIn.OtherDomainDn);
        Assert.Contains($"Get-ADGroup {server} {ProtectedGroupMembership.ExactDnFilter(group.Dn)}", _h.Forest.Searches);
        Assert.Contains($"Get-ADGroup {server} {ProtectedGroupMembership.GroupContainsAnyFilter(group.Dn, [_jdoe.Dn, _h.Forest.DomainUsers.Dn])}",
            _h.Forest.Searches);
        Assert.Empty(_h.Forest.Errors);
    }

    [Theory]
    [InlineData("the member read fails")]
    [InlineData("the group's domain cannot be reached")]
    [InlineData("the group's domain does not answer for it")]
    public async Task AMembershipThatCannotBeRead_IsRefused_AsIncomplete(string shape)
    {
        // The operator is not a member here: the refusal is the unanswered question, not the answer.
        _h.Forest.AddGroup("Mailbox Delegates", mail: Delegates, domainDn: SelfGrantDirectoryStandIn.OtherDomainDn,
            domainSid: SelfGrantDirectoryStandIn.OtherDomainSid, catalogOnly: shape == "the group's domain does not answer for it",
            domainLocal: true, members: [_other]);
        if (shape == "the member read fails")
            _h.Forest.Failures["Get-ADGroup member"] = "The server has returned the following error: invalid enumeration context.";
        if (shape == "the group's domain cannot be reached")
            _h.Forest.Failures["Get-ADGroup fabrikam.test"] = SelfGrantDirectoryStandIn.UnreachableServer;
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Delegates).Returns(GroupRecipient(Delegates));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, Delegates);

        Assert.Equal(IncompleteBecause($"whether you are a member of the group '{Delegates}' could not be read"), result);
    }

    [Fact]
    public async Task ASyncedGroupExchangeNamesThatTheDirectoryDoesNotHold_IsRefused_AsIncomplete()
    {
        // Exchange calls it synced, so its membership is the directory's - and the directory does not hold it.
        const string syncedGroup = "synced-team@contoso.test";
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(syncedGroup).Returns(GroupRecipient(syncedGroup));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, syncedGroup);

        Assert.Equal(IncompleteBecause(
            $"'{syncedGroup}' is a group the on-premises directory does not hold, so whether you are a member could not be checked"), result);
        await resolver.DidNotReceive().ReadGroupMembersAsync(Arg.Any<string>());
        Assert.Empty(_h.Forest.Errors);
    }

    // ---- aud-a52: nested groups ------------------------------------------------------------------

    public static TheoryData<string> NestedChains =>
    [
        "in their own domain", "a domain-local group of another domain", "universal groups across domains",
        "their primary group", "three deep",
    ];

    [Theory]
    [MemberData(nameof(NestedChains))]
    public async Task AGroupHoldingTheOperatorThroughAnotherGroup_IsRefused(string chain)
    {
        // The granted group holds the operator only through a group nested in it, or through their primary group, so no
        // member link of its own names them: the check that read direct links alone allowed each of these.
        const string OtherDomain = SelfGrantDirectoryStandIn.OtherDomainDn;
        const string OtherDomainSid = SelfGrantDirectoryStandIn.OtherDomainSid;
        switch (chain)
        {
            case "in their own domain":
                var team = _h.Forest.AddGroup("Inner Team", members: [_jdoe]);
                _h.Forest.AddGroup("Mailbox Delegates", mail: Delegates, members: [team]);
                break;
            case "a domain-local group of another domain":
                var global = _h.Forest.AddGroup("Inner Team", members: [_jdoe]);
                _h.Forest.AddGroup("Mailbox Delegates", mail: Delegates, domainDn: OtherDomain, domainSid: OtherDomainSid, domainLocal: true, members: [global]);
                break;
            case "universal groups across domains":
                var universal = _h.Forest.AddGroup("Inner Team", universal: true, members: [_jdoe]);
                _h.Forest.AddGroup("Mailbox Delegates", mail: Delegates, domainDn: OtherDomain, domainSid: OtherDomainSid, universal: true, members: [universal]);
                break;
            case "their primary group":
                _h.Forest.AddGroup("Mailbox Delegates", mail: Delegates, domainDn: OtherDomain, domainSid: OtherDomainSid, domainLocal: true,
                    members: [_h.Forest.DomainUsers]);
                break;
            default:
                var first = _h.Forest.AddGroup("First Team", members: [_jdoe]);
                var second = _h.Forest.AddGroup("Second Team", members: [first]);
                _h.Forest.AddGroup("Mailbox Delegates", mail: Delegates, domainDn: OtherDomain, domainSid: OtherDomainSid, domainLocal: true, members: [second]);
                break;
        }
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Delegates).Returns(GroupRecipient(Delegates));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, Delegates);

        Assert.Equal(SelfGrantRefusal, result);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task AGroupNestingOnlyOthers_IsAllowed()
    {
        var team = _h.Forest.AddGroup("Inner Team", members: [_other]);
        _h.Forest.AddGroup("Mailbox Delegates", mail: Delegates, domainDn: SelfGrantDirectoryStandIn.OtherDomainDn,
            domainSid: SelfGrantDirectoryStandIn.OtherDomainSid, domainLocal: true, members: [team]);
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Delegates).Returns(GroupRecipient(Delegates));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, Delegates);

        Assert.Null(result);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task ANestedMembershipThatCannotBeRead_IsRefused_AsIncomplete()
    {
        // The catalog's read of universal nesting fails: a universal chain to the operator cannot be ruled out.
        _h.Forest.AddGroup("Mailbox Delegates", mail: Delegates, members: [_other]);
        _h.Forest.Failures["Get-ADGroup (&(groupType"] = "The server is not operational.";
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Delegates).Returns(GroupRecipient(Delegates));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, Delegates);

        Assert.Equal(IncompleteBecause($"whether you are a member of the group '{Delegates}' could not be read"), result);
    }

    // ---- aud-a52: a group only Exchange Online holds ---------------------------------------------

    private const string CloudGroup = "cloud-team@contoso.test";

    private static ExchangeGroupMember Person(string? address, string? objectId) =>
        new(address, objectId, "UserMailbox", "UserMailbox", IsDirSynced: false);

    private static ExchangeGroupMember GroupMember(string address, bool synced) =>
        new(address, "ext-" + address, "MailUniversalSecurityGroup", "MailUniversalSecurityGroup", synced);

    /// <summary>Exchange knows the operator's mailbox, and the grantee as a group only it holds, with these members.</summary>
    private static IIdentityResolver ExchangeHoldsTheCloudGroup(params ExchangeGroupMember[] members)
    {
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Upn).Returns(Recipient(Upn, "ext-jdoe"));
        resolver.ResolveRecipientAsync(CloudGroup).Returns(
            new ResolvedRecipient(CloudGroup, "ext-" + CloudGroup, "MailUniversalSecurityGroup", ExistsOnPrem: false));
        resolver.ReadGroupMembersAsync(CloudGroup).Returns((IReadOnlyList<ExchangeGroupMember>)members);
        return resolver;
    }

    [Theory]
    [InlineData("address")]
    [InlineData("object id")]
    public async Task ACloudOnlyGroupHoldingTheOperator_IsRefused_ExchangeAskedForItsMembers(string by)
    {
        // Its membership is Exchange's, so Exchange is asked; it names the operator's recipient among the members - by its
        // address, or by its object id where it reports no address.
        var resolver = ExchangeHoldsTheCloudGroup(Person(Other, "ext-other"),
            by == "address" ? Person(Upn.ToUpperInvariant(), null) : Person(null, "EXT-JDOE"));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, CloudGroup);

        Assert.Equal(SelfGrantRefusal, result);
        await resolver.Received(1).ReadGroupMembersAsync(CloudGroup);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task ACloudOnlyGroupWithoutTheOperator_IsAllowed()
    {
        // aud-a43 refused every grant to such a group outright. A cloud group nested in it is among the members Exchange
        // reads; it is not the operator.
        var resolver = ExchangeHoldsTheCloudGroup(Person(Other, "ext-other"), GroupMember("nested-cloud@contoso.test", synced: false),
            Person("third@contoso.test", null));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, CloudGroup);

        Assert.Null(result);
        await resolver.Received(1).ReadGroupMembersAsync(CloudGroup);
        Assert.Empty(_h.Forest.Errors);
    }

    [Theory]
    [InlineData("throws")]
    [InlineData("no answer")]
    public async Task ACloudOnlyGroupWhoseMembersCannotBeRead_IsRefused_AsIncomplete(string shape)
    {
        var resolver = ExchangeHoldsTheCloudGroup();
        resolver.ReadGroupMembersAsync(CloudGroup).Returns(shape == "throws"
            ? Task.FromException<IReadOnlyList<ExchangeGroupMember>>(new InvalidOperationException("Exchange Online is unreachable"))
            : Task.FromResult<IReadOnlyList<ExchangeGroupMember>>(null!));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, CloudGroup);

        Assert.Equal(IncompleteBecause($"whether you are a member of the group '{CloudGroup}' could not be read"), result);
    }

    [Fact]
    public async Task ACloudOnlyGroup_WhenExchangeHoldsNoRecipientForTheOperator_IsRefused_AsIncomplete()
    {
        // There is nobody to compare its members with.
        var resolver = ExchangeHoldsTheCloudGroup(Person(Other, "ext-other"));
        resolver.ResolveRecipientAsync(Upn).Returns((ResolvedRecipient?)null);

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, CloudGroup);

        Assert.Equal(IncompleteBecause(
            $"Exchange Online holds no recipient for you, so whether you are a member of the group '{CloudGroup}' could not be checked"), result);
        await resolver.DidNotReceive().ReadGroupMembersAsync(Arg.Any<string>());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ACloudOnlyGroupMemberThatCannotBeCompared_IsRefused_AsIncomplete(bool memberHasObjectId)
    {
        // A member with no address cannot be told apart from the operator without an object id on both sides.
        var resolver = ExchangeHoldsTheCloudGroup(Person(Other, "ext-other"), Person(null, memberHasObjectId ? "ext-someone" : null));
        if (memberHasObjectId)
            resolver.ResolveRecipientAsync(Upn).Returns(Recipient(Upn, null));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, CloudGroup);

        Assert.Equal(IncompleteBecause($"whether you are a member of the group '{CloudGroup}' could not be read"), result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASyncedGroupInACloudOnlyGroup_IsAskedOfTheDirectory(bool operatorInside)
    {
        // Exchange lists a synced group in the cloud-only one and does not read into it: its membership is the directory's,
        // which holds the groups nested in it that Exchange does not list at all.
        const string synced = "synced-team@contoso.test";
        var hidden = _h.Forest.AddGroup("Hidden Team", members: [operatorInside ? _jdoe : _other]);
        _h.Forest.AddGroup("Synced Team", mail: synced, members: [hidden]);
        var resolver = ExchangeHoldsTheCloudGroup(Person(Other, "ext-other"), GroupMember(synced, synced: true));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, CloudGroup);

        Assert.Equal(operatorInside ? SelfGrantRefusal : null, result);
        Assert.Empty(_h.Forest.Errors);
    }

    [Fact]
    public async Task ASyncedGroupInACloudOnlyGroup_ThatTheDirectoryDoesNotHold_IsRefused_AsIncomplete()
    {
        var resolver = ExchangeHoldsTheCloudGroup(Person(Other, "ext-other"), GroupMember("synced-gone@contoso.test", synced: true));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, CloudGroup);

        Assert.Equal(IncompleteBecause($"whether you are a member of the group '{CloudGroup}' could not be read"), result);
        Assert.Empty(_h.Forest.Errors);
    }

    // ---- Exchange Online (aud-a28y) ----------------------------------------------------------------

    [Theory]
    [InlineData(Upn)]
    [InlineData(CloudAlias)]
    public async Task ACheckExchangeCannotAnswer_IsRefused_NamingWhoCouldNotBeResolved(string unreachable)
    {
        // The other side is what Exchange would name as the operator's own mailbox, so the comparison - had it been
        // made - refuses. Clearing the grant on the string match alone because one lookup could not run was the defect.
        var resolver = ExchangeKnowsTheTarget();
        var own = Recipient(Upn, "ext-jdoe");
        resolver.ResolveRecipientAsync(Upn).Returns(own);
        resolver.ResolveRecipientAsync(CloudAlias).Returns(own);
        Unreachable(resolver, unreachable);

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, CloudAlias);

        Assert.Equal(Incomplete(unreachable), result);
        Assert.Equal(0, _h.Connects);
    }

    [Fact]
    public async Task NoIdentityResolver_RefusesTheGrant_AsACheckThatCouldNotBeCompleted()
    {
        var result = await _h.NothingProtectedValidator(null!).ValidateSelfGrantAsync(Jdoe, Other);

        Assert.Equal(Incomplete(Upn), result);
    }

    [Theory]
    [InlineData("ext-jdoe", "ext-jdoe")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public async Task AnAliasExchangeNamesAsTheOperator_IsRefused_AsASelfGrant(string? operatorId, string? aliasId)
    {
        // One recipient, by its object id and its address; then by the address alone, when Exchange reports no object id
        // (absent or blank) - the primary address is Exchange's own name for the recipient, compared regardless of case.
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Upn).Returns(Recipient(Upn, operatorId));
        resolver.ResolveRecipientAsync(CloudAlias).Returns(Recipient(Upn.ToUpperInvariant(), aliasId));

        var result = await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, CloudAlias);

        Assert.Equal(SelfGrantRefusal, result);
    }

    [Theory]
    [InlineData("ext-jdoe", "ext-other")]
    [InlineData(null, null)]
    [InlineData("", "")]
    public async Task TwoRecipientsExchangeNamesApart_AreAllowed(string? operatorId, string? otherId)
    {
        // The control. Two blank object ids are no object id, not one shared id: they no longer make two recipients one.
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Upn).Returns(Recipient(Upn, operatorId));
        resolver.ResolveRecipientAsync(Other).Returns(Recipient(Other, otherId));

        Assert.Null(await _h.NothingProtectedValidator(resolver).ValidateSelfGrantAsync(Jdoe, Other));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public async Task ExchangeReportingNoSuchRecipient_IsAnAnswer_AndTheStringMatchStands(bool operatorKnown, bool otherKnown)
    {
        // A lookup that ran and found no recipient leaves Exchange nothing to compare, as the excluded-user check reads the
        // same null (aud-a2b); the directory still names the grantee. The string match still refuses on its own.
        var resolver = ExchangeKnowsTheTarget();
        if (operatorKnown)
            resolver.ResolveRecipientAsync(Upn).Returns(Recipient(Upn, "ext-jdoe"));
        if (otherKnown)
            resolver.ResolveRecipientAsync(Other).Returns(Recipient(Other, "ext-other"));
        var validator = _h.NothingProtectedValidator(resolver);

        Assert.Null(await validator.ValidateSelfGrantAsync(Jdoe, Other));
        Assert.Equal(SelfGrantRefusal, await validator.ValidateSelfGrantAsync(Jdoe, Upn));
    }

    // ---- the bulk loops --------------------------------------------------------------------------

    [Theory]
    [InlineData("unreachable")]
    [InlineData("alias")]
    [InlineData("no operator")]
    public async Task TheMailboxPermissionsBulkLoop_RefusesTheRow_AndRecordsTheValidatorsReason(string shape)
    {
        // The row passes the protected-target check, so the self-grant check is what refuses it - and the record carries
        // its reason: a check that could not be completed is not recorded as an operator granting themselves access.
        var (resolver, grantee, expected, actingUser) = Shape(shape);
        var csv = "Target,User,FullAccess,SendAs,AutoMapping\n" + $"{Target},{grantee},True,False,False\n";

        var bulk = await new MailboxPermissionService(_h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace)
            .ProcessMailboxPermissionsCsvAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(csv)), isAdd: true, _h.NothingProtectedValidator(resolver), Logon,
                _h.Audit, Email(), "10.0.0.5", "INC100", actingUser);

        Assert.Equal(0, bulk.SuccessCount);
        Assert.Equal(1, bulk.FailedCount);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, _h.Connects);

        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("BulkAddFullAccess_SelfGrantAttempt", Text(record, "action"));
        Assert.Equal(expected, Text(record, "error"));
    }

    [Theory]
    [InlineData("unreachable")]
    [InlineData("alias")]
    [InlineData("no operator")]
    public async Task TheCalendarPermissionsBulkLoop_RefusesTheRow_AndRecordsTheValidatorsReason(string shape)
    {
        var (resolver, grantee, expected, actingUser) = Shape(shape);
        var csv = "Target,User,AccessRight\n" + $"{Target},{grantee},Reviewer\n";

        var bulk = await new CalendarPermissionService(_h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace)
            .ProcessCalendarPermissionsCsvAsync(
                new MemoryStream(Encoding.UTF8.GetBytes(csv)), isSet: true, _h.NothingProtectedValidator(resolver), Logon,
                _h.Audit, Email(), "10.0.0.5", "INC100", actingUser);

        Assert.Equal(0, bulk.SuccessCount);
        Assert.Equal(1, bulk.FailedCount);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, _h.Connects);

        var record = Assert.Single(_h.AuditRows());
        Assert.Equal("BulkSetCalendar_SelfGrantAttempt", Text(record, "action"));
        Assert.Equal(expected, Text(record, "error"));
    }

    // aud-a50, part 3: a row that names no user is an invalid row, refused before any check.

    private const string NoUser = "No user specified (the User column is empty)";

    public static TheoryData<bool, bool, string> BlankUserRows => new()
    {
        { false, true, "" }, { false, true, "   " }, { false, false, "" }, { false, false, "   " },
        { true, true, "" }, { true, true, "   " }, { true, false, "" }, { true, false, "   " },
    };

    private static string Row(bool calendar, string user) =>
        calendar ? $"{Target},{user},Reviewer\n" : $"{Target},{user},True,False,False\n";

    /// <summary>The Mailbox Permissions or Calendar Permissions bulk loop over <paramref name="rows"/>, run as the operator.</summary>
    private async Task<BulkOperationResult> RunBulk(bool calendar, bool grant, string rows, IIdentityResolver resolver)
    {
        var validator = _h.NothingProtectedValidator(resolver);
        if (calendar)
        {
            return await new CalendarPermissionService(_h.Pool, null!, NullLogger<CalendarPermissionService>.Instance, _h.Config, null!, _h.Trace)
                .ProcessCalendarPermissionsCsvAsync(
                    new MemoryStream(Encoding.UTF8.GetBytes("Target,User,AccessRight\n" + rows)), isSet: grant, validator, Logon,
                    _h.Audit, Email(), "10.0.0.5", "INC100", Jdoe);
        }

        return await new MailboxPermissionService(_h.Pool, null!, NullLogger<MailboxPermissionService>.Instance, _h.Config, null!, _h.Trace)
            .ProcessMailboxPermissionsCsvAsync(
                new MemoryStream(Encoding.UTF8.GetBytes("Target,User,FullAccess,SendAs,AutoMapping\n" + rows)), isAdd: grant, validator, Logon,
                _h.Audit, Email(), "10.0.0.5", "INC100", Jdoe);
    }

    private static string NoUserAction(bool calendar, bool grant) => calendar
        ? $"Bulk{(grant ? "Set" : "Remove")}Calendar_NoUser"
        : $"Bulk{(grant ? "Add" : "Remove")}_NoUser";

    [Theory]
    [MemberData(nameof(BlankUserRows))]
    public async Task ABulkRowThatNamesNoUser_IsAnInvalidRow_RefusedBeforeAnyCheck(bool calendar, bool grant, string user)
    {
        var resolver = ExchangeKnowsTheTarget();

        var bulk = await RunBulk(calendar, grant, Row(calendar, user), resolver);

        Assert.Equal(0, bulk.SuccessCount);
        Assert.Equal(1, bulk.FailedCount);
        var record = Assert.Single(_h.AuditRows());
        Assert.Equal(NoUserAction(calendar, grant), Text(record, "action"));
        Assert.Equal(NoUser, Text(record, "error"));
        Assert.Equal("Failed", Text(record, "result"));
        var entry = Assert.Single(bulk.Entries);
        Assert.Equal("FAILED", entry.Status);
        Assert.Equal("No user specified", entry.Message);
        Assert.Equal($"{Target}/{user}: {NoUser}", Assert.Single(bulk.Errors));
        await resolver.DidNotReceive().ResolveRecipientAsync(Arg.Any<string>());
        Assert.Empty(_h.Forest.Calls);
        Assert.Empty(_h.Writes);
        Assert.Equal(0, _h.Connects);
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public async Task ARowAfterOneThatNamesNoUser_IsStillProcessed(bool calendar, bool grant)
    {
        // The control: the invalid row ends that row only.
        var resolver = ExchangeKnowsTheTarget();
        resolver.ResolveRecipientAsync(Upn).Returns(Recipient(Upn, "ext-jdoe"));

        var bulk = await RunBulk(calendar, grant, Row(calendar, "") + Row(calendar, Other), resolver);

        Assert.Equal(1, bulk.SuccessCount);
        Assert.Equal(1, bulk.FailedCount);
        Assert.Equal(["FAILED", "SUCCESS"], bulk.Entries.Select(entry => entry.Status));
        Assert.Single(_h.AuditRows(), row => Text(row, "action") == NoUserAction(calendar, grant));
        Assert.NotEmpty(_h.Writes);
    }

    /// <summary>
    /// "unreachable": Exchange cannot answer for the operator, so the check cannot be completed. "alias": Exchange names
    /// the grantee as the operator's own mailbox, a self-grant the string match misses. "no operator": the loop was handed
    /// no principal, so who is granting cannot be established.
    /// </summary>
    private (IIdentityResolver resolver, string grantee, string expected, ClaimsPrincipal? actingUser) Shape(string shape)
    {
        var resolver = ExchangeKnowsTheTarget();
        switch (shape)
        {
            case "unreachable":
                Unreachable(resolver, Upn);
                return (resolver, Other, Incomplete(Upn), Jdoe);
            case "no operator":
                return (resolver, Other, IncompleteBecause("the signed-in operator could not be identified"), null);
            default:
                var own = Recipient(Upn, "ext-jdoe");
                resolver.ResolveRecipientAsync(Upn).Returns(own);
                resolver.ResolveRecipientAsync(CloudAlias).Returns(own);
                return (resolver, CloudAlias, SelfGrantRefusal, Jdoe);
        }
    }

    // ---- the source ------------------------------------------------------------------------------

    [Theory]
    [InlineData("Components/Pages/MailboxPermissions.razor", 2)]
    [InlineData("Components/Pages/CalendarPermissions.razor", 2)]
    [InlineData("Services/MailboxPermissionService.cs", 1)]
    [InlineData("Services/CalendarPermissionService.cs", 1)]
    public void EverySelfGrantRecord_CarriesTheValidatorsOwnRefusal(string relativePath, int sites)
    {
        // The two bulk loops are driven above and the two pages in PermissionPagesSelfGrantTests; this reads every record
        // from the source, comments blanked. Each records the refusal it was handed, as their protected-target denials
        // already do, rather than a fixed sentence that says every refusal was a self-grant. aud-a50: each page records
        // the refusal in SubmitSingle and again in ExecuteOnPrem, which checks the grantee right before the on-prem write.
        var path = AuditCategoryFilingTests.FindRepoFile(relativePath.Split('/'));
        var code = RazorSyntax.CommentsBlanked(File.ReadAllText(path));

        var records = Regex.Matches(code, @"_SelfGrantAttempt""").ToList();
        Assert.Equal(sites, records.Count);
        foreach (var record in records)
        {
            var detail = Regex.Match(code[record.Index..], @"errorDetail:\s*([^,)]+)");

            Assert.True(detail.Success, $"{relativePath}: a self-grant record names no errorDetail.");
            Assert.Equal("selfGrantError", detail.Groups[1].Value.Trim());
        }
    }

    [Theory]
    [InlineData("Components/Pages/MailboxPermissions.razor", new[] { "validationAuthState.User", "onPremAuthState.User" })]
    [InlineData("Components/Pages/CalendarPermissions.razor", new[] { "validationAuthState.User", "onPremAuthState.User" })]
    [InlineData("Services/MailboxPermissionService.cs", new[] { "actingUser" })]
    [InlineData("Services/CalendarPermissionService.cs", new[] { "actingUser" })]
    public void EverySelfGrantCheck_IsHandedTheSignedInPrincipal_NeverTheLogonName(string relativePath, string[] principals)
    {
        // The pages hand over the token the protected-target check was decided against - SubmitSingle's, and ExecuteOnPrem's
        // own re-check's (aud-a50); the bulk loops the principal they already hold for it. The logon name (currentUser)
        // stays the audit's actor and nothing more.
        var path = AuditCategoryFilingTests.FindRepoFile(relativePath.Split('/'));
        var code = RazorSyntax.CommentsBlanked(File.ReadAllText(path));

        var calls = Regex.Matches(code, @"ValidateSelfGrantAsync\(\s*([^,]+?)\s*,").Select(call => call.Groups[1].Value).ToArray();

        Assert.Equal(principals, calls);
    }

    [Fact]
    public void TheStringOnlySelfGrantCheck_IsGone_AndTheCheckTakesThePrincipal()
    {
        const BindingFlags Every = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;

        Assert.DoesNotContain(typeof(PermissionValidator).GetMethods(Every), method => method.Name == "ValidateSelfGrant");
        var check = Assert.Single(typeof(PermissionValidator).GetMethods(Every),
            method => method.Name == nameof(PermissionValidator.ValidateSelfGrantAsync));
        Assert.Equal(new[] { typeof(ClaimsPrincipal), typeof(string) }, check.GetParameters().Select(p => p.ParameterType));
    }
}
