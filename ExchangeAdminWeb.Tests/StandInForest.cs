using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Text;
using System.Text.RegularExpressions;
using ExchangeAdminWeb.Services;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExchangeAdminWeb.Tests;

/// <summary>
/// A stand-in forest for the forest-wide single-user lookups,
/// <see cref="ADDirectorySearchService.FindUserByEmployeeId"/> and
/// <see cref="ADDirectorySearchService.FindUserByLogonName"/>, behind the service's psl-11 search-session seam.
/// </summary>
/// <remarks>
/// <see cref="Service"/> hands back the real service with both of its sessions standing in, the shape
/// ADDirectorySearchServiceTests.AvailableStandInService uses: the probe's Import-Module is a local advanced function that
/// loads nothing, so the directory reads as available, and the search session's <c>Get-ADForest</c> and <c>Get-ADUser</c>
/// are local advanced functions answered by this object. Module autoloading is off in both sessions, so nothing here loads
/// the ActiveDirectory module or reaches a directory, and no class using it belongs in LiveDirectoryCollection.
///
/// <c>Get-ADUser</c> answers the one filter shape those lookups send, an LDAP equality <c>(attribute=value)</c>, the way a
/// domain controller would: case-insensitively, an escaped character (<c>\2a</c> and the rest) as that literal character,
/// an UNescaped <c>*</c> as a wildcard, and an unescaped parenthesis as a malformed filter that throws. So a filter built
/// without escaping misbehaves here as it would against a directory, where a test can see it. At most
/// <c>-ResultSetSize</c> users come back per query, as from the real cmdlet.
///
/// A domain, the forest read, or any single query can fail either way the real cmdlets fail: <see cref="Failure.Reports"/>
/// writes an error record without throwing (Write-Error with its own -ErrorAction Continue, which the caller's Stop does
/// not turn into a throw - the shape DirectoryModuleImportTests measured), and <see cref="Failure.Throws"/> throws.
///
/// Public, with public members, because the stand-in session calls into it.
/// </remarks>
public sealed class StandInForest
{
    public enum Failure
    {
        None,
        Reports,
        Throws,
    }

    /// <summary>A directory user in one domain of the stand-in forest. A null GUID is a user that answers without one.</summary>
    public sealed record User(
        string Domain, string SamAccountName, string? EmployeeId, string? Mail, string? ObjectGuid, string DisplayName);

    /// <summary>One Get-ADUser call, as the lookup made it.</summary>
    public sealed record UserQuery(string Server, string LdapFilter, int ResultSetSize, IReadOnlyList<string> Properties);

    /// <summary>What a stand-in command does: fail one of the two ways, or emit <see cref="Output"/>.</summary>
    public sealed class Answer
    {
        public string Mode { get; init; } = nameof(Failure.None);

        public string Message { get; init; } = "";

        public object?[] Output { get; init; } = [];
    }

    /// <summary>The catalog Get-ADForest reports. No lookup under test may query it.</summary>
    public const string GlobalCatalog = "gc01.example.test";

    private readonly List<User> _users = [];
    private readonly Dictionary<string, Failure> _domainFailures = new(StringComparer.OrdinalIgnoreCase);

    public StandInForest(params string[] domains) => Domains.AddRange(domains);

    /// <summary>The domains Get-ADForest reports, in order.</summary>
    public List<string> Domains { get; } = [];

    /// <summary>How the forest read itself fails, if it does.</summary>
    public Failure ForestFailure { get; set; }

    /// <summary>A failure decided per query; <see cref="Failure.None"/> lets the domain answer.</summary>
    public Func<UserQuery, Failure>? QueryFailure { get; set; }

    /// <summary>Every directory command the search session ran, in order: "Get-ADForest" or "Get-ADUser".</summary>
    public List<string> Commands { get; } = [];

    /// <summary>Every Get-ADUser call, in order.</summary>
    public List<UserQuery> UserQueries { get; } = [];

    /// <summary>The calls the availability probe took.</summary>
    public List<string> ProbeCalls { get; } = [];

    /// <summary>A user with a fresh ObjectGUID.</summary>
    public User Add(string domain, string samAccountName, string? employeeId, string? mail, string? displayName = null) =>
        Add(new User(domain, samAccountName, employeeId, mail, Guid.NewGuid().ToString(), displayName ?? samAccountName));

    public User Add(User user)
    {
        _users.Add(user);
        return user;
    }

    /// <summary>Make every query against <paramref name="domain"/> fail.</summary>
    public void Fail(string domain, Failure failure) => _domainFailures[domain] = failure;

    /// <summary>The real service over this forest, checked available with the probe's one import.</summary>
    public ADDirectorySearchService Service(ILogger<ADDirectorySearchService>? logger = null)
    {
        var service = new ADDirectorySearchService(logger ?? NullLogger<ADDirectorySearchService>.Instance)
        {
            CreateProbeRunspace = () => Session(("Import-Module", ProbeImport)),
            CreateSearchRunspace = () => Session(("Get-ADForest", GetAdForest), ("Get-ADUser", GetAdUser)),
        };

        Assert.True(service.IsAvailable);
        Assert.Equal(["Import-Module|ActiveDirectory"], ProbeCalls);
        return service;
    }

    /// <summary>Called by the stand-in Get-ADForest.</summary>
    public Answer AnswerForest()
    {
        Commands.Add("Get-ADForest");

        return ForestFailure == Failure.None
            ? new Answer { Output = Domains.Cast<object?>().ToArray() }
            : new Answer { Mode = ForestFailure.ToString(), Message = "The stand-in forest could not be read." };
    }

    /// <summary>Called by the stand-in Get-ADUser.</summary>
    public Answer AnswerUserQuery(string server, string ldapFilter, int resultSetSize, string[]? properties)
    {
        Commands.Add("Get-ADUser");
        var query = new UserQuery(server, ldapFilter, resultSetSize, properties ?? []);
        UserQueries.Add(query);

        var failure = QueryFailure?.Invoke(query) ?? Failure.None;
        if (failure == Failure.None && _domainFailures.TryGetValue(server, out var domainFailure))
            failure = domainFailure;

        if (failure != Failure.None)
            return new Answer { Mode = failure.ToString(), Message = $"The stand-in query against {server} failed." };

        IEnumerable<User> hits = Matching(server, ldapFilter);
        if (resultSetSize > 0)
            hits = hits.Take(resultSetSize);

        return new Answer { Output = hits.Select(ToPSObject).Cast<object?>().ToArray() };
    }

    private const string ProbeImport = """
        [CmdletBinding()] param([Parameter(Position = 0)] $Name)
        $global:Forest.ProbeCalls.Add("Import-Module|$Name")
        """;

    private const string GetAdForest = $$"""
        [CmdletBinding()] param()
        $answer = $global:Forest.AnswerForest()
        if ($answer.Mode -eq 'Throws') { throw $answer.Message }
        if ($answer.Mode -eq 'Reports') { Write-Error -Message $answer.Message -ErrorAction Continue; return }
        [pscustomobject]@{ Domains = $answer.Output; GlobalCatalogs = @('{{GlobalCatalog}}') }
        """;

    private const string GetAdUser = """
        [CmdletBinding()] param($LDAPFilter, $Properties, $ResultSetSize, $Server)
        $answer = $global:Forest.AnswerUserQuery([string]$Server, [string]$LDAPFilter, [int]$ResultSetSize, [string[]]$Properties)
        if ($answer.Mode -eq 'Throws') { throw $answer.Message }
        if ($answer.Mode -eq 'Reports') { Write-Error -Message $answer.Message -ErrorAction Continue; return }
        $answer.Output
        """;

    private Runspace Session(params (string Name, string Body)[] commands)
    {
        var state = InitialSessionState.CreateDefault();
        state.Variables.Add(new SessionStateVariableEntry("PSModuleAutoloadingPreference", "None", "no autoloading"));
        state.Variables.Add(new SessionStateVariableEntry("Forest", this, "the stand-in forest that answers the directory commands"));
        foreach (var (name, body) in commands)
            state.Commands.Add(new SessionStateFunctionEntry(name, body));
        return RunspaceFactory.CreateRunspace(state);
    }

    private static readonly Regex EqualityFilter = new(
        @"^\((?<attribute>[A-Za-z]+)=(?<value>.*)\)$", RegexOptions.Singleline | RegexOptions.CultureInvariant);

    private List<User> Matching(string server, string ldapFilter)
    {
        var filter = EqualityFilter.Match(ldapFilter);
        if (!filter.Success)
            throw new InvalidOperationException($"The stand-in answers an equality filter only, not '{ldapFilter}'.");

        Func<User, string?> attribute = filter.Groups["attribute"].Value.ToUpperInvariant() switch
        {
            "SAMACCOUNTNAME" => user => user.SamAccountName,
            "EMPLOYEEID" => user => user.EmployeeId,
            var other => throw new InvalidOperationException($"The stand-in holds no attribute '{other}'."),
        };

        var value = AssertionPattern(filter.Groups["value"].Value);

        return _users
            .Where(user => string.Equals(user.Domain, server, StringComparison.OrdinalIgnoreCase)
                           && attribute(user) is { } held
                           && value.IsMatch(held))
            .ToList();
    }

    /// <summary>
    /// The assertion value as a pattern: <c>\XX</c> is the character with that hex code, an unescaped <c>*</c> matches any
    /// run, and an unescaped parenthesis or a broken escape is the malformed filter a directory refuses.
    /// </summary>
    private static Regex AssertionPattern(string value)
    {
        var pattern = new StringBuilder("^");

        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];

            if (c == '\\')
            {
                if (i + 2 >= value.Length
                    || !int.TryParse(value.AsSpan(i + 1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code))
                {
                    throw new InvalidOperationException($"The search filter cannot be recognized: a broken escape in '{value}'.");
                }

                pattern.Append(Regex.Escape(((char)code).ToString()));
                i += 2;
            }
            else if (c == '*')
            {
                pattern.Append(".*");
            }
            else if (c is '(' or ')')
            {
                throw new InvalidOperationException($"The search filter cannot be recognized: '{value}'.");
            }
            else
            {
                pattern.Append(Regex.Escape(c.ToString()));
            }
        }

        pattern.Append('$');
        return new Regex(pattern.ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant);
    }

    private static PSObject ToPSObject(User user)
    {
        var dn = $"CN={user.DisplayName},OU=Staff,DC={user.Domain.Replace(".", ",DC=", StringComparison.Ordinal)}";

        var result = new PSObject();
        result.Properties.Add(new PSNoteProperty("DisplayName", user.DisplayName));
        result.Properties.Add(new PSNoteProperty("Name", user.DisplayName));
        result.Properties.Add(new PSNoteProperty("DistinguishedName", dn));
        result.Properties.Add(new PSNoteProperty("SamAccountName", user.SamAccountName));
        result.Properties.Add(new PSNoteProperty("UserPrincipalName", $"{user.SamAccountName}@{user.Domain}"));
        result.Properties.Add(new PSNoteProperty("mail", user.Mail));
        result.Properties.Add(new PSNoteProperty("employeeID", user.EmployeeId));
        result.Properties.Add(new PSNoteProperty("Enabled", true));
        result.Properties.Add(new PSNoteProperty("ObjectGUID", user.ObjectGuid is null ? null : Guid.Parse(user.ObjectGuid)));
        return result;
    }
}
