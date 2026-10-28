using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Forest-wide lookups of the one directory user carrying a given <c>employeeID</c>, or a given
/// logon name.
/// </summary>
/// <remarks>
/// Added for Cloud Password Reset (docs/CloudPasswordReset-Plan.md, "Where the password goes"),
/// which derives a reset's destination mailbox from the employee ID stamped on the target cloud
/// account. It is a partial of <see cref="ADDirectorySearchService"/> rather than a new service
/// so it shares that class's throttled runspace, availability probe and outcome vocabulary -
/// three things a second AD service would have had to duplicate and could have got subtly
/// different.
///
/// **Each lookup reads ONE employee ID or ONE logon name per call and never enumerates.** The
/// module calls them for the one account an operator is resetting (`.agents/decisions.md`
/// 2026-09-22, "No tooling enumerates the directory"). The per-domain loop below is coverage for
/// a single subject across every domain of the forest, not a sweep of a population.
///
/// **The two lookups share one wrapper and one per-domain loop**
/// (<see cref="FindOneUserAcrossForest"/>, <see cref="SearchEveryDomainForOneUser"/>), so their
/// failure semantics cannot drift. The logon-name lookup was added for the employee-ID fill-in
/// (docs/CloudPasswordResetEmployeeId-Plan.md 4.2), and a second copy of the loop is how one of
/// them would come to read a failed domain as an empty one.
/// </remarks>
public sealed partial class ADDirectorySearchService
{
    /// <summary>
    /// Find the single enabled directory user whose <c>employeeID</c> equals
    /// <paramref name="employeeId"/>, searching every domain in the host's forest.
    /// </summary>
    /// <returns>
    /// <see cref="DirectoryLookupOutcome.Found"/> with the user and
    /// <c>Ambiguous = false</c> when exactly one user carries the id;
    /// <see cref="DirectoryLookupOutcome.Found"/> with <c>Ambiguous = true</c> when more than one
    /// does, in which case the caller must refuse rather than pick;
    /// <see cref="DirectoryLookupOutcome.NotFound"/> when the directory was searched and nobody
    /// carries it; and <see cref="DirectoryLookupOutcome.Unavailable"/> when the search did not
    /// complete.
    /// </returns>
    /// <remarks>
    /// **NotFound and Unavailable are different answers and the caller must treat them
    /// differently.** "Nobody has this id" is a fact about the directory; "the search failed" is
    /// a fact about the network. Collapsing them is how a broken query reports as a clean
    /// negative - which this repo did on 2026-09-22, publishing a confident 0% coverage figure
    /// that was entirely an artefact of querying an attribute the global catalog does not
    /// replicate.
    ///
    /// **Which is also why this searches each domain directly rather than the global catalog.**
    /// `employeeID` is not in the GC's default partial attribute set, verified on this forest:
    /// `mail` carries <c>isMemberOfPartialAttributeSet</c> true and `employeeID` carries nothing.
    /// A GC filter on it matches nothing anywhere, silently. Querying the domains themselves
    /// reads the real attribute, and returns `mail` and `Enabled` authoritatively in the same
    /// call.
    ///
    /// **A domain that cannot be reached makes the whole lookup Unavailable**, not a partial
    /// answer. A match in the unreachable domain is exactly what would make the reachable
    /// domain's single hit ambiguous, so a partial search cannot distinguish "one match" from
    /// "one match that I can see".
    ///
    /// Runs under the app pool's ambient identity, like the rest of this service: a read-only
    /// lookup does not need the protected-principal directory-read secret
    /// (`.agents/decisions.md` 2026-07-31).
    /// </remarks>
    public DirectoryValidationResult FindUserByEmployeeId(string employeeId) =>
        FindOneUserAcrossForest(employeeId, BuildEmployeeIdFilter, "employeeID");

    /// <summary>
    /// Find the single directory user whose <c>sAMAccountName</c> (the pre-Windows 2000 logon
    /// name) equals <paramref name="logonName"/>, searching every domain in the host's forest.
    /// </summary>
    /// <returns>
    /// The same four answers as <see cref="FindUserByEmployeeId"/>: Found with
    /// <c>Ambiguous = false</c> for exactly one user, Found with <c>Ambiguous = true</c> for more
    /// than one, NotFound when every domain was searched and nobody answers to the name, and
    /// Unavailable when the search did not complete. The match carries the user's
    /// <c>employeeID</c> (<see cref="ADSearchResult.EmployeeId"/>), mail and ObjectGUID.
    /// </returns>
    /// <remarks>
    /// Added for Cloud Password Reset's fill-in of a missing employee ID
    /// (docs/CloudPasswordResetEmployeeId-Plan.md 4.2), which reads the probable owner of an
    /// unstamped cloud account from the account's name and then verifies that owner by number
    /// through <see cref="FindUserByEmployeeId"/>.
    ///
    /// **A logon name is unique within a domain, not within a forest**: two domains can each hold
    /// the same one. So this searches every domain, exactly as the employee-ID lookup does, and two
    /// hits anywhere read as Ambiguous. The caller must refuse rather than pick, because picking
    /// here is choosing whose mailbox receives a password.
    ///
    /// **Each domain directly, never the global catalog**, for the employee-ID lookup's reasons and
    /// because the match must carry the owner's real <c>employeeID</c>, which the catalog does not
    /// replicate by default. A domain that cannot be searched makes the whole lookup Unavailable,
    /// never NotFound.
    ///
    /// Read-only, under the app pool's ambient identity, like <see cref="FindUserByEmployeeId"/>.
    /// </remarks>
    public DirectoryValidationResult FindUserByLogonName(string logonName) =>
        FindOneUserAcrossForest(logonName, BuildLogonNameFilter, "logon-name");

    /// <summary>The LDAP filter for an exact employeeID match, escaped.</summary>
    /// <remarks>
    /// Escaping is not decoration here. An employee ID comes from a directory attribute, not from
    /// a fixed list, and an unescaped <c>*</c> turns this equality test into a wildcard that
    /// matches every stamped user in the domain. That would read as Ambiguous at best and, for a
    /// domain with one stamped user, as a confident wrong match.
    /// </remarks>
    internal static string BuildEmployeeIdFilter(string employeeId) =>
        $"(employeeID={ProtectedPrincipalService.EscapeLdapFilter(employeeId)})";

    /// <summary>The LDAP filter for an exact logon-name (sAMAccountName) match, escaped.</summary>
    /// <remarks>
    /// An equality test and nothing else. The name comes from a cloud account's name, which is not
    /// a fixed list either, and an unescaped <c>*</c>, <c>(</c>, <c>)</c>, <c>\</c> or NUL would
    /// turn the test into a wildcard or into a different filter: a lookup that answers for people
    /// the name does not name.
    /// </remarks>
    internal static string BuildLogonNameFilter(string logonName) =>
        $"(sAMAccountName={ProtectedPrincipalService.EscapeLdapFilter(logonName)})";

    /// <summary>
    /// The one wrapper both forest-wide single-user lookups run in: blank input, availability, the
    /// shared throttle, and the rule that a lookup which did not complete is Unavailable.
    /// </summary>
    /// <param name="value">The employee ID or logon name, trimmed here.</param>
    /// <param name="buildFilter">The escaped equality filter for <paramref name="value"/>.</param>
    /// <param name="lookupKind">Names the lookup in the log, so the two stay distinguishable there.</param>
    private DirectoryValidationResult FindOneUserAcrossForest(string value, Func<string, string> buildFilter, string lookupKind)
    {
        if (string.IsNullOrWhiteSpace(value))
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.BlankInput), null);

        if (!IsAvailable)
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.DirectoryUnavailable), null);

        try
        {
            if (!_runspaceLock.Wait(TimeSpan.FromSeconds(30)))
            {
                _logger.LogWarning("AD {LookupKind} lookup throttle timeout", lookupKind);
                return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.ThrottleTimeout), null);
            }

            try
            {
                return SearchEveryDomainForOneUser(buildFilter(value.Trim()), lookupKind);
            }
            catch
            {
                _searchRunspace?.Dispose();
                _searchRunspace = null;
                throw;
            }
            finally
            {
                _runspaceLock.Release();
            }
        }
        catch (Exception ex)
        {
            // Unavailable, never NotFound: the lookup did not complete, which is not evidence
            // that nobody carries the value.
            _logger.LogWarning(ex, "AD {LookupKind} lookup failed", lookupKind);
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.LookupThrew), null);
        }
    }

    /// <summary>
    /// Run <paramref name="ldapFilter"/> against every domain of the host's forest, each domain
    /// directly, and say whether exactly one user answered.
    /// </summary>
    /// <remarks>
    /// Called with <see cref="_runspaceLock"/> held. Any one domain's error ends the search as
    /// Unavailable - a match in the domain that failed is exactly what would make another domain's
    /// single hit ambiguous - and so does a forest whose domains cannot be listed. At most two
    /// users per domain are asked for: two is all it takes to know the answer is ambiguous.
    /// </remarks>
    private DirectoryValidationResult SearchEveryDomainForOneUser(string ldapFilter, string lookupKind)
    {
        var domains = GetForestDomains();
        if (domains.Count == 0)
        {
            _logger.LogWarning("AD {LookupKind} lookup could not enumerate forest domains", lookupKind);
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.LookupThrew), null);
        }

        var runspace = GetOrCreateRunspace();
        var matches = new List<PSObject>();

        foreach (var domain in domains)
        {
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            ps.AddCommand("Get-ADUser")
              .AddParameter("Server", domain)
              .AddParameter("LDAPFilter", ldapFilter)
              .AddParameter("Properties", new[] { "DisplayName", "mail", "Enabled", "employeeID", "ObjectGUID" })
              .AddParameter("ResultSetSize", 2)
              .AddParameter("ErrorAction", "Stop");

            var found = ps.Invoke();

            if (ps.HadErrors)
            {
                var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Get-ADUser failed";
                ps.Streams.Error.Clear();
                _logger.LogWarning("AD {LookupKind} lookup errored against {Domain}: {Error}", lookupKind, domain, errMsg);
                return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.CmdletReportedErrors), null);
            }

            var hits = found.Where(o => o is not null).ToList();
            _logger.LogDebug("AD {LookupKind} lookup matched {Count} user(s) in {Domain}", lookupKind, hits.Count, domain);
            matches.AddRange(hits);
        }

        if (matches.Count == 0)
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.CompletedWithNoResults), null);

        var obj = matches[0];

        return new DirectoryValidationResult(
            ClassifyOutcome(ValidationStep.CompletedWithResults),
            new ADSearchResult(
                DisplayName: obj.Properties["DisplayName"]?.Value?.ToString()
                             ?? obj.Properties["Name"]?.Value?.ToString() ?? "",
                DistinguishedName: obj.Properties["DistinguishedName"]?.Value?.ToString() ?? "",
                SamAccountName: obj.Properties["SamAccountName"]?.Value?.ToString(),
                UserPrincipalName: obj.Properties["UserPrincipalName"]?.Value?.ToString(),
                Email: obj.Properties["mail"]?.Value?.ToString(),
                ObjectType: "User",
                ObjectGuid: obj.Properties["ObjectGUID"]?.Value?.ToString(),
                EmployeeId: obj.Properties["employeeID"]?.Value?.ToString()),
            Ambiguous: matches.Count > 1);
    }

    /// <summary>Every domain in the host's own forest, discovered at runtime.</summary>
    /// <remarks>
    /// Discovered, never named or configured: environment neutrality is a repo invariant
    /// (`.agents/repo-guidance.md`, owner ruling 2026-09-11). The app works in whatever forest
    /// its host belongs to and knows nothing about any particular one.
    ///
    /// Returns empty rather than falling back to the local domain when the forest cannot be
    /// read. A local-domain-only answer cannot see a second domain's match, so it would report
    /// a single hit as unambiguous when it may not be - the failure mode that ends with a
    /// password mailed to the wrong person.
    /// </remarks>
    private List<string> GetForestDomains()
    {
        try
        {
            var runspace = GetOrCreateRunspace();
            using var ps = PowerShell.Create();
            ps.Runspace = runspace;

            // AddScript, not AddCommand + Properties["Domains"]. Two independent reasons, and
            // either one alone returns an empty list, which refuses every reset this module is
            // asked to do:
            // 1. `(Get-ADForest).Domains` is an ADPropertyValueCollection, which derives from
            //    CollectionBase and implements only the NON-generic IEnumerable - verified by
            //    reflection against Microsoft.ActiveDirectory.Management 10.0.0.0. An
            //    `is IEnumerable<object>` test against it is false no matter what it holds.
            // 2. Reading Properties[...] off the PSObject this runspace returns yields an empty
            //    value regardless - measured for GlobalCatalogs in ResolveGlobalCatalog, same
            //    class, and the reason that method already projects inside PowerShell.
            // Projecting in the pipeline sidesteps both: PowerShell unrolls the collection and
            // emits one domain name per object.
            ps.AddScript("(Get-ADForest -ErrorAction Stop).Domains");
            var result = ps.Invoke();

            if (ps.HadErrors)
            {
                // Log the reason. Clearing the stream and returning empty made this failure
                // indistinguishable from every other Unavailable cause, on the one code path
                // whose whole job is to tell those causes apart.
                var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Get-ADForest failed";
                ps.Streams.Error.Clear();
                _logger.LogWarning("Could not enumerate forest domains for a forest-wide user lookup: {Error}", errMsg);
                return [];
            }

            return ProjectDomainNames(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate forest domains for a forest-wide user lookup");
            return [];
        }
    }

    /// <summary>The domain names carried by a <c>(Get-ADForest).Domains</c> pipeline result.</summary>
    /// <remarks>
    /// Pure, so the projection is testable without a forest - the same split
    /// <see cref="ClassifyDestination"/> uses, and the reason the type error this replaced could
    /// ship unnoticed: nothing exercised it off a live directory.
    ///
    /// Blank entries are dropped rather than handed to <c>Get-ADUser -Server ""</c>, which throws
    /// and would turn the whole lookup Unavailable on one junk element.
    /// </remarks>
    internal static List<string> ProjectDomainNames(IEnumerable<PSObject> results) =>
        results
            .Select(o => o?.ToString())
            .Where(d => !string.IsNullOrWhiteSpace(d))
            .Select(d => d!.Trim())
            .ToList();
}
