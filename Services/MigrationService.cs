using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Management.Automation;
using System.Management.Automation.Runspaces;
using System.Security.Claims;
using System.Text;
using CsvHelper;
using CsvHelper.Configuration;
using ExchangeAdminWeb.Models;

namespace ExchangeAdminWeb.Services;

public class MigrationService : ExchangeServiceBase
{
    private readonly IConfiguration _config;
    private readonly ModuleConfigService _moduleConfig;
    private readonly ProtectedPrincipalService _protectedPrincipals;
    private readonly ProtectedPrincipalServicerService _servicers;

    /// <summary>Module id for the servicer grant. Must match the catalog descriptor.</summary>
    private const string ServicerModuleId = "Migration";
    private readonly string[] _adminNotificationEmails;

    private string MigrationConfig(string key, string fallbackConfigKey, string defaultValue = "")
    {
        var val = _moduleConfig.GetValue("Migration", key);
        if (_moduleConfig.IsModuleCorrupt("Migration")) return "";
        if (val != null) return val;
        if (!_moduleConfig.HasModuleConfigFile("Migration"))
            return _config[fallbackConfigKey] ?? defaultValue;
        return defaultValue;
    }

    private string _hybridEndpoint => MigrationConfig("HybridEndpoint", "Migration:HybridEndpoint", "hybrid1");
    private string _cloudTargetDomain => MigrationConfig("CloudTargetDeliveryDomain", "Migration:CloudTargetDeliveryDomain");
    private string _onPremTargetDomain => MigrationConfig("OnPremTargetDeliveryDomain", "Migration:OnPremTargetDeliveryDomain");
    private long _cloudQuotaGB => long.TryParse(MigrationConfig("CloudQuotaGB", "Migration:CloudQuotaGB", "99"), out var v) ? v : 99;
    private string[] _excludedADGroups
    {
        get
        {
            var val = _moduleConfig.GetValue("Migration", "ExcludedADGroups");
            if (!string.IsNullOrEmpty(val))
                return val.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (!_moduleConfig.HasModuleConfigFile("Migration"))
                return _config.GetSection("Migration:ExcludedADGroups").Get<string[]>() ?? Array.Empty<string>();
            return Array.Empty<string>();
        }
    }

    public MigrationService(IConfiguration config, ExoConnectionPool exoPool, DelineaService delineaService, ILogger<MigrationService> logger, ModuleConfigService moduleConfig, ModuleCredentialService moduleCredentials, OperationTraceService operationTrace, ProtectedPrincipalService protectedPrincipals, ProtectedPrincipalServicerService servicers)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "", moduleCredentials, "Migration", operationTrace)
    {
        _config = config;
        _moduleConfig = moduleConfig;
        _protectedPrincipals = protectedPrincipals;
        _servicers = servicers;

        var adminEmail = config["Email:AdminNotificationEmail"] ?? "";
        _adminNotificationEmails = adminEmail.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToArray();
    }

    // aud-a23r. One check is one run of the excluded-group check's directory reads (DirectoryLookupRun); a bulk file is one
    // run for all its rows (CheckBulkMigrationEligibilityAsync).
    public Task<MigrationEligibilityResult> CheckMigrationEligibilityAsync(string emailAddress, MigrationDirection direction) =>
        CheckEligibilityAsync(emailAddress, direction, new DirectoryLookupRun());

    private async Task<MigrationEligibilityResult> CheckEligibilityAsync(string emailAddress, MigrationDirection direction, DirectoryLookupRun directoryRun)
    {
        var result = await RunPooledQueryAsync((ps, tracker) =>
        {
            var r = new MigrationEligibilityResult
            {
                EmailAddress = emailAddress,
                Status = MigrationStatus.Eligible
            };

            // aud-a23h. The read in flight when the check fails, so the reason names it; none once both are in.
            string? reading = "Get-MigrationUser";
            try
            {
                // aud-a23h. Stop, not Ignore. Under Ignore a read that failed - an error the cmdlet wrote, even a throw,
                // which Ignore runs straight past - came back as no rows: "no migration in progress" here, "no cloud
                // mailbox" below, and a mailbox Exchange could not be asked about was called eligible. ReadUnlessNotFound
                // keeps Exchange's own "no such object" an answer and throws every other failure into the catch.
                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var migUser = ReadUnlessNotFound(ps, tracker);
                if (migUser.Any())
                {
                    r.Status = MigrationStatus.Ineligible;
                    r.IneligibilityReasons.Add("Migration already in progress");
                }

                reading = "Get-Mailbox";
                ps.AddCommand("Get-Mailbox")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                var cloudMbx = ReadUnlessNotFound(ps, tracker);
                reading = null;
                var isCloudMailbox = false;

                if (cloudMbx.Any())
                {
                    var recipientType = cloudMbx.FirstOrDefault()?.Properties["RecipientTypeDetails"]?.Value?.ToString();
                    isCloudMailbox = recipientType?.Contains("UserMailbox") == true || recipientType?.Contains("SharedMailbox") == true;
                }

                if (direction == MigrationDirection.ToCloud)
                {
                    if (isCloudMailbox)
                    {
                        r.Status = MigrationStatus.Ineligible;
                        r.IneligibilityReasons.Add("Already a cloud mailbox");
                    }

                    if (_excludedADGroups.Length > 0)
                    {
                        r.NeedsAdGroupCheck = true;
                    }
                }
                else
                {
                    if (!isCloudMailbox)
                    {
                        r.Status = MigrationStatus.Ineligible;
                        r.IneligibilityReasons.Add("Not a cloud mailbox (must be in Exchange Online to migrate back to on-premises)");
                    }
                    else
                    {
                        var mbxObj = cloudMbx.FirstOrDefault();
                        var mailboxLocationsRaw = mbxObj?.Properties["MailboxLocations"]?.Value;
                        var hasAuxArchive = false;
                        if (mailboxLocationsRaw is System.Collections.IEnumerable locations and not string)
                        {
                            foreach (var loc in locations)
                                if (loc?.ToString()?.Contains("AuxArchive", StringComparison.OrdinalIgnoreCase) == true)
                                { hasAuxArchive = true; break; }
                        }
                        else if (mailboxLocationsRaw?.ToString()?.Contains("AuxArchive", StringComparison.OrdinalIgnoreCase) == true)
                        {
                            hasAuxArchive = true;
                        }
                        if (hasAuxArchive)
                        {
                            r.Status = MigrationStatus.Ineligible;
                            r.IneligibilityReasons.Add("Mailbox has AuxArchive locations and cannot be moved back to on-premises");
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // aud-a23h. A read that failed is not an answer. An Eligible verdict becomes Undetermined - "could not be
                // determined" on the page, and not Eligible, so no size step runs and no batch can be created from it;
                // Ineligible would be a guess too. An Ineligible verdict already reached stands: its reason is known
                // whatever the read that failed would have said. The reason in the cmdlet's own words (ResolveFailure).
                if (r.Status == MigrationStatus.Eligible)
                    r.Status = MigrationStatus.Undetermined;
                var reason = ResolveFailure(ex).primary;
                r.IneligibilityReasons.Add(reading is null
                    ? $"Could not be determined: {reason}"
                    : $"Could not be determined: {reading} failed: {reason}");
                _logger.LogError(ex, "Migration eligibility for {Email} could not be determined", emailAddress);
            }

            return r;
        }, allowRetry: true);

        if (direction == MigrationDirection.ToCloud && result.NeedsAdGroupCheck)
        {
            try
            {
                CheckExcludedGroups(result, _excludedADGroups, directoryRun);
            }
            catch (Exception ex)
            {
                // aud-a23j. A check that could not be completed is no answer. It used to add a warning and leave the
                // mailbox Eligible, so a batch could be created for a member of an excluded group. An Eligible verdict
                // becomes Undetermined - no size step, no batch (aud-a23h); an Ineligible one stands, its reason known.
                _logger.LogError(ex, "The excluded-group check for {Email} could not be completed", emailAddress);
                if (result.Status == MigrationStatus.Eligible)
                    result.Status = MigrationStatus.Undetermined;
                result.IneligibilityReasons.Add($"Could not be determined: the excluded-group check could not be completed: {ResolveFailure(ex).primary}");
            }
        }

        if (result.Status == MigrationStatus.Eligible)
        {
            try
            {
                if (direction == MigrationDirection.ToCloud)
                {
                    // aud-a23o. A size that could not be read is not 0 and not a reason to refuse: an Eligible verdict becomes
                    // Undetermined, in the base's words - no batch can be created from it - and a known reason (a size that was
                    // read and is over the quota) stands. It read an unparseable size as 0, which passed any quota, and filed
                    // every other failure as Ineligible "(on-prem connection unavailable)", a cause the base never reported.
                    var size = await GetOnPremMailboxSizeAsync(emailAddress);
                    result.MailboxSizeGB = size.MailboxGB;
                    result.ArchiveSizeGB = size.ArchiveGB;
                    result.MailboxSizeUnreadable = size.MailboxUnreadable;
                    result.ArchiveSizeUnreadable = size.ArchiveUnreadable;
                    result.CloudQuotaGB = _cloudQuotaGB;

                    // The primary mailbox and the archive are each capped at the per-mailbox
                    // limit on their own. Their combined size is not a migration criterion,
                    // so a user with a large mailbox AND a large archive stays eligible.
                    if (size.MailboxUnreadable is null && result.MailboxExceedsQuota)
                    {
                        result.Status = MigrationStatus.Ineligible;
                        result.IneligibilityReasons.Add($"Primary mailbox size ({size.MailboxGB:F2} GB) exceeds the per-mailbox limit ({_cloudQuotaGB} GB)");
                    }

                    if (size.ArchiveUnreadable is null && result.ArchiveExceedsQuota)
                    {
                        result.Status = MigrationStatus.Ineligible;
                        result.IneligibilityReasons.Add($"Archive size ({size.ArchiveGB:F2} GB) exceeds the per-mailbox limit ({_cloudQuotaGB} GB)");
                    }

                    if (size.UnreadableReason("on-premises") is { } unreadable)
                    {
                        if (result.Status == MigrationStatus.Eligible)
                            result.Status = MigrationStatus.Undetermined;
                        result.IneligibilityReasons.Add($"Could not be determined: {unreadable}");
                    }
                }
                else
                {
                    // aud-a23k. A size that could not be read is a marker with its reason, never 0. No move back
                    // on-premises is decided by size (no quota applies there), so none is refused: the page says it.
                    var cloudSize = await GetCloudMailboxSizeAsync(emailAddress);
                    result.MailboxSizeGB = cloudSize.MailboxGB;
                    result.ArchiveSizeGB = cloudSize.ArchiveGB;
                    result.MailboxSizeUnreadable = cloudSize.MailboxUnreadable;
                    result.ArchiveSizeUnreadable = cloudSize.ArchiveUnreadable;
                }
            }
            catch (Exception sizeEx)
            {
                _logger.LogWarning(sizeEx, "Could not retrieve mailbox size for {Email}", emailAddress);
                if (direction == MigrationDirection.ToCloud)
                {
                    // aud-a23o. A size read that failed as a whole - the throttle, a cancellation neither read's catch takes -
                    // marks both sizes and is Undetermined, never Ineligible.
                    var unread = MailboxSizes.Unreadable(ResolveFailure(sizeEx).primary.TrimEnd('.'));
                    result.MailboxSizeUnreadable = unread.MailboxUnreadable;
                    result.ArchiveSizeUnreadable = unread.ArchiveUnreadable;
                    if (result.Status == MigrationStatus.Eligible)
                        result.Status = MigrationStatus.Undetermined;
                    result.IneligibilityReasons.Add($"Could not be determined: {unread.UnreadableReason("on-premises")}");
                }
                else
                {
                    // aud-a23k. A size read that failed as a whole - the pool, the connection, a cancellation neither
                    // read's own catch takes - marks both sizes; this catch used to drop it and the page showed none.
                    result.MailboxSizeUnreadable = result.ArchiveSizeUnreadable =
                        $"the size read failed: {ResolveFailure(sizeEx).primary.TrimEnd('.')}";
                }
            }
        }

        // Flag protected principals as a separate axis from the Ex/AD verdict. A protected
        // target keeps its real Eligible/Ineligible status but must be escalated outside this
        // tool; the UI suppresses single-user batch creation for it, and the GAP 2 gate filters
        // it out of bulk batches at creation time. Fail-closed via the shared protection check.
        await ApplyProtectionFlagAsync(result);

        return result;
    }

    // aud-a23j. The Active Directory session the excluded-group check opens, as ExchangeServiceBase.CheckAdGroupMembership
    // built it (that method is gone, aud-a23o; not the Exchange pool). Internal virtual as a test seam, the shape of GroupManagementService's: a test hands
    // back a session whose Import-Module and AD reads are stand-ins, so the check runs for real with no directory.
    internal virtual Runspace CreateDirectoryRunspace()
    {
        var iss = InitialSessionState.CreateDefault();
        iss.ExecutionPolicy = Microsoft.PowerShell.ExecutionPolicy.Bypass;
        return RunspaceFactory.CreateRunspace(iss);
    }

    // aud-a23j. The excluded-group check of a move to Exchange Online, moved here from
    // ExchangeServiceBase.CheckAdGroupMembership. That matched each configured value as a SUBSTRING of the user's memberOf
    // DNs - a group of a similar name, the name in an OU path, a fragment of a DN cut at the setting's commas all matched
    // groups that are not the excluded one - and returned without checking when no directory user had the address as its
    // UPN. Here each configured value is RESOLVED to its group (Get-ADGroup -Identity: a DN, GUID, SID or sAMAccountName)
    // and that group's DN compared exactly, ignoring case as AD does, with the user's memberOf - the Constitution's "OU and
    // group checks must be based on resolved directory objects, not substring matches". Whatever keeps the check from
    // completing - the module, the lookup, no user or several, no memberOf, a group that does not resolve to one - throws
    // to the caller, which makes the verdict Undetermined. Membership is direct (memberOf), as before.
    //
    // aud-a23r. Across the forest, not the host's domain: without -Server, Get-ADUser was answered by a domain controller of
    // the host's own domain, so a user of any other domain read "no directory user has the user principal name ..." -
    // Undetermined, a valid mailbox blocked. The user is found on the forest's global catalog (discovered at runtime from the
    // host's own forest, nothing named - Invariant 7) and read again on its own domain for memberOf, which a catalog holds
    // only partly for another domain's user; one found nowhere in the forest is still Undetermined. Each configured group is
    // resolved in each domain of the forest and must be one group across it (ResolveExcludedGroup). The catalog, the
    // domains and each group are read once per run and kept, failures too. ASSUMPTION, unverified (no live call): a -Filter
    // search on the catalog port spans the forest's domains, as the repo measured an -LDAPFilter one doing
    // (ADDirectorySearchService.ResolveGlobalCatalog); and the app-pool identity can read the catalog and a domain
    // controller of each domain. Either wrong, and the check is Undetermined - visible, never Eligible.
    private void CheckExcludedGroups(MigrationEligibilityResult result, IReadOnlyList<string> excludedGroups, DirectoryLookupRun run)
    {
        using var runspace = CreateDirectoryRunspace();
        runspace.Open();
        using var ps = PowerShell.Create();
        ps.Runspace = runspace;

        ps.AddCommand("Import-Module")
          .AddParameter("Name", "ActiveDirectory")
          .AddParameter("ErrorAction", "Stop");
        DirectoryStep(ps, "the ActiveDirectory module could not be loaded");

        var catalog = (run.Catalog ??= DirectoryRead<string>.Of(() => ForestGlobalCatalog(ps))).Get();

        ps.AddCommand("Get-ADUser")
          .AddParameter("Filter", $"UserPrincipalName -eq '{result.EmailAddress.Replace("'", "''")}'")
          .AddParameter("Server", catalog)
          .AddParameter("ErrorAction", "Stop");
        var users = DirectoryStep(ps, "Get-ADUser failed");
        if (users.Count != 1)
        {
            throw new InvalidOperationException(users.Count == 0
                ? $"no directory user in the forest has the user principal name {result.EmailAddress}"
                : $"{users.Count} directory users in the forest have the user principal name {result.EmailAddress}");
        }

        var userDn = users[0].Properties["DistinguishedName"]?.Value?.ToString()?.Trim();
        var userDomain = GroupManagementService.ServerFromDn(userDn)
            ?? throw new InvalidOperationException(
                $"the directory user found for {result.EmailAddress} has no distinguished name that names its domain ({(string.IsNullOrEmpty(userDn) ? "none" : userDn)})");

        ps.AddCommand("Get-ADUser")
          .AddParameter("Identity", userDn)
          .AddParameter("Server", userDomain)
          .AddParameter("Properties", "memberOf")
          .AddParameter("ErrorAction", "Stop");
        var inDomain = DirectoryStep(ps, $"reading {userDn} in its own domain {userDomain} failed");
        if (inDomain.Count != 1)
            throw new InvalidOperationException($"reading {userDn} in its own domain {userDomain} returned {inDomain.Count} users");

        var memberOf = MemberOfDistinguishedNames(inDomain[0], result.EmailAddress);

        foreach (var configured in excludedGroups)
        {
            if (!run.Groups.TryGetValue(configured, out var group))
                run.Groups[configured] = group = DirectoryRead<string>.Of(() => ResolveExcludedGroup(ps, configured, run));

            if (memberOf.Contains(group.Get()))
            {
                result.Status = MigrationStatus.Ineligible;
                result.IneligibilityReasons.Add($"Member of excluded group: {configured}");
                _logger.LogWarning("User {Email} is ineligible for cloud migration - member of {Group}", result.EmailAddress, configured);
            }
        }
    }

    // aud-a23r. The forest's global catalog as "host:3268", from the host's own forest. AddScript with the projection inside
    // PowerShell: GlobalCatalogs read off the returned object comes back empty in a fresh runspace (measured -
    // ADDirectorySearchService.ResolveGlobalCatalog). Never a bare ":3268", which the AD cmdlets accept and quietly answer
    // from the local domain - the one-domain search this replaces.
    private static string ForestGlobalCatalog(PowerShell ps)
    {
        ps.AddScript("(Get-ADForest -ErrorAction Stop).GlobalCatalogs | Select-Object -First 1");
        var host = DirectoryStep(ps, "the forest's global catalog could not be found").FirstOrDefault(o => o is not null)?.ToString()?.Trim();
        return string.IsNullOrEmpty(host)
            ? throw new InvalidOperationException("the forest reported no global catalog")
            : $"{host}:3268";
    }

    // aud-a23r. The forest's domain names, projected inside PowerShell for the reasons ADDirectorySearchService's
    // GetForestDomains gives; blank entries dropped. None is no answer.
    private static IReadOnlyList<string> ForestDomains(PowerShell ps)
    {
        ps.AddScript("(Get-ADForest -ErrorAction Stop).Domains");
        var domains = DirectoryStep(ps, "the forest's domains could not be read")
            .Select(o => o?.ToString()?.Trim())
            .Where(d => !string.IsNullOrEmpty(d))
            .Select(d => d!)
            .ToList();
        return domains.Count > 0 ? domains : throw new InvalidOperationException("the forest reported no domains");
    }

    // aud-a23r. A configured group resolved in each domain of the forest with Get-ADGroup -Identity (its sAMAccountName, SID
    // or objectGUID - what the cmdlet takes): the AD module's not-found there, by structure, is "not in that domain", and
    // exactly one group across the forest is the group. None, several - a name two domains use, which its SID or objectGUID
    // tells apart - or a domain that could not be asked cannot be checked: another domain might hold a second group by that
    // name. aud-a23j asked the host's domain alone. ASSUMPTION, unverified (no live call): "not in this domain" is
    // ADIdentityNotFoundException under ObjectNotFound, the shape IsNotFound reads.
    private static string ResolveExcludedGroup(PowerShell ps, string configured, DirectoryLookupRun run)
    {
        var domains = (run.Domains ??= DirectoryRead<IReadOnlyList<string>>.Of(() => ForestDomains(ps))).Get();
        var found = new List<(string Domain, string? Dn)>();
        var absent = new List<string>();
        foreach (var domain in domains)
        {
            ps.AddCommand("Get-ADGroup")
              .AddParameter("Identity", configured)
              .AddParameter("Server", domain)
              .AddParameter("ErrorAction", "Stop");
            Collection<PSObject> groups;
            try
            {
                groups = Invoke(ps);
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                absent.Add($"{domain}: {ResolveFailure(ex).primary.TrimEnd('.')}");
                continue;
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                throw new InvalidOperationException(
                    $"the excluded group '{configured}' could not be resolved in {domain}: {ResolveFailure(ex).primary.TrimEnd('.')}", ex);
            }

            if (groups.Count == 0)
                absent.Add($"{domain}: no group was returned");
            found.AddRange(groups.Select(group => (domain, group.Properties["DistinguishedName"]?.Value?.ToString()?.Trim())));
        }

        if (found.Count == 0)
            throw new InvalidOperationException($"the excluded group '{configured}' could not be resolved in any domain of the forest ({string.Join("; ", absent)})");
        if (found.Count > 1)
        {
            throw new InvalidOperationException(
                $"the excluded group '{configured}' resolved to {found.Count} groups across the forest ({string.Join(", ", found.Select(f => f.Domain).Distinct(StringComparer.OrdinalIgnoreCase))})");
        }

        return string.IsNullOrEmpty(found[0].Dn)
            ? throw new InvalidOperationException($"the excluded group '{configured}' was returned with no distinguished name")
            : found[0].Dn!;
    }

    // aud-a23r. What one check run learns of the forest: the catalog, the domains, each configured group's DN - or why one
    // could not be read, kept too, so every row of a run is told the same and the directory is not asked again. A new run
    // asks again, so a group put right in the directory is seen.
    private sealed class DirectoryLookupRun
    {
        public DirectoryRead<string>? Catalog { get; set; }

        public DirectoryRead<IReadOnlyList<string>>? Domains { get; set; }

        public Dictionary<string, DirectoryRead<string>> Groups { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    // aud-a23r. A read made once: its value, or why there is none. A cancellation is not kept; it escapes.
    private sealed record DirectoryRead<T>(T? Value, string? Failure) where T : class
    {
        public T Get() => Value ?? throw new InvalidOperationException(Failure);

        public static DirectoryRead<T> Of(Func<T> read)
        {
            try
            {
                return new(read(), null);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                return new(null, ex.Message);
            }
        }
    }

    // aud-a23j. One step of the check under Stop, through the base's Invoke, which fails on an error and on HadErrors alike;
    // a failure names the step, in the cmdlet's own words (ResolveFailure).
    private static Collection<PSObject> DirectoryStep(PowerShell ps, string failure)
    {
        try
        {
            return Invoke(ps);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException($"{failure}: {ResolveFailure(ex).primary.TrimEnd('.')}", ex);
        }
    }

    // aud-a23j. The user's memberOf as a set of DNs, compared ignoring case. ASSUMPTION, unverified (no live call): the AD
    // module reports a requested memberOf as a list, empty for a user in no group. Absent, or not a list, it cannot be read:
    // the check cannot be completed - visible and blocking - never "in no excluded group".
    private static HashSet<string> MemberOfDistinguishedNames(PSObject user, string emailAddress)
    {
        var reported = user.Properties["memberOf"]?.Value;
        if (reported is PSObject wrapped)
            reported = wrapped.BaseObject;
        if (reported is null)
            throw new InvalidOperationException($"the directory reported no memberOf for {emailAddress}");

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (reported is string single)
        {
            if (!string.IsNullOrWhiteSpace(single))
                names.Add(single.Trim());
            return names;
        }

        if (reported is not System.Collections.IEnumerable values)
            throw new InvalidOperationException($"the directory's memberOf for {emailAddress} could not be read as a list of distinguished names");

        foreach (var value in values)
        {
            var name = (value is PSObject item ? item.BaseObject : value)?.ToString();
            if (!string.IsNullOrWhiteSpace(name))
                names.Add(name.Trim());
        }

        return names;
    }

    public async Task<MigrationBatchResult> CheckBulkMigrationEligibilityAsync(Stream csvStream, MigrationDirection direction)
    {
        var config = new CsvConfiguration(CultureInfo.InvariantCulture)
        {
            HeaderValidated = null,
            MissingFieldFound = null
        };

        using var reader = new StreamReader(csvStream, Encoding.UTF8);
        using var csv = new CsvReader(reader, config);

        var records = new List<MigrationCsvRow>();
        await foreach (var row in csv.GetRecordsAsync<MigrationCsvRow>())
            records.Add(row);
        var results = new List<MigrationEligibilityResult>();
        // aud-a23r. The file is one run: the forest and each excluded group are read once for all its rows.
        var directoryRun = new DirectoryLookupRun();

        foreach (var row in records)
        {
            var eligibility = await CheckEligibilityAsync(row.EmailAddress, direction, directoryRun);
            results.Add(eligibility);
        }

        var eligible = results.Count(r => r.Status == MigrationStatus.Eligible);
        // aud-a23h. An undetermined row is neither eligible nor ineligible: counted apart, and named on the page.
        var ineligible = results.Count(r => r.Status == MigrationStatus.Ineligible);
        var undetermined = results.Count(r => r.Status == MigrationStatus.Undetermined);

        return new MigrationBatchResult
        {
            BatchName = $"{DateTime.Now:yyyy-MM-dd}_{(direction == MigrationDirection.ToCloud ? "Move" : "MoveBack")}",
            Success = true,
            Direction = direction,
            EligibilityResults = results,
            TotalUsers = results.Count,
            EligibleUsers = eligible,
            IneligibleUsers = ineligible,
            UndeterminedUsers = undetermined,
            AutoStart = false,
            AutoComplete = false
        };
    }

    /// <summary>
    /// In-service protected-principal gate, enforced immediately before a migration target is
    /// written into a batch, regardless of caller or identity format. Mirrors
    /// <see cref="GroupManagementService"/>'s gate: fails closed when resolution is Unavailable
    /// or Ambiguous, and on any exception. Returns null when the target is clear to migrate, or
    /// a Fail result (whose Message is the operator-facing reason) to exclude it.
    /// </summary>
    /// <summary>
    /// Outcome of the gate for ONE target: a <paramref name="Denial"/> excluding it, or null to
    /// migrate it. <paramref name="ServicedNote"/> is set only when the target was protected and an
    /// authorised servicer overrode the exclusion; it must reach the page's audit call.
    /// aud-a23l: <paramref name="IsProtected"/> is true only when the denial IS the check's answer
    /// that the target is protected. A check that could not answer - unavailable, ambiguous, failed,
    /// thrown - says nothing either way, so its denial leaves it false; and false is the default, so
    /// a denial added later that forgets it reads "could not be verified", never a claim of protection.
    /// </summary>
    internal readonly record struct ProtectionGate(PermissionResult? Denial, string? ServicedNote, bool IsProtected = false);

    private async Task<ProtectionGate> CheckProtectedAsync(string identity, ClaimsPrincipal? actingUser)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return new(null, null);

        try
        {
            // Exchange fallback, not AD-only: an AD miss reports every cloud-only and
            // alias-addressed target as NotFound, which skipped the check entirely.
            // docs/ProtectedPrincipalGapFix-Plan.md GAP B.
            var (resolved, status) = await _protectedPrincipals.ResolveWithExchangeFallbackAsync(identity);
            if (status is ProtectedPrincipalService.ResolutionStatus.Unavailable
                       or ProtectedPrincipalService.ResolutionStatus.Ambiguous)
            {
                return new(PermissionResult.Fail(status == ProtectedPrincipalService.ResolutionStatus.Ambiguous
                    ? "Identity is ambiguous - matches multiple AD users."
                    : "Protection check unavailable. Cannot verify if this mailbox is protected."), null);
            }

            if (resolved != null)
            {
                var check = await _protectedPrincipals.CheckAsync(resolved);
                if (check.CheckFailed)
                    return new(PermissionResult.Fail($"Protection check failed: {check.Reason}"), null);

                // An authorised servicer may proceed. Protection is evaluated first and never
                // weakened - this only decides whether THIS operator may migrate a target already
                // known to be protected. A null actingUser refuses, so any caller that does not
                // supply one is safe. Note this overrides only the PROTECTED verdict: an
                // Unavailable, Ambiguous or failed check above already returned, because a check
                // that could not complete does not know whether the target is protected, so there
                // is no refusal for a servicer to override.
                if (check.IsProtected)
                {
                    var servicedNote = ProtectedPrincipalServicing.NoteFor(
                        _servicers, actingUser, ServicerModuleId, check.MatchedRules, qualifier: identity);

                    if (servicedNote is null)
                        return new(PermissionResult.Fail("This mailbox is a protected principal. Operation not permitted."), null, IsProtected: true);

                    return new(null, servicedNote);
                }
            }

            return new(null, null);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protected principal check failed for migration target {Identity} - excluding as precaution", identity);
            return new(PermissionResult.Fail($"Protection check error: {ex.Message}"), null);
        }
    }

    /// <summary>
    /// Splits migration targets into those clear to migrate and those excluded by the
    /// protected-principal gate. Each excluded entry is an operator-facing "identity - reason"
    /// string. Runs before any batch side effect, so a protected target never reaches the write
    /// path. The <paramref name="checker"/> seam exists for unit testing; production passes null
    /// and the real gate is used.
    /// </summary>
    /// <remarks>
    /// Servicing is decided PER TARGET, so a batch can carry a mix: some targets clear, some
    /// excluded, some protected-but-serviced. The serviced notes come back alongside the allowed
    /// list so the caller can audit each override individually - a batch-level "some target was
    /// serviced" would not say which, which is the fact an audit exists to answer.
    /// </remarks>
    internal async Task<(List<string> allowed, List<string> excluded, List<string> servicedNotes, List<string> unverified)> PartitionByProtectionAsync(
        IEnumerable<string> targets,
        ClaimsPrincipal? actingUser = null,
        Func<string, Task<ProtectionGate>>? checker = null)
    {
        checker ??= target => CheckProtectedAsync(target, actingUser);

        var allowed = new List<string>();
        var excluded = new List<string>();
        var servicedNotes = new List<string>();
        var unverified = new List<string>();

        foreach (var target in targets)
        {
            var gate = await checker(target);
            if (gate.Denial == null)
            {
                allowed.Add(target);
                if (!string.IsNullOrWhiteSpace(gate.ServicedNote))
                    servicedNotes.Add(gate.ServicedNote!);
            }
            else
            {
                // aud-a23l. Each exclusion is also listed by kind, so the caller can say which it is: a target whose
                // check did not answer is not a protected principal, only one not verified as unprotected.
                var entry = $"{target} - {gate.Denial.Message}";
                excluded.Add(entry);
                if (!gate.IsProtected)
                    unverified.Add(entry);
            }
        }

        return (allowed, excluded, servicedNotes, unverified);
    }

    /// <summary>
    /// Flags an eligibility result if its target is a protected principal (or protection
    /// cannot be verified - fail-closed). Sets <see cref="MigrationEligibilityResult.IsProtected"/>
    /// and <see cref="MigrationEligibilityResult.ProtectionNote"/> but never changes
    /// <see cref="MigrationEligibilityResult.Status"/>: protection is an orthogonal axis to the
    /// Exchange/AD eligibility verdict. The <paramref name="checker"/> seam exists for unit
    /// testing; production passes null and the real gate is used.
    /// </summary>
    /// <remarks>
    /// This is the ELIGIBILITY display path, not a write, so it deliberately does not service:
    /// no actingUser is taken and the flag reflects the protection verdict alone. An operator who
    /// holds the grant still sees the target flagged as protected, and the override is applied and
    /// recorded at the batch write, which is where the mutation actually happens. Flagging it as
    /// clear here would hide from the operator that they are about to use their grant.
    /// </remarks>
    internal async Task ApplyProtectionFlagAsync(
        MigrationEligibilityResult result,
        Func<string, Task<ProtectionGate>>? checker = null)
    {
        checker ??= target => CheckProtectedAsync(target, actingUser: null);

        var gate = await checker(result.EmailAddress);
        if (gate.Denial != null)
        {
            result.IsProtected = true;
            result.ProtectionNote = gate.Denial.Message;
        }
    }

    /// <param name="actingUser">
    /// REQUIRED and not defaulted: the servicer decision needs a real principal, and a default
    /// would silently make every caller that forgot it unable to service - or invite an ambient
    /// lookup, which attributes a bypass to whoever is on the thread. Null refuses.
    /// </param>
    public async Task<PermissionResult> CreateMigrationBatchAsync(MigrationDirection direction, List<string> eligibleEmails, string batchName, bool autoStart, bool autoComplete, ClaimsPrincipal? actingUser)
    {
        string[]? targetDatabases = null;
        if (direction == MigrationDirection.ToOnPrem)
        {
            targetDatabases = MigrationTargetDatabaseSelector.Resolve(_moduleConfig, _config);
            if (targetDatabases.Length == 0)
                return PermissionResult.Fail("No on-prem target databases are configured. Check Migration:OnPremTargetDatabases or the Migration module configuration.");
        }

        // Protected-principal gate (Constitution: protected principals are off-limits to every
        // mutating module). Partition the targets BEFORE building the CSV or invoking
        // New-MigrationBatch so a protected mailbox can never reach the write path. Per owner
        // decision (2026-06-30): one protected target must not block the whole batch and the
        // exclusion must never be silent - filter the protected targets out, migrate the rest,
        // and report the exclusions back clearly.
        var (allowedEmails, excludedTargets, servicedNotes, unverifiedTargets) = await PartitionByProtectionAsync(eligibleEmails, actingUser);

        if (allowedEmails.Count == 0)
        {
            // Every target was excluded (includes the single-protected-target case). Create
            // nothing and tell the operator plainly why.
            return new PermissionResult
            {
                Success = false,
                Message = AllExcludedMessage(excludedTargets, unverifiedTargets),
                Detail = "Excluded:\n" + string.Join("\n", excludedTargets),
                ExcludedTargets = excludedTargets,
                UnverifiedTargets = unverifiedTargets
            };
        }

        // NOT retry-eligible: New-MigrationBatch plus conditional Start-/Set-MigrationBatch are
        // multiple writes; New- is not idempotent (retry would duplicate or collide on the batch
        // name). A dead session discards and fails as before; the operator re-runs manually.
        var result = await RunAsync((ps, tracker) =>
        {
            // Create CSV content in memory
            var csvContent = "EmailAddress\r\n" + string.Join("\r\n", allowedEmails);
            var csvBytes = Encoding.UTF8.GetBytes(csvContent);

            if (direction == MigrationDirection.ToCloud)
            {
                ps.AddCommand("New-MigrationBatch")
                  .AddParameter("Name", batchName)
                  .AddParameter("SourceEndpoint", _hybridEndpoint)
                  .AddParameter("TargetDeliveryDomain", _cloudTargetDomain)
                  .AddParameter("CSVData", csvBytes)
                  .AddParameter("NotificationEmails", _adminNotificationEmails)
                  .AddParameter("ErrorAction", "Stop");
            }
            else // ToOnPrem
            {
                ps.AddCommand("New-MigrationBatch")
                  .AddParameter("Name", batchName)
                  .AddParameter("TargetEndpoint", _hybridEndpoint)
                  .AddParameter("TargetDeliveryDomain", _onPremTargetDomain)
                  .AddParameter("TargetDatabases", targetDatabases!)
                  .AddParameter("CSVData", csvBytes)
                  .AddParameter("NotificationEmails", _adminNotificationEmails)
                  .AddParameter("ErrorAction", "Stop");
            }

            Invoke(ps, tracker);

            // Auto-start if requested
            if (autoStart)
            {
                ps.AddCommand("Start-MigrationBatch")
                  .AddParameter("Identity", batchName)
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);
            }

            // Auto-complete if requested
            if (autoComplete)
            {
                ps.AddCommand("Set-MigrationBatch")
                  .AddParameter("Identity", batchName)
                  // A PAST time is the Exchange idiom for "finalise at the first opportunity".
                  // Deliberate here: this is the auto-complete the operator ticked at creation.
                  // S8 separated this from a real schedule - see
                  // ScheduleMigrationBatchCompletionAsync, which is the only writer of a FUTURE
                  // CompleteAfter and refuses a past one.
                  .AddParameter("CompleteAfter", DateTime.Now.AddHours(-1))
                  .AddParameter("ErrorAction", "Stop");
                Invoke(ps, tracker);
            }

        }, () =>
        {
            var userList = string.Join(", ", allowedEmails);

            string message;
            if (autoStart && allowedEmails.Count == 1)
            {
                message = $"Mailbox \"{allowedEmails[0]}\" migration is in progress. Please monitor it in the portal.";
            }
            else if (autoStart)
            {
                message = $"Migration batch '{batchName}' with {allowedEmails.Count} user(s) is in progress. Please monitor it in the portal.";
            }
            else if (allowedEmails.Count == 1)
            {
                message = $"Mailbox \"{allowedEmails[0]}\" migration batch created. Please monitor it in the portal.";
            }
            else
            {
                message = $"Migration batch '{batchName}' created successfully with {allowedEmails.Count} user(s). Please monitor it in the portal.";
            }

            var details = $@"Batch Name: {batchName}
Direction: {direction}
On-Prem Target Databases: {(targetDatabases == null || targetDatabases.Length == 0 ? "N/A" : string.Join(", ", targetDatabases))}
Users: {userList}
Count: {allowedEmails.Count}
Auto-Start: {autoStart}
Auto-Complete: {autoComplete}";

            if (excludedTargets.Count > 0)
            {
                // aud-a23p. Only protected: the header and the escalation line as before; otherwise each kind counted, and
                // the advice for the kinds present. It said "protected principals" of every exclusion.
                var (header, advice) = unverifiedTargets.Count == 0
                    ? ($"EXCLUDED - protected principals, NOT migrated ({excludedTargets.Count}):", "Escalate these to an administrator outside this tool if migration is required.")
                    : ($"EXCLUDED, NOT migrated ({excludedTargets.Count}) - {ExclusionSummary(excludedTargets, unverifiedTargets)}", ExclusionAdvice(excludedTargets, unverifiedTargets));
                details += $@"

{header}
{string.Join("\n", excludedTargets)}
{advice}";
            }

            details += @"

Monitor progress in the Exchange Admin Center at:
https://admin.exchange.microsoft.com/#/migration";

            return (message, details);
        });

        // Every serviced override in the batch, one line each, so the audit names which targets
        // were overridden rather than only that some were. Null when nothing was serviced.
        var servicedSummary = servicedNotes.Count > 0 ? string.Join("\n", servicedNotes) : null;

        // Carry the protected-principal exclusions back to the caller so the page and the admin
        // notification can surface them. RunAsync builds the result; re-wrap to attach the list
        // (and prepend a clear note to the success message) without losing anything else.
        if (excludedTargets.Count > 0 || servicedSummary != null)
        {
            return WithProtectionOutcome(result, excludedTargets, servicedSummary, unverifiedTargets);
        }

        return result;
    }

    // aud-a23l. Why nothing was created when every target was excluded, saying which kind each exclusion is. It said "is a
    // protected principal" (or "are protected principals") for all of them, including a target whose protection check
    // could not answer; that is said only of a target the check found protected now, and one it could not verify is "could
    // not be verified as unprotected", with its reason. The Detail lists every target and its reason, as before.
    private static string AllExcludedMessage(IReadOnlyList<string> excluded, IReadOnlyList<string> unverified)
    {
        if (unverified.Count == 0)
        {
            return excluded.Count == 1
                ? "The migration target is a protected principal and was excluded. Nothing was created. Escalate to an administrator outside this tool if migration is required."
                : $"All {excluded.Count} migration target(s) are protected principals and were excluded. Nothing was created. Escalate to an administrator outside this tool if migration is required.";
        }

        if (excluded.Count == 1)
            return $"The migration target could not be verified as unprotected: {unverified[0].TrimEnd('.')}; nothing was written.";

        if (unverified.Count == excluded.Count)
            return $"None of the {excluded.Count} migration targets could be verified as unprotected; nothing was written.";

        var protectedCount = excluded.Count - unverified.Count;
        return $"All {excluded.Count} migration targets were excluded: {protectedCount} "
            + (protectedCount == 1 ? "is a protected principal" : "are protected principals")
            + $" and {unverified.Count} could not be verified as unprotected; nothing was written. "
            + "Escalate the protected ones to an administrator outside this tool if migration is required.";
    }

    /// <summary>
    /// The exclusion re-wrap: the result <c>RunAsync</c> built, with the batch's protected-principal
    /// exclusions attached, a NOTE on a success's message, and the batch's serviced overrides as its
    /// <see cref="PermissionResult.ServicedNote"/>. Every other field is carried as it was. aud-a23p: and
    /// the exclusions whose protection check did not answer, as <see cref="PermissionResult.UnverifiedTargets"/>,
    /// which the NOTE counts apart (<see cref="ExclusionSummary"/>).
    /// </summary>
    /// <remarks>
    /// Audit finding aud-a23c. This was an initializer at the call site that named the fields it
    /// knew about, so it dropped <see cref="PermissionResult.AppliedRights"/> (aud-a23) and reset
    /// <see cref="PermissionResult.Changed"/> to its default TRUE (aud-a25) - an init property left
    /// out of an initializer keeps its default. Like <see cref="PermissionResult.WithServicedNote"/>,
    /// this names every property; MigrationResultRewrapTests sets each one and checks it survives,
    /// so a field the model gains later is held to the same rule. With <see cref="WithDetail"/>, the
    /// module's only copies of a result.
    /// </remarks>
    internal static PermissionResult WithProtectionOutcome(PermissionResult result, IReadOnlyList<string> excludedTargets, string? servicedSummary, IReadOnlyList<string>? unverifiedTargets = null)
        => new()
        {
            Success = result.Success,
            Message = result.Success && excludedTargets.Count > 0
                ? $"{result.Message} NOTE: {ExclusionSummary(excludedTargets, unverifiedTargets)}"
                : result.Message,
            Detail = result.Detail,
            ExcludedTargets = excludedTargets,
            UnverifiedTargets = unverifiedTargets,
            AppliedRights = result.AppliedRights,
            Changed = result.Changed,
            ServicedNote = servicedSummary,
        };

    // aud-a23p. How many exclusions of each kind, for every site that names them - the NOTE, the Detail, the page's banners
    // and its notification. A target whose protection check did not answer (UnverifiedTargets) is not known to be protected
    // and is never called a protected principal; they all were. When every exclusion is protected the words are as before.
    internal static string ExclusionSummary(IReadOnlyList<string> excluded, IReadOnlyList<string>? unverified)
    {
        var notVerified = excluded.Count(entry => unverified?.Contains(entry) == true);
        var protectedCount = excluded.Count - notVerified;
        if (notVerified == 0)
            return $"{excluded.Count} protected principal(s) were excluded and NOT migrated.";
        if (protectedCount == 0)
            return $"{notVerified} target(s) could not be verified as unprotected and were excluded, NOT migrated.";
        return $"{excluded.Count} target(s) were excluded and NOT migrated: {protectedCount} protected principal(s) and {notVerified} that could not be verified as unprotected.";
    }

    // aud-a23p. What to do with each kind: escalate a protected one; check an unverified one again once its check can
    // answer. Only protected exclusions: the page's sentence as before.
    internal static string ExclusionAdvice(IReadOnlyList<string> excluded, IReadOnlyList<string>? unverified)
    {
        const string escalate = "Migrating a protected principal is not permitted in this tool. Escalate to an administrator outside this tool if migration is required.";
        const string recheck = "A target whose protection check could not answer is excluded until it can be verified as unprotected; check it again once the protection check is available.";
        var notVerified = excluded.Count(entry => unverified?.Contains(entry) == true);
        return notVerified == 0 ? escalate : notVerified == excluded.Count ? recheck : $"{escalate} {recheck}";
    }

    /// <summary>
    /// The same result with <see cref="PermissionResult.Detail"/> replaced and every other field
    /// carried. The Migration page notes a failed audit write this way on the result it shows.
    /// </summary>
    /// <remarks>
    /// Audit finding aud-a23c: the page's four failed-audit catches rebuilt the result field by
    /// field and dropped AppliedRights and Changed, and two of them ServicedNote and ExcludedTargets
    /// as well. See <see cref="WithProtectionOutcome"/>.
    /// </remarks>
    internal static PermissionResult WithDetail(PermissionResult result, string? detail)
        => new()
        {
            Success = result.Success,
            Message = result.Message,
            Detail = detail,
            ExcludedTargets = result.ExcludedTargets,
            UnverifiedTargets = result.UnverifiedTargets,
            AppliedRights = result.AppliedRights,
            Changed = result.Changed,
            ServicedNote = result.ServicedNote,
        };

    // aud-a23q. A failed authorization on a single action or a create: the bulk executors' "_Denied" row - action, target,
    // Failed, ticket, "<policy> authorization denied" - with mutationAttempted false (the aud-a28 refusal convention), and the
    // text the page shows and ends the activity with. It returned in silence: no banner, no row, an activity that read
    // "Nothing was done.". A failed audit write is logged and swallowed, as the bulk executors' is, so it cannot take the
    // denial's place. Static, with the page's services passed in, so each page line stays one statement (the page is
    // line-keyed by ClickGateRegistry).
    internal static string RecordDenial(AuditService audit, ILogger logger, string actor, string ipAddress,
        string auditAction, string description, string target, string ticket, string policy)
    {
        try
        {
            audit.LogMigrationAction(actor, ipAddress, $"{auditAction}_Denied", target, false, ticket,
                errorDetail: $"{policy} authorization denied",
                extra: new Dictionary<string, object?> { ["mutationAttempted"] = false });
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to write audit for {Action} denial", auditAction);
        }

        return $"Not authorized to {description}; nothing was done.";
    }

    // aud-a23q. Why a create's guard stopped it, in the guard's own order: no check yet; a check that is not Eligible (or no
    // eligible row); no ticket. It returned with nothing shown. Not audited: nothing was attempted (the bulk executors'
    // "nothing to do" rule). NotNullWhen tells the page's null analysis what its inline test did: past the guard, the check
    // is there.
    internal static bool RefusesSingleCreate([NotNullWhen(false)] MigrationEligibilityResult? check, string ticket, [NotNullWhen(true)] out string? refusal)
    {
        refusal = check is null ? "No eligibility check has been run for this mailbox; nothing was created."
            : check.Status != MigrationStatus.Eligible ? $"{check.EmailAddress}'s eligibility check reads {check.Status}, and only an eligible mailbox can be migrated; nothing was created."
            : string.IsNullOrWhiteSpace(ticket) ? "A ticket number is required; nothing was created."
            : null;
        return refusal is not null;
    }

    internal static bool RefusesBulkCreate([NotNullWhen(false)] MigrationBatchResult? check, string ticket, [NotNullWhen(true)] out string? refusal)
    {
        refusal = check is null ? "No eligibility check of a file has been run; nothing was created."
            : check.EligibleUsers == 0 ? "No mailbox in the checked file is eligible; nothing was created."
            : string.IsNullOrWhiteSpace(ticket) ? "A ticket number is required; nothing was created."
            : null;
        return refusal is not null;
    }

    // aud-a55. Why a bulk executor stopped before its first row: the list it acts on is not loaded. It returned with nothing
    // shown and an activity that read "Nothing was done.". Not audited - nothing was attempted (the executors' own "nothing to
    // do" rule). The aud-a23q refusal helpers' shape, so each page line stays one statement.
    internal static bool RefusesWithNoListLoaded([NotNullWhen(false)] object? loaded, string list, [NotNullWhen(true)] out string? refusal)
    {
        refusal = loaded is null ? $"No {list} list is loaded; nothing was done." : null;
        return refusal is not null;
    }

    public async Task<MigrationBatchList> GetMigrationBatchesAsync()
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var batches = new List<MigrationBatchInfo>();
            var unreadable = new List<UnreadableMigrationBatch>();

            try
            {
                // Get all migration batches. aud-a23g: Stop, not Ignore. Under Ignore a failed call came back
                // as no rows - an error the cmdlet wrote, and even a throw, which Ignore runs straight past - and
                // the page said there were no batches. Under Stop both throw, into the catch below.
                // InvokeOptional rather than Invoke: it fails on an exception, where Invoke also fails on
                // HadErrors alone, which an error a nested command suppresses sets with nothing thrown.
                ps.AddCommand("Get-MigrationBatch")
                  .AddParameter("ErrorAction", "Stop");

                var batchResults = InvokeOptional(ps, tracker);

                foreach (var batchObj in batchResults)
                {
                    // aud-a23f. Outside the try, so a row that throws is listed with its identity as far as
                    // it was read.
                    string? batchNameVal = null;
                    try
                    {
                        batchNameVal = batchObj.Properties["Identity"]?.Value?.ToString();
                        // aud-a23f. Every batch action names its target by this, so a row without one is
                        // listed as unreadable. It used to become a batch named "Unknown", which an action
                        // could then be aimed at.
                        if (string.IsNullOrWhiteSpace(batchNameVal))
                        {
                            _logger.LogWarning("Get-MigrationBatch returned a row with no Identity; it is listed as unreadable");
                            unreadable.Add(new UnreadableMigrationBatch(null, "a row with no Identity"));
                            continue;
                        }

                        // aud-a23f. The text fields stay ToString(): every value has a text, so their one
                        // failure is the row throwing, which the catch below lists.
                        var status = batchObj.Properties["Status"]?.Value?.ToString() ?? "Unknown";
                        // aud-a23f. Each count through ReportedCount.Read, not Convert.ToInt32, which threw on
                        // a value it could not convert and lost the whole batch: one this app cannot read is
                        // the marker in its own field. Absent is still 0.
                        var totalCount = ReportedCount.Read(batchObj.Properties["TotalCount"]?.Value) ?? ReportedCount.Of(0);
                        // No fallback to SyncedItemCount: that counts ITEMS and TotalCount
                        // counts MAILBOXES, so the fallback would render an item count over a
                        // mailbox total and read as a nonsense fraction. Absent means 0 here.
                        var syncedCount = ReportedCount.Read(batchObj.Properties["SyncedCount"]?.Value) ?? ReportedCount.Of(0);
                        var finalizedCount = ReportedCount.Read(batchObj.Properties["FinalizedCount"]?.Value ?? batchObj.Properties["FinalizedItemCount"]?.Value) ?? ReportedCount.Of(0);
                        var failedCount = ReportedCount.Read(batchObj.Properties["FailedCount"]?.Value ?? batchObj.Properties["FailedItemCount"]?.Value) ?? ReportedCount.Of(0);
                        // aud-a23d. Every time through the one reader, not `as DateTime?`: any other
                        // type came back as null and the batch showed no start, no completion, no
                        // schedule - as if none were set. Which types the Exchange Online module hands
                        // back is an unverified assumption; one this app cannot read is kept and named.
                        // aud-a23e: an absent creation time is null - none - not MinValue, which the page
                        // showed as the year 0001 and sorted first.
                        var createdDateTime = ReportedTime.Read(batchObj.Properties["CreationDateTime"]?.Value);
                        var startDateTime = ReportedTime.Read(batchObj.Properties["StartDateTime"]?.Value);
                        var completedDateTime = ReportedTime.Read(batchObj.Properties["CompletionDateTime"]?.Value);
                        var targetEndpoint = batchObj.Properties["TargetEndpoint"]?.Value?.ToString();
                        // aud-a23f. Convert.ToBoolean threw on a wrapped value or other text and lost the
                        // batch, for a flag no page reads.
                        var autoStartVal = ReadFlag(batchObj.Properties["AutoStart"]?.Value);
                        // S8. The VALUE, not a boolean derived from its presence. A batch told to
                        // complete now and a batch scheduled for 22:00 both have a non-null
                        // CompleteAfter, and collapsing them here is what made queue item 12
                        // impossible to represent. MigrationBatchInfo derives AutoComplete from
                        // this, so the flag and the time cannot disagree.
                        var completeAfterVal = ReportedTime.Read(batchObj.Properties["CompleteAfter"]?.Value);

                        // Determine direction based on endpoint (hybrid = ToOnPrem, null/cloud = ToCloud)
                        var direction = targetEndpoint?.Contains("hybrid", StringComparison.OrdinalIgnoreCase) == true
                            ? MigrationDirection.ToOnPrem
                            : MigrationDirection.ToCloud;

                        batches.Add(new MigrationBatchInfo
                        {
                            BatchName = batchNameVal,
                            Status = status,
                            CreatedDateTime = createdDateTime,
                            StartDateTime = startDateTime,
                            CompletedDateTime = completedDateTime,
                            TotalCount = totalCount,
                            PendingCount = ReportedCount.Read(batchObj.Properties["PendingCount"]?.Value) ?? ReportedCount.Of(0),
                            WorkflowStage = batchObj.Properties["WorkflowStage"]?.Value?.ToString(),
                            SyncedCount = syncedCount,
                            FinalizedCount = finalizedCount,
                            FailedCount = failedCount,
                            TargetEndpoint = targetEndpoint,
                            AutoStart = autoStartVal,
                            CompleteAfter = completeAfterVal,
                            Direction = direction
                        });
                    }
                    catch (Exception ex) when (!IsCancellation(ex))
                    {
                        // aud-a23f. A row whose reading throws anyway - a property whose getter throws, which
                        // a deserialized row has none of - is listed, not dropped: logging alone left the page
                        // showing the other batches as if they were all there were. aud-a23g: a cancellation is
                        // not such a row. Listed as one, it made the rest of a cancelled load look like a list.
                        _logger.LogWarning(ex, "Could not read the migration batch row {Identity}; it is listed as unreadable",
                            batchNameVal ?? "(no identity)");
                        unreadable.Add(batchNameVal is null
                            ? new UnreadableMigrationBatch(null, $"a row whose Identity could not be read: {ex.Message}")
                            : new UnreadableMigrationBatch(batchNameVal, ex.Message));
                    }
                }

                return new MigrationBatchList(batches, unreadable);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                // aud-a23g. A failed call is a failure, not an empty list, which the page showed as "No
                // migration batches found". Returned rather than thrown: the tracker still holds what
                // InvokeOptional classified, so the pool discards a dead session and retries a precheck
                // failure once, as when the empty list came back.
                _logger.LogError(ex, "Failed to retrieve migration batches");
                return MigrationBatchList.Failed(ResolveFailure(ex).primary);
            }
        }, allowRetry: true);
    }

    // aud-a23f. A flag as reported: false when nothing was, a bool as itself (wrappers unwrapped), text only
    // when it is True or False. Anything else is null, not false, which would be a guess.
    private static bool? ReadFlag(object? reported) => ReportedTime.Unwrap(reported) switch
    {
        null => false,
        bool flag => flag,
        string text when bool.TryParse(text, out var parsed) => parsed,
        _ => null,
    };

    public async Task<MigrationMailboxList> GetMigrationBatchUsersAsync(string batchName)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var users = new List<MigrationUserInfo>();
            var unreadable = new List<UnreadableMigrationMailbox>();

            try
            {
                // Piping a batch binds its Identity as a USER identity. Select the batch
                // explicitly, and fetch every row so the page can filter/page the whole set.
                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("BatchId", batchName)
                  .AddParameter("ResultSize", "Unlimited")
                  .AddParameter("ErrorAction", "Stop");

                var userResults = Invoke(ps, tracker);

                foreach (var userObj in userResults)
                {
                    // aud-a23i. Outside the try, so a row that throws is listed with its identity as far as it was read.
                    string? email = null;
                    try
                    {
                        email = userObj.Properties["Identity"]?.Value?.ToString();
                        // aud-a23i. Every mailbox action names its target by this, so a row without one is listed as
                        // unreadable. It used to become a mailbox named "Unknown", which a tick box and an action could reach.
                        if (string.IsNullOrWhiteSpace(email))
                        {
                            _logger.LogWarning("Get-MigrationUser returned a row with no Identity in batch {BatchName}; it is listed as unreadable", batchName);
                            unreadable.Add(new UnreadableMigrationMailbox(null, "a row with no Identity"));
                            continue;
                        }

                        var userStatus = userObj.Properties["Status"]?.Value?.ToString() ?? "Unknown";
                        var errorSummary = userObj.Properties["ErrorSummary"]?.Value?.ToString();
                        // aud-a23d. Through the one reader: `as DateTime?` showed any other type as "-",
                        // a mailbox that never synced.
                        var lastSyncDateTime = ReportedTime.Read(userObj.Properties["LastSyncTime"]?.Value);
                        // aud-a23g. Both counts through ReportedCount.Read, not Convert.ToInt64, which threw on a value
                        // it could not convert and lost the whole mailbox list: one this app cannot read is the marker
                        // in its own cell, and the list loads. Absent is still 0.
                        var itemsSynced = ReportedCount.Read(userObj.Properties["ItemsSynced"]?.Value) ?? ReportedCount.Of(0);
                        var itemsSkipped = ReportedCount.Read(userObj.Properties["ItemsSkipped"]?.Value) ?? ReportedCount.Of(0);

                        users.Add(new MigrationUserInfo
                        {
                            EmailAddress = email,
                            Status = userStatus,
                            ErrorSummary = errorSummary,
                            LastSyncDateTime = lastSyncDateTime,
                            ItemsSynced = itemsSynced,
                            ItemsSkipped = itemsSkipped
                        });
                    }
                    catch (Exception ex) when (!IsCancellation(ex))
                    {
                        // aud-a23i. A row whose reading throws is listed, not the end of the list: the throw used to reach the
                        // outer catch and lose every mailbox. A cancellation is not such a row (aud-a23g's rule), so it escapes.
                        _logger.LogWarning(ex, "Could not read the migration user row {Identity} in batch {BatchName}; it is listed as unreadable",
                            email ?? "(no identity)", batchName);
                        unreadable.Add(email is null
                            ? new UnreadableMigrationMailbox(null, $"a row whose Identity could not be read: {ex.Message}")
                            : new UnreadableMigrationMailbox(email, ex.Message));
                    }
                }

                return new MigrationMailboxList(users, unreadable);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve migration users for batch {BatchName}", batchName);
                throw;
            }
        }, allowRetry: true);
    }

    public Task<PermissionResult> CompleteMigrationBatchAsync(string batchName)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Complete-MigrationBatch")
              .AddParameter("Identity", batchName)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration batch '{batchName}' completion initiated.", (string?)null), allowRetry: true);
    }

    /// <summary>
    /// Schedules <paramref name="batchName"/> to finalise at <paramref name="completeAfterUtc"/>,
    /// or cancels an existing schedule when it is null.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>S8, and the reason queue item 12 could not be built before it.</b> This codebase used
    /// <c>CompleteAfter</c> in two incompatible ways. Four call sites pass a time already in the
    /// PAST - <c>AddHours(-1)</c>, <c>AddDays(-1)</c>, <c>UtcNow</c> - as the idiom for "finalise
    /// at the first opportunity", and the batch reader turned any non-null value into a boolean.
    /// So "complete now" and "complete at 22:00" were the same thing to this app, and a real
    /// schedule had nowhere to live.
    /// </para>
    /// <para>
    /// This method is the only one that writes a FUTURE CompleteAfter, and it refuses a past one
    /// rather than silently performing an immediate completion. An operator who mistypes a date
    /// and gets an instant cutover instead of an error is the failure that matters here - the
    /// mailboxes move, and nothing undoes that. Use
    /// <see cref="CompleteMigrationBatchAsync(string)"/> to complete now, deliberately.
    /// </para>
    /// </remarks>
    public Task<PermissionResult> ScheduleMigrationBatchCompletionAsync(
        string batchName, DateTime? completeAfterUtc)
    {
        if (completeAfterUtc is { } when && when.ToUniversalTime() <= DateTime.UtcNow)
        {
            return Task.FromResult(PermissionResult.Fail(
                "That time has already passed. To finalise the batch now, use Complete instead - "
                + "scheduling refuses a past time rather than cutting the mailboxes over "
                + "immediately."));
        }

        return RunAsync((ps, tracker) =>
        {
            var command = ps.AddCommand("Set-MigrationBatch")
                            .AddParameter("Identity", batchName)
                            .AddParameter("ErrorAction", "Stop");

            if (completeAfterUtc is { } scheduled)
            {
                command.AddParameter("CompleteAfter", scheduled.ToUniversalTime());
            }
            else
            {
                // Cancelling a schedule, not setting one to "now". $null is what clears the
                // property; passing a past date here would finalise the batch, which is the
                // opposite of what "cancel the schedule" means.
                command.AddParameter("CompleteAfter", null);
            }

            Invoke(ps, tracker);
        }, () => completeAfterUtc is { } at
            ? ($"Migration batch '{batchName}' will complete after {at.ToUniversalTime():yyyy-MM-dd HH:mm} UTC.", (string?)null)
            : ($"Scheduled completion cleared for migration batch '{batchName}'.", (string?)null),
        allowRetry: true);
    }

    public Task<PermissionResult> CompleteMigrationUserAsync(string emailAddress)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
              // "Complete now" for one mailbox, using the same past-time idiom. Not a schedule.
              .AddParameter("CompleteAfter", DateTime.UtcNow)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration completion initiated for {emailAddress}.", (string?)null), allowRetry: true);
    }

    public Task<PermissionResult> ApproveMigrationUserAsync(string emailAddress)
    {
        // Mirrors ApproveMigrationUser.ps1: approve skipped items, set complete, resume move request.
        // NOT retry-eligible: multiple writes incl. non-idempotent Resume-MoveRequest (retry-safety
        // audit). A dead session discards and fails as before; the operator re-runs manually.
        string? batchOutcome = null;
        return RunAsync((ps, tracker) =>
        {
            var pastDate = DateTime.Now.AddDays(-1);

            // aud-a23h. The batch steps below act on a batch named for the mailbox - the name a single-user batch gets by
            // default. A mailbox in a batch of many has none, which -ErrorAction Ignore hid by swallowing every failure of
            // those steps; under Stop each would fail. So whether the batch exists is read first, before any write, and a
            // read that fails, or answers with more than one batch, changes nothing.
            ps.AddCommand("Get-MigrationBatch")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ErrorAction", "Stop");
            Collection<PSObject> ownBatch;
            try
            {
                ownBatch = ReadUnlessNotFound(ps, tracker);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    $"Could not tell whether {emailAddress} is in a migration batch of its own: {ResolveFailure(ex).primary.TrimEnd('.')}. Nothing was changed.", ex);
            }

            if (ownBatch.Count > 1)
            {
                throw new InvalidOperationException(
                    $"Exchange returned {ownBatch.Count} migration batches for {emailAddress}. Nothing was changed.");
            }

            // aud-a23h. Every write under Stop through ApproveStep: one that fails is the row's failure, naming its step and
            // the steps already applied, where under Ignore the batch steps could not fail and "initiated completion" was
            // reported whether or not Complete-MigrationBatch ran.
            var hasOwnBatch = ownBatch.Count == 1;
            // aud-a23n. And the batch's own state, before any write: a batch the approval cannot complete from is refused
            // here, naming its state. It used to pass this read and fail at Complete-MigrationBatch after Set-MigrationUser
            // and Set-MigrationBatch -CompleteAfter had already written.
            // aud-a55. A mailbox in a batch of many has no batch named for it, so there is no batch state to read, and it
            // used to be approved with nothing read at all: a stale page or a second operator could run Set-MigrationUser
            // -CompleteAfter, Set-MoveRequest and Resume-MoveRequest from a state that no longer allows them. Its own status
            // is read instead, before any write, and judged by the Approve action's rule.
            if (hasOwnBatch)
                RefuseUnlessApprovable(emailAddress, ownBatch[0].Properties["Status"]?.Value?.ToString());
            else
                RefuseUnlessAwaitingApproval(ps, tracker, emailAddress);

            var applied = new List<string>();

            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ApproveSkippedItems", true)
              .AddParameter("ErrorAction", "Stop");
            ApproveStep(ps, tracker, "Set-MigrationUser -ApproveSkippedItems", emailAddress, applied);

            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("CompleteAfter", pastDate)
              .AddParameter("ErrorAction", "Stop");
            ApproveStep(ps, tracker, "Set-MigrationUser -CompleteAfter", emailAddress, applied);

            if (hasOwnBatch)
            {
                ps.AddCommand("Set-MigrationBatch")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ApproveSkippedItems", true)
                  .AddParameter("ErrorAction", "Stop");
                ApproveStep(ps, tracker, "Set-MigrationBatch -ApproveSkippedItems", emailAddress, applied);

                ps.AddCommand("Set-MigrationBatch")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("CompleteAfter", pastDate)
                  .AddParameter("ErrorAction", "Stop");
                ApproveStep(ps, tracker, "Set-MigrationBatch -CompleteAfter", emailAddress, applied);
            }

            ps.AddCommand("Set-MoveRequest")
              .AddParameter("Identity", emailAddress)
              .AddParameter("SkippedItemApprovalTime", pastDate)
              .AddParameter("ErrorAction", "Stop");
            ApproveStep(ps, tracker, "Set-MoveRequest -SkippedItemApprovalTime", emailAddress, applied);

            ps.AddCommand("Resume-MoveRequest")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ErrorAction", "Stop");
            ApproveStep(ps, tracker, "Resume-MoveRequest", emailAddress, applied);

            if (hasOwnBatch)
            {
                ps.AddCommand("Complete-MigrationBatch")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("Confirm", false)
                  .AddParameter("ErrorAction", "Stop");
                ApproveStep(ps, tracker, "Complete-MigrationBatch", emailAddress, applied);
            }

            // aud-a23h. Which of the two happened is the Detail: completing a batch and completing only this mailbox are
            // different outcomes, and the message alone would claim the first for both.
            batchOutcome = hasOwnBatch
                ? DescribeCompletionReadBack(ps, tracker, emailAddress)
                : $"{emailAddress} is not in a migration batch of its own, so no batch was completed; only this mailbox was told to complete.";
        }, () => ($"Approved skipped items and initiated completion for {emailAddress}.", batchOutcome));
    }

    // aud-a23h. One write of the approval. Invoke, as every other Migration write: it fails on HadErrors too, and a write
    // PowerShell flagged is not one to report as done. The failure names the step, in the cmdlet's own words, and what
    // the steps before it had already applied, so the operator knows the state the row was left in.
    private static void ApproveStep(PowerShell ps, ConnectionErrorTracker tracker, string step, string emailAddress, List<string> applied)
    {
        try
        {
            Invoke(ps, tracker);
        }
        catch (Exception ex)
        {
            var before = applied.Count == 0
                ? "Nothing was changed before it."
                : $"Already applied: {string.Join(", ", applied)}.";
            throw new InvalidOperationException($"{step} failed for {emailAddress}: {ResolveFailure(ex).primary.TrimEnd('.')}. {before}", ex);
        }

        applied.Add(step);
    }

    // aud-a23n. Throws, before the first write, for a batch that reports no status - which state it is in is not known - or
    // one the approval does not complete; RunAsync makes that the row's failure, and "Nothing was changed." is true.
    // Completed is its own outcome: the migration has finished, which is not a fault, so it is said as such rather than as
    // a refused state.
    //
    // aud-a23r. Every other status is refused unless the batch Complete action completes a batch from it -
    // MigrationBatchActionPlanner.Applies(Complete, ...), the rule's one owner (Synced; owner ruling 2026-09-25: accepting an
    // unanticipated status is the harmful direction), called here, not copied. aud-a23n refused a list of six, so a state the
    // list did not name - Syncing, SyncedWithErrors, Corrupted, anything Exchange adds - ran Set-MigrationUser,
    // Set-MigrationBatch -CompleteAfter, Set-MoveRequest and Resume-MoveRequest before Complete-MigrationBatch refused it:
    // writes left behind a reported failure (Known Failure Class 1). Not split into "approve the mailbox, leave the batch":
    // for the one mailbox of a batch named for it, telling it to complete and resuming its move is that batch's cutover by
    // another route, which is what the rule refuses.
    private static void RefuseUnlessApprovable(string emailAddress, string? reportedStatus)
    {
        var status = reportedStatus?.Trim();
        if (string.IsNullOrEmpty(status))
        {
            throw new InvalidOperationException(
                $"{emailAddress} was not approved: its migration batch {emailAddress} reports no status, so whether it can be completed is not known. Nothing was changed.");
        }

        if (string.Equals(status, "Completed", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"{emailAddress} was not approved: its migration batch {emailAddress} is already {status}, so its migration has finished and there is nothing left to approve. Nothing was changed.");
        }

        if (!MigrationBatchActionPlanner.Applies(MigrationBatchAction.Complete, status))
        {
            throw new InvalidOperationException(
                $"{emailAddress} was not approved: approving it completes its migration batch {emailAddress}, which is {status} - not a status the batch Complete action completes a batch from. Nothing was changed.");
        }
    }

    // aud-a55. Throws, before the first write, unless Exchange reports the migration user at a status the Approve action
    // approves a mailbox from: MigrationUserActionPlanner.Applies(Approve, ...), the rule's one owner (NeedsApproval) and the
    // call the page's Approve button and mailbox bar make - called here, not copied. The status is read as the page's mailbox
    // list reads it (Get-MigrationUser's Status). A read that fails, no migration user (Exchange's not-found, by structure),
    // several, or no status are each refused in their own words; RunAsync makes the refusal the row's failure, so "Nothing
    // was changed." is true. ASSUMPTION, unverified (no live call): Get-MigrationUser -Identity reports the Status the list
    // reads through -BatchId; if not, an approval the page offers is refused naming what Exchange said - visible and fail
    // closed, never a write from a state nobody checked.
    private static void RefuseUnlessAwaitingApproval(PowerShell ps, ConnectionErrorTracker tracker, string emailAddress)
    {
        ps.AddCommand("Get-MigrationUser")
          .AddParameter("Identity", emailAddress)
          .AddParameter("ErrorAction", "Stop");
        Collection<PSObject> users;
        try
        {
            users = ReadUnlessNotFound(ps, tracker);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Could not read the migration status of {emailAddress}: {ResolveFailure(ex).primary.TrimEnd('.')}. Nothing was changed.", ex);
        }

        if (users.Count == 0)
        {
            throw new InvalidOperationException(
                $"{emailAddress} was not approved: Exchange has no migration user {emailAddress}, so there is nothing to approve. Nothing was changed.");
        }

        if (users.Count > 1)
        {
            throw new InvalidOperationException(
                $"Exchange returned {users.Count} migration users for {emailAddress}. Nothing was changed.");
        }

        var status = users[0].Properties["Status"]?.Value?.ToString()?.Trim();
        if (string.IsNullOrEmpty(status))
        {
            throw new InvalidOperationException(
                $"{emailAddress} was not approved: its migration user reports no status, so whether it is awaiting approval is not known. Nothing was changed.");
        }

        if (!MigrationUserActionPlanner.Applies(MigrationUserAction.Approve, status))
        {
            throw new InvalidOperationException(
                $"{emailAddress} was not approved: its migration status is {status} - not a status the Approve action approves a mailbox from. Nothing was changed.");
        }
    }

    // aud-a23n. The read-back after an accepted Complete-MigrationBatch, as the Detail of a SUCCESSFUL approval. An accepted
    // completion is the completion: reporting the approval failed because a read-back could not confirm it invites a second
    // run of writes already made (Known Failure Class 1). It used to require CompleteAfter (or CompleteAfterUTC) to still be
    // set, so a cmdlet that clears or stops returning it once a completion is accepted turned the irreversible write into a
    // reported failure. Nothing here interprets a status: the Detail says what the read-back showed - the status as
    // reported, and whether a completion time is set - or that the state since was not confirmed, and why. Never throws.
    private static string DescribeCompletionReadBack(PowerShell ps, ConnectionErrorTracker tracker, string batchName)
    {
        var accepted = $"Exchange accepted Complete-MigrationBatch for its migration batch {batchName}";
        ps.AddCommand("Get-MigrationBatch")
          .AddParameter("Identity", batchName)
          .AddParameter("ErrorAction", "Stop");
        Collection<PSObject> read;
        try
        {
            read = InvokeOptional(ps, tracker);
        }
        catch (Exception ex)
        {
            return $"{accepted}; reading the batch back failed ({ResolveFailure(ex).primary.TrimEnd('.')}), so its state since was not confirmed.";
        }

        if (read.Count != 1)
            return $"{accepted}; reading the batch back returned {read.Count} batches, so its state since was not confirmed.";

        var status = read[0].Properties["Status"]?.Value?.ToString()?.Trim();
        var reports = string.IsNullOrEmpty(status) ? "no status" : $"status {status}";
        var scheduled = (read[0].Properties["CompleteAfter"]?.Value ?? read[0].Properties["CompleteAfterUTC"]?.Value) is not null;
        return scheduled
            ? $"{accepted}; read back, the batch reports {reports} with a completion time set."
            : $"{accepted}; read back, the batch reports {reports} and no completion time, so the read-back did not confirm the schedule.";
    }

    public Task<PermissionResult> StopMigrationUserAsync(string emailAddress)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Stop-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration stopped for {emailAddress}.", (string?)null), allowRetry: true);
    }

    public Task<PermissionResult> ResumeMigrationUserAsync(string emailAddress)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Start-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration resumed for {emailAddress}.", (string?)null), allowRetry: true);
    }

    public Task<PermissionResult> RemoveMigrationUserAsync(string emailAddress)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Remove-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration user {emailAddress} removed.", (string?)null), allowRetry: true);
    }

    // These three cmdlets return when Exchange ACCEPTS the request, not when the work is done: the
    // batch then sits at Removing / Stopping / Starting for anything from seconds to minutes. The
    // messages say "queued" for that reason - reporting "removed" over a row the operator can still
    // see on screen reads as a broken app, and it was.
    public Task<PermissionResult> RemoveMigrationBatchAsync(string batchName)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Remove-MigrationBatch")
              .AddParameter("Identity", batchName)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration batch '{batchName}' queued for removal.", (string?)null), allowRetry: true);
    }

    public Task<PermissionResult> StopMigrationBatchAsync(string batchName)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Stop-MigrationBatch")
              .AddParameter("Identity", batchName)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration batch '{batchName}' queued to stop.", (string?)null), allowRetry: true);
    }

    public Task<PermissionResult> StartMigrationBatchAsync(string batchName)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Start-MigrationBatch")
              .AddParameter("Identity", batchName)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration batch '{batchName}' queued to start.", (string?)null), allowRetry: true);
    }

    // aud-a23k. A size that could not be read used to be 0 - the read failing, no row, a size that did not parse, and any
    // failure of the archive read at all - which the page showed as an empty mailbox or no size. Each is a marker now (the
    // ReportedCount idea, for a size). Exchange's not-found for the archive is no archive, read from the error's structure
    // (aud-a23h's IsNotFound). ASSUMPTION, unverified (no live call): Get-MailboxStatistics -Archive reports a mailbox with
    // no archive in one of those shapes; if not, such a mailbox shows "Archive: could not be read (...)" - visible, and no
    // decision reads it. Neither read's catch takes a cancellation (aud-a23g's rule); one escapes to the caller.
    private async Task<MailboxSizes> GetCloudMailboxSizeAsync(string emailAddress)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            double mailboxGB = 0;
            string? mailboxUnreadable;
            try
            {
                ps.AddCommand("Get-MailboxStatistics")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Stop");
                (mailboxGB, mailboxUnreadable) = ReadSize(Invoke(ps, tracker), "Exchange returned no mailbox statistics");
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                mailboxUnreadable = $"Get-MailboxStatistics failed: {ResolveFailure(ex).primary.TrimEnd('.')}";
            }

            double archiveGB = 0;
            string? archiveUnreadable = null;
            try
            {
                ps.AddCommand("Get-MailboxStatistics")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("Archive")
                  .AddParameter("ErrorAction", "Stop");
                // aud-a55. Only Exchange's own not-found, by structure, is no archive (the IsNotFound catch below). An answer
                // with no row and no error is ReadSize's marker: through ReadUnlessNotFound both came back as no rows, and
                // both were no archive.
                (archiveGB, archiveUnreadable) = ReadSize(InvokeOptional(ps, tracker), "Exchange returned no archive statistics");
            }
            catch (Exception ex) when (IsNotFound(ex))
            {
                _logger.LogInformation("No archive mailbox found for {Email}", emailAddress);
            }
            catch (Exception ex) when (!IsCancellation(ex))
            {
                archiveUnreadable = $"Get-MailboxStatistics -Archive failed: {ResolveFailure(ex).primary.TrimEnd('.')}";
            }

            return new MailboxSizes(mailboxGB, mailboxUnreadable, archiveGB, archiveUnreadable);
        }, allowRetry: true);
    }

    // aud-a23k. The report, or why there is none - never failure text where the report goes. A failed fetch came back as
    // "Error retrieving report: ...", which the page stored and showed as the report, its activity succeeded, and the bulk
    // export stored as the mailbox's report on a Succeeded row; no statistics row was a null report the page then stored.
    public async Task<MigrationUserReport> GetMigrationUserReportAsync(string emailAddress)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            try
            {
                ps.AddCommand("Get-MigrationUserStatistics")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("IncludeReport", true)
                  .AddParameter("ErrorAction", "Stop");

                var results = Invoke(ps, tracker);
                var stats = results.FirstOrDefault();
                if (stats is null)
                    return MigrationUserReport.Failed($"Exchange returned no migration statistics for {emailAddress}");

                var sb = new StringBuilder();

                var error = stats.Properties["Error"]?.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(error))
                    sb.AppendLine($"Error: {error}");

                var errorSummary = stats.Properties["ErrorSummary"]?.Value?.ToString();
                if (!string.IsNullOrWhiteSpace(errorSummary))
                    sb.AppendLine($"Error Summary: {errorSummary}");

                var status = stats.Properties["Status"]?.Value?.ToString();
                sb.AppendLine($"Status: {status}");

                var skippedItemCount = stats.Properties["SkippedItemCount"]?.Value;
                sb.AppendLine($"Skipped Items: {skippedItemCount}");

                var syncedItemCount = stats.Properties["SyncedItemCount"]?.Value;
                sb.AppendLine($"Synced Items: {syncedItemCount}");

                var report = stats.Properties["Report"]?.Value;
                if (report is not null)
                {
                    sb.AppendLine();
                    sb.AppendLine("--- Move Report ---");
                    sb.AppendLine(report.ToString());
                }

                return MigrationUserReport.Of(sb.ToString());
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get migration report for {Email}", emailAddress);
                // aud-a23l. In the cmdlet's own words (ResolveFailure): an error written under Stop read "The running
                // command stopped because the preference variable ..." in front of them.
                return MigrationUserReport.Failed(ResolveFailure(ex).primary);
            }
        }, allowRetry: true);
    }

    public async Task<MigrationUserSearchResult> FindMigrationUserBatchAsync(string searchTerm)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            try
            {
                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("ResultSize", "Unlimited")
                  .AddParameter("ErrorAction", "Stop");

                var results = Invoke(ps, tracker);

                var users = new List<(string Email, string? BatchId)>();
                foreach (var user in results)
                {
                    var email = user.Properties["Identity"]?.Value?.ToString();
                    var batchId = user.Properties["BatchId"]?.Value?.ToString();
                    if (email != null)
                        users.Add((email, batchId));
                }

                return MatchMigrationUser(searchTerm, users);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to search migration users for {Term}", searchTerm);
                // aud-a23l. In the cmdlet's own words (ResolveFailure): it read "Search failed: The running command stopped
                // because the preference variable ..." for an error written under Stop.
                return MigrationUserSearchResult.Failed(ResolveFailure(ex).primary);
            }
        }, allowRetry: true);
    }

    public static MigrationUserSearchResult MatchMigrationUser(
        string searchTerm, List<(string Email, string? BatchId)> users)
    {
        var term = searchTerm.Trim();

        var exact = users.FirstOrDefault(u =>
            u.Email.Equals(term, StringComparison.OrdinalIgnoreCase));

        if (exact.Email != null)
        {
            if (string.IsNullOrEmpty(exact.BatchId))
                return MigrationUserSearchResult.NotFound();
            return MigrationUserSearchResult.Found(exact.BatchId, exact.Email);
        }

        var partials = users
            .Where(u => u.Email.Contains(term, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (partials.Count == 0)
            return MigrationUserSearchResult.NotFound();

        if (partials.Count == 1)
        {
            var match = partials[0];
            if (string.IsNullOrEmpty(match.BatchId))
                return MigrationUserSearchResult.NotFound();
            return MigrationUserSearchResult.Found(match.BatchId, match.Email);
        }

        return MigrationUserSearchResult.Ambiguous(partials.Count);
    }
}
