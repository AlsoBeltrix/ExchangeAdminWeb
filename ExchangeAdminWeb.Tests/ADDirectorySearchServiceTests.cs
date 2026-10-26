using System.Management.Automation.Runspaces;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// ADDirectorySearchService's pure helpers and its fail-soft contract, over a stand-in directory session (psl-8).
/// </summary>
/// <remarks>
/// Every service here comes from <see cref="CreateService"/>, whose availability probe opens a stand-in session: its
/// Import-Module is an advanced function that fails as the real cmdlet does for a module it cannot find, and module
/// autoloading is off. So the directory is unavailable on every host, the fail-soft tests run everywhere instead of
/// skipping wherever Active Directory is reachable, and nothing here loads the ActiveDirectory module or reaches a
/// directory. The probe used to be the production one, which imported the real module - and on a host with RSAT the
/// module's import connects to a domain controller for its AD: drive. That is also why the class is no longer in
/// LiveDirectoryCollection, whose rule is that a class talking to no live directory must not join it. The classes that
/// do exercise the real directory, where one is reachable, are ADDirectoryLiveTests and AdDirectoryForestSearchLiveTests;
/// ADDirectoryValidateExistsTests' one search takes this class's <see cref="CreateService"/> since psl-10, which is
/// internal for it.
/// </remarks>
public class ADDirectorySearchServiceTests
{
    // ---------------------------------------------------------------
    // LDAP filter escaping (delegates to ProtectedPrincipalService.EscapeLdapFilter)
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("john", "john")]
    [InlineData("O'Brien", "O'Brien")]              // Apostrophe is safe in LDAP
    [InlineData("test*user", "test\\2auser")]        // Wildcard escaped
    [InlineData("test(user)", "test\\28user\\29")]   // Parentheses escaped
    [InlineData("test\\user", "test\\5cuser")]       // Backslash escaped
    [InlineData("a\0b", "a\\00b")]                   // Null byte escaped
    [InlineData("*()\\\0", "\\2a\\28\\29\\5c\\00")]  // All special chars together
    public void EscapeLdapFilter_EscapesSpecialCharacters(string input, string expected)
    {
        var result = ProtectedPrincipalService.EscapeLdapFilter(input);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void EscapeLdapFilter_EmptyString_ReturnsEmpty()
    {
        Assert.Equal("", ProtectedPrincipalService.EscapeLdapFilter(""));
    }

    [Fact]
    public void EscapeLdapFilter_PlainText_Unchanged()
    {
        const string input = "John Smith 123";
        Assert.Equal(input, ProtectedPrincipalService.EscapeLdapFilter(input));
    }

    // ---------------------------------------------------------------
    // Minimum query length enforcement
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("")]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("  ")]
    [InlineData("  a")]
    [InlineData(" ab")]
    public void Search_TermTooShort_ReturnsEmpty(string term)
    {
        var svc = CreateService();
        var results = svc.Search(term, "Any");
        Assert.Empty(results);
    }

    [Fact]
    public void Search_NullTerm_ReturnsEmpty()
    {
        var svc = CreateService();
        var results = svc.Search(null!, "Any");
        Assert.Empty(results);
    }

    // ---------------------------------------------------------------
    // Max results enforcement
    // ---------------------------------------------------------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void Search_InvalidMaxResults_DoesNotThrow(int maxResults)
    {
        var svc = CreateService();
        // Should not throw; returns empty because the directory is unavailable (the stand-in probe)
        var results = svc.Search("test term", "Any", maxResults);
        Assert.NotNull(results);
    }

    // ---------------------------------------------------------------
    // Graceful fallback when AD unavailable
    // ---------------------------------------------------------------

    [Fact]
    public void IsAvailable_WhenAdUnavailable_ReturnsFalse()
    {
        // The probe's failing import is the directory's unavailability, reported once as a warning carrying the
        // import's own error - not the "could not be loaded" HadErrors branch psl-7 removed.
        var logger = new CapturingLogger();
        var svc = CreateService(logger);

        Assert.False(svc.IsAvailable);
        var warning = Assert.Single(logger.Warnings);
        Assert.Contains(ImportFailure, warning.Exception?.Message, StringComparison.Ordinal);
    }

    // These three assert the fail-soft contract: a search of a directory that is unavailable answers nothing. They used
    // to hold only where AD was ABSENT, because the probe imported the real module - so they skipped loudly wherever
    // RSAT was installed and the directory reachable (ppv-4's rule: a returned test reports PASSED), and ran only on CI.
    // The stand-in probe makes the directory unavailable on every host, so they run everywhere (psl-8).

    [Fact]
    public void Search_WhenAdUnavailable_ReturnsEmpty()
    {
        var svc = CreateService();

        Assert.Empty(svc.Search("test user search", "User"));
    }

    [Fact]
    public void SearchUsers_WhenAdUnavailable_ReturnsEmpty()
    {
        var svc = CreateService();

        Assert.Empty(svc.SearchUsers("test user"));
    }

    [Fact]
    public void SearchGroups_WhenAdUnavailable_ReturnsEmpty()
    {
        var svc = CreateService();

        Assert.Empty(svc.SearchGroups("test group"));
    }

    // ---------------------------------------------------------------
    // ObjectKind parameter validation
    // ---------------------------------------------------------------

    [Theory]
    [InlineData("User")]
    [InlineData("Group")]
    [InlineData("Any")]
    public void Search_AllObjectKinds_DoNotThrow(string objectKind)
    {
        var svc = CreateService();
        var results = svc.Search("test search term", objectKind);
        Assert.NotNull(results);
    }

    // ---------------------------------------------------------------
    // ADSearchResult record
    // ---------------------------------------------------------------

    [Fact]
    public void ADSearchResult_StoresAllFields()
    {
        var result = new ADSearchResult(
            DisplayName: "John Smith",
            DistinguishedName: "CN=John Smith,OU=Users,DC=contoso,DC=com",
            SamAccountName: "jsmith",
            UserPrincipalName: "jsmith@contoso.com",
            Email: "john.smith@contoso.com",
            ObjectType: "User");

        Assert.Equal("John Smith", result.DisplayName);
        Assert.Equal("CN=John Smith,OU=Users,DC=contoso,DC=com", result.DistinguishedName);
        Assert.Equal("jsmith", result.SamAccountName);
        Assert.Equal("jsmith@contoso.com", result.UserPrincipalName);
        Assert.Equal("john.smith@contoso.com", result.Email);
        Assert.Equal("User", result.ObjectType);
    }

    [Fact]
    public void ADSearchResult_GroupWithNullFields()
    {
        var result = new ADSearchResult(
            DisplayName: "IT Admins",
            DistinguishedName: "CN=IT Admins,OU=Groups,DC=contoso,DC=com",
            SamAccountName: "ITAdmins",
            UserPrincipalName: null,
            Email: null,
            ObjectType: "Group");

        Assert.Equal("IT Admins", result.DisplayName);
        Assert.Null(result.UserPrincipalName);
        Assert.Null(result.Email);
        Assert.Equal("Group", result.ObjectType);
    }

    [Fact]
    public void ADSearchResult_Equality()
    {
        var a = new ADSearchResult("A", "DN=A", "a", "a@x.com", "a@x.com", "User");
        var b = new ADSearchResult("A", "DN=A", "a", "a@x.com", "a@x.com", "User");
        Assert.Equal(a, b);
    }

    // ---------------------------------------------------------------
    // Helpers
    // ---------------------------------------------------------------

    // The real cmdlet's words for a module it cannot find (aud-a2r measured the shape: a non-terminating error that only
    // the caller's -ErrorAction Stop throws).
    private const string ImportFailure =
        "The specified module ActiveDirectory was not loaded because no valid module file was found in any module directory.";

    /// <summary>
    /// The service, its availability probe given a stand-in session (psl-8). Checked before it is handed over: the
    /// directory is unavailable, and the stand-in took the probe's one import - so no test reaches a search (which
    /// returns before opening any session while the directory is unavailable), and none could be reaching the real
    /// module through some other session instead.
    /// </summary>
    internal static ADDirectorySearchService CreateService(ILogger<ADDirectorySearchService>? logger = null)
    {
        var calls = new List<string>();
        var svc = new ADDirectorySearchService(logger ?? NullLogger<ADDirectorySearchService>.Instance)
        {
            CreateProbeRunspace = () => StandInProbeSession(calls),
        };

        Assert.False(svc.IsAvailable);
        Assert.Equal(["Import-Module|ActiveDirectory"], calls);
        return svc;
    }

    /// <summary>
    /// A local session whose Import-Module is an ADVANCED function - so the probe's -ErrorAction Stop acts on it as on the
    /// cmdlet - writing the cmdlet's error for a module it cannot find, and noting the call. Module autoloading is off, so
    /// nothing in the session can load a module.
    /// </summary>
    private static Runspace StandInProbeSession(List<string> calls)
    {
        var state = InitialSessionState.CreateDefault();
        state.Variables.Add(new SessionStateVariableEntry("PSModuleAutoloadingPreference", "None", "no autoloading"));
        state.Variables.Add(new SessionStateVariableEntry("DirectoryCalls", calls, "the calls the session took"));
        state.Commands.Add(new SessionStateFunctionEntry("Import-Module",
            "[CmdletBinding()] param([Parameter(Position = 0)] $Name)\n"
            + "$global:DirectoryCalls.Add(\"Import-Module|$Name\")\n"
            + $"Write-Error -Message '{ImportFailure}'"));
        return RunspaceFactory.CreateRunspace(state);
    }

    // ---- psl-11: an AVAILABLE stand-in, so a search reaches its commands ------------------------------------------

    internal const string StandInGlobalCatalog = "gc01.example.test";

    /// <summary>kd-55. The objectGUID the stand-in group answers with - what a validated group's result must carry.</summary>
    internal const string StandInGroupGuid = "6f1c2c7e-3b9a-4d55-9a51-1c2f0e4b7a10";

    /// <summary>
    /// The service with both its sessions standing in (psl-11): the probe's import loads, so the directory reads as
    /// available - checked here, with the probe's one import - and the search's session answers <c>Get-ADUser</c>,
    /// <c>Get-ADGroup</c>, <c>Get-ADOrganizationalUnit</c> and <c>Get-ADForest</c>, noting every call in
    /// <paramref name="calls"/> as "command" or, for a group, "command|server". Every command is an advanced function and
    /// autoloading is off in both sessions, so neither can load a module or reach a directory.
    /// </summary>
    /// <param name="answers">
    /// kd-55. How many objects each directory command returns: "one" (the default, what the search tests expect), "none" -
    /// the absence an existence check must report as NotFound rather than Unavailable - or "two", which makes a validation
    /// ambiguous. <c>Get-ADForest</c> always answers its catalog.
    /// </param>
    internal static ADDirectorySearchService AvailableStandInService(List<string> calls, string answers = "one")
    {
        var times = answers switch { "none" => 0, "two" => 2, _ => 1 };
        var probeCalls = new List<string>();
        var svc = new ADDirectorySearchService(NullLogger<ADDirectorySearchService>.Instance)
        {
            CreateProbeRunspace = () => StandInSession(probeCalls, ("Import-Module", """
                [CmdletBinding()] param([Parameter(Position = 0)] $Name)
                $global:DirectoryCalls.Add("Import-Module|$Name")
                """)),
            CreateSearchRunspace = () => StandInSession(calls,
                ("Get-ADUser", $$"""
                    [CmdletBinding()] param($LDAPFilter, $Properties, $ResultSetSize, $Server)
                    $global:DirectoryCalls.Add('Get-ADUser')
                    for ($i = 0; $i -lt {{times}}; $i++) { [pscustomobject]@{ DisplayName = 'Alex Doe'; DistinguishedName = 'CN=Alex Doe,OU=Staff,DC=example,DC=test'; SamAccountName = 'adoe'; UserPrincipalName = 'alex.doe@example.test'; mail = 'alex.doe@example.test' } }
                    """),
                ("Get-ADGroup", $$"""
                    [CmdletBinding()] param($LDAPFilter, $Properties, $ResultSetSize, $Server)
                    $global:DirectoryCalls.Add("Get-ADGroup|$Server")
                    for ($i = 0; $i -lt {{times}}; $i++) { [pscustomobject]@{ DisplayName = 'Finance Approvers'; Name = 'Finance Approvers'; DistinguishedName = 'CN=Finance Approvers,OU=Groups,DC=example,DC=test'; SamAccountName = 'FinanceApprovers'; mail = $null; objectSid = 'S-1-5-21-1000000000-1000000000-1000000000-2001'; ObjectGUID = [guid]'{{StandInGroupGuid}}' } }
                    """),
                ("Get-ADOrganizationalUnit", $$"""
                    [CmdletBinding()] param($LDAPFilter, $Properties, $ResultSetSize, $Server)
                    $global:DirectoryCalls.Add('Get-ADOrganizationalUnit')
                    for ($i = 0; $i -lt {{times}}; $i++) { [pscustomobject]@{ Name = 'Test Rooms'; DistinguishedName = 'OU=Test Rooms,DC=example,DC=test' } }
                    """),
                ("Get-ADForest", $$"""
                    [CmdletBinding()] param()
                    $global:DirectoryCalls.Add('Get-ADForest')
                    [pscustomobject]@{ GlobalCatalogs = @('{{StandInGlobalCatalog}}') }
                    """)),
        };

        Assert.True(svc.IsAvailable);
        Assert.Equal(["Import-Module|ActiveDirectory"], probeCalls);
        return svc;
    }

    private static Runspace StandInSession(List<string> calls, params (string Name, string Body)[] commands)
    {
        var state = InitialSessionState.CreateDefault();
        state.Variables.Add(new SessionStateVariableEntry("PSModuleAutoloadingPreference", "None", "no autoloading"));
        state.Variables.Add(new SessionStateVariableEntry("DirectoryCalls", calls, "the calls the session took"));
        foreach (var (name, body) in commands)
            state.Commands.Add(new SessionStateFunctionEntry(name, body));
        return RunspaceFactory.CreateRunspace(state);
    }

    private sealed record Logged(LogLevel Level, string Message, Exception? Exception);

    private sealed class CapturingLogger : ILogger<ADDirectorySearchService>
    {
        private readonly List<Logged> _entries = [];

        public IReadOnlyList<Logged> Warnings
        {
            get { lock (_entries) return _entries.Where(e => e.Level == LogLevel.Warning).ToList(); }
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (_entries)
                _entries.Add(new Logged(logLevel, formatter(state, exception), exception));
        }
    }
}
