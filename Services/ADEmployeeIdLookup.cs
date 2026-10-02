using System.Management.Automation;

namespace ExchangeAdminWeb.Services;

/// <summary>
/// Forest-wide lookup of the one directory user carrying a given <c>employeeID</c>.
/// </summary>
/// <remarks>
/// Added for Cloud Password Reset (docs/CloudPasswordReset-Plan.md, "Where the password goes"),
/// which derives a reset's destination mailbox from the employee ID stamped on the target cloud
/// account. It is a partial of <see cref="ADDirectorySearchService"/> rather than a new service
/// so it shares that class's throttled runspace, availability probe and outcome vocabulary -
/// three things a second AD service would have had to duplicate and could have got subtly
/// different.
///
/// **This reads ONE employee ID per call and never enumerates.** The module calls it once, for
/// the one account an operator is resetting (`.agents/decisions.md` 2026-09-22, "No tooling
/// enumerates the directory"). The per-domain loop below is coverage for a single subject across
/// a two-domain forest, not a sweep of a population.
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
    public DirectoryValidationResult FindUserByEmployeeId(string employeeId)
    {
        if (string.IsNullOrWhiteSpace(employeeId))
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.BlankInput), null);

        if (!IsAvailable)
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.DirectoryUnavailable), null);

        try
        {
            if (!_runspaceLock.Wait(TimeSpan.FromSeconds(30)))
            {
                _logger.LogWarning("AD employeeID lookup throttle timeout");
                return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.ThrottleTimeout), null);
            }

            try
            {
                return ExecuteFindUserByEmployeeId(employeeId.Trim());
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
            // that nobody carries the id.
            _logger.LogWarning(ex, "AD employeeID lookup failed");
            return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.LookupThrew), null);
        }
    }

    /// <summary>The LDAP filter for an exact employeeID match, escaped.</summary>
    /// <remarks>
    /// Escaping is not decoration here. An employee ID comes from a directory attribute, not from
    /// a fixed list, and an unescaped <c>*</c> turns this equality test into a wildcard that
    /// matches every stamped user in the domain. That would read as Ambiguous at best and, for a
    /// domain with one stamped user, as a confident wrong match.
    /// </remarks>
    internal static string BuildEmployeeIdFilter(string employeeId) =>
        $"(employeeID={ProtectedPrincipalService.EscapeLdapFilter(employeeId)})";

    private DirectoryValidationResult ExecuteFindUserByEmployeeId(string employeeId)
    {
        var domains = GetForestDomains();
        if (domains.Count == 0)
        {
            _logger.LogWarning("AD employeeID lookup could not enumerate forest domains");
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
              .AddParameter("LDAPFilter", BuildEmployeeIdFilter(employeeId))
              .AddParameter("Properties", new[] { "DisplayName", "mail", "Enabled", "employeeID", "ObjectGUID" })
              .AddParameter("ResultSetSize", 2)
              .AddParameter("ErrorAction", "Stop");

            var found = ps.Invoke();

            if (ps.HadErrors)
            {
                var errMsg = ps.Streams.Error.FirstOrDefault()?.Exception?.Message ?? "Get-ADUser failed";
                ps.Streams.Error.Clear();
                _logger.LogWarning("AD employeeID lookup errored against {Domain}: {Error}", domain, errMsg);
                return new DirectoryValidationResult(ClassifyOutcome(ValidationStep.CmdletReportedErrors), null);
            }

            matches.AddRange(found.Where(o => o is not null));
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
                ObjectGuid: obj.Properties["ObjectGUID"]?.Value?.ToString()),
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
                _logger.LogWarning("Could not enumerate forest domains for an employeeID lookup: {Error}", errMsg);
                return [];
            }

            return ProjectDomainNames(result);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not enumerate forest domains for an employeeID lookup");
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
