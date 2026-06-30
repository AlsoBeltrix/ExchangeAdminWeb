using System.Globalization;
using System.Management.Automation;
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
    private long _cloudQuotaGB => long.TryParse(MigrationConfig("CloudQuotaGB", "Migration:CloudQuotaGB", "100"), out var v) ? v : 100;
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

    public MigrationService(IConfiguration config, ExoConnectionPool exoPool, DelineaService delineaService, ILogger<MigrationService> logger, ModuleConfigService moduleConfig, ModuleCredentialService moduleCredentials, OperationTraceService operationTrace, ProtectedPrincipalService protectedPrincipals)
        : base(exoPool, delineaService, logger, config["OnPremExchange:ServerUri"] ?? "", moduleCredentials, "Migration", operationTrace)
    {
        _config = config;
        _moduleConfig = moduleConfig;
        _protectedPrincipals = protectedPrincipals;

        var adminEmail = config["Email:AdminNotificationEmail"] ?? "";
        _adminNotificationEmails = adminEmail.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Select(e => e.Trim())
            .Where(e => !string.IsNullOrWhiteSpace(e))
            .ToArray();
    }

    public async Task<MigrationEligibilityResult> CheckMigrationEligibilityAsync(string emailAddress, MigrationDirection direction)
    {
        var result = await RunPooledQueryAsync((ps, tracker) =>
        {
            var r = new MigrationEligibilityResult
            {
                EmailAddress = emailAddress,
                Status = MigrationStatus.Eligible
            };

            try
            {
                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Ignore");
                var migUser = InvokeOptional(ps, tracker);
                if (migUser.Any())
                {
                    r.Status = MigrationStatus.Ineligible;
                    r.IneligibilityReasons.Add("Migration already in progress");
                }

                ps.AddCommand("Get-Mailbox")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("ErrorAction", "Ignore");
                var cloudMbx = InvokeOptional(ps, tracker);
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
                r.Status = MigrationStatus.Ineligible;
                r.IneligibilityReasons.Add($"Error checking eligibility: {ex.Message}");
                _logger.LogError(ex, "Error checking migration eligibility for {Email}", emailAddress);
            }

            return r;
        }, allowRetry: true);

        if (direction == MigrationDirection.ToCloud && result.NeedsAdGroupCheck)
        {
            try
            {
                CheckAdGroupMembership(result, _excludedADGroups);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error checking AD group membership for {Email}", emailAddress);
                result.IneligibilityReasons.Add($"Warning: Could not verify AD group membership ({ex.Message})");
            }
        }

        if (result.Status == MigrationStatus.Eligible)
        {
            try
            {
                if (direction == MigrationDirection.ToCloud)
                {
                    var sizeResult = await GetOnPremMailboxSizeAsync(emailAddress);
                    if (sizeResult is not null)
                    {
                        var (mailboxGB, archiveGB) = sizeResult.Value;
                        var totalGB = mailboxGB + archiveGB;
                        result.MailboxSizeGB = mailboxGB;
                        result.ArchiveSizeGB = archiveGB;
                        result.CloudQuotaGB = _cloudQuotaGB;

                        if (totalGB > _cloudQuotaGB)
                        {
                            result.Status = MigrationStatus.Ineligible;
                            result.IneligibilityReasons.Add($"Mailbox + archive size ({totalGB:F2} GB) exceeds cloud quota ({_cloudQuotaGB} GB)");
                        }
                    }
                    else
                    {
                        result.Status = MigrationStatus.Ineligible;
                        result.IneligibilityReasons.Add("Could not verify on-prem mailbox size (on-prem connection unavailable)");
                    }
                }
                else
                {
                    var cloudSize = await GetCloudMailboxSizeAsync(emailAddress);
                    if (cloudSize is not null)
                    {
                        result.MailboxSizeGB = cloudSize.Value.mailboxGB;
                        result.ArchiveSizeGB = cloudSize.Value.archiveGB;
                    }
                }
            }
            catch (Exception sizeEx)
            {
                _logger.LogWarning(sizeEx, "Could not retrieve mailbox size for {Email}", emailAddress);
                if (direction == MigrationDirection.ToCloud)
                {
                    result.Status = MigrationStatus.Ineligible;
                    result.IneligibilityReasons.Add($"Could not verify on-prem mailbox size ({sizeEx.Message})");
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

        foreach (var row in records)
        {
            var eligibility = await CheckMigrationEligibilityAsync(row.EmailAddress, direction);
            results.Add(eligibility);
        }

        var eligible = results.Count(r => r.Status == MigrationStatus.Eligible);
        var ineligible = results.Count - eligible;

        return new MigrationBatchResult
        {
            BatchName = $"{DateTime.Now:yyyy-MM-dd}_{(direction == MigrationDirection.ToCloud ? "Move" : "MoveBack")}",
            Success = true,
            Direction = direction,
            EligibilityResults = results,
            TotalUsers = results.Count,
            EligibleUsers = eligible,
            IneligibleUsers = ineligible,
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
    private async Task<PermissionResult?> CheckProtectedAsync(string identity)
    {
        if (string.IsNullOrWhiteSpace(identity))
            return null;

        try
        {
            var (resolved, status) = await _protectedPrincipals.ResolveWithStatusAsync(identity);
            if (status is ProtectedPrincipalService.ResolutionStatus.Unavailable
                       or ProtectedPrincipalService.ResolutionStatus.Ambiguous)
            {
                return PermissionResult.Fail(status == ProtectedPrincipalService.ResolutionStatus.Ambiguous
                    ? "Identity is ambiguous — matches multiple AD users."
                    : "Protection check unavailable. Cannot verify if this mailbox is protected.");
            }

            if (resolved != null)
            {
                var check = await _protectedPrincipals.CheckAsync(resolved);
                if (check.CheckFailed)
                    return PermissionResult.Fail($"Protection check failed: {check.Reason}");
                if (check.IsProtected)
                    return PermissionResult.Fail("This mailbox is a protected principal. Operation not permitted.");
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Protected principal check failed for migration target {Identity} — excluding as precaution", identity);
            return PermissionResult.Fail($"Protection check error: {ex.Message}");
        }
    }

    /// <summary>
    /// Splits migration targets into those clear to migrate and those excluded by the
    /// protected-principal gate. Each excluded entry is an operator-facing "identity — reason"
    /// string. Runs before any batch side effect, so a protected target never reaches the write
    /// path. The <paramref name="checker"/> seam exists for unit testing; production passes null
    /// and the real gate is used.
    /// </summary>
    internal async Task<(List<string> allowed, List<string> excluded)> PartitionByProtectionAsync(
        IEnumerable<string> targets,
        Func<string, Task<PermissionResult?>>? checker = null)
    {
        checker ??= CheckProtectedAsync;

        var allowed = new List<string>();
        var excluded = new List<string>();

        foreach (var target in targets)
        {
            var block = await checker(target);
            if (block == null)
                allowed.Add(target);
            else
                excluded.Add($"{target} — {block.Message}");
        }

        return (allowed, excluded);
    }

    /// <summary>
    /// Flags an eligibility result if its target is a protected principal (or protection
    /// cannot be verified — fail-closed). Sets <see cref="MigrationEligibilityResult.IsProtected"/>
    /// and <see cref="MigrationEligibilityResult.ProtectionNote"/> but never changes
    /// <see cref="MigrationEligibilityResult.Status"/>: protection is an orthogonal axis to the
    /// Exchange/AD eligibility verdict. The <paramref name="checker"/> seam exists for unit
    /// testing; production passes null and the real gate is used.
    /// </summary>
    internal async Task ApplyProtectionFlagAsync(
        MigrationEligibilityResult result,
        Func<string, Task<PermissionResult?>>? checker = null)
    {
        checker ??= CheckProtectedAsync;

        var block = await checker(result.EmailAddress);
        if (block != null)
        {
            result.IsProtected = true;
            result.ProtectionNote = block.Message;
        }
    }

    public async Task<PermissionResult> CreateMigrationBatchAsync(MigrationDirection direction, List<string> eligibleEmails, string batchName, bool autoStart, bool autoComplete)
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
        // exclusion must never be silent — filter the protected targets out, migrate the rest,
        // and report the exclusions back clearly.
        var (allowedEmails, excludedTargets) = await PartitionByProtectionAsync(eligibleEmails);

        if (allowedEmails.Count == 0)
        {
            // Every target was excluded (includes the single-protected-target case). Create
            // nothing and tell the operator plainly why.
            return new PermissionResult
            {
                Success = false,
                Message = excludedTargets.Count == 1
                    ? "The migration target is a protected principal and was excluded. Nothing was created. Escalate to an administrator outside this tool if migration is required."
                    : $"All {excludedTargets.Count} migration target(s) are protected principals and were excluded. Nothing was created. Escalate to an administrator outside this tool if migration is required.",
                Detail = "Excluded:\n" + string.Join("\n", excludedTargets),
                ExcludedTargets = excludedTargets
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
                details += $@"

EXCLUDED — protected principals, NOT migrated ({excludedTargets.Count}):
{string.Join("\n", excludedTargets)}
Escalate these to an administrator outside this tool if migration is required.";
            }

            details += @"

Monitor progress in the Exchange Admin Center at:
https://admin.exchange.microsoft.com/#/migration";

            return (message, details);
        });

        // Carry the protected-principal exclusions back to the caller so the page and the admin
        // notification can surface them. RunAsync builds the result; re-wrap to attach the list
        // (and prepend a clear note to the success message) without losing Success/Detail.
        if (excludedTargets.Count > 0)
        {
            return new PermissionResult
            {
                Success = result.Success,
                Message = result.Success
                    ? $"{result.Message} NOTE: {excludedTargets.Count} protected principal(s) were excluded and NOT migrated."
                    : result.Message,
                Detail = result.Detail,
                ExcludedTargets = excludedTargets
            };
        }

        return result;
    }

    public async Task<List<MigrationBatchInfo>> GetMigrationBatchesAsync()
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var batches = new List<MigrationBatchInfo>();

            try
            {
                // Get all migration batches
                ps.AddCommand("Get-MigrationBatch")
                  .AddParameter("ErrorAction", "Ignore");

                var batchResults = InvokeOptional(ps, tracker);

                foreach (var batchObj in batchResults)
                {
                    try
                    {
                        var batchNameVal = batchObj.Properties["Identity"]?.Value?.ToString() ?? "Unknown";
                        var status = batchObj.Properties["Status"]?.Value?.ToString() ?? "Unknown";
                        var totalCount = Convert.ToInt32(batchObj.Properties["TotalCount"]?.Value ?? 0);
                        var syncedCount = Convert.ToInt32(batchObj.Properties["SyncedCount"]?.Value ?? batchObj.Properties["SyncedItemCount"]?.Value ?? 0);
                        var finalizedCount = Convert.ToInt32(batchObj.Properties["FinalizedCount"]?.Value ?? batchObj.Properties["FinalizedItemCount"]?.Value ?? 0);
                        var failedCount = Convert.ToInt32(batchObj.Properties["FailedCount"]?.Value ?? batchObj.Properties["FailedItemCount"]?.Value ?? 0);
                        var createdDateTime = batchObj.Properties["CreationDateTime"]?.Value as DateTime? ?? DateTime.MinValue;
                        var startDateTime = batchObj.Properties["StartDateTime"]?.Value as DateTime?;
                        var completedDateTime = batchObj.Properties["CompletionDateTime"]?.Value as DateTime?;
                        var targetEndpoint = batchObj.Properties["TargetEndpoint"]?.Value?.ToString();
                        var autoStartVal = Convert.ToBoolean(batchObj.Properties["AutoStart"]?.Value ?? false);
                        var autoCompleteVal = Convert.ToBoolean(batchObj.Properties["CompleteAfter"]?.Value != null);

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
                            SyncedCount = syncedCount,
                            FinalizedCount = finalizedCount,
                            FailedCount = failedCount,
                            TargetEndpoint = targetEndpoint,
                            AutoStart = autoStartVal,
                            AutoComplete = autoCompleteVal,
                            Direction = direction
                        });
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Failed to process migration batch");
                    }
                }

                return batches;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve migration batches");
                return new List<MigrationBatchInfo>();
            }
        }, allowRetry: true);
    }

    public async Task<List<MigrationUserInfo>> GetMigrationBatchUsersAsync(string batchName)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            var users = new List<MigrationUserInfo>();

            try
            {
                ps.AddCommand("Get-MigrationUser")
                  .AddParameter("BatchId", batchName)
                  .AddParameter("ErrorAction", "Ignore");

                var userResults = InvokeOptional(ps, tracker);

                foreach (var userObj in userResults)
                {
                    var email = userObj.Properties["Identity"]?.Value?.ToString() ?? "Unknown";
                    var userStatus = userObj.Properties["Status"]?.Value?.ToString() ?? "Unknown";
                    var errorSummary = userObj.Properties["ErrorSummary"]?.Value?.ToString();
                    var lastSyncDateTime = userObj.Properties["LastSyncTime"]?.Value as DateTime?;
                    var itemsSynced = Convert.ToInt64(userObj.Properties["ItemsSynced"]?.Value ?? 0);
                    var itemsSkipped = Convert.ToInt64(userObj.Properties["ItemsSkipped"]?.Value ?? 0);

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

                return users;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to retrieve migration users for batch {BatchName}", batchName);
                return new List<MigrationUserInfo>();
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

    public Task<PermissionResult> CompleteMigrationUserAsync(string emailAddress)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
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
        return RunAsync((ps, tracker) =>
        {
            var pastDate = DateTime.Now.AddDays(-1);

            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ApproveSkippedItems", true)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);

            ps.AddCommand("Set-MigrationUser")
              .AddParameter("Identity", emailAddress)
              .AddParameter("CompleteAfter", pastDate)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);

            ps.AddCommand("Set-MigrationBatch")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ApproveSkippedItems", true)
              .AddParameter("ErrorAction", "Ignore");
            InvokeOptional(ps, tracker);

            ps.AddCommand("Set-MigrationBatch")
              .AddParameter("Identity", emailAddress)
              .AddParameter("CompleteAfter", pastDate)
              .AddParameter("ErrorAction", "Ignore");
            InvokeOptional(ps, tracker);

            ps.AddCommand("Set-MoveRequest")
              .AddParameter("Identity", emailAddress)
              .AddParameter("SkippedItemApprovalTime", pastDate)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);

            ps.AddCommand("Resume-MoveRequest")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);

            ps.AddCommand("Complete-MigrationBatch")
              .AddParameter("Identity", emailAddress)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Ignore");
            InvokeOptional(ps, tracker);
        }, () => ($"Approved skipped items and initiated completion for {emailAddress}.", (string?)null));
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

    public Task<PermissionResult> RemoveMigrationBatchAsync(string batchName)
    {
        return RunAsync((ps, tracker) =>
        {
            ps.AddCommand("Remove-MigrationBatch")
              .AddParameter("Identity", batchName)
              .AddParameter("Confirm", false)
              .AddParameter("ErrorAction", "Stop");
            Invoke(ps, tracker);
        }, () => ($"Migration batch '{batchName}' removed.", (string?)null), allowRetry: true);
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
        }, () => ($"Migration batch '{batchName}' stopped.", (string?)null), allowRetry: true);
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
        }, () => ($"Migration batch '{batchName}' started.", (string?)null), allowRetry: true);
    }

    private async Task<(double mailboxGB, double archiveGB)?> GetCloudMailboxSizeAsync(string emailAddress)
    {
        return await RunPooledQueryAsync((ps, tracker) =>
        {
            ps.AddCommand("Get-MailboxStatistics")
              .AddParameter("Identity", emailAddress)
              .AddParameter("ErrorAction", "Stop");
            var stats = Invoke(ps, tracker);
            var mbxStat = stats.FirstOrDefault();
            var totalItemSize = mbxStat?.Properties["TotalItemSize"]?.Value?.ToString();
            var mailboxGB = ParseSizeToGB(totalItemSize) ?? 0;

            double archiveGB = 0;
            try
            {
                ps.AddCommand("Get-MailboxStatistics")
                  .AddParameter("Identity", emailAddress)
                  .AddParameter("Archive")
                  .AddParameter("ErrorAction", "Stop");
                var archiveStats = Invoke(ps, tracker);
                var archiveStat = archiveStats.FirstOrDefault();
                var archiveSize = archiveStat?.Properties["TotalItemSize"]?.Value?.ToString();
                archiveGB = ParseSizeToGB(archiveSize) ?? 0;
            }
            catch
            {
                // No archive mailbox
            }

            return ((double mailboxGB, double archiveGB)?)(mailboxGB, archiveGB);
        }, allowRetry: true);
    }

    public async Task<string?> GetMigrationUserReportAsync(string emailAddress)
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
                if (stats is null) return (string?)null;

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

                return (string?)sb.ToString();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to get migration report for {Email}", emailAddress);
                return (string?)$"Error retrieving report: {ex.Message}";
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
                return MigrationUserSearchResult.Failed(ex.Message);
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
