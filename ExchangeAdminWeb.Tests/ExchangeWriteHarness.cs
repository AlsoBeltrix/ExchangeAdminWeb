using System.Collections;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text.Json;
using ExchangeAdminWeb.Modules;
using ExchangeAdminWeb.Services;
using ExchangeAdminWeb.Services.Storage;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// aud-a2d's harness: the Exchange writes of the four Exchange-written modules, driven for real
/// against stand-in cmdlets that record exactly what they were bound with.
/// </summary>
/// <remarks>
/// A real <see cref="ExoConnectionPool"/> over a temp SQLite config store; only
/// Connect-ExchangeOnline is replaced, through the internal connect seam, by an opened local
/// runspace whose functions stand in for the Exchange cmdlets (the psl-1 shape, as in
/// <see cref="ExchangeServiceBasePooledQueryTests"/>). <c>Get-Mailbox</c> and the other reads answer
/// from <see cref="Mailboxes"/>; every cmdlet records its bound parameters in <see cref="Calls"/>.
/// The on-prem and Active Directory paths get the same stand-ins through their own test seams
/// (<see cref="DefineOnPremSession"/>, <see cref="OpenDirectoryRunspace"/>). No Exchange module,
/// credential, directory or network connection is used.
/// </remarks>
internal sealed class ExchangeWriteHarness : IDisposable
{
    public string Dir { get; } = Path.Combine(Path.GetTempPath(), "eaw-a2d-" + Guid.NewGuid().ToString("N"));
    public IConfiguration Config { get; }
    public ModuleConfigService ModuleConfig { get; }
    public ModuleEnablementService Enablement { get; }
    public OperationTraceService Trace { get; }
    public JsonlLogService Jsonl { get; }
    public AuditService Audit { get; }
    public ExoConnectionPool Pool { get; }

    /// <summary>Every stand-in call in order: a Hashtable of its bound parameters plus <c>__Command</c>.</summary>
    public IList Calls { get; } = ArrayList.Synchronized(new ArrayList());

    /// <summary>
    /// What the stand-in reads answer, keyed by any identity that resolves to the entry - the GUID,
    /// the primary address and any alias registered. Case-insensitive, as Exchange is.
    /// </summary>
    public Hashtable Mailboxes { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// What the stand-in <c>Get-ADUser -Identity</c> answers, keyed by objectGUID - and, since aud-a2p, what the
    /// stand-in <c>Set-ADUser</c>'s <c>-Replace</c> changes, so a read-back sees what a write did.
    /// </summary>
    public Hashtable DirectoryObjects { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// Command name to error message: that stand-in records its call, then throws the message - or, given an
    /// exception, throws it as it is, so it becomes the <c>InnerException</c> of what PowerShell throws (aud-a2n).
    /// </summary>
    /// <remarks>
    /// Only for a command the code calls with <c>-ErrorAction Stop</c> (or none). Called with
    /// <c>SilentlyContinue</c>, PowerShell suppresses the throw and the stand-in carries on and answers.
    /// </remarks>
    public Hashtable Failures { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// aud-a2s. <see cref="Failures"/> for the directory stand-ins, once a <c>Set-Place</c> has been called: the same keys and
    /// the same failure, made only by a call after one - the on-prem room-list add's steps, not its preflight's, which
    /// come before it.
    /// </summary>
    public Hashtable FailuresAfterSetPlace { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// aud-a2s. The key, in <see cref="Failures"/> or <see cref="FailuresAfterSetPlace"/>, that fails a stand-in
    /// <c>Get-ADGroup</c> read asking for the <c>member</c> attribute - the membership read - with its message, and no other
    /// read. <c>Get-ADGroup -Identity</c> fails every read by identity, so the group's re-read, which comes first.
    /// </summary>
    public const string MemberReadFailureKey = "Get-ADGroup -Properties member";

    /// <summary>
    /// The primary addresses the stand-in <c>Get-DistributionGroupMember</c> reports on any list: all of
    /// them to <c>-ResultSize Unlimited</c>, the first N to a number, and none - an error - to a read
    /// without the parameter (aud-a2h).
    /// </summary>
    public IList ListMembers { get; } = ArrayList.Synchronized(new ArrayList());

    /// <summary>
    /// Exchange Online's default page for <c>Get-DistributionGroupMember</c>: the most members a read
    /// without <c>-ResultSize</c> returns. A test list one longer has a member only the whole list shows.
    /// </summary>
    public const int ExchangeDefaultResultSize = 1000;

    /// <summary>
    /// What the stand-in <c>Get-DistributionGroup</c> reports as <c>IsDirSynced</c>, by list name: a
    /// bool, or null to leave the property out. A list not named here reports False - a cloud list.
    /// </summary>
    public Hashtable ListSyncState { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// aud-a2k. The <c>PrimarySmtpAddress</c> of a list <see cref="ListAddresses"/> does not name, and
    /// the <c>mail</c> of a group <see cref="DirectoryGroupAttributes"/> does not name: so, unless a test
    /// says otherwise, every list's on-prem group is every stand-in group - the shape the tests written
    /// before aud-a2k assume.
    /// </summary>
    public const string DefaultListAddress = "list@contoso.test";

    /// <summary>
    /// aud-a2k. What the stand-in <c>Get-DistributionGroup</c> reports as <c>PrimarySmtpAddress</c>, by
    /// list name: an address (an empty or blank one included), or null to leave the property out.
    /// </summary>
    public Hashtable ListAddresses { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>aud-a2m. The <c>Guid</c> the stand-in <c>Get-DistributionGroup</c> reports for a list <see cref="ListGuids"/> does not name.</summary>
    public const string DefaultListGuid = "21212121-2121-4121-8121-212121212121";

    /// <summary>
    /// aud-a2m. What the stand-in <c>Get-DistributionGroup</c> reports as <c>Guid</c>, by list name: a GUID,
    /// any other string (reported as given, as a malformed value would be), or null to leave the property out.
    /// </summary>
    public Hashtable ListGuids { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>aud-a2m. The list names the stand-in <c>Get-DistributionGroup</c> does not find: a Building with no room list yet.</summary>
    public IList MissingLists { get; } = ArrayList.Synchronized(new ArrayList());

    /// <summary>aud-a2m. The <c>Guid</c> of the group the stand-in <c>New-DistributionGroup</c> returns unless a test changes <see cref="CreatedListGuid"/>.</summary>
    public const string DefaultCreatedListGuid = "31313131-3131-4131-8131-313131313131";

    /// <summary>
    /// aud-a2m. The <c>Guid</c> of the group the stand-in <c>New-DistributionGroup</c> returns, as
    /// <see cref="ListGuids"/> reports one; null returns the group without it. Read when an Exchange runspace opens.
    /// </summary>
    public string? CreatedListGuid { get; set; } = DefaultCreatedListGuid;

    /// <summary>
    /// aud-a2m. A list name that a second recipient also answers to, and that recipient's <c>Guid</c>: the
    /// stand-in <c>Get-DistributionGroupMember</c> and <c>Add-DistributionGroupMember</c> refuse that name as
    /// ambiguous, as Exchange refuses an identity matching more than one object. Every other identity -
    /// the list's <c>Guid</c> included - is answered as the list.
    /// </summary>
    public Hashtable ListNamesakes { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// aud-a2k. The attributes a stand-in group search is matched against, by objectGUID - set through
    /// <see cref="SetDirectoryGroupAttributes"/>. A group named here has exactly those attributes; one
    /// not named has <c>mail</c> <see cref="DefaultListAddress"/> and its CN, <c>Group &lt;objectGUID&gt;</c>,
    /// as both <c>displayName</c> and <c>name</c>.
    /// </summary>
    public Hashtable DirectoryGroupAttributes { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>aud-a2k. Gives the group <paramref name="objectGuid"/> exactly these attributes; a null one is absent, as an unset attribute is in Active Directory.</summary>
    public void SetDirectoryGroupAttributes(string objectGuid, string? mail, string? displayName, string? name)
    {
        var attributes = new Hashtable(StringComparer.OrdinalIgnoreCase);
        if (mail is not null)
            attributes["mail"] = mail;
        if (displayName is not null)
            attributes["displayName"] = displayName;
        if (name is not null)
            attributes["name"] = name;
        DirectoryGroupAttributes[objectGuid] = attributes;
    }

    /// <summary>The objectGUID of the one group the stand-in <c>Get-ADGroup</c> search returns unless a test changes it.</summary>
    public const string DefaultDirectoryGroupGuid = "99999999-9999-9999-9999-999999999999";

    /// <summary>
    /// The objectGUIDs of the stand-in directory's groups: one unless a test changes it. A
    /// <c>Get-ADGroup</c> search returns every one of them in a domain the call reaches (aud-a2j:
    /// <see cref="DirectoryGroupDomains"/> places each) whose attributes its filter matches (aud-a2k:
    /// <see cref="DirectoryGroupAttributes"/>).
    /// </summary>
    public IList DirectoryGroupGuids { get; } = ArrayList.Synchronized(new ArrayList { DefaultDirectoryGroupGuid });

    /// <summary>
    /// The stand-in forest's default domain, as the DC part of a DN: the one a directory call reaches
    /// with no <c>-Server</c>, and where a group lives unless <see cref="DirectoryGroupDomains"/> says
    /// otherwise. To the stand-in <c>Get-ADGroupMember</c> a member DN outside it is a member in another
    /// domain (aud-a2i).
    /// </summary>
    public const string DirectoryDomainDn = "DC=contoso,DC=test";

    /// <summary>
    /// aud-a2j. The forest's other domain - the a2i fixtures' "another domain". A group placed in it is
    /// reached only through the global catalog or by this domain's own DNS name, <c>fabrikam.test</c>.
    /// </summary>
    public const string OtherDirectoryDomainDn = "DC=fabrikam,DC=test";

    /// <summary>aud-a2j. Every domain of the stand-in forest, by DN: what a domain's DNS name in <c>-Server</c> is matched against.</summary>
    public IList DirectoryDomains { get; } = ArrayList.Synchronized(new ArrayList { DirectoryDomainDn, OtherDirectoryDomainDn });

    /// <summary>aud-a2j. The domain DN a group's objectGUID lives in; a group not named here is in <see cref="DirectoryDomainDn"/>.</summary>
    public Hashtable DirectoryGroupDomains { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>A stand-in group's DN from its objectGUID and its domain: what the stand-in <c>Get-ADGroup</c> reports as DistinguishedName.</summary>
    public const string DirectoryGroupDnFormat = "CN=Group {0},OU=Groups,{1}";

    /// <summary>The DN of the stand-in group whose objectGUID is <paramref name="objectGuid"/>, in the default domain.</summary>
    public static string DirectoryGroupDn(string objectGuid) => DirectoryGroupDn(objectGuid, DirectoryDomainDn);

    /// <summary>The DN of the stand-in group whose objectGUID is <paramref name="objectGuid"/>, in <paramref name="domainDn"/>.</summary>
    public static string DirectoryGroupDn(string objectGuid, string domainDn) =>
        string.Format(System.Globalization.CultureInfo.InvariantCulture, DirectoryGroupDnFormat, objectGuid, domainDn);

    /// <summary>aud-a2j. The host the stand-in <c>Get-ADForest</c> reports as its global catalog unless a test changes <see cref="GlobalCatalogs"/>.</summary>
    public const string GlobalCatalogHost = "gc01.contoso.test";

    /// <summary>aud-a2j. The <c>-Server</c> that reaches every domain of the stand-in forest: the catalog host on port 3268.</summary>
    public const string GlobalCatalogEndpoint = GlobalCatalogHost + ":3268";

    /// <summary>
    /// aud-a2j. What the stand-in <c>Get-ADForest</c> reports as <c>GlobalCatalogs</c>: one host unless
    /// a test clears it (no catalog) or adds a blank one - the empty host that once produced the bare
    /// <c>:3268</c>. <c>Failures['Get-ADForest']</c> fails the read itself.
    /// </summary>
    public IList GlobalCatalogs { get; } = ArrayList.Synchronized(new ArrayList { GlobalCatalogHost });

    /// <summary>
    /// The member DNs on the stand-in group's linked <c>member</c> attribute - what
    /// <c>Get-ADGroup -Properties member</c> returns, and what the stand-in <c>Get-ADGroupMember</c>
    /// resolves against <see cref="DirectoryObjects"/>: none unless a test adds them (aud-a2i), or the
    /// stand-in <c>Set-ADGroup</c> does (aud-a2l). One list for every stand-in group.
    /// </summary>
    public IList DirectoryGroupMembers { get; } = ArrayList.Synchronized(new ArrayList());

    /// <summary>
    /// What the stand-in <c>Get-ADGroup -Identity</c> answers as ObjectGUID, by the identity asked: a
    /// test's way of making Active Directory answer with another object. An identity not named here
    /// answers as its own group.
    /// </summary>
    public Hashtable DirectoryGroupReadBack { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// aud-a2j. The same, for a read that asks for the <c>member</c> attribute only - the membership
    /// read - and taken before <see cref="DirectoryGroupReadBack"/>. Since the re-read of the group in
    /// its own domain comes first, this is how a test reaches the membership read's own objectGUID
    /// check: the group answers as itself to the re-read and as another object to the read after it.
    /// </summary>
    public Hashtable DirectoryGroupMemberReadBack { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// aud-a2l. The text of Active Directory's refusal of a member-attribute add for a DN the group already
    /// holds: the Win32 1378 text, one of the forms <c>ConferenceRoomService.IsDuplicateMemberValue</c>
    /// recognizes (which form the real cmdlet reports is not established - no directory is probed). Since
    /// aud-a2n the message of the coded refusal the stand-in raises (<see cref="DuplicateMemberValueRefusal"/>),
    /// and, as a <see cref="Failures"/> entry, the refusal with no code at all.
    /// </summary>
    public const string DuplicateMemberValueError = "The specified account name is already a member of the group.";

    /// <summary>
    /// aud-a2n. What the stand-in <c>Set-ADGroup</c> raises for a DN the group already holds, through
    /// <c>Write-Error -Exception</c> - so under <c>-ErrorAction Stop</c> it sits in the thrown exception's error
    /// record, not in its <c>InnerException</c>: unless a test replaces it, a directory exception with the
    /// 1378 code and text (<c>ERROR_MEMBER_IN_ALIAS</c>; which code the real module raises is not established -
    /// no directory is probed). Read when a directory runspace opens.
    /// </summary>
    public Exception DuplicateMemberValueRefusal { get; set; } =
        new Microsoft.ActiveDirectory.Management.ADException(DuplicateMemberValueError, 1378);

    /// <summary>
    /// aud-a2n. The stand-in <c>Set-ADGroup</c> applies the add and then throws this (a message, or an exception
    /// as it is) - the write that committed and then failed, a timeout after the server took it. Null, the
    /// default, throws nothing. Read when a directory runspace opens.
    /// </summary>
    public object? SetAdGroupFailsAfterApplying { get; set; }

    /// <summary>
    /// aud-a2n. Once a <c>Set-ADGroup</c> has been called, every read of a group's <c>member</c> attribute fails
    /// with this message: a read-back that cannot be made, the reads before the write unaffected. Null, the
    /// default, fails nothing. Read when a directory runspace opens.
    /// </summary>
    public string? MemberReadBackFailure { get; set; }

    /// <summary>
    /// aud-a2o. After the add, the stand-in <c>Set-ADGroup</c> reports this as an error that <c>-ErrorAction Stop</c>
    /// does not throw - written with its own <c>-ErrorAction Continue</c> - and carries on: an error reported without
    /// a throw, which a cmdlet's own error under <c>-ErrorAction Stop</c> is not, so that the service can be shown to
    /// leave it unread. Null, the default, reports nothing. Read when a directory runspace opens.
    /// </summary>
    public string? SetAdGroupReportsErrorWithoutThrowing { get; set; }

    /// <summary>
    /// aud-a2l. The stand-in <c>Set-ADGroup</c> reports success and changes nothing - the write a domain
    /// controller dropped without saying, which only a read-back can catch. Read when a directory runspace
    /// opens.
    /// </summary>
    public bool SetAdGroupChangesNothing { get; set; }

    /// <summary>
    /// aud-a2p. The stand-in <c>Set-ADUser</c> reports success and changes nothing - the City/State/Country write a
    /// domain controller dropped without saying, which only a read-back can catch. Read when a directory runspace opens.
    /// </summary>
    public bool SetAdUserChangesNothing { get; set; }

    /// <summary>
    /// aud-a2p. After applying its <c>-Replace</c>, the stand-in <c>Set-ADUser</c> reports this as an error that
    /// <c>-ErrorAction Stop</c> does not throw - written with its own <c>-ErrorAction Continue</c> - and carries on:
    /// aud-a2o's knob, on this write, so that the service can be shown to leave it unread. Null, the default, reports
    /// nothing. Read when a directory runspace opens.
    /// </summary>
    public string? SetAdUserReportsErrorWithoutThrowing { get; set; }

    /// <summary>
    /// aud-a2p. Once a <c>Set-ADUser</c> has been called, a <c>Get-ADUser</c> asking for <c>-Properties</c> - the
    /// read-back - fails with this message; a read before any write, or one without <c>-Properties</c> (the reads that
    /// prove the object), is unaffected. Null, the default, fails nothing. Read when a directory runspace opens.
    /// </summary>
    public string? AttributeReadBackFailure { get; set; }

    /// <summary>
    /// aud-a2p. Once a <c>Set-ADUser</c> has been called, a <c>Get-ADUser</c> asking for <c>-Properties</c> answers with
    /// this objectGUID - another object answering the read-back; the reads that prove the object are unaffected. Null,
    /// the default, answers as the object asked for. Read when a directory runspace opens.
    /// </summary>
    public string? AttributeReadBackAnswersAs { get; set; }

    /// <summary>
    /// aud-a2q. A <c>-Server</c> value the stand-in <c>Get-ADUser</c> and <c>Set-ADUser</c> refuse every call sent to, with the
    /// message given: a domain this host reaches through the default domain controller - a call with no <c>-Server</c> is
    /// untouched - but not by its DNS name, or one the module credential cannot use there. A <c>-Server</c> the stand-in
    /// forest does not have at all both refuse as "Unable to contact the server".
    /// </summary>
    public Hashtable DirectoryServerFailures { get; } = Hashtable.Synchronized(new Hashtable(StringComparer.OrdinalIgnoreCase));

    /// <summary>
    /// aud-a2q. The stand-in <c>Set-ADUser</c> applies its <c>-Replace</c> and then throws this (a message, or an exception as
    /// it is) - the write that committed and then failed, a timeout after the server took it. Null, the default, throws
    /// nothing. Read when a directory runspace opens.
    /// </summary>
    public object? SetAdUserFailsAfterApplying { get; set; }

    /// <summary>
    /// aud-a2q. Once a <c>Set-Place</c> has been called, opening a directory runspace throws this: the City/State/Country
    /// write's runspace fails, the preflight's - opened before <c>Set-Place</c> - does not. Null, the default, fails nothing.
    /// </summary>
    public string? DirectoryRunspaceFailureAfterSetPlace { get; set; }

    /// <summary>
    /// aud-a2q. Once a <c>Set-Place</c> has been called, the stand-in <c>Import-Module</c> throws this - the write's import,
    /// not the preflight's. Null, the default, fails nothing. Read when a directory runspace opens.
    /// </summary>
    public string? ImportModuleFailureAfterSetPlace { get; set; }

    /// <summary>
    /// aud-a2q. Once a <c>Set-Place</c> has been called, a <c>Get-ADUser -Identity</c> without <c>-Properties</c> - the read
    /// that proves the object before a write, not the read-back - throws this. Null, the default, fails nothing. Read
    /// when a directory runspace opens.
    /// </summary>
    public string? ObjectReadFailureAfterSetPlace { get; set; }

    /// <summary>
    /// aud-a2q. Once a <c>Set-Place</c> has been called, the stand-in <c>Get-ADUser</c> answers with this as the object's
    /// DistinguishedName: the object read differently after the preflight, so a write that derived its server from its
    /// own read would go elsewhere. Null, the default, changes nothing. Read when a directory runspace opens.
    /// </summary>
    public string? ObjectDnAfterSetPlace { get; set; }

    /// <summary>aud-a2r. Opening a directory runspace throws this, at any time - the first AD step's runspace fails. Null, the default, fails nothing.</summary>
    public string? DirectoryRunspaceFailure { get; set; }

    /// <summary>
    /// aud-a2r. The stand-in <c>Import-Module</c> fails with this at any time, as the real cmdlet reports a module it
    /// cannot load: a non-terminating error, thrown only by the caller's <c>-ErrorAction Stop</c>. Null, the default,
    /// fails nothing. Read when a directory runspace opens.
    /// </summary>
    public string? ImportModuleFailure { get; set; }

    /// <summary>
    /// aud-a2r. The stand-in <c>Import-Module</c> reports this as an error that <c>-ErrorAction Stop</c> does not throw -
    /// written with its own <c>-ErrorAction Continue</c> - and carries on, the module loaded. Null, the default, reports
    /// nothing. Read when a directory runspace opens.
    /// </summary>
    public string? ImportModuleReportsErrorWithoutThrowing { get; set; }

    /// <summary>
    /// aud-a2r. A <c>Get-ADUser -Identity</c> without <c>-Properties</c> - the read that proves the object - answers with
    /// this objectGUID, at any time. Null, the default, answers as the object asked for. Read when a directory runspace opens.
    /// </summary>
    public string? ObjectReadAnswersAs { get; set; }

    /// <summary>aud-a2r. The same, only once a <c>Set-Place</c> has been called. Read when a directory runspace opens.</summary>
    public string? ObjectReadAnswersAsAfterSetPlace { get; set; }

    /// <summary>
    /// aud-a2l. Another actor's add of the same DN lands just before the stand-in <c>Set-ADGroup</c>'s:
    /// the DN is put on the group first, so the write is refused as a value already there (or fails with
    /// whatever <see cref="Failures"/> names). The race codex raised. Read when a directory runspace opens.
    /// </summary>
    public bool AnotherAddLandsFirst { get; set; }

    /// <summary>
    /// The default ADWS <c>MaxGroupOrMemberEntries</c>: past this many members the real
    /// <c>Get-ADGroupMember</c> refuses the read ("The size limit for this request was exceeded"), and so
    /// does the stand-in.
    /// </summary>
    public const int AdwsMaxGroupOrMemberEntries = 5000;

    private int _connects;

    /// <summary>How many Exchange connections the pool opened - zero proves nothing was borrowed.</summary>
    public int Connects => Volatile.Read(ref _connects);

    public ExchangeWriteHarness()
    {
        Directory.CreateDirectory(Dir);
        Config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Audit:LogRoot"] = Dir,
            ["Delinea:SecretServerUrl"] = "https://vault.invalid",
        }).Build();
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(Dir);

        var store = TestConfigStore.Create(Dir);
        var catalog = new ModuleCatalog();
        ModuleConfig = new ModuleConfigService(catalog, env, new ModuleConfigRepository(store), NullLogger<ModuleConfigService>.Instance);
        Enablement = new ModuleEnablementService(catalog, env, ModuleConfig, new ModuleEnablementRepository(store), Config,
            NullLogger<ModuleEnablementService>.Instance);
        Enablement.SaveEnablement(new Dictionary<string, bool> { [ExoConnectionPool.ConfigModuleKey] = true });
        ModuleConfig.SaveModuleConfig(ExoConnectionPool.ConfigModuleKey, new Dictionary<string, string>
        {
            [ExoConnectionPool.ConfigAppIdKey] = "11111111-1111-1111-1111-111111111111",
            [ExoConnectionPool.ConfigOrganizationKey] = "contoso.onmicrosoft.com",
            [ExoConnectionPool.ConfigCertSubjectKey] = "CN=EXO-Automation",
        });
        Jsonl = new JsonlLogService(Config, NullLogger<JsonlLogService>.Instance);
        Trace = new OperationTraceService(Config, Jsonl);
        Audit = new AuditService(Jsonl, Trace);
        Pool = new ExoConnectionPool(Config, ModuleConfig, Enablement, NullLogger<ExoConnectionPool>.Instance, Trace,
            connect: (_, generation, _) =>
            {
                Interlocked.Increment(ref _connects);
                return StandIn(generation);
            });
    }

    public void Dispose()
    {
        Pool.Dispose();
        try { Directory.Delete(Dir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>
    /// Registers a mailbox under its GUID, its primary address and every alias given. An alias
    /// registered on a DIFFERENT mailbox is how a test makes the typed string and the validated
    /// mailbox name two objects.
    /// </summary>
    public void AddMailbox(string primary, Guid exchangeGuid, string recipientTypeDetails = "UserMailbox",
        string calendarFolder = "Calendar", params string[] aliases)
    {
        var entry = new Hashtable
        {
            ["PrimarySmtpAddress"] = primary,
            ["ExchangeGuid"] = exchangeGuid,
            ["RecipientTypeDetails"] = recipientTypeDetails,
            ["DisplayName"] = primary,
            ["Identity"] = "OBJ:" + primary,
            ["CalendarFolder"] = calendarFolder,
        };
        Mailboxes[exchangeGuid.ToString("D")] = entry;
        Mailboxes[primary] = entry;
        foreach (var alias in aliases)
            Mailboxes[alias] = entry;
    }

    /// <summary>
    /// A real <see cref="PermissionValidator"/> with no protection configured, asking Exchange
    /// through <paramref name="resolver"/> - so the bulk loops bind each row through a real check.
    /// </summary>
    public PermissionValidator NothingProtectedValidator(IIdentityResolver resolver)
    {
        var env = Substitute.For<IWebHostEnvironment>();
        env.ContentRootPath.Returns(Dir);
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient(Arg.Any<string>()).Returns(_ => new HttpClient());
        var extLog = new ExtendedLogService(Config, env, TestConfigStore.CreateAppSettings(Dir),
            NullLogger<ExtendedLogService>.Instance);
        var delinea = new DelineaService(httpClientFactory, Config, NullLogger<DelineaService>.Instance, extLog, Trace);
        var protectedPrincipals = new ProtectedPrincipalService(env, Config, ModuleConfig,
            TestConfigStore.CreateProtectedPrincipal(Dir), delinea, NullLogger<ProtectedPrincipalService>.Instance);
        var servicers = new ProtectedPrincipalServicerService(
            new SectionAccessService(Config, NullLogger<SectionAccessService>.Instance, env, new ModuleCatalog(),
                new SectionAccessRepository(TestConfigStore.Create(Dir))),
            NullLogger<ProtectedPrincipalServicerService>.Instance);
        var scopeFactory = Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScopeFactory>();
        var scope = Substitute.For<Microsoft.Extensions.DependencyInjection.IServiceScope>();
        var provider = Substitute.For<IServiceProvider>();
        scopeFactory.CreateScope().Returns(scope);
        scope.ServiceProvider.Returns(provider);
        provider.GetService(typeof(IIdentityResolver)).Returns(resolver);
        return new PermissionValidator(Config, ModuleConfig, Pool, protectedPrincipals, servicers,
            NullLogger<PermissionValidator>.Instance, scopeFactory);
    }

    /// <summary>
    /// An Exchange resolver that knows <paramref name="typed"/> as <paramref name="mailbox"/> and
    /// nobody else: every other identity, the self-grant check's included, is "no such recipient".
    /// </summary>
    public static IIdentityResolver ExchangeKnows(string typed, string primary, Guid mailbox)
    {
        var resolver = Substitute.For<IIdentityResolver>();
        resolver.ResolveRecipientAsync(Arg.Any<string>()).Returns((ResolvedRecipient?)null);
        resolver.ResolveRecipientAsync(typed).Returns(
            new ResolvedRecipient(primary, "ext-" + primary, "UserMailbox", ExistsOnPrem: false, ExchangeGuid: mailbox));
        return resolver;
    }

    /// <summary>A validated mailbox as the protected-principal check would hand it on.</summary>
    public static ValidatedMailbox Validated(Guid exchangeGuid, string? directoryObjectGuid = null) =>
        ValidatedMailbox.From(new ResolvedDirectoryPrincipal(
            "Test", "test", "test@contoso.test", null, "test@contoso.test", null, directoryObjectGuid, null,
            ExchangeGuid: exchangeGuid))!;

    public IReadOnlyList<Hashtable> CallsTo(string command) =>
        Calls.Cast<Hashtable>().Where(c => (string)c["__Command"]! == command).ToList();

    public IReadOnlyList<string> Commands => Calls.Cast<Hashtable>().Select(c => (string)c["__Command"]!).ToList();

    /// <summary>The commands that change something - every recorded call that is not a read.</summary>
    public IReadOnlyList<string> Writes => Commands.Where(c => !c.StartsWith("Get-", StringComparison.Ordinal)
        && c is not "Invoke-Command" and not "Import-Module").ToList();

    public static object? Bound(Hashtable call, string parameter) =>
        call[parameter] is PSObject wrapped ? wrapped.BaseObject : call[parameter];

    /// <summary>
    /// aud-a2j. The member DN a <c>Set-ADGroup -Add @{ member = ... }</c> call added - how the on-prem
    /// room-list add now writes - or null when the call added none.
    /// </summary>
    public static string? AddedMember(Hashtable call) =>
        Bound(call, "Add") is IDictionary add && add["member"] is { } member
            ? (member is PSObject wrapped ? wrapped.BaseObject : member).ToString()
            : null;

    /// <summary>The <paramref name="index"/>-th argument an <c>Invoke-Command</c> sent to the remote script.</summary>
    public static string? Argument(Hashtable call, int index)
    {
        var item = Bound(call, "ArgumentList") switch
        {
            IList list when list.Count > index => list[index],
            _ => null,
        };
        return (item is PSObject wrapped ? wrapped.BaseObject : item)?.ToString();
    }

    public List<JsonElement> AuditRows()
    {
        var rows = new List<JsonElement>();
        foreach (var path in Jsonl.GetAuditLogPaths(DateTime.Now).Where(File.Exists))
        {
            foreach (var line in File.ReadAllLines(path).Where(line => !string.IsNullOrWhiteSpace(line)))
            {
                using var document = JsonDocument.Parse(line);
                if (document.RootElement.TryGetProperty("eventType", out var type) && type.GetString() == "audit")
                    rows.Add(document.RootElement.Clone());
            }
        }
        return rows;
    }

    /// <summary>
    /// What an on-prem <c>ConnectOnPrem</c> override does: defines the stand-ins in the service's own
    /// runspace, an <c>Invoke-Command</c> that records the script and its arguments and then runs it
    /// locally against them, and a marker session.
    /// </summary>
    public void DefineOnPremSession(PowerShell ps)
    {
        Define(ps.Runspace, StandIns + OnPremStandIns);
        ps.Runspace.SessionStateProxy.SetVariable("onpremSession", "stand-in-session");
    }

    /// <summary>What a Conference Rooms directory-runspace override returns: AD stand-ins, opened.</summary>
    public Runspace OpenDirectoryRunspace()
    {
        // aud-a2q. The runspace of an AD step after Set-Place - the City/State/Country write's - cannot be opened.
        if (DirectoryRunspaceFailureAfterSetPlace is { } failure && CallsTo("Set-Place").Count > 0)
            throw new InvalidOperationException(failure);
        // aud-a2r. Any directory runspace, the preflight's included: the first AD step's fails.
        if (DirectoryRunspaceFailure is { } anyTime)
            throw new InvalidOperationException(anyTime);
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        Define(rs, DirectoryStandIns);
        return rs;
    }

    /// <summary>The Exchange stand-ins, opened - what the pool's connect seam hands out; also for testing a stand-in itself.</summary>
    public Runspace OpenExchangeRunspace()
    {
        var rs = RunspaceFactory.CreateRunspace();
        rs.Open();
        Define(rs, StandIns);
        return rs;
    }

    private PooledRunspace StandIn(long generation)
    {
        var rs = OpenExchangeRunspace();
        var ps = PowerShell.Create();
        ps.Runspace = rs;
        return new PooledRunspace(rs, ps, generation, "stand-in");
    }

    private void Define(Runspace rs, string definitions)
    {
        rs.SessionStateProxy.SetVariable("Calls", Calls);
        rs.SessionStateProxy.SetVariable("Mailboxes", Mailboxes);
        rs.SessionStateProxy.SetVariable("DirectoryObjects", DirectoryObjects);
        rs.SessionStateProxy.SetVariable("Failures", Failures);
        rs.SessionStateProxy.SetVariable("FailuresAfterSetPlace", FailuresAfterSetPlace);
        rs.SessionStateProxy.SetVariable("ListMembers", ListMembers);
        rs.SessionStateProxy.SetVariable("ListSyncState", ListSyncState);
        rs.SessionStateProxy.SetVariable("DefaultListAddress", DefaultListAddress);
        rs.SessionStateProxy.SetVariable("ListAddresses", ListAddresses);
        rs.SessionStateProxy.SetVariable("DefaultListGuid", DefaultListGuid);
        rs.SessionStateProxy.SetVariable("ListGuids", ListGuids);
        rs.SessionStateProxy.SetVariable("MissingLists", MissingLists);
        rs.SessionStateProxy.SetVariable("CreatedListGuid", CreatedListGuid);
        rs.SessionStateProxy.SetVariable("ListNamesakes", ListNamesakes);
        rs.SessionStateProxy.SetVariable("DirectoryGroupAttributes", DirectoryGroupAttributes);
        rs.SessionStateProxy.SetVariable("DirectoryGroupGuids", DirectoryGroupGuids);
        rs.SessionStateProxy.SetVariable("DirectoryGroupMembers", DirectoryGroupMembers);
        rs.SessionStateProxy.SetVariable("DirectoryGroupReadBack", DirectoryGroupReadBack);
        rs.SessionStateProxy.SetVariable("DirectoryGroupMemberReadBack", DirectoryGroupMemberReadBack);
        rs.SessionStateProxy.SetVariable("DirectoryGroupDnFormat", DirectoryGroupDnFormat);
        rs.SessionStateProxy.SetVariable("DirectoryDomainDn", DirectoryDomainDn);
        rs.SessionStateProxy.SetVariable("DirectoryDomains", DirectoryDomains);
        rs.SessionStateProxy.SetVariable("DirectoryGroupDomains", DirectoryGroupDomains);
        rs.SessionStateProxy.SetVariable("GlobalCatalogs", GlobalCatalogs);
        rs.SessionStateProxy.SetVariable("AdwsMaxGroupOrMemberEntries", AdwsMaxGroupOrMemberEntries);
        rs.SessionStateProxy.SetVariable("DuplicateMemberValueError", DuplicateMemberValueError);
        rs.SessionStateProxy.SetVariable("DuplicateMemberValueRefusal", DuplicateMemberValueRefusal);
        rs.SessionStateProxy.SetVariable("SetAdGroupFailsAfterApplying", SetAdGroupFailsAfterApplying);
        rs.SessionStateProxy.SetVariable("MemberReadBackFailure", MemberReadBackFailure);
        rs.SessionStateProxy.SetVariable("SetAdGroupReportsErrorWithoutThrowing", SetAdGroupReportsErrorWithoutThrowing);
        rs.SessionStateProxy.SetVariable("SetAdGroupChangesNothing", SetAdGroupChangesNothing);
        rs.SessionStateProxy.SetVariable("SetAdUserChangesNothing", SetAdUserChangesNothing);
        rs.SessionStateProxy.SetVariable("SetAdUserReportsErrorWithoutThrowing", SetAdUserReportsErrorWithoutThrowing);
        rs.SessionStateProxy.SetVariable("AttributeReadBackFailure", AttributeReadBackFailure);
        rs.SessionStateProxy.SetVariable("AttributeReadBackAnswersAs", AttributeReadBackAnswersAs);
        rs.SessionStateProxy.SetVariable("DirectoryServerFailures", DirectoryServerFailures);
        rs.SessionStateProxy.SetVariable("SetAdUserFailsAfterApplying", SetAdUserFailsAfterApplying);
        rs.SessionStateProxy.SetVariable("ImportModuleFailureAfterSetPlace", ImportModuleFailureAfterSetPlace);
        rs.SessionStateProxy.SetVariable("ObjectReadFailureAfterSetPlace", ObjectReadFailureAfterSetPlace);
        rs.SessionStateProxy.SetVariable("ObjectDnAfterSetPlace", ObjectDnAfterSetPlace);
        rs.SessionStateProxy.SetVariable("ImportModuleFailure", ImportModuleFailure);
        rs.SessionStateProxy.SetVariable("ImportModuleReportsErrorWithoutThrowing", ImportModuleReportsErrorWithoutThrowing);
        rs.SessionStateProxy.SetVariable("ObjectReadAnswersAs", ObjectReadAnswersAs);
        rs.SessionStateProxy.SetVariable("ObjectReadAnswersAsAfterSetPlace", ObjectReadAnswersAsAfterSetPlace);
        rs.SessionStateProxy.SetVariable("AnotherAddLandsFirst", AnotherAddLandsFirst);
        using var init = PowerShell.Create();
        init.Runspace = rs;
        init.AddScript(definitions);
        init.Invoke();
        if (init.HadErrors)
            throw new InvalidOperationException("stand-in definitions failed: "
                + string.Join(" | ", init.Streams.Error.Select(e => e.ToString())));
    }

    // Recording helper plus one stand-in per cmdlet the four modules call. Advanced functions, so
    // a parameter the code passes and the stand-in does not declare is a binding error - a
    // stand-in cannot silently swallow an argument the real cmdlet would have received.
    private const string StandIns = """
        function global:Write-StandInCall([string] $Command, $Bound) {
            $row = @{ '__Command' = $Command }
            foreach ($key in $Bound.Keys) { if ($key -ne 'ErrorAction') { $row[$key] = $Bound[$key] } }
            [void]$global:Calls.Add($row)
            $failure = $global:Failures[$Command]
            if ($failure) { throw $failure }
        }
        # Not found is a non-terminating error, as on the real cmdlets: -ErrorAction Stop makes it
        # throw, -ErrorAction Ignore swallows it.
        function global:Find-StandInMailbox($Identity) {
            $entry = $global:Mailboxes["$Identity"]
            if ($null -eq $entry) { Write-Error "The operation couldn't be performed because object '$Identity' couldn't be found on 'stand-in'." }
            $entry
        }
        function global:Get-Mailbox {
            [CmdletBinding()] param([Parameter(Position = 0)] $Identity)
            Write-StandInCall 'Get-Mailbox' $PSBoundParameters
            $entry = Find-StandInMailbox $Identity
            if ($null -ne $entry) { New-Object psobject -Property $entry }
        }
        function global:Get-Recipient {
            [CmdletBinding()] param([Parameter(Position = 0)] $Identity)
            Write-StandInCall 'Get-Recipient' $PSBoundParameters
            $entry = $global:Mailboxes["$Identity"]
            if ($null -ne $entry) { New-Object psobject -Property $entry } else { [pscustomobject]@{ PrimarySmtpAddress = "$Identity" } }
        }
        function global:Add-MailboxPermission {
            [CmdletBinding()] param($Identity, $User, $AccessRights, $AutoMapping, $Confirm)
            Write-StandInCall 'Add-MailboxPermission' $PSBoundParameters
        }
        function global:Remove-MailboxPermission {
            [CmdletBinding()] param($Identity, $User, $AccessRights, $Confirm)
            Write-StandInCall 'Remove-MailboxPermission' $PSBoundParameters
        }
        function global:Add-RecipientPermission {
            [CmdletBinding()] param($Identity, $Trustee, $AccessRights, $Confirm)
            Write-StandInCall 'Add-RecipientPermission' $PSBoundParameters
        }
        function global:Remove-RecipientPermission {
            [CmdletBinding()] param($Identity, $Trustee, $AccessRights, $Confirm)
            Write-StandInCall 'Remove-RecipientPermission' $PSBoundParameters
        }
        function global:Get-MailboxFolderStatistics {
            [CmdletBinding()] param($Identity, $FolderScope)
            Write-StandInCall 'Get-MailboxFolderStatistics' $PSBoundParameters
            $entry = Find-StandInMailbox $Identity
            if ($null -ne $entry) { [pscustomobject]@{ FolderPath = '\' + $entry.CalendarFolder; FolderType = 'Calendar'; Name = $entry.CalendarFolder } }
        }
        function global:Get-MailboxFolderPermission {
            [CmdletBinding()] param($Identity)
            Write-StandInCall 'Get-MailboxFolderPermission' $PSBoundParameters
            [pscustomobject]@{ User = 'Default' }
            [pscustomobject]@{ User = 'previous.editor@contoso.test' }
        }
        function global:Set-MailboxFolderPermission {
            [CmdletBinding()] param($Identity, $User, $AccessRights)
            Write-StandInCall 'Set-MailboxFolderPermission' $PSBoundParameters
        }
        function global:Add-MailboxFolderPermission {
            [CmdletBinding()] param($Identity, $User, $AccessRights)
            Write-StandInCall 'Add-MailboxFolderPermission' $PSBoundParameters
        }
        function global:Remove-MailboxFolderPermission {
            [CmdletBinding()] param($Identity, $User, $Confirm)
            Write-StandInCall 'Remove-MailboxFolderPermission' $PSBoundParameters
        }
        function global:Get-MailboxAutoReplyConfiguration {
            [CmdletBinding()] param($Identity)
            Write-StandInCall 'Get-MailboxAutoReplyConfiguration' $PSBoundParameters
            if ($null -ne (Find-StandInMailbox $Identity)) {
                [pscustomobject]@{ AutoReplyState = 'Disabled'; InternalMessage = $null; ExternalMessage = $null; StartTime = $null; EndTime = $null }
            }
        }
        function global:Set-MailboxAutoReplyConfiguration {
            [CmdletBinding()] param($Identity, $AutoReplyState, $InternalMessage, $ExternalMessage, $StartTime, $EndTime)
            Write-StandInCall 'Set-MailboxAutoReplyConfiguration' $PSBoundParameters
        }
        function global:Get-Place {
            [CmdletBinding()] param($Identity)
            Write-StandInCall 'Get-Place' $PSBoundParameters
            if ($null -ne (Find-StandInMailbox $Identity)) {
                [pscustomobject]@{ City = ''; Building = ''; Capacity = 0; Floor = ''; FloorLabel = ''; DisplayDeviceName = ''; VideoDeviceName = ''; CountryOrRegion = ''; State = '' }
            }
        }
        function global:Set-Place {
            [CmdletBinding()] param($Identity, $Building, $Capacity, $Floor, $FloorLabel, $DisplayDeviceName, $VideoDeviceName)
            Write-StandInCall 'Set-Place' $PSBoundParameters
        }
        function global:Get-MailboxRegionalConfiguration {
            [CmdletBinding()] param($Identity)
            Write-StandInCall 'Get-MailboxRegionalConfiguration' $PSBoundParameters
            if ($null -ne (Find-StandInMailbox $Identity)) { [pscustomobject]@{ TimeZone = '' } }
        }
        function global:Set-MailboxRegionalConfiguration {
            [CmdletBinding()] param($Identity, $TimeZone)
            Write-StandInCall 'Set-MailboxRegionalConfiguration' $PSBoundParameters
        }
        function global:Set-MailboxCalendarConfiguration {
            [CmdletBinding()] param($Identity, $WorkingHoursTimeZone, $WorkDays)
            Write-StandInCall 'Set-MailboxCalendarConfiguration' $PSBoundParameters
        }
        function global:Set-CalendarProcessing {
            [CmdletBinding()] param($Identity, $AllowConflicts, $AutomateProcessing, $DeleteSubject, $AddOrganizerToSubject,
                $DeleteNonCalendarItems, $DeleteAttachments, $AllowRecurringMeetings, $MaximumDurationInMinutes,
                $EnforceSchedulingHorizon, $DeleteComments, $AllBookInPolicy, $AddAdditionalResponse, $AdditionalResponse,
                $BookingWindowInDays, $ProcessExternalMeetingMessages, $BookInPolicy, $ResourceDelegates)
            Write-StandInCall 'Set-CalendarProcessing' $PSBoundParameters
        }
        function global:Set-Mailbox {
            [CmdletBinding()] param($Identity, $CustomAttribute9, $MailTip)
            Write-StandInCall 'Set-Mailbox' $PSBoundParameters
        }
        # A list's IsDirSynced (ListSyncState, aud-a2f) and PrimarySmtpAddress (ListAddresses, aud-a2k): False
        # and DefaultListAddress unless a test says otherwise; a null in either leaves the property out.
        # aud-a2m. And its Guid (ListGuids): DefaultListGuid unless a test says otherwise, a value that is not a
        # GUID as given, a null none. A list in MissingLists is not found.
        function global:ConvertTo-StandInGuid($Value) {
            $parsed = [guid]::Empty
            if ([guid]::TryParse("$Value", [ref]$parsed)) { $parsed } else { "$Value" }
        }
        function global:Get-DistributionGroup {
            [CmdletBinding()] param($Identity)
            Write-StandInCall 'Get-DistributionGroup' $PSBoundParameters
            if (@($global:MissingLists | Where-Object { "$_" -ieq "$Identity" }).Count -gt 0) {
                Write-Error "The operation couldn't be performed because object '$Identity' couldn't be found on 'stand-in'."
                return
            }
            $list = @{ Name = "$Identity"; PrimarySmtpAddress = $global:DefaultListAddress; IsDirSynced = $false; Guid = [guid]$global:DefaultListGuid }
            if ($global:ListGuids.ContainsKey("$Identity")) {
                $guid = $global:ListGuids["$Identity"]
                if ($null -eq $guid) { $list.Remove('Guid') } else { $list['Guid'] = ConvertTo-StandInGuid $guid }
            }
            if ($global:ListAddresses.ContainsKey("$Identity")) {
                $address = $global:ListAddresses["$Identity"]
                if ($null -eq $address) { $list.Remove('PrimarySmtpAddress') } else { $list['PrimarySmtpAddress'] = $address }
            }
            if ($global:ListSyncState.ContainsKey("$Identity")) {
                $state = $global:ListSyncState["$Identity"]
                if ($null -eq $state) { $list.Remove('IsDirSynced') } else { $list['IsDirSynced'] = $state }
            }
            New-Object psobject -Property $list
        }
        # aud-a2m. Returns the group it made, as the real cmdlet does: CreatedListGuid its Guid, a null none.
        function global:New-DistributionGroup {
            [CmdletBinding()] param($Name, [switch] $RoomList)
            Write-StandInCall 'New-DistributionGroup' $PSBoundParameters
            $group = @{ Name = "$Name" }
            if ($null -ne $global:CreatedListGuid) { $group['Guid'] = ConvertTo-StandInGuid $global:CreatedListGuid }
            New-Object psobject -Property $group
        }
        # aud-a2m. A list name a second recipient also answers to (ListNamesakes) matches two objects, and Exchange
        # refuses such an identity. Write-Error then return, so the refusal stands under any -ErrorAction.
        function global:Test-StandInAmbiguousList($Identity) { $global:ListNamesakes.ContainsKey("$Identity") }
        # aud-a2h. Exchange Online returns at most -ResultSize members, 1000 when it is not passed. A read
        # without it is refused - an error and no members - rather than answered short, so no test can
        # see a member through a read the real cmdlet would have cut off. Write-Error then return, not
        # throw: called with -ErrorAction SilentlyContinue, as the service calls it, a throw is
        # suppressed and the function carries on.
        function global:Get-DistributionGroupMember {
            [CmdletBinding()] param($Identity, $ResultSize)
            Write-StandInCall 'Get-DistributionGroupMember' $PSBoundParameters
            if (Test-StandInAmbiguousList $Identity) { Write-Error "The operation couldn't be performed because '$Identity' matches multiple entries."; return }
            if (-not $PSBoundParameters.ContainsKey('ResultSize')) {
                Write-Error "stand-in: Get-DistributionGroupMember without -ResultSize - Exchange Online would answer with its default page only, not the whole list."
                return
            }
            $members = @($global:ListMembers)
            if ("$ResultSize" -ne 'Unlimited') { $members = @($members | Select-Object -First ([int]"$ResultSize")) }
            foreach ($member in $members) { [pscustomobject]@{ PrimarySmtpAddress = $member } }
        }
        function global:Add-DistributionGroupMember {
            [CmdletBinding()] param($Identity, $Member)
            Write-StandInCall 'Add-DistributionGroupMember' $PSBoundParameters
            if (Test-StandInAmbiguousList $Identity) { Write-Error "The operation couldn't be performed because '$Identity' matches multiple entries."; return }
        }
        function global:Disconnect-ExchangeOnline { param($ConnectionId, $Confirm) }

        """;

    // The remote session, run locally: the script the service sends and the arguments it binds,
    // recorded, then executed against the stand-ins above. Send As pipes a mailbox object into
    // the ADPermission cmdlets, so those bind -Identity by property name, as the real ones do.
    private const string OnPremStandIns = """
        function global:Invoke-Command {
            [CmdletBinding()] param($Session, [scriptblock] $ScriptBlock, [object[]] $ArgumentList)
            [void]$global:Calls.Add(@{ '__Command' = 'Invoke-Command'; Session = $Session; Script = $ScriptBlock.ToString(); ArgumentList = $ArgumentList })
            & $ScriptBlock @ArgumentList
        }
        function global:Remove-PSSession { param($Session) }
        function global:Add-ADPermission {
            [CmdletBinding()] param([Parameter(ValueFromPipelineByPropertyName)] $Identity, $User, $ExtendedRights, $Confirm)
            process { Write-StandInCall 'Add-ADPermission' $PSBoundParameters }
        }
        function global:Remove-ADPermission {
            [CmdletBinding()] param([Parameter(ValueFromPipelineByPropertyName)] $Identity, $User, $ExtendedRights, $Confirm)
            process { Write-StandInCall 'Remove-ADPermission' $PSBoundParameters }
        }

        """;

    // Active Directory, for the Conference Rooms City/State/Country and room-list steps.
    private const string DirectoryStandIns = """
        function global:Write-StandInCall([string] $Command, $Bound) {
            $row = @{ '__Command' = $Command }
            foreach ($key in $Bound.Keys) { if ($key -ne 'ErrorAction' -and $key -ne 'Credential') { $row[$key] = $Bound[$key] } }
            [void]$global:Calls.Add($row)
            $failure = $global:Failures[$Command]
            if ($failure) { throw $failure }
            # aud-a2s. And once a Set-Place is recorded, FailuresAfterSetPlace the same way: the room-list add's steps fail,
            # its preflight's - all made before Set-Place - do not.
            $later = $global:FailuresAfterSetPlace[$Command]
            if ($later -and (Test-StandInAfterSetPlace)) { throw $later }
        }
        # aud-a2q. Whether a Set-Place has been recorded: the knobs named "after Set-Place" fail the City/State/Country
        # write's steps and leave its preflight's - all made before Set-Place - alone. ImportModuleFailureAfterSetPlace
        # fails the import of a runspace's module after one.
        function global:Test-StandInAfterSetPlace { @($global:Calls | Where-Object { $_['__Command'] -eq 'Set-Place' }).Count -gt 0 }
        # aud-a2r. An advanced function, so the caller's -ErrorAction Stop acts on it as on the real cmdlet: it declared
        # $ErrorAction as a plain parameter, which the preference never reached. ImportModuleFailure is reported as the
        # real cmdlet reports a module it cannot load - a non-terminating error, thrown only by the caller's Stop;
        # ImportModuleReportsErrorWithoutThrowing is written past that, with its own -ErrorAction Continue, and the import
        # carries on, the module loaded.
        function global:Import-Module {
            [CmdletBinding()] param($Name)
            if ($global:ImportModuleFailureAfterSetPlace -and (Test-StandInAfterSetPlace)) { throw "$($global:ImportModuleFailureAfterSetPlace)" }
            if ($global:ImportModuleFailure) { Write-Error "$($global:ImportModuleFailure)"; return }
            if ($global:ImportModuleReportsErrorWithoutThrowing) { Write-Error "$($global:ImportModuleReportsErrorWithoutThrowing)" -ErrorAction Continue }
        }
        # aud-a2q. Why a call's -Server cannot be used, or $null: one DirectoryServerFailures names fails with its message;
        # one the stand-in forest does not have (Get-StandInReach) cannot be contacted; no -Server - the default domain -
        # is always usable.
        function global:Get-StandInServerFailure($Bound) {
            if (-not $Bound.ContainsKey('Server')) { return $null }
            $server = "$($Bound['Server'])"
            if ($global:DirectoryServerFailures.ContainsKey($server)) { return "$($global:DirectoryServerFailures[$server])" }
            if ($null -eq (Get-StandInReach $Bound)) { return "Unable to contact the server '$server'." }
            return $null
        }
        # aud-a2r. Where a directory object lives - the forest domain its DN ends with, else the default domain (no DN, or
        # one naming no domain of the forest: aud-a2q's fallback fixtures) - and whether a call's -Server reaches it, by the
        # reach a group is found by (Get-StandInReach, Test-StandInReaches, below): the default domain with no -Server or a
        # bare port, a domain by its DNS name, every domain through the catalog.
        function global:Get-StandInObjectDomain($Entry) {
            $dn = "$($Entry['DistinguishedName'])"
            foreach ($domain in @($global:DirectoryDomains)) {
                if ($dn.EndsWith(",$domain", [StringComparison]::OrdinalIgnoreCase)) { return "$domain" }
            }
            return $global:DirectoryDomainDn
        }
        function global:Test-StandInObjectReached($Bound, $Entry) {
            Test-StandInReaches (Get-StandInReach $Bound) (Get-StandInObjectDomain $Entry)
        }
        # aud-a2p. Set-ADUser applies its -Replace to the object in DirectoryObjects, so a read-back sees what it changed -
        # unless SetAdUserChangesNothing, a write that reports success and changes nothing - and, told to
        # (SetAdUserReportsErrorWithoutThrowing), then reports an error the caller's -ErrorAction Stop does not throw: its
        # own -ErrorAction Continue. An object it does not hold is refused (Write-Error then return, so it stays refused
        # under any -ErrorAction); Failures['Set-ADUser'] throws before anything is applied. A Get-ADUser asking for
        # -Properties after a Set-ADUser is the read-back: AttributeReadBackFailure fails it, AttributeReadBackAnswersAs
        # makes another object answer it. Both take -Server, which the code passes for the preflight's routed read, the
        # write and the read-back: since aud-a2q they refuse one Get-StandInServerFailure finds unusable (Write-Error then
        # return). aud-a2r: and they find an object only in a domain the call reaches (Test-StandInObjectReached), as the
        # real cmdlets do - Get-ADUser throws its not-found, Set-ADUser writes it, and refuses the catalog, which takes no
        # write - so a wrong -Server fails a test by what the directory does with it, not only by the value a test pins.
        # ObjectReadAnswersAs, or after a Set-Place ObjectReadAnswersAsAfterSetPlace, makes another object answer the
        # object read (-Identity without -Properties). aud-a2q, after a Set-Place: ObjectReadFailureAfterSetPlace fails
        # the object read and ObjectDnAfterSetPlace changes the DN a read answers; SetAdUserFailsAfterApplying throws once
        # the -Replace is applied.
        function global:Get-ADUser {
            [CmdletBinding()] param($Identity, $Filter, $Credential, $Properties, $Server)
            Write-StandInCall 'Get-ADUser' $PSBoundParameters
            $unusable = Get-StandInServerFailure $PSBoundParameters
            if ($unusable) { Write-Error $unusable; return }
            if ($PSBoundParameters.ContainsKey('Identity')) {
                $entry = $global:DirectoryObjects["$Identity"]
                if ($null -eq $entry -or -not (Test-StandInObjectReached $PSBoundParameters $entry)) { throw "Cannot find an object with identity: '$Identity'" }
                $afterSetPlace = Test-StandInAfterSetPlace
                if (-not $PSBoundParameters.ContainsKey('Properties') -and $global:ObjectReadFailureAfterSetPlace -and $afterSetPlace) { throw "$($global:ObjectReadFailureAfterSetPlace)" }
                $readBack = $PSBoundParameters.ContainsKey('Properties') -and @($global:Calls | Where-Object { $_['__Command'] -eq 'Set-ADUser' }).Count -gt 0
                if ($readBack -and $global:AttributeReadBackFailure) { Write-Error "$($global:AttributeReadBackFailure)"; return }
                $answer = New-Object psobject -Property $entry
                if ($readBack -and $global:AttributeReadBackAnswersAs) { $answer.ObjectGUID = [guid]"$($global:AttributeReadBackAnswersAs)" }
                $objectAs = if ($PSBoundParameters.ContainsKey('Properties')) { $null } elseif ($global:ObjectReadAnswersAsAfterSetPlace -and $afterSetPlace) { $global:ObjectReadAnswersAsAfterSetPlace } else { $global:ObjectReadAnswersAs }
                if ($objectAs) { $answer.ObjectGUID = [guid]"$objectAs" }
                if ($global:ObjectDnAfterSetPlace -and $afterSetPlace) { $answer | Add-Member -NotePropertyName DistinguishedName -NotePropertyValue "$($global:ObjectDnAfterSetPlace)" -Force }
                $answer
            }
        }
        function global:Set-ADUser {
            [CmdletBinding()] param($Identity, $Replace, $Server, $Credential)
            Write-StandInCall 'Set-ADUser' $PSBoundParameters
            $unusable = Get-StandInServerFailure $PSBoundParameters
            if ($unusable) { Write-Error $unusable; return }
            if ((Get-StandInReach $PSBoundParameters) -eq 'forest') { Write-Error 'The server is unwilling to process the request.'; return }
            $entry = $global:DirectoryObjects["$Identity"]
            if ($null -eq $entry -or -not (Test-StandInObjectReached $PSBoundParameters $entry)) { Write-Error "Cannot find an object with identity: '$Identity'"; return }
            if (-not $global:SetAdUserChangesNothing -and $null -ne $Replace) { foreach ($key in @($Replace.Keys)) { $entry["$key"] = $Replace[$key] } }
            if ($global:SetAdUserReportsErrorWithoutThrowing) { Write-Error "$($global:SetAdUserReportsErrorWithoutThrowing)" -ErrorAction Continue }
            if ($null -ne $global:SetAdUserFailsAfterApplying) { throw $global:SetAdUserFailsAfterApplying }
        }
        # aud-a2j. The forest: what it reports as its global catalogs (GlobalCatalogs);
        # Failures['Get-ADForest'] fails the read.
        function global:Get-ADForest {
            [CmdletBinding()] param($Credential, $Server)
            Write-StandInCall 'Get-ADForest' $PSBoundParameters
            [pscustomobject]@{ GlobalCatalogs = [string[]]@($global:GlobalCatalogs) }
        }
        # aud-a2j. What a directory call reaches, by its -Server, as with the real cmdlets: none, the default
        # domain; a leading ':' - a bare ':3268' - the default domain as well, because the real cmdlet
        # ACCEPTS that and quietly answers from the local domain (the ADDirectorySearchService incident);
        # '<catalog>:3268', every domain of the forest; a domain's DNS name, that domain; anything else,
        # nothing ($null - the caller's "Unable to contact the server").
        function global:Get-StandInDomainName([string] $DomainDn) {
            (@($DomainDn -split ',') | ForEach-Object { $_.Trim() -replace '^DC=', '' }) -join '.'
        }
        function global:Get-StandInReach($Bound) {
            if (-not $Bound.ContainsKey('Server')) { return 'default' }
            $server = "$($Bound['Server'])"
            if ($server.StartsWith(':')) { return 'default' }
            foreach ($gc in @($global:GlobalCatalogs)) {
                if (-not [string]::IsNullOrWhiteSpace("$gc") -and $server -eq "$($gc):3268") { return 'forest' }
            }
            foreach ($domain in @($global:DirectoryDomains)) {
                if ($server -eq (Get-StandInDomainName "$domain")) { return "$domain" }
            }
            return $null
        }
        function global:Test-StandInReaches($Reach, [string] $DomainDn) {
            if ($Reach -eq 'forest') { return $true }
            if ($Reach -eq 'default') { return $DomainDn -eq $global:DirectoryDomainDn }
            return $DomainDn -eq "$Reach"
        }
        # A group's domain (DirectoryGroupDomains, else the default one) and its DN there; and the groups a
        # reach sees.
        function global:Get-StandInGroupDomain([string] $Guid) {
            $domain = $global:DirectoryGroupDomains[$Guid]
            if ($domain) { return "$domain" }
            return $global:DirectoryDomainDn
        }
        function global:Get-StandInGroupDn([string] $Guid) { $global:DirectoryGroupDnFormat -f $Guid, (Get-StandInGroupDomain $Guid) }
        function global:Find-StandInGroups($Reach) {
            foreach ($g in @($global:DirectoryGroupGuids)) { if (Test-StandInReaches $Reach (Get-StandInGroupDomain "$g")) { "$g" } }
        }
        # aud-a2k. A search answers with the groups its -Filter matches, not every group it reaches: the filter
        # is read as the clauses the service builds - "<attribute> -eq '<value>'", single quotes doubled
        # inside, joined by -or - and a group matches when any clause equals its attribute (DirectoryGroupAttributes,
        # or the default mail and the CN as both names), case-insensitively as Active Directory compares these
        # attributes; an attribute the group lacks reads as empty, so "mail -eq ''" finds every group without
        # one - the worst a search by a blank address could do. A filter in any other shape is an error, so a
        # test cannot pass through a filter the stand-in did not evaluate.
        function global:Get-StandInGroupAttribute([string] $Guid, [string] $Name) {
            $attributes = $global:DirectoryGroupAttributes[$Guid]
            if ($null -eq $attributes) {
                $cn = "Group $Guid"
                $attributes = @{ mail = $global:DefaultListAddress; displayName = $cn; name = $cn }
            }
            foreach ($key in @($attributes.Keys)) { if ("$key" -ieq $Name) { return "$($attributes[$key])" } }
            return ''
        }
        function global:Read-StandInFilter([string] $Filter) {
            $clauses = New-Object System.Collections.ArrayList
            foreach ($clause in ($Filter -split '\s+-or\s+')) {
                if ($clause -notmatch "^\s*(\w+)\s+-eq\s+'((?:[^']|'')*)'\s*$") { return $null }
                [void]$clauses.Add(@($Matches[1], ($Matches[2] -replace "''", "'")))
            }
            , $clauses
        }
        function global:Test-StandInGroupMatches($Clauses, [string] $Guid) {
            foreach ($clause in $Clauses) {
                if ((Get-StandInGroupAttribute $Guid $clause[0]) -ieq $clause[1]) { return $true }
            }
            return $false
        }
        # aud-a2i, aud-a2j, aud-a2k. A search answers with every group the call reaches that its filter matches,
        # by objectGUID and DN. A read
        # by -Identity (a DN or an objectGUID) answers with that group when the call reaches it - as another
        # object when DirectoryGroupReadBack names the identity, or, for a read of the member attribute,
        # DirectoryGroupMemberReadBack - and with its member attribute when -Properties asks for it;
        # Failures['Get-ADGroup -Identity'] fails every read by identity. Its errors are Write-Error then
        # return, so they stay errors under any -ErrorAction. aud-a2s: Failures['Get-ADGroup -Properties member']
        # (MemberReadFailureKey) fails only a read of the member attribute - the membership read, which the key above
        # cannot reach, failing the group's re-read before it; and FailuresAfterSetPlace holds either key for the calls
        # made after a Set-Place.
        function global:Get-ADGroup {
            [CmdletBinding()] param($Filter, $Identity, $Properties, $Server, $Credential)
            Write-StandInCall 'Get-ADGroup' $PSBoundParameters
            $reach = Get-StandInReach $PSBoundParameters
            if ($null -eq $reach) { Write-Error "Unable to contact the server '$Server'."; return }
            $reached = @(Find-StandInGroups $reach)
            if (-not $PSBoundParameters.ContainsKey('Identity')) {
                $clauses = Read-StandInFilter "$Filter"
                if ($null -eq $clauses) { Write-Error "stand-in: cannot read the filter '$Filter'."; return }
                foreach ($group in $reached) {
                    if (Test-StandInGroupMatches $clauses "$group") {
                        [pscustomobject]@{ ObjectGUID = [guid]"$group"; DistinguishedName = (Get-StandInGroupDn "$group") }
                    }
                }
                return
            }
            $failure = $global:Failures['Get-ADGroup -Identity']
            if (-not $failure -and $global:FailuresAfterSetPlace['Get-ADGroup -Identity'] -and (Test-StandInAfterSetPlace)) { $failure = $global:FailuresAfterSetPlace['Get-ADGroup -Identity'] }
            if ($failure) { Write-Error $failure; return }
            $group = @($reached | Where-Object { "$Identity" -eq (Get-StandInGroupDn "$_") -or "$Identity" -eq "$_" })
            if ($group.Count -ne 1) { Write-Error "Cannot find an object with identity: '$Identity'"; return }
            $memberRead = @($Properties) -contains 'member'
            $memberFailure = $global:Failures['Get-ADGroup -Properties member']
            if (-not $memberFailure -and $global:FailuresAfterSetPlace['Get-ADGroup -Properties member'] -and (Test-StandInAfterSetPlace)) { $memberFailure = $global:FailuresAfterSetPlace['Get-ADGroup -Properties member'] }
            if ($memberRead -and $memberFailure) { Write-Error $memberFailure; return }
            # aud-a2n. MemberReadBackFailure: a member read after any Set-ADGroup fails - the read-back, not the checks before it.
            if ($memberRead -and $global:MemberReadBackFailure -and @($global:Calls | Where-Object { $_['__Command'] -eq 'Set-ADGroup' }).Count -gt 0) { Write-Error "$($global:MemberReadBackFailure)"; return }
            $answer = $global:DirectoryGroupReadBack["$Identity"]
            if ($memberRead -and $global:DirectoryGroupMemberReadBack.ContainsKey("$Identity")) { $answer = $global:DirectoryGroupMemberReadBack["$Identity"] }
            $row = @{ ObjectGUID = [guid]"$(if ($answer) { $answer } else { $group[0] })"; DistinguishedName = (Get-StandInGroupDn "$($group[0])") }
            if ($memberRead) { $row['member'] = [string[]]@($global:DirectoryGroupMembers) }
            New-Object psobject -Property $row
        }
        # aud-a2j. The on-prem room-list add's write. Its group must be one the call reaches - routed to the
        # group's own domain - and a write sent to the catalog is refused, as the read-only catalog refuses it.
        # aud-a2l. And it writes: the DN goes on the member attribute (DirectoryGroupMembers, the one member list
        # the stand-in keeps for its groups), so a read-back sees it - unless SetAdGroupChangesNothing, a write
        # that reports success and changes nothing. A DN already there is refused, as Active Directory refuses
        # a value already present (DuplicateMemberValueRefusal, aud-a2n: Write-Error -Exception then return, so
        # the exception sits in the error record); AnotherAddLandsFirst puts the DN there just before the call,
        # another actor's add, and before Failures['Set-ADGroup'] can throw. aud-a2n: SetAdGroupFailsAfterApplying
        # throws after the add was applied - the write that committed and then failed. aud-a2o:
        # SetAdGroupReportsErrorWithoutThrowing, after the add, writes an error that the caller's -ErrorAction Stop
        # does not throw - its own -ErrorAction Continue - so the error stream holds a record no throw carried.
        function global:Set-ADGroup {
            [CmdletBinding()] param($Identity, $Add, $Server, $Credential)
            $member = if ($null -ne $Add) { "$($Add['member'])" } else { '' }
            if ($global:AnotherAddLandsFirst -and $member) { [void]$global:DirectoryGroupMembers.Add($member) }
            Write-StandInCall 'Set-ADGroup' $PSBoundParameters
            $reach = Get-StandInReach $PSBoundParameters
            if ($null -eq $reach) { Write-Error "Unable to contact the server '$Server'."; return }
            if ($reach -eq 'forest') { Write-Error 'The server is unwilling to process the request.'; return }
            $group = @(Find-StandInGroups $reach | Where-Object { "$Identity" -eq "$_" -or "$Identity" -eq (Get-StandInGroupDn "$_") })
            if ($group.Count -ne 1) { Write-Error "Cannot find an object with identity: '$Identity'"; return }
            if (-not $member) { return }
            if (@($global:DirectoryGroupMembers | Where-Object { "$_" -ieq $member }).Count -gt 0) { Write-Error -Exception $global:DuplicateMemberValueRefusal; return }
            if (-not $global:SetAdGroupChangesNothing) { [void]$global:DirectoryGroupMembers.Add($member) }
            if ($global:SetAdGroupReportsErrorWithoutThrowing) { Write-Error "$($global:SetAdGroupReportsErrorWithoutThrowing)" -ErrorAction Continue }
            if ($null -ne $global:SetAdGroupFailsAfterApplying) { throw $global:SetAdGroupFailsAfterApplying }
        }
        # aud-a2i. The real cmdlet resolves every member server-side: Active Directory Web Services refuses a
        # group of more than MaxGroupOrMemberEntries members (5,000 by default), and the read faults on a
        # member in another domain it cannot chase. The stand-in refuses both - Write-Error then return, so
        # it stays refused under any -ErrorAction - and otherwise answers each member's objectGUID from
        # DirectoryObjects. No production code calls it (GroupMemberListingTests pins that).
        function global:Get-ADGroupMember {
            [CmdletBinding()] param($Identity, $Server, $Credential)
            Write-StandInCall 'Get-ADGroupMember' $PSBoundParameters
            $members = @($global:DirectoryGroupMembers)
            if ($members.Count -gt $global:AdwsMaxGroupOrMemberEntries) { Write-Error 'The size limit for this request was exceeded'; return }
            if (@($members | Where-Object { -not "$_".EndsWith(",$($global:DirectoryDomainDn)", [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
                Write-Error 'An operations error occurred'
                return
            }
            foreach ($dn in $members) {
                $object = @($global:DirectoryObjects.Values | Where-Object { "$($_['DistinguishedName'])" -eq "$dn" })
                [pscustomobject]@{ DistinguishedName = "$dn"; ObjectGUID = $(if ($object.Count -eq 1) { $object[0]['ObjectGUID'] }) }
            }
        }
        # aud-a2j. The real cmdlet resolves its group and every -Members value on the one domain the call
        # reaches - the group's own when routed there - so a member from another domain is not found ("Cannot
        # find an object with identity ... under: ...", Group Management's live dev failure of 2026-09-03),
        # and without -Server neither is a group outside the default domain. The stand-in refuses both. No
        # production code calls it now; it is here so a restored call fails for the reason it fails live.
        function global:Add-ADGroupMember {
            [CmdletBinding()] param($Identity, $Members, $Server, $Credential)
            Write-StandInCall 'Add-ADGroupMember' $PSBoundParameters
            $reach = Get-StandInReach $PSBoundParameters
            if ($null -eq $reach) { Write-Error "Unable to contact the server '$Server'."; return }
            if ($reach -eq 'forest') { Write-Error 'The server is unwilling to process the request.'; return }
            $domain = if ($reach -eq 'default') { $global:DirectoryDomainDn } else { "$reach" }
            $group = @(Find-StandInGroups $reach | Where-Object { "$Identity" -eq "$_" -or "$Identity" -eq (Get-StandInGroupDn "$_") })
            if ($group.Count -ne 1) { Write-Error "Cannot find an object with identity: '$Identity' under: '$domain'."; return }
            foreach ($member in @($Members)) {
                $object = $global:DirectoryObjects["$member"]
                if ($null -eq $object) { $object = @($global:DirectoryObjects.Values | Where-Object { "$($_['DistinguishedName'])" -eq "$member" }) | Select-Object -First 1 }
                if ($null -eq $object -or -not "$($object['DistinguishedName'])".EndsWith(",$domain", [StringComparison]::OrdinalIgnoreCase)) {
                    Write-Error "Cannot find an object with identity: '$member' under: '$domain'."
                    return
                }
            }
        }

        """;
}
